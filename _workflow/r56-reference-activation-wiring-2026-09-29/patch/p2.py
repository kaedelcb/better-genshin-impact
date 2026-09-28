import hashlib, pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
src = p.read_text(encoding="utf-8")
def rep(old, new, count=1):
    global src
    assert src.count(old) == count, (src.count(old), old[:90])
    src = src.replace(old, new, count)

# ---- gate the stage-only APIs when real effects are wired ----
rep('''    public MigrationResult MarkReferenceUpdateCompleted() => Advance(MigrationStage.ReferenceUpdating);
    public MigrationResult MarkActivated() => Advance(MigrationStage.Activated);''',
'''    /// <summary>
    /// **阶段推进旧语义（仅限未接真实副作用的只读夹具）**：已注入真实副作用端口后**必须**用
    /// <see cref="ApplyReferenceUpdate"/>；否则本方法会在零写入的情况下把阶段推进到 `ReferenceUpdating`，
    /// 使 `RehearseRollback`/`Commit` 全部通过而配置根**从未发生真实引用更新**（假成功）。此门禁只在
    /// 生产/真实事务上生效，不改变旧夹具（`effectService: null`）的行为。
    /// </summary>
    public MigrationResult MarkReferenceUpdateCompleted()
        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
    /// <summary>阶段推进旧语义（同 <see cref="MarkReferenceUpdateCompleted"/>：注入真实副作用端口后一律拒绝）。</summary>
    public MigrationResult MarkActivated()
        => _effects is null ? Advance(MigrationStage.Activated)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);

    /// <summary>
    /// **真实引用更新（R5.6 A 项）**：声明写集 → 真实副作用 → **逐项读回确认** → **才**持久化阶段与写集证据。
    /// 拒绝/未知/取消/读回不符一律不推进阶段：未知与读回不符置 `Blocked`（fail-closed、不盲目重试）；
    /// 副作用前取消保持当前阶段（可重试/可回滚）；已到本阶段时幂等重读盘复核，**不二次触发副作用**。
    /// </summary>
    public MigrationResult ApplyReferenceUpdate(MigrationReferenceUpdatePlan plan)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入

            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
            if (!IsLegalAdvance(m.Stage, MigrationStage.ReferenceUpdating))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->ReferenceUpdating", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 真实写入必须在**存续**窗口内
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            var result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            // **读回确认先于阶段推进**：语义读回（引用已改写）+ 字节读回（写入后哈希）
            var writeSet = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var target in plan!.Targets)
            {
                if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
                writeSet[target.Path] = hash;
            }
            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>
    /// **真实激活（R5.6 A 项：D13 `candidate → active`）**：须已完成真实引用更新；目标必须是**已确认写集内**的文件；
    /// 副作用成功且状态读回一致后才推进到 `Activated`。已到 `Activated` 时幂等重读复核（不二次写入）。
    /// </summary>
    public MigrationResult ActivateCandidate(MigrationActivationRequest request)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**

            if (m.Stage != MigrationStage.ReferenceUpdating)
                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);
            if (ValidateActivationRequest(m, request) is { } requestProblem) return MarkBlocked(requestProblem);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            var result = _effects.Activate(_configRoot, request!);
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("activation_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("activation_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            if (!_effects.TryReadActivationStatus(_configRoot, request!.Path, out var status, out var detail)
                || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
            if (!TryHashConfigFile(request.Path, out var hash, out var hashProblem))
                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);

            m.ActivationRecord = new MigrationActivationRecord
            {
                Path = request.Path,
                BeforeStatus = request.ExpectedBeforeStatus,
                AfterStatus = request.TargetStatus,
                AfterHash = hash,
            };
            m.Stage = MigrationStage.Activated;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>幂等复核：写集内每个文件仍在盘上且哈希与已确认写集一致（不触发副作用）。</summary>
    private MigrationResult RecheckReferenceWriteSet(MigrationManifest m)
    {
        if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_write_set_empty", m.Stage);
        foreach (var entry in m.ReferenceWriteSet)
        {
            if (!TryHashConfigFile(entry.Key, out var hash, out var problem))
                return MigrationResult.Fail("reference_recheck_" + problem + ":" + entry.Key, m.Stage);
            if (!string.Equals(hash, entry.Value, StringComparison.Ordinal))
                return MigrationResult.Fail("reference_recheck_hash_mismatch:" + entry.Key, m.Stage);
        }
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>幂等复核：激活记录仍在盘上（哈希一致）且状态仍为目标状态（不触发副作用）。</summary>
    private MigrationResult RecheckActivationRecord(MigrationManifest m)
    {
        var record = m.ActivationRecord;
        if (record is null) return MigrationResult.Fail("activation_record_missing", m.Stage);
        if (!TryHashConfigFile(record.Path, out var hash, out var problem))
            return MigrationResult.Fail("activation_recheck_" + problem + ":" + record.Path, m.Stage);
        if (!string.Equals(hash, record.AfterHash, StringComparison.Ordinal))
            return MigrationResult.Fail("activation_recheck_hash_mismatch:" + record.Path, m.Stage);
        if (_effects is null) return MigrationResult.Ok(m.Stage);
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var status, out var detail)
            || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
            return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>写集声明校验：非空、路径安全、不重复、逐项等于**已登记变更归属**且无漏项/多项（精确写集）。</summary>
    private static string? ValidateReferencePlan(MigrationManifest m, MigrationReferenceUpdatePlan? plan)
    {
        if (plan?.Targets is null || plan.Targets.Count == 0) return "reference_writeset_mismatch:plan_empty";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in plan.Targets)
        {
            if (target is null) return "reference_writeset_mismatch:null_target";
            if (!IsSafeRelativePath(target.Path)) return "reference_writeset_mismatch:unsafe_path:" + target.Path;
            if (!seen.Add(PathKey(target.Path))) return "reference_writeset_mismatch:duplicate:" + target.Path;
            var record = FindChange(m, target.Path);
            if (record is null) return "reference_writeset_mismatch:not_registered:" + target.Path;
            if (record.Kind != target.Kind) return "reference_writeset_mismatch:kind:" + target.Path;
            switch (target.Kind)
            {
                case ChangeKind.Added when string.IsNullOrEmpty(target.NewContent):
                    return "reference_writeset_mismatch:added_without_content:" + target.Path;
                case ChangeKind.Modified when string.IsNullOrEmpty(target.RenameFrom) || string.IsNullOrEmpty(target.RenameTo)
                    || string.Equals(target.RenameFrom, target.RenameTo, StringComparison.Ordinal):
                    return "reference_writeset_mismatch:modified_without_rename:" + target.Path;
                case ChangeKind.Deleted:
                    return "reference_writeset_mismatch:deleted_target_unsupported:" + target.Path;
            }
        }
        foreach (var record in m.ChangedFiles)
        {
            if (record.Kind == ChangeKind.Deleted)
                return "reference_writeset_mismatch:deleted_record_unsupported:" + record.Path;
            if (!seen.Contains(PathKey(record.Path)))
                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项
        }
        return null;
    }

    /// <summary>激活请求校验：目标必须**已在确认写集内**且为本次真实写入的文件（不得激活写集外目标）。</summary>
    private static string? ValidateActivationRequest(MigrationManifest m, MigrationActivationRequest? request)
    {
        if (request is null) return "activation_request_invalid:null";
        if (!IsSafeRelativePath(request.Path)) return "activation_request_invalid:unsafe_path:" + request.Path;
        if (string.IsNullOrEmpty(request.ExpectedBeforeStatus) || string.IsNullOrEmpty(request.TargetStatus))
            return "activation_request_invalid:status_missing";
        if (string.Equals(request.ExpectedBeforeStatus, request.TargetStatus, StringComparison.Ordinal))
            return "activation_request_invalid:no_state_change";
        if (m.ReferenceWriteSet.Count == 0) return "activation_request_invalid:reference_write_set_empty";
        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4
        var record = FindChange(m, request.Path);
        if (record is null || record.Kind == ChangeKind.Deleted)
            return "activation_target_not_a_written_file:" + request.Path;                    // REF-F4
        return null;
    }

    /// <summary>按大小写不敏感身份查变更归属。</summary>
    private static ChangeRecord? FindChange(MigrationManifest m, string path)
    {
        var key = PathKey(path);
        foreach (var record in m.ChangedFiles) if (PathKey(record.Path) == key) return record;
        return null;
    }

    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>
    private bool TryHashConfigFile(string rel, out string hash, out string problem)
    {
        hash = "";
        problem = "unhashable";
        if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
        var full = Path.Combine(_configRoot, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) { problem = "file_missing"; return false; }
        try
        {
            hash = Sha256Hex(File.ReadAllBytes(full));
            problem = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = "read_failed";
            return false;
        }
    }''')
p.write_text(src, encoding="utf-8")
print("stage2 ok sha256=", hashlib.sha256(src.encode()).hexdigest())
