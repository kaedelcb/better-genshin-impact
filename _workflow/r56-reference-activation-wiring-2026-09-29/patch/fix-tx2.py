import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:100])
    s = s.replace(old, new, cnt)

# ---------- MUST-3：写集外「新增文件」检测（当前文件集合 == 基线 ∪ 声明新增） ----------
rep('''            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）''',
'''            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);
            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）''')

# ---------- MUST-3 helper + MUST-4 结构关系不变量辅助 ----------
rep('''    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>''',
'''    /// <summary>
    /// **写集外新增文件检测（会诊 MUST-3）**：配置根当前文件集合必须等于「基线 ∪ 声明写集中的新增目标」。
    /// 只比较基线清单会漏掉「副作用在写集外新建了文件」；本检查补齐该面（静止窗口下无其他写方，新增即本事务产物）。
    /// 返回 null 表示一致。
    /// </summary>
    private string? UnexpectedFileReason(MigrationManifest m, IEnumerable<MigrationReferenceWriteTarget> declared)
    {
        List<string> current;
        try { current = EnumerateFilesSafe(_configRoot).Select(p => PathKey(p)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "config_root_enumeration_failed:" + ex.GetType().Name;
        }
        var expected = new HashSet<string>(m.FileHashes.Keys.Select(PathKey), StringComparer.Ordinal);
        foreach (var target in declared)
            if (target.Kind == ChangeKind.Added) expected.Add(PathKey(target.Path));
        foreach (var path in current)
            if (!expected.Contains(path)) return "unexpected_new_file_outside_writeset:" + path;
        return null;
    }

    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>''')

# ---------- MUST-5：回滚改为核对**完整基线字节集**（不依赖成功写集） ----------
rep('''                // **回滚后旧态一致性**：写集内每个文件要么回到基线字节，要么（本事务新增）已不存在
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
                    if (m.ActivationRecord is { } restored)''',
'''                // **回滚后旧态一致性（会诊 MUST-5）**：核对**完整基线字节集**（而不是「成功写集」——部分写后
                // Unknown / 阶段发布前失败时写集为空，只查写集会空过并假报完整回滚）。
                foreach (var baseline in m.FileHashes)
                {
                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))
                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);
                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))
                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);
                }
                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                {
                    var target = Path.Combine(_configRoot, NormalizePath(added.Path).Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target))
                        return MarkBlocked("rollback_addition_still_present:" + added.Path);
                }
                if (m.RealEffectsRequired)
                {
                    if (m.ActivationRecord is { } restored)''')

# ---------- MUST-2：回滚删除「本事务新增」需具备归属证据（真实写入路径） ----------
rep('''                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断''',
'''                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                // **归属证据（会诊 MUST-2）**：真实写入路径下，只有「内容等于本事务所写字节」的新增文件才可删除；
                // 同名但他方创建/被改动 ⇒ 保留并阻断，绝不把他方文件当本事务新增删掉。
                if (m.RealEffectsRequired && OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                RestoreFromSnapshot(m, _configRoot);
                var ownershipChecked = m.RealEffectsRequired;
                if (DeleteRecordedAdditions(m, _configRoot, ownershipChecked) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断''')

rep('''    private static int DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
    {
        var failed = 0;
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeTarget(targetRoot, c.Path)) { failed++; continue; }
            var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
            if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }''',
'''    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, bool requireOwnershipEvidence = false)
    {
        var failed = 0;
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeTarget(targetRoot, c.Path)) { failed++; continue; }
            var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
            if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
            if (requireOwnershipEvidence)
            {
                // 归属证据：内容必须等于本事务写入的字节；否则视为他方文件，保留并计为未完成（不删）
                if (!TryGetWriteSetHash(m, c.Path, out var owned) || requireOwnershipEvidence
                    && !TryHashConfigFileOnRoot(targetRoot, c.Path, out var currentHash)
                    || !string.Equals(owned, currentHash, StringComparison.Ordinal))
                {
                    failed++;
                    continue;
                }
            }
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }

    /// <summary>归属冲突预检：真实写入路径下，任何「新增」文件若不存在或字节不等于本事务所写 ⇒ 冲突（不删除）。</summary>
    private string? OwnershipConflictReason(MigrationManifest m)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
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
        return null;
    }

    /// <summary>指定根下文件的 SHA-256（用于演练副本的归属核对；不做真实根绑定）。</summary>
    private static bool TryHashConfigFileOnRoot(string root, string rel, out string hash)
    {
        hash = "";
        var full = Path.Combine(root, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return false;
        try { hash = Sha256Hex(File.ReadAllBytes(full)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }''')
p.write_text(s, encoding="utf-8")
print("transaction stage-2 patched", hashlib.sha256(s.encode()).hexdigest()[:16])
