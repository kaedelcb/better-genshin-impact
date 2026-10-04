using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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

public sealed record MigrationLegacyBaselineObservation(string LegacySourceSha256, string SnapshotManifestHash,
    MigrationControlledRootWitness CurrentObservation, bool MutationPerformed = false,
    string Qualification = "legacy-exact-bytes-only-no-historical-identity");

public sealed record MigrationJournalBinding(string RelativePath, string JournalBytesSha256,
    string? DeclarationSha256, string ResolutionChainDigest, string AppliedChainDigest,
    MigrationStage LastStableStage, MigrationStage? PendingStage);

/// <summary>迁移 manifest（**全字段完整性 + 结构与状态不变量**）。</summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 2;
    [JsonPropertyName("legacySource")] public MigrationLegacySource? LegacySource { get; set; }
    [JsonPropertyName("legacyBaselineObservation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MigrationLegacyBaselineObservation? LegacyBaselineObservation { get; set; }
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    /// <summary>不可变快照身份（＝开事务时的会话标识）；快照路径**精确**绑定本事务，不用前缀判定。</summary>
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("baselineVersions")] public Dictionary<string, MigrationFileVersion> BaselineVersions { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("controlledBaseline")] public MigrationControlledRootWitness? ControlledBaseline { get; set; }
    [JsonPropertyName("controlledLatest")] public MigrationControlledRootWitness? ControlledLatest { get; set; }
    [JsonPropertyName("journalBinding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MigrationJournalBinding? JournalBinding { get; set; }
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
    /// <summary>
    /// **真实引用写入的读回证据**（本事务写集声明；`path → 写入后盘上字节 SHA256`；大小写不敏感身份）。
    /// 只有**逐项读回确认**后才写入；空表示尚未完成真实引用更新。
    /// </summary>
    [JsonPropertyName("referenceWriteSet")] public Dictionary<string, string> ReferenceWriteSet { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
    /// </summary>
    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }
    /// <summary>
    /// **本事务是否由真实副作用端口建立**（`BeginTransaction` 时按实例是否注入端口写入；受完整性摘要覆盖）。
    /// `true` ⇒ 阶段 `ReferenceUpdating/Activated/Committed` 必须携带真实写集/激活读回证据，且阶段标记只能由
    /// 真实副作用 + 读回确认推进；入口回绝 `MarkReferenceUpdateCompleted`/`MarkActivated`。
    /// `false` ⇒ 仅限未注入端口的只读夹具（旧行为保留）。
    /// </summary>
    [JsonPropertyName("realEffectsRequired")] public bool RealEffectsRequired { get; set; }
}

/// <summary>真实激活读回证据（D13：`candidate → active`）。</summary>
public sealed class MigrationActivationRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("beforeStatus")] public string BeforeStatus { get; set; } = "";
    [JsonPropertyName("afterStatus")] public string AfterStatus { get; set; } = "";
    [JsonPropertyName("afterHash")] public string AfterHash { get; set; } = "";
    /// <summary>
    /// **撤销激活后的盘上字节哈希**（回滚已经执行撤销时持久化）。恢复路径重入时据此判定「当前版本是本事务自己写的」，
    /// 而不是把它误判为他方漂移（会诊第 3 轮 MUST-1：归属证据必须**持久化**，不能只存在于内存）。
    /// </summary>
    [JsonPropertyName("revertedHash")] public string? RevertedHash { get; set; }
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
    private readonly IMigrationEffectService? _effects;
    private readonly Action<string>? _referenceWriteFault;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileStream? _lock;
    /// <summary>**重入守卫**：外部副作用/读回回调执行期间置位；此期间任何变更入口一律拒绝（monitor 可重入，
    /// 单靠 `lock` 不能阻止回调同步重入事务，见会诊第 2 轮 IMPORTANT-7）。</summary>
    private volatile bool _effectCallInProgress;
    private static readonly AsyncLocal<MigrationSwitchTransaction?> CallbackOwner = new();
    private readonly Stack<(MigrationSwitchTransaction? Owner, bool Active)> _callbackScopes = [];
    /// <summary>回调期间被请求的 Dispose：等回调返回后再真正释放（避免锁/窗口在回调用途中被撤）。</summary>
    private bool _disposeDeferred;
    /// <summary>**操作级深度**：公开入口进入时 +1、退出时 -1；回调期间的 Dispose 请求只在深度归零后兑现，
    /// 避免「回调结束即释放锁/窗口，而外层操作仍在继续写入」的保护缺口（会诊第 4 轮 IMPORTANT-4）。</summary>
    private int _operationDepth;
    private int _disposeRequested;
    private MigrationRootAuthority? _rootAuthority;
    private MigrationMainOwnership? _mainOwnership;
    private bool _mainAdmissionAttempted;
    private IDisposable? _quiet;
    private bool _quietValid;
    private int _quietGeneration;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null, IMigrationEffectService? effectService = null,
        Action<string>? referenceWriteFault = null)
    {
        _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
        _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _quiesce = quiesce;
        _requireQuiescence = requireQuiescence;
        _stageHook = stageHook;
        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
        _effects = effectService;               // **真实副作用端口**：null ⇒ 本实例只能演练阶段推进（旧语义仅保留给只读夹具）
        _referenceWriteFault = referenceWriteFault;
        ValidateRoots();
    }

    /// <summary>权威 D13 激活词（R5.2 §21.2：`candidate → active`）；仅本事务的激活入口使用，不作通用状态改写器。</summary>
    internal const string CandidateReadyStatus = "candidate-ready";
    internal const string ActiveStatus = "active";

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
        // **路径命名空间门禁（第 15 轮会诊）**：`\\?\C:\data` 与 `C:\data` 指向同一目录却字符串前缀不匹配，
        // 可绕过「根互不包含」。此处**明确拒绝尚不支持的扩展/设备前缀**，并要求两根同属一种命名空间。
        foreach (var chain in new[] { _configRoot, _transactionRoot })
        {
            if (chain.StartsWith(@"\\?\", StringComparison.Ordinal) || chain.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidOperationException("不支持扩展/设备路径前缀（\\\\?\\、\\\\.\\）：无法保证与普通路径的目录身份一致，拒绝迁移事务：" + chain);
        }
        // **同卷约束（第 16 轮会诊）**：`SUBST X: C:\data` / 映射盘会让两个不同盘符指向同一目录，字符串互不包含且
        // 逐段名称检查也通过 ⇒ 无法证明隔离。处置＝要求两根**同卷**（不同卷直接拒绝；同卷别名由 reparse point 检查拦截）。
        var cfgVolume = (Path.GetPathRoot(_configRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        var txVolume = (Path.GetPathRoot(_transactionRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(cfgVolume, txVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根与事务根必须位于**同一卷**（不同卷可能由 SUBST/映射盘指向同一目录，目录身份不可比较），拒绝迁移事务。");

        var cfgIsUnc = _configRoot.StartsWith(@"\\", StringComparison.Ordinal);
        var txIsUnc = _transactionRoot.StartsWith(@"\\", StringComparison.Ordinal);
        if (cfgIsUnc != txIsUnc)
            throw new InvalidOperationException("配置根与事务根必须同属一种路径命名空间（均为盘符路径或均为 UNC），拒绝迁移事务。");

        var cfg = _configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tx = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (tx.StartsWith(cfg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务根不得位于配置根内（自包含快照会污染备份）。");
        if (cfg.StartsWith(tx, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根不得位于事务根内。");
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (HasReparsePoint(chain))
                throw new InvalidOperationException("根路径链上存在重解析点（junction/符号链接），拒绝迁移事务：" + chain);
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (!IsCanonicalAbsolutePath(chain, out var badSegment))
                throw new InvalidOperationException("根路径存在非规范名（如 NTFS 8.3 短名别名），拒绝迁移事务：" + badSegment
                    + "（根：" + chain + "）");
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

    /// <summary>
    /// **规范化名称校验**（Windows 短名/别名防护）：对已存在的每一段，要求该段名称与其父目录**枚举名**大小写不敏感匹配；
    /// 若路径在磁盘上存在、却**未被父目录枚举名匹配**（例：NTFS 8.3 短名别名）⇒ 视为**非规范名**拒绝。
    /// 尚未存在的新路径（新增文件/新目录）不受此限（新名不可能是既有别名）。
    /// </summary>
    internal static bool IsCanonicalExistingPath(string root, string? rel, out string notCanonicalAt)
    {
        notCanonicalAt = "";
        var segments = NormalizePath(rel).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = EnsureTrailingSeparator(root);   // 保留分隔符：`C:\` + seg 必须仍是**完全限定**路径
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            var exists = File.Exists(candidate) || Directory.Exists(candidate);
            if (!exists) return true;                        // 从这里起都是新路径 ⇒ 无别名风险
            var parentEntries = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName).Where(n => n is not null).ToList();
            if (!parentEntries.Any(n => string.Equals(n, segments[i], StringComparison.OrdinalIgnoreCase)))
            {
                notCanonicalAt = string.Join('/', segments.Take(i + 1));   // 存在但非枚举名 ⇒ 别名（如 8.3 短名）
                return false;
            }
            current = candidate;
        }
        return true;
    }

    /// <summary>规范化并**保留尾分隔符**（避免 `C:\` 被裁剪成 `C:`；盘符相对路径会随当前目录漂移）。</summary>
    internal static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
            ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>绝对路径版规范名校验（从盘符/UNC 根逐段枚举比对；用于根路径别名防护）。</summary>
    internal static bool IsCanonicalAbsolutePath(string fullPath, out string notCanonicalAt)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(full) ?? "";
        var rel = full.Length > root.Length ? full[root.Length..] : "";
        if (rel.Length == 0) { notCanonicalAt = ""; return true; }
        return IsCanonicalExistingPath(root, rel, out notCanonicalAt);
    }

    /// <summary>路径前缀判定（a 为 b 的祖先目录）：用于**文件/目录拓扑互换**的登记拒绝。</summary>
    internal static bool IsAncestorPath(string? a, string? b)
    {
        var ka = PathKey(a);
        var kb = PathKey(b);
        if (ka.Length == 0 || kb.Length == 0 || ka == kb) return false;
        return kb.StartsWith(ka + "/", StringComparison.Ordinal);
    }

    /// <summary>目标路径安全性：规范化后必须仍在 root 内，且**父目录链上无 reparse point**（逐段链接拒绝）。</summary>
    internal static bool IsSafeTarget(string root, string rel)
    {
        if (!IsSafeRelativePath(rel)) return false;
        var rootFull = EnsureTrailingSeparator(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            return false;                                                                         // **目标文件本身**是链接 ⇒ 拒绝
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;   // 父链任一段链接 ⇒ 拒绝
            if (string.Equals(EnsureTrailingSeparator(dir.FullName), rootFull, StringComparison.OrdinalIgnoreCase)) break;
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
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (HoldsExclusiveLock) return MigrationResult.Ok(MigrationStage.None);
            try
            {
                var facts = ClassifyPersistentProtocol();
                if (facts.Kind is PersistentProtocolKind.Unknown or PersistentProtocolKind.UnverifiableModern)
                    return MigrationResult.Fail(facts.Reason ?? "migration_persistent_protocol_unverified", MigrationStage.None);
                if (_effects is IPreparedMigrationEffectService && facts.Kind == PersistentProtocolKind.Diagnostic)
                    return MigrationResult.Fail("migration_diagnostic_modern_conversion_unverified", MigrationStage.None);
                var metadataOnly = _effects is not IPreparedMigrationEffectService &&
                    facts.Kind is PersistentProtocolKind.Modern or PersistentProtocolKind.ReservedModern;
                if (facts.Kind == PersistentProtocolKind.Fresh) Directory.CreateDirectory(_transactionRoot);
                _lock = new FileStream(Path.Combine(_transactionRoot, "migration.lock"),
                    metadataOnly ? FileMode.Open : FileMode.OpenOrCreate,
                    metadataOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None);
                if (_effects is IPreparedMigrationEffectService)
                {
                    var legacyImport = facts.Kind == PersistentProtocolKind.VerifiedLegacy;
                    _rootAuthority = new MigrationRootAuthority(_configRoot, _transactionRoot, legacyImport);
                }
                var acquired = ClassifyPersistentProtocol();
                if (acquired.Kind is PersistentProtocolKind.Unknown or PersistentProtocolKind.UnverifiableModern)
                    throw new MigrationAdmissionException(acquired.Reason ?? "migration_acquired_protocol_unverified");
            }
            catch (IOException)
            {
                _lock?.Dispose(); _lock = null;
                _rootAuthority?.Dispose(); _rootAuthority = null;
                return MigrationResult.Fail("transaction_busy", MigrationStage.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                _lock?.Dispose(); _lock = null;
                _rootAuthority?.Dispose(); _rootAuthority = null;
                return MigrationResult.Fail("root_authority_unavailable:" + ex.GetType().Name, MigrationStage.None);
            }
            return MigrationResult.Ok(MigrationStage.None);
        }
    }

    /// <summary>开启新事务：须持锁；有未决事务或事务号占用 ⇒ 拒绝；先持久化 Snapshotting 并取得全程静止窗口。</summary>
    public MigrationResult BeginTransaction(string transactionId)
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!IsSafeTransactionId(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
            var openingFacts = ClassifyPersistentProtocol();
            if (openingFacts.History.Contains(transactionId)) return MigrationResult.Fail("transaction_id_in_use", MigrationStage.None);
            if (openingFacts.MainInput?.Version.Exists == true && openingFacts.Reason == "migration_protocol_main_invalid")
                return MigrationResult.Fail("pending_manifest_corrupt", MigrationStage.None);
            if (!HoldsExclusiveLock)
            {
                var acq = TryAcquireExclusive();
                if (!acq.Success) return acq;
            }

            var beginFacts = ClassifyPersistentProtocol();
            AdmitPersistentMutation(nameof(BeginTransaction), beginFacts, allowAbsent: true);
            var admittedMain = _rootAuthority is not null ? AdmitOrCheckMain(true) : null;
            if (beginFacts.Kind == PersistentProtocolKind.Modern && beginFacts.Main is { } terminal)
                QualifyModernSuccess(terminal, terminal.Stage, SnapshotOwnedMain(), beginFacts, ModernSuccessPurpose.TerminalAdmission);
            if (_rootAuthority?.PendingTransactionId is { } pendingIdentity)
                return MigrationResult.Fail("pending_authority_transaction_exists:" + pendingIdentity, MigrationStage.None);
            if (File.Exists(ManifestPath))
            {
                var validated = admittedMain is null ? LoadValidated() : DecodeValidatedMain(admittedMain.Bytes);
                if (validated is { Stage: MigrationStage.RolledBack, BaselineCompleted: false } &&
                    _effects is IPreparedMigrationEffectService && !VerifyNoBaselineAbort(validated, true))
                    return MigrationResult.Fail("migration_abort_terminal_unverified", validated.Stage);
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

            _quiet = _quiesce is null ? null : InvokeExternal(_quiesce);
            _quietValid = _quiet is not null;
            _quietGeneration++;
            var manifest = new MigrationManifest
            {
                TransactionId = transactionId,
                CreatedAtUtc = InvokeExternal(_utcNow),
                ConfigRoot = _configRoot,
                SnapshotPath = snapshotPath,
                SnapshotId = _sessionId,
                RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
                Stage = MigrationStage.Snapshotting,
                CommitMarker = null,
                RollbackRehearsed = false,
                QuiescedAtUtc = _quiet is null ? null : InvokeExternal(_utcNow),
                QuiesceSessionId = _quiet is null ? null : _sessionId,
                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
                RealEffectsRequired = _effects is not null,
            };
            // **先持久化占号、再发布 manifest**（占号失败/崩溃也保守占号 ⇒ 事务号不复用；不依赖窗口是否存在）。
            WriteManifest(manifest, () => File.AppendAllText(HistoryPath, transactionId + Environment.NewLine));
            return MigrationResult.Ok(MigrationStage.Snapshotting);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }
    /// <summary>步骤①②：全量快照（字节+SHA256）→ 清单哈希与快照路径落盘 → SnapshotReady。</summary>
    public MigrationResult TakeSnapshot()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Snapshotting) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 采集期须有**存续**窗口（重开实例不得续用旧资格）
            if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var versions = new Dictionary<string, MigrationFileVersion>(StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(m.SnapshotPath);   // **空配置根也须建立可验证快照目录**（否则基线完成后 VerifySnapshot 报 snapshot_missing）
                var store = _effects is IPreparedMigrationEffectService ? new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot) : null;
                foreach (var file in EnumerateFiles(_configRoot))
                {
                    var rel = Rel(file, _configRoot);
                    if (!IsSafeTarget(_configRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                        return MarkBlocked("unsafe_path:" + rel);      // 读端与写端都须安全（含目标文件本身与父链链接）
                    var input = store?.ReadInput(rel);
                    var bytes = input?.Bytes ?? File.ReadAllBytes(file);
                    if (input is not null) versions[rel] = input.Version;
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
            m.BaselineVersions = versions;
            if (_rootAuthority is not null)
            {
                var witness = MigrationControlledRootCapture.Capture(_configRoot, _transactionRoot, _rootAuthority,
                    m.TransactionId, _sessionId, _quietGeneration, MigrationOperationJournal.EmptyChainDigest,
                    MigrationOperationJournal.EmptyChainDigest, MigrationStage.SnapshotReady);
                var capturedFiles = witness.Entries.Where(e => e.Version.Kind == MigrationEntryKind.File).ToArray();
                if (capturedFiles.Length != hashes.Count || capturedFiles.Any(e =>
                    !hashes.TryGetValue(e.Path, out var hash) || e.Version.Sha256 != hash ||
                    !versions.TryGetValue(e.Path, out var version) || !version.Matches(e.Version)))
                    return MarkBlocked("snapshot_changed_during_controlled_capture");
                m.ControlledBaseline = witness;
                m.ControlledLatest = witness;
            }
            m.SnapshotManifestHash = ComputeSnapshotManifestHash(hashes);
            m.BaselineCompleted = true;                       // 基线（完整清单）已发布
            m.Stage = MigrationStage.SnapshotReady;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
                    }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
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
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            // **确认引用更新后冻结登记（会诊 IMPORTANT-6）**：真实写集一旦确认，变更登记即为已核验证据的一部分；
            // 此后改登记（新增/改写归属）会使「声明写集 ↔ 变更登记」一致性失效，一律拒绝，须重新开事务。
            if (m.ReferenceWriteSet.Count > 0 || _effects is IPreparedMigrationEffectService &&
                File.Exists(Path.Combine(OperationRoot(m), "operation-journal.json")) && OpenJournal(m, companion: false).ReadAuthority().Document.RegistrationFrozen)
                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);

            if (!TryFreezeChanges(changes, out var frozenChanges, out var freezeFailure))
                return MigrationResult.Fail(freezeFailure!, m.Stage);
            if (CheckAfterInputFreeze() is { } freezeProblem) return MigrationResult.Fail(freezeProblem, m.Stage);
            var batch = new List<ChangeRecord>();
            foreach (var c in frozenChanges)
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

            // **拓扑约束**：`Added` 路径不得与 `Modified/Deleted` 路径互为祖先——文件↔目录互换会让「先恢复子路径、
            // 后删除父路径」无法收敛（父被新文件阻挡）。登记期结构化拒绝，避免提交前崩溃后进入不可恢复回滚。
            foreach (var c in batch)
            {
                if (!IsCanonicalExistingPath(_configRoot, c.Path, out var badSegment))
                    return MigrationResult.Fail("non_canonical_path:" + badSegment, m.Stage);   // 短名/别名等新引用不一致 ⇒ 拒绝
            }

            var merged = m.ChangedFiles.Concat(batch).ToList();
            for (var i = 0; i < merged.Count; i++)
            {
                for (var j = 0; j < merged.Count; j++)
                {
                    if (i == j) continue;
                    var a = merged[i];
                    var b = merged[j];
                    if (a.Kind != ChangeKind.Added && b.Kind != ChangeKind.Added) continue;   // Modified/Deleted 必对应基线文件，不互为祖先
                    if (IsAncestorPath(a.Path, b.Path))                                        // 含 **Added↔Added**（同批与跨次）
                        return MigrationResult.Fail("unsupported_topology_change:" + a.Path + "<->" + b.Path, m.Stage);
                }
            }

            // **拓扑（基线侧）**：每条 Added 亦须与**完整基线文件集合**双向祖先检查——只登记 Added（未同时登记
            // 对应 Deleted）同样可能造成「先恢复基线子路径、后删除新增父路径」的不可收敛回滚，不能依赖调用方补齐。
            foreach (var added in merged.Where(c => c.Kind == ChangeKind.Added))
            {
                foreach (var basePath in m.FileHashes.Keys)
                {
                    if (IsAncestorPath(added.Path, basePath) || IsAncestorPath(basePath, added.Path))
                        return MigrationResult.Fail("unsupported_topology_change:" + added.Path + "<->baseline:" + basePath, m.Stage);
                }
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
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    /// <summary>
    /// **阶段推进旧语义（仅限未接真实副作用的只读夹具）**：已注入真实副作用端口后**必须**用
    /// <see cref="ApplyReferenceUpdate"/>；否则本方法会在零写入的情况下把阶段推进到 `ReferenceUpdating`，
    /// 使 `RehearseRollback`/`Commit` 全部通过而配置根**从未发生真实引用更新**（假成功）。此门禁只在
    /// 生产/真实事务上生效，不改变旧夹具（`effectService: null`）的行为。
    /// </summary>
    public MigrationResult MarkReferenceUpdateCompleted()
    {
        using var entry = EnterMutation();
        if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
        return _effects is null ? Advance(MigrationStage.ReferenceUpdating)
            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
    }
    /// <summary>阶段推进旧语义（同 <see cref="MarkReferenceUpdateCompleted"/>：注入真实副作用端口后一律拒绝）。</summary>
    public MigrationResult MarkActivated()
    {
        using var entry = EnterMutation();
        if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
        return _effects is null ? Advance(MigrationStage.Activated)
            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
    }

    /// <summary>
    /// **真实引用更新（R5.6 A 项）**：声明写集 → 真实副作用 → **逐项读回确认** → **才**持久化阶段与写集证据。
    /// 拒绝/未知/取消/读回不符一律不推进阶段：未知与读回不符置 `Blocked`（fail-closed、不盲目重试）；
    /// 副作用前取消保持当前阶段（可重试/可回滚）；已到本阶段时幂等重读盘复核，**不二次触发副作用**。
    /// </summary>
    public MigrationResult ApplyReferenceUpdate(MigrationReferenceUpdatePlan plan)
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (m.LegacySource is not null) return MigrationResult.Fail("legacy_forward_evidence_unverified", m.Stage);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入（不抛异常）

            if (!TryFreezeReferencePlan(plan, out var frozenPlan, out var freezeFailure))
                return MigrationResult.Fail(freezeFailure!, m.Stage);
            if (CheckAfterInputFreeze() is { } freezeProblem) return MigrationResult.Fail(freezeProblem, m.Stage);
            plan = frozenPlan!;
            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
            if (!IsLegalAdvance(m.Stage, MigrationStage.ReferenceUpdating))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->ReferenceUpdating", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 真实写入必须在**存续**窗口内
            if (_effects is null) return MigrationResult.Fail("effect_service_absent", m.Stage);

            // A baseline is the authorized input, not whatever happens to be on disk
            // when the effect service opens the target. Never adopt foreign bytes.
            foreach (var baseline in m.FileHashes)
            {
                if (!TryHashConfigFile(baseline.Key, out var inputHash, out var inputProblem))
                    return MarkBlocked("reference_input_unverifiable:" + baseline.Key + ":" + inputProblem);
                if (!string.Equals(inputHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("reference_input_differs_from_baseline:" + baseline.Key);
            }

            var stageBeforeEffect = m.Stage;
            MigrationEffectResult result;
            Dictionary<string, string>? actualWriteHashes = null;
            try
            {
                BeginEffectCall();
                if (_effects is IPreparedMigrationEffectService prepared)
                    result = ApplyPreparedReferencePlan(m, plan!, prepared, out actualWriteHashes);
                else
                    result = _effects.ApplyReferenceUpdate(_configRoot, plan!);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }
            finally
            {
                EndEffectCall();
            }
            if (ReleaseGuardProblem() is { } releasedAfterApply) return MarkBlocked(releasedAfterApply);
            if (CurrentStageOrNone() != stageBeforeEffect)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeEffect + "->" + CurrentStageOrNone());
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
                if (!ReadReferenceStateGuarded(target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
                if (ReleaseGuardProblem() is { } releasedAfterReadback) return MarkBlocked(releasedAfterReadback);
                if (!TryHashConfigFile(target.Path, out var hash, out var hashProblem))
                    return MarkBlocked("reference_readback_" + hashProblem + ":" + target.Path);
                if (actualWriteHashes is not null &&
                    (!actualWriteHashes.TryGetValue(target.Path, out var appliedHash) || hash != appliedHash))
                    return MarkBlocked("reference_readback_differs_from_applied:" + target.Path);
                writeSet[target.Path] = hash;
            }
            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);
            // **写集外零改动**：未在声明写集内的基线文件必须与快照逐字节一致（防「确认之外的部分写入」）
            foreach (var baseline in m.FileHashes)
            {
                if (writeSet.Keys.Any(k => PathKey(k) == PathKey(baseline.Key))) continue;
                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
            }
            m.ReferenceWriteSet = writeSet;
            if (RefreshControlledWitness(m, MigrationStage.ReferenceUpdating) is { } rootProblem)
                return MarkBlocked(rootProblem);
            m.Stage = MigrationStage.ReferenceUpdating;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    private MigrationInputBytes AdmitOrCheckMain(bool allowAbsent)
    {
        if (!HoldsExclusiveLock || _rootAuthority is null || !_rootAuthority.IsHeld)
            throw new MigrationMainOwnershipException("migration_main_admission_lease_missing");
        MigrationInputBytes observed;
        try
        {
            observed = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot)
                .ReadArtifactInput("migration-manifest.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _mainAdmissionAttempted = true;
            throw new MigrationMainOwnershipException("migration_main_input_unverifiable:" + ex.Message);
        }
        if (_mainOwnership is null)
        {
            if (_mainAdmissionAttempted) throw new MigrationMainOwnershipException("migration_main_admission_unverified");
            _mainAdmissionAttempted = true;
            if (observed.Version.Exists)
            {
                if (DecodeValidatedMain(observed.Bytes) is null)
                    throw new MigrationMainOwnershipException("migration_main_admission_invalid");
            }
            else if (!allowAbsent) throw new MigrationMainOwnershipException("migration_main_admission_missing");
            _mainOwnership = new(observed);
        }
        else if (!_mainOwnership.Input.Version.Matches(observed.Version) ||
                 !_mainOwnership.Input.Bytes.AsSpan().SequenceEqual(observed.Bytes))
            throw new MigrationMainOwnershipException("migration_main_input_not_owned");
        return SnapshotOwnedMain();
    }

    private MigrationInputBytes SnapshotOwnedMain()
    {
        var input = _mainOwnership?.Input ?? throw new MigrationMainOwnershipException("migration_main_owned_input_missing");
        return new(input.Version, (byte[])input.Bytes.Clone());
    }

    private MigrationArtifactWrite MainWrite(byte[] bytes, MigrationInputBytes expected)
        => new("migration-manifest.json", (byte[])bytes.Clone(), expected.Version.Sha256)
        {
            ExpectedInput = new(expected.Version, (byte[])expected.Bytes.Clone()),
            MainOwnership = _mainOwnership ?? throw new MigrationMainOwnershipException("migration_main_owner_missing")
        };

    private MigrationStage OwnedMainStage()
    {
        try { return _mainOwnership?.Input.Version.Exists == true ? DecodeLocalMain(_mainOwnership.Input.Bytes).Stage : MigrationStage.None; }
        catch { return MigrationStage.None; }
    }

    private bool IsNoBaselineAbortShape(MigrationManifest m)
        => m.SchemaVersion == 2 && m.LegacySource is null && !m.BaselineCompleted &&
           m.FileHashes.Count == 0 && m.BaselineVersions.Count == 0 && m.ChangedFiles.Count == 0 &&
           m.ReferenceWriteSet.Count == 0 && m.ControlledBaseline is null && m.ControlledLatest is null &&
           m.JournalBinding is null && m.ActivationRecord is null && m.LegacyBaselineObservation is null &&
           m.CommitMarker is null && !m.RollbackRehearsed && m.RehearsalScope is null;

    private bool VerifyNoBaselineAbort(MigrationManifest m, bool terminal)
    {
        if (!IsNoBaselineAbortShape(m) || _rootAuthority is null || !_rootAuthority.IsHeld ||
            terminal && (m.Stage != MigrationStage.RolledBack || m.BlockedReason is not null) ||
            !terminal && m.Stage is not (MigrationStage.Snapshotting or MigrationStage.Blocked)) return false;
        if (_rootAuthority.PendingTransactionId != (terminal ? null : m.TransactionId)) return false;
        if (!File.Exists(HistoryPath) || !File.ReadAllLines(HistoryPath).Any(line => line.Trim() == m.TransactionId)) return false;
        var ownRoot = Path.Combine(_transactionRoot, "operations", m.TransactionId);
        if (!IsSafeTarget(_transactionRoot, "operations/" + m.TransactionId)) return false;
        if (Directory.Exists(ownRoot) && Directory.EnumerateFileSystemEntries(ownRoot).Any()) return false;
        // No shared journal exemption, even if its evidence is corrupt/unreadable.
        if (File.Exists(Path.Combine(_transactionRoot, "operation-journal.json"))) return false;
        return true;
    }

    private MigrationOperationJournal OpenJournal(MigrationManifest manifest, bool create = false, bool companion = true)
    {
        var root = OperationRoot(manifest, create);
        var prefix = Path.GetRelativePath(_transactionRoot, root).Replace('\\', '/');
        if (prefix == ".") prefix = "";
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
        return new(root, store, prefix, companion ? (previous, next) => BuildMainCompanion(previous, next, prefix) : null);
    }

    private MigrationManifest DecodeLocalMain(byte[] raw)
    {
        if (!MigrationLegacyManifestCodec.TryDecode(raw, out var manifest, out _) || manifest is null ||
            !IsManifestIntegrityValidCore(manifest, validateJournal: false))
            throw new InvalidDataException("migration_local_main_invalid");
        return manifest;
    }

    private IReadOnlyList<MigrationArtifactWrite> BuildMainCompanion(MigrationAuthorityView? previous,
        MigrationAuthorityView next, string prefix, MigrationControlledRootWitness? witness = null,
        MigrationManifest? requested = null, MigrationInputBytes? expectedMain = null)
    {
        var expected = expectedMain ?? SnapshotOwnedMain();
        if (!expected.Version.Exists) throw new MigrationMainOwnershipException("migration_companion_main_missing");
        var old = DecodeLocalMain(expected.Bytes);
        if (previous is not null && old.JournalBinding is not null) ValidateMainAgainstAuthority(old, previous, prefix);
        if (requested is not null && requested.TransactionId != old.TransactionId)
            throw new InvalidDataException("migration_requested_main_transaction_changed");
        var projected = ProjectMain(requested ?? old, next, prefix);
        if (witness is not null) projected.ControlledLatest = witness;
        projected.ManifestIntegrity = ComputeManifestIntegrity(projected);
        return [MainWrite(JsonSerializer.SerializeToUtf8Bytes(projected,
            new JsonSerializerOptions { WriteIndented = true }), expected)];
    }

    private static MigrationActivationRecord? DeriveActivation(MigrationAuthorityView view)
    {
        var activate = view.Document.Operations.SingleOrDefault(e => e.State == MigrationOperationState.Applied &&
            e.Intent.Phase == MigrationOperationPhase.Activate);
        if (activate is null) return null;
        var predecessor = view.Document.Operations.Single(e => e.Intent.OperationId == activate.Intent.PredecessorOperationId);
        if (predecessor.State != MigrationOperationState.Applied || predecessor.Intent.Phase != MigrationOperationPhase.Reference ||
            PathKey(predecessor.Intent.Path) != PathKey(activate.Intent.Path))
            throw new InvalidDataException("migration_activation_reference_predecessor_invalid");
        var reference = view.Resolutions[predecessor.Intent.OperationId];
        var applied = view.Resolutions[activate.Intent.OperationId];
        if (!reference.Output.Matches(applied.Input)) throw new InvalidDataException("migration_activation_input_not_reference_output");
        var pure = new WorkflowFileMigrationEffectService();
        if (!pure.TryPrepareActivation(new(activate.Intent.Path, CandidateReadyStatus, ActiveStatus, applied.Input.Sha256),
            view.Outputs[predecessor.Intent.OperationId], out var expected, out var already, out _) || already ||
            !expected.AsSpan().SequenceEqual(view.Outputs[activate.Intent.OperationId]))
            throw new InvalidDataException("migration_activation_fixed_d13_evidence_invalid");
        var record = new MigrationActivationRecord { Path = activate.Intent.Path, BeforeStatus = CandidateReadyStatus,
            AfterStatus = ActiveStatus, AfterHash = applied.Output.Sha256! };
        var undo = view.Document.Operations.SingleOrDefault(e => e.State == MigrationOperationState.Applied &&
            e.Intent.Phase == MigrationOperationPhase.Undo && PathKey(e.Intent.Path) == PathKey(record.Path));
        if (undo is not null)
        {
            var reverted = view.Resolutions[undo.Intent.OperationId];
            if (undo.Intent.PredecessorOperationId != activate.Intent.OperationId || !applied.Output.Matches(reverted.Input) ||
                !pure.TryPrepareActivation(new(record.Path, ActiveStatus, CandidateReadyStatus, reverted.Input.Sha256),
                    view.Outputs[activate.Intent.OperationId], out var expectedReverted, out var alreadyReverted, out _) || alreadyReverted ||
                !expectedReverted.AsSpan().SequenceEqual(view.Outputs[undo.Intent.OperationId]))
                throw new InvalidDataException("migration_undo_fixed_d13_evidence_invalid");
            record.RevertedHash = reverted.Output.Sha256;
        }
        return record;
    }

    private static MigrationManifest ProjectMain(MigrationManifest original, MigrationAuthorityView view, string prefix)
    {
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(JsonSerializer.SerializeToUtf8Bytes(original))!;
        if (manifest.TransactionId != view.Document.TransactionId)
            throw new InvalidDataException("migration_main_journal_transaction_mismatch");
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in view.Document.Operations.Where(e => e.State == MigrationOperationState.Applied &&
            e.Intent.Phase == MigrationOperationPhase.Reference))
            if (!writes.TryAdd(entry.Intent.Path, view.Resolutions[entry.Intent.OperationId].Output.Sha256!))
                throw new InvalidDataException("migration_duplicate_reference_applied");
        var activation = DeriveActivation(view);
        if (activation is not null)
        {
            var key = writes.Keys.Single(k => PathKey(k) == PathKey(activation.Path));
            writes[key] = activation.RevertedHash ?? activation.AfterHash;
        }
        manifest.ReferenceWriteSet = writes;
        manifest.ActivationRecord = activation;
        manifest.Stage = view.Document.Stage;
        manifest.BlockedReason = view.Document.BlockedReason;
        manifest.CommitMarker = manifest.Stage == MigrationStage.Committed ? manifest.TransactionId : null;
        if (manifest.Stage is MigrationStage.Blocked or MigrationStage.RollingBack or MigrationStage.RolledBack)
        { manifest.RollbackRehearsed = false; manifest.RehearsalScope = null; }
        if (view.Document.Declaration is { } declaration && view.Document.RegistrationFrozen)
            manifest.ChangedFiles = declaration.ChangedFiles.Select(c => new ChangeRecord { Path = c.Path, Kind = c.Kind }).ToList();
        manifest.JournalBinding = new((prefix.Length == 0 ? "" : prefix + "/") + "operation-journal.json",
            view.JournalBytesSha256, view.Document.Declaration is null ? null : MigrationOperationJournal.Hash(view.Document.Declaration),
            view.ResolutionChainDigest, view.AppliedChainDigest, view.Document.LastStableStage, view.Document.PendingStage);
        return manifest;
    }

    private static void ValidateMainAgainstAuthority(MigrationManifest manifest, MigrationAuthorityView view, string prefix)
    {
        var expected = ProjectMain(manifest, view, prefix);
        if (MigrationOperationJournal.Hash(manifest.ReferenceWriteSet) != MigrationOperationJournal.Hash(expected.ReferenceWriteSet) ||
            MigrationOperationJournal.Hash(manifest.ActivationRecord) != MigrationOperationJournal.Hash(expected.ActivationRecord) ||
            MigrationOperationJournal.Hash(manifest.JournalBinding) != MigrationOperationJournal.Hash(expected.JournalBinding) ||
            manifest.Stage != expected.Stage || manifest.BlockedReason != expected.BlockedReason || manifest.CommitMarker != expected.CommitMarker ||
            MigrationOperationJournal.Hash(manifest.ChangedFiles) != MigrationOperationJournal.Hash(expected.ChangedFiles))
            throw new InvalidDataException("migration_main_journal_view_mismatch");
        if (view.Document.Declaration is { } declaration &&
            (declaration.SnapshotId != manifest.SnapshotId || declaration.SnapshotManifestHash != manifest.SnapshotManifestHash ||
             manifest.ControlledBaseline is null || MigrationOperationJournal.Hash(declaration.BaselineEntries) !=
                MigrationOperationJournal.Hash(manifest.ControlledBaseline.Entries)))
            throw new InvalidDataException("migration_main_declaration_snapshot_mismatch");
    }

    private MigrationFrozenDeclaration BuildDeclaration(MigrationManifest manifest, MigrationReferenceUpdatePlan plan,
        IPreparedMigrationEffectService prepared)
    {
        var references = new List<MigrationFrozenReference>();
        foreach (var target in plan.Targets)
        {
            byte[]? input = null;
            var version = MigrationFileVersion.Absent();
            if (target.Kind == ChangeKind.Modified)
            {
                if (!TryGetBaselineVersion(manifest, target.Path, out version))
                    throw new InvalidDataException("migration_declaration_baseline_identity_missing");
                input = File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, NormalizePath(target.Path).Replace('/', Path.DirectorySeparatorChar)));
                if (MigrationFileVersion.Hash(input) != version.Sha256)
                    throw new InvalidDataException("migration_declaration_snapshot_bytes_changed");
            }
            if (!prepared.TryPrepareReference(target, input, out var output, out var problem))
                throw new InvalidDataException("migration_declaration_transform_rejected:" + problem);
            references.Add(new(target with { }, version, MigrationFileVersion.Hash(output)));
        }
        return new(manifest.TransactionId, manifest.SnapshotId, manifest.SnapshotManifestHash,
            manifest.ChangedFiles.Select(c => new ChangeRecord { Path = c.Path, Kind = c.Kind }).ToArray(), references)
        { BaselineEntries = manifest.ControlledBaseline?.Entries ?? throw new InvalidDataException("migration_declaration_s0_missing") };
    }


    private MigrationEffectResult ApplyPreparedReferencePlan(MigrationManifest manifest,
        MigrationReferenceUpdatePlan plan, IPreparedMigrationEffectService prepared,
        out Dictionary<string, string> actualWriteHashes)
    {
        actualWriteHashes = new(StringComparer.OrdinalIgnoreCase);
        var operationRoot = OperationRoot(manifest, create: true);
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
        var journal = OpenJournal(manifest, create: true);
        var path = Path.Combine(operationRoot, "operation-journal.json");
        var document = File.Exists(path) ? journal.Load() : journal.Create(manifest.TransactionId);
        if (document.TransactionId != manifest.TransactionId)
            return MigrationEffectResult.Rejected("reference_journal_transaction_mismatch");
        journal.FreezeDeclaration(BuildDeclaration(manifest, plan, prepared));
        var writes = 0;
        foreach (var target in plan.Targets)
        {
            var input = store.ReadVersion(target.Path);
            if (target.Kind == ChangeKind.Added ? input.Exists :
                !input.Exists || !TryGetHashCaseInsensitive(manifest.FileHashes, target.Path, out var baseline) || input.Sha256 != baseline)
                return MigrationEffectResult.Rejected("reference_input_version_mismatch:" + target.Path, writes);
            if (target.Kind == ChangeKind.Modified && TryGetBaselineVersion(manifest, target.Path, out var baselineVersion) &&
                !baselineVersion.Matches(input))
                return MigrationEffectResult.Rejected("reference_baseline_identity_mismatch:" + target.Path, writes);
            var parents = new Dictionary<string, MigrationFileVersion>(StringComparer.OrdinalIgnoreCase);
            var parentParts = NormalizePath(target.Path).Split('/');
            for (var depth = 1; depth < parentParts.Length; depth++)
            {
                var parent = string.Join('/', parentParts.Take(depth));
                var expectedRoot = FoldControlledRoot(manifest, journal.ReadAuthority());
                parents[parent] = expectedRoot.TryGetValue(parent, out var expectedParent)
                    ? expectedParent.Version : MigrationFileVersion.Absent(MigrationEntryKind.Directory);
            }
            var applied = journal.ExecutePreparedFile(target.Path, input, MigrationOperationPhase.Reference, bytes =>
            {
                if (!prepared.TryPrepareReference(target, bytes, out var output, out var reason))
                    throw new InvalidOperationException(reason);
                return output;
            }, parents: parents, fault: _referenceWriteFault);
            if (applied.CommitConfirmed)
            {
                writes++;
                actualWriteHashes[target.Path] = applied.Output!.Sha256!;
            }
            if (!applied.Success)
                return MigrationEffectResult.Unknown("reference_atomic_write_failed:" + target.Path + ":" + applied.Reason, writes);
        }
        return MigrationEffectResult.Ok(writes);
    }

    private string OperationRoot(MigrationManifest manifest, bool create = false)
    {
        if (!IsSafeTransactionId(manifest.TransactionId)) throw new InvalidOperationException("unsafe_operation_transaction_id");
        var relative = "operations/" + manifest.TransactionId;
        if (!IsSafeTarget(_transactionRoot, relative)) throw new InvalidOperationException("unsafe_operation_root");
        var root = Path.Combine(_transactionRoot, "operations", manifest.TransactionId);
        // Preserve and read an older shared journal only for its own transaction.
        var legacy = Path.Combine(_transactionRoot, "operation-journal.json");
        if (!Directory.Exists(root) && File.Exists(legacy))
        {
            var old = new MigrationOperationJournal(_transactionRoot,
                new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot)).Load();
            if (old.TransactionId == manifest.TransactionId) return _transactionRoot;
        }
        if (create) Directory.CreateDirectory(root);
        return root;
    }

    private void SaveControlledWitness(MigrationControlledRootWitness witness)
        => new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot).WriteArtifacts(
            [new("witnesses/" + witness.WitnessId + ".controlled.json", MigrationOperationJournal.Encode(witness))]);

    private string? RefreshControlledWitness(MigrationManifest manifest, MigrationStage? successfulStage = null)
    {
        if (_rootAuthority is null) return null;
        try
        {
            var facts = ClassifyPersistentProtocol();
            manifest.ControlledLatest = QualifyModernSuccess(manifest, successfulStage ?? manifest.Stage,
                SnapshotOwnedMain(), facts, ModernSuccessPurpose.Publication);
            return null;
        }
        catch (MigrationQualificationException ex) { return ex.Message; }
    }

    private MigrationEffectResult ApplyPreparedActivation(MigrationManifest manifest, MigrationActivationRequest request,
        IPreparedMigrationEffectService prepared, MigrationOperationPhase phase, out string? appliedHash)
    {
        appliedHash = null;
        var root = OperationRoot(manifest);
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
        var journal = OpenJournal(manifest);
        var predecessor = journal.LastAppliedFile(request.Path);
        var input = store.ReadVersion(request.Path);
        if (predecessor is null || !predecessor.Value.Output.Matches(input) || input.Sha256 != request.ExpectedContentHash)
            return MigrationEffectResult.Rejected("activation_input_version_not_applied:" + request.Path);
        if (phase == MigrationOperationPhase.Activate)
        {
            var current = LoadValidated();
            if (current is null || current.ManifestIntegrity != manifest.ManifestIntegrity || current.Stage != manifest.Stage)
                return MigrationEffectResult.Rejected("activation_manifest_changed_before_prepare");
            journal.BeginActivationPhase(SnapshotOwnedMain().Version.Sha256!, request.Path);
        }
        else if (journal.Load().Stage != MigrationStage.RollingBack)
            journal.SetStage(MigrationStage.RollingBack, null);
        var result = journal.ExecutePreparedFile(request.Path, input, phase, bytes =>
        {
            if (!prepared.TryPrepareActivation(request, bytes!, out var output, out var alreadyTarget, out var reason))
                throw new InvalidOperationException(reason);
            if (alreadyTarget) throw new InvalidOperationException("activation_transition_already_applied");
            return output;
        }, predecessor.Value.OperationId, fault: point => _referenceWriteFault?.Invoke(phase.ToString().ToLowerInvariant() + ":" + point));
        if (result.CommitConfirmed) appliedHash = result.Output!.Sha256;
        return result.Success ? MigrationEffectResult.Ok(1) :
            MigrationEffectResult.Unknown("activation_atomic_write_failed:" + result.Reason, result.CommitConfirmed ? 1 : 0);
    }

    /// <summary>
    /// **真实激活（R5.6 A 项：D13 `candidate → active`）**：须已完成真实引用更新；目标必须是**已确认写集内**的文件；
    /// 副作用成功且状态读回一致后才推进到 `Activated`。已到 `Activated` 时幂等重读复核（不二次写入）。
    /// </summary>
    public MigrationResult ActivateCandidate(MigrationActivationRequest request)
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (m.LegacySource is not null) return MigrationResult.Fail("legacy_forward_evidence_unverified", m.Stage);

            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**

            if (m.Stage != MigrationStage.ReferenceUpdating)
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

            // **事务主导的版本绑定（会诊第 3 轮 MUST-3）**：请求携带的哈希必须等于**已确认写集哈希**，
            // 真正交给端口的一律是本事务自己算出的哈希——调用方无法用「另一份内容的哈希」把漂移内容合法化。
            if (!string.IsNullOrEmpty(request.ExpectedContentHash)
                && !string.Equals(request.ExpectedContentHash, confirmedHash, StringComparison.Ordinal))
                return MarkBlocked("activation_request_hash_mismatch:" + request.Path);
            request = request with { ExpectedContentHash = confirmedHash };

            // **前置状态观测（会诊 IMPORTANT-7）**：盘上现值必须**恰为**声明的 before 状态；已等于目标态 ⇒ 拒绝，
            // 不得凭「已生效」零写入成功（幂等复核只经由已持久化的 `Activated` 阶段证据）。
            if (!ReadActivationStatusGuarded(request.Path, out var observed, out var observeDetail))
                return MarkBlocked("activation_precondition_status_unreadable:" + request.Path + ":" + observeDetail);
            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_already_applied:" + request.Path);
            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);

            var stageBeforeActivation = m.Stage;
            MigrationEffectResult result;
            string? actualActivationHash = null;
            try
            {
                BeginEffectCall();
                if (_effects is IPreparedMigrationEffectService prepared)
                    result = ApplyPreparedActivation(m, request, prepared, MigrationOperationPhase.Activate, out actualActivationHash);
                else result = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("activation_exception_unknown:" + request.Path + ":" + ex.GetType().Name);
            }
            finally
            {
                EndEffectCall();
            }
            if (ReleaseGuardProblem() is { } releasedAfterActivation) return MarkBlocked(releasedAfterActivation);
            if (CurrentStageOrNone() != stageBeforeActivation)
                return MarkBlocked("concurrent_state_change_after_effect:" + stageBeforeActivation + "->" + CurrentStageOrNone());
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("activation_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("activation_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }

            if (!ReadActivationStatusGuarded(request.Path, out var status, out var detail)
                || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
            if (!TryHashConfigFile(request.Path, out var hash, out var hashProblem))
                return MarkBlocked("activation_readback_" + hashProblem + ":" + request.Path);
            if (actualActivationHash is not null && hash != actualActivationHash)
                return MarkBlocked("activation_readback_differs_from_applied:" + request.Path);

            m.ActivationRecord = new MigrationActivationRecord
            {
                Path = request.Path,
                BeforeStatus = request.ExpectedBeforeStatus,
                AfterStatus = request.TargetStatus,
                AfterHash = hash,
            };
            // 写集记录的是「本事务写过的文件的**当前期望盘上状态**」：激活同样改写了该文件，故须同步更新其哈希，
            // 否则提交前的写集复核会把本事务自己的激活写入误判为漂移。
            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
                m.ReferenceWriteSet[key] = hash;
            if (RefreshControlledWitness(m, MigrationStage.Activated) is { } rootProblem)
                return MarkBlocked(rootProblem);
            m.Stage = MigrationStage.Activated;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    /// <summary>幂等复核：写集内每个文件仍在盘上且哈希与已确认写集一致（不触发副作用）。</summary>
    private MigrationResult RecheckReferenceWriteSet(MigrationManifest m)
    {
        if (_rootAuthority is not null)
            QualifyModernSuccess(m, m.Stage, SnapshotOwnedMain(), ClassifyPersistentProtocol(), ModernSuccessPurpose.Idempotent);

        if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_write_set_empty", m.Stage);
        foreach (var entry in m.ReferenceWriteSet)
        {
            if (!TryHashConfigFile(entry.Key, out var hash, out var problem))
                return MigrationResult.Fail("reference_recheck_" + problem + ":" + entry.Key, m.Stage);
            if (!string.Equals(hash, entry.Value, StringComparison.Ordinal))
                return MigrationResult.Fail("reference_recheck_hash_mismatch:" + entry.Key, m.Stage);
        }
        if (_rootAuthority is not null)
            QualifyModernSuccess(m, m.Stage, SnapshotOwnedMain(), ClassifyPersistentProtocol(), ModernSuccessPurpose.Idempotent);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>幂等复核：激活记录仍在盘上（哈希一致）且状态仍为目标状态（不触发副作用）。</summary>
    private MigrationResult RecheckActivationRecord(MigrationManifest m)
    {
        if (_rootAuthority is not null)
            QualifyModernSuccess(m, m.Stage, SnapshotOwnedMain(), ClassifyPersistentProtocol(), ModernSuccessPurpose.Idempotent);

        var record = m.ActivationRecord;
        if (record is null) return MigrationResult.Fail("activation_record_missing", m.Stage);
        if (!TryHashConfigFile(record.Path, out var hash, out var problem))
            return MigrationResult.Fail("activation_recheck_" + problem + ":" + record.Path, m.Stage);
        if (!string.Equals(hash, record.AfterHash, StringComparison.Ordinal))
            return MigrationResult.Fail("activation_recheck_hash_mismatch:" + record.Path, m.Stage);
        if (_effects is null) return MigrationResult.Ok(m.Stage);
        try
        {
            if (!ReadActivationStatusGuarded(record.Path, out var status, out var detail)
                || !string.Equals(status, record.AfterStatus, StringComparison.Ordinal))
                return MigrationResult.Fail("activation_recheck_status_mismatch:" + record.Path + ":" + detail, m.Stage);
        }
        catch (Exception ex)      // 复核期异常同样收敛（会诊第 2 轮 IMPORTANT-6）
        {
            return MigrationResult.Fail("activation_recheck_exception:" + record.Path + ":" + ex.GetType().Name, m.Stage);
        }
        if (_rootAuthority is not null)
            QualifyModernSuccess(m, m.Stage, SnapshotOwnedMain(), ClassifyPersistentProtocol(), ModernSuccessPurpose.Idempotent);
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
        // **只接受权威的 D13 转换**（R5.2 §21.2：激活＝`candidate → active`）：本事务不发明通用状态改写器；
        // 其余状态对一律拒绝（回滚的撤销路径不经此入口，直接用端口）。
        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;
        if (m.ReferenceWriteSet.Count == 0) return "activation_request_invalid:reference_write_set_empty";
        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4
        var record = FindChange(m, request.Path);
        if (record is null || record.Kind == ChangeKind.Deleted)
            return "activation_target_not_a_written_file:" + request.Path;                    // REF-F4
        return null;
    }

    /// <summary>
    /// **证据关系不变量（会诊 MUST-4）**：真实证据之间必须自洽——写集键集合**恰等于**变更登记中非删除项、
    /// 身份键唯一、激活记录的盘上哈希必须等于写集中该文件的哈希。任一不符 ⇒ 拒绝（返回原因码）。
    /// </summary>
    private static string? EvidenceRelationProblem(MigrationManifest m)
    {
        var writeSetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in m.ReferenceWriteSet.Keys)
            if (!writeSetKeys.Add(PathKey(key))) return "evidence_relation:duplicate_writeset_key:" + key;
        var changeIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in m.ChangedFiles)
            if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;
        var registered = new HashSet<string>(m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted).Select(c => PathKey(c.Path)), StringComparer.Ordinal);
        if (!writeSetKeys.SetEquals(registered)) return "evidence_relation:writeset_registry_mismatch";
        if (m.ActivationRecord is { } activation)
        {
            if (!TryGetWriteSetHash(m, activation.Path, out var activationHash))
                return "evidence_relation:activation_not_in_writeset:" + activation.Path;
            // **阶段化关系（第 5 轮 IMPORTANT-5）**：Committed 严格等于激活后版本；回滚阶段允许写集停在
            // 「撤销前」或已同步为「撤销后」版本（撤销是两步写入，中间态必须合法且可恢复）。
            var allowed = new HashSet<string>(StringComparer.Ordinal) { activation.AfterHash };
            if (m.Stage != MigrationStage.Committed && !string.IsNullOrEmpty(activation.RevertedHash))
                allowed.Add(activation.RevertedHash);
            if (!allowed.Contains(activationHash))
                return "evidence_relation:activation_hash_mismatch:" + activation.Path;
        }
        return null;
    }

    /// <summary>按大小写不敏感身份取已确认写集哈希。</summary>
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

    private static bool TryGetBaselineVersion(MigrationManifest manifest, string relative, out MigrationFileVersion version)
    {
        foreach (var entry in manifest.BaselineVersions)
            if (PathKey(entry.Key) == PathKey(relative)) { version = entry.Value; return true; }
        version = null!;
        return false;
    }

    /// <summary>按大小写不敏感身份查变更归属。</summary>
    private static ChangeRecord? FindChange(MigrationManifest m, string path)
    {
        var key = PathKey(path);
        foreach (var record in m.ChangedFiles) if (PathKey(record.Path) == key) return record;
        return null;
    }

    /// <summary>
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
        var present = new HashSet<string>(current, StringComparer.Ordinal);
        foreach (var baseline in m.FileHashes.Keys)
            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半
        return null;
    }

    /// <summary>配置根内既有文件的 SHA-256（先做链接/越根安全校验；失败给出原因码）。</summary>
    private bool TryHashConfigFile(string rel, out string hash, out string problem)
    {
        hash = "";
        problem = "unhashable";
        try
        {
            if (!IsSafeRelativePath(rel) || !IsSafeTarget(_configRoot, rel)) { problem = "unsafe_target"; return false; }
        }
        catch (Exception)     // 安全检查自身异常同样收敛为「不可哈希」（会诊第 2 轮 IMPORTANT-6）
        {
            problem = "unsafe_target_check_failed";
            return false;
        }
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
    }

    private MigrationResult Advance(MigrationStage to)
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejected) return rejected;
            try
            {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (!IsLegalAdvance(m.Stage, to)) return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + to, m.Stage);
            m.Stage = to;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException or MigrationMainOwnershipException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
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
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
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
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    /// <summary>提交：阶段 Activated、演练范围一致、无 blocked、静止窗口为当前会话且快照有效 ⇒ 写唯一提交标记。</summary>
    public MigrationResult Commit()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (m.RealEffectsRequired || _effects is not null)
            {
                // **真实事务的提交前置（会诊 MUST-4）**：门槛绑定「本实例是否接入真实副作用」与持久化标记的**并集**，
                // 故把 `realEffectsRequired` 改成 false 不能降级绕过；引用写入与激活都必须由真实副作用 + 读回确认产生
                if (m.ReferenceWriteSet.Count == 0) return MigrationResult.Fail("reference_update_not_confirmed", m.Stage);
                if (m.ActivationRecord is null) return MigrationResult.Fail("activation_not_confirmed", m.Stage);
                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
                if (EvidenceRelationProblem(m) is { } relationProblem) return MigrationResult.Fail(relationProblem, m.Stage);
                if (UnexpectedFileReason(m, m.ChangedFiles.Where(c => c.Kind != ChangeKind.Deleted)
                        .Select(c => new MigrationReferenceWriteTarget(c.Path, c.Kind)).ToList()) is { } unexpected)
                    return MigrationResult.Fail(unexpected, m.Stage);
                // **提交面全基线字节复核（会诊第 3 轮 MUST-4）**：集合相等只证明文件在不在；
                // 写集之外的基线文件若在提交前被改写，同样必须拒绝提交。
                foreach (var baselineFile in m.FileHashes)
                {
                    // 写集内的基线文件被本事务**合法改写/激活**（其当前期望哈希由写集承载），故只核对**写集之外**的基线文件
                    if (TryGetWriteSetHash(m, baselineFile.Key, out _)) continue;
                    if (!TryHashConfigFile(baselineFile.Key, out var commitHash, out var commitProblem))
                        return MigrationResult.Fail("commit_baseline_" + commitProblem + ":" + baselineFile.Key, m.Stage);
                    if (!string.Equals(commitHash, baselineFile.Value, StringComparison.Ordinal))
                        return MigrationResult.Fail("commit_baseline_bytes_differ:" + baselineFile.Key, m.Stage);
                }
            }
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
                return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null || m.QuiescedAtUtc is null
                || !string.Equals(m.QuiesceSessionId, _sessionId, StringComparison.Ordinal)
                || m.QuiesceGeneration != _quietGeneration))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 须**实际存续**且同代次的窗口
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            m.CommitMarker = m.TransactionId;
            m.Stage = MigrationStage.Committed;
            if (RefreshControlledWitness(m, MigrationStage.Committed) is { } rootProblem)
                return MarkBlocked(rootProblem);
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    /// <summary>回滚：先落 RollingBack（清提交标记与演练资格）再做 IO，最后落 RolledBack；只删归属为 Added 的文件。</summary>
    public MigrationResult Rollback()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (_effects is IPreparedMigrationEffectService && IsVerifiedLegacyWithoutModernJournal(m))
                return ObserveLegacyBaselineOrBlock(m);
            if (m.Stage == MigrationStage.RolledBack)
            {
                if (_rootAuthority is not null)
                    QualifyModernSuccess(m, m.Stage, SnapshotOwnedMain(), ClassifyPersistentProtocol(), ModernSuccessPurpose.Idempotent);
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (!m.BaselineCompleted)
            {
                if (_effects is IPreparedMigrationEffectService && !VerifyNoBaselineAbort(m, false))
                    return MigrationResult.Fail("migration_abort_input_unverified", m.Stage);
                // 基线未完成（快照发布前失败/中止）⇒ **安全中止**：清理未完成快照、置 RolledBack，使新事务可开启。
                if (_rootAuthority is null)
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
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }

    /// <summary>
    /// **撤销真实激活**：读回当前状态 → 仍为 `AfterStatus` 时施加反向副作用 → **再读回**确认等于 `BeforeStatus`。
    /// 任一读回失败或状态异常 ⇒ `Blocked`（**绝不**在未确认时报告完整回滚；本事务新增文件已不存在视为旧态）。
    /// </summary>
    private MigrationResult UndoActivation(MigrationManifest m, MigrationActivationRecord record)
    {
        var kind = FindChange(m, record.Path)?.Kind;
        if (_effects is null) return MarkBlocked("rollback_activation_service_absent");
        var readOk = ReadActivationStatusGuarded(record.Path, out var current, out var detail);
        if (!readOk)
        {
            if (kind == ChangeKind.Added && detail == "target_missing")
                return MigrationResult.Ok(m.Stage);            // 本事务新增文件已不存在＝旧态（无激活可撤销）
            return MarkBlocked("rollback_activation_readback_failed:" + record.Path + ":" + detail);
        }
        if (string.Equals(current, record.AfterStatus, StringComparison.Ordinal))
        {
            MigrationEffectResult undo;
            try
            {
                BeginEffectCall();
                // 撤销访问的**期望字节版本＝撤销前（当前）版本**：即激活后的字节（写集/AfterHash）；
                // 撤销后的版本（RevertedHash）用于撤销完成后的确认与重入判定，不能当作写入前的前置哈希。
                var request = new MigrationActivationRequest(record.Path, record.AfterStatus, record.BeforeStatus, record.AfterHash);
                if (_effects is IPreparedMigrationEffectService prepared)
                {
                    undo = ApplyPreparedActivation(m, request, prepared, MigrationOperationPhase.Undo, out var actualUndoHash);
                    if (actualUndoHash is not null && actualUndoHash != record.RevertedHash)
                        return MarkBlocked("rollback_actual_undo_differs_from_prediction:" + record.Path);
                }
                else undo = _effects.Activate(_configRoot, request);
            }
            catch (Exception ex)
            {
                return MarkBlocked("rollback_activation_undo_exception:" + ex.GetType().Name);
            }
            finally { EndEffectCall(); }
            if (ReleaseGuardProblem() is { } releasedDuringUndo) return MarkBlocked(releasedDuringUndo);
            if (undo.Outcome != MigrationEffectOutcome.Succeeded)
                return MarkBlocked("rollback_activation_undo_" + undo.Outcome.ToString().ToLowerInvariant() + ":" + undo.Reason);
        }
        else if (!string.Equals(current, record.BeforeStatus, StringComparison.Ordinal))
        {
            return MarkBlocked("rollback_activation_state_unexpected:" + record.Path + ":" + current);
        }
        if (!ReadActivationStatusGuarded(record.Path, out var afterUndo, out var undoDetail)
            || !string.Equals(afterUndo, record.BeforeStatus, StringComparison.Ordinal))
            return MarkBlocked("rollback_activation_not_reverted:" + record.Path + ":" + undoDetail);
        return MigrationResult.Ok(m.Stage);
    }

    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>
    private void RecordExactBaselineObservation(MigrationManifest manifest, MigrationOperationJournal journal,
        MigrationAuthorityView before, string path, MigrationFileVersion observed, MigrationFileVersion baseline,
        string latestOperationId)
    {
        if (_rootAuthority is null || !baseline.Matches(observed) || baseline.Sha256 != manifest.FileHashes[path])
            throw new InvalidDataException("migration_baseline_observation_input_invalid");
        var witness = MigrationControlledRootCapture.Capture(_configRoot, _transactionRoot, _rootAuthority,
            manifest.TransactionId, _sessionId, _quietGeneration, before.AppliedChainDigest,
            before.ResolutionChainDigest, MigrationStage.RollingBack);
        var observation = new MigrationBaselineObservation(path, observed, baseline.Sha256!, latestOperationId, witness.WitnessId);
        var observations = (before.Document.BaselineObservations ?? []).Where(o => !o.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            .Append(observation).ToArray();
        var expected = FoldControlledRoot(manifest, before, observations);
        if (witness.Entries.Count != expected.Count || witness.Entries.Any(e =>
            !expected.TryGetValue(e.Path, out var wanted) || !wanted.Version.Matches(e.Version)))
            throw new InvalidDataException("migration_baseline_observation_full_root_mismatch");
        var root = OperationRoot(manifest);
        var prefix = Path.GetRelativePath(_transactionRoot, root).Replace('\\', '/');
        if (prefix == ".") prefix = "";
        var candidate = before.Document with { BaselineObservations = observations };
        var writes = journal.BuildPublication(before, candidate,
            rootExtras: [new("witnesses/" + witness.WitnessId + ".controlled.json", MigrationOperationJournal.Encode(witness))],
            companionOverride: (previous, next) => BuildMainCompanion(previous, next, prefix, witness));
        new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot).WriteArtifacts(writes);
    }

    private Dictionary<string, MigrationRootEntry> FoldControlledRoot(MigrationManifest manifest,
        MigrationAuthorityView? authority, IReadOnlyList<MigrationBaselineObservation>? observations = null)
    {
        var baseline = manifest.ControlledBaseline ?? throw new InvalidDataException("controlled_root_baseline_missing");
        var expected = baseline.Entries.ToDictionary(e => e.Path, e => e, StringComparer.OrdinalIgnoreCase);
        if (authority is null) return expected;
        foreach (var entry in authority.Document.Operations.Where(e => e.State == MigrationOperationState.Applied))
        {
            var effect = authority.Resolutions[entry.Intent.OperationId];
            foreach (var directory in effect.Directories) expected[directory.Path] = new(directory.Path, directory.Output, null);
            if (effect.Output.Exists) expected[entry.Intent.Path] = new(entry.Intent.Path, effect.Output, null);
            else expected.Remove(entry.Intent.Path);
        }
        foreach (var observation in observations ?? authority.Document.BaselineObservations ?? [])
        {
            if (observation.MutationPerformed || observation.Reason != "already_exact_baseline_no_write" ||
                !manifest.BaselineVersions.TryGetValue(observation.Path, out var original) || !original.Matches(observation.Observed) ||
                original.Sha256 != observation.BaselineHash || string.IsNullOrWhiteSpace(observation.WitnessId))
                throw new InvalidDataException("migration_baseline_observation_invalid");
            var latest = authority.Document.Operations.LastOrDefault(e => e.State == MigrationOperationState.Applied &&
                e.Intent.Path.Equals(observation.Path, StringComparison.OrdinalIgnoreCase));
            if (latest?.Intent.OperationId != observation.LatestAppliedOperationId ||
                observation.WitnessId.Length != 32 || !observation.WitnessId.All(Uri.IsHexDigit))
                throw new InvalidDataException("migration_baseline_observation_predecessor_invalid");
            var witnessPath = Path.Combine(_transactionRoot, "witnesses", observation.WitnessId + ".controlled.json");
            // A newly captured observation is not yet published when constructing its candidate.
            if (observations is null)
            {
                var proof = JsonSerializer.Deserialize<MigrationControlledRootWitness>(File.ReadAllBytes(witnessPath),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                    ?? throw new InvalidDataException("migration_baseline_observation_witness_missing");
                if (!ValidControlledWitness(proof, manifest.TransactionId) || proof.WitnessId != observation.WitnessId ||
                    proof.SuccessfulStage != MigrationStage.RollingBack ||
                    !proof.ConfigIdentity.Matches(baseline.ConfigIdentity) || !proof.ArtifactIdentity.Matches(baseline.ArtifactIdentity) ||
                    !proof.Entries.Any(e => e.Path.Equals(observation.Path, StringComparison.OrdinalIgnoreCase) && e.Version.Matches(observation.Observed)))
                    throw new InvalidDataException("migration_baseline_observation_witness_invalid");
                var validPrefix = Enumerable.Range(0, authority.Document.Operations.Count + 1).Any(count =>
                {
                    var prefix = authority.Document.Operations.Take(count).ToArray();
                    return MigrationOperationJournal.Hash(prefix.Where(e => e.State == MigrationOperationState.Applied).ToArray()) == proof.AppliedChainDigest &&
                        MigrationOperationJournal.Hash(prefix.Where(e => e.State != MigrationOperationState.Prepared).ToArray()) == proof.ResolutionChainDigest;
                });
                if (!validPrefix) throw new InvalidDataException("migration_baseline_observation_witness_chain_invalid");
            }
            expected[observation.Path] = new(observation.Path, observation.Observed, null);
        }
        return expected;
    }


    private bool IsVerifiedLegacyWithoutModernJournal(MigrationManifest manifest)
    {
        if (manifest.SchemaVersion != 2 || manifest.LegacySource is not { } source ||
            manifest.JournalBinding is not null || manifest.ControlledBaseline is not null ||
            manifest.ControlledLatest is not null || manifest.BaselineVersions.Count != 0 ||
            source.ArchivePath != "legacy/" + source.Sha256 + ".json" || !IsSafeTarget(_transactionRoot, source.ArchivePath)) return false;
        var root = OperationRoot(manifest);
        // A missing modern journal never becomes a legacy exemption, including orphaned receipts/blobs.
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() ||
            File.Exists(Path.Combine(_transactionRoot, "operation-journal.json"))) return false;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(_transactionRoot, source.ArchivePath));
            if (MigrationFileVersion.Hash(bytes) != source.Sha256 ||
                !MigrationLegacyManifestCodec.TryDecode(bytes, out var original, out var format) ||
                original!.SchemaVersion != 1 || format != source.Format) return false;
            object Facts(MigrationManifest m) => new { m.TransactionId, m.CreatedAtUtc, m.ConfigRoot, m.SnapshotPath,
                m.SnapshotId, m.RollbackEntry, m.FileHashes, m.ChangedFiles, m.SnapshotManifestHash, m.BaselineCompleted,
                m.ReferenceWriteSet, m.ActivationRecord, m.RealEffectsRequired };
            return MigrationOperationJournal.Hash(Facts(manifest)) == MigrationOperationJournal.Hash(Facts(original));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }

    private MigrationControlledRootWitness CaptureLegacyExactBaseline(MigrationManifest manifest)
    {
        if (!IsVerifiedLegacyWithoutModernJournal(manifest) || _rootAuthority is null || !_rootAuthority.IsHeld ||
            !manifest.BaselineCompleted || VerifySnapshot() is { Length: > 0 })
            throw new InvalidDataException("legacy_observation_authority_or_snapshot_invalid");
        var current = MigrationControlledRootCapture.Capture(_configRoot, _transactionRoot, _rootAuthority,
            manifest.TransactionId, _sessionId, _quietGeneration, MigrationOperationJournal.EmptyChainDigest,
            MigrationOperationJournal.EmptyChainDigest, null);
        var files = current.Entries.Where(e => e.Version.Kind == MigrationEntryKind.File).ToArray();
        if (files.Length != manifest.FileHashes.Count || files.Any(e =>
            !TryGetHashCaseInsensitive(manifest.FileHashes, e.Path, out var hash) || hash != e.Version.Sha256) ||
            manifest.ChangedFiles.Where(c => c.Kind == ChangeKind.Added).Any(c => current.Entries.Any(e => PathKey(e.Path) == PathKey(c.Path))))
            throw new InvalidDataException("legacy_ownership_unknown_no_data_write");
        return current;
    }

    private MigrationResult BlockOwnLegacyMetadata(MigrationManifest manifest, string reason, MigrationInputBytes? expectedMain)
    {
        if (expectedMain is null || _rootAuthority is null || !_rootAuthority.IsHeld ||
            !IsVerifiedLegacyWithoutModernJournal(manifest)) return MigrationResult.Fail(reason, MigrationStage.None);
        try
        {
            var pending = _rootAuthority.PendingTransactionId;
            if (pending is not null && pending != manifest.TransactionId) return MigrationResult.Fail(reason, MigrationStage.None);
            manifest.Stage = MigrationStage.Blocked; manifest.BlockedReason = reason; manifest.CommitMarker = null;
            manifest.RollbackRehearsed = false; manifest.RehearsalScope = null;
            manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
            // The original/own committed input remains the CAS expectation; foreign main bytes are never adopted.
            new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot).WriteArtifactsWithAuthority(
                [MainWrite(bytes, expectedMain)], _rootAuthority, pending, manifest.TransactionId);
            return MigrationResult.Fail(reason, MigrationStage.Blocked);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return MigrationResult.Fail(reason + ":metadata_not_owned", MigrationStage.None); }
    }

    private MigrationResult ObserveLegacyBaselineOrBlock(MigrationManifest manifest)
    {
        var expectedMain = SnapshotOwnedMain();
        try
        {
            if (!IsVerifiedLegacyWithoutModernJournal(manifest) || _rootAuthority is null || !_rootAuthority.IsHeld)
                return MigrationResult.Fail("legacy_observation_authority_invalid", manifest.Stage);
            if (!manifest.BaselineCompleted || VerifySnapshot() is { Length: > 0 })
                return BlockOwnLegacyMetadata(manifest, "legacy_observation_snapshot_invalid", expectedMain);
            if (!EnsureQuiescence(manifest)) return MigrationResult.Fail("no_quiescence_window", manifest.Stage);
            expectedMain = SnapshotOwnedMain();
            var current = CaptureLegacyExactBaseline(manifest);
            // This is a fresh byte observation, never a fabricated historical FileId/directory/Applied proof.
            manifest.LegacyBaselineObservation = new(manifest.LegacySource!.Sha256, manifest.SnapshotManifestHash, current);
            manifest.Stage = MigrationStage.RolledBack;
            manifest.CommitMarker = null; manifest.BlockedReason = null;
            manifest.RollbackRehearsed = false; manifest.RehearsalScope = null;
            WriteManifest(manifest);
            ReleaseQuiescence();
            return MigrationResult.Ok(MigrationStage.RolledBack);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return BlockOwnLegacyMetadata(manifest, "legacy_observation_failed:" + ex.GetType().Name, SnapshotOwnedMain()); }
    }

    private MigrationResult CompletePreparedRollback(MigrationManifest manifest)
    {
        try
        {
            var root = OperationRoot(manifest);
            if (!File.Exists(Path.Combine(root, "operation-journal.json")))
            {
                if (manifest.JournalBinding is not null || manifest.ReferenceWriteSet.Count != 0 || manifest.ActivationRecord is not null)
                    return MigrationResult.Fail("migration_required_journal_missing", manifest.Stage);
                if (RefreshControlledWitness(manifest, MigrationStage.RolledBack) is { } baselineProblem)
                    return MarkBlocked(baselineProblem);
                manifest.Stage = MigrationStage.RolledBack;
                manifest.CommitMarker = null; manifest.BlockedReason = null;
                manifest.RollbackRehearsed = false; manifest.RehearsalScope = null;
                WriteManifest(manifest);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            var journal = OpenJournal(manifest);
            if (journal.ReadAuthority().Document.Operations.Any(e => e.State == MigrationOperationState.Prepared))
            {
                if (_rootAuthority is null || !_rootAuthority.IsHeld) throw new InvalidDataException("migration_resolution_root_authority_missing");
                var beforeResolution = journal.ReadAuthority();
                var expected = FoldControlledRoot(manifest, beforeResolution);
                var fresh = MigrationControlledRootCapture.Capture(_configRoot, _transactionRoot, _rootAuthority,
                    manifest.TransactionId, _sessionId, _quietGeneration, beforeResolution.AppliedChainDigest,
                    beforeResolution.ResolutionChainDigest, null);
                if (fresh.Entries.Count != expected.Count || fresh.Entries.Any(e =>
                    !expected.TryGetValue(e.Path, out var item) || !item.Version.Matches(e.Version)))
                    throw new InvalidDataException("migration_resolution_root_drifted");
                SaveControlledWitness(fresh);
                journal.ResolveTerminatedPreparedTail(fresh);
            }
            journal.SetStage(MigrationStage.RollingBack, null);
            manifest = LoadValidated() ?? throw new InvalidDataException("migration_rollback_main_relation_invalid");
            var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
            var authority = journal.ReadAuthority();
            var references = authority.Document.Operations.Where(e => e.State == MigrationOperationState.Applied &&
                e.Intent.Phase == MigrationOperationPhase.Reference).ToArray();
            foreach (var reference in references)
            {
                var path = reference.Intent.Path;
                authority = journal.ReadAuthority();
                var latest = authority.Document.Operations.Last(e => e.State == MigrationOperationState.Applied &&
                    string.Equals(e.Intent.Path, path, StringComparison.OrdinalIgnoreCase));
                var expected = authority.Resolutions[latest.Intent.OperationId].Output;
                var input = store.ReadVersion(path);
                var added = !reference.Intent.Input.Exists;
                if (added && !input.Exists)
                {
                    if (latest.Intent.Phase != MigrationOperationPhase.DeleteAdded)
                        throw new InvalidDataException("migration_added_absence_without_applied_delete");
                    continue;
                }
                if (!added && TryGetBaselineVersion(manifest, path, out var baseline) && baseline.Matches(input))
                {
                    if (latest.Intent.Phase != MigrationOperationPhase.Restore)
                        RecordExactBaselineObservation(manifest, journal, authority, path, input, baseline, latest.Intent.OperationId);
                    continue;
                }
                if (!expected.Matches(input)) throw new InvalidDataException("migration_rollback_input_not_owned:" + path);
                if (latest.Intent.Phase == MigrationOperationPhase.Activate)
                {
                    var prepared = (IPreparedMigrationEffectService)_effects!;
                    var undo = journal.ExecutePreparedFile(path, input, MigrationOperationPhase.Undo, bytes =>
                    {
                        if (!prepared.TryPrepareActivation(new(path, ActiveStatus, CandidateReadyStatus, input.Sha256),
                            bytes!, out var output, out var already, out var reason) || already)
                            throw new InvalidDataException("migration_undo_prepare_rejected:" + reason);
                        return output;
                    }, latest.Intent.OperationId, fault: point => _referenceWriteFault?.Invoke("undo:" + point));
                    if (!undo.Success) throw new IOException("migration_undo_failed:" + undo.Reason);
                    authority = journal.ReadAuthority();
                    latest = authority.Document.Operations.Last(e => e.State == MigrationOperationState.Applied &&
                        string.Equals(e.Intent.Path, path, StringComparison.OrdinalIgnoreCase));
                    input = authority.Resolutions[latest.Intent.OperationId].Output;
                }
                if (latest.Intent.Phase is not (MigrationOperationPhase.Reference or MigrationOperationPhase.Undo))
                    throw new InvalidDataException("migration_rollback_edge_not_allowed:" + path);
                MigrationVersionMutationResult restored;
                if (added) restored = journal.ExecutePreparedDelete(path, input, latest.Intent.OperationId);
                else
                {
                    var bytes = File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, NormalizePath(path).Replace('/', Path.DirectorySeparatorChar)));
                    if (!TryGetHashCaseInsensitive(manifest.FileHashes, path, out var hash) || MigrationFileVersion.Hash(bytes) != hash)
                        throw new InvalidDataException("migration_snapshot_restore_bytes_changed:" + path);
                    restored = journal.ExecutePreparedFile(path, input, MigrationOperationPhase.Restore,
                        _ => bytes, latest.Intent.OperationId, fault: point => _referenceWriteFault?.Invoke("restore:" + point));
                }
                if (!restored.Success) throw new IOException("migration_restore_or_delete_failed:" + restored.Reason);
                InvokeExternal(() => _fileRestoredHook?.Invoke(path));
            }
            manifest = LoadValidated() ?? throw new InvalidDataException("migration_rollback_main_relation_invalid");
            if (CleanupJournalDirectories(manifest) is { } directoryProblem) throw new IOException(directoryProblem);
            manifest = LoadValidated() ?? throw new InvalidDataException("migration_rollback_main_relation_invalid");
            if (RefreshControlledWitness(manifest, MigrationStage.RolledBack) is { } rootProblem) return MarkBlocked(rootProblem);
            var baselineEntries = manifest.ControlledBaseline!.Entries;
            if (manifest.ControlledLatest!.Entries.Count != baselineEntries.Count || manifest.ControlledLatest.Entries.Any(e =>
                !baselineEntries.Any(b => b.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase) && b.Version.Matches(e.Version))))
                return MarkBlocked("migration_terminal_root_not_exact_baseline");
            manifest.Stage = MigrationStage.RolledBack; manifest.CommitMarker = null; manifest.BlockedReason = null;
            manifest.RollbackRehearsed = false; manifest.RehearsalScope = null;
            WriteManifest(manifest);
            ReleaseQuiescence();
            return MigrationResult.Ok(MigrationStage.RolledBack);
        }
        catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
        { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return MarkBlocked(exception.Message.StartsWith("rollback_directory_cleanup_failed:", StringComparison.Ordinal)
            ? exception.Message : "prepared_rollback_failed:" + exception.Message); }
    }


    private MigrationResult CompleteRollback(MigrationManifest m)
    {
        lock (_sync)
        {
            var rollbackFacts = ClassifyPersistentProtocol();
            AdmitPersistentMutation(nameof(Rollback), rollbackFacts);
            if (rollbackFacts.Kind == PersistentProtocolKind.Modern ||
                rollbackFacts.Kind == PersistentProtocolKind.VerifiedLegacy && _rootAuthority is not null)
                return CompletePreparedRollback(m);
            try
            {
                // **归属预检必须先于任何写入（会诊第 2 轮 MUST-1）**：撤销激活本身也是写入，
                // 若目标是他方文件，先写再查会破坏他方内容；故先核对归属，再决定是否允许写入。
                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（R2-MUST-2）
                var operationRoot = OperationRoot(m);
                if (File.Exists(Path.Combine(operationRoot, "operation-journal.json")))
                {
                    var journal = new MigrationOperationJournal(operationRoot,
                        new WindowsTxfMigrationVersionStore(_configRoot, operationRoot));
                    journal.ResolveTerminatedPreparedTail();
                    if (journal.Load().Stage != MigrationStage.RollingBack) journal.SetStage(MigrationStage.RollingBack, null);
                }
                if (m.LegacySource is not null && !File.Exists(Path.Combine(OperationRoot(m), "operation-journal.json")))
                {
                    foreach (var baseline in m.FileHashes)
                        if (!TryHashConfigFile(baseline.Key, out var current, out _) || current != baseline.Value)
                            return MarkBlocked("legacy_ownership_unknown:" + baseline.Key);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        if (File.Exists(Path.Combine(_configRoot, NormalizePath(added.Path).Replace('/', Path.DirectorySeparatorChar))))
                            return MarkBlocked("legacy_addition_ownership_unknown:" + added.Path);
                    // Already-baseline legacy data needs no destructive operation or invented receipt.
                    m.Stage = MigrationStage.RolledBack;
                    m.CommitMarker = null;
                    WriteManifest(m);
                    ReleaseQuiescence();
                    return MigrationResult.Ok(m.Stage);
                }
                if (ownerBound && PreWriteOwnershipProblem(m) is { } preWriteConflict)
                    return MarkBlocked(preWriteConflict);
                if (m.ActivationRecord is { } activation)
                {
                    // **证据先行（会诊第 4 轮 MUST-1）**：撤销访问的期望字节版本必须在撤销写入**之前**持久化；
                    // 之后重入回滚时，撤销后的版本（预测哈希）与撤销前的版本（写集哈希）都可被认领。
                    if (ownerBound && !PredictAndPersistRevertedHash(m, activation))
                        return MarkBlocked("rollback_reverted_hash_unavailable:" + activation.Path);
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                    // 撤销完成后把写集同步到「撤销后的版本」（写集语义＝本事务写过的文件的当前期望盘上状态）
                    var reverted = activation.RevertedHash;
                    if (ownerBound && !string.IsNullOrEmpty(reverted))
                    {
                        foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(activation.Path)).ToList())
                            m.ReferenceWriteSet[key] = reverted;
                        WriteManifest(m);
                    }
                }
                RestoreFromSnapshot(m, _configRoot);
                // **按核验过的版本删除本事务新增（会诊第 3 轮 MUST-2）**：只查路径成员资格会给「预检时不存在」的
                // 路径授予删除权；改为要求盘上字节仍等于本事务所写/撤销后的期望哈希，否则保留并阻断。
                if (DeleteRecordedAdditions(m, _configRoot, ownerBound) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理/归属不符 ⇒ 保持阻断
                if (CleanupJournalDirectories(m) is { } directoryProblem)
                    return MarkBlocked(directoryProblem);
                // **回滚后旧态一致性（会诊 MUST-5）**：核对**完整基线字节集**（而不是「成功写集」——部分写后
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
                    if (m.ActivationRecord is { } restored)
                    {
                        var kind = FindChange(m, restored.Path)?.Kind;
                        if (kind == ChangeKind.Added)
                        {
                            if (File.Exists(Path.Combine(_configRoot, NormalizePath(restored.Path).Replace('/', Path.DirectorySeparatorChar))))
                                return MarkBlocked("rollback_activation_target_still_present:" + restored.Path);
                        }
                        else if (!ReadActivationStatusGuarded(restored.Path, out var finalStatus, out var finalDetail)
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
            }
            m.Stage = MigrationStage.RolledBack;
            m.CommitMarker = null;
            if (RefreshControlledWitness(m, MigrationStage.RolledBack) is { } rootProblem)
                return MarkBlocked(rootProblem);
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>重启恢复（须先持锁）：已提交+标记+无 blocked ⇒ 保持新态；RollingBack ⇒ 幂等续做；RolledBack ⇒ 幂等；其余 ⇒ 回滚旧态。</summary>
    public MigrationResult RecoverOnStart()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadForMutation();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (_effects is IPreparedMigrationEffectService && IsVerifiedLegacyWithoutModernJournal(m))
                return ObserveLegacyBaselineOrBlock(m);
            if (m.Stage == MigrationStage.Committed
                && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(m.BlockedReason))
            {
                if (_effects is IPreparedMigrationEffectService)
                {
                    if (RefreshControlledWitness(m, MigrationStage.Committed) is { } problem) return MarkBlocked(problem);
                    WriteManifest(m);
                }
                return MigrationResult.Ok(MigrationStage.Committed);
            }
            if (m.Stage == MigrationStage.RolledBack)
            {
                if (_effects is IPreparedMigrationEffectService && !m.BaselineCompleted)
                    return VerifyNoBaselineAbort(m, true) ? MigrationResult.Ok(MigrationStage.RolledBack)
                        : MigrationResult.Fail("migration_abort_terminal_unverified", m.Stage);
                if (_effects is IPreparedMigrationEffectService)
                {
                    if (RefreshControlledWitness(m, MigrationStage.RolledBack) is { } problem) return MarkBlocked(problem);
                    WriteManifest(m);
                }
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (m.Stage == MigrationStage.None) return MigrationResult.Fail("illegal_stage:None", m.Stage);
            if (m.Stage == MigrationStage.Snapshotting || (m.Stage == MigrationStage.Blocked && !m.BaselineCompleted))
            {
                if (_effects is IPreparedMigrationEffectService && !VerifyNoBaselineAbort(m, false))
                    return MigrationResult.Fail("migration_abort_input_unverified", m.Stage);
                // **基线尚未完成**（且尚未发生任何迁移变更）⇒ 安全中止：清理未完成快照、置 RolledBack，使新事务可开启。
                // **绝不**把部分快照用于恢复（不调用 VerifySnapshot/CompleteRollback）。
                if (_rootAuthority is null)
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
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
    }
    /// <summary>授权（须持锁）：完整性校验 → 已提交 → 标记匹配 → 无 blocked。</summary>
    public MigrationResult AuthorizeProductionExecution()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            if (_effects is IPreparedMigrationEffectService)
                return MigrationResult.Fail("isolated_production_not_accepted", MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (m.LegacySource is not null || m.SchemaVersion == 1)
                return MigrationResult.Fail("legacy_production_evidence_unverified", m.Stage);
            var productionFacts = ClassifyPersistentProtocol();
            if (productionFacts.Kind != PersistentProtocolKind.Diagnostic)
                return MigrationResult.Fail("persistent_production_not_accepted", productionFacts.Main?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
                return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if ((m.RealEffectsRequired || _effects is not null)
                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))
                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>
    public MigrationResult TryRunProduction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            var auth = AuthorizeProductionExecution();
            if (!auth.Success) return auth;
            InvokeExternal(action);
            return MigrationResult.Ok(MigrationStage.Committed);
        }
    }

    /// <summary>快照完整性：manifest 结构与不变量 → 快照文件齐全/哈希一致 → 无未登记多余文件。</summary>
    public string VerifySnapshot()
    {
        var manifest = LoadManifest();
        if (manifest is null) return "manifest_missing_or_corrupt";
        if (!IsManifestIntegrityValid(manifest))
            return "manifest_integrity_mismatch";

        return VerifySnapshotContent(manifest);
    }

    private string VerifySnapshotContent(MigrationManifest manifest)
    {
        if (!string.Equals(manifest.SnapshotManifestHash,
            ComputeSnapshotManifestHash(manifest.FileHashes),
            StringComparison.Ordinal))
            return "snapshot_manifest_hash_mismatch";

        var snapshotAttributes =
            MigrationRootAuthority.ObserveAttributes(manifest.SnapshotPath);

        if (snapshotAttributes is null) return "snapshot_missing";
        if ((snapshotAttributes.Value & FileAttributes.Directory) == 0 ||
            (snapshotAttributes.Value & (FileAttributes.ReparsePoint |
                                         FileAttributes.Encrypted)) != 0)
            return "snapshot_unsupported_root";

        List<string> onDisk;
        try
        {
            onDisk = EnumerateFilesSafe(manifest.SnapshotPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            return "snapshot_enumeration_failed:" + ex.GetType().Name;
        }

        foreach (var extra in onDisk
            .Where(file => !manifest.FileHashes.ContainsKey(file))
            .OrderBy(file => file, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;

        foreach (var pair in manifest.FileHashes
            .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsSafeTarget(manifest.SnapshotPath, pair.Key))
                return "snapshot_unsafe_target:" + pair.Key;

            var target = Path.Combine(manifest.SnapshotPath,
                NormalizePath(pair.Key).Replace('/', Path.DirectorySeparatorChar));

            string hash;
            try
            {
                var attributes = MigrationRootAuthority.ObserveAttributes(target);
                if (attributes is null) return "snapshot_file_missing:" + pair.Key;
                if ((attributes.Value & (FileAttributes.Directory |
                                         FileAttributes.ReparsePoint |
                                         FileAttributes.Encrypted)) != 0)
                    return "snapshot_unsafe_target:" + pair.Key;

                hash = Sha256Hex(File.ReadAllBytes(target));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                return "snapshot_read_failed:" + pair.Key;
            }

            if (!string.Equals(hash, pair.Value, StringComparison.Ordinal))
                return "snapshot_hash_mismatch:" + pair.Key;
        }

        return "";
    }

    /// <summary>全字段完整性摘要（仅完整性，不提供来源认证）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
        => m.SchemaVersion == 2 ? MigrationLegacyManifestCodec.CurrentDigest(m) : ComputeLegacyManifestIntegrity(m, true);

    internal static string ComputeLegacyManifestIntegrity(MigrationManifest m, bool includeReverted)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.SnapshotId).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append(m.BaselineCompleted ? '1' : '0').Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
        sb.Append(m.RealEffectsRequired ? '1' : '0').Append('|');
        foreach (var p in (m.ReferenceWriteSet ?? new Dictionary<string, string>(StringComparer.Ordinal))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
        sb.Append('|').Append(m.ActivationRecord is { } ar
            ? ar.Path + ':' + ar.BeforeStatus + '>' + ar.AfterStatus + ':' + ar.AfterHash + (includeReverted ? ':' + (ar.RevertedHash ?? "<null>") : "")
            : "<null>").Append('|');
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

    private bool IsManifestIntegrityValidCore(MigrationManifest m, bool validateJournal = true)
    {
        if (m is null) return false;
        if (m.SchemaVersion is not (1 or 2)) return false;
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
        if (m.BaselineVersions is null) return false;
        if (m.FileHashes.Keys.Select(PathKey).Distinct(StringComparer.Ordinal).Count() != m.FileHashes.Count ||
            m.BaselineVersions.Keys.Select(PathKey).Distinct(StringComparer.Ordinal).Count() != m.BaselineVersions.Count) return false;
        if (m.SchemaVersion == 1 && m.BaselineVersions.Count != 0) return false;
        if (m.ControlledBaseline is { } controlledBaseline && !ValidControlledWitness(controlledBaseline, m.TransactionId)) return false;
        if (m.ControlledLatest is { } controlledLatest &&
            (!ValidControlledWitness(controlledLatest, m.TransactionId) || m.ControlledBaseline is null ||
             !controlledLatest.ConfigIdentity.Matches(m.ControlledBaseline.ConfigIdentity) ||
             !controlledLatest.ArtifactIdentity.Matches(m.ControlledBaseline.ArtifactIdentity))) return false;
        foreach (var baseline in m.BaselineVersions)
        {
            var version = baseline.Value;
            if (!TryGetHashCaseInsensitive(m.FileHashes, baseline.Key, out var hash) || version is null ||
                !version.Exists || version.Kind != MigrationEntryKind.File || string.IsNullOrWhiteSpace(version.VolumeGuid) ||
                version.VolumeSerial is null || version.FileId is null || version.LinkCount != 1 ||
                version.Length is null or < 0 || version.Sha256 != hash) return false;
        }
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
        if (m.ReferenceWriteSet is null) return false;
        foreach (var entry in m.ReferenceWriteSet)
            if (!IsSafeRelativePath(entry.Key) || string.IsNullOrEmpty(entry.Value)) return false;
        if (m.ActivationRecord is { } activation)
        {
            if (!IsSafeRelativePath(activation.Path)) return false;
            if (string.IsNullOrEmpty(activation.BeforeStatus) || string.IsNullOrEmpty(activation.AfterStatus)
                || string.IsNullOrEmpty(activation.AfterHash)) return false;
            if (activation.RevertedHash is { Length: 0 }) return false;
            if (string.Equals(activation.BeforeStatus, activation.AfterStatus, StringComparison.Ordinal)) return false;
            if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(activation.Path))) return false;   // 激活目标须在写集内
        }
        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }
        // **证据关系（会诊 MUST-4）**：只要出现真实证据，写集必须与变更登记精确对应、激活哈希必须等于写集哈希。
        if (m.JournalBinding is null && m.ReferenceWriteSet.Count > 0 && EvidenceRelationProblem(m) is not null) return false;
        if (m.LegacyBaselineObservation is { } legacyObservation)
        {
            var witness = legacyObservation.CurrentObservation;
            if (m.LegacySource is null || legacyObservation.LegacySourceSha256 != m.LegacySource.Sha256 ||
                legacyObservation.SnapshotManifestHash != m.SnapshotManifestHash || legacyObservation.MutationPerformed ||
                legacyObservation.Qualification != "legacy-exact-bytes-only-no-historical-identity" ||
                m.ControlledBaseline is not null || m.ControlledLatest is not null || m.BaselineVersions.Count != 0 ||
                m.JournalBinding is not null || !ValidControlledWitness(witness, m.TransactionId) ||
                witness.SuccessfulStage is not null || witness.AppliedChainDigest != MigrationOperationJournal.EmptyChainDigest ||
                witness.ResolutionChainDigest != MigrationOperationJournal.EmptyChainDigest ||
                witness.Entries.Count(e => e.Version.Kind == MigrationEntryKind.File) != m.FileHashes.Count ||
                witness.Entries.Where(e => e.Version.Kind == MigrationEntryKind.File).Any(e =>
                    !TryGetHashCaseInsensitive(m.FileHashes, e.Path, out var hash) || hash != e.Version.Sha256)) return false;
        }
        if (m.JournalBinding is { } binding)
        {
            if (!IsSafeRelativePath(binding.RelativePath) || binding.JournalBytesSha256 is not { Length: 64 } ||
                binding.ResolutionChainDigest is not { Length: 64 } || binding.AppliedChainDigest is not { Length: 64 }) return false;
            if (validateJournal)
            {
                var root = OperationRoot(m);
                var prefix = Path.GetRelativePath(_transactionRoot, root).Replace('\\', '/');
                if (prefix == ".") prefix = "";
                var authority = OpenJournal(m, companion: false).ReadAuthority();
                ValidateMainAgainstAuthority(m, authority, prefix);
            }
        }
        return MigrationLegacyManifestCodec.VerifyDigest(m);
    }

    private static bool ValidControlledWitness(MigrationControlledRootWitness witness, string transactionId)
        => witness.TransactionId == transactionId && witness.WriterDomain == "registered-root-authority" &&
           witness.LeaseId != Guid.Empty && witness.ProcessId > 0 && witness.Generation >= 0 &&
           witness.LeaseAcquiredAtUtc <= witness.CaptureStartedAtUtc && witness.CaptureStartedAtUtc <= witness.CaptureEndedAtUtc &&
           witness.Entries is not null && witness.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == witness.Entries.Count &&
           witness.ManifestHash == MigrationOperationJournal.Hash(witness.Entries.Select(e => new { e.Path, e.Version }).ToArray()) &&
           witness.Integrity == MigrationOperationJournal.Hash(witness with { Integrity = "" });

    private static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取并按结构+不变量+完整性校验的 manifest（不合格＝null）。</summary>
    public MigrationManifest? LoadValidated()
    {
        try { return File.Exists(ManifestPath) ? DecodeValidatedMain(File.ReadAllBytes(ManifestPath)) : null; }
        catch (IOException) { return null; }
    }

    private MigrationManifest? DecodeValidatedMain(byte[] inputBytes)
    {
        if (!MigrationLegacyManifestCodec.TryDecode(inputBytes, out var m, out _) || m is null || !IsManifestIntegrityValid(m)) return null;
        if (m.LegacySource is { } source)
        {
            try
            {
                if (source.Sha256.Length != 64 || source.ArchivePath != "legacy/" + source.Sha256 + ".json" ||
                    !IsSafeTarget(_transactionRoot, source.ArchivePath)) return null;
                var raw = File.ReadAllBytes(Path.Combine(_transactionRoot, source.ArchivePath.Replace('/', Path.DirectorySeparatorChar)));
                if (Sha256Hex(raw) != source.Sha256 || !MigrationLegacyManifestCodec.TryDecode(raw, out var original, out var format) ||
                    original!.SchemaVersion != 1 || format != source.Format || original.TransactionId != m.TransactionId ||
                    original.ConfigRoot != m.ConfigRoot || original.SnapshotId != m.SnapshotId || original.SnapshotPath != m.SnapshotPath)
                    return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
        }
        return m;
    }

    public MigrationResult UpgradeLegacyManifest()
    {
        using (var entry = EnterMutation())
        {
            if (entry.Rejection is { } rejectedEntry) return rejectedEntry;
            BeginOperation();
            try
            {
                if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", MigrationStage.None);
                if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", MigrationStage.None);
                var upgradeFacts = ClassifyPersistentProtocol();
                AdmitPersistentMutation(nameof(UpgradeLegacyManifest), upgradeFacts);
                var owned = _rootAuthority is not null ? AdmitOrCheckMain(false) : null;
                var validated = owned is null ? LoadValidated() : DecodeValidatedMain(owned.Bytes);
                if (validated is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
                if (validated.SchemaVersion == 2) return MigrationResult.Ok(validated.Stage);
                var raw = owned?.Bytes ?? File.ReadAllBytes(ManifestPath);
                if (!MigrationLegacyManifestCodec.TryDecode(raw, out var original, out var format) ||
                    original!.ManifestIntegrity != validated.ManifestIntegrity)
                    return MigrationResult.Fail("legacy_manifest_changed_before_upgrade", validated.Stage);
                if (validated.BaselineCompleted && VerifySnapshot() is { Length: > 0 } problem)
                    return MigrationResult.Fail("legacy_snapshot_invalid:" + problem, validated.Stage);
                var hash = Sha256Hex(raw);
                var relative = "legacy/" + hash + ".json";
                if (!IsSafeTarget(_transactionRoot, relative)) return MigrationResult.Fail("unsafe_legacy_archive", validated.Stage);
                var archive = Path.Combine(_transactionRoot, "legacy", hash + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                if (File.Exists(archive))
                {
                    if (Sha256Hex(File.ReadAllBytes(archive)) != hash) return MigrationResult.Fail("legacy_archive_changed", validated.Stage);
                }
                else
                {
                    using var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    stream.Write(raw);
                    stream.Flush(true);
                }
                validated.SchemaVersion = 2;
                validated.LegacySource = new(hash, format, relative);
                WriteManifest(validated);
                return MigrationResult.Ok(validated.Stage);
            }
            catch (Exception ex) when (ex is MigrationAdmissionException or MigrationQualificationException)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            catch (MigrationMainOwnershipException ex)
            { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
            finally { EndOperation(); }
        }
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
        var facts = ClassifyPersistentProtocol();
        if (facts.Kind is PersistentProtocolKind.Unknown or PersistentProtocolKind.UnverifiableModern)
            return MigrationResult.Fail(reason + ":persistent_protocol_unverified", OwnedMainStage());
        try { AdmitPersistentMutation(nameof(MarkBlocked), facts); }
        catch (MigrationAdmissionException ex) { return MigrationResult.Fail(ex.Message, OwnedMainStage()); }
        if (_effects is IPreparedMigrationEffectService)
        {
            try
            {
                var owned = DecodeValidatedMain(AdmitOrCheckMain(false).Bytes);
                if (owned is null) return MigrationResult.Fail(reason + ":metadata_not_owned", OwnedMainStage());
                owned.BlockedReason = reason; owned.Stage = MigrationStage.Blocked;
                owned.RollbackRehearsed = false; owned.RehearsalScope = null; owned.CommitMarker = null;
                WriteManifest(owned);
                return MigrationResult.Fail(reason, MigrationStage.Blocked);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return MigrationResult.Fail(reason + ":metadata_not_owned", OwnedMainStage()); }
        }
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

    private void PublishPreparedMain(MigrationManifest requested, Action? beforeAtomicPublish = null)
    {
        if (_rootAuthority is null || !_rootAuthority.IsHeld)
            throw new InvalidOperationException("migration_publication_root_authority_missing");
        // Immutable owned input precedes every publication callback.
        var expectedMain = SnapshotOwnedMain();
        InvokeExternal(() => _stageHook?.Invoke(requested.Stage));
        if (!HoldsExclusiveLock || !_rootAuthority.IsHeld) throw new InvalidOperationException("migration_publication_lease_lost");
        var facts = ClassifyPersistentProtocol();
        var newTransaction = requested.Stage == MigrationStage.Snapshotting &&
            (!expectedMain.Version.Exists || facts.Main?.TransactionId != requested.TransactionId);
        if (newTransaction)
        {
            AdmitPersistentMutation(nameof(BeginTransaction), facts, allowAbsent: true);
            AdmitOrCheckMain(true);
            if (facts.Kind == PersistentProtocolKind.Modern && facts.Main is { } terminal)
                QualifyModernSuccess(terminal, terminal.Stage, expectedMain, facts, ModernSuccessPurpose.TerminalAdmission);
            else if (facts.Kind == PersistentProtocolKind.VerifiedLegacy && facts.Main is { BaselineCompleted: true } legacy)
                CaptureLegacyExactBaseline(legacy);
        }
        else if (facts.Kind is not (PersistentProtocolKind.Modern or PersistentProtocolKind.VerifiedLegacy))
            throw new MigrationAdmissionException(facts.Reason ?? "migration_publication_protocol_invalid");
        else AdmitOrCheckMain(false);
        var successful = requested.Stage is MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or
            MigrationStage.Activated or MigrationStage.Committed or MigrationStage.RolledBack;
        if (successful && facts.Kind == PersistentProtocolKind.Modern)
            requested.ControlledLatest = QualifyModernSuccess(requested, requested.Stage, expectedMain, facts, ModernSuccessPurpose.Publication);
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
        var root = OperationRoot(requested);
        var journalPath = Path.Combine(root, "operation-journal.json");
        IReadOnlyList<MigrationArtifactWrite> writes;
        if (!newTransaction && facts.Journal is { } journal && facts.Authority is { } before)
        {
            var prefix = facts.Prefix;
            var witness = requested.ControlledLatest;
            var candidate = journal.BuildStageTransition(before, requested.Stage, successful || requested.Stage == MigrationStage.RollingBack && !before.Document.Operations.Any(e => e.State == MigrationOperationState.Prepared) ? null : before.Document.PendingStage,
                successful ? witness?.WitnessId : before.Document.StageWitnessId, requested.BlockedReason);
            IReadOnlyList<MigrationArtifactWrite> extra = [];
            if (successful)
            {
                if (witness is null || witness.LeaseId != _rootAuthority.LeaseId || witness.SessionId != _sessionId ||
                    witness.SuccessfulStage != requested.Stage || witness.AppliedChainDigest != before.AppliedChainDigest ||
                    witness.ResolutionChainDigest != before.ResolutionChainDigest)
                    throw new InvalidDataException("migration_publication_fresh_witness_mismatch");
                extra = [new("witnesses/" + witness.WitnessId + ".controlled.json", MigrationOperationJournal.Encode(witness))];
            }
            writes = journal.BuildPublication(before, candidate, rootExtras: extra,
                companionOverride: (previous, next) => BuildMainCompanion(previous, next, prefix, witness, requested, expectedMain));
        }
        else
        {
            if (!IsVerifiedLegacyWithoutModernJournal(requested) &&
                (requested.JournalBinding is not null || requested.ReferenceWriteSet.Count != 0 || requested.ActivationRecord is not null))
                throw new InvalidDataException("migration_required_journal_missing");
            if (requested.LegacySource is not null && requested.Stage == MigrationStage.RolledBack)
                requested.LegacyBaselineObservation = new(requested.LegacySource.Sha256, requested.SnapshotManifestHash,
                    CaptureLegacyExactBaseline(requested));
            requested.ManifestIntegrity = ComputeManifestIntegrity(requested);
            var list = new List<MigrationArtifactWrite>
            {
                MainWrite(JsonSerializer.SerializeToUtf8Bytes(requested,
                    new JsonSerializerOptions { WriteIndented = true }), expectedMain)
            };
            foreach (var witness in new[] { requested.ControlledBaseline, requested.ControlledLatest }
                .Where(w => w is not null).DistinctBy(w => w!.WitnessId))
            {
                var path = "witnesses/" + witness!.WitnessId + ".controlled.json";
                if (!File.Exists(Path.Combine(_transactionRoot, path.Replace('/', Path.DirectorySeparatorChar))))
                    list.Add(new(path, MigrationOperationJournal.Encode(witness)));
            }
            writes = list;
        }
        var currentPending = _rootAuthority.PendingTransactionId;
        if (currentPending is not null && currentPending != requested.TransactionId)
            throw new InvalidOperationException("migration_pending_other_transaction");
        var nextPending = requested.Stage is MigrationStage.Committed or MigrationStage.RolledBack ? null : requested.TransactionId;
        beforeAtomicPublish?.Invoke();
        store.WriteArtifactsWithAuthority(writes, _rootAuthority, currentPending, nextPending);
        InvokeExternal(() => _stageHook?.Invoke(requested.Stage));
        if (requested.LegacySource is not null && requested.Stage == MigrationStage.RolledBack)
        {
            try { CaptureLegacyExactBaseline(requested); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                BlockOwnLegacyMetadata(requested, "legacy_publication_callback_drift", SnapshotOwnedMain());
                throw new InvalidDataException("legacy_publication_callback_drift", ex);
            }
        }
    }


    private void WriteManifest(MigrationManifest manifest, Action? beforeAtomicPublish = null)
    {
        if (manifest.SchemaVersion != 2) throw new InvalidOperationException("legacy_upgrade_required_before_write");
        if (manifest.LegacySource is not null && !IsVerifiedLegacyWithoutModernJournal(manifest))
            throw new InvalidDataException("legacy_projection_or_modern_evidence_invalid");
        if (_rootAuthority is not null && _rootAuthority.IsHeld)
        { PublishPreparedMain(manifest, beforeAtomicPublish); return; }
        var facts = ClassifyPersistentProtocol();
        if (facts.Kind != PersistentProtocolKind.Diagnostic && facts.Kind != PersistentProtocolKind.VerifiedLegacy &&
            !(manifest.Stage == MigrationStage.Snapshotting && facts.Kind == PersistentProtocolKind.Fresh))
            throw new MigrationAdmissionException(facts.Reason ?? "migration_plain_publication_protocol_rejected");
        Directory.CreateDirectory(_transactionRoot);
        InvokeExternal(() => _stageHook?.Invoke(manifest.Stage));
        if (ClassifyPersistentProtocol().Kind != facts.Kind)
            throw new MigrationAdmissionException("migration_plain_publication_protocol_changed");
        beforeAtomicPublish?.Invoke();
        manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true);
        InvokeExternal(() => _stageHook?.Invoke(manifest.Stage));
    }

    private void BeginEffectCall()
    {
        _callbackScopes.Push((CallbackOwner.Value, _effectCallInProgress));
        CallbackOwner.Value = this;
        _effectCallInProgress = true;
    }

    private void InvokeExternal(Action callback)
    {
        BeginEffectCall();
        try { callback(); }
        finally { EndEffectCall(); }
    }

    private T InvokeExternal<T>(Func<T> callback)
    {
        BeginEffectCall();
        try { return callback(); }
        finally { EndEffectCall(); }
    }

    private bool TryFreezeChanges(IEnumerable<ChangeRecord>? source, out ChangeRecord[] frozen, out string? failure)
    {
        try
        {
            frozen = InvokeExternal(() =>
            {
                var values = new List<ChangeRecord>();
                foreach (var item in source ?? [])
                    values.Add(item is null ? null! : new ChangeRecord { Path = item.Path, Kind = item.Kind });
                return values.ToArray();
            });
            failure = null;
            return true;
        }
        catch (Exception ex)
        { frozen = []; failure = "input_freeze_failed:" + ex.GetType().Name; return false; }
    }

    private bool TryFreezeReferencePlan(MigrationReferenceUpdatePlan? source,
        out MigrationReferenceUpdatePlan? frozen, out string? failure)
    {
        try
        {
            frozen = InvokeExternal(() =>
            {
                if (source?.Targets is null) return source;
                var values = new List<MigrationReferenceWriteTarget>();
                foreach (var item in source.Targets)
                    values.Add(item is null ? null! : new MigrationReferenceWriteTarget(
                        item.Path, item.Kind, item.NewContent, item.RenameFrom, item.RenameTo));
                return new MigrationReferenceUpdatePlan(values.ToArray());
            });
            failure = null;
            return true;
        }
        catch (Exception ex)
        { frozen = null; failure = "input_freeze_failed:" + ex.GetType().Name; return false; }
    }

    private string? CheckAfterInputFreeze()
    {
        if (Volatile.Read(ref _disposeRequested) != 0) return "disposed_during_input_freeze";
        if (!HoldsExclusiveLock) return "lock_lost_during_input_freeze";
        // Recheck the original owner; never replace it with input changed by caller code.
        if (_effects is IPreparedMigrationEffectService) AdmitOrCheckMain(false);
        return null;
    }

    private enum PersistentProtocolKind
    {
        Fresh,
        ReservedModern,
        Diagnostic,
        VerifiedLegacy,
        Modern,
        UnverifiableModern,
        Unknown
    }

    private enum ModernSuccessPurpose
    {
        Publication,
        Idempotent,
        TerminalAdmission
    }

    private bool _modernObserved;

    private sealed record ProtocolFacts(
        PersistentProtocolKind Kind,
        MigrationRootPersistentFacts Root,
        MigrationInputBytes? MainInput,
        MigrationManifest? Main,
        HashSet<string> History,
        Dictionary<string, MigrationInputBytes> Files,
        HashSet<string> Directories,
        Dictionary<string, MigrationControlledRootWitness> Witnesses,
        MigrationOperationJournal? Journal,
        MigrationAuthorityView? Authority,
        string Prefix,
        string? Reason);

    private sealed class MigrationAdmissionException(string reason)
        : InvalidOperationException(reason)
    {
    }

    private sealed class MigrationQualificationException(string reason)
        : InvalidOperationException(reason)
    {
    }

    private static readonly JsonSerializerOptions ProtocolJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string ProtocolPath(string prefix, string relative)
        => prefix.Length == 0 ? relative : prefix + "/" + relative;

    private void ScanProtocolTree(
        WindowsTxfMigrationVersionStore store,
        string relative,
        Dictionary<string, MigrationInputBytes> files,
        HashSet<string> directories)
    {
        var full = WindowsTxfMigrationVersionStore.Resolve(_transactionRoot, relative);
        var attributes = MigrationRootAuthority.ObserveAttributes(full);
        if (attributes is null) return;

        if ((attributes.Value & (FileAttributes.ReparsePoint |
                                 FileAttributes.Encrypted)) != 0)
            throw new InvalidDataException("migration_protocol_link_or_encryption");
        if ((attributes.Value & FileAttributes.Directory) == 0)
            throw new InvalidDataException(
                "migration_protocol_directory_kind_invalid:" + relative);

        directories.Add(relative);

        foreach (var path in Directory.EnumerateFileSystemEntries(
            full, "*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false
            }))
        {
            var child = NormalizePath(
                Path.GetRelativePath(_transactionRoot, path));

            if (!IsSafeRelativePath(child))
                throw new InvalidDataException("migration_protocol_path_invalid");

            // 枚举到的 entry 消失，属于无法验证，不当作确认 absence。
            var attr = File.GetAttributes(path);
            if ((attr & (FileAttributes.ReparsePoint |
                         FileAttributes.Encrypted)) != 0)
                throw new InvalidDataException("migration_protocol_entry_unsupported");

            if ((attr & FileAttributes.Directory) != 0)
            {
                ScanProtocolTree(store, child, files, directories);
            }
            else
            {
                var input = store.ReadArtifactInput(child);
                if (!input.Version.Exists || !files.TryAdd(child, input))
                    throw new InvalidDataException(
                        "migration_protocol_entry_changed_or_alias");
            }
        }
    }

    private MigrationControlledRootWitness ReadProtocolWitness(
        MigrationInputBytes input,
        MigrationRootPersistentFacts root,
        HashSet<string> history)
    {
        var proof = JsonSerializer.Deserialize<MigrationControlledRootWitness>(
            input.Bytes, ProtocolJson)
            ?? throw new InvalidDataException("migration_protocol_witness_null");

        if (!ValidControlledWitness(proof, proof.TransactionId) ||
            !IsSafeTransactionId(proof.TransactionId) ||
            !history.Contains(proof.TransactionId) ||
            root.ConfigIdentity is null ||
            root.ArtifactIdentity is null ||
            !proof.ConfigIdentity.Matches(root.ConfigIdentity) ||
            !proof.ArtifactIdentity.Matches(root.ArtifactIdentity))
            throw new InvalidDataException("migration_protocol_witness_invalid");

        return proof;
    }

    private static bool WitnessMatchesJournalPrefix(
        MigrationControlledRootWitness witness,
        MigrationAuthorityView view)
    {
        for (var count = 0; count <= view.Document.Operations.Count; count++)
        {
            var prefix = view.Document.Operations.Take(count).ToArray();

            if (witness.AppliedChainDigest ==
                MigrationOperationJournal.Hash(prefix
                    .Where(e => e.State == MigrationOperationState.Applied).ToArray()) &&
                witness.ResolutionChainDigest ==
                MigrationOperationJournal.Hash(prefix
                    .Where(e => e.State != MigrationOperationState.Prepared).ToArray()))
                return true;
        }

        return false;
    }

    private void ValidateProtocolJournal(
        MigrationAuthorityView view,
        string prefix,
        Dictionary<string, MigrationInputBytes> files,
        HashSet<string> directories,
        Dictionary<string, MigrationControlledRootWitness> witnesses,
        MigrationRootPersistentFacts root,
        HashSet<string> history)
    {
        var doc = view.Document;

        // journal.Create -> FreezeDeclaration 之间的零 data 中间态。
        if (!history.Contains(doc.TransactionId) ||
            doc.Declaration is null &&
            (doc.Operations.Count != 0 ||
             doc.RegistrationFrozen ||
             doc.PendingStage is not null ||
             doc.PhaseAuthorization is not null ||
             (doc.BaselineObservations?.Count ?? 0) != 0))
            throw new InvalidDataException(
                "migration_protocol_undeclared_or_unoccupied_journal");

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProtocolPath(prefix, "operation-journal.json")
        };

        var expectedDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (prefix.Length != 0)
            expectedDirectories.Add(prefix);

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < doc.Operations.Count; index++)
            positions.Add(doc.Operations[index].Intent.OperationId, index);

        foreach (var entry in doc.Operations)
        {
            if (entry.Intent.OutputBlob is { } blob)
                expectedFiles.Add(ProtocolPath(prefix, blob));
            if (entry.ResolutionPath is { } receipt)
                expectedFiles.Add(ProtocolPath(prefix, receipt));

            // 注意：Cancelled resolution.WitnessId 是此 JSON 的 SHA256，
            // 不是 witnesses/<32位rootWitnessId>.controlled.json 的 id。
            var noEffect = ProtocolPath(prefix,
                "resolutions/" + entry.Intent.OperationId +
                ".no-effect-witness.json");

            if (entry.State == MigrationOperationState.Cancelled ||
                entry.State == MigrationOperationState.Prepared &&
                files.ContainsKey(noEffect))
            {
                if (!files.TryGetValue(noEffect, out var input))
                    throw new InvalidDataException(
                        "migration_protocol_no_effect_evidence_missing");

                expectedFiles.Add(noEffect);

                using var json = JsonDocument.Parse(input.Bytes);
                var value = json.RootElement;
                var observed = value.GetProperty("observedInput")
                    .Deserialize<MigrationFileVersion>(ProtocolJson)
                    ?? throw new InvalidDataException(
                        "migration_protocol_no_effect_input_null");

                if (value.GetProperty("transactionId").GetString() !=
                        doc.TransactionId ||
                    value.GetProperty("operationId").GetString() !=
                        entry.Intent.OperationId ||
                    !value.GetProperty("producerTerminated").GetBoolean() ||
                    value.GetProperty("kernelTransactionId").GetGuid() !=
                        entry.Intent.KernelTransactionId ||
                    !observed.Matches(entry.Intent.Input))
                    throw new InvalidDataException(
                        "migration_protocol_no_effect_evidence_invalid");

                // 缺字段/非法格式在 ClassifyPersistentProtocol 中转 Unknown。
                _ = value.GetProperty("capturedAtUtc").GetDateTimeOffset();

                if (entry.State == MigrationOperationState.Cancelled &&
                    view.Resolutions[entry.Intent.OperationId].WitnessId !=
                        MigrationFileVersion.Hash(input.Bytes))
                    throw new InvalidDataException(
                        "migration_protocol_no_effect_witness_hash_mismatch");

                if (doc.Declaration is not null)
                {
                    var proof = value.GetProperty("rootWitness")
                        .Deserialize<MigrationControlledRootWitness>(ProtocolJson)
                        ?? throw new InvalidDataException(
                            "migration_protocol_no_effect_root_missing");

                    var previous = doc.Operations
                        .Take(positions[entry.Intent.OperationId]).ToArray();

                    if (!ValidControlledWitness(proof, doc.TransactionId) ||
                        proof.SuccessfulStage is not null ||
                        root.ConfigIdentity is null ||
                        root.ArtifactIdentity is null ||
                        !proof.ConfigIdentity.Matches(root.ConfigIdentity) ||
                        !proof.ArtifactIdentity.Matches(root.ArtifactIdentity) ||
                        proof.AppliedChainDigest !=
                            MigrationOperationJournal.Hash(previous.Where(e =>
                                e.State == MigrationOperationState.Applied).ToArray()) ||
                        proof.ResolutionChainDigest !=
                            MigrationOperationJournal.Hash(previous.Where(e =>
                                e.State != MigrationOperationState.Prepared).ToArray()))
                        throw new InvalidDataException(
                            "migration_protocol_no_effect_root_invalid");
                }
            }
        }

        foreach (var file in expectedFiles)
        {
            var parts = file.Split('/');
            for (var count = 1; count < parts.Length; count++)
                expectedDirectories.Add(string.Join('/', parts.Take(count)));
        }

        bool InSharedTree(string path)
            => path == "operation-journal.json" ||
               path.StartsWith("outputs/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("resolutions/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("receipts/", StringComparison.OrdinalIgnoreCase);

        foreach (var pair in files)
        {
            var inTree = prefix.Length == 0
                ? InSharedTree(pair.Key)
                : pair.Key.StartsWith(prefix + "/",
                    StringComparison.OrdinalIgnoreCase);

            if (inTree && !expectedFiles.Contains(pair.Key))
                throw new InvalidDataException(
                    "migration_protocol_orphan_operation_artifact:" + pair.Key);
        }

        foreach (var directory in directories)
        {
            var inTree = prefix.Length == 0
                ? directory is "outputs" or "resolutions" or "blobs" or "receipts" ||
                  InSharedTree(directory)
                : directory.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                  directory.StartsWith(prefix + "/",
                      StringComparison.OrdinalIgnoreCase);

            if (inTree && !expectedDirectories.Contains(directory))
                throw new InvalidDataException(
                    "migration_protocol_orphan_operation_directory:" + directory);
        }

        if (doc.StageWitnessId is { } id)
        {
            if (!witnesses.TryGetValue(id, out var proof) ||
                proof.TransactionId != doc.TransactionId ||
                !WitnessMatchesJournalPrefix(proof, view))
                throw new InvalidDataException(
                    "migration_protocol_stage_witness_invalid");
        }

        foreach (var observation in doc.BaselineObservations ?? [])
        {
            if (!witnesses.TryGetValue(observation.WitnessId, out var proof) ||
                proof.TransactionId != doc.TransactionId ||
                !WitnessMatchesJournalPrefix(proof, view))
                throw new InvalidDataException(
                    "migration_protocol_observation_witness_invalid");
        }
    }

    private string? ControlledBaselineProblem(
        MigrationManifest manifest,
        MigrationRootPersistentFacts root)
    {
        var baseline = manifest.ControlledBaseline;

        if (!manifest.BaselineCompleted ||
            baseline is null ||
            !ValidControlledWitness(baseline, manifest.TransactionId) ||
            baseline.SessionId != manifest.SnapshotId ||
            baseline.SuccessfulStage != MigrationStage.SnapshotReady ||
            baseline.AppliedChainDigest != MigrationOperationJournal.EmptyChainDigest ||
            baseline.ResolutionChainDigest != MigrationOperationJournal.EmptyChainDigest)
            return "migration_s0_invalid";

        if (root.ConfigIdentity is null ||
            root.ArtifactIdentity is null ||
            !baseline.ConfigIdentity.Matches(root.ConfigIdentity) ||
            !baseline.ArtifactIdentity.Matches(root.ArtifactIdentity))
            return "migration_s0_root_mismatch";

        var entries = new Dictionary<string, MigrationRootEntry>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baseline.Entries)
        {
            if (entry is null ||
                !IsSafeRelativePath(entry.Path) ||
                entry.Version is null ||
                !entry.Version.Exists ||
                !Enum.IsDefined(entry.Version.Kind) ||
                !entries.TryAdd(NormalizePath(entry.Path), entry) ||
                string.IsNullOrWhiteSpace(entry.Version.VolumeGuid) ||
                entry.Version.VolumeSerial is null ||
                entry.Version.FileId is null ||
                entry.Version.LinkCount != 1 ||
                !string.Equals(entry.Version.VolumeGuid,
                    baseline.ConfigIdentity.VolumeGuid,
                    StringComparison.OrdinalIgnoreCase) ||
                entry.Version.VolumeSerial != baseline.ConfigIdentity.VolumeSerial ||
                (entry.Version.Kind == MigrationEntryKind.File
                    ? entry.Version.Length is null or < 0 ||
                      entry.Version.Sha256 is not { Length: 64 } ||
                      !entry.Version.Sha256.All(Uri.IsHexDigit)
                    : entry.Version.Length is not null ||
                      entry.Version.Sha256 is not null))
                return "migration_s0_entry_invalid";
        }

        var files = entries.Values
            .Where(e => e.Version.Kind == MigrationEntryKind.File).ToArray();

        if (files.Length != manifest.FileHashes.Count ||
            files.Length != manifest.BaselineVersions.Count ||
            manifest.FileHashes.Keys.Select(PathKey).Distinct().Count() !=
                manifest.FileHashes.Count ||
            manifest.BaselineVersions.Keys.Select(PathKey).Distinct().Count() !=
                manifest.BaselineVersions.Count)
            return "migration_s0_file_set_mismatch";

        foreach (var entry in files)
        {
            if (!TryGetHashCaseInsensitive(
                    manifest.FileHashes, entry.Path, out var hash) ||
                entry.Version.Sha256 != hash ||
                !TryGetBaselineVersion(manifest, entry.Path, out var version) ||
                !entry.Version.Matches(version))
                return "migration_s0_file_version_mismatch";
        }

        foreach (var entry in entries.Values)
        {
            var parts = NormalizePath(entry.Path).Split('/');
            for (var count = 1; count < parts.Length; count++)
            {
                if (!entries.TryGetValue(
                        string.Join('/', parts.Take(count)), out var parent) ||
                    parent.Version.Kind != MigrationEntryKind.Directory)
                    return "migration_s0_directory_set_invalid";
            }
        }

        if (manifest.SnapshotManifestHash !=
            ComputeSnapshotManifestHash(manifest.FileHashes))
            return "migration_s0_snapshot_hash_mismatch";

        return null;
    }

    private ProtocolFacts ClassifyPersistentProtocol()
    {
        var root = MigrationRootAuthority.ObservePersistentFacts(
            _configRoot, _transactionRoot);

        MigrationInputBytes? mainInput = null;
        MigrationManifest? main = null;
        var mainInvalid = false;
        var history = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, MigrationInputBytes>(
            StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var witnesses = new Dictionary<string, MigrationControlledRootWitness>(
            StringComparer.Ordinal);

        MigrationOperationJournal? journal = null;
        MigrationAuthorityView? authority = null;
        var prefix = "";
        var modernTrace = _modernObserved ||
            root.RootLockExists ||
            root.BindingInput?.Version.Exists == true;

        ProtocolFacts Result(PersistentProtocolKind kind, string? reason = null)
        {
            if (kind is PersistentProtocolKind.Modern or
                        PersistentProtocolKind.ReservedModern or
                        PersistentProtocolKind.UnverifiableModern)
                _modernObserved = true;

            return new(kind, root, mainInput, main, history, files, directories,
                witnesses, journal, authority, prefix, reason);
        }

        try
        {
            if (root.Error is { } rootError)
                return Result(modernTrace
                    ? PersistentProtocolKind.UnverifiableModern
                    : PersistentProtocolKind.Unknown, rootError);

            if (!root.ArtifactRootExists)
                return Result(modernTrace
                    ? PersistentProtocolKind.UnverifiableModern
                    : PersistentProtocolKind.Fresh,
                    modernTrace ? "migration_artifact_root_missing_with_history" : null);

            var store = new WindowsTxfMigrationVersionStore(
                _configRoot, _transactionRoot);

            mainInput = store.ReadArtifactInput("migration-manifest.json");
            if (mainInput.Version.Exists)
            {
                mainInvalid =
                    !MigrationLegacyManifestCodec.TryDecode(
                        mainInput.Bytes, out main, out _) ||
                    main is null ||
                    !IsManifestIntegrityValidCore(main, validateJournal: false);

                if (main is not null)
                    modernTrace |= main.ControlledBaseline is not null ||
                        main.ControlledLatest is not null ||
                        (main.BaselineVersions?.Count ?? 0) != 0 ||
                        main.JournalBinding is not null;
            }

            var occupied = store.ReadArtifactInput("migration-history.txt");
            if (occupied.Version.Exists)
            {
                var text = new UTF8Encoding(false, true)
                    .GetString(occupied.Bytes).TrimStart('\uFEFF');

                foreach (var line in text.Split(
                    ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = line.Trim();
                    if (!IsSafeTransactionId(id) || !history.Add(id))
                        throw new InvalidDataException(
                            "migration_protocol_history_invalid");
                }
            }

            foreach (var tree in new[]
            {
                "operations", "witnesses", "outputs",
                "resolutions", "blobs", "receipts", "legacy"
            })
                ScanProtocolTree(store, tree, files, directories);

            var sharedInput = store.ReadArtifactInput("operation-journal.json");
            if (sharedInput.Version.Exists)
                files.Add("operation-journal.json", sharedInput);

            var sharedFiles = files.Keys.Where(path =>
                path == "operation-journal.json" ||
                path.StartsWith("outputs/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("resolutions/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("receipts/", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            modernTrace |= files.Keys.Any(path =>
                !path.StartsWith("legacy/", StringComparison.OrdinalIgnoreCase));

            foreach (var path in Directory.EnumerateFileSystemEntries(
                _transactionRoot, "*", new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false
                }))
            {
                var name = Path.GetFileName(path);
                var attr = File.GetAttributes(path);

                if ((attr & (FileAttributes.ReparsePoint |
                             FileAttributes.Encrypted)) != 0)
                    throw new InvalidDataException(
                        "migration_protocol_top_entry_unsupported");

                if (name.StartsWith("snapshot-", StringComparison.OrdinalIgnoreCase))
                {
                    if ((attr & FileAttributes.Directory) == 0)
                        throw new InvalidDataException(
                            "migration_protocol_snapshot_kind_invalid");

                    directories.Add(name);
                    var separator = name.LastIndexOf('-');
                    if (separator <= "snapshot-".Length ||
                        name.Length - separator - 1 != 32 ||
                        !name[(separator + 1)..].All(Uri.IsHexDigit) ||
                        !history.Contains(name["snapshot-".Length..separator]))
                        throw new InvalidDataException(
                            "migration_protocol_snapshot_unoccupied");
                }
            }

            if (mainInvalid)
                return Result(modernTrace
                    ? PersistentProtocolKind.UnverifiableModern
                    : PersistentProtocolKind.Unknown,
                    "migration_protocol_main_invalid");

            foreach (var pair in files.Where(pair =>
                pair.Key.StartsWith("witnesses/", StringComparison.OrdinalIgnoreCase)))
            {
                var proof = ReadProtocolWitness(pair.Value, root, history);

                if (pair.Key != "witnesses/" + proof.WitnessId + ".controlled.json" ||
                    proof.WitnessId is not { Length: 32 } ||
                    !proof.WitnessId.All(Uri.IsHexDigit) ||
                    !witnesses.TryAdd(proof.WitnessId, proof))
                    throw new InvalidDataException(
                        "migration_protocol_witness_filename_invalid");
            }

            foreach (var pair in files.Where(pair =>
                pair.Key.StartsWith("legacy/", StringComparison.OrdinalIgnoreCase)))
            {
                var hash = MigrationFileVersion.Hash(pair.Value.Bytes);

                if (pair.Key != "legacy/" + hash + ".json" ||
                    !MigrationLegacyManifestCodec.TryDecode(
                        pair.Value.Bytes, out var old, out _) ||
                    old is null ||
                    old.SchemaVersion != 1 ||
                    !IsManifestIntegrityValidCore(old, validateJournal: false) ||
                    !history.Contains(old.TransactionId))
                    throw new InvalidDataException(
                        "migration_protocol_legacy_archive_invalid");
            }

            var journals = new Dictionary<string,
                (MigrationOperationJournal Journal,
                 MigrationAuthorityView View,
                 string Prefix)>(StringComparer.Ordinal);

            foreach (var path in files.Keys.Where(path =>
                path.EndsWith("/operation-journal.json",
                    StringComparison.OrdinalIgnoreCase) ||
                path == "operation-journal.json"))
            {
                var location = path == "operation-journal.json"
                    ? ""
                    : path[..^"/operation-journal.json".Length];

                if (location.Length != 0 &&
                    (location.Split('/').Length != 2 ||
                     !location.StartsWith("operations/",
                         StringComparison.OrdinalIgnoreCase) ||
                     !IsSafeTransactionId(location["operations/".Length..])))
                    throw new InvalidDataException(
                        "migration_protocol_journal_location_invalid");

                var currentJournal = new MigrationOperationJournal(
                    location.Length == 0
                        ? _transactionRoot
                        : Path.Combine(_transactionRoot,
                            location.Replace('/', Path.DirectorySeparatorChar)),
                    store, location);

                var view = currentJournal.ReadAuthority();

                if (view.JournalBytesSha256 != files[path].Version.Sha256 ||
                    view.Document.SchemaVersion != 2 ||
                    location.Length != 0 &&
                    view.Document.TransactionId != location["operations/".Length..] ||
                    !journals.TryAdd(view.Document.TransactionId,
                        (currentJournal, view, location)))
                    throw new InvalidDataException(
                        "migration_protocol_journal_identity_or_duplicate");

                ValidateProtocolJournal(view, location,
                    files, directories, witnesses, root, history);
            }

            foreach (var directory in directories.Where(path =>
                path.StartsWith("operations/", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = directory.Split('/');
                if (parts.Length < 2 ||
                    !IsSafeTransactionId(parts[1]) ||
                    !history.Contains(parts[1]))
                    throw new InvalidDataException(
                        "migration_protocol_operation_directory_unoccupied");

                var own = "operations/" + parts[1];
                if (!journals.ContainsKey(parts[1]) &&
                    (parts.Length > 2 ||
                     files.Keys.Any(path => path.StartsWith(
                         own + "/", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException(
                        "migration_protocol_orphan_operation_tree");
            }

            if (sharedFiles.Length != 0 &&
                !files.ContainsKey("operation-journal.json"))
                throw new InvalidDataException(
                    "migration_protocol_orphan_shared_tree");

            if (main is null)
            {
                if (history.Count != 0 ||
                    directories.Any(path => path.StartsWith(
                        "snapshot-", StringComparison.OrdinalIgnoreCase)) ||
                    files.Count != 0 ||
                    root.Binding?.PendingTransactionId is not null)
                    return Result(modernTrace
                        ? PersistentProtocolKind.UnverifiableModern
                        : PersistentProtocolKind.Unknown,
                        "migration_protocol_main_missing_with_history");

                if (root.Binding is not null)
                    return Result(PersistentProtocolKind.ReservedModern);

                return Result(modernTrace
                    ? PersistentProtocolKind.UnverifiableModern
                    : PersistentProtocolKind.Fresh,
                    modernTrace ? "migration_protocol_binding_missing" : null);
            }

            var ownPrefix = "operations/" + main.TransactionId;
            var ownFacts =
                files.Keys.Any(path => path.StartsWith(
                    ownPrefix + "/", StringComparison.OrdinalIgnoreCase)) ||
                directories.Any(path => path.StartsWith(
                    ownPrefix + "/", StringComparison.OrdinalIgnoreCase));

            var ownWitness = witnesses.Values.Any(witness =>
                witness.TransactionId == main.TransactionId);

            // 原 schema1/精确 archive 投影先按 legacy 来源判定；
            // 合法 legacyImport 的根 binding 本身不会把它误判为 Modern。
            if (main.SchemaVersion == 1 || main.LegacySource is not null)
            {
                if (_modernObserved ||
                    ownFacts ||
                    sharedFiles.Length != 0 ||
                    witnesses.Count != 0 ||
                    main.JournalBinding is not null ||
                    main.ControlledBaseline is not null ||
                    main.ControlledLatest is not null ||
                    main.BaselineVersions.Count != 0 ||
                    main.SchemaVersion == 2 &&
                    !IsVerifiedLegacyWithoutModernJournal(main) ||
                    root.Binding?.PendingTransactionId is { } legacyPending &&
                    legacyPending != main.TransactionId)
                    return Result(PersistentProtocolKind.Unknown,
                        "migration_protocol_legacy_mixed_or_invalid");

                return Result(PersistentProtocolKind.VerifiedLegacy);
            }

            // SchemaVersion=2、RealEffectsRequired、诊断旧写集不单独证明 Modern。
            if (!modernTrace)
                return Result(PersistentProtocolKind.Diagnostic);

            if (root.Binding is null ||
                !history.Contains(main.TransactionId) ||
                main.Stage == MigrationStage.None ||
                root.Binding.PendingTransactionId !=
                    (main.Stage is MigrationStage.Committed or MigrationStage.RolledBack
                        ? null
                        : main.TransactionId))
                return Result(PersistentProtocolKind.UnverifiableModern,
                    "migration_protocol_binding_history_or_pending_invalid");

            if (!main.BaselineCompleted)
            {
                if (!IsNoBaselineAbortShape(main) ||
                    main.SnapshotManifestHash.Length != 0 ||
                    main.Stage is not (MigrationStage.Snapshotting or
                                       MigrationStage.Blocked or
                                       MigrationStage.RolledBack) ||
                    main.Stage == MigrationStage.RolledBack &&
                    main.BlockedReason is not null ||
                    ownFacts ||
                    ownWitness ||
                    sharedFiles.Length != 0)
                    return Result(PersistentProtocolKind.UnverifiableModern,
                        "migration_protocol_no_s0_shape_invalid");

                return Result(PersistentProtocolKind.Modern);
            }

            if (ControlledBaselineProblem(main, root) is not null ||
                main.ControlledLatest is not { } latest ||
                !ValidControlledWitness(latest, main.TransactionId) ||
                !latest.ConfigIdentity.Matches(root.ConfigIdentity!) ||
                !latest.ArtifactIdentity.Matches(root.ArtifactIdentity!))
                return Result(PersistentProtocolKind.UnverifiableModern,
                    "migration_protocol_s0_or_latest_invalid");

            foreach (var witness in new[]
            {
                main.ControlledBaseline!, main.ControlledLatest!
            })
            {
                if (!witnesses.TryGetValue(witness.WitnessId, out var saved) ||
                    MigrationOperationJournal.Hash(saved) !=
                        MigrationOperationJournal.Hash(witness))
                    return Result(PersistentProtocolKind.UnverifiableModern,
                        "migration_protocol_embedded_witness_not_persisted");
            }

            if (journals.TryGetValue(main.TransactionId, out var current))
            {
                if (current.Prefix.Length == 0 && directories.Contains(ownPrefix))
                    return Result(PersistentProtocolKind.UnverifiableModern,
                        "migration_protocol_shared_journal_shadowed");

                journal = current.Journal;
                authority = current.View;
                prefix = current.Prefix;

                if (main.JournalBinding is null)
                    return Result(PersistentProtocolKind.UnverifiableModern,
                        "migration_protocol_main_journal_binding_missing");

                ValidateMainAgainstAuthority(main, authority, prefix);
            }
            else if (main.JournalBinding is not null ||
                     main.ReferenceWriteSet.Count != 0 ||
                     main.ActivationRecord is not null ||
                     ownFacts ||
                     sharedFiles.Length != 0 ||
                     main.Stage is not (MigrationStage.SnapshotReady or
                                        MigrationStage.RollingBack or
                                        MigrationStage.RolledBack or
                                        MigrationStage.Blocked))
            {
                return Result(PersistentProtocolKind.UnverifiableModern,
                    "migration_protocol_required_journal_missing");
            }

            return Result(PersistentProtocolKind.Modern);
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException and not StackOverflowException)
        {
            modernTrace |= files.Keys.Any(path =>
                !path.StartsWith("legacy/", StringComparison.OrdinalIgnoreCase));

            return Result(modernTrace
                ? PersistentProtocolKind.UnverifiableModern
                : PersistentProtocolKind.Unknown,
                "migration_protocol_facts_unverifiable:" + ex.GetType().Name);
        }
    }

    private void CheckProtocolInputStillExact(
        ProtocolFacts facts,
        MigrationInputBytes expectedMain)
    {
        var store = new WindowsTxfMigrationVersionStore(
            _configRoot, _transactionRoot);

        var currentMain = store.ReadArtifactInput("migration-manifest.json");

        if (!currentMain.Version.Matches(expectedMain.Version) ||
            !currentMain.Bytes.AsSpan().SequenceEqual(expectedMain.Bytes))
            throw new MigrationQualificationException(
                "migration_success_main_input_changed");

        // 校验已消费的协议原件，不重新读取/拼装第二份 authority。
        foreach (var pair in facts.Files)
        {
            var current = store.ReadArtifactInput(pair.Key);
            if (!current.Version.Matches(pair.Value.Version) ||
                !current.Bytes.AsSpan().SequenceEqual(pair.Value.Bytes))
                throw new MigrationQualificationException(
                    "migration_success_protocol_input_changed:" + pair.Key);
        }

        var root = MigrationRootAuthority.ObservePersistentFacts(
            _configRoot, _transactionRoot);

        if (root.Error is not null ||
            root.BindingInput is null ||
            facts.Root.BindingInput is null ||
            !root.BindingInput.Version.Matches(facts.Root.BindingInput.Version) ||
            !root.BindingInput.Bytes.AsSpan().SequenceEqual(
                facts.Root.BindingInput.Bytes))
            throw new MigrationQualificationException(
                "migration_success_root_binding_changed");
    }

    private void QualifyStrictAbort(
        MigrationManifest candidate,
        ProtocolFacts facts,
        ModernSuccessPurpose purpose)
    {
        var owned = facts.Main
            ?? throw new MigrationQualificationException(
                "migration_abort_owned_main_missing");

        var terminal = purpose is ModernSuccessPurpose.Idempotent or
                                  ModernSuccessPurpose.TerminalAdmission ||
                       owned.Stage == MigrationStage.RolledBack;

        if (!IsNoBaselineAbortShape(owned) ||
            !IsNoBaselineAbortShape(candidate) ||
            owned.SnapshotManifestHash.Length != 0 ||
            candidate.SnapshotManifestHash.Length != 0 ||
            candidate.TransactionId != owned.TransactionId ||
            candidate.SnapshotId != owned.SnapshotId ||
            candidate.SnapshotPath != owned.SnapshotPath ||
            !facts.History.Contains(owned.TransactionId) ||
            terminal &&
            (owned.Stage != MigrationStage.RolledBack ||
             owned.BlockedReason is not null) ||
            !terminal &&
            owned.Stage is not (MigrationStage.Snapshotting or MigrationStage.Blocked) ||
            facts.Root.Binding?.PendingTransactionId !=
                (terminal ? null : owned.TransactionId))
            throw new MigrationQualificationException(
                "migration_abort_shape_history_or_pending_invalid");

        var own = "operations/" + owned.TransactionId;

        if (facts.Files.Keys.Any(path =>
                path.StartsWith(own + "/", StringComparison.OrdinalIgnoreCase) ||
                path == "operation-journal.json" ||
                path.StartsWith("outputs/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("resolutions/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("receipts/", StringComparison.OrdinalIgnoreCase)) ||
            facts.Directories.Any(path =>
                path.StartsWith(own + "/", StringComparison.OrdinalIgnoreCase)) ||
            facts.Witnesses.Values.Any(witness =>
                witness.TransactionId == owned.TransactionId))
            throw new MigrationQualificationException(
                "migration_abort_orphan_protocol_facts");

        if (!string.Equals(Path.GetFullPath(owned.SnapshotPath),
            Path.GetFullPath(SnapshotPathOf(owned.TransactionId, owned.SnapshotId)),
            StringComparison.OrdinalIgnoreCase))
            throw new MigrationQualificationException(
                "migration_abort_snapshot_not_owned");

        var attributes =
            MigrationRootAuthority.ObserveAttributes(owned.SnapshotPath);

        if (attributes is { } attr &&
            ((attr & FileAttributes.Directory) == 0 ||
             (attr & (FileAttributes.ReparsePoint | FileAttributes.Encrypted)) != 0))
            throw new MigrationQualificationException(
                "migration_abort_snapshot_unsupported");

        // 部分快照保留。这里只读验证路径安全，不删除、不恢复配置。
        if (attributes is not null)
            _ = EnumerateFilesSafe(owned.SnapshotPath);
    }

    private MigrationControlledRootWitness? QualifyModernSuccess(
        MigrationManifest candidate,
        MigrationStage targetStage,
        MigrationInputBytes expectedMain,
        ProtocolFacts facts,
        ModernSuccessPurpose purpose)
    {
        try
        {
            if (facts.Kind != PersistentProtocolKind.Modern ||
                facts.Main is not { } owned ||
                facts.MainInput is null ||
                !HoldsExclusiveLock ||
                _rootAuthority is null ||
                !_rootAuthority.IsHeld ||
                facts.Root.ConfigIdentity is null ||
                facts.Root.ArtifactIdentity is null ||
                !_rootAuthority.ConfigIdentity.Matches(facts.Root.ConfigIdentity) ||
                !_rootAuthority.ArtifactIdentity.Matches(facts.Root.ArtifactIdentity))
                throw new MigrationQualificationException(
                    "migration_success_authority_or_protocol_invalid");

            if (_mainOwnership is null ||
                !_mainOwnership.Input.Version.Matches(expectedMain.Version) ||
                !_mainOwnership.Input.Bytes.AsSpan().SequenceEqual(expectedMain.Bytes) ||
                !facts.MainInput.Version.Matches(expectedMain.Version) ||
                !facts.MainInput.Bytes.AsSpan().SequenceEqual(expectedMain.Bytes) ||
                candidate.TransactionId != owned.TransactionId ||
                candidate.SnapshotId != owned.SnapshotId ||
                candidate.SnapshotPath != owned.SnapshotPath)
                throw new MigrationQualificationException(
                    "migration_success_main_not_owned");

            if (purpose != ModernSuccessPurpose.Publication &&
                (owned.Stage != targetStage || candidate.Stage != targetStage))
                throw new MigrationQualificationException(
                    "migration_success_repeated_stage_mismatch");

            if (facts.Root.Binding?.PendingTransactionId !=
                (owned.Stage is MigrationStage.Committed or MigrationStage.RolledBack
                    ? null
                    : owned.TransactionId))
                throw new MigrationQualificationException(
                    "migration_success_pending_invalid");

            if (!candidate.BaselineCompleted)
            {
                if (targetStage != MigrationStage.RolledBack)
                    throw new MigrationQualificationException(
                        "migration_success_s0_required");

                QualifyStrictAbort(candidate, facts, purpose);
                CheckProtocolInputStillExact(facts, expectedMain);
                return null;
            }

            if (targetStage is not (MigrationStage.SnapshotReady or
                                   MigrationStage.ReferenceUpdating or
                                   MigrationStage.Activated or
                                   MigrationStage.Committed or
                                   MigrationStage.RolledBack))
                throw new MigrationQualificationException(
                    "migration_success_target_stage_invalid");

            if (ControlledBaselineProblem(candidate, facts.Root) is { } baselineProblem)
                throw new MigrationQualificationException(baselineProblem);

            if (VerifySnapshotContent(candidate) is { Length: > 0 } snapshotProblem)
                throw new MigrationQualificationException(
                    "migration_success_snapshot_invalid:" + snapshotProblem);

            var before = facts.Authority;
            var chain = MigrationOperationJournal.EmptyChainDigest;
            var resolutionChain = MigrationOperationJournal.EmptyChainDigest;

            if (before is not null)
            {
                // 验证盘上 owned main 的派生关系；
                // candidate 的 Stage 正待改变，不能拿它与旧 Stage 做等值比较。
                ValidateMainAgainstAuthority(owned, before, facts.Prefix);

                if (before.Document.Operations.Any(entry =>
                    entry.State == MigrationOperationState.Prepared))
                    throw new MigrationQualificationException(
                        "migration_success_unresolved_prepared");

                if (purpose != ModernSuccessPurpose.Publication &&
                    before.Document.PendingStage is not null ||
                    targetStage == MigrationStage.SnapshotReady &&
                    before.Document.PendingStage is not null ||
                    purpose == ModernSuccessPurpose.Publication &&
                    before.Document.PendingStage is { } pending &&
                    pending != targetStage)
                    throw new MigrationQualificationException(
                        "migration_success_unresolved_pending_phase");

                if (targetStage is MigrationStage.ReferenceUpdating or
                                   MigrationStage.Activated or
                                   MigrationStage.Committed)
                {
                    var declaration = before.Document.Declaration;
                    if (declaration is null)
                        throw new MigrationQualificationException(
                            "migration_success_declaration_required");

                    var references = before.Document.Operations.Where(entry =>
                        entry.Intent.Phase == MigrationOperationPhase.Reference)
                        .ToArray();

                    if (references.Length != declaration.References.Count ||
                        references.Any(entry =>
                            entry.State != MigrationOperationState.Applied) ||
                        before.Document.Operations.Any(entry =>
                            entry.State == MigrationOperationState.Cancelled ||
                            entry.Intent.Phase is MigrationOperationPhase.Undo or
                                                  MigrationOperationPhase.Restore or
                                                  MigrationOperationPhase.DeleteAdded or
                                                  MigrationOperationPhase.RemoveOwnedDirectory))
                        throw new MigrationQualificationException(
                            "migration_success_reference_not_complete");

                    var projected = ProjectMain(candidate, before, facts.Prefix);

                    if (projected.ReferenceWriteSet.Count == 0 ||
                        targetStage is MigrationStage.Activated or MigrationStage.Committed &&
                        (projected.ActivationRecord is null ||
                         before.Document.Operations.Count(entry =>
                             entry.State == MigrationOperationState.Applied &&
                             entry.Intent.Phase == MigrationOperationPhase.Activate) != 1))
                        throw new MigrationQualificationException(
                            "migration_success_effect_evidence_incomplete");
                }

                chain = before.AppliedChainDigest;
                resolutionChain = before.ResolutionChainDigest;
            }
            else
            {
                if (candidate.JournalBinding is not null ||
                    candidate.ReferenceWriteSet.Count != 0 ||
                    candidate.ActivationRecord is not null ||
                    targetStage is not (MigrationStage.SnapshotReady or MigrationStage.RolledBack))
                    throw new MigrationQualificationException(
                        "migration_success_required_journal_missing");
            }

            if (targetStage == MigrationStage.Committed)
            {
                if (candidate.CommitMarker != candidate.TransactionId ||
                    !string.IsNullOrEmpty(candidate.BlockedReason) ||
                    !candidate.RollbackRehearsed ||
                    candidate.RehearsalScope != RehearsalScopeOf(candidate))
                    throw new MigrationQualificationException(
                        "migration_success_commit_shape_invalid");

                if (purpose == ModernSuccessPurpose.Publication &&
                    owned.Stage != MigrationStage.Committed &&
                    _requireQuiescence &&
                    (!_quietValid ||
                     _quiet is null ||
                     candidate.QuiescedAtUtc is null ||
                     candidate.QuiesceSessionId != _sessionId ||
                     candidate.QuiesceGeneration != _quietGeneration))
                    throw new MigrationQualificationException(
                        "migration_success_commit_window_invalid");
            }

            var expected = FoldControlledRoot(candidate, before);
            var baseline = candidate.ControlledBaseline!;

            if (targetStage == MigrationStage.RolledBack &&
                (expected.Count != baseline.Entries.Count ||
                 baseline.Entries.Any(entry =>
                     !expected.TryGetValue(entry.Path, out var final) ||
                     !entry.Version.Matches(final.Version))))
                throw new MigrationQualificationException(
                    "migration_success_rollback_chain_not_s0");

            var witness = MigrationControlledRootCapture.Capture(
                _configRoot, _transactionRoot, _rootAuthority,
                candidate.TransactionId, _sessionId, _quietGeneration,
                chain, resolutionChain, targetStage);

            if (witness.Entries.Count != expected.Count ||
                witness.Entries.Any(entry =>
                    !expected.TryGetValue(entry.Path, out var wanted) ||
                    !wanted.Version.Matches(entry.Version)))
                throw new MigrationQualificationException(
                    "migration_success_whole_root_differs_from_chain");

            CheckProtocolInputStillExact(facts, expectedMain);
            return witness;
        }
        catch (MigrationQualificationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw new MigrationQualificationException(
                "migration_success_unverifiable:" + ex.GetType().Name);
        }
    }

    private void AdmitPersistentMutation(
        string entryName,
        ProtocolFacts facts,
        bool allowAbsent = false)
    {
        if (facts.Kind is PersistentProtocolKind.Unknown or
                          PersistentProtocolKind.UnverifiableModern)
        {
            if (_mainOwnership is null)
                _mainAdmissionAttempted = true;

            throw new MigrationAdmissionException(
                facts.Reason ?? "migration_persistent_protocol_unverified");
        }

        if (facts.Kind == PersistentProtocolKind.Diagnostic)
        {
            if (_effects is IPreparedMigrationEffectService)
                throw new MigrationAdmissionException(
                    "migration_diagnostic_modern_conversion_unverified");
            return;
        }

        if (facts.Kind == PersistentProtocolKind.VerifiedLegacy)
        {
            if (entryName is not ("BeginTransaction" or "UpgradeLegacyManifest" or
                                 "Rollback" or "RecoverOnStart" or "MarkBlocked"))
                throw new MigrationAdmissionException(
                    "legacy_forward_evidence_unverified");
            return;
        }

        if (facts.Kind is PersistentProtocolKind.Fresh or
                          PersistentProtocolKind.ReservedModern)
        {
            if (!allowAbsent || entryName != "BeginTransaction" ||
                facts.Kind == PersistentProtocolKind.ReservedModern &&
                _effects is not IPreparedMigrationEffectService)
                throw new MigrationAdmissionException(
                    "migration_empty_protocol_entry_rejected");
            return;
        }

        if (_effects is not IPreparedMigrationEffectService ||
            !HoldsExclusiveLock ||
            _rootAuthority is null ||
            !_rootAuthority.IsHeld)
            throw new MigrationAdmissionException(
                "migration_modern_prepared_authority_required");

        if (entryName is "MarkReferenceUpdateCompleted" or "MarkActivated" or "Advance")
            throw new MigrationAdmissionException("real_side_effects_required");

        var stage = facts.Main?.Stage ?? MigrationStage.None;

        var allowed = entryName switch
        {
            "BeginTransaction" =>
                stage is MigrationStage.Committed or MigrationStage.RolledBack,
            "TakeSnapshot" =>
                stage == MigrationStage.Snapshotting,
            "RecordChanges" =>
                stage == MigrationStage.SnapshotReady,
            "ApplyReferenceUpdate" =>
                stage is MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating,
            "ActivateCandidate" =>
                stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated,
            "RehearseRollback" or "Commit" =>
                stage == MigrationStage.Activated,
            "Rollback" or "RecoverOnStart" =>
                stage is not MigrationStage.None,
            "UpgradeLegacyManifest" =>
                stage is not MigrationStage.None, // validated schema2 no-op，只读返回
            "MarkBlocked" =>
                stage is not (MigrationStage.None or MigrationStage.Committed or
                              MigrationStage.RolledBack),
            _ => false
        };

        if (!allowed)
            throw new MigrationAdmissionException(
                "migration_persistent_entry_stage_rejected:" + entryName);

        // 空 undeclared journal 只保留零 data 恢复路径。
        if (facts.Authority is { Document.Declaration: null } &&
            entryName is not ("Rollback" or "RecoverOnStart" or "MarkBlocked" or
                              "UpgradeLegacyManifest"))
            throw new MigrationAdmissionException(
                "migration_empty_journal_recovery_only");
    }

    private MigrationManifest? LoadForMutation(
        [System.Runtime.CompilerServices.CallerMemberName] string entryName = "")
    {
        var facts = ClassifyPersistentProtocol();
        AdmitPersistentMutation(entryName, facts);

        var preparedRoute =
            facts.Kind == PersistentProtocolKind.Modern ||
            facts.Kind == PersistentProtocolKind.VerifiedLegacy &&
            _effects is IPreparedMigrationEffectService;

        var manifest = preparedRoute
            ? DecodeValidatedMain(AdmitOrCheckMain(false).Bytes)
            : facts.Main;

        if (manifest?.SchemaVersion != 1)
            return manifest;

        if (!UpgradeLegacyManifest().Success)
            return null;

        facts = ClassifyPersistentProtocol();
        AdmitPersistentMutation(entryName, facts);

        return preparedRoute
            ? DecodeValidatedMain(SnapshotOwnedMain().Bytes)
            : facts.Main;
    }

    private void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            if (!IsSafeTarget(targetRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                throw new InvalidOperationException("unsafe_target:" + rel);   // 恢复目标与快照源都须安全
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var liveRoot = string.Equals(Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                _configRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            var operationRoot = liveRoot ? OperationRoot(m) : "";
            if (liveRoot && _effects is IPreparedMigrationEffectService && File.Exists(Path.Combine(operationRoot, "operation-journal.json")))
            {
                var store = new WindowsTxfMigrationVersionStore(_configRoot, operationRoot);
                var journal = new MigrationOperationJournal(operationRoot, store);
                var input = store.ReadVersion(rel);
                if (input.Sha256 != m.FileHashes[rel])
                {
                    var predecessor = journal.LastAppliedFile(rel);
                    if (predecessor is null || !predecessor.Value.Output.Matches(input))
                        throw new InvalidOperationException("rollback_restore_input_not_owned:" + rel);
                    if (journal.Load().Stage != MigrationStage.RollingBack)
                        journal.SetStage(MigrationStage.RollingBack, null);
                    var bytes = File.ReadAllBytes(source);
                    if (Sha256Hex(bytes) != m.FileHashes[rel])
                        throw new InvalidOperationException("rollback_snapshot_bytes_changed:" + rel);
                    var restored = journal.ExecutePreparedFile(rel, input, MigrationOperationPhase.Restore,
                        _ => bytes, predecessor.Value.OperationId);
                    if (!restored.Success) throw new IOException("rollback_atomic_restore_failed:" + restored.Reason);
                }
                InvokeExternal(() => _fileRestoredHook?.Invoke(rel));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
            if (string.Equals(Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                    _configRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                InvokeExternal(() => _fileRestoredHook?.Invoke(rel));   // 恢复中断注入点
        }
    }

    /// <summary>
    /// 只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。返回**失败条数**——
    /// 不安全目标或删除失败**不得静默跳过**：调用方据此保持阻断（不得报告完整回滚）。
    /// </summary>
    private int DeleteRecordedAdditions(MigrationManifest m, string targetRoot, bool requireOwnershipEvidence = false)
    {
        var failed = 0;
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeTarget(targetRoot, c.Path)) { failed++; continue; }
            var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
            if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
            var operationRoot = OperationRoot(m);
            if (requireOwnershipEvidence && _effects is IPreparedMigrationEffectService &&
                File.Exists(Path.Combine(operationRoot, "operation-journal.json")))
            {
                var store = new WindowsTxfMigrationVersionStore(_configRoot, operationRoot);
                var journal = new MigrationOperationJournal(operationRoot, store);
                var applied = journal.LastAppliedFile(c.Path);
                var input = store.ReadVersion(c.Path);
                if (applied is null || !applied.Value.Output.Matches(input)) { failed++; continue; }
                if (journal.Load().Stage != MigrationStage.RollingBack) journal.SetStage(MigrationStage.RollingBack, null);
                if (!journal.ExecutePreparedDelete(c.Path, input, applied.Value.OperationId).Success) failed++;
                continue;
            }
            if (requireOwnershipEvidence)
            {
                // **删除授权绑定「核验过的版本」（会诊第 3 轮 MUST-2）**：路径成员资格不构成删除权——
                // 预检时不存在、或随后被他方替换的文件一律保留；只有字节仍等于本事务写的（或撤销后的）期望哈希才删。
                if (!TryGetWriteSetHash(m, c.Path, out var owned)) { failed++; continue; }
                if (!TryHashConfigFileOnRoot(targetRoot, c.Path, out var current)) { failed++; continue; }
                if (!string.Equals(current, owned, StringComparison.Ordinal)) { failed++; continue; }
            }
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }

    private string? CleanupJournalDirectories(MigrationManifest manifest)
    {
        var root = OperationRoot(manifest);
        if (!File.Exists(Path.Combine(root, "operation-journal.json"))) return null;
        var store = new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot);
        var journal = OpenJournal(manifest);
        foreach (var directory in journal.AppliedDirectories())
        {
            var current = store.ReadVersion(directory.Path, MigrationEntryKind.Directory);
            if (!current.Exists) continue;
            if (!directory.Output.Matches(current)) return "rollback_directory_not_owned:" + directory.Path;
            if (journal.Load().Stage != MigrationStage.RollingBack) journal.SetStage(MigrationStage.RollingBack, null);
            var previous = journal.LastAppliedFile(directory.Path);
            var deleted = journal.ExecutePreparedDelete(directory.Path, current, previous?.OperationId);
            if (!deleted.Success) return "rollback_directory_cleanup_failed:" + directory.Path + ":" + deleted.Reason;
        }
        return null;
    }

    /// <summary>
    /// **写入前归属预检（会诊第 3 轮 MUST-1/MUST-2）**：真实写入路径下，回滚的第一个写动作（撤销激活）之前，
    /// 所有「新增」路径上**已存在**的文件、以及激活目标，其盘上字节必须等于本事务记录在写集里的期望哈希；
    /// 不存在视为「无需处理」（不得据此授予删除权）；不符即冲突 ⇒ 保留文件并阻断。
    /// </summary>
    private string? PreWriteOwnershipProblem(MigrationManifest m)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!TryHashConfigFile(c.Path, out var current, out var problem))
            {
                if (problem == "file_missing") continue;                  // 不存在 ⇒ 无需处理（也不授予删除权）
                return "rollback_addition_unverifiable:" + c.Path + ":" + problem;
            }
            var acceptedForAddition = AcceptedVersions(m, c.Path);
            if (acceptedForAddition.Count == 0) return "rollback_addition_without_ownership_evidence:" + c.Path;
            if (!acceptedForAddition.Contains(current)) return "rollback_addition_not_owned:" + c.Path;
        }
        if (m.ActivationRecord is { } activation && ActivationTargetProblem(m, activation) is { } activationProblem)
            return activationProblem;
        // Preserve specific addition/activation failure identities, then inspect every
        // other baseline target before undo or snapshot restoration writes anything.
        foreach (var baseline in m.FileHashes)
        {
            if (!TryHashConfigFile(baseline.Key, out var current, out var problem))
                return "rollback_baseline_unverifiable:" + baseline.Key + ":" + problem;
            if (_effects is IPreparedMigrationEffectService && current == baseline.Value &&
                TryGetBaselineVersion(m, baseline.Key, out var baselineVersion) &&
                !baselineVersion.Matches(new WindowsTxfMigrationVersionStore(_configRoot, _transactionRoot).ReadVersion(baseline.Key)))
                return "rollback_baseline_identity_changed:" + baseline.Key;
            if (!AcceptedVersions(m, baseline.Key).Contains(current))
            {
                var operationRoot = OperationRoot(m);
                if (!File.Exists(Path.Combine(operationRoot, "operation-journal.json")))
                    return "rollback_baseline_not_owned:" + baseline.Key;
                var store = new WindowsTxfMigrationVersionStore(_configRoot, operationRoot);
                var journal = new MigrationOperationJournal(operationRoot, store);
                if (journal.Load().TransactionId != m.TransactionId ||
                    journal.LastAppliedFile(baseline.Key) is not { } applied ||
                    !applied.Output.Matches(store.ReadVersion(baseline.Key)))
                    return "rollback_baseline_not_owned:" + baseline.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// **激活目标的归属判定（会诊第 4 轮 MUST-2／MUST-1）**：任何一次回滚（含首轮）都必须核对激活目标的字节版本。
    /// 可接受的版本集合全部**已持久化**：① 撤销后哈希（若已预测/已执行撤销）、② 写集哈希（撤销前的激活后版本）、
    /// ③ 基线哈希（快照恢复已经开始/完成）；本事务新增且已不存在 ⇒ 视为已完成清理。
    /// 他方内容（不在上述集合内）一律判为不可认领 ⇒ 阻断，绝不把他方字节登记为本事务证据。
    /// </summary>
    private string? ActivationTargetProblem(MigrationManifest m, MigrationActivationRecord activation)
    {
        var isAdded = FindChange(m, activation.Path)?.Kind == ChangeKind.Added;
        if (!TryHashConfigFile(activation.Path, out var current, out var problem))
        {
            if (problem == "file_missing")
                return isAdded ? null : "rollback_activation_target_file_missing:" + activation.Path;
            return "rollback_activation_target_" + problem + ":" + activation.Path;
        }
        if (AcceptedVersions(m, activation.Path).Contains(current)) return null;
        return "rollback_activation_target_not_owned:" + activation.Path;
    }

    /// <summary>
    /// 该路径上**已持久化**的全部可接受字节版本（第 5 轮 MUST-1）：写集期望哈希、激活后哈希、撤销后哈希、基线哈希。
    /// 全部是本事务自己写过的（或快照中的）版本；其余内容（他方字节）不在集合内 ⇒ 天然拒绝。
    /// </summary>
    private static HashSet<string> AcceptedVersions(MigrationManifest m, string rel)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetWriteSetHash(m, rel, out var writeSetHash) && !string.IsNullOrEmpty(writeSetHash)) accepted.Add(writeSetHash);
        if (m.ActivationRecord is { } a && PathKey(a.Path) == PathKey(rel))
        {
            if (!string.IsNullOrEmpty(a.AfterHash)) accepted.Add(a.AfterHash);
            if (!string.IsNullOrEmpty(a.RevertedHash)) accepted.Add(a.RevertedHash!);
        }
        if (TryGetHashCaseInsensitive(m.FileHashes, rel, out var baseline) && !string.IsNullOrEmpty(baseline))
            accepted.Add(baseline!);
        return accepted;
    }

    /// <summary>撤销激活后，把该文件的当前字节哈希持久化为写集与其撤销证据（供崩溃重入幂等判定）。</summary>
    /// <summary>
    /// **在写入之前**预测并持久化「撤销后的字节哈希」（会诊第 4 轮 MUST-1）：撤销证据先落盘，随后才执行撤销写入，
    /// 因此不存在「已写入但证据未持久化」的窗口——重入时按 ①预测哈希 ②写集哈希 ③基线哈希 三者之一即可认领。
    /// </summary>
    private bool PredictAndPersistRevertedHash(MigrationManifest m, MigrationActivationRecord activation)
    {
        var full = Path.Combine(_configRoot, NormalizePath(activation.Path).Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return true;                              // 本事务新增已被移除＝旧态，无字节证据需要
        if (!TryHashConfigFile(activation.Path, out var current, out _)) return false;
        if (!string.IsNullOrEmpty(activation.RevertedHash)
            && string.Equals(current, activation.RevertedHash, StringComparison.Ordinal)) return true;   // 已撤销 ⇒ 沿用证据
        // **输入版本绑定（第 5 轮 MUST-2）**：预测只能基于**已核验的写集版本**；盘上被锁外改动时
        // 绝不把它登记为撤销证据（否则他方内容会被写集认领并被快照覆盖）。
        // **基线版本已就位**（快照恢复已执行过该文件）⇒ 无需求解撤销证据，直接续做后续步骤
        // （会诊第 6 轮 MUST：否则基线激活目标在「恢复后中断」时会被本前置永久阻断）
        if (TryGetHashCaseInsensitive(m.FileHashes, activation.Path, out var alreadyBaseline)
            && string.Equals(current, alreadyBaseline, StringComparison.Ordinal)) return true;
        if (!TryGetWriteSetHash(m, activation.Path, out var verified)
            || !string.Equals(current, verified, StringComparison.Ordinal)) return false;
        try
        {
            BeginEffectCall();
            if (!_effects!.TryComputeStatusTransitionHash(_configRoot, activation.Path,
                    activation.AfterStatus, activation.BeforeStatus, verified, out var predicted)) return false;
            activation.RevertedHash = predicted;
            WriteManifest(m);
            return true;
        }
        catch (Exception)      // 任意异常 ⇒ 不可预测（不得跨步外泄）
        {
            return false;
        }
        finally { EndEffectCall(); }
    }

    /// <summary>归属冲突预检：真实写入路径下，任何「新增」文件若不存在或字节不等于本事务所写 ⇒ 冲突（不删除）。</summary>
    private string? OwnershipConflictReason(MigrationManifest m)
    {
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
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
    }

    /// <summary>释放实际窗口并**使资格失效**（代次前进 ⇒ 历史时间戳/会话不可复用）。</summary>
    private void ReleaseQuiescence()
    {
        try { InvokeExternal(() => _quiet?.Dispose()); } catch { }
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
            _quiet = _quiesce is null ? null : InvokeExternal(_quiesce);
            if (_quiet is null) return false;
            _quietValid = true;
            _quietGeneration++;
        }
        m.QuiescedAtUtc = InvokeExternal(_utcNow);
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
    /// <summary>
    /// **带守卫的语义读回**：回调期间置位重入守卫（monitor 可重入 ⇒ 必须显式阻断回调内重入事务），
    /// 并把**任意异常**收敛为「读取失败 + 原因」（会诊第 3 轮 IMPORTANT-5/6：读回回调与回滚后续读回都不得外泄异常）。
    /// </summary>
    private bool ReadReferenceStateGuarded(MigrationReferenceWriteTarget target, out string detail)
    {
        detail = "";
        try
        {
            BeginEffectCall();
            var ok = _effects!.TryReadReferenceState(_configRoot, target, out detail);
            if (ok && ReleaseGuardProblem() is { } released) { detail = released; return false; }
            return ok;
        }
        catch (Exception ex)
        {
            detail = "readback_exception:" + ex.GetType().Name;
            return false;
        }
        finally { EndEffectCall(); }
    }

    /// <summary>带守卫的激活状态读回（语义同上）。</summary>
    private bool ReadActivationStatusGuarded(string path, out string status, out string detail)
    {
        status = "";
        detail = "";
        try
        {
            BeginEffectCall();
            var ok = _effects!.TryReadActivationStatus(_configRoot, path, out status, out detail);
            if (ok && ReleaseGuardProblem() is { } released) { detail = released; return false; }
            return ok;
        }
        catch (Exception ex)
        {
            detail = "readback_exception:" + ex.GetType().Name;
            return false;
        }
        finally { EndEffectCall(); }
    }

    /// <summary>结束一次受守卫的端口回调：清除守卫位；仅当**最外层操作已结束**时兑现延后的 Dispose。</summary>
    private MigrationResult? NewEntryRejection()
    {
        if (_effectCallInProgress)
            return MigrationResult.Fail("reentrant_mutation_rejected", MigrationStage.None);
        if (Volatile.Read(ref _disposeRequested) != 0) return MigrationResult.Fail("disposed_instance", MigrationStage.None);
        return null;
    }

    // Poll while waiting as a callback may begin after the first lock-free check.
    // Normal unrelated callers remain serialized; callback waiters reject instead of deadlocking.
    private MutationEntry EnterMutation()
    {
        while (true)
        {
            if (NewEntryRejection() is { } rejected) return new(null, rejected);
            if (!Monitor.TryEnter(_sync, 10)) continue;
            if (NewEntryRejection() is { } insideRejected)
            {
                Monitor.Exit(_sync);
                return new(null, insideRejected);
            }
            BeginOperation();
            return new(this, null);
        }
    }

    private sealed class MutationEntry(MigrationSwitchTransaction? owner, MigrationResult? rejection) : IDisposable
    {
        private MigrationSwitchTransaction? _owner = owner;
        public MigrationResult? Rejection { get; } = rejection;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            try { current.EndOperation(); }
            finally { Monitor.Exit(current._sync); }
        }
    }


    private void EndEffectCall()
    {
        if (_callbackScopes.Count != 0)
        {
            var previous = _callbackScopes.Pop();
            CallbackOwner.Value = previous.Owner;
            _effectCallInProgress = previous.Active;
        }
        else { CallbackOwner.Value = null; _effectCallInProgress = false; }
        // Every public mutation has a MutationEntry. Its outermost EndOperation alone releases resources.
    }

    /// <summary>进入一次公开操作（与 <see cref="EndOperation"/> 成对；用 finally 保证平衡）。</summary>
    private void BeginOperation() { _operationDepth++; }

    /// <summary>退出一次公开操作；若期间被请求过 Dispose，则在深度归零时兑现。</summary>
    private void EndOperation()
    {
        if (--_operationDepth > 0) return;
        if (!_disposeDeferred && Volatile.Read(ref _disposeRequested) == 0) return;
        _disposeDeferred = false;
        ReleaseQuiescence();
        try { _lock?.Dispose(); } catch { }
        _lock = null;
        _rootAuthority?.Dispose(); _rootAuthority = null;
    }

    /// <summary>回调返回后核对：持有的锁与静止窗口仍在（回调内可能请求释放）⇒ 否则不得继续副作用与发布。</summary>
    private string? ReleaseGuardProblem()
    {
        if (!HoldsExclusiveLock) return "lock_released_during_callback";
        if (_requireQuiescence && (!_quietValid || _quiet is null)) return "quiescence_released_during_callback";
        return null;
    }

    /// <summary>当前持久化阶段（读取失败返回 None；用于副作用返回后核对未被重入改变）。</summary>
    private MigrationStage CurrentStageOrNone()
    {
        try { return LoadManifest()?.Stage ?? MigrationStage.None; }
        catch (Exception) { return MigrationStage.None; }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        if (Volatile.Read(ref _operationDepth) > 0 || _effectCallInProgress) return;
        if (!Monitor.TryEnter(_sync)) return;
        try
        {
            // 回调中途**或**操作进行中：不得释放锁/窗口（第 3/5 轮：须延后到最外层操作结束）
            if (_effectCallInProgress || _operationDepth > 0) { _disposeDeferred = true; return; }
            _disposeDeferred = false;
            ReleaseQuiescence();
            _lock?.Dispose();
            _lock = null;
            _rootAuthority?.Dispose(); _rootAuthority = null;
        }
        finally { Monitor.Exit(_sync); }
    }
}
