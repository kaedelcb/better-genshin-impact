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
    /// <summary>不可变快照身份（＝开事务时的会话标识）；快照路径**精确**绑定本事务，不用前缀判定。</summary>
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>本事务变更归属（回滚据此判定新增；无记录 ⇒ 不删除任何文件）。</summary>
    [JsonPropertyName("changedFiles")] public List<ChangeRecord> ChangedFiles { get; set; } = [];
    [JsonPropertyName("snapshotManifestHash")] public string SnapshotManifestHash { get; set; } = "";
    /// <summary>**基线是否完成**（快照清单已发布）。未完成 ⇒ 允许安全中止（**不得**用部分快照恢复）。</summary>
    [JsonPropertyName("baselineCompleted")] public bool BaselineCompleted { get; set; }
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
    /// <summary>静止窗口**代次**（释放即失效；同实例重新取锁不得复用历史资格）。</summary>
    [JsonPropertyName("quiesceGeneration")] public int QuiesceGeneration { get; set; }
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
    private readonly Action<string>? _fileRestoredHook;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileStream? _lock;
    private IDisposable? _quiet;
    private bool _quietValid;
    private int _quietGeneration;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null)
    {
        _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
        _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _quiesce = quiesce;
        _requireQuiescence = requireQuiescence;
        _stageHook = stageHook;
        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
        ValidateRoots();
    }

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");

    /// <summary>事务号**占用历史**（追加式；重复事务号一律拒绝——不依赖当前 manifest 是否仍在）。</summary>
    public string HistoryPath => Path.Combine(_transactionRoot, "migration-history.txt");
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

    /// <summary>
    /// 相对路径安全校验（**先拒绝不支持的原始路径**，不做静默裁剪）：绝对路径/盘符/`..`/`.`/空段、
    /// 控制字符、以及段内**前导/尾随空格或尾点**（Windows 会裁剪 ⇒ 别名冲突）一律拒绝。
    /// </summary>
    internal static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        if (rel.Any(char.IsControl)) return false;                 // NUL 等控制字符拒绝（避免路径解析异常/绕过）
        var norm = rel.Replace('\\', '/');
        if (norm.StartsWith('/') || norm.Contains(':')) return false;
        foreach (var seg in norm.Split('/'))
        {
            if (seg is ".." or "." || seg.Length == 0) return false;
            if (seg.EndsWith('.') || seg.StartsWith(' ') || seg.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>路径身份规范化（**只统一分隔符，不裁剪**——裁剪会把合法文件名映射到另一个文件）。</summary>
    internal static string NormalizePath(string? rel) => (rel ?? "").Replace('\\', '/');

    /// <summary>路径**身份键**（Windows 语义：大小写不敏感）——基线、变更记录与实际文件操作三处统一使用。</summary>
    internal static string PathKey(string? rel) => NormalizePath(rel).ToLowerInvariant();

    /// <summary>按身份键查快照哈希（大小写不敏感；避免 `A.json` 与 `a.json` 在 Windows 上互相删改）。</summary>
    internal static bool TryGetHashCaseInsensitive(IReadOnlyDictionary<string, string> hashes, string? path, out string? hash)
    {
        var key = PathKey(path);
        foreach (var p in hashes)
        {
            if (PathKey(p.Key) == key)
            {
                hash = p.Value;
                return true;
            }
        }
        hash = null;
        return false;
    }

    /// <summary>目标路径安全性：规范化后必须仍在 root 内，且**父目录链上无 reparse point**（逐段链接拒绝）。</summary>
    internal static bool IsSafeTarget(string root, string rel)
    {
        if (!IsSafeRelativePath(rel)) return false;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            return false;                                                                         // **目标文件本身**是链接 ⇒ 拒绝
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;   // 父链任一段链接 ⇒ 拒绝
            if (string.Equals(dir.FullName.TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase)) break;
            dir = dir.Parent;
        }
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

            if (File.Exists(ManifestPath))
            {
                var validated = LoadValidated();
                if (validated is null) return MigrationResult.Fail("pending_manifest_corrupt", MigrationStage.None);  // 存在但不可验证 ⇒ 保守阻断
                if (validated.Stage is not (MigrationStage.Committed or MigrationStage.RolledBack))
                    return MigrationResult.Fail("pending_transaction_exists:" + validated.TransactionId, validated.Stage);
                if (string.Equals(validated.TransactionId, transactionId, StringComparison.Ordinal))
                    return MigrationResult.Fail("transaction_id_in_use", validated.Stage);
            }
            if (File.Exists(HistoryPath) && File.ReadAllLines(HistoryPath).Any(l => string.Equals(l.Trim(), transactionId, StringComparison.Ordinal)))
                return MigrationResult.Fail("transaction_id_in_use", MigrationStage.None);        // 历史占用：事务号不复用

            var snapshotPath = SnapshotPathOf(transactionId, _sessionId);
            if (Directory.Exists(snapshotPath)) return MigrationResult.Fail("snapshot_path_in_use", MigrationStage.None);

            _quiet = _quiesce?.Invoke();                       // 静止窗口：覆盖全程，提交/回滚/释放时结束
            _quietValid = _quiet is not null;
            _quietGeneration++;
            var manifest = new MigrationManifest
            {
                TransactionId = transactionId,
                CreatedAtUtc = _utcNow(),
                ConfigRoot = _configRoot,
                SnapshotPath = snapshotPath,
                SnapshotId = _sessionId,
                RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
                Stage = MigrationStage.Snapshotting,
                CommitMarker = null,
                RollbackRehearsed = false,
                QuiescedAtUtc = _quiet is null ? null : _utcNow(),
                QuiesceSessionId = _quiet is null ? null : _sessionId,
                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
            };
            // **先持久化占号、再发布 manifest**（占号失败/崩溃也保守占号 ⇒ 事务号不复用；不依赖窗口是否存在）。
            File.AppendAllText(HistoryPath, transactionId + Environment.NewLine);
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
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 采集期须有**存续**窗口（重开实例不得续用旧资格）
            if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(m.SnapshotPath);   // **空配置根也须建立可验证快照目录**（否则基线完成后 VerifySnapshot 报 snapshot_missing）
                foreach (var file in EnumerateFiles(_configRoot))
                {
                    var rel = Rel(file, _configRoot);
                    if (!IsSafeTarget(_configRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                        return MarkBlocked("unsafe_path:" + rel);      // 读端与写端都须安全（含目标文件本身与父链链接）
                    var bytes = File.ReadAllBytes(file);
                    var target = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    hashes[rel] = Sha256Hex(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("snapshot_io_failed:" + ex.GetType().Name);
            }

            m.FileHashes = hashes;
            m.SnapshotManifestHash = ComputeSnapshotManifestHash(hashes);
            m.BaselineCompleted = true;                       // 基线（完整清单）已发布
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
                if (c is null) return MigrationResult.Fail("null_change_record", m.Stage);
                var raw = c.Path ?? "";
                if (!IsSafeRelativePath(raw)) return MigrationResult.Fail("unsafe_path:" + raw, m.Stage);   // **先验原始输入**
                var path = NormalizePath(raw);                                                             // 再统一解析（**不裁剪**）
                if (batch.Any(x => PathKey(x.Path) == PathKey(path)))
                    return MigrationResult.Fail("duplicate_change_path:" + path, m.Stage);
                var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, path, out _);
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
                m.ChangedFiles.RemoveAll(x => PathKey(x.Path) == PathKey(c.Path));
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
        (MigrationStage.Snapshotting, MigrationStage.RolledBack) => true,   // 基线未完成的中止出口（不迁移任何变更）
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
                if (DeleteRecordedAdditions(m, rehearsalRoot) > 0)
                    return MigrationResult.Fail("rehearsal_cleanup_incomplete", m.Stage);
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
            if (_requireQuiescence && (!_quietValid || _quiet is null || m.QuiescedAtUtc is null
                || !string.Equals(m.QuiesceSessionId, _sessionId, StringComparison.Ordinal)
                || m.QuiesceGeneration != _quietGeneration))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 须**实际存续**且同代次的窗口
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
            if (!m.BaselineCompleted)
            {
                // 基线未完成（快照发布前失败/中止）⇒ **安全中止**：清理未完成快照、置 RolledBack，使新事务可开启。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (!IsLegalAdvance(m.Stage, MigrationStage.RollingBack))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->RollingBack", m.Stage);

            // **先持久化封锁**（落 RollingBack 并清标记/演练资格）——此后即不可生产执行；
            // 再做窗口/快照校验与恢复，失败一律 Blocked（失败处理产出仍可加载）。
            m.Stage = MigrationStage.RollingBack;
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            m.CommitMarker = null;
            WriteManifest(m);
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("rollback_snapshot_invalid:" + bad);
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
                if (DeleteRecordedAdditions(m, _configRoot) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
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
            if (m.Stage == MigrationStage.Snapshotting || (m.Stage == MigrationStage.Blocked && !m.BaselineCompleted))
            {
                // **基线尚未完成**（且尚未发生任何迁移变更）⇒ 安全中止：清理未完成快照、置 RolledBack，使新事务可开启。
                // **绝不**把部分快照用于恢复（不调用 VerifySnapshot/CompleteRollback）。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("recover_snapshot_invalid:" + bad);

            if (m.Stage != MigrationStage.RollingBack)
            {
                m.Stage = MigrationStage.RollingBack;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.CommitMarker = null;
                WriteManifest(m);
            }
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 恢复亦须有效窗口
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

        List<string> onDisk;
        try { onDisk = EnumerateFilesSafe(m.SnapshotPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "snapshot_enumeration_failed:" + ex.GetType().Name;      // 读端链接/IO 失败 ⇒ 验证失败（不继续读）
        }
        foreach (var extra in onDisk.Where(f => !m.FileHashes.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;
        foreach (var p in m.FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!IsSafeTarget(m.SnapshotPath, p.Key)) return "snapshot_unsafe_target:" + p.Key;   // **读端**同样拒绝链接逃逸
            var target = Path.Combine(m.SnapshotPath, NormalizePath(p.Key).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;
            string hash;
            try { hash = Sha256Hex(File.ReadAllBytes(target)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return "snapshot_read_failed:" + p.Key;
            }
            if (!string.Equals(hash, p.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + p.Key;
        }
        return "";
    }

    /// <summary>全字段完整性摘要（仅完整性，不提供来源认证）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.SnapshotId).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append(m.BaselineCompleted ? '1' : '0').Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
        foreach (var c in (m.ChangedFiles ?? []).OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>结构与状态不变量校验（先于摘要校验；null/非法枚举/非法组合一律拒绝）。</summary>
    public bool IsManifestIntegrityValid(MigrationManifest m)
    {
        try { return IsManifestIntegrityValidCore(m); }
        catch (Exception) { return false; }        // 路径解析等异常 ⇒ **稳定转为验证失败**（不向上抛）
    }

    private bool IsManifestIntegrityValidCore(MigrationManifest m)
    {
        if (m is null) return false;
        if (m.SchemaVersion != 1) return false;
        if (!IsSafeTransactionId(m.TransactionId)) return false;
        if (!string.Equals(m.ConfigRoot, _configRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(m.SnapshotPath) || string.IsNullOrWhiteSpace(m.RollbackEntry)) return false;  // 先验空值，避免 GetFullPath 抛异常
        if (m.SnapshotId is not { Length: 32 }) return false;                                                       // 快照身份必填且格式固定
        foreach (var ch in m.SnapshotId) if (!Uri.IsHexDigit(ch)) return false;                                     // 仅 32 位十六进制（无分隔符/..）
        var expectedSnapshot = SnapshotPathOf(m.TransactionId, m.SnapshotId);
        if (!string.Equals(Path.GetFullPath(m.SnapshotPath ?? ""), Path.GetFullPath(expectedSnapshot), StringComparison.OrdinalIgnoreCase))
            return false;                                                                                            // **精确**绑定本事务快照（非前缀判定）
        if (!IsWithin(m.SnapshotPath, _transactionRoot)) return false;
        if (!Enum.IsDefined(m.Stage)) return false;
        if (m.FileHashes is null || m.ChangedFiles is null) return false;
        foreach (var k in m.FileHashes.Keys) if (!IsSafeRelativePath(k)) return false;
        foreach (var c in m.ChangedFiles)
        {
            if (c is null || !IsSafeRelativePath(c.Path) || !Enum.IsDefined(c.Kind)) return false;
            var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, c.Path, out _);
            if (c.Kind == ChangeKind.Added && inSnapshot) return false;
            if (c.Kind is ChangeKind.Modified or ChangeKind.Deleted && !inSnapshot) return false;
        }
        if (m.Stage == MigrationStage.Committed)
        {
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(m.BlockedReason)) return false;          // 已提交不得带 blocked
            if (!m.BaselineCompleted) return false;                            // 已提交 ⇒ 基线必已建立
        }
        else if (!string.IsNullOrEmpty(m.CommitMarker)) return false;          // 非提交态不得带标记（含 Blocked）
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
        m.CommitMarker = null;                 // 与校验规则一致：Blocked 态不得带提交标记（失败处理产出仍可加载）
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

    private void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            if (!IsSafeTarget(targetRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                throw new InvalidOperationException("unsafe_target:" + rel);   // 恢复目标与快照源都须安全
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
            if (string.Equals(Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                    _configRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                _fileRestoredHook?.Invoke(rel);   // 恢复中断注入点（**仅真实配置根**；演练副本不触发）
        }
    }

    /// <summary>
    /// 只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。返回**失败条数**——
    /// 不安全目标或删除失败**不得静默跳过**：调用方据此保持阻断（不得报告完整回滚）。
    /// </summary>
    private static int DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
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
    }

    /// <summary>释放实际窗口并**使资格失效**（代次前进 ⇒ 历史时间戳/会话不可复用）。</summary>
    private void ReleaseQuiescence()
    {
        try { _quiet?.Dispose(); } catch { }
        _quiet = null;
        _quietValid = false;
        _quietGeneration++;
    }

    /// <summary>确保存在**有效（存续）**静止窗口：已有效则通过；否则重新取得并持久化会话/时刻/代次。</summary>
    private bool EnsureQuiescence(MigrationManifest m)
    {
        if (!_requireQuiescence) return true;
        if (!_quietValid || _quiet is null)
        {
            _quiet = _quiesce?.Invoke();
            if (_quiet is null) return false;
            _quietValid = true;
            _quietGeneration++;
        }
        m.QuiescedAtUtc = _utcNow();
        m.QuiesceSessionId = _sessionId;
        m.QuiesceGeneration = _quietGeneration;
        WriteManifest(m);
        return true;
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    /// <summary>安全枚举（**先拒绝重解析点再进入子目录**）；用于快照验证读端。</summary>
    private static List<string> EnumerateFilesSafe(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("snapshot_dir_reparse_point:" + dir);
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_dir_reparse_point:" + sub);
                pending.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_file_reparse_point:" + f);
                result.Add(Rel(f, root));
            }
        }
        return result;
    }
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