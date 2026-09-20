using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>事务阶段（**持久化**；除 Committed 外一律不可生产执行）。</summary>
public enum MigrationStage
{
    None = 0,
    Snapshotting = 1,
    SnapshotReady = 2,
    ReferenceUpdating = 3,
    Activated = 4,
    Committed = 5,
    RollingBack = 6,
    RolledBack = 7,
    Blocked = 8,
}

/// <summary>变更归属（回滚据此判定「本事务新增」；**不再删除未登记文件**）。</summary>
public enum ChangeKind { Added = 0, Modified = 1, Deleted = 2 }

/// <summary>一条变更记录（相对路径 + 归属）。</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public ChangeKind Kind { get; set; }
}

/// <summary>迁移 manifest（**全字段完整性 + 结构与状态不变量**）。</summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>本事务变更归属（回滚据此判定新增；无记录 ⇒ 不删除任何文件）。</summary>
    [JsonPropertyName("changedFiles")] public List<ChangeRecord> ChangedFiles { get; set; } = [];
    [JsonPropertyName("snapshotManifestHash")] public string SnapshotManifestHash { get; set; } = "";
    [JsonPropertyName("stage")] public MigrationStage Stage { get; set; }
    /// <summary>唯一提交标记；提交前为空、非提交态必为空。</summary>
    [JsonPropertyName("commitMarker")] public string? CommitMarker { get; set; }
    [JsonPropertyName("rollbackRehearsed")] public bool RollbackRehearsed { get; set; }
    /// <summary>演练范围（绑定快照清单 + 变更归属；变更集改变即失效）。</summary>
    [JsonPropertyName("rehearsalScope")] public string? RehearsalScope { get; set; }
    /// <summary>结构化 blocked 原因（非空 ⇒ 禁止提交与生产执行）。</summary>
    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }
    /// <summary>静止窗口取得时刻（须与 QuiesceSessionId 同会话才算有效）。</summary>
    [JsonPropertyName("quiescedAtUtc")] public DateTimeOffset? QuiescedAtUtc { get; set; }
    /// <summary>取得静止窗口的会话标识（重启后不得凭历史记录提交）。</summary>
    [JsonPropertyName("quiesceSessionId")] public string? QuiesceSessionId { get; set; }
    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
}

