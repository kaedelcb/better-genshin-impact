import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:100])
    s = s.replace(old, new, cnt)

# ---------- MUST-1 / IMPORTANT-7 / IMPORTANT-8：激活入口的前置、状态与异常处置 ----------
rep('''            if (m.Stage != MigrationStage.ReferenceUpdating)
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
                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);''',
'''            if (m.Stage != MigrationStage.ReferenceUpdating)
                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);
            if (ValidateActivationRequest(m, request) is { } requestProblem) return MarkBlocked(requestProblem);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            // **激活前的字节版本核对（会诊 MUST-1）**：激活必须基于**已确认写集**所记录的那一份字节；
            // 若该文件自引用更新确认后已被锁外改动，则激活会把它「连同漂移一起合法化」——此处一律 fail-closed。
            if (!TryGetWriteSetHash(m, request!.Path, out var confirmedHash))
                return MarkBlocked("activation_writeset_hash_missing:" + request.Path);
            if (!TryHashConfigFile(request.Path, out var preHash, out var preProblem))
                return MarkBlocked("activation_precondition_" + preProblem + ":" + request.Path);
            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_drifted:" + request.Path);

            // **前置状态观测（会诊 IMPORTANT-7）**：盘上现值必须**恰为**声明的 before 状态；已等于目标态 ⇒ 拒绝，
            // 不得凭「已生效」零写入成功（幂等复核只经由已持久化的 `Activated` 阶段证据）。
            string observed;
            string observeDetail;
            try
            {
                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out observed, out observeDetail))
                    return MarkBlocked("activation_precondition_status_unreadable:" + request.Path + ":" + observeDetail);
            }
            catch (Exception ex)
            {
                return MarkBlocked("activation_precondition_exception:" + request.Path + ":" + ex.GetType().Name);
            }
            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_already_applied:" + request.Path);
            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);

            MigrationEffectResult result;
            try
            {
                result = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
            }
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("activation_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("activation_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            try
            {
                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)
                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
            }
            catch (Exception ex)
            {
                return MarkBlocked("activation_readback_exception:" + request.Path + ":" + ex.GetType().Name);
            }
            if (!TryHashConfigFile(request.Path, out var hash, out var hashProblem))
                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);''')

# ---------- IMPORTANT-7：激活请求只接受权威 D13 转换 ----------
rep('''        if (string.Equals(request.ExpectedBeforeStatus, request.TargetStatus, StringComparison.Ordinal))
            return "activation_request_invalid:no_state_change";''',
'''        if (string.Equals(request.ExpectedBeforeStatus, request.TargetStatus, StringComparison.Ordinal))
            return "activation_request_invalid:no_state_change";
        // **只接受权威的 D13 转换**（R5.2 §21.2：激活＝`candidate → active`）：本事务不发明通用状态改写器；
        // 其余状态对一律拒绝（回滚的撤销路径不经此入口，直接用端口）。
        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;''')

# ---------- NEEDED: 常量 + TryGetWriteSetHash ----------
rep('''    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");''',
'''    /// <summary>权威 D13 激活词（R5.2 §21.2：`candidate → active`）；仅本事务的激活入口使用，不作通用状态改写器。</summary>
    internal const string CandidateReadyStatus = "candidate-ready";
    internal const string ActiveStatus = "active";

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");''')

rep('''    /// <summary>按大小写不敏感身份查变更归属。</summary>''',
'''    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>
    private static bool TryGetWriteSetHash(MigrationManifest m, string rel, out string hash)
    {
        hash = "";
        var key = PathKey(rel);
        foreach (var entry in m.ReferenceWriteSet)
        {
            if (PathKey(entry.Key) != key) continue;
            hash = entry.Value;
            return true;
        }
        return false;
    }

    /// <summary>按大小写不敏感身份查变更归属。</summary>''')
p.write_text(s, encoding="utf-8")
print("transaction stage-1 patched", hashlib.sha256(s.encode()).hexdigest()[:16])
