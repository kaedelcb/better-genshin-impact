using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>事务阶段（**持久化**；除 `Committed` 外一律不可生产执行）。</summary>
public enum MigrationStage
{
    None = 0,
    Snapshotting = 1,
    SnapshotReady = 2,
    ReferenceUpdating = 3,
    Activated = 4,
    Committed = 5,
    RolledBack = 6,
    Blocked = 7,
}

/// <summary>变更归属（回滚据此判定「本事务新增」——**不再删除未登记文件**）。</summary>
public enum ChangeKind { Added = 0, Modified = 1, Deleted = 2 }

/// <summary>一条变更记录（相对路径 + 归属）。</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public ChangeKind Kind { get; set; }
}

/// <summary>
/// 迁移 manifest（**全字段完整性**：`ManifestIntegrity` 覆盖除自身外全部字段，含阶段/提交标记/演练结果）。
/// </summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>本事务的**变更归属**（有记录才按归属回滚；无记录一律**不删除**任何文件）。</summary>
    [JsonPropertyName("changedFiles")] public List<ChangeRecord> ChangedFiles { get; set; } = [];
    [JsonPropertyName("snapshotManifestHash")] public string SnapshotManifestHash { get; set; } = "";
    [JsonPropertyName("stage")] public MigrationStage Stage { get; set; }
    /// <summary>唯一提交标记；**提交前为空**。</summary>
    [JsonPropertyName("commitMarker")] public string? CommitMarker { get; set; }
    [JsonPropertyName("rollbackRehearsed")] public bool RollbackRehearsed { get; set; }
    /// <summary>演练范围（绑定「快照清单 + 变更归属」；与当前不符 ⇒ 演练资格失效）。</summary>
    [JsonPropertyName("rehearsalScope")] public string? RehearsalScope { get; set; }
    /// <summary>结构化 blocked 原因（非空 ⇒ 禁止提交且不可生产执行）。</summary>
    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }
    /// <summary>写入静止窗口取得时刻（**null ⇒ 未取得静止窗口** ⇒ 严格模式拒绝提交）。</summary>
    [JsonPropertyName("quiescedAtUtc")] public DateTimeOffset? QuiescedAtUtc { get; set; }
    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
}

/// <summary>事务操作结果。</summary>
public sealed record MigrationResult(bool Success, string Reason, MigrationStage Stage)
{
    public static MigrationResult Ok(MigrationStage s) => new(true, "", s);
    public static MigrationResult Fail(string reason, MigrationStage s) => new(false, reason, s);
}

