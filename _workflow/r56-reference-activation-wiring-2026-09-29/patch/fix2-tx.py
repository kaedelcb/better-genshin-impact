import pathlib, hashlib
p=pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s=p.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt,(s.count(old),old[:100])
    s=s.replace(old,new,cnt)

# ---- 重入守卫 + 副作用期间的代次（IMPORTANT-7） ----
rep('''    private FileStream? _lock;''','''    private FileStream? _lock;
    /// <summary>**重入守卫**：外部副作用/读回回调执行期间置位；此期间任何变更入口一律拒绝（monitor 可重入，
    /// 单靠 `lock` 不能阻止回调同步重入事务，见会诊第 2 轮 IMPORTANT-7）。</summary>
    private bool _effectCallInProgress;''')
rep('''            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);''',
'''            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);''')
rep('''            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**''',
'''            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**''')

# ---- 副作用调用统一走带守卫的包装，并在返回后核对阶段未被他路改变 ----
rep('''            MigrationEffectResult result;
            try
            {
                result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            }''',
'''            var stageBeforeEffect = m.Stage;
            MigrationEffectResult result;
            try
            {
                _effectCallInProgress = true;
                result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            }
            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeEffect)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());''')
rep('''            MigrationEffectResult result;
            try
            {
                result = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
            }''',
'''            var stageBeforeActivation = m.Stage;
            MigrationEffectResult result;
            try
            {
                _effectCallInProgress = true;
                result = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
            }
            finally
            {
                _effectCallInProgress = false;
            }
            if (CurrentStageOrNone() != stageBeforeActivation)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeActivation + "->" + CurrentStageOrNone());''')

# ---- 激活请求携带版本哈希；撤销请求携带撤销前实际字节哈希 ----
rep('''            if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out observed, out observeDetail))''',
'''            if (string.IsNullOrEmpty(request.ExpectedContentHash))
                return MarkBlocked("activation_request_invalid:missing_content_hash:" + request.Path);
            if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out observed, out observeDetail))''')
rep('''                undo = _effects.Activate(_configRoot,
                    new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus));''',
'''                undo = _effects.Activate(_configRoot,
                    new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus, record.AfterHash));''')

# ---- 归属：先判不存在，再判证据；并把判据绑定实例（IMPORTANT-5 / MUST-2） ----
rep('''        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!TryGetWriteSetHash(m, c.Path, out var owned)) return "rollback_addition_without_ownership_evidence:" + c.Path;
            if (!TryHashConfigFile(c.Path, out var currentHash, out var problem))
            {
                if (problem == "file_missing") continue;              // 已不存在 ⇒ 无冲突
                return "rollback_addition_unverifiable:" + c.Path + ":" + problem;
            }
            if (!string.Equals(currentHash, owned, StringComparison.Ordinal))
                return "rollback_addition_not_owned:" + c.Path;       // 他方文件/被改动 ⇒ 保留并阻断
        }
        return null;''',
'''        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!TryHashConfigFile(c.Path, out var currentHash, out var problem))
            {
                // **先判不存在**：目标不存在 ⇒ 无需归属证据（否则「只快照+登记、尚未写入」的合法中止会被永久阻断）
                if (problem == "file_missing") continue;
                return "rollback_addition_unverifiable:" + c.Path + ":" + problem;
            }
            if (!TryGetWriteSetHash(m, c.Path, out var owned))
                return "rollback_addition_without_ownership_evidence:" + c.Path;   // 存在但无证据 ⇒ 保留并阻断
            if (!string.Equals(currentHash, owned, StringComparison.Ordinal))
                return "rollback_addition_not_owned:" + c.Path;       // 他方文件/被改动 ⇒ 保留并阻断
        }
        return null;''')