/// <summary>事务操作结果。</summary>
public sealed record MigrationResult(bool Success, string Reason, MigrationStage Stage)
{
    public static MigrationResult Ok(MigrationStage s) => new(true, "", s);
    public static MigrationResult Fail(string reason, MigrationStage s) => new(false, reason, s);
}
/// <summary>
/// **R5.6 事务迁移切换（v3：按第 2 轮会诊 7 项必改重构）**。
/// 不变量：①串行边界覆盖全部入口（恢复/变更/授权/执行/回滚均须本实例持锁，且实例内串行；
/// TryRunProduction 的「授权+执行」在同一临界区）；②回滚可恢复（先落 RollingBack 再做 IO，
/// 恢复路径幂等续做）；③静止窗口覆盖全程且绑定会话（重启后不得凭历史时间戳提交）；
/// ④身份/路径前置校验（事务号安全、快照路径属本事务、根绑定、链接拒绝）；
/// ⑤未决事务存在时拒绝开新事务，事务号/快照目录不复用；⑥变更归属按基线校验；
/// ⑦结构与状态不变量 + 全字段完整性双校验，null 字段结构化拒绝。
/// 开发验证只用独立配置根；真实 User 目录切换由 owner 另行下令。
/// </summary>
public sealed class MigrationSwitchTransaction : IDisposable
{
    private readonly string _configRoot;
    private readonly string _transactionRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IDisposable>? _quiesce;
    private readonly bool _requireQuiescence;
    private readonly Action<MigrationStage>? _stageHook;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileStream? _lock;
    private IDisposable? _quiet;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null)
    {
        _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
        _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _quiesce = quiesce;
        _requireQuiescence = requireQuiescence;
        _stageHook = stageHook;
        ValidateRoots();
    }

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");
    internal bool HoldsExclusiveLock => _lock is not null;
    internal string SessionId => _sessionId;

    /// <summary>快照路径＝事务根下按「事务号 + 会话」确定性推导（不复用既有目录）。</summary>
    internal string SnapshotPathOf(string transactionId, string sessionId)
        => Path.Combine(_transactionRoot, "snapshot-" + transactionId + "-" + sessionId);

    private void ValidateRoots()
    {
        var cfg = _configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tx = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (tx.StartsWith(cfg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务根不得位于配置根内（自包含快照会污染备份）。");
        if (cfg.StartsWith(tx, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根不得位于事务根内。");
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (HasReparsePoint(chain))
                throw new InvalidOperationException("根路径链上存在重解析点（junction/符号链接），拒绝迁移事务：" + chain);
    }

    /// <summary>链接逃逸防护：路径链上任一层为 reparse point 即拒绝。</summary>
    internal static bool HasReparsePoint(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            dir = dir.Parent;
        }
        return false;
    }

    /// <summary>相对路径安全校验（拒绝绝对路径/盘符/.. /./空段）。</summary>
    internal static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        var norm = rel.Replace('\\', '/');
        if (norm.StartsWith('/') || norm.Contains(':')) return false;
        foreach (var seg in norm.Split('/'))
            if (seg is ".." or "." || seg.Length == 0) return false;
        return true;
    }

    /// <summary>事务号安全校验（单段安全路径）。</summary>
    internal static bool IsSafeTransactionId(string? txId)
        => IsSafeRelativePath(txId) && !(txId ?? "").Contains('/') && !(txId ?? "").Contains('\\');

    /// <summary>取得事务独占锁（不写 manifest；恢复路径用）。</summary>
    public MigrationResult TryAcquireExclusive()
    {
        lock (_sync)
        {
            if (HoldsExclusiveLock) return MigrationResult.Ok(MigrationStage.None);
            Directory.CreateDirectory(_transactionRoot);
            try
            {
                _lock = new FileStream(Path.Combine(_transactionRoot, "migration.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return MigrationResult.Fail("transaction_busy", MigrationStage.None);
            }
            return MigrationResult.Ok(MigrationStage.None);
        }
    }

    /// <summary>开启新事务：须持锁；有未决事务或事务号占用 ⇒ 拒绝；先持久化 Snapshotting 并取得全程静止窗口。</summary>
    public MigrationResult BeginTransaction(string transactionId)
    {
        lock (_sync)
        {
            if (!IsSafeTransactionId(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
            if (!HoldsExclusiveLock)
            {
                var acq = TryAcquireExclusive();
                if (!acq.Success) return acq;
            }

            var existing = LoadManifest();
            if (existing is not null)
            {
                if (existing.Stage is not (MigrationStage.Committed or MigrationStage.RolledBack))
                    return MigrationResult.Fail("pending_transaction_exists:" + existing.TransactionId, existing.Stage);
                if (string.Equals(existing.TransactionId, transactionId, StringComparison.Ordinal))
                    return MigrationResult.Fail("transaction_id_in_use", existing.Stage);
            }

            var snapshotPath = SnapshotPathOf(transactionId, _sessionId);
            if (Directory.Exists(snapshotPath)) return MigrationResult.Fail("snapshot_path_in_use", MigrationStage.None);

            _quiet = _quiesce?.Invoke();                       // 静止窗口：覆盖全程，提交/回滚/释放时结束
            var manifest = new MigrationManifest
            {
                TransactionId = transactionId,
                CreatedAtUtc = _utcNow(),
                ConfigRoot = _configRoot,
                SnapshotPath = snapshotPath,
                RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
                Stage = MigrationStage.Snapshotting,
                CommitMarker = null,
                RollbackRehearsed = false,
                QuiescedAtUtc = _quiet is null ? null : _utcNow(),
                QuiesceSessionId = _quiet is null ? null : _sessionId,
            };
            WriteManifest(manifest);
            return MigrationResult.Ok(MigrationStage.Snapshotting);
        }
    }
    /// <summary>步骤①②：全量快照（字节+SHA256）→ 清单哈希与快照路径落盘 → SnapshotReady。</summary>
    public MigrationResult TakeSnapshot()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Snapshotting) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var file in EnumerateFiles(_configRoot))
                {
                    var rel = Rel(file, _configRoot);
                    if (!IsSafeRelativePath(rel)) return MarkBlocked("unsafe_path:" + rel);
                    var bytes = File.ReadAllBytes(file);
                    var target = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    hashes[rel] = Sha256Hex(bytes);
                }
            }
            catch (IOException ex)
            {
                return MarkBlocked("snapshot_io_failed:" + ex.GetType().Name);
            }

            m.FileHashes = hashes;
            m.SnapshotManifestHash = ComputeSnapshotManifestHash(hashes);
            m.Stage = MigrationStage.SnapshotReady;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>快照清单哈希（排序后的 相对路径:内容哈希 取 SHA256）。</summary>
    public static string ComputeSnapshotManifestHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        var ordered = (fileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var p in ordered) sb.Append(p.Key).Append(':').Append(p.Value ?? "").Append('\n');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>记录变更归属（基线校验：Added 不得命中快照；Modified/Deleted 必须在快照中）。</summary>
    public MigrationResult RecordChanges(IEnumerable<ChangeRecord> changes)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);

            var batch = new List<ChangeRecord>();
            foreach (var c in changes ?? [])
            {
                var path = (c.Path ?? "").Replace('\\', '/').Trim();
                if (!IsSafeRelativePath(path)) return MigrationResult.Fail("unsafe_path:" + c.Path, m.Stage);
                if (batch.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return MigrationResult.Fail("duplicate_change_path:" + path, m.Stage);
                var inSnapshot = m.FileHashes.ContainsKey(path);
                var ok = c.Kind switch
                {
                    ChangeKind.Added => !inSnapshot,
                    ChangeKind.Modified or ChangeKind.Deleted => inSnapshot,
                    _ => false,
                };
                if (!ok) return MigrationResult.Fail("change_baseline_mismatch:" + path, m.Stage);
                batch.Add(new ChangeRecord { Path = path, Kind = c.Kind });
            }

            foreach (var c in batch)
            {
                m.ChangedFiles.RemoveAll(x => string.Equals(x.Path, c.Path, StringComparison.OrdinalIgnoreCase));
                m.ChangedFiles.Add(c);
            }
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    public MigrationResult MarkReferenceUpdateCompleted() => Advance(MigrationStage.ReferenceUpdating);
    public MigrationResult MarkActivated() => Advance(MigrationStage.Activated);

    private MigrationResult Advance(MigrationStage to)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (!IsLegalAdvance(m.Stage, to)) return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + to, m.Stage);
            m.Stage = to;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>合法阶段转换（跳步提交被拒；回滚可自任意中间态/已提交/阻塞态发起）。</summary>
    public static bool IsLegalAdvance(MigrationStage from, MigrationStage to) => (from, to) switch
    {
        (MigrationStage.Snapshotting, MigrationStage.SnapshotReady) => true,
        (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) => true,
        (MigrationStage.ReferenceUpdating, MigrationStage.Activated) => true,
        (MigrationStage.Activated, MigrationStage.Committed) => true,
        (_, MigrationStage.Blocked) => from is not (MigrationStage.Committed or MigrationStage.RolledBack),
        (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated
            or MigrationStage.Committed or MigrationStage.Blocked or MigrationStage.RollingBack,
            MigrationStage.RollingBack) => true,
        (MigrationStage.RollingBack, MigrationStage.RolledBack) => true,
        (MigrationStage.RolledBack, MigrationStage.RolledBack) => true,
        _ => false,
    };
    /// <summary>步骤④：回滚演练——副本＝快照 → 施加代表变更 → 复用实际回滚核心 → 逐字节校验。</summary>
    public MigrationResult RehearseRollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            var rehearsalRoot = Path.Combine(_transactionRoot, "rehearsal-" + _sessionId);
            try
            {
                if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true);
                Directory.CreateDirectory(rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);
                ApplyRepresentativeChanges(m, rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);      // 复用实际回滚核心
                DeleteRecordedAdditions(m, rehearsalRoot);
                foreach (var p in m.FileHashes)
                {
                    var target = Path.Combine(rehearsalRoot, p.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target)) return MigrationResult.Fail("rehearsal_missing:" + p.Key, m.Stage);
                    if (!string.Equals(Sha256Hex(File.ReadAllBytes(target)), p.Value, StringComparison.Ordinal))
                        return MigrationResult.Fail("rehearsal_hash_mismatch:" + p.Key, m.Stage);
                }
                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                {
                    var target = Path.Combine(rehearsalRoot, added.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target)) return MigrationResult.Fail("rehearsal_addition_not_cleaned:" + added.Path, m.Stage);
                }
            }
            catch (IOException ex)
            {
                return MarkBlocked("rehearsal_io_failed:" + ex.GetType().Name);
            }
            finally
            {
                try { if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true); } catch { }
            }

            m.RollbackRehearsed = true;
            m.RehearsalScope = RehearsalScopeOf(m);
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>提交：阶段 Activated、演练范围一致、无 blocked、静止窗口为当前会话且快照有效 ⇒ 写唯一提交标记。</summary>
    public MigrationResult Commit()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
                return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
            if (_requireQuiescence && (m.QuiescedAtUtc is null || !string.Equals(m.QuiesceSessionId, _sessionId, StringComparison.Ordinal)))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            m.CommitMarker = m.TransactionId;
            m.Stage = MigrationStage.Committed;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>回滚：先落 RollingBack（清提交标记与演练资格）再做 IO，最后落 RolledBack；只删归属为 Added 的文件。</summary>
    public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (!IsLegalAdvance(m.Stage, MigrationStage.RollingBack))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->RollingBack", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            m.Stage = MigrationStage.RollingBack;
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            m.CommitMarker = null;
            WriteManifest(m);
            return CompleteRollback(m);
        }
    }

    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>
    private MigrationResult CompleteRollback(MigrationManifest m)
    {
        lock (_sync)
        {
            try
            {
                RestoreFromSnapshot(m, _configRoot);
                DeleteRecordedAdditions(m, _configRoot);
            }
            catch (IOException ex)
            {
                return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
            }
            m.Stage = MigrationStage.RolledBack;
            m.CommitMarker = null;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>重启恢复（须先持锁）：已提交+标记+无 blocked ⇒ 保持新态；RollingBack ⇒ 幂等续做；RolledBack ⇒ 幂等；其余 ⇒ 回滚旧态。</summary>
    public MigrationResult RecoverOnStart()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.Committed
                && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(m.BlockedReason))
                return MigrationResult.Ok(MigrationStage.Committed);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (m.Stage == MigrationStage.None) return MigrationResult.Fail("illegal_stage:None", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("recover_snapshot_invalid:" + bad);

            if (m.Stage != MigrationStage.RollingBack)
            {
                m.Stage = MigrationStage.RollingBack;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.CommitMarker = null;
                WriteManifest(m);
            }
            return CompleteRollback(m);
        }
    }
    /// <summary>授权（须持锁）：完整性校验 → 已提交 → 标记匹配 → 无 blocked。</summary>
    public MigrationResult AuthorizeProductionExecution()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
                return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>
    public MigrationResult TryRunProduction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            var auth = AuthorizeProductionExecution();
            if (!auth.Success) return auth;
            action();
            return MigrationResult.Ok(MigrationStage.Committed);
        }
    }

    /// <summary>快照完整性：manifest 结构与不变量 → 快照文件齐全/哈希一致 → 无未登记多余文件。</summary>
    public string VerifySnapshot()
    {
        var m = LoadManifest();
        if (m is null) return "manifest_missing_or_corrupt";
        if (!IsManifestIntegrityValid(m)) return "manifest_integrity_mismatch";
        if (!string.Equals(m.SnapshotManifestHash, ComputeSnapshotManifestHash(m.FileHashes), StringComparison.Ordinal))
            return "snapshot_manifest_hash_mismatch";
        if (!Directory.Exists(m.SnapshotPath)) return "snapshot_missing";

        var onDisk = EnumerateFiles(m.SnapshotPath).Select(f => Rel(f, m.SnapshotPath)).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in onDisk.Where(f => !m.FileHashes.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;
        foreach (var p in m.FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var target = Path.Combine(m.SnapshotPath, p.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;
            if (!string.Equals(Sha256Hex(File.ReadAllBytes(target)), p.Value, StringComparison.Ordinal))
                return "snapshot_hash_mismatch:" + p.Key;
        }
        return "";
    }

    /// <summary>全字段完整性摘要（仅完整性，不提供来源认证）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|');
        foreach (var c in (m.ChangedFiles ?? []).OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>结构与状态不变量校验（先于摘要校验；null/非法枚举/非法组合一律拒绝）。</summary>
    public bool IsManifestIntegrityValid(MigrationManifest m)
    {
        if (m is null) return false;
        if (m.SchemaVersion != 1) return false;
        if (!IsSafeTransactionId(m.TransactionId)) return false;
        if (!string.Equals(m.ConfigRoot, _configRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsWithin(m.SnapshotPath, _transactionRoot)) return false;
        if (!Enum.IsDefined(m.Stage)) return false;
        if (m.FileHashes is null || m.ChangedFiles is null) return false;
        foreach (var k in m.FileHashes.Keys) if (!IsSafeRelativePath(k)) return false;
        foreach (var c in m.ChangedFiles)
        {
            if (c is null || !IsSafeRelativePath(c.Path) || !Enum.IsDefined(c.Kind)) return false;
            var inSnapshot = m.FileHashes.ContainsKey(c.Path);
            if (c.Kind == ChangeKind.Added && inSnapshot) return false;
            if (c.Kind is ChangeKind.Modified or ChangeKind.Deleted && !inSnapshot) return false;
        }
        if (m.Stage == MigrationStage.Committed)
        {
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)) return false;
        }
        else if (!string.IsNullOrEmpty(m.CommitMarker)) return false;
        if (m.Stage == MigrationStage.RolledBack && (m.RollbackRehearsed || m.RehearsalScope is not null)) return false;
        if (m.RollbackRehearsed && string.IsNullOrEmpty(m.RehearsalScope)) return false;
        if (m.BlockedReason is { Length: 0 }) return false;
        return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);
    }

    private static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取并按结构+不变量+完整性校验的 manifest（不合格＝null）。</summary>
    public MigrationManifest? LoadValidated()
    {
        var m = LoadManifest();
        return m is not null && IsManifestIntegrityValid(m) ? m : null;
    }

    /// <summary>读取 manifest（原样，不校验；业务判定用 LoadValidated）。</summary>
    public MigrationManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private MigrationResult MarkBlocked(string reason)
    {
        var m = LoadManifest();
        if (m is null) return MigrationResult.Fail(reason, MigrationStage.None);
        m.BlockedReason = reason;
        m.Stage = MigrationStage.Blocked;
        m.RollbackRehearsed = false;
        m.RehearsalScope = null;
        WriteManifest(m);
        return MigrationResult.Fail(reason, MigrationStage.Blocked);
    }

    private static string RehearsalScopeOf(MigrationManifest m)
    {
        var sb = new StringBuilder(m.SnapshotManifestHash).Append('|');
        foreach (var c in m.ChangedFiles.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void ApplyRepresentativeChanges(MigrationManifest m, string root)
    {
        foreach (var c in m.ChangedFiles)
        {
            var target = Path.Combine(root, c.Path.Replace('/', Path.DirectorySeparatorChar));
            switch (c.Kind)
            {
                case ChangeKind.Added:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "rehearsal-added", new UTF8Encoding(false));
                    break;
                case ChangeKind.Modified:
                    if (File.Exists(target)) File.WriteAllText(target, "rehearsal-modified", new UTF8Encoding(false));
                    break;
                case ChangeKind.Deleted:
                    if (File.Exists(target)) File.Delete(target);
                    break;
            }
        }
    }

    private void WriteManifest(MigrationManifest manifest)
    {
        Directory.CreateDirectory(_transactionRoot);
        _stageHook?.Invoke(manifest.Stage);
        manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true);
        _stageHook?.Invoke(manifest.Stage);
    }

    private static void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            if (!IsSafeRelativePath(rel)) continue;
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
        }
    }

    /// <summary>只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。</summary>
    private static void DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeRelativePath(c.Path)) continue;
            var target = Path.Combine(targetRoot, c.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) File.Delete(target);
        }
    }

    private void ReleaseQuiescence()
    {
        try { _quiet?.Dispose(); } catch { }
        _quiet = null;
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
    private static string Rel(string file, string root) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        lock (_sync)
        {
            ReleaseQuiescence();
            _lock?.Dispose();
            _lock = null;
        }
    }
}