import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:100])
    s = s.replace(old, new, cnt)

# ---- 修正在上一阶段写坏的归属判据（可读且正确） ----
rep('''            if (requireOwnershipEvidence)
            {
                // 归属证据：内容必须等于本事务写入的字节；否则视为他方文件，保留并计为未完成（不删）
                if (!TryGetWriteSetHash(m, c.Path, out var owned) || requireOwnershipEvidence
                    && !TryHashConfigFileOnRoot(targetRoot, c.Path, out var currentHash)
                    || !string.Equals(owned, currentHash, StringComparison.Ordinal))
                {
                    failed++;
                    continue;
                }
            }''',
'''            if (requireOwnershipEvidence)
            {
                // 归属证据：内容必须等于本事务写入的字节；否则视为他方文件，保留并计为未完成（不删）
                if (!TryGetWriteSetHash(m, c.Path, out var ownedHash)
                    || !TryHashConfigFileOnRoot(targetRoot, c.Path, out var currentHash)
                    || !string.Equals(ownedHash, currentHash, StringComparison.Ordinal))
                {
                    failed++;
                    continue;
                }
            }''')

# ---- IMPORTANT-6：确认引用更新后冻结变更登记 ----
rep('''            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);''',
'''            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            // **确认引用更新后冻结登记（会诊 IMPORTANT-6）**：真实写集一旦确认，变更登记即为已核验证据的一部分；
            // 此后改登记（新增/改写归属）会使「声明写集 ↔ 变更登记」一致性失效，一律拒绝，须重新开事务。
            if (m.ReferenceWriteSet.Count > 0)
                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);''')

# ---- MUST-4：提交门槛绑定「本实例是否接真实副作用」，并加写集↔登记/激活关系与文件集合核对 ----
rep('''            if (m.RealEffectsRequired)
            {
                // **真实事务的提交前置**：引用写入与激活都必须由真实副作用 + 读回确认产生，且提交前重核读回证据''',
'''            if (m.RealEffectsRequired || _effects is not null)
            {
                // **真实事务的提交前置（会诊 MUST-4）**：门槛绑定「本实例是否接入真实副作用」与持久化标记的**并集**，
                // 故把 `realEffectsRequired` 改成 false 不能降级绕过；引用写入与激活都必须由真实副作用 + 读回确认产生''')

rep('''                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
            }''',
'''                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
                if (EvidenceRelationProblem(m) is { } relationProblem) return MigrationResult.Fail(relationProblem, m.Stage);
                if (UnexpectedFileReason(m, m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted)
                        .Select(c => new MigrationReferenceWriteTarget(c.Path, c.Kind)).ToList()) is { } unexpected)
                    return MigrationResult.Fail(unexpected, m.Stage);
            }''')

# ---- MUST-4：授权门槛同样绑定实例 ----
rep('''            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>''',
'''            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if ((m.RealEffectsRequired || _effects is not null)
                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))
                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>''')

# ---- MUST-4：证据关系不变量 ----
rep('''    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>''',
'''    /// <summary>
    /// **证据关系不变量（会诊 MUST-4）**：真实证据之间必须自洽——写集键集合**恰等于**变更登记中非删除项、
    /// 身份键唯一、激活记录的盘上哈希必须等于写集中该文件的哈希。任一不符 ⇒ 拒绝（返回原因码）。
    /// </summary>
    private static string? EvidenceRelationProblem(MigrationManifest m)
    {
        var writeSetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in m.ReferenceWriteSet.Keys)
            if (!writeSetKeys.Add(PathKey(key))) return "evidence_relation:duplicate_writeset_key:" + key;
        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);
        if (!writeSetKeys.SetEquals(registered)) return "evidence_relation:writeset_registry_mismatch";
        if (m.ActivationRecord is { } activation)
        {
            if (!TryGetWriteSetHash(m, activation.Path, out var activationHash))
                return "evidence_relation:activation_not_in_writeset:" + activation.Path;
            if (!string.Equals(activationHash, activation.AfterHash, StringComparison.Ordinal))
                return "evidence_relation:activation_hash_mismatch:" + activation.Path;
        }
        return null;
    }

    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>''')

# ---- MUST-4：结构校验补关系不变量（含写集↔登记） ----
rep('''        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }''',
'''        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }
        // **证据关系（会诊 MUST-4）**：只要出现真实证据，写集必须与变更登记精确对应、激活哈希必须等于写集哈希。
        if (m.ReferenceWriteSet.Count > 0 && EvidenceRelationProblem(m) is not null) return false;''')

# ---- IMPORTANT-8：撤销激活的端口调用与读回同样收敛异常 ----
rep('''        var kind = FindChange(m, record.Path)?.Kind;
        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var current, out var detail))
        {''',
'''        var kind = FindChange(m, record.Path)?.Kind;
        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
        bool readOk;
        string current;
        string detail;
        try
        {
            readOk = _effects.TryReadActivationStatus(_configRoot, record.Path, out current, out detail);
        }
        catch (Exception ex)
        {
            return MarkBlocked("rollback_activation_readback_exception:" + ex.GetType().Name);
        }
        if (!readOk)
        {''')

rep('''        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
        {
            var undo = _effects.Activate(_configRoot,
                new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus));
            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
        }''',
'''        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
        {
            MigrationEffectResult undo;
            try
            {
                undo = _effects.Activate(_configRoot,
                    new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus));
            }
            catch (Exception ex)
            {
                return MarkBlocked("rollback_activation_undo_exception:" + ex.GetType().Name);
            }
            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
        }''')
p.write_text(s, encoding="utf-8")
print("transaction stage-3 patched", hashlib.sha256(s.encode()).hexdigest()[:16])
