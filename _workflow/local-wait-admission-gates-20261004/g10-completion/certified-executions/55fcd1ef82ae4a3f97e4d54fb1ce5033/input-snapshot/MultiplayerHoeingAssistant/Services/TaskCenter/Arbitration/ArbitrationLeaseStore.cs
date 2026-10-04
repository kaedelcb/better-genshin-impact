using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 租约读取结果（§6.4）：状态 + 解析到的单文件三段（可空）+ 诊断明细。
/// </summary>
public sealed class LeaseReadResult
{
    /// <summary>五态（Absent/Valid/Expired/Corrupt/Unsupported，§6.2）。</summary>
    public ArbitrationLeaseStatus Status { get; set; }
    /// <summary>解析到的租约文件（Corrupt 时为 null，绝不降级解析）。</summary>
    public LogicalOwnerLeaseFile? File { get; set; }
    /// <summary>诊断明细（含残件留痕；只写内存，绝不落盘）。</summary>
    public string? Detail { get; set; }
    /// <summary>正式文件缺失但存在崩窗残件（§6.4：不得据此宣称没有未决动作）——持续准入约束，须先 QuarantineResidues 才允许新获取。</summary>
    public bool UncertainResidue { get; set; }
}

/// <summary>
/// 租约操作结果（§6）：成功标志 + 结构化拒绝原因码 + 变更后的所有权段（可空）。
/// </summary>
public sealed class LeaseOpResult
{
    public bool Success { get; set; }
    /// <summary>结构化拒绝原因码（corrupt/unsupported_version/held/heartbeat_advanced/lease_stale_generation/lease_expired_no_renew/switch_gate_active/residue_reconcile_pending/intent_conflict/evidence_required/evidence_phase_incompatible/evidence_mismatch/intent_not_found/illegal_transition）。</summary>
    public string? Reason { get; set; }
    /// <summary>操作后的所有权段（拒绝时 null；释放成功时亦为 null）。</summary>
    public LeaseSegment? Lease { get; set; }
}

/// <summary>R5.2 锁内原子变更结果（MutateHandoff：成功返回变更后文件快照——含新 Revision，供链式变更/断言；拒绝时文件保持不变）。</summary>
public sealed class LeaseMutateResult
{
    public bool Success { get; set; }

    /// <summary>响亮拒绝原因码（lease_stale_generation/corrupt/unsupported_version/residue_reconcile_pending/switch_gate_active/变更函数返回码）。</summary>
    public string? Reason { get; set; }

    public LogicalOwnerLeaseFile? File { get; set; }
}