/// <summary>
/// **R5.6 事务迁移切换（v2：按第 1 轮会诊 8 项必改重构）**。
/// 不变量：①**事务串行边界**＝事务根独占锁（`migration.lock`，`FileShare.None`）全程持有；
/// ②**静止窗口**＝调用方提供 `quiesce` 委托，严格模式下未取得静止窗口**拒绝提交**；
/// ③**合法阶段转换**（见 <see cref="IsLegalAdvance"/>），跳步提交被拒；
/// ④**回滚资格随范围失效**（演练范围＝快照清单+变更归属哈希，回滚/变更后作废）；
/// ⑤**回滚只按变更归属**（无记录⇒不删任何文件）；
/// ⑥**manifest 全字段完整性**校验，闸门先校验再判定；
/// ⑦**未提交绝不生产执行**——生产入口须经 <see cref="TryRunProduction"/>（唯一强制检查点）。
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
    private FileStream? _lock;
    private string _transactionId = "";

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

    /// <summary>根关系校验：事务根**不得**位于配置根内（避免自包含快照/回滚删事务资料），也不得包含配置根。</summary>
    private void ValidateRoots()
    {
        var cfg = _configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tx = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (tx.StartsWith(cfg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务根不得位于配置根内（自包含快照会污染备份）。");
        if (cfg.StartsWith(tx, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根不得位于事务根内。");
    }

    /// <summary>相对路径安全校验：拒绝绝对路径、盘符、`..` 段与目录分隔逃逸（写入/删除前一律先校验）。</summary>
    internal static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        var norm = rel.Replace('\\', '/');
        if (norm.StartsWith('/') || norm.Contains(':')) return false;
        foreach (var seg in norm.Split('/'))
            if (seg == ".." || seg == "." || seg.Length == 0) return false;
        return true;
    }

    /// <summary>启动事务：取得**独占锁**并**先持久化** `Snapshotting`（提交标记为空、无演练资格）。</summary>
    public MigrationResult BeginTransaction(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
        if (HoldsExclusiveLock) return MigrationResult.Fail("already_begun", LoadManifest()?.Stage ?? MigrationStage.None);
        Directory.CreateDirectory(_transactionRoot);
        try
        {
            _lock = new FileStream(Path.Combine(_transactionRoot, "migration.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return MigrationResult.Fail("transaction_busy", MigrationStage.None); // 跨实例/跨进程排他
        }
        _transactionId = transactionId;
        var manifest = new MigrationManifest
        {
            TransactionId = transactionId,
            CreatedAtUtc = _utcNow(),
            ConfigRoot = _configRoot,
            SnapshotPath = Path.Combine(_transactionRoot, "snapshot-" + transactionId),
            RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
            Stage = MigrationStage.Snapshotting,
            CommitMarker = null,
            RollbackRehearsed = false,
        };
        WriteManifest(manifest);
        return MigrationResult.Ok(MigrationStage.Snapshotting);
    }

    /// <summary>步骤①②：取得静止窗口 → 全量快照（字节+SHA256）→ 写清单哈希与快照路径 → 阶段 `SnapshotReady`。</summary>
    public MigrationResult TakeSnapshot()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage != MigrationStage.Snapshotting) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
        if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

        using var quiet = _quiesce?.Invoke();     // 写入静止窗口（等效一致性机制；调用方负责覆盖所有写方）
        m.QuiescedAtUtc = quiet is null ? null : _utcNow();

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in EnumerateFiles(_configRoot))
            {
                var rel = Rel(file, _configRoot);
                if (!IsSafeRelativePath(rel)) return MigrationResult.Fail("unsafe_path:" + rel, m.Stage);
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
        return MigrationResult.Ok(MigrationStage.SnapshotReady);
    }

    /// <summary>快照清单哈希（相对路径 + 内容哈希，排序后取 SHA256）。</summary>
    public static string ComputeSnapshotManifestHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        var sb = new StringBuilder();
        foreach (var p in fileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(p.Key).Append(':').Append(p.Value).Append('\n');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>记录**变更归属**（回滚据此判定新增/修改/删除；无记录⇒回滚不删除任何文件）。</summary>
    public MigrationResult RecordChanges(IEnumerable<ChangeRecord> changes)
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
            return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
        foreach (var c in changes)
        {
            if (!IsSafeRelativePath(c.Path)) return MigrationResult.Fail("unsafe_path:" + c.Path, m.Stage);
            m.ChangedFiles.RemoveAll(x => string.Equals(x.Path, c.Path, StringComparison.Ordinal));
            m.ChangedFiles.Add(new ChangeRecord { Path = c.Path, Kind = c.Kind });
        }
        // 变更归属变化 ⇒ 既有演练资格失效（演练范围绑定变更集合）
        m.RollbackRehearsed = false;
        m.RehearsalScope = null;
        WriteManifest(m);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>步骤③前半：引用更新完成（阶段 `ReferenceUpdating`）。</summary>
    public MigrationResult MarkReferenceUpdateCompleted()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (!IsLegalAdvance(m.Stage, MigrationStage.ReferenceUpdating))
            return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + MigrationStage.ReferenceUpdating, m.Stage);
        m.Stage = MigrationStage.ReferenceUpdating;
        WriteManifest(m);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>步骤③后半：激活完成（`candidate → active`；阶段 `Activated`）——**仍未提交**。</summary>
    public MigrationResult MarkActivated()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (!IsLegalAdvance(m.Stage, MigrationStage.Activated))
            return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + MigrationStage.Activated, m.Stage);
        m.Stage = MigrationStage.Activated;
        WriteManifest(m);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>合法阶段转换（**跳步提交被拒**）：`Snapshotting→SnapshotReady→ReferenceUpdating→Activated→Committed`；任意中间态→`Blocked`；`Activated/Blocked→RollingBack`。</summary>
    public static bool IsLegalAdvance(MigrationStage from, MigrationStage to) => (from, to) switch
    {
        (MigrationStage.Snapshotting, MigrationStage.SnapshotReady) => true,
        (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) => true,
        (MigrationStage.ReferenceUpdating, MigrationStage.Activated) => true,
        (MigrationStage.Activated, MigrationStage.Committed) => true,
        (_, MigrationStage.Blocked) => from is not (MigrationStage.Committed or MigrationStage.RolledBack),
        // 回滚允许自「快照就绪/引用更新/已激活/已提交/阻塞」发起：**已提交后仍须可回滚**（I1：恢复旧文件+清新增+撤销激活）。
        (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated
            or MigrationStage.Committed or MigrationStage.Blocked, MigrationStage.RolledBack) => true,
        _ => false,
    };

    /// <summary>
    /// 步骤④：**回滚演练**——在隔离副本上先施加「本次变更归属」代表的变更，再**复用实际回滚核心**回滚，
    /// 校验逐字节恢复 + 新增文件被清理；通过则记录 `RehearsalScope`（绑定快照清单+变更归属）。
    /// </summary>
    public MigrationResult RehearseRollback()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        var rehearsalRoot = Path.Combine(_transactionRoot, "rehearsal");
        try
        {
            if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true);
            Directory.CreateDirectory(rehearsalRoot);
            RestoreFromSnapshot(m, rehearsalRoot);                       // 副本＝快照
            ApplyRepresentativeChanges(m, rehearsalRoot);                // 施加本次变更（新增/修改/删除）
            RestoreFromSnapshot(m, rehearsalRoot);                       // **复用实际回滚核心**
            DeleteRecordedAdditions(m, rehearsalRoot);                   // 同一删除归属逻辑
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

    /// <summary>
    /// **提交**：前置＝阶段 `Activated`、**演练范围与当前一致**、**无 blocked**、严格模式须**已取得静止窗口**、
    /// 快照有效 ⇒ 写唯一提交标记。任一前置不满足 ⇒ 结构化拒绝（**不写标记**）。
    /// </summary>
    public MigrationResult Commit()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage != MigrationStage.Activated)
            return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
        if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
        if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
            return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
        if (_requireQuiescence && m.QuiescedAtUtc is null)
            return MigrationResult.Fail("no_quiescence_window", m.Stage);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        m.CommitMarker = m.TransactionId;
        m.Stage = MigrationStage.Committed;
        WriteManifest(m);
        return MigrationResult.Ok(MigrationStage.Committed);
    }

    /// <summary>
    /// **回滚**：恢复快照内旧字节（含被删文件）→ **只删除「本事务新增」**（按变更归属；无记录⇒不删）→
    /// 撤销激活（清提交标记）→ 置 `RolledBack` 并**作废演练资格**。
    /// </summary>
    public MigrationResult Rollback()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (!IsLegalAdvance(m.Stage, MigrationStage.RolledBack))
            return MigrationResult.Fail("illegal_advance:" + m.Stage + "->RolledBack", m.Stage);
        if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

        m.Stage = MigrationStage.RolledBack;   // 先落盘：进入回滚即失去提交资格
        m.RollbackRehearsed = false;
        m.RehearsalScope = null;
        m.CommitMarker = null;
        WriteManifest(m);
        try
        {
            RestoreFromSnapshot(m, _configRoot);
            DeleteRecordedAdditions(m, _configRoot);
        }
        catch (IOException ex)
        {
            return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
        }
        WriteManifest(m);
        return MigrationResult.Ok(MigrationStage.RolledBack);
    }

    /// <summary>
    /// **重启恢复入口**：按持久化阶段收敛——`Committed`（标记＝事务号）保持**完整新态**并可执行；
    /// 其余中间态（`Snapshotting`/`SnapshotReady`/`ReferenceUpdating`/`Activated`/`RolledBack`/`Blocked`）
    /// 收敛为**完整旧态**（回滚）或标记 `Blocked`（快照不可用时）。
    /// </summary>
    public MigrationResult RecoverOnStart()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage == MigrationStage.Committed && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
            return MigrationResult.Ok(MigrationStage.Committed);   // 完整新态（已提交）
        if (m.Stage is MigrationStage.None) return MigrationResult.Fail("illegal_stage:None", m.Stage);
        if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("recover_snapshot_invalid:" + bad);
        return Rollback();
    }

    /// <summary>**强制闸门**：生产执行前必须调用；仅当完整性校验通过、已提交、标记匹配且无 blocked 时为真。</summary>
    public MigrationResult AuthorizeProductionExecution()
    {
        var m = LoadValidated();
        if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
        if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
        if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
            return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
        if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>**唯一生产执行检查点**：未获授权则**不执行**（`action` 调用次数＝0）并返回结构化拒绝。</summary>
    public MigrationResult TryRunProduction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var auth = AuthorizeProductionExecution();
        if (!auth.Success) return auth;
        action();
        return MigrationResult.Ok(MigrationStage.Committed);
    }

    /// <summary>快照完整性：清单哈希自洽 + 快照文件齐全且哈希一致 + **无未登记多余文件**。</summary>
    public string VerifySnapshot()
    {
        var m = LoadManifest();
        if (m is null) return "manifest_missing_or_corrupt";
        if (!IsManifestIntegrityValid(m)) return "manifest_integrity_mismatch";
        if (!string.Equals(m.ConfigRoot, _configRoot, StringComparison.OrdinalIgnoreCase)) return "config_root_mismatch";
        if (!string.Equals(m.SnapshotManifestHash, ComputeSnapshotManifestHash(m.FileHashes), StringComparison.Ordinal))
            return "snapshot_manifest_hash_mismatch";
        if (!Directory.Exists(m.SnapshotPath)) return "snapshot_missing";
        foreach (var key in m.FileHashes.Keys)
            if (!IsSafeRelativePath(key)) return "unsafe_snapshot_path:" + key;

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

    /// <summary>manifest 全字段完整性（覆盖阶段/提交标记/演练范围/静止窗口/变更归属等，防单字段改写）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        foreach (var c in m.ChangedFiles.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes));
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>结构+完整性校验（闸门与所有变更前**先校验**）。</summary>
    public bool IsManifestIntegrityValid(MigrationManifest m)
        => m.SchemaVersion == 1
           && !string.IsNullOrWhiteSpace(m.TransactionId)
           && !string.IsNullOrWhiteSpace(m.ConfigRoot)
           && !string.IsNullOrWhiteSpace(m.SnapshotPath)
           && IsSafeRelativePath("probe")         // 常量自检（保证校验函数本身可用）
           && string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);

    /// <summary>读取并按**全字段完整性**校验的 manifest（不合格＝null，调用方一律拒绝）。</summary>
    public MigrationManifest? LoadValidated()
    {
        var m = LoadManifest();
        return m is not null && IsManifestIntegrityValid(m) ? m : null;
    }

    /// <summary>读取 manifest（原样；不校验——供诊断/夹具查看，业务判定请用 <see cref="LoadValidated"/>）。</summary>
    public MigrationManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>演练用「代表本次变更」的模拟（只作用于**演练副本**；真实目录不参与）。</summary>
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
        _stageHook?.Invoke(manifest.Stage);                       // 夹具接缝：写前崩溃注入
        manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);   // 全字段完整性
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true);            // 同目录原子替换
        _stageHook?.Invoke(manifest.Stage);                       // 夹具接缝：写后崩溃注入
    }

    private static void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
        }
    }

    /// <summary>只删除**变更归属记录为「本事务新增」**的文件（无记录⇒不删任何文件）。</summary>
    private static void DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeRelativePath(c.Path)) continue;
            var target = Path.Combine(targetRoot, c.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) File.Delete(target);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
    private static string Rel(string file, string root) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}