# ---- 回滚顺序：归属预检（含实例绑定）先于任何写入；删除时不再按已失效哈希复核 ----
rep('''                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                // **归属证据（会诊 MUST-2）**：真实写入路径下，只有「内容等于本事务所写字节」的新增文件才可删除；
                // 同名但他方创建/被改动 ⇒ 保留并阻断，绝不把他方文件当本事务新增删掉。
                if (m.RealEffectsRequired && OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                RestoreFromSnapshot(m, _configRoot);
                var ownershipChecked = m.RealEffectsRequired;
                if (DeleteRecordedAdditions(m, _configRoot, ownershipChecked) > 0)''',
'''                // **归属预检必须先于任何写入（会诊第 2 轮 MUST-1）**：撤销激活本身也是写入，
                // 若目标是他方文件，先写再查会破坏他方内容；故先核对归属，再决定是否允许写入。
                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
                var ownedAdditions = new HashSet<string>(StringComparer.Ordinal);
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownedAdditions.Add(PathKey(added.Path));
                }
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, ownedAdditions) > 0)''')
rep('''    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, bool requireOwnershipEvidence = false)''',
'''    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, HashSet<string>? ownershipVerified = null)''')
rep('''            if (requireOwnershipEvidence)
            {
                // 归属证据：内容必须等于本事务写入的字节；否则视为他方文件，保留并计为未完成（不删）
                if (!TryGetWriteSetHash(m, c.Path, out var ownedHash)
                    || !TryHashConfigFileOnRoot(targetRoot, c.Path, out var currentHash)
                    || !string.Equals(ownedHash, currentHash, StringComparison.Ordinal))
                {
                    failed++;
                    continue;
                }
            }''',
'''            if (ownershipVerified is not null)
            {
                // 归属已在**写入之前**核对并登记（见 CompleteRollback）；此后本事务自己的撤销会改变字节，
                // 故此处不再按（已失效的）写集哈希复核，只按预先核验的归属集合决定是否删除。
                if (!ownershipVerified.Contains(PathKey(c.Path))) { failed++; continue; }
            }''')

# ---- 提交面：文件集合必须**相等**（缺文件同样阻断）（MUST-4） ----
rep('''        var expected = new HashSet<string>(m.FileHashes.Keys.Select(PathKey), StringComparer.Ordinal);
        foreach (var target in declared)
            if (target.Kind == ChangeKind.Added) expected.Add(PathKey(target.Path));
        foreach (var path in current)
            if (!expected.Contains(path)) return "unexpected_new_file_outside_writeset:" + path;
        return null;''',
'''        var expected = new HashSet<string>(m.FileHashes.Keys.Select(PathKey), StringComparer.Ordinal);
        foreach (var target in declared)
            if (target.Kind == ChangeKind.Added) expected.Add(PathKey(target.Path));
        foreach (var path in current)
            if (!expected.Contains(path)) return "unexpected_new_file_outside_writeset:" + path;
        var present = new HashSet<string>(current, StringComparer.Ordinal);
        foreach (var baseline in m.FileHashes.Keys)
            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半
        return null;''')

# ---- 结构不变量：变更登记身份唯一 + 写集身份唯一（MUST-4 残余） ----
rep('''        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);''',
'''        var changeIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in m.ChangedFiles)
            if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;
        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);''')

# ---- 异常边界补全（IMPORTANT-6） ----
rep('''        if (_effects is null) return MigrationResult.Ok(m.Stage);
        if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var status, out var detail)
            || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
            return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
        return MigrationResult.Ok(m.Stage);''',
'''        if (_effects is null) return MigrationResult.Ok(m.Stage);
        try
        {
            if (!_effects.TryReadActivationStatus(_configRoot, record.Path, out var status, out var detail)
                || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
                return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
        }
        catch (Exception ex)      // 复核期异常同样收敛（会诊第 2 轮 IMPORTANT-6）
        {
            return MigrationResult.Fail("activation_recheck_exception:" + record.Path + ":" + ex.GetType().Name, m.Stage);
        }
        return MigrationResult.Ok(m.Stage);''')
rep('''        if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }''',
'''        try
        {
            if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
        }
        catch (Exception)     // 安全检查自身异常同样收敛为「不可哈希」（会诊第 2 轮 IMPORTANT-6）
        {
            problem = "unsafe_target_check_failed";
            return false;
        }''')
rep('''    private static string Sha256Hex(byte[] bytes)''',
'''    /// <summary>当前持久化阶段（读取失败返回 None；用于副作用返回后核对未被重入改变）。</summary>
    private MigrationStage CurrentStageOrNone()
    {
        try { return LoadManifest()?.Stage ?? MigrationStage.None; }
        catch (Exception) { return MigrationStage.None; }
    }

    private static string Sha256Hex(byte[] bytes)''')
p.write_text(s,encoding="utf-8")
print("tx round-2 fixes applied", hashlib.sha256(s.encode()).hexdigest()[:16])