/// <summary>
/// 槲寄生 · 任务中心——仲裁租约存取（R5.1 冻结稿 v5 §6，单文件三段：lease/handoff/diag）。
/// 文件：configDir/arbitration-lease.json；锁对象：固定 configDir/arbitration-lease.lock（永不原子替换/删除/清空）。
///
/// 责任边界（§6 类注释约定）：
/// - 失联责任 = BGI 执行锁 + 新所有者对账资格（本机没有组件负责取消 BGI 在跑任务）；
/// - 崩窗按件处置（每个未决意图依据关联权威证据单独消解，不整体重放）；
/// - 禁止回滚复活旧所有者资格（回滚后按 Expired/Absent 须重新获取 + 对账）；
/// - TTL 过期仅撤资格，不代表执行锁释放/任务取消/票据失效（§6.2 语义分离）。
///
/// 并发协议：所有公开操作在跨进程锁（FileStream FileShare.None）内完成「读取→判定→校验→更新→发布」全程；
/// 发布采用 UTF8 无 BOM + 临时文件 + 同目录原子替换（File.Move overwrite），finally 清理残件。
/// </summary>
public sealed class ArbitrationLeaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    /// <summary>
    /// 当前支持/写入的租约文件格式代。
    /// R5.2 §4.0：新增 Submission/Operations 字段＝格式代 2；
    /// **R5.3 §24.20-A**：责任事实字段使用格式代 **3**；发送轮次受理认领使用格式代 **4**；到期墓碑历史归档使用格式代 **5**。
    /// v5 旧消费者遇到时响亮拒绝；≤v3 含未决责任不得就地升版，避免丢失受理认领；v4 的受理认领字段可安全迁移至 v5。
    /// </summary>
    public const int SupportedVersion = 5;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _configDir;
    private readonly string _leasePath;
    private readonly string _lockPath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _monotonic;
    /// <summary>本进程最近一次所有者写入的单调时刻（§6.3：本进程存活判断用单调时间；UTC 仅诊断）。</summary>
    private TimeSpan? _lastOwnerWriteMono;

    /// <summary>
    /// 构造零副作用（§6.4）：不建目录、不建文件（首次写入才 Directory.CreateDirectory）。
    /// 租约文件 = configDir/arbitration-lease.json；固定锁文件 = configDir/arbitration-lease.lock。
    /// </summary>
    public ArbitrationLeaseStore(string configDir, Func<DateTimeOffset>? utcNow = null, Func<TimeSpan>? monotonic = null)
    {
        _configDir = configDir;
        _leasePath = Path.Combine(configDir, "arbitration-lease.json");
        _lockPath = Path.Combine(configDir, "arbitration-lease.lock");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _monotonic = monotonic ?? (() => sw.Elapsed);
    }

    // ============================================================
    // 读取（只读）
    // ============================================================

    /// <summary>
    /// 只读盘读租约（§6.4 五态）。
    /// - 正式文件不存在 → Absent（若目录存在且发现残留临时文件 → Detail 注明残件按无正式文件处理）；
    /// - 存在但 JSON 解析失败/反序列化为 null → Corrupt（原件保留留痕，绝不降级 Absent、绝不改写原件）；
    /// - Version &gt; SupportedVersion → Unsupported；v1 向后读兼容（Pending 段原样保留，Submission/Operations 视为空，缺字段保守拒绝不默认为无责任）；
    ///   Lease 段非空 → (LastHeartbeatUtc + TtlSeconds 秒) &lt; utcNow() 为 Expired 否则 Valid；
    /// - Lease 段空 → Absent。
    /// 本方法只读不写盘（残件留痕只写进返回的 Detail）；配置目录不存在时直接判 Absent（不建目录、不建文件）。
    /// </summary>
    public LeaseReadResult Read()
    {
        // **[P50 复核·批次四十九] 不得用 `Exists` 探测**（`Directory.Exists`／`File.Exists` 在任何访问错误下
        // 静默返回 false，会把「不可读/被拒」折成「目录不存在/无锁文件/无正式文件」⇒ 误判 **Absent**，
        // 而 Absent 是「可获取」的依据 ⇒ 有误判「无归属」的风险。改为**直接访问 + 异常分类**：
        // 锁文件存在 = 曾有写入者（写者先建锁再发布，锁文件永不删除）——读取也在锁内，与写者串行；
        // 锁文件不存在 = 无并发写者，直接读（`FileMode.Open` **绝不新建**文件，恪守「只读不写盘」）；
        // 争用族（共享冲突/拒绝访问）⇒ 有界重试；**预算耗尽 ⇒ Corrupt（fail-closed），绝不降级 Absent**。
        try
        {
            return WithContentionRetry(() =>
            {
                if (TryOpenLockFile(out var lockStream))
                {
                    using (lockStream) return ReadCore();
                }

                var unlocked = ReadCore();
                // **[第五轮会诊重要项处置]** 无锁快照的**首写者竞态**：`Open` 抛 NotFound 之后、快照读之前/期间，
                // 首个写者可能已建锁并发布 ⇒ 快照可能早于其发布。故**读后复核锁文件是否已出现**（复核同样
                // **不得**用 `Exists` 探测：以 `Open` 成败分类，`FileNotFound` 才算「仍无锁」）；已出现 ⇒
                // **丢弃快照**、改走锁内读取（宁可重读，也不返回可能陈旧的快照）。
                if (TryOpenLockFile(out var lateLock))
                {
                    using (lateLock) return ReadCore();
                }
                return unlocked;
            });
        }
        catch (Exception ex) when (RunStore.IsFileContention(ex))
        {
            return new LeaseReadResult
            {
                Status = ArbitrationLeaseStatus.Corrupt,
                File = null,
                Detail = "租约锁不可用（争用预算耗尽），按不可读 fail-closed 处理（不降级为 Absent）：" + ex.Message,
            };
        }

        // **[P50 复核]** 以 `Open`（**绝不新建**）成败分类「锁文件是否存在」：`FileNotFound`／`DirectoryNotFound`
        // ⇒ false（仍无锁）；其余异常（争用/拒绝访问）**原样抛出**交给有界重试，杜绝 `Exists` 的静默 false。
        bool TryOpenLockFile(out FileStream? stream)
        {
            try
            {
                stream = new FileStream(_lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                stream = null;
                return false;
            }
        }
    }

    /// <summary>盘读判定核心（不做任何写盘；调用方负责是否持锁）。</summary>
    private LeaseReadResult ReadCore()
    {
        var now = _utcNow();

        // **[P50 复核]** 同上：以**读取**取代 `File.Exists` 探测——「不存在」⇒ Absent（合法无正式文件）；
        // 「争用/拒绝访问」⇒ 有界重试后 **Corrupt（fail-closed，绝不 Absent）**；其余 IOException ⇒ 既有 Corrupt 口径。
        string text;
        try
        {
            text = WithContentionRetry(() => File.ReadAllText(_leasePath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadAbsentWithResidueProbe();   // 已确认不存在（含「探测与读取之间被移除」竞态）
        }
        catch (IOException ex) when (RunStore.IsFileContention(ex))
        {
            // 争用族预算耗尽：**不可读 ≠ 不存在** ⇒ 取与「损坏」同族的 fail-closed（调用方一律拒绝变更/接管），
            // 绝不 Absent（那会给「无归属」结论）；原件保留留痕、绝不改写。
            return new LeaseReadResult
            {
                Status = ArbitrationLeaseStatus.Corrupt,
                File = null,
                Detail = "租约文件不可读（争用预算耗尽），按 fail-closed 处理（原件保留留痕）：" + ex.Message,
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            // 同上：以 `UnauthorizedAccessException` 形态出现的「拒绝访问」（Win32 路径占用/ACL）不是 IOException 子类。
            return new LeaseReadResult
            {
                Status = ArbitrationLeaseStatus.Corrupt,
                File = null,
                Detail = "租约文件不可读（拒绝访问），按 fail-closed 处理（原件保留留痕）：" + ex.Message,
            };
        }
        catch (IOException ex)
        {
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件读取失败，原件保留留痕：" + ex.Message };
        }

        int version;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("version", out var versionEl)
                || versionEl.ValueKind != JsonValueKind.Number
                || !versionEl.TryGetInt32(out version))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 version 缺失或非法，原件保留留痕（不当作合法 v1）。" };
        }
        catch (JsonException ex)
        {
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件解析失败，原件保留留痕：" + ex.Message };
        }

        if (version > SupportedVersion)
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Unsupported, File = null, Detail = $"租约文件 version={version} 高于支持版本 {SupportedVersion}，响亮拒绝执行（不降级解析）。" };
        if (version < 1)
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = $"租约文件 version={version} 非法，原件保留留痕。" };

        // v3+ 责任段原始 JSON 预检（区分「字段缺失」与合法空值——handoff 段存在则
        // operations 与 §24.20-A′ 的追加式集合（preObservations／conflictResolutionAudits／
        // reconciledNotAcceptedEvidence）必填，缺字段不得默认为合法空责任；**既有 v3 文件缺字段＝损坏，fail-closed**）。
        if (version >= 3)
        {
            using var doc2 = JsonDocument.Parse(text);
            JsonElement handoffEl = default;
            var hasHandoff = doc2.RootElement.ValueKind == JsonValueKind.Object
                             && doc2.RootElement.TryGetProperty("handoff", out handoffEl);
            if (hasHandoff && handoffEl.ValueKind == JsonValueKind.Object
                && (!handoffEl.TryGetProperty("operations", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array
                    || !handoffEl.TryGetProperty("preObservations", out var preEl) || preEl.ValueKind != JsonValueKind.Array
                    || !handoffEl.TryGetProperty("conflictResolutionAudits", out var auditEl) || auditEl.ValueKind != JsonValueKind.Array
                    || !handoffEl.TryGetProperty("reconciledNotAcceptedEvidence", out var evEl) || evEl.ValueKind != JsonValueKind.Array))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = $"租约文件 v{version} Handoff 责任段集合缺失（Operations／PreObservations／ConflictResolutionAudits／ReconciledNotAcceptedEvidence），原件保留留痕。" };
            if (version >= 5 && hasHandoff && handoffEl.ValueKind == JsonValueKind.Object
                && (!handoffEl.TryGetProperty("archivedOperations", out var archivesEl) || archivesEl.ValueKind != JsonValueKind.Array))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 v5 Handoff 缺少 ArchivedOperations 历史归档集合，原件保留留痕。" };
            if (version >= 4 && hasHandoff && handoffEl.ValueKind == JsonValueKind.Object
                && handoffEl.TryGetProperty("operations", out var v4Ops)
                && v4Ops.ValueKind == JsonValueKind.Array
                && v4Ops.EnumerateArray().Any(op => op.ValueKind != JsonValueKind.Object || !op.TryGetProperty("acceptanceClaim", out _)))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 v4 Operation 缺少 acceptanceClaim 字段，原件保留留痕。" };
            if (version >= 5 && hasHandoff && handoffEl.ValueKind == JsonValueKind.Object
                && handoffEl.TryGetProperty("archivedOperations", out var rawArchives)
                && rawArchives.ValueKind == JsonValueKind.Array
                && rawArchives.EnumerateArray().Any(archive => archive.ValueKind != JsonValueKind.Object
                    || !archive.TryGetProperty("operation", out var archivedOperation)
                    || archivedOperation.ValueKind != JsonValueKind.Object
                    || !archivedOperation.TryGetProperty("acceptanceClaim", out _)))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 v5 历史归档 Operation 缺少 acceptanceClaim 字段，原件保留留痕。" };
        }

        LogicalOwnerLeaseFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LogicalOwnerLeaseFile>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 v1 结构解析失败，原件保留留痕：" + ex.Message };
        }
        if (file is null)
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件反序列化为空，原件保留留痕。" };

        // §24.20-A′（[R5.3 落地批次会诊阻断处置]）**有序升级第一步＝先判未决责任**：
        // 旧格式代（≤3）仍含未决责任时**禁止就地升版**：v3 写者不持久化受理认领，不能安全推断 claim 为空。
        // 必须先通过旧责任核对/隔离流程消解；拒绝时原文件保持只读。
        // 无未决责任的旧文件仍兼容读，写入时由 Publish 单点升为当前格式代。
        if (version < 4 && HasUnresolvedResponsibilityForLegacyUpgrade(file))
            return new LeaseReadResult
            {
                Status = ArbitrationLeaseStatus.Unsupported,
                File = null,
                Detail = $"租约文件 version={version} 仍含未决责任（§24.20-A′：旧写者不持久化受理认领，禁止就地升版），响亮拒绝执行。",
            };

        // Lease 段结构校验（§6.2 复核：缺失身份/零代次等非法取值 = Corrupt，不得当作可获取）。
        var lease = file.Lease;
        if (!ValidateLeaseSegment(lease, out _))
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 Lease 段结构/取值非法（缺身份或零代次等），原件保留留痕。" };

        // v2 责任段结构校验（R5.2 §4.0：v1 兼容默认值与 v2 责任完整性分开——version==2 时
        // Handoff 段存在则 Operations 必填、记录身份/枚举/唯一性非法=Corrupt，不降级为空责任）。
        if (version >= 3 && !ValidateHandoffSegment(file.Handoff, version, out var handoffDetail))
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = handoffDetail };

        // Lease 段空 → Absent；非空按 UTC 诊断性判 Expired/Valid（§6.3：接管依据另需单调观察+锁内复核，UTC 仅诊断）。
        if (lease is null)
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Absent, File = file, Detail = null };

        return new LeaseReadResult
        {
            Status = IsExpired(lease, now) ? ArbitrationLeaseStatus.Expired : ArbitrationLeaseStatus.Valid,
            File = file,
            Detail = null,
        };
    }

    // ============================================================
    // 所有权操作（§6.1/§6.2）
    // ============================================================

    /// <summary>
    /// 尝试获取租约（§6.2）。
    /// - 盘上 Corrupt → 拒 corrupt；Unsupported → 拒 unsupported_version；
    /// - Valid（未过期）：若同时提供了 observedHeartbeatSeq 与 observedOwnerKey 且锁内复核（等待期间无变化）等于当前
    ///   HeartbeatSeq 与 CurrentOwnerKey(Lease) → 视为确认失联允许接管，否则拒 held（提供了观察但已变化 → 拒 heartbeat_advanced）；
    /// - Absent/Expired（含经确认失联的 Valid）→ 获取成功：Generation=旧Lease?.Generation+1（无=1）、Revision=文件级单调+1（释放后也延续不回退）、
    ///   HeartbeatSeq=旧Lease?.HeartbeatSeq+1（无=1）、LeaseId=新 Guid "N"、OwnerEpoch=参数、AcquiredAtUtc=LastHeartbeatUtc=now、TtlSeconds=ttlSeconds；
    /// - 保留旧文件 Handoff/Diag 段不清空（§6.2 更替继承未决意图）。
    /// </summary>
    public LeaseOpResult TryAcquire(string ownerEpoch, int ttlSeconds = 15, LeaseTakeoverEvidence? evidence = null)
    {
        // 写侧参数校验（会诊二轮 P2-⑦：拒绝不得改变已有文件；空身份/非法 TTL 不得落盘成不可恢复 Corrupt）。
        if (string.IsNullOrWhiteSpace(ownerEpoch) || ttlSeconds <= 0) return Reject("invalid_request");

        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var now = _utcNow();
            var file = read.File;
            var oldLease = file?.Lease;

            // §6.4 残件保守约束：正式文件缺失但有崩窗残件 → 不得据此宣称没有未决动作，先 QuarantineResidues。
            if (oldLease is null && read.UncertainResidue) return Reject("residue_uncertain");
            // 残件已隔离但未对账：隔离≠不确定性消除，持续准入约束直到显式对账清除。
            if (file?.Diag?.ResidueReconcilePending == true) return Reject("residue_reconcile_pending");

            // §6.3：接管既有租约（Valid 或 UTC 诊断 Expired  alike）唯一依据=LeaseTakeoverObserver 单调观察满 TTL
            // 产出的证据令牌（绑定所有者键+心跳序号）+ 本锁内复核；UTC 仅诊断，不单独撤权/放权。
            if (oldLease is not null)
            {
                if (evidence is null) return Reject("held");
                var matched = evidence.HeartbeatSeq == oldLease.HeartbeatSeq
                              && evidence.TtlSeconds == oldLease.TtlSeconds
                              && string.Equals(evidence.OwnerKey, CurrentOwnerKey(oldLease), StringComparison.Ordinal);
                if (!matched) return Reject("heartbeat_advanced");
                // matched：观察满 TTL 且锁内复核无变化 → 确认失联，允许接管（继续走获取路径）。
            }

            // Absent/Expired（含经确认失联的 Valid）→ 获取成功。
            var target = file ?? new LogicalOwnerLeaseFile { Version = SupportedVersion };
            var newLease = new LeaseSegment
            {
                LeaseId = Guid.NewGuid().ToString("N"),
                OwnerEpoch = ownerEpoch,
                // 世代取自文件级 LastGeneration（释放清空 Lease 段后仍单调延续，禁止回退复活旧代次）。
                Generation = Math.Max(oldLease?.Generation ?? 0, file?.LastGeneration ?? 0) + 1,
                HeartbeatSeq = (oldLease?.HeartbeatSeq ?? 0) + 1,
                AcquiredAtUtc = now,
                LastHeartbeatUtc = now,
                TtlSeconds = ttlSeconds,
            };
            // 文件级单调修订（§6.1：每次写入+1，释放清空 Lease 段后仍延续、禁止回退）。
            target.Revision = (file?.Revision ?? 0) + 1;
            target.Lease = newLease;
            target.LastGeneration = newLease.Generation;
            // §6.2：更替继承未决意图——只替换所有权部分，Handoff/Diag 段原样保留不清空。
            Publish(target);
            _lastOwnerWriteMono = _monotonic(); // 所有者写入单调基线（§6.3；四轮 P1-②：发布成功后才刷新，写失败窗口不得续命）
            return Ok(newLease);
        });
    }

    /// <summary>
    /// 尝试续期（§6.1）。Lease 空或 LeaseId/OwnerEpoch/Revision 任一不符 → 拒 lease_stale_generation；
    /// 已过期 → 拒 lease_expired_no_renew（§6.1 过期不得续期复活，只能重新获取）；
    /// 通过 → HeartbeatSeq+1、Revision+1、LastHeartbeatUtc=now。
    /// </summary>
    public LeaseOpResult TryRenew(string leaseId, string ownerEpoch, long expectedRevision)
        => TryRenewCore(leaseId, ownerEpoch, expectedRevision);

    /// <summary>
    /// 尝试续期（修订号「最新」加法变体，B2-β 并发实证）：与 <see cref="TryRenew"/> 同一锁内核与同一身份/TTL 校验，
    /// 唯一差别=不要求调用方提供 expectedRevision（锁内就地取最新修订号，即**跳过调用方快照 CAS**）。
    /// 两者均不含残件/切换闸门检查（与冻结语义一致）。
    /// 动机：心跳调用方「Read() 取修订号 → TryRenew」之间存在 TOCTOU——并发方任何一次成功写入都会让捕获的修订号
    /// 失效而被误判 lease_stale_generation；续期是所有权存续的主要手段（任何成功的所有者写入同样刷新心跳与
    /// 单调基线），续期误判累积到 TTL 即失去租约（比其它写入点更严重）。
    /// </summary>
    public LeaseOpResult TryRenewLatest(string leaseId, string ownerEpoch)
        => TryRenewCore(leaseId, ownerEpoch, null);

    private LeaseOpResult TryRenewCore(string leaseId, string ownerEpoch, long? expectedRevision)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || (expectedRevision is { } expected && read.File!.Revision != expected))
                return Reject("lease_stale_generation");

            if (IsOwnerExpired(lease)) return Reject("lease_expired_no_renew"); // 单调判定（§6.3：UTC 仅诊断）

            lease.HeartbeatSeq += 1;
            read.File.Revision += 1;
            lease.LastHeartbeatUtc = _utcNow();
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 续期成功刷新单调基线（三轮 P1-①；四轮 P1-②：发布成功后才刷新）
            return Ok(lease);
        });
    }

    /// <summary>
    /// 尝试释放（§6.1）。三连校验同 TryRenew（过期同样拒 lease_stale_generation）；
    /// 通过 → Lease=null（保留 Handoff/Diag）、Revision+1、不刷 HeartbeatSeq。
    /// </summary>
    public LeaseOpResult TryRelease(string leaseId, string ownerEpoch, long expectedRevision)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");

            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || read.File!.Revision != expectedRevision)
                return Reject("lease_stale_generation");

            // §6.1：过期的租约同样不得凭旧资格释放（只能重新获取 + 对账）；单调判定（UTC 仅诊断）。
            if (IsOwnerExpired(lease)) return Reject("lease_stale_generation");

            // 文件级修订单调延续（§6.1：释放清空 Lease 段后 revision 不回退），Handoff/Diag 原样保留，不刷 HeartbeatSeq。
            read.File.Revision += 1;
            read.File.Lease = null;
            Publish(read.File);
            _lastOwnerWriteMono = null; // 释放后本进程不再是所有者（基线清除；四轮 P1-②：发布成功后才清除）
            return Ok(null);
        });
    }

    /// <summary>
    /// 发布未决交接意图（§6.2 原子准入边界：意图先在跨进程锁内持久化再发送）。
    /// - 三连校验失败或已过期 → 拒 lease_stale_generation（§6.2 过期即禁启）；
    /// - Diag?.SwitchGateActive == true → 拒 switch_gate_active（§7 切换闸门）；
    /// - 已有 Pending：ActionId 且 SuspendedRunIdentity/AuthorizedPreemptor/TargetEpoch/Phase/RestoreBranch/SubmissionIdentity 全等 → 幂等成功不改写；
    ///   否则拒 intent_conflict（同所有者并发不覆盖未决意图）；
    /// - 通过 → Handoff ??= new LeaseHandoffSegment()、Handoff.Pending=intent（先置 intent.RecordedAtUtc=now）、Revision+1、HeartbeatSeq+1。
    /// </summary>
    public LeaseOpResult TryPublishIntent(string leaseId, string ownerEpoch, long expectedRevision, PendingHandoffIntent intent)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var now = _utcNow();
            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || read.File!.Revision != expectedRevision
                || IsOwnerExpired(lease))
                return Reject("lease_stale_generation"); // 过期即禁启（单调判定，UTC 仅诊断）

            // 残件对账约束持续阻断发布（三轮 P1-③：不只阻断获取）。
            if (read.File.Diag?.ResidueReconcilePending == true) return Reject("residue_reconcile_pending");

            // 同所有者同意图重复发布=幂等成功不改写（既有事实不因重放改变——与未决 Submission 并存亦成立，I4 复核）。
            var existing = read.File.Handoff?.Pending;
            if (existing is not null
                && string.Equals(existing.ActionId, intent.ActionId, StringComparison.Ordinal)
                && SameIntent(existing, intent))
                return Ok(lease);

            // R5.2 §4.1 组合约束（反向）：未决 Submission 存续期不得发布新交接意图
            // （不确定期不新增抢占意图；正常顺序=Pending 先于授权方提交，§4.1 双字段组合约束覆盖两种写入顺序）。
            if (read.File.Handoff?.Submission is not null) return Reject("submission_unresolved");

            // 写侧校验：关联字段非空（空关联身份会使消解端证据关联失效，P1-③复核）。
            if (string.IsNullOrWhiteSpace(intent.ActionId)
                || string.IsNullOrWhiteSpace(intent.SubmissionIdentity)
                || string.IsNullOrWhiteSpace(intent.TargetEpoch))
                return Reject("invalid_request");

            // §7 切换闸门：激活期间租约锁内新意图发布一律拒绝（幂等重放不产生新事实，不受闸门阻断）。
            if (read.File!.Diag?.SwitchGateActive == true) return Reject("switch_gate_active");

            var handoff = read.File.Handoff ??= new LeaseHandoffSegment();
            if (handoff.Pending is not null) return Reject("intent_conflict"); // 异意图并发冲突（不覆盖未决意图）

            intent.RecordedAtUtc = now;
            handoff.Pending = intent;
            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now; // 所有者写入刷新心跳（§6.1：TTL 与心跳保持一致）
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 四轮 P1-②：发布成功后才刷新
            return Ok(lease);
        });
    }

    /// <summary>
    /// 消解未决意图（§6.2：消解必须基于关联的权威证据）。
    /// - 三连校验失败 → 拒 lease_stale_generation；
    /// - !authoritativeEvidence → 拒 evidence_required（查询未命中/超时不能单独消解）；
    /// - Pending 空或 ActionId 不符 → 拒 intent_not_found；
    /// - 通过 → Pending=null、Revision+1、HeartbeatSeq+1。
    /// </summary>
    public LeaseOpResult TryResolveIntent(string leaseId, string ownerEpoch, long expectedRevision, IntentResolveEvidence? evidence)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var now = _utcNow();
            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || read.File!.Revision != expectedRevision
                || IsOwnerExpired(lease))
                return Reject("lease_stale_generation"); // 过期主体不得以有效所有者身份消解/续命

            // §6.2：消解必须基于关联的权威证据——空证据/未知类事实（查询未命中/超时/result_unknown）不能单独消解；
            // 可消解事实白名单=权威退出四词（§4.1 唯一引用点）+ settle 协议事实（restore_confirmed/protocol_ended）。
            // 错误码优先级（五轮建议项明确）：证据本身不合格先于 Pending 存在性判定——
            // 无 Pending 且证据为空/非白名单时返回 evidence_required（而非 intent_not_found）。
            if (evidence is null || !IsResolvableFact(evidence.ObservedFact)) return Reject("evidence_required");

            // 三轮 P1-④：事实与交接阶段相容（§4.1a 恢复责任）——RestorePending 只认 restore_confirmed，
            // SettlePending 只认 protocol_ended，抢占方终态词在恢复待确认期间不得清除持久化责任。
            var pending = read.File.Handoff?.Pending;
            if (pending is null || !string.Equals(pending.ActionId, evidence.ActionId, StringComparison.Ordinal))
                return Reject("intent_not_found");

            // 四轮 P1-①：ReconcilePending/未知阶段一律拒绝消解——保守待对账期间任何普通终词不得清除持久化责任。
            var compatible = pending.Phase switch
            {
                HandoffPhase.PreemptRequested or HandoffPhase.Confirming
                    => HandoffTransitionPolicy.IsAuthoritativeExitWord(evidence.ObservedFact),
                HandoffPhase.SettlePending => evidence.ObservedFact == "protocol_ended",
                HandoffPhase.RestorePending => evidence.ObservedFact == "restore_confirmed",
                _ => false,
            };
            if (!compatible) return Reject("evidence_phase_incompatible");

            // 关联校验：证据须对应本意图的提交身份与目标 epoch（旧 epoch 迟到事实不构成证据）。
            if (!string.Equals(evidence.SubmissionIdentity, pending.SubmissionIdentity, StringComparison.Ordinal)
                || !string.Equals(evidence.Epoch, pending.TargetEpoch, StringComparison.Ordinal))
                return Reject("evidence_mismatch");

            read.File.Handoff!.Pending = null;
            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now;
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 四轮 P1-②：发布成功后才刷新
            return Ok(lease);
        });
    }

    // ============================================================
    // 诊断/控制面写入（§6.1/§7，不续命）
    // ============================================================

    /// <summary>
    /// 置切换闸门（§7，控制面持久化：崩溃重启恢复闸门状态，不复用旧通过结果）。
    /// 只写 Diag 段（SwitchGateActive/SwitchGateReason）、Revision+1、不刷 HeartbeatSeq（§6.1 非所有者/控制面写入不续命）。
    /// </summary>
    public LeaseOpResult SetSwitchGate(bool active, string? reason)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            // §6.4：残件不确定期间禁止凭空建正式文件（否则后续读取不再检出残件）。
            if (read.File is null && read.UncertainResidue) return Reject("residue_uncertain");
            var file = read.File ?? new LogicalOwnerLeaseFile { Version = 1 };
            file.Diag ??= new LeaseDiagSegment();
            file.Diag.SwitchGateActive = active;
            file.Diag.SwitchGateReason = reason;
            // §6.1：diag 写入刷新 revision，但不刷新 heartbeatSeq。
            file.Revision += 1;
            Publish(file);
            return Ok(file.Lease);
        });
    }

    /// <summary>
    /// 记录诊断留痕（§6.1）。只写 Diag 段（Notes.Add(note)）、Revision+1、不刷 HeartbeatSeq。
    /// </summary>
    public LeaseOpResult RecordDiagNote(string note)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            if (read.File is null && read.UncertainResidue) return Reject("residue_uncertain");
            var file = read.File ?? new LogicalOwnerLeaseFile { Version = 1 };
            file.Diag ??= new LeaseDiagSegment();
            file.Diag.Notes ??= [];
            file.Diag.Notes.Add(note);
            file.Revision += 1;
            Publish(file);
            return Ok(file.Lease);
        });
    }

    // ============================================================
    // 所有者键
    // ============================================================

    /// <summary>所有者键（§6.2 失联复核）：LeaseId | OwnerEpoch | Generation，Observe 侧的稳定比较对象。</summary>
    public static string CurrentOwnerKey(LeaseSegment l)
        => l.LeaseId + "|" + l.OwnerEpoch + "|" + l.Generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 崩窗残件隔离（§6.4 按件处置）：把残留临时件移入 _backup 留痕（字节保留可查），
    /// 隔离后残件不确定性解除，才允许新获取——禁止直接删除、禁止据此宣称无未决动作。
    /// </summary>
    public LeaseOpResult QuarantineResidues()
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            // **[第五轮会诊重要项处置]** 枚举/建目录/**逐个迁件**各自有界重试（逐点，不整段重放）：
            // 迁移是**幂等可控**的（每个残件一次 `File.Move`；成功即不再重试该件）。
            var remnants = WithContentionRetry(() => Directory.EnumerateFiles(_configDir)
                .Where(p => IsLeaseResidueFileName(Path.GetFileName(p)))
                .ToList());
            if (remnants.Count == 0) return Ok(read.File?.Lease);

            // 崩窗保守序（会诊二轮 P1-④）：先持久化不确定标记——隔离完成前崩溃，重启仍见标记，
            // 备份目录不能成为绕过约束的通道；隔离≠对账完成，准入约束持续到 ClearResidueUncertainty。
            var file = read.File ?? new LogicalOwnerLeaseFile { Version = 1 };
            file.Diag ??= new LeaseDiagSegment();
            file.Diag.Notes ??= [];
            file.Diag.ResidueReconcilePending = true;
            file.Revision += 1; // 文件级修订单调（diag 写入亦递增，不刷 HeartbeatSeq）
            Publish(file);

            var backupDir = Path.Combine(_configDir, "_backup");
            WithContentionRetry(() => Directory.CreateDirectory(backupDir));
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
            foreach (var remnant in remnants)
            {
                // 不覆盖同名历史留痕：目标名追加单调时间戳后缀。
                WithContentionRetry(() => File.Move(
                    remnant, Path.Combine(backupDir, Path.GetFileName(remnant) + "." + stamp), overwrite: false));
            }

            file.Diag.Notes.Add($"崩窗残件隔离留痕（{remnants.Count} 件移入 _backup，未决不确定性保持）：{string.Join("，", remnants.Select(Path.GetFileName))}");
            file.Revision += 1;
            Publish(file);
            return Ok(file.Lease);
        });
    }

    /// <summary>
    /// 残件不确定性对账清除（§6.4）：仅在关联证据完成对账后由调用方显式清除——
    /// reconciliationEvidenceNote 必须非空（引用对账证据：对账结果/人工确认记录），空=拒 evidence_required。
    /// </summary>
    public LeaseOpResult ClearResidueUncertainty(string reconciliationEvidenceNote)
    {
        if (string.IsNullOrWhiteSpace(reconciliationEvidenceNote)) return Reject("evidence_required");
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var file = read.File ?? new LogicalOwnerLeaseFile { Version = 1 };
            file.Diag ??= new LeaseDiagSegment();
            if (!file.Diag.ResidueReconcilePending) return Ok(file.Lease); // 幂等
            file.Diag.ResidueReconcilePending = false;
            file.Diag.Notes ??= [];
            file.Diag.Notes.Add("残件不确定性对账清除：" + reconciliationEvidenceNote);
            file.Revision += 1;
            Publish(file);
            return Ok(file.Lease);
        });
    }

    // ============================================================
    // 内部辅助
    /// <summary>
    /// 未决意图阶段推进（四轮 P1-③：持久化闭环入口——SettlePending→RestorePending 等转换必须落盘，不允许只存内存）。
    /// - 三连校验失败或已过期 → 拒 lease_stale_generation；
    /// - 残件对账未完成 → 拒 residue_reconcile_pending（与 TryPublishIntent 同级约束）；
    /// - 无 Pending 或 ActionId 不匹配 → 拒 intent_not_found；
    /// - 合法转换（唯一判定=HandoffTransitionPolicy.IsLegalPhaseAdvance）：PreemptRequested→Confirming、
    ///   PreemptRequested/Confirming→SettlePending、SettlePending→RestorePending、任意阶段→ReconcilePending、
    ///   同阶段幂等；ReconcilePending 退出按持久化 ReconcileFromPhase 视同原责任阶段校验（恢复责任不可降级）；
    ///   其余 → 拒 illegal_transition；
    /// - 通过 → Pending.Phase=target、Revision+1、HeartbeatSeq+1、LastHeartbeatUtc=now，发布成功后刷新单调基线。
    /// </summary>
    public LeaseOpResult TryAdvanceIntentPhase(string leaseId, string ownerEpoch, long expectedRevision, string actionId, HandoffPhase targetPhase)
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var now = _utcNow();
            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || read.File!.Revision != expectedRevision
                || IsOwnerExpired(lease))
                return Reject("lease_stale_generation");

            if (read.File.Diag?.ResidueReconcilePending == true) return Reject("residue_reconcile_pending");

            var pending = read.File.Handoff?.Pending;
            if (pending is null || !string.Equals(pending.ActionId, actionId, StringComparison.Ordinal))
                return Reject("intent_not_found");

            // 五轮 P1-①②：合法性引用 HandoffTransitionPolicy.IsLegalPhaseAdvance 统一定义点（不另立白名单）。
            // ReconcilePending 退出按持久化的 ReconcileFromPhase 校验（视同从原责任阶段出发）——
            // 禁止 RestorePending/SettlePending 经待对账降级为可凭普通终词消解的阶段（恢复责任不可降级）。
            var legal = pending.Phase == HandoffPhase.ReconcilePending && targetPhase != HandoffPhase.ReconcilePending
                ? pending.ReconcileFromPhase is { } fromPhase && HandoffTransitionPolicy.IsLegalPhaseAdvance(fromPhase, targetPhase)
                : HandoffTransitionPolicy.IsLegalPhaseAdvance(pending.Phase, targetPhase);
            if (!legal) return Reject("illegal_transition");

            // 进入待对账：保留原责任阶段（已在待对账则保留既有记录，不覆盖）；退出待对账：清除记录。
            if (targetPhase == HandoffPhase.ReconcilePending)
            {
                if (pending.Phase != HandoffPhase.ReconcilePending) pending.ReconcileFromPhase = pending.Phase;
            }
            else
            {
                pending.ReconcileFromPhase = null;
            }
            pending.Phase = targetPhase;
            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now;
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 四轮 P1-②：发布成功后才刷新
            return Ok(lease);
        });
    }

    // ============================================================
    // R5.2 统一原子变更点（接线设计稿 v8 §4.2：Submission/Operations 一切变更=租约锁内一次原子发布）
    // ============================================================

    /// <summary>
    /// 锁内原子变更（R5.2 唯一入口）：所有者四连校验（leaseId+ownerEpoch+expectedRevision+TTL 未过期且属当前所有者）
    /// → 残件对账闸门 →（可选）切换闸门 → 变更函数（返回 null=提交变更；非 null=响亮拒绝原因、文件保持不变）
    /// → Revision+1/HeartbeatSeq+1/LastHeartbeatUtc → 原子发布。
    /// 变更函数在本店跨进程锁内同步执行=权威串行边界（§4.1a 容量检查/清理迁移/新登记同边界成立）；
    /// 锁内只消费本地事实（I-3：变更函数不得做远端网络查询）。
    /// </summary>
    public LeaseMutateResult MutateHandoff(string leaseId, string ownerEpoch, long expectedRevision, Func<LogicalOwnerLeaseFile, string?> mutate, bool checkSwitchGate = false)
        => MutateCore(leaseId, ownerEpoch, expectedRevision, mutate, checkSwitchGate);

    /// <summary>
    /// 锁内原子变更（修订号「最新」加法变体，B2-β 并发实证）：与 <see cref="MutateHandoff"/> 同一边界，
    /// **区别在于跳过调用方快照 CAS**——不接受 expectedRevision、修订号在锁内就地读取；身份
    /// （leaseId+ownerEpoch）、单调 TTL、残件闸门与可选切换闸门仍逐次核验，修订号单调发布语义不变。
    /// 正确性由「锁内取最新修订号 + 变更回调内的业务复核」共同承担，而非调用方快照比对——调用点必须保证
    /// 回调内自带足够的业务关联校验（例：待对账迁移校验 submissionIdentity+sendSeq）。
    /// 动机：调用方「Read() 取修订号 → MutateHandoff」之间存在 TOCTOU 窗口——并发方任何一次成功写入
    /// （心跳续期/同胞登记/对账/终局化/迁移清理）都会使捕获的修订号失效，被误判 lease_stale_generation；
    /// 恢复路径该码映射为终局拒绝，等于把并发下的合法用户请求假失败。变更回调本身在锁内对活动 file 做全部
    /// 校验（回调=权威判定），故锁内取最新修订号不改变身份/TTL 校验与单调发布语义——**改变的是快照 CAS 责任**：
    /// 由调用方比对转为「锁内最新修订号 + 回调业务复核」。仅当调用方刻意要「基于旧快照裁决」时才应使用带
    /// expectedRevision 的冻结签名。
    /// </summary>
    public LeaseMutateResult MutateHandoffLatest(string leaseId, string ownerEpoch, Func<LogicalOwnerLeaseFile, string?> mutate, bool checkSwitchGate = false)
        => MutateCore(leaseId, ownerEpoch, null, mutate, checkSwitchGate);

    /// <summary>统一锁内核（expectedRevision=null 表示锁内取最新修订号；其余校验两变体完全一致）。</summary>
    private LeaseMutateResult MutateCore(string leaseId, string ownerEpoch, long? expectedRevision, Func<LogicalOwnerLeaseFile, string?> mutate, bool checkSwitchGate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return MutateReject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return MutateReject("unsupported_version");

            var now = _utcNow();
            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || (expectedRevision is { } expected && read.File!.Revision != expected)
                || IsOwnerExpired(lease))
                return MutateReject("lease_stale_generation");

            if (read.File.Diag?.ResidueReconcilePending == true) return MutateReject("residue_reconcile_pending");
            if (checkSwitchGate && read.File.Diag?.SwitchGateActive == true) return MutateReject("switch_gate_active");

            var reason = mutate(read.File);
            if (reason is not null) return MutateReject(reason);

            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now;
            // Validate the exact candidate that would be published. Read-side validation alone is too late:
            // it would let a successful mutation replace a valid lease with a file that every later reader
            // classifies as Corrupt. The in-memory candidate is discarded on rejection, leaving disk intact.
            if (!ValidateLeaseSegment(read.File.Lease, out _)
                || !ValidateHandoffSegment(read.File.Handoff, SupportedVersion, out _))
                return MutateReject("invalid_mutation_state");
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 一律 Publish 成功后刷新（R5.1 四轮 P1-② 纪律延伸）
            return new LeaseMutateResult { Success = true, Reason = null, File = read.File };
        });
    }

    private static LeaseMutateResult MutateReject(string reason) => new() { Success = false, Reason = reason, File = null };

    // ============================================================

    /// <summary>
    /// **[P50 复核·批次四十九]** 「正式文件已确认不存在」的 Absent 结果（含**诊断性**残件留痕探测）：
    /// 状态**只**由「正式文件不存在」决定（到达此处的必要条件＝读取抛 `FileNotFoundException`／
    /// `DirectoryNotFoundException`，即目录可读或不存在）。
    /// **[第五轮会诊阻断项处置]** 残件探测**失败**（拒绝访问/争用耗尽）**不得**折成「无残件」：
    /// `TryAcquire` 只在 `UncertainResidue == true` 时拒 `residue_uncertain` ⇒ 折成 false 是 **fail-open**
    /// （未能排除崩窗残件却仍可获取新租约，绕过 `QuarantineResidues`）。故此时按**存在未决残件**保守处理
    /// （`UncertainResidue = true` ＋ 明细说明「残件目录不可枚举」），须人工/隔离对账后方可获取。
    /// </summary>
    private LeaseReadResult ReadAbsentWithResidueProbe()
    {
        string? detail = null;
        var residueUnknown = false;
        try
        {
            var residue = Directory.EnumerateFiles(_configDir)
                .Where(p => IsLeaseResidueFileName(Path.GetFileName(p)))
                .ToList();
            if (residue.Count > 0)
                detail = "正式文件不存在；发现残留临时文件（残件按无正式文件处理，不采用）："
                    + string.Join("，", residue.Select(Path.GetFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // **[第五轮会诊阻断项]** 不可枚举 ⇒ **不能排除残件**：按存在未决残件保守处理（fail-closed）。
            residueUnknown = true;
            detail = "正式文件不存在且残件目录不可枚举（**按存在未决残件保守处理**，须隔离/对账后方可获取）：" + ex.Message;
        }
        return new LeaseReadResult
        {
            Status = ArbitrationLeaseStatus.Absent,
            File = null,
            Detail = detail,
            UncertainResidue = detail is not null || residueUnknown,
        };
    }

    /// <summary>
    /// 跨进程锁内执行「读取→判定→校验→更新→发布」全程。
    /// 首次写入才建目录；锁对象固定 arbitration-lease.lock，OpenOrCreate 打开后永不替换/删除/清空。
    /// </summary>
    private T WithLock<T>(Func<LeaseReadResult, T> action)
    {
        // **[P50 复核·批次四十九]** 建目录也纳入争用重试（原先在重试边界之外抛出 ⇒ 与「全部文件访问点
        // 覆盖争用族」不符）。
        WithContentionRetry(() => Directory.CreateDirectory(_configDir)); // §6.4：构造零副作用，首次写入才建目录
        // **[第五轮会诊重要项处置]** 有界重试**只包围「取锁句柄」**——重试的对象是「瞬时拿不到锁」这一碰撞；
        // 持锁后的「读取→业务回调→发布」**不再整段重放**（否则发布/枚举抛争用时会把 `mutate` 回调、身份生成、
        // 残件迁移**重复执行**）。持锁后的各文件访问点各自有界重试（`ReadCore`／`Publish`／`QuarantineResidues`）。
        using var lockStream = WithContentionRetry(
            () => new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        var read = ReadCore();
        return action(read);
    }

    /// <summary>
    /// 锁争用有界重试（R5.2 B2-α 实证落地）：FileShare.None 跨进程/跨实例互斥下瞬时碰撞属预期并发形态——
    /// 单次操作持锁极短，碰撞方有界重试（80×15ms≈1.2s 预算）即可随持锁方释放收敛。
    /// **[第五轮会诊重要项处置·注意]** 现调用方**只把它用于「取锁句柄」（及各访问点单点重试）**，
    /// **不再**把整段「读取→判定→更新→发布」包进重试边界——否则争用时业务回调/身份生成/残件迁移会被
    /// **重复执行**（旧注释曾称整段重试「安全」，该表述**已作废**，勿据此把事务重新包回重试）。
    /// **[P50 根因修复·批次四十九]** 争用族**统一取自 `RunStore.IsFileContention`**（`UnauthorizedAccessException` ∪
    /// `IOException` 且非 `FileNotFoundException`/`DirectoryNotFoundException`）——Windows 上「路径被占用/拒绝访问」
    /// 与共享冲突同属瞬时争用；**「不存在」不是争用**（等多久也不会出现，纳入只会白烧预算）。
    /// 其他异常（编程错误/损坏）不掩饰、响亮抛出；**预算耗尽后原样上抛＝响亮失败不静默**。
    /// </summary>
    private static T WithLockContentionRetry<T>(Func<T> action)
    {
        const int maxAttempts = 80;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (RunStore.IsFileContention(ex) && attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(15);
            }
        }
    }

    /// <summary>
    /// **[P50 复核·批次四十九]** 单次文件访问的有界争用重试（与锁窗口**同预算同口径**）：
    /// 用于锁窗口之外的访问点（读正文、建目录、清临时件）——使「瞬时争用」不再被误判为「损坏」。
    /// 预算耗尽后**原样抛出**，由各调用点按既有 fail-closed 语义归类（读正文 ⇒ `Corrupt`；发布路径 ⇒ 响亮）。
    /// </summary>
    private static T WithContentionRetry<T>(Func<T> action) => WithLockContentionRetry(action);

    /// <summary>**有界争用重试（无返回值重载）**：建目录／写临时件／原子替换／残件迁移等无返回值的访问点。</summary>
    private static void WithContentionRetry(Action action)
        => WithContentionRetry<object?>(() => { action(); return null; });

    /// <summary>原子发布：UTF8 无 BOM + 临时文件（同目录 ".guid.tmp"）→ 同目录原子替换（overwrite），finally 清残件。</summary>
    private void Publish(LogicalOwnerLeaseFile file)
    {
        file.Version = SupportedVersion; // v5：发布必须保留受理认领字段、历史归档与校验合同
        var detail = !ValidateLeaseSegment(file.Lease, out var leaseDetail)
            ? leaseDetail
            : !ValidateHandoffSegment(file.Handoff, SupportedVersion, out var handoffDetail)
                ? handoffDetail
                : null;
        if (detail is not null)
            throw new InvalidOperationException("拒绝发布无效租约候选状态：" + detail);
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(file, JsonOptions));
        var tmp = Path.Combine(_configDir, ".lease-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // **[第五轮会诊重要项处置]** 发布内各文件访问点**逐点**有界重试（不再靠整段事务重放 ⇒
            // 业务回调不会被重复执行）；预算耗尽**原样抛出**＝响亮失败。
            WithContentionRetry(() => File.WriteAllBytes(tmp, bytes));
            WithContentionRetry(() => File.Move(tmp, _leasePath, overwrite: true));
        }
        finally
        {
            // **[P50 复核·批次四十九]** 清残件是**尽力而为**（发布已成功 ⇒ 主流程结果不受清理失败影响），
            // 且**不得**用 `File.Exists` 探测（拒绝访问会被折成「无残件」而跳过清理）。残留 `.tmp` 由既有
            // **残件语义**保守承接：`IsLeaseResidueFileName` ⇒ `UncertainResidue` 约束（未隔离前禁止新获取）
            // ＋ `QuarantineResidues` 留痕，故这里吞掉清理异常不构成「静默放行」。
            try
            {
                File.Delete(tmp);   // 不存在时 `File.Delete` 本身即无操作（幂等）
            }
            catch (Exception)
            {
                // best-effort：残留 .tmp 由残件语义保守承接（保守方向，不误判「无未决动作」）。
            }
        }
    }

    /// <summary>UTC 诊断性 TTL 判定（仅用于 Read 状态展示；§6.3：UTC 仅诊断，不作资格裁决依据）。</summary>
    private static bool IsExpired(LeaseSegment lease, DateTimeOffset now)
        => lease.LastHeartbeatUtc.AddSeconds(lease.TtlSeconds) < now;

    /// <summary>v2 责任段结构校验：Submission 完整身份/枚举、Operations 必填身份与状态枚举合法、RequestIdentity 唯一。</summary>
    private static bool ValidateHandoffSegment(LeaseHandoffSegment? handoff, int version, out string detail)
    {
        detail = "";
        if (handoff is null) return true;
        if (handoff.Operations is null
            || handoff.ArchivedOperations is null
            || (version >= 3 && (handoff.PreObservations is null
                || handoff.ConflictResolutionAudits is null
                || handoff.ReconciledNotAcceptedEvidence is null)))
        {
            detail = "租约文件 Handoff 必需责任集合缺失（责任完整性校验失败），原件保留留痕。";
            return false;
        }

        if (handoff.Submission is { } sub)
        {
            if (string.IsNullOrWhiteSpace(sub.SubmissionIdentity) || sub.SendSeq < 1
                || !Enum.IsDefined(sub.State))
            {
                detail = "租约文件 v2 Submission 身份/枚举非法，原件保留留痕。";
                return false;
            }

            // [终审会诊阻断处置] 未决发送身份必须是**规范形式** `sub:{requestIdentity}:{sendSeq}`：
            // 尾段必须是**不变文化的精确十进制文本**（`01`／`+1`／带空白一律拒绝），且与该记录 SendSeq 全等。
            var subLastColon = sub.SubmissionIdentity.LastIndexOf(':');
            var subSeqText = subLastColon > 4 ? sub.SubmissionIdentity[(subLastColon + 1)..] : "";
            if (!sub.SubmissionIdentity.StartsWith("sub:", StringComparison.Ordinal)
                || subLastColon <= 4
                || !TryParseCanonicalSeq(subSeqText, out var parsedSubSeq)
                || parsedSubSeq != sub.SendSeq)
            {
                detail = "租约文件 v2 Submission 身份非规范形式（应为 sub:{requestIdentity}:{sendSeq} 且尾段＝SendSeq）。";
                return false;
            }

            var subRequestIdentity = sub.SubmissionIdentity[4..subLastColon];

            // 关联一致性（[终审会诊阻断处置] **绑定到同一 Operation**）：未决发送必须有**同身份、同轮次、且其请求身份
            // 恰为身份中声明的 requestIdentity** 的操作记录——否则「身份声明的请求」与「实际持有记录的请求」可分属不同记录（交叉关联绕过）。
            var linked = handoff.Operations.Any(o => o is not null
                && string.Equals(o.SubmissionIdentity, sub.SubmissionIdentity, StringComparison.Ordinal)
                && o.LastSendSeq == sub.SendSeq
                && string.Equals(o.RequestIdentity, subRequestIdentity, StringComparison.Ordinal));
            if (!linked)
            {
                detail = "租约文件 v2 Submission 无关联 Operations 记录（交叉不一致），原件保留留痕。";
                return false;
            }
        }

        // v5 历史归档只接纳已经过最短保留期的终局墓碑；有未决责任的记录仍留在热 Operations。
        foreach (var archived in handoff.ArchivedOperations)
        {
            var archivedOp = archived?.Operation;
            if (archived is null
                || archivedOp is null
                || archived.ArchivedAtUtc == default
                || archivedOp.UpdatedAtUtc == default
                || archivedOp.Zone is not (OperationZone.Tombstone or OperationZone.TerminalPendingTransfer)
                || archivedOp.RequestState is not (OperationRequestState.TerminalCompleted
                    or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected)
                || archivedOp.ConflictPending
                || (archivedOp.AcceptanceClaim is not null
                    && !ArbitrationRetentionPolicy.IsSettledAcceptanceClaim(archivedOp))
                || archivedOp.ConflictAdjudicationClaim is not null
                || (archivedOp.PendingTerminal is not null
                    && (archivedOp.RequestState != OperationRequestState.TerminalCompleted
                        || archivedOp.ConflictResolutionState != "ResolvedHistoricalAcceptedTerminal"))
                || archived.ArchivedAtUtc < archivedOp.UpdatedAtUtc
                || archived.ArchivedAtUtc - archivedOp.UpdatedAtUtc < ArbitrationRetentionPolicy.TombstoneMinimumAge)
            {
                detail = "租约文件 v5 ArchivedOperations 只能包含已满 24h 且无未决责任的终局操作（墓碑或待迁墓碑），原件保留留痕。";
                return false;
            }
        }

        var allOperations = handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation)).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in allOperations)
        {
            if (op is null
                || string.IsNullOrWhiteSpace(op.RequestIdentity)
                || string.IsNullOrWhiteSpace(op.CandidateId)
                || !Enum.IsDefined(op.RequestState)
                || !Enum.IsDefined(op.Zone)
                || !seen.Add(op.RequestIdentity))
            {
                detail = "租约文件 v2 Operations 记录身份/枚举/唯一性非法，原件保留留痕。";
                return false;
            }
            if (op.RejectedSendRounds is { } rejectedRounds
                && (op.OperationType != OperationType.NodeExecution || op.LastSendSeq < 2 || rejectedRounds.Count != op.LastSendSeq - 1
                    || rejectedRounds.Where((r, index) => r is null || r.AnsweredSendSeq != index + 1
                        || r.Outcome != OperationOutcome.Rejected || !r.Retryable
                        || string.IsNullOrWhiteSpace(r.ReasonCode) || string.IsNullOrWhiteSpace(r.EvidenceSource)).Any()))
            {
                detail = "节点原发送轮次拒绝快照不完整/冲突，原件保留。";
                return false;
            }
            if (version >= 4 && op.AcceptanceClaim is { } claim
                && (claim.SendSeq < 1
                    || !string.Equals(claim.SubmissionIdentity,
                        $"sub:{op.RequestIdentity}:{claim.SendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StringComparison.Ordinal)
                    || claim.SendSeq > op.LastSendSeq
                    || (claim.SendSeq != op.LastSendSeq && !op.ConflictPending)
                    || string.IsNullOrWhiteSpace(claim.RequestIdentity)
                    || !string.Equals(claim.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(claim.SubmissionIdentity)
                    || string.IsNullOrWhiteSpace(claim.OwnerLeaseId)
                    || string.IsNullOrWhiteSpace(claim.OwnerEpoch)
                    || claim.ClaimedAtUtc == default
                    || string.IsNullOrWhiteSpace(claim.EvidenceSource)
                    || op.OperationType != OperationType.ExternalStart))
            {
                detail = "租约文件 v4 AcceptanceClaim 身份/轮次/所有者/证据不匹配，原件保留留痕。";
                return false;
            }
            // [Batch B 续 会诊] 冲突证据必须**结构化且字段完整**（自由字符串无法执行幂等/关联校验）。
            var seenConflictEvidence = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ce in op.ConflictEvidence ?? [])
            {
                var hasHistoricalAudit = ce is not null && (handoff.ConflictResolutionAudits ?? []).Any(a => a is not null
                    && string.Equals(a.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal)
                    && string.Equals(a.SubmissionIdentity, ce.SubmissionIdentity, StringComparison.Ordinal)
                    && a.SendSeq == ce.SendSeq);
                var hasAcceptedTerminalAudit = ce is not null && (handoff.ConflictResolutionAudits ?? []).Any(a => a is not null
                    && a.Resolution is (ConflictResolutionKind.ResolvedAcceptedTerminal
                        or ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal)
                    && string.Equals(a.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal)
                    && string.Equals(a.SubmissionIdentity, ce.SubmissionIdentity, StringComparison.Ordinal)
                    && a.SendSeq == ce.SendSeq
                    && a.ResolutionEvidenceSnapshot is { } snapshot
                    && string.Equals(snapshot.SubmissionIdentity, ce.SubmissionIdentity, StringComparison.Ordinal)
                    && snapshot.SendSeq == ce.SendSeq);
                var conflictingExecution = ce?.ConflictingExecutionResultSnapshot;
                var isAcceptedReceipt = ce is not null
                    && string.Equals(ce.RawTerminal, "accepted_receipt", StringComparison.Ordinal);
                var historicalAcceptedReceipt = ce is not null && (op.ConflictEvidence ?? []).Any(receipt => receipt is not null
                    && string.Equals(receipt.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
                    && string.Equals(receipt.SubmissionIdentity, ce.SubmissionIdentity, StringComparison.Ordinal)
                    && receipt.SendSeq == ce.SendSeq
                    && !string.IsNullOrWhiteSpace(receipt.JobId)
                    && string.Equals(receipt.JobId, ce.JobId, StringComparison.Ordinal));
                var isHistoricalAcceptedTerminalObservation = ce is not null
                    && op.ConflictResolutionState is ("AcceptedTerminalObserved" or "ResolvedHistoricalAcceptedTerminal")
                    && historicalAcceptedReceipt
                    && ce.SendSeq < op.LastSendSeq
                    && ce.ConflictingExecutionResultSnapshot is not null
                    && !string.Equals(ce.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(ce.JobId);
                if (ce is null
                    || string.IsNullOrWhiteSpace(ce.EvidenceId)
                    || string.IsNullOrWhiteSpace(ce.RawTerminal)
                    || string.IsNullOrWhiteSpace(ce.EvidenceSource)
                    || ce.ObservedAtUtc == default
                    || string.IsNullOrWhiteSpace(ce.SubmissionIdentity)
                    || ce.SendSeq < 1
                    // 当前轮证据须命中当前身份；历史轮证据须有同轮裁决审计，唯一先行保留例外是显式非终态受理回执。
                    || ((!string.Equals(ce.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal)
                         || ce.SendSeq != op.LastSendSeq) && !hasHistoricalAudit
                        && !isAcceptedReceipt && !isHistoricalAcceptedTerminalObservation)
                    // 迟到受理回执是逐轮台账中的非终态事实，允许先追加为待决历史证据；不得伪装为执行终态载荷。
                    || (isAcceptedReceipt && (conflictingExecution is not null
                        || op.OperationType != OperationType.ExternalStart
                        || ce.SendSeq > op.LastSendSeq
                        || !string.Equals(ce.SubmissionIdentity,
                            $"sub:{op.RequestIdentity}:{ce.SendSeq.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal)
                        || (!op.ConflictPending && !hasAcceptedTerminalAudit)))
                    || (conflictingExecution is not null
                        && (!Enum.IsDefined(conflictingExecution.Kind)
                            || conflictingExecution.Kind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
                            || !string.Equals(conflictingExecution.SubmissionIdentity, ce.SubmissionIdentity, StringComparison.Ordinal)
                            || conflictingExecution.SendSeq != ce.SendSeq
                            || !string.Equals(conflictingExecution.RawTerminal, ce.RawTerminal, StringComparison.Ordinal)
                            || !string.Equals(conflictingExecution.ExecutionErrorCode, ce.ExecutionErrorCode, StringComparison.Ordinal)
                            || !string.Equals(conflictingExecution.EvidenceSource, ce.EvidenceSource, StringComparison.Ordinal)
                            || conflictingExecution.ObservedAtUtc != ce.ObservedAtUtc
                            || conflictingExecution.ObservedAtUtc == default
                            || (conflictingExecution.Kind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(conflictingExecution.ExecutionErrorCode))))
                    || !seenConflictEvidence.Add(ce.EvidenceId))
                {
                    detail = "租约文件 v3 冲突证据记录字段/唯一性非法（保守待对账）。";
                    return false;
                }
            }
            var hasHistoricalAcceptedReceipt = (op.ConflictEvidence ?? []).Any(e => e is not null
                && string.Equals(e.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
                && e.SendSeq < op.LastSendSeq
                && string.Equals(e.SubmissionIdentity,
                    $"sub:{op.RequestIdentity}:{e.SendSeq.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal));
            var hasHistoricalAcceptedTerminal = (op.ConflictEvidence ?? []).Any(terminal => terminal is not null
                && terminal.SendSeq < op.LastSendSeq
                && !string.Equals(terminal.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
                && terminal.ConflictingExecutionResultSnapshot is not null
                && (op.ConflictEvidence ?? []).Any(receipt => receipt is not null
                    && string.Equals(receipt.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
                    && string.Equals(receipt.SubmissionIdentity, terminal.SubmissionIdentity, StringComparison.Ordinal)
                    && receipt.SendSeq == terminal.SendSeq
                    && !string.IsNullOrWhiteSpace(receipt.JobId)
                    && string.Equals(receipt.JobId, terminal.JobId, StringComparison.Ordinal)));
            if (hasHistoricalAcceptedReceipt && string.IsNullOrWhiteSpace(op.ConflictResolutionState))
            {
                detail = "历史受理回执缺少逐轮跟踪状态（保守待对账）。";
                return false;
            }
            if (op.ConflictResolutionState is { } acceptedState
                && (acceptedState is not ("AcceptedAwaitingTerminal" or "AcceptedTerminalObserved"
                        or "ResolvedHistoricalAcceptedTerminal")
                    || op.OperationType != OperationType.ExternalStart
                    || !hasHistoricalAcceptedReceipt
                    || (acceptedState is "AcceptedTerminalObserved" or "ResolvedHistoricalAcceptedTerminal"
                        && !hasHistoricalAcceptedTerminal)
                    || (acceptedState is "AcceptedAwaitingTerminal" or "AcceptedTerminalObserved"
                        && !op.ConflictPending)
                    || (acceptedState == "ResolvedHistoricalAcceptedTerminal"
                        && !IsHistoricalAcceptedTerminalResolved(handoff, op))))
            {
                detail = "租约文件 v3 历史受理跟踪状态缺少同轮回执/终态证据或与冲突状态不匹配（保守待对账）。";
                return false;
            }
        }

        // R5.3 §24.2-2″／§24.20-A′：追加式集合的**引用完整性**（写入一律 v3，故仅对本格式代校验）。
        var seenEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ev in handoff.ReconciledNotAcceptedEvidence ?? [])
        {
            if (ev is null
                || string.IsNullOrWhiteSpace(ev.EvidenceId)
                || string.IsNullOrWhiteSpace(ev.SubmissionIdentity)
                || ev.SendSeq < 1
                || string.IsNullOrWhiteSpace(ev.FactKind)
                // [终审会诊阻断处置] 事实类型必须命中**封闭白名单**——否则任意非空字符串都能冒充「权威未受理事实」。
                || !ReconciledNotAcceptedFactKinds.All.Contains(ev.FactKind)
                || string.IsNullOrWhiteSpace(ev.RawEvidenceWord)
                || string.IsNullOrWhiteSpace(ev.EvidenceSource)
                || ev.ObservedAtUtc == default
                || !seenEvidence.Add(ev.EvidenceId))
            {
                detail = "租约文件 v3 ReconciledNotAcceptedEvidence 记录身份/唯一性/观察时点非法，原件保留留痕。";
                return false;
            }
        }

        var seenAudits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var audit in handoff.ConflictResolutionAudits ?? [])
        {
            if (audit is null
                || string.IsNullOrWhiteSpace(audit.AuditId)
                || string.IsNullOrWhiteSpace(audit.RequestIdentity)
                || string.IsNullOrWhiteSpace(audit.SubmissionIdentity)
                || audit.SendSeq < 1
                || audit.ResolvedAtUtc == default
                || !Enum.IsDefined(audit.Resolution)
                || (audit.Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal
                    ? audit.SupersededRejectedResultSnapshot is not null || audit.SupersededExecutionResultSnapshot is not null
                    : (audit.SupersededRejectedResultSnapshot is null) == (audit.SupersededExecutionResultSnapshot is null))
                || (audit.Resolution == ConflictResolutionKind.ResolvedNotAccepted
                    && (audit.SupersededRejectedResultSnapshot is null || audit.SupersededExecutionResultSnapshot is not null))
                || !seenAudits.Add(audit.AuditId))
            {
                detail = "租约文件 v3 ConflictResolutionAudits 记录身份/枚举/唯一性/被覆盖事实快照非法，原件保留留痕。";
                return false;
            }

            // 判别式两字段**严格互斥且必需**（§24.2-2″ 分支必需载荷）：受理终态⇒快照；未受理⇒证据引用。
            var expectSnapshot = audit.Resolution is ConflictResolutionKind.ResolvedAcceptedTerminal
                or ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal;
            var hasSnapshot = audit.ResolutionEvidenceSnapshot is not null;
            var hasRef = !string.IsNullOrWhiteSpace(audit.ResolutionEvidenceRef?.EvidenceId);
            var expectRelatedCurrentRoundRejection = audit.Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal;
            if (expectSnapshot != hasSnapshot || hasSnapshot == hasRef
                || expectRelatedCurrentRoundRejection != (audit.RelatedCurrentRoundRejectedResultSnapshot is not null))
            {
                detail = "租约文件 v3 裁决审计的 resolutionEvidence 判别式字段非法（必需载荷缺失或两字段并存）。";
                return false;
            }

            // 审计可属于该操作的历史发送轮；RetryAsync 推进当前轮不得抹掉历史责任证据。
            var op = allOperations.FirstOrDefault(o => o is not null
                && string.Equals(o.RequestIdentity, audit.RequestIdentity, StringComparison.Ordinal));
            var expectedAuditSubmission = $"sub:{audit.RequestIdentity}:{audit.SendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var expectedMergedSubmission = op?.MergedInto is { Length: > 0 } mergedIdentity
                ? $"sub:{mergedIdentity}:{audit.SendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : null;
            if (op is null
                || (!string.Equals(audit.SubmissionIdentity, expectedAuditSubmission, StringComparison.Ordinal)
                    && !string.Equals(audit.SubmissionIdentity, expectedMergedSubmission, StringComparison.Ordinal)))
            {
                detail = "租约文件 v3 裁决审计与 Operation 关联不一致（引用完整性失败）。";
                return false;
            }

            // 被覆盖事实快照必须有且只有一种，并与仍保留的权威事实逐字段一致。
            // 常规路径保存拒绝快照；“已有执行终态后收到未受理回执”路径保存执行终态快照。
            var rejectionSnapshot = audit.SupersededRejectedResultSnapshot;
            var auditIsCurrentRound = string.Equals(op.SubmissionIdentity, audit.SubmissionIdentity, StringComparison.Ordinal)
                                      && op.LastSendSeq == audit.SendSeq;
            if (rejectionSnapshot is not null && (rejectionSnapshot.Outcome != OperationOutcome.Rejected
                || rejectionSnapshot.AnsweredSendSeq != audit.SendSeq
                || string.IsNullOrWhiteSpace(rejectionSnapshot.ReasonCode)
                || string.IsNullOrWhiteSpace(rejectionSnapshot.EvidenceSource)
                || (auditIsCurrentRound && (op.LastResult is null
                    || op.LastResult.Outcome != rejectionSnapshot.Outcome
                    || op.LastResult.AnsweredSendSeq != rejectionSnapshot.AnsweredSendSeq
                    || op.LastResult.Retryable != rejectionSnapshot.Retryable
                    || !string.Equals(op.LastResult.ReasonCode, rejectionSnapshot.ReasonCode, StringComparison.Ordinal)
                    || !string.Equals(op.LastResult.EvidenceSource, rejectionSnapshot.EvidenceSource, StringComparison.Ordinal)))))
            {
                detail = "租约文件 v3 裁决审计的拒绝快照与既有拒绝结果不一致（逐字段校验失败）。";
                return false;
            }
            if (audit.RelatedCurrentRoundRejectedResultSnapshot is { } relatedRejection
                && (relatedRejection.Outcome != OperationOutcome.Rejected
                    || relatedRejection.AnsweredSendSeq != op.LastSendSeq
                    || op.LastResult is not { Outcome: OperationOutcome.Rejected } currentRejected
                    || currentRejected.AnsweredSendSeq != relatedRejection.AnsweredSendSeq
                    || currentRejected.Retryable != relatedRejection.Retryable
                    || currentRejected.RetryBudgetUsed != relatedRejection.RetryBudgetUsed
                    || !string.Equals(currentRejected.ReasonCode, relatedRejection.ReasonCode, StringComparison.Ordinal)
                    || !string.Equals(currentRejected.EvidenceSource, relatedRejection.EvidenceSource, StringComparison.Ordinal)))
            {
                detail = "租约文件 v3 历史轮受理终态审计的当前轮拒绝关联快照与现存结果不一致。";
                return false;
            }
            if (audit.SupersededExecutionResultSnapshot is { } executionSnapshot)
            {
                var result = op.ExecutionResult;
                if (audit.Resolution != ConflictResolutionKind.ResolvedAcceptedTerminal
                    || result is null
                    || !string.Equals(executionSnapshot.SubmissionIdentity, audit.SubmissionIdentity, StringComparison.Ordinal)
                    || executionSnapshot.SendSeq != audit.SendSeq
                    || executionSnapshot.Kind != result.Kind
                    || !string.Equals(executionSnapshot.RawTerminal, result.RawTerminal, StringComparison.Ordinal)
                    || !string.Equals(executionSnapshot.ExecutionErrorCode, result.ExecutionErrorCode, StringComparison.Ordinal)
                    || !string.Equals(executionSnapshot.JobId, result.JobId, StringComparison.Ordinal)
                    || !string.Equals(executionSnapshot.EvidenceSource, result.EvidenceSource, StringComparison.Ordinal)
                    || executionSnapshot.ObservedAtUtc != result.ObservedAtUtc
                    || !string.Equals(executionSnapshot.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
                    || executionSnapshot.SendSeq != result.SendSeq)
                {
                    detail = "租约文件 v3 裁决审计的执行终态快照与权威 ExecutionResult 不一致（逐字段校验失败）。";
                    return false;
                }
            }

            if (expectSnapshot)
            {
                // 受理终态分支：必须存在同身份同轮次的权威终态 ExecutionResult，且快照与其业务字段逐字段一致。
                var result = op.ExecutionResult;
                var ev = audit.ResolutionEvidenceSnapshot!;
                // [验证会诊阻断处置] 权威执行终态包括 Succeeded／Failed／Cancelled（§24.15：三类终态均可独立终局），
                // 不得只认 Succeeded（否则合法失败/取消终态被判损坏）。
                if (result is null
                    || !string.Equals(result.SubmissionIdentity, audit.SubmissionIdentity, StringComparison.Ordinal)
                    || result.SendSeq != audit.SendSeq
                    // [第三轮验证会诊阻断处置] 先 `Enum.IsDefined` 再白名单：反序列化得到的非法枚举值
                    // （如 999）不等于 Unknown，仅比较 `!= Unknown` 会 fail-open。
                    || !Enum.IsDefined(result.Kind)
                    || result.Kind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
                    || string.IsNullOrWhiteSpace(result.RawTerminal)
                    || string.IsNullOrWhiteSpace(result.EvidenceSource)
                    || result.ObservedAtUtc == default
                    || (result.Kind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(result.ExecutionErrorCode))
                    || !string.Equals(ev.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
                    || ev.SendSeq != result.SendSeq
                    || ev.Kind != result.Kind
                    || ev.ObservedAtUtc != result.ObservedAtUtc
                    || !string.Equals(ev.RawTerminal, result.RawTerminal, StringComparison.Ordinal)
                    || !string.Equals(ev.ExecutionErrorCode, result.ExecutionErrorCode, StringComparison.Ordinal)
                    || !string.Equals(ev.JobId, result.JobId, StringComparison.Ordinal)
                    || !string.Equals(ev.EvidenceSource, result.EvidenceSource, StringComparison.Ordinal))
                {
                    detail = "租约文件 v3 受理终态裁决审计缺少匹配的 ExecutionResult 或快照逐字段不一致。";
                    return false;
                }
            }
            else
            {
                // 未受理分支：证据引用必须唯一命中，且身份/轮次与 Operation、审计项一致。
                var evidenceId = audit.ResolutionEvidenceRef!.EvidenceId;
                var evidence = (handoff.ReconciledNotAcceptedEvidence ?? [])
                    .FirstOrDefault(e => e is not null && string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
                if (!seenEvidence.Contains(evidenceId) || evidence is null
                    || !string.Equals(evidence.SubmissionIdentity, audit.SubmissionIdentity, StringComparison.Ordinal)
                    || evidence.SendSeq != audit.SendSeq)
                {
                    detail = "租约文件 v3 未受理裁决审计的证据引用不完整或身份/来源不一致（引用完整性失败）。";
                    return false;
                }
                // [第三轮验证会诊阻断处置] **删除** `evidence.EvidenceSource == snapshot.EvidenceSource` 约束：
                // 二者是**两个独立事实**（历史拒绝结果来源 vs 后续权威未受理观察来源），设计未要求相等，强制相等会误拒合法记录。
            }
        }

        var seenPre = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pre in handoff.PreObservations ?? [])
        {
            if (pre is null
                || string.IsNullOrWhiteSpace(pre.SubmissionIdentity)
                || pre.SendSeq < 1
                || !Enum.IsDefined(pre.OperationType)
                // 目标纪元：流程/节点路径既有行为允许为空（纪元未知，§14 残余1），不得据此判损坏；
                // **外部启动**必须非空（入口已有固定纪元守卫：未知纪元不签发）。
                || pre.TargetEpoch is null
                || (pre.OperationType == OperationType.ExternalStart && string.IsNullOrWhiteSpace(pre.TargetEpoch))
                // [终审会诊阻断处置] 状态必须是**封闭二值**（`pending`/`completed`），任意非空字符串不得通过。
                || pre.State is not ("pending" or "completed")
                // 无句柄路径必须有**替代查询依据**（§24.7-2）：空依据不得当作可重建观察。
                || string.IsNullOrWhiteSpace(pre.QueryBasis)
                || pre.CreatedAtUtc == default
                || !seenPre.Add(pre.SubmissionIdentity + "#" + pre.SendSeq))
            {
                detail = "租约文件 v3 PreObservations 记录身份/枚举/唯一性非法，原件保留留痕。";
                return false;
            }

            // [终审会诊阻断处置] 与 Operation 的**关联一致性**：由**规范形式** `sub:{requestIdentity}:{sendSeq}` 反解身份——
            // 必须校验尾段与 `SendSeq` 全等（否则 `sub:x:garbage` 之类的畸形身份会绕过跨轮隔离）。
            var preIdentity = pre.SubmissionIdentity;
            var lastColon = preIdentity.LastIndexOf(':');
            var preSeqText = lastColon > 4 ? preIdentity[(lastColon + 1)..] : "";
            if (!preIdentity.StartsWith("sub:", StringComparison.Ordinal)
                || lastColon <= 4
                || !TryParseCanonicalSeq(preSeqText, out var parsedPreSeq)
                || parsedPreSeq != pre.SendSeq)
            {
                detail = "租约文件 v3 PreObservations 的发送身份非规范形式（应为 sub:{requestIdentity}:{sendSeq} 且尾段＝SendSeq）。";
                return false;
            }
            var preRequestIdentity = preIdentity[4..lastColon];
            var preOp = string.IsNullOrEmpty(preRequestIdentity)
                ? null
                : allOperations.FirstOrDefault(o => o is not null
                    && string.Equals(o.RequestIdentity, preRequestIdentity, StringComparison.Ordinal));
            if (preOp is null || preOp.OperationType != pre.OperationType)
            {
                detail = "租约文件 v3 PreObservations 与对应 Operation 的关联/类型不一致（引用完整性失败）。";
                return false;
            }

            // 未完成（`pending`）的预观察必须**正对应当前未决发送**（身份＋轮次全等），否则视为孤立残件。
            if (pre.State == "pending"
                && (handoff.Submission is not { } currentPre
                    || !string.Equals(currentPre.SubmissionIdentity, pre.SubmissionIdentity, StringComparison.Ordinal)
                    || currentPre.SendSeq != pre.SendSeq))
            {
                detail = "租约文件 v3 存在未对应未决发送的 pending 预观察记录（不可恢复发送窗口）。";
                return false;
            }
        }

        // [终审会诊阻断处置] **反向不变量**：存在未决发送时，必须有同身份/同轮次的预观察记录（未持久化不得发送）。
        if (handoff.Submission is { } pendingSubmission
            && !(handoff.PreObservations ?? []).Any(p => p is not null
                && string.Equals(p.SubmissionIdentity, pendingSubmission.SubmissionIdentity, StringComparison.Ordinal)
                && p.SendSeq == pendingSubmission.SendSeq
                && p.State == "pending"))
        {
            detail = "租约文件 v3 未决发送缺少同身份/同轮次的预观察记录（§24.16-2 持久化不变量）。";
            return false;
        }

        // 当前审计引用＋历史审计 ID 集合构成双向关系；每个 AuditId 仍只能归属一个 Operation。
        var auditIdToOperation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in allOperations)
        {
            var auditIds = (op.ConflictResolutionAuditHistoryIds ?? []).ToList();
            if (auditIds.Count != auditIds.Distinct(StringComparer.Ordinal).Count())
            {
                detail = "租约文件 v3 Operation 的历史裁决审计引用重复（引用完整性失败）。";
                return false;
            }
            if (op.ConflictResolutionAuditId is { Length: > 0 } currentAuditId
                && !auditIds.Contains(currentAuditId, StringComparer.Ordinal))
                auditIds.Add(currentAuditId);
            if (op.ConflictResolutionAuditId is { Length: > 0 } currentId
                && !(handoff.ConflictResolutionAudits ?? []).Any(a => a is not null
                    && string.Equals(a.AuditId, currentId, StringComparison.Ordinal)
                    && string.Equals(a.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal)
                    && string.Equals(a.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal)
                    && a.SendSeq == op.LastSendSeq))
            {
                detail = "租约文件 v3 Operation 的当前裁决审计与当前发送轮不匹配（引用完整性失败）。";
                return false;
            }
            foreach (var auditId in auditIds)
            {
                var bound = (handoff.ConflictResolutionAudits ?? []).FirstOrDefault(a => a is not null
                    && string.Equals(a.AuditId, auditId, StringComparison.Ordinal)
                    && string.Equals(a.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal));
                if (bound is null || !auditIdToOperation.TryAdd(auditId, op.RequestIdentity))
                {
                    detail = "租约文件 v3 Operation 的历史裁决审计引用缺失或被多个 Operation 共享（引用完整性失败）。";
                    return false;
                }
            }
        }

        foreach (var audit in handoff.ConflictResolutionAudits ?? [])
        {
            if (audit is null) continue;
            if (!auditIdToOperation.TryGetValue(audit.AuditId, out var referencing)
                || !string.Equals(referencing, audit.RequestIdentity, StringComparison.Ordinal))
            {
                detail = "租约文件 v3 裁决审计缺少对应 Operation 的反向引用（三项/四项原子事务未提交）。";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateLeaseSegment(LeaseSegment? lease, out string detail)
    {
        detail = "";
        if (lease is null) return true;
        if (string.IsNullOrWhiteSpace(lease.LeaseId)
            || string.IsNullOrWhiteSpace(lease.OwnerEpoch)
            || lease.Generation < 1
            || lease.HeartbeatSeq < 1
            || lease.TtlSeconds <= 0)
        {
            detail = "租约文件 Lease 段结构/取值非法（缺身份或零代次等），原件保留留痕。";
            return false;
        }
        return true;
    }

    private static bool IsHistoricalAcceptedTerminalResolved(LeaseHandoffSegment handoff, OperationRecord op)
    {
        if (op.ConflictPending
            || op.RequestState != OperationRequestState.TerminalCompleted
            || op.Zone is not (OperationZone.TerminalPendingTransfer or OperationZone.Tombstone)
            || op.ConflictAdjudicationClaim is not null
            || op.AcceptanceClaim is not null
            || op.ExecutionResult is not { } result
            || op.PendingTerminal is not { } pending
            || op.LastResult is not { Outcome: OperationOutcome.Rejected } currentRejection
            || currentRejection.AnsweredSendSeq != op.LastSendSeq
            || result.SendSeq >= op.LastSendSeq
            || !string.Equals(result.SubmissionIdentity,
                $"sub:{op.RequestIdentity}:{result.SendSeq.ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal))
            return false;

        var receipt = (op.ConflictEvidence ?? []).FirstOrDefault(e => e is not null
            && string.Equals(e.RawTerminal, "accepted_receipt", StringComparison.Ordinal)
            && string.Equals(e.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
            && e.SendSeq == result.SendSeq
            && !string.IsNullOrWhiteSpace(e.JobId)
            && string.Equals(e.JobId, result.JobId, StringComparison.Ordinal));
        var terminal = (op.ConflictEvidence ?? []).FirstOrDefault(e => e is not null
            && e.SendSeq == result.SendSeq
            && string.Equals(e.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
            && e.ConflictingExecutionResultSnapshot is { } snapshot
            && ExecutionResultPayloadMatches(snapshot, result));
        if (receipt is null || terminal is null
            || !Enum.IsDefined(result.Kind)
            || result.Kind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
            || string.IsNullOrWhiteSpace(result.JobId)
            || (result.Kind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(result.ExecutionErrorCode))
            || !PendingTerminalPayloadMatches(pending, result))
            return false;

        var auditId = (op.ConflictResolutionAuditHistoryIds ?? []).FirstOrDefault(id =>
            (handoff.ConflictResolutionAudits ?? []).Any(a => a is not null
                && string.Equals(a.AuditId, id, StringComparison.Ordinal)
                && a.Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal
                && string.Equals(a.RequestIdentity, op.RequestIdentity, StringComparison.Ordinal)
                && string.Equals(a.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
                && a.SendSeq == result.SendSeq
                && a.SupersededRejectedResultSnapshot is null
                && a.SupersededExecutionResultSnapshot is null
                && a.ResolutionEvidenceRef is null
                && a.ResolutionEvidenceSnapshot is { } resolutionSnapshot
                && ExecutionResultPayloadMatches(resolutionSnapshot, result)
                && a.RelatedCurrentRoundRejectedResultSnapshot is { } related
                && OperationRejectionMatches(related, currentRejection)));
        return !string.IsNullOrWhiteSpace(auditId);
    }

    private static bool ExecutionResultPayloadMatches(ExecutionResult left, ExecutionResult right)
        => left.Kind == right.Kind
           && string.Equals(left.RawTerminal, right.RawTerminal, StringComparison.Ordinal)
           && string.Equals(left.ExecutionErrorCode, right.ExecutionErrorCode, StringComparison.Ordinal)
           && string.Equals(left.JobId, right.JobId, StringComparison.Ordinal)
           && string.Equals(left.EvidenceSource, right.EvidenceSource, StringComparison.Ordinal)
           && string.Equals(left.SubmissionIdentity, right.SubmissionIdentity, StringComparison.Ordinal)
           && left.SendSeq == right.SendSeq
           && left.ObservedAtUtc == right.ObservedAtUtc;

    private static bool PendingTerminalPayloadMatches(PendingTerminal pending, ExecutionResult result)
        => pending.Kind == result.Kind
           && string.Equals(pending.RawTerminal, result.RawTerminal, StringComparison.Ordinal)
           && string.Equals(pending.ExecutionErrorCode, result.ExecutionErrorCode, StringComparison.Ordinal)
           && string.Equals(pending.JobId, result.JobId, StringComparison.Ordinal)
           && string.Equals(pending.EvidenceSource, result.EvidenceSource, StringComparison.Ordinal)
           && string.Equals(pending.SubmissionIdentity, result.SubmissionIdentity, StringComparison.Ordinal)
           && pending.SendSeq == result.SendSeq
           && pending.OperationType == OperationType.ExternalStart
           && pending.ObservedAtUtc == result.ObservedAtUtc
           && pending.RecordedAtUtc != default;

    private static bool OperationRejectionMatches(OperationResult left, OperationResult right)
        => left.Outcome == OperationOutcome.Rejected
           && right.Outcome == OperationOutcome.Rejected
           && left.AnsweredSendSeq == right.AnsweredSendSeq
           && left.Retryable == right.Retryable
           && left.RetryBudgetUsed == right.RetryBudgetUsed
           && string.Equals(left.ReasonCode, right.ReasonCode, StringComparison.Ordinal)
           && string.Equals(left.EvidenceSource, right.EvidenceSource, StringComparison.Ordinal);

    /// <summary>残件匹配（P2-⑥复核：仅本组件专属命名 ".lease-*.tmp"，不波及配置根其他文件）。</summary>
    private static bool IsLeaseResidueFileName(string name)
        => name.StartsWith(".lease-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);

    /// <summary>
    /// 发送身份尾段的**规范十进制文本**解析（§24.2-2″／终审会诊）：
    /// 只接受 `value.ToString(CultureInfo.InvariantCulture)` 的不变文化精确写法——`01`／`+1`／前后空白等一律拒绝，
    /// 避免「数值等价 ≠ 文本规范」的绕过。
    /// </summary>
    private static bool TryParseCanonicalSeq(string text, out int value)
    {
        value = 0;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (!string.Equals(text, parsed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>
    /// §24.20-A′ 有序升级判定的第一步：旧格式代文件是否仍含**未决责任**
    /// （`Submission`／未终结 `Pending`／`Granted`／`Sending`／`Reconciling`／`Accepted` 记录，
    /// 以及即使请求态已回退仍持有交接或冲突责任的覆盖标记）。
    /// 命中 ⇒ 必须走隔离态结算事务，**不得**就地升版（本批尚未实现该事务 ⇒ fail-closed 只读拒绝）。
    /// </summary>
    private static bool HasUnresolvedResponsibilityForLegacyUpgrade(LogicalOwnerLeaseFile file)
    {
        var handoff = file.Handoff;
        if (handoff is null) return false;
        if (handoff.Submission is not null || handoff.Pending is not null) return true;
        return (handoff.Operations ?? []).Any(o => o is not null
            && (o.ConflictPending
                || o.PreemptConfirmPending
                || o.RequestState is OperationRequestState.Granted
                    or OperationRequestState.Sending
                    or OperationRequestState.Reconciling
                    or OperationRequestState.Accepted));
    }

    /// <summary>可消解事实白名单（§6.2/§4.1）：权威退出四词 + settle 协议事实；未知/超时/未命中不在其列。</summary>
    private static bool IsResolvableFact(string? fact)
        => HandoffTransitionPolicy.IsAuthoritativeExitWord(fact)
           || fact is "restore_confirmed" or "protocol_ended";

    /// <summary>
    /// 所有者资格单调判定（§6.3 复核：本进程存活判断用单调时间——UTC 前跳不得提前撤权、回拨不得续命）。
    /// 本进程无单调基线（新实例/未写过）→ 保守视为过期（不确定不放行；接管走 LeaseTakeoverObserver 路径）。
    /// </summary>
    private bool IsOwnerExpired(LeaseSegment lease)
        => _lastOwnerWriteMono is not { } t || _monotonic() - t > TimeSpan.FromSeconds(lease.TtlSeconds);

    /// <summary>未决意图等价判定（§6.2 幂等去重）：ActionId 之外的六要素全等才视为同一意图。</summary>
    private static bool SameIntent(PendingHandoffIntent a, PendingHandoffIntent b)
        => string.Equals(a.SuspendedRunIdentity, b.SuspendedRunIdentity, StringComparison.Ordinal)
           && string.Equals(a.AuthorizedPreemptor, b.AuthorizedPreemptor, StringComparison.Ordinal)
           && string.Equals(a.TargetEpoch, b.TargetEpoch, StringComparison.Ordinal)
           && a.Phase == b.Phase
           && a.ReconcileFromPhase == b.ReconcileFromPhase
           && string.Equals(a.RestoreBranch, b.RestoreBranch, StringComparison.Ordinal)
           && string.Equals(a.SubmissionIdentity, b.SubmissionIdentity, StringComparison.Ordinal);

    private static LeaseOpResult Reject(string reason) => new() { Success = false, Reason = reason, Lease = null };

    private static LeaseOpResult Ok(LeaseSegment? lease) => new() { Success = true, Reason = null, Lease = lease };
}

/// <summary>
/// 接管证据令牌（§6.3 复核）：仅 LeaseTakeoverObserver 在「绑定所有者身份+heartbeatSeq 的单调观察满 TTL」
/// 后才能产出（internal 构造——调用方无法凭一次新读快照伪造）；TryAcquire 锁内复核其与时下文件一致才允许接管。
/// </summary>
public sealed class LeaseTakeoverEvidence
{
    /// <summary>被观察所有者键（leaseId|ownerEpoch|generation）。</summary>
    public string OwnerKey { get; }
    /// <summary>被观察心跳序号。</summary>
    public long HeartbeatSeq { get; }
    /// <summary>观察期限依据=被观察租约的 TTL 秒（三轮 P1-②：期限由当前租约决定并纳入锁内复核，杜绝调用方自报短 TTL 提前接管）。</summary>
    public int TtlSeconds { get; }

    internal LeaseTakeoverEvidence(string ownerKey, long heartbeatSeq, int ttlSeconds)
    {
        OwnerKey = ownerKey;
        HeartbeatSeq = heartbeatSeq;
        TtlSeconds = ttlSeconds;
    }
}

/// <summary>
/// §6.3 跨进程接管观察器（单调计时）：「观察到 heartbeatSeq 持续不变达到 TTL」的本地单调计时为接管唯一依据，
/// 锁内复核由 ArbitrationLeaseStore.TryAcquire(LeaseTakeoverEvidence) 完成。
/// 观察计时绑定完整所有者身份（leaseId|ownerEpoch|generation）+heartbeatSeq：更替或心跳前进→重置计时（含成熟后）；
/// 观察者重启（新实例/Reset）=重新等待一个 TTL；UTC 拨动不影响本判定（单调钟）。
/// </summary>
public sealed class LeaseTakeoverObserver
{
    private readonly Func<TimeSpan> _monotonic;
    private string? _ownerKey;
    private long _heartbeatSeq;
    private TimeSpan _since;

    /// <summary>观察期限不由调用方指定——取自每次观察到的租约 TtlSeconds（三轮 P1-②）。</summary>
    public LeaseTakeoverObserver(Func<TimeSpan> monotonic)
    {
        _monotonic = monotonic ?? throw new ArgumentNullException(nameof(monotonic));
    }

    /// <summary>观察一次租约读取结果；非 null=不变已达 TTL 的接管证据（仍须经 TryAcquire 锁内复核）。</summary>
    public LeaseTakeoverEvidence? Observe(LeaseReadResult read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var lease = read.File?.Lease;
        if (lease is null)
        {
            Reset();
            return null; // 无所有者：无需观察证据（TryAcquire 直接获取；残件不确定另由 TryAcquire 保守拒绝）。
        }

        var key = ArbitrationLeaseStore.CurrentOwnerKey(lease);
        if (_ownerKey is null
            || !string.Equals(key, _ownerKey, StringComparison.Ordinal)
            || lease.HeartbeatSeq != _heartbeatSeq)
        {
            // 更替/心跳前进（含成熟后所有者续期）→重置计时；旧证据自然失效（TryAcquire 复核不匹配）。
            _ownerKey = key;
            _heartbeatSeq = lease.HeartbeatSeq;
            _since = _monotonic();
            return null;
        }

        // 期限=当前租约 TTL（非法取值视为未成熟；结构校验正常已挡）。
        var ttl = TimeSpan.FromSeconds(Math.Max(lease.TtlSeconds, 0));
        return lease.TtlSeconds > 0 && _monotonic() - _since >= ttl
            ? new LeaseTakeoverEvidence(key, lease.HeartbeatSeq, lease.TtlSeconds)
            : null;
    }

    /// <summary>观察者重启=重新等待一个 TTL（§6.3：代价=恢复延迟一个 TTL，规则明确可测）。</summary>
    public void Reset() => _ownerKey = null;
}
