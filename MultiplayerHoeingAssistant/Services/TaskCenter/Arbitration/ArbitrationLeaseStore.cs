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
    /// <summary>当前支持/写入的租约文件格式代（R5.2 冻结稿 §4.0：新增 Submission/Operations 字段=格式代 2；旧消费方按 Unsupported 响亮拒绝）。</summary>
    public const int SupportedVersion = 2;
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
    /// - Version &gt; SupportedVersion(2) → Unsupported；v1 向后读兼容（Pending 段原样保留，Submission/Operations 视为空，缺字段保守拒绝不默认为无责任）；
    ///   Lease 段非空 → (LastHeartbeatUtc + TtlSeconds 秒) &lt; utcNow() 为 Expired 否则 Valid；
    /// - Lease 段空 → Absent。
    /// 本方法只读不写盘（残件留痕只写进返回的 Detail）；配置目录不存在时直接判 Absent（不建目录、不建文件）。
    /// </summary>
    public LeaseReadResult Read()
    {
        // 配置目录不存在 = 从未写入发布 → Absent，不建目录、不建文件（构造零副作用延伸）。
        if (!Directory.Exists(_configDir))
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Absent, File = null, Detail = null };

        // 锁文件存在 = 曾有写入者（写者先建锁再发布，且锁文件永不删除）——读取也在锁内，与写者串行；
        // 锁文件不存在 = 无并发写者，直接读，绝不 New 出任何文件（恪守「只读不写盘」）。
        if (!File.Exists(_lockPath))
            return ReadCore();
        return WithLockContentionRetry(() =>
        {
            using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return ReadCore();
        });
    }

    /// <summary>盘读判定核心（不做任何写盘；调用方负责是否持锁）。</summary>
    private LeaseReadResult ReadCore()
    {
        var now = _utcNow();

        // 正式文件不存在 → Absent；目录存在时探测残留临时文件并把留痕写进 Detail（不落盘）。
        if (!File.Exists(_leasePath))
        {
            string? detail = null;
            if (Directory.Exists(_configDir))
            {
                var residue = Directory.EnumerateFiles(_configDir)
                    .Where(p => IsLeaseResidueFileName(Path.GetFileName(p)))
                    .ToList();
                if (residue.Count > 0)
                    detail = "正式文件不存在；发现残留临时文件（残件按无正式文件处理，不采用）："
                        + string.Join("，", residue.Select(Path.GetFileName));
            }
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Absent, File = null, Detail = detail, UncertainResidue = detail is not null };
        }

        // 先判版本再严格解析（§6.2 复核：未来版本改变字段类型时必须 Unsupported 而非 Corrupt）。
        string text;
        try
        {
            text = File.ReadAllText(_leasePath, Encoding.UTF8);
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

        // v2 责任段原始 JSON 预检（区分「字段缺失」与合法空值——handoff 段存在则 operations 必填，缺字段不得默认为合法空责任）。
        if (version == SupportedVersion)
        {
            using var doc2 = JsonDocument.Parse(text);
            if (doc2.RootElement.ValueKind == JsonValueKind.Object
                && doc2.RootElement.TryGetProperty("handoff", out var handoffEl)
                && handoffEl.ValueKind == JsonValueKind.Object
                && (!handoffEl.TryGetProperty("operations", out var opsEl) || opsEl.ValueKind != JsonValueKind.Array))
                return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 v2 Handoff.Operations 缺失（责任完整性校验失败），原件保留留痕。" };
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

        // Lease 段结构校验（§6.2 复核：缺失身份/零代次等非法取值 = Corrupt，不得当作可获取）。
        var lease = file.Lease;
        if (lease is not null
            && (string.IsNullOrWhiteSpace(lease.LeaseId)
                || string.IsNullOrWhiteSpace(lease.OwnerEpoch)
                || lease.Generation < 1
                || lease.HeartbeatSeq < 1
                || lease.TtlSeconds <= 0))
            return new LeaseReadResult { Status = ArbitrationLeaseStatus.Corrupt, File = null, Detail = "租约文件 Lease 段结构/取值非法（缺身份或零代次等），原件保留留痕。" };

        // v2 责任段结构校验（R5.2 §4.0：v1 兼容默认值与 v2 责任完整性分开——version==2 时
        // Handoff 段存在则 Operations 必填、记录身份/枚举/唯一性非法=Corrupt，不降级为空责任）。
        if (version == SupportedVersion && !ValidateHandoffSegment(file.Handoff, out var handoffDetail))
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
    {
        return WithLock(read =>
        {
            if (read.Status == ArbitrationLeaseStatus.Corrupt) return Reject("corrupt");
            if (read.Status == ArbitrationLeaseStatus.Unsupported) return Reject("unsupported_version");

            var lease = read.File?.Lease;
            if (lease is null
                || !string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
                || !string.Equals(lease.OwnerEpoch, ownerEpoch, StringComparison.Ordinal)
                || read.File!.Revision != expectedRevision)
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

            var remnants = Directory.EnumerateFiles(_configDir)
                .Where(p => IsLeaseResidueFileName(Path.GetFileName(p)))
                .ToList();
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
            Directory.CreateDirectory(backupDir);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
            foreach (var remnant in remnants)
            {
                // 不覆盖同名历史留痕：目标名追加单调时间戳后缀。
                File.Move(remnant, Path.Combine(backupDir, Path.GetFileName(remnant) + "." + stamp), overwrite: false);
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
                || read.File!.Revision != expectedRevision
                || IsOwnerExpired(lease))
                return MutateReject("lease_stale_generation");

            if (read.File.Diag?.ResidueReconcilePending == true) return MutateReject("residue_reconcile_pending");
            if (checkSwitchGate && read.File.Diag?.SwitchGateActive == true) return MutateReject("switch_gate_active");

            var reason = mutate(read.File);
            if (reason is not null) return MutateReject(reason);

            read.File.Revision += 1;
            lease.HeartbeatSeq += 1;
            lease.LastHeartbeatUtc = now;
            Publish(read.File);
            _lastOwnerWriteMono = _monotonic(); // 一律 Publish 成功后刷新（R5.1 四轮 P1-② 纪律延伸）
            return new LeaseMutateResult { Success = true, Reason = null, File = read.File };
        });
    }

    private static LeaseMutateResult MutateReject(string reason) => new() { Success = false, Reason = reason, File = null };

    // ============================================================

    /// <summary>
    /// 跨进程锁内执行「读取→判定→校验→更新→发布」全程。
    /// 首次写入才建目录；锁对象固定 arbitration-lease.lock，OpenOrCreate 打开后永不替换/删除/清空。
    /// </summary>
    private T WithLock<T>(Func<LeaseReadResult, T> action)
    {
        Directory.CreateDirectory(_configDir); // §6.4：构造零副作用，首次写入才建目录
        return WithLockContentionRetry(() =>
        {
            using var lockStream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var read = ReadCore();
            return action(read);
        });
    }

    /// <summary>
    /// 锁争用有界重试（R5.2 B2-α 实证落地）：FileShare.None 跨进程/跨实例互斥下瞬时碰撞属预期并发形态——
    /// 单次操作持锁极短，碰撞方有界重试（80×15ms≈1.2s 预算）即可随持锁方释放收敛；整段「读取→判定→更新→发布」
    /// 重试安全（每轮重新盘读并以修订号守卫，不产生重复副作用）。仅兜底 IOException（锁/文件瞬时争用）；
    /// 其他异常（编程错误/损坏）不掩饰、响亮抛出。预算耗尽后 IOException 原样上抛=响亮失败不静默。
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
            catch (IOException) when (attempt < maxAttempts)
            {
                System.Threading.Thread.Sleep(15);
            }
        }
    }

    /// <summary>原子发布：UTF8 无 BOM + 临时文件（同目录 ".guid.tmp"）→ 同目录原子替换（overwrite），finally 清残件。</summary>
    private void Publish(LogicalOwnerLeaseFile file)
    {
        file.Version = SupportedVersion; // §4.0：写入一律 version 2（v1 向后读兼容只发生在读取方向）
        var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(file, JsonOptions));
        var tmp = Path.Combine(_configDir, ".lease-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _leasePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>UTC 诊断性 TTL 判定（仅用于 Read 状态展示；§6.3：UTC 仅诊断，不作资格裁决依据）。</summary>
    private static bool IsExpired(LeaseSegment lease, DateTimeOffset now)
        => lease.LastHeartbeatUtc.AddSeconds(lease.TtlSeconds) < now;

    /// <summary>v2 责任段结构校验：Submission 完整身份/枚举、Operations 必填身份与状态枚举合法、RequestIdentity 唯一。</summary>
    private static bool ValidateHandoffSegment(LeaseHandoffSegment? handoff, out string detail)
    {
        detail = "";
        if (handoff is null) return true;
        if (handoff.Operations is null)
        {
            detail = "租约文件 v2 Handoff.Operations 缺失（责任完整性校验失败），原件保留留痕。";
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

            // 关联一致性：未决发送必须有对应操作记录（孤儿 Submission=交叉不一致，不得推导空闲/可发送）。
            var linked = handoff.Operations.Any(o => o is not null
                && string.Equals(o.SubmissionIdentity, sub.SubmissionIdentity, StringComparison.Ordinal)
                && o.LastSendSeq == sub.SendSeq);
            if (!linked)
            {
                detail = "租约文件 v2 Submission 无关联 Operations 记录（交叉不一致），原件保留留痕。";
                return false;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in handoff.Operations)
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
        }

        return true;
    }

    /// <summary>残件匹配（P2-⑥复核：仅本组件专属命名 ".lease-*.tmp"，不波及配置根其他文件）。</summary>
    private static bool IsLeaseResidueFileName(string name)
        => name.StartsWith(".lease-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);

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
