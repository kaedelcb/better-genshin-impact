import hashlib, pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
src = p.read_text(encoding="utf-8")
def rep(old, new, count=1):
    global src
    assert src.count(old) == count, (src.count(old), old[:90])
    src = src.replace(old, new, count)

# ---- A) manifest flag ----
rep('''    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
    /// </summary>
    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }''',
'''    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
    /// </summary>
    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }
    /// <summary>
    /// **本事务是否由真实副作用端口建立**（`BeginTransaction` 时按实例是否注入端口写入；受完整性摘要覆盖）。
    /// `true` ⇒ 阶段 `ReferenceUpdating/Activated/Committed` 必须携带真实写集/激活读回证据，且阶段标记只能由
    /// 真实副作用 + 读回确认推进；入口回绝 `MarkReferenceUpdateCompleted`/`MarkActivated`。
    /// `false` ⇒ 仅限未注入端口的只读夹具（旧行为保留）。
    /// </summary>
    [JsonPropertyName("realEffectsRequired")] public bool RealEffectsRequired { get; set; }''')

# ---- B) BeginTransaction sets the flag ----
rep('''                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
            };''',
'''                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
                RealEffectsRequired = _effects is not null,
            };''')

# ---- C) Commit gate ----
rep('''            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))''',
'''            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (m.RealEffectsRequired)
            {
                // **真实事务的提交前置**：引用写入与激活都必须由真实副作用 + 读回确认产生，且提交前重核读回证据
                if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);
                if (m.ActivationRecord is null) return MigrationResult.Fail("activation_not_confirmed", m.Stage);
                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
            }
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))''')

# ---- D) rollback: undo real activation + post-restore consistency ----
rep('''            try
            {
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
            }''',
'''            try
            {
                // **先撤销真实激活**（语义前沿先回退），再整份恢复旧字节；两步都须读回确认。
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
                // **回滚后旧态一致性**：写集内每个文件要么回到基线字节，要么（本事务新增）已不存在
                if (m.RealEffectsRequired)
                {
                    foreach (var entry in m.ReferenceWriteSet)
                    {
                        var record = FindChange(m, entry.Key);
                        if (record is { Kind: ChangeKind.Added })
                        {
                            if (File.Exists(Path.Combine(_configRoot, NormalizePath(entry.Key).Replace('/', Path.DirectorySeparatorChar))))
                                return MarkBlocked("rollback_addition_still_present:" + entry.Key);
                            continue;
                        }
                        if (!TryHashConfigFile(entry.Key, out var hash, out var problem))
                            return MarkBlocked("rollback_restore_" + problem + ":" + entry.Key);
                        if (!TryGetHashCaseInsensitive(m.FileHashes, entry.Key, out var baseline)
                            || !string.Equals(hash, baseline, StringComparison.Ordinal))
                            return MarkBlocked("rollback_restore_bytes_differ:" + entry.Key);
                    }
                    if (m.ActivationRecord is { } restored)
                    {
                        var kind = FindChange(m, restored.Path)?.Kind;
                        if (kind == ChangeKind.Added)
                        {
                            if (File.Exists(Path.Combine(_configRoot, NormalizePath(restored.Path).Replace('/', Path.DirectorySeparatorChar))))
                                return MarkBlocked("rollback_activation_target_still_present:" + restored.Path);
                        }
                        else if (!_effects!.TryReadActivationStatus(_configRoot, restored.Path, out var finalStatus, out var finalDetail)
                            || !string.Equals(finalStatus, restored.BeforeStatus, StringComparison.Ordinal))
                        {
                            return MarkBlocked("rollback_activation_state_after_restore:" + restored.Path + ":" + finalDetail);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
            }''')

# ---- E) UndoActivation helper (inserted before CompleteRollback) ----
rep('''    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>''',
'''    /// <summary>
    /// **撤销真实激活**：读回当前状态 → 仍为 `AfterStatus` 时施加反向副作用 → **再读回**确认等于 `BeforeStatus`。
    /// 任一读回失败或状态异常 ⇒ `Blocked`（**绝不**在未确认时报告完整回滚；本事务新增文件已不存在视为旧态）。
    /// </summary>
    private MigrationResult UndoActivation(MigrationManifest m, MigrationActivationRecord record)
    {
        var kind = FindChange(m, record.Path)?.Kind;
        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var current, out var detail))
        {
            if (kind == ChangeKind.Added && detail == "target_missing")
                return MigrationResult.Ok(m.Stage);            // 本事务新增文件已不存在＝旧态（无激活可撤销）
            return MarkBlocked("rollback_activation_readback_failed:" + record.Path + ":" + detail);
        }
        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
        {
            var undo = _effects.Activate(_configRoot,
                new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus));
            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
        }
        else if (!string.Equals(current, record.BeforeStatus, StringComparison.Ordinal))
        {
            return MarkBlocked("rollback_activation_state_unexpected:" + record.Path + ":" + current);
        }
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var afterUndo, out var undoDetail)
            || !string.Equals(afterUndo, record.BeforeStatus, StringComparison.Ordinal))
            return MarkBlocked("rollback_activation_not_reverted:" + record.Path + ":" + undoDetail);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>''')

# ---- F) integrity digest coverage ----
rep('''        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');''',
'''        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
        sb.Append(m.RealEffectsRequired ? '1' : '0').Append('|');
        foreach (var p in (m.ReferenceWriteSet ?? new Dictionary<string, string>(StringComparer.Ordinal))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
        sb.Append('|').Append(m.ActivationRecord is { } ar
            ? ar.Path + ':' + ar.BeforeStatus + '>' + ar.AfterStatus + ':' + ar.AfterHash : "<null>").Append('|');''')

# ---- G) structural validation ----
rep('''        if (m.BlockedReason is { Length: 0 }) return false;
        return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);''',
'''        if (m.BlockedReason is { Length: 0 }) return false;
        if (m.ReferenceWriteSet is null) return false;
        foreach (var entry in m.ReferenceWriteSet)
            if (!IsSafeRelativePath(entry.Key) || string.IsNullOrEmpty(entry.Value)) return false;
        if (m.ActivationRecord is { } activation)
        {
            if (!IsSafeRelativePath(activation.Path)) return false;
            if (string.IsNullOrEmpty(activation.BeforeStatus) || string.IsNullOrEmpty(activation.AfterStatus)
                || string.IsNullOrEmpty(activation.AfterHash)) return false;
            if (string.Equals(activation.BeforeStatus, activation.AfterStatus, StringComparison.Ordinal)) return false;
            if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(activation.Path))) return false;   // 激活目标须在写集内
        }
        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }
        return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);''')
p.write_text(src, encoding="utf-8")
print("stage3 ok sha256=", hashlib.sha256(src.encode()).hexdigest())
