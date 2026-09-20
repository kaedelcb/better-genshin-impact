using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>许可请求类别（§3.3：操作身份与处理状态分离）。</summary>
public enum AdmissionKind
{
    /// <summary>创建新操作（仅可信适配器在「无既有身份」时发起；身份分配+Operations 登记=同一次原子发布）。</summary>
    Create,
    /// <summary>续用已有操作（显式声明续用意图；Operations 记录缺失=stale_operation_identity 响亮拒绝，绝不回退创建/换身份/重绑）。</summary>
    ContinueUse,
}

/// <summary>许可请求（可信入口上下文+候选；入口适配器在汇聚前保留来源）。</summary>
public sealed class AdmissionRequest
{
    /// <summary>入口类别命名空间（调用方代码位置判定：manual/v2/resume/system/trigger），不用 opcode/配置名/远程自报字段推断。</summary>
    public string Namespace { get; set; } = "manual";
    /// <summary>请求身份（Create=空由门面首次接纳分配一次；ContinueUse=必填既有身份）。</summary>
    public string RequestIdentity { get; set; } = "";
    public AdmissionKind Kind { get; set; } = AdmissionKind.Create;
    /// <summary>诊断用自由文本（入口名/操作者），不参与判定。</summary>
    public string SourceDetail { get; set; } = "";
    /// <summary>仲裁候选（八段身份由适配器按 §2.2 全字段映射填好；ActionId=null 确定性派生；登记时冻结快照）。</summary>
    public ArbitrationCandidate Candidate { get; set; } = new();
    /// <summary>线上提交键（无法确定性推导时显式携带，逐操作 §6.1 映射表）。</summary>
    public string? WireSubmitKey { get; set; }
    /// <summary>
    /// **进程内不可变请求上下文**（§13.10 A1/A2）：由**可信适配器**在入队时冻结携带，随获选排队项一路传到 Sender。
    /// **不参与任何序列化**（租约/台账均不落此字段）；用于让 Sender 消费「Runner 当时提交的那份请求」，
    /// 而不是从当前流程定义/运行对象重建。**上下文缺失时调用方必须响亮拒绝，不得静默重建后发送。**
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? ProcessLocalContext { get; set; }
    /// <summary>candidateId→runId→首节点提交键（E1 流程绑定：首绑写入、再绑必须一致，不可改写）。</summary>
    public string? RunBinding { get; set; }
    /// <summary>运行台账合法游标（Runner 后继授权：游标合法+授权未消费双条件）。</summary>
    public string? CursorRef { get; set; }
    public long? CursorRevision { get; set; }
}

/// <summary>
/// E2 恢复准入请求（冻结稿 v8 §5.1：恢复不产候选、不排序——过恢复专用准入边界）。
/// 恢复分支：paused-continue（暂停续行——仅解除调度暂停，实际执行留待 §5.2 后继提交边界）/
/// interrupted-relocate（中断游标重定位——A6 票据恢复的状态机消费侧归 R5.3，本边界只负责准入与责任持久化）。
/// </summary>
public sealed class RecoveryAdmissionRequest
{
    /// <summary>诊断用自由文本（入口名），不参与判定。</summary>
    public string SourceDetail { get; set; } = "";
    /// <summary>被恢复运行（台账身份匹配依据；candidateId→runId 绑定不可改写同口径）。</summary>
    public string RunId { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    /// <summary>恢复分支（paused-continue / interrupted-relocate）。</summary>
    public string RestoreBranch { get; set; } = "";
    /// <summary>作用域 bgi:{实例}:{bgiEpoch}——首次构造捕获固定，锁内只比较不重写。</summary>
    public string Scope { get; set; } = "";
}

/// <summary>许可结果类别。</summary>
public enum AdmissionResultKind
{
    /// <summary>发送已受理（接管台账已持久化并可重建，Submission 已关闭）。</summary>
    Accepted,
    /// <summary>终局拒绝（不得静默返回成功）。</summary>
    TerminalRejected,
    /// <summary>可重试拒绝（白名单内、预算/窗口内可经 RetryAsync 重新 Admit）。</summary>
    RetryableRejected,
    /// <summary>未获选终局（含胜者引用与压制来源；不悬置不自动进入下一轮）。</summary>
    NotSelected,
    /// <summary>F11 独立停止闸门（先于租约获取与一切排序，不发生租约副作用）。</summary>
    F11Blocked,
    /// <summary>执行占用→需安全交接确认（分流交接状态机，语义验证归 R5.3）。</summary>
    NeedPreemptConfirm,
    /// <summary>权威执行事实未知→待对账（禁止换键重跑、禁止回 Idle 重提交）。</summary>
    NeedReconcile,
    /// <summary>发送结果未知→Submission.Reconciling 保守停驻（不重发）。</summary>
    Reconciling,
    /// <summary>门面/存取响亮拒绝（invalid_request/lease_stale_generation/stale_operation_identity/operations_capacity_full 等）。</summary>
    Error,
}

/// <summary>许可结果（结构化载荷：原因码/胜者引用/压制来源/完整判定——日志与夹具按载荷断言）。</summary>
public sealed class AdmissionResult
{
    public AdmissionResultKind Kind { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Detail { get; set; } = "";
    public string RequestIdentity { get; set; } = "";
    public string? WinnerCandidateId { get; set; }
    public string SuppressionSource { get; set; } = "";
    public string? SubmissionIdentity { get; set; }
    public int SendSeq { get; set; }
    /// <summary>当轮完整判定（决策集合完整性：冲突组/重复项/非胜者结果，不只看胜者）。</summary>
    public ArbitrationDecision? Decision { get; set; }

    public static AdmissionResult Of(AdmissionResultKind kind, string reasonCode, string detail, string requestIdentity = "")
        => new() { Kind = kind, ReasonCode = reasonCode, Detail = detail, RequestIdentity = requestIdentity };
}

/// <summary>锁外发送分派载荷（完整发送关联身份——与 Submission/Operations/台账/回执处理/对账同一规则）。</summary>
public sealed class SubmissionDispatch
{
    public string RequestIdentity { get; set; } = "";
    public string SubmissionIdentity { get; set; } = "";
    public int SendSeq { get; set; }
    public string CandidateId { get; set; } = "";
    public string StableIdentity { get; set; } = "";
    public string ActionId { get; set; } = "";
    public string TargetEpoch { get; set; } = "";
    public string ResourceRef { get; set; } = "";
    public string Intent { get; set; } = "";
    public string? WireSubmitKey { get; set; }
    public ArbitrationCandidate Candidate { get; set; } = new();
    /// <summary>
    /// **本轮获选排队项携带的进程内不可变请求上下文**（§13.10 A2）：`SubmitAsync` 的调用方经
    /// <see cref="AdmissionRequest.ProcessLocalContext"/> 传入，门面**原样**附到派发对象上；
    /// Sender 只消费本字段，**不得**用「最新 Operation」或执行上下文重新拼接另一轮上下文。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object? ProcessLocalContext { get; set; }
}

/// <summary>发送结果三态（§4.2 三态对账：受理/确定拒绝/未知——未知立即转对账不再重试）。</summary>
public abstract record SendOutcome
{
    /// <summary>受理（evidenceSource=原始回执词+产生端；runId=托管流程关联运行）。</summary>
    public sealed record Accepted(string EvidenceSource, string? RunId) : SendOutcome;
    /// <summary>经关联验证的确定未受理（先关闭 Submission 再按 §3.3 记可重试/终局）。</summary>
    public sealed record Rejected(string ReasonCode, bool Retryable, string EvidenceSource) : SendOutcome;
    /// <summary>未知（Submission.Reconciling；不换键重跑）。</summary>
    public sealed record Unknown(string Detail) : SendOutcome;
}

/// <summary>
/// 显式对账结清证据（§8 待对账处置入口=owner 显式对账动作/权威事实到达；仅两分支——
/// 受理且接管持久化 / 经关联验证的确定未受理，不设第三关闭依据）。
/// </summary>
public abstract record ReconcileSettlement
{
    /// <summary>权威事实确认「曾受理」（evidenceSource=对账结论+产生端，保留原始证据来源、不伪造远端回执词）。证据必须携带原发送关联——旧轮次证据不得关闭新轮次责任。</summary>
    public sealed record Accepted(string SubmissionIdentity, int SendSeq, string EvidenceSource, string? RunId) : ReconcileSettlement;
    /// <summary>权威事实确认「确定未受理」。同上携带原发送关联。</summary>
    public sealed record NotAccepted(string SubmissionIdentity, int SendSeq, string ReasonCode, bool Retryable, string EvidenceSource) : ReconcileSettlement;
}

/// <summary>
/// 门面钩子（I-3：事实供给方在锁外先刷新、锁内只消费本地最新快照——F11/票据/资格/epoch 均为本地事实，
/// 锁内最终校验链在 MutateHandoff 权威串行边界内复核；含远端查询的取证一律锁外有界等待）。
/// 发送与台账持久化在锁外。
/// </summary>
public sealed class AdmissionHooks
{
    /// <summary>F11 独立停止闸门（本地事实；锁内最终校验会复核）。</summary>
    public Func<bool> F11Active { get; set; } = () => false;
    /// <summary>仲裁全局事实快照（票据/执行占用/未知——本地事实，资格判定的全部输入）。</summary>
    public Func<ArbitrationFacts> FactsProvider { get; set; } = () => new ArbitrationFacts();
    /// <summary>当前 BGI epoch（本地缓存事实；epoch 固定合同：只比较不重写）。</summary>
    public Func<string> BgiEpochProvider { get; set; } = () => "";
    /// <summary>资格快照供给（每候选——本地事实）。</summary>
    public Func<AdmissionRequest, CandidateEligibility> EligibilityProvider { get; set; } = _ => new CandidateEligibility();
    /// <summary>锁外发送（既有提交路径原样；发布失败/锁内复核失败=不得调用）。</summary>
    public Func<SubmissionDispatch, Task<SendOutcome>> Sender { get; set; } = _ => Task.FromResult<SendOutcome>(new SendOutcome.Unknown("sender_not_configured"));
    /// <summary>
    /// 受理→接管台账持久化（锁外；E3/E4/E5=ExternalStartLedger.RecordAccepted+ConfirmRebuildable，E1/E2=RunStore 运行记录委托）。
    /// 返回 null=已持久化且可跨重启重建；非 null=失败原因（Submission 保持未决，保守待对账）。
    /// </summary>
    public Func<ExternalStartLedgerEntry, Task<string?>> TakeoverPersist { get; set; } = _ => Task.FromResult<string?>("takeover_persist_not_configured");
    /// <summary>
    /// 台账权威终态交叉确认（MarkOperationTerminal 前置：关联 job 权威终态+台账一致才允许 Accepted→TerminalCompleted）。
    /// 未配置=一律不允许终局完成（保守）。
    /// </summary>
    public Func<string, int, bool>? TakeoverTerminalConfirmed { get; set; }
    /// <summary>并发屏障夹具接缝（九类交错可控屏障；生产=null 零开销）。</summary>
    public AdmissionBarriers? Barriers { get; set; }
}

/// <summary>并发屏障接缝（夹具注入可控屏障覆盖九类交错；全部可选）。</summary>
public sealed class AdmissionBarriers
{
    /// <summary>轮次串行段前、快照前（夹具确定性：多源并发入队收齐后放行快照——不持 _gate 等待，入队方不致饿死）。</summary>
    public Func<Task>? BeforeRoundSnapshot { get; set; }
    /// <summary>入队完成后（锁外同步调用，须同步完成）——夹具计数收齐并发入队后放行 <see cref="BeforeRoundSnapshot"/>。</summary>
    public Func<Task>? AfterEnqueue { get; set; }
    /// <summary>轮次快照后、裁决前。</summary>
    public Func<Task>? AfterRoundSnapshot { get; set; }
    /// <summary>占位发布前（锁内最终校验链在占位事务内复核——本屏障用于注入「校验后事实变化」反例）。</summary>
    public Func<Task>? BeforeOccupyPublish { get; set; }
    /// <summary>占位发布后、锁外发送前。</summary>
    public Func<Task>? AfterOccupyBeforeSend { get; set; }
    /// <summary>受理后、台账接管持久化前。</summary>
    public Func<Task>? AfterAcceptBeforeLedger { get; set; }
    /// <summary>台账已持久化、Submission 关闭前。</summary>
    public Func<Task>? AfterLedgerBeforeClose { get; set; }
}

/// <summary>
/// 仲裁门面（R5.2 冻结稿 v8 §3/§4 核心——统一链路：可信入口上下文→无副作用解析→仲裁轮次→
/// 锁内最终校验→Submission 落盘→锁外发送→三态对账→受理先台账后关闭）。
/// 无双跑承诺：①BGI 执行锁=物理互斥（既有）；②本门面+租约持久化=逻辑准入互斥；③提交点清单归 B4。
/// 排序仅对本进程内队列成立；跨进程一致性由租约文件锁+Submission 不覆盖+锁内状态复核保证。
/// 一切责任变更经 MutateHandoff 权威串行边界；变更一律使用流程起点捕获的所有者身份（所有权更替=旧身份响亮拒绝）。
/// </summary>
public sealed class ArbitrationAdmissionService
{
    /// <summary>主槽位上限（§4.1a：primarySlotsUsed=Active 数+TerminalPendingTransfer 数）。</summary>
    public const int PrimarySlotLimit = 32;
    /// <summary>墓碑环形上限。</summary>
    public const int TombstoneLimit = 256;
    /// <summary>墓碑最短保留（重放安全：兼容重试预算窗口为秒级有界，24h 墓碑 ≫ 合法重放窗口）。</summary>
    public static readonly TimeSpan TombstoneRetain = TimeSpan.FromHours(24);
    /// <summary>重试预算上限（既有无损拒绝 1s×6 语义的次数口径）。</summary>
    public const int RetryBudgetMax = 6;
    /// <summary>有界重试窗口（首次确定拒绝派生，持久化不得重置）。</summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(30);

    private readonly ArbitrationLeaseStore _store;
    private readonly AdmissionHooks _hooks;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _queueLock = new();
    private readonly List<PendingAdmission> _queue = [];
    /// <summary>本进程在途处理者（合并语义按「本进程在途」判定；跨实例一致性靠锁内状态复核，不靠本集合）。</summary>
    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);

    private sealed class PendingAdmission
    {
        public AdmissionRequest Request { get; init; } = new();
        public TaskCompletionSource<AdmissionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>登记/入队时捕获的所有者身份（一切变更沿用本快照——所有权更替=旧排队流程响亮拒绝，不借新身份继续）。</summary>
        public string CapturedLeaseId { get; init; } = "";
        public string CapturedOwnerEpoch { get; init; } = "";
    }

    public ArbitrationAdmissionService(ArbitrationLeaseStore store, AdmissionHooks hooks, Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _hooks = hooks;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    // ============================================================
    // 租约所有权（门面不自行接管：失联接管走 LeaseTakeoverObserver 证据路径，由宿主编排）
    // ============================================================

    /// <summary>确保本进程持有租约（held=响亮拒绝——接管编排归宿主；§6.2 过期即禁启）。</summary>
    public LeaseOpResult EnsureOwnership(string ownerEpoch, int ttlSeconds = 15)
        => _store.TryAcquire(ownerEpoch, ttlSeconds);

    /// <summary>带接管证据的获取（§6.3：证据仅 LeaseTakeoverObserver 单调观察满 TTL 产出，锁内复核与时下文件一致才放行）。</summary>
    public LeaseOpResult EnsureOwnership(string ownerEpoch, int ttlSeconds, LeaseTakeoverEvidence? evidence)
        => _store.TryAcquire(ownerEpoch, ttlSeconds, evidence);

    /// <summary>登记时冻结候选快照（不可变消费记录：占位/重试按快照比对与重建，不凭调用方后置可变对象）。</summary>
    internal static ArbitrationCandidate CloneCandidate(ArbitrationCandidate c)
        => new()
        {
            Scope = c.Scope,
            Namespace = c.Namespace,
            WorkflowId = c.WorkflowId,
            TriggerOccurrenceId = c.TriggerOccurrenceId,
            RunId = c.RunId,
            NodeId = c.NodeId,
            Occurrence = c.Occurrence,
            LoopIteration = c.LoopIteration,
            Attempt = c.Attempt,
            Tier = c.Tier,
            Priority = c.Priority,
            ScheduledAt = c.ScheduledAt,
            PayloadFingerprint = c.PayloadFingerprint,
            ResourceRef = c.ResourceRef,
            Intent = c.Intent,
            ActionId = c.ActionId,
        };

    // ============================================================
    // 创建/续用（§2.1 创建/续用分离）
    // ============================================================

    /// <summary>
    /// 提交执行请求（启动类候选→仲裁轮次）。
    /// Create：身份分配+Operations 登记=同一次原子发布（F11 先于租约——激活时不发生租约副作用）；
    /// ContinueUse：按 §3.3 请求状态分类表返回；本进程无在途处理者的 Queued/InRound 操作=重新驱动（再准入轮次），
    /// 记录缺失=stale_operation_identity。
    /// </summary>
    public async Task<AdmissionResult> SubmitAsync(AdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidate is null) return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "候选缺失。");

        // F11 独立停止闸门：先于租约获取与一切排序——不发生租约副作用（§7.1；占位事务内还会锁内复核）。
        if (_hooks.F11Active())
            return AdmissionResult.Of(AdmissionResultKind.F11Blocked, "f11_active", "F11 独立停止闸门激活（不发生租约副作用）。", request.RequestIdentity);

        // N2：读快照+创建登记在同一进程内串行段（修订不漂移；跨线程并发提交不争用存取锁）。
        LeaseReadResult read;
        LeaseMutateResult? register = null;
        string? requestIdentity = null;
        AdmissionRequest? frozen = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            read = _store.Read();
            if (read.Status is not (ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)
                && read.File?.Lease is not null
                && request.Kind == AdmissionKind.Create)
            {
                // 创建路径：身份分配→触发出现身份回填（§2.2/I3："{requestIdentity}" 占位符在身份分配后回填，
                // 适配器无需预知身份）→内部冻结副本→Operations 登记=同一次原子发布（容量检查/清理迁移/新登记同边界）。
                var rid = Guid.NewGuid().ToString("N");
                requestIdentity = rid;
                var frz = new AdmissionRequest
                {
                    Namespace = request.Namespace,
                    RequestIdentity = rid,
                    Kind = AdmissionKind.Create,
                    SourceDetail = request.SourceDetail,
                    Candidate = CloneCandidate(request.Candidate),
                    WireSubmitKey = request.WireSubmitKey,
                    RunBinding = request.RunBinding,
                    CursorRef = request.CursorRef,
                    CursorRevision = request.CursorRevision,
                    // §13.10 A2（[纠正·2026-09-21] 会诊阻断项）：冻结副本必须**原样携带**进程内不可变请求上下文——
                    // 漏掉它会让正常后继路径在 Sender 处确定性落到 successor_context_missing。
                    ProcessLocalContext = request.ProcessLocalContext,
                };
                frozen = frz;
                // 内部冻结（B3）：登记/队列/裁决/占位/发送一律只消费本副本——调用方后置修改/替换不改变已登记事实。
                frz.Candidate.TriggerOccurrenceId = (frz.Candidate.TriggerOccurrenceId ?? "")
                    .Replace("{requestIdentity}", rid, StringComparison.Ordinal);
                var stableIdentity = ArbitrationOrdering.BuildStableIdentity(frz.Candidate);
                var candidateId = ArbitrationOrdering.DeriveCandidateId(stableIdentity);
                var targetEpoch = ExtractEpoch(frz.Candidate.Scope);
                var sortKeyFingerprint = SortKeyFingerprintOf(frz.Candidate, frz.RunBinding, frz.CursorRef, frz.CursorRevision);
                var now = _utcNow();
                register = _store.MutateHandoffLatest(read.File.Lease.LeaseId, read.File.Lease.OwnerEpoch, file =>
                {
                    var capacity = EnsureCapacityForCreate(file, now);
                    if (capacity is not null) return capacity;
                    file.Handoff ??= new LeaseHandoffSegment();
                    file.Handoff.Operations.Add(new OperationRecord
                    {
                        RequestIdentity = rid,
                        CandidateId = candidateId,
                        PayloadFingerprint = frz.Candidate.PayloadFingerprint ?? "",
                        SortKeyFingerprint = sortKeyFingerprint,
                        Candidate = CloneCandidate(frz.Candidate),
                        RunBinding = frz.RunBinding,
                        CursorRef = frz.CursorRef,
                        CursorRevision = frz.CursorRevision,
                        RequestState = OperationRequestState.Queued,
                        LastSendSeq = 0,
                        Zone = OperationZone.Active,
                        UpdatedRevision = file.Revision + 1,
                        UpdatedAtUtc = now,
                        TargetEpoch = targetEpoch,
                        WireSubmitKey = frz.WireSubmitKey,
                        ResourceRef = frz.Candidate.ResourceRef ?? "",
                        Intent = frz.Candidate.Intent ?? "",
                    });
                    return null;
                });
            }
        }
        finally
        {
            _gate.Release();
        }

        if (read.Status == ArbitrationLeaseStatus.Corrupt)
            return AdmissionResult.Of(AdmissionResultKind.Error, "corrupt", "租约文件损坏（保守待对账）。", request.RequestIdentity);
        if (read.Status == ArbitrationLeaseStatus.Unsupported)
            return AdmissionResult.Of(AdmissionResultKind.Error, "unsupported_version", "租约文件版本不受支持。", request.RequestIdentity);
        // UTC 诊断态（Valid/Expired）不作资格裁决——所有者资格一律由 MutateHandoff/Latest 锁内核单调 TTL 判定（R5.1 §6.3 合同）。
        if (read.File?.Lease is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约（先 EnsureOwnership）。", request.RequestIdentity);

        if (request.Kind == AdmissionKind.ContinueUse)
        {
            var continueIdentity = ArbitrationOrdering.BuildStableIdentity(request.Candidate);
            return await ContinueUseAsync(request, read.File, continueIdentity).ConfigureAwait(false);
        }

        request.RequestIdentity = requestIdentity!; // 身份回显=API 合同；其余字段一律不回调用方持有的可变对象
        if (!register!.Success)
            return ClassifyMutateReject(register.Reason ?? "invalid_request", requestIdentity!);

        // —— 入队→仲裁轮次（原子入队快照：进入串行段时快照并清空；登记时所有者身份随排队快照，B8）——
        var pending = Enqueue(frozen!, read.File.Lease);
        _ = Task.Run(() => DrainRoundAsync());
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private PendingAdmission Enqueue(AdmissionRequest request, LeaseSegment captured)
    {
        var pending = new PendingAdmission
        {
            Request = request,
            CapturedLeaseId = captured.LeaseId,
            CapturedOwnerEpoch = captured.OwnerEpoch,
        };
        lock (_queueLock)
        {
            _queue.Add(pending);
            _inflight.Add(request.RequestIdentity);
        }

        // AfterEnqueue 屏障：夹具确定性收齐信号（测试接缝，锁外同步调用须同步完成——配合 BeforeRoundSnapshot 计数放行）。
        // 接缝异常/返回 null 防御：响亮完成本 pending 并回滚在途登记（与 BeforeRoundSnapshot 兜底对齐，防未来夹具误用悬挂）。
        try
        {
            var afterEnqueue = _hooks.Barriers?.AfterEnqueue;
            if (afterEnqueue is not null) (afterEnqueue() ?? Task.CompletedTask).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            lock (_queueLock)
            {
                _queue.Remove(pending);
                _inflight.Remove(request.RequestIdentity);
            }

            pending.Completion.TrySetResult(AdmissionResult.Of(AdmissionResultKind.Error, "internal_error", "入队后屏障异常（响亮失败不静默）。", request.RequestIdentity));
        }

        return pending;
    }

    /// <summary>续用路径（§3.3 分类表五行——状态读取在权威串行边界内完成；无在途处理者的 Queued/InRound 重新驱动）。</summary>
    private async Task<AdmissionResult> ContinueUseAsync(AdmissionRequest request, LogicalOwnerLeaseFile file, string stableIdentity)
    {
        var ops = file.Handoff?.Operations ?? [];
        var op = ops.FirstOrDefault(o => string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
        if (op is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity",
                "续用方 Operations 记录缺失=响亮拒绝（绝不回退为创建新操作/换身份/重新绑定）。", request.RequestIdentity);

        // 候选身份核验（B3：换 workflow/scope=换候选号——同载荷/排序键不得命中他者缓存）。
        if (!string.Equals(op.CandidateId, ArbitrationOrdering.DeriveCandidateId(stableIdentity), StringComparison.Ordinal))
            return AdmissionResult.Of(AdmissionResultKind.TerminalRejected, "identity_conflict",
                "候选身份不一致（换 workflow/scope 不得命中既有操作缓存）。", request.RequestIdentity);
        // 绑定一致性（B7：再绑必须一致——runBinding/cursorRef/cursorRevision 任一不一致=终局拒绝，不可改写）。
        if ((request.RunBinding is { } runBinding && !string.Equals(op.RunBinding, runBinding, StringComparison.Ordinal))
            || (request.CursorRef is { } cursorRef && (!string.Equals(op.CursorRef, cursorRef, StringComparison.Ordinal) || op.CursorRevision != request.CursorRevision)))
            return AdmissionResult.Of(AdmissionResultKind.TerminalRejected, "binding_conflict",
                "runBinding/cursorRef 绑定不一致（不可改写）。", request.RequestIdentity);
        // 身份与载荷/排序键一致性检查先于缓存结果返回（§3.3-5）；冲突不抹掉原请求已受理事实（§4.1a 附属冻结）。
        // （绑定一致性已在上方显式核验——指纹比较按 op 绑定重放，请求省略绑定不构成指纹冲突。）
        if (!string.Equals(op.PayloadFingerprint, request.Candidate.PayloadFingerprint ?? "", StringComparison.Ordinal)
            || !string.Equals(op.SortKeyFingerprint, SortKeyFingerprintOf(request.Candidate, op.RunBinding, op.CursorRef, op.CursorRevision), StringComparison.Ordinal))
            return AdmissionResult.Of(AdmissionResultKind.TerminalRejected, "identity_conflict",
                "同身份不同规范化载荷/排序键=终局拒绝（原请求事实不变）。", request.RequestIdentity);

        // 去重合并关联（B6）：未镜像完成前按胜者当前事实分类；已镜像终态的按本记录分类（落入下方 switch）。
        if (op.MergedInto is { } mergedInto
            && op.RequestState is OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected)
        {
            var target = ops.FirstOrDefault(o => string.Equals(o.RequestIdentity, mergedInto, StringComparison.Ordinal));
            return target?.RequestState switch
            {
                OperationRequestState.Accepted or OperationRequestState.TerminalCompleted =>
                    new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_accepted", Detail = "去重合并：共享胜者受理结果（不新增发送者）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = target.SubmissionIdentity, SendSeq = target.LastSendSeq },
                OperationRequestState.TerminalRejected =>
                    AdmissionResult.Of(AdmissionResultKind.TerminalRejected, target.LastResult?.ReasonCode ?? "terminal_rejected", "去重合并：共享胜者终局拒绝。", request.RequestIdentity),
                OperationRequestState.NotSelected =>
                    new AdmissionResult { Kind = AdmissionResultKind.NotSelected, ReasonCode = target.LastResult?.ReasonCode ?? "not_selected", Detail = "去重合并：共享胜者未获选终局。", RequestIdentity = request.RequestIdentity, WinnerCandidateId = target.LastResult?.WinnerRef, SuppressionSource = target.LastResult?.SuppressionSource ?? "" },
                null => AdmissionResult.Of(AdmissionResultKind.Error, "merged_target_missing", "合并目标记录缺失=响亮拒绝。", request.RequestIdentity),
                _ => AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "in_flight", "去重合并：胜者处理在途（合并，不新增发送者）。", request.RequestIdentity),
            };
        }

        switch (op.RequestState)
        {
            // 排队/裁决中：本进程在途=合并不新增发送者；无在途处理者（闸门拒绝/事实未知/异常遗留）=重新驱动。
            case OperationRequestState.Queued:
            case OperationRequestState.InRound:
            {
                bool inFlight;
                lock (_queueLock) inFlight = _inflight.Contains(request.RequestIdentity);
                if (inFlight)
                    return AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "in_flight", "已有处理在途（合并，不新增发送者）。", request.RequestIdentity);
                // 重新驱动：按 Operations 权威快照重建请求再准入（不凭调用方重报——候选取自持久化快照）。
                var redrive = new AdmissionRequest
                {
                    Namespace = op.Candidate?.Namespace ?? request.Namespace,
                    Kind = AdmissionKind.ContinueUse,
                    RequestIdentity = request.RequestIdentity,
                    Candidate = CloneCandidate(op.Candidate ?? request.Candidate),
                    WireSubmitKey = op.WireSubmitKey,
                    RunBinding = op.RunBinding,
                    CursorRef = op.CursorRef,
                    CursorRevision = op.CursorRevision,
                    // §13.10 A2：续用同样必须携带调用方传入的进程内不可变上下文（不得丢弃后由 Sender 重建）。
                    ProcessLocalContext = request.ProcessLocalContext,
                };
                var pending = Enqueue(redrive, file.Lease!);
                _ = Task.Run(() => DrainRoundAsync());
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            // 已占位/发送中：合并到已有处理，不新增发送者。
            case OperationRequestState.Granted:
            case OperationRequestState.Sending:
                return AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "in_flight", "已有发送责任在途（合并，不新增发送者）。", request.RequestIdentity);
            // 未知/待对账：返回对账状态，不重发。
            case OperationRequestState.Reconciling:
                return AdmissionResult.Of(AdmissionResultKind.Reconciling, "reconciling", "发送结果未知，保守待对账（不重发）。", request.RequestIdentity);
            // 已受理/终局完成：返回既有结果。
            case OperationRequestState.Accepted:
                return new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_accepted", Detail = "已受理（返回既有结果）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = op.SubmissionIdentity, SendSeq = op.LastSendSeq, WinnerCandidateId = op.CandidateId, SuppressionSource = op.LastResult?.EvidenceSource ?? "" };
            case OperationRequestState.TerminalCompleted:
                return new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_terminal", Detail = "终局完成（返回既有结果）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = op.SubmissionIdentity, SendSeq = op.LastSendSeq };
            // 可重试拒绝：预算/窗口内由唯一重试者重新 Admit（RetryAsync）。
            case OperationRequestState.RetryableRejected:
                return AdmissionResult.Of(AdmissionResultKind.RetryableRejected, op.LastResult?.ReasonCode ?? "retryable_rejected", "可重试拒绝（经 RetryAsync 重新 Admit；并发重试者合并）。", request.RequestIdentity);
            // 终局拒绝/未获选：返回终局拒绝——不得静默返回成功。
            case OperationRequestState.NotSelected:
                return new AdmissionResult { Kind = AdmissionResultKind.NotSelected, ReasonCode = op.LastResult?.ReasonCode ?? "not_selected", Detail = "未获选终局（含胜者引用与压制来源）。", RequestIdentity = request.RequestIdentity, WinnerCandidateId = op.LastResult?.WinnerRef, SuppressionSource = op.LastResult?.SuppressionSource ?? "" };
            default:
                return AdmissionResult.Of(AdmissionResultKind.TerminalRejected, op.LastResult?.ReasonCode ?? "terminal_rejected", "终局拒绝。", request.RequestIdentity);
        }
    }

    // ============================================================
    // E2 恢复专用准入边界（冻结稿 v8 §5.1 行1/行2 冻结——不排序、不产候选、不换键重跑）
    // ============================================================

    /// <summary>
    /// 恢复专用准入（E2：面板恢复/启动移交 resume 共用）：
    /// ①F11 前置零副作用；②恢复意图登记（Operations 直接 InRound——恢复不入队不排序，无轮次快照；
    ///   崩溃窗「登记后占位前」由 RecoverAfterRestart ② 三无确认终局中止同口径覆盖）；
    /// ③锁内共同闸门复用 §4.2 统一校验链（F11 复核/票据压制/事实未知/执行占用/切换闸门/目标 epoch 只比较/
    ///   Pending 阶段与身份——恢复 stableIdentity 由 resume 伪候选派生≠授权抢占方身份，Pending 存续期一律
    ///   pending_conflict 保守拒绝；「用于完成该交接的合法恢复动作不受此禁」的授权恢复豁免归 R5.3 A6 消费侧落实）；
    /// ④占位（Submission 落盘）→锁外发送→三态对账——与启动同一提交边界（§4.2/§4.2c）。
    /// 保留原票据与 RestorePending 责任：本方法绝不触碰 Pending 段、绝不改写既有操作为新作业。
    /// 恢复操作不走 RetryAsync（重试轮次排序违反「恢复不排序」——可重试拒绝由入口以新操作重新发起，每次=新操作）。
    /// </summary>
    public async Task<AdmissionResult> AdmitRecoveryAsync(RecoveryAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.WorkflowId))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "恢复准入请求缺 RunId/WorkflowId。");
        if (request.RestoreBranch is not ("paused-continue" or "interrupted-relocate"))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "未知恢复分支：" + request.RestoreBranch);

        // ① F11 独立停止闸门：先于一切操作级租约副作用（§7.1 口径=Operations/Submission 零副作用；
        //    所有权自举/心跳不受此限——会诊 建议-3 措辞对齐；占位事务内还会锁内复核）。
        if (_hooks.F11Active())
            return AdmissionResult.Of(AdmissionResultKind.F11Blocked, "f11_active", "F11 独立停止闸门激活（不发生租约副作用）。");

        // N2 同口径（会诊 建议-2 处置）：读快照+恢复登记在同一进程内串行段（修订不漂移；与其他准入/结清串行）。
        string rid;
        string stableIdentity;
        string candidateId;
        string targetEpoch;
        DateTimeOffset now;
        AdmissionRequest inner;
        LeaseSegment lease;
        LeaseMutateResult register;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.Status == ArbitrationLeaseStatus.Corrupt)
                return AdmissionResult.Of(AdmissionResultKind.Error, "corrupt", "租约文件损坏（保守待对账）。");
            if (read.Status == ArbitrationLeaseStatus.Unsupported)
                return AdmissionResult.Of(AdmissionResultKind.Error, "unsupported_version", "租约文件版本不受支持。");
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约（先 EnsureOwnership）。");
            lease = read.File.Lease;

            // ② 恢复意图登记（持久化先于发送）：身份分配+Operations 登记=同一次原子发布；候选快照冻结（不可变消费记录）。
            rid = Guid.NewGuid().ToString("N");
            var candidate = new ArbitrationCandidate
            {
                Scope = request.Scope,
                Namespace = "resume",
                WorkflowId = request.WorkflowId,
                TriggerOccurrenceId = $"resume:{request.RestoreBranch}:{rid}", // 身份分配后一次性成形（无占位符往返）
                RunId = request.RunId,
                Intent = "resume",
                ResourceRef = "run:" + request.RunId,
            };
            stableIdentity = ArbitrationOrdering.BuildStableIdentity(candidate);
            candidateId = ArbitrationOrdering.DeriveCandidateId(stableIdentity);
            targetEpoch = ExtractEpoch(candidate.Scope);
            now = _utcNow();
            inner = new AdmissionRequest
            {
                Namespace = "resume",
                RequestIdentity = rid,
                Kind = AdmissionKind.Create,
                SourceDetail = request.SourceDetail,
                Candidate = candidate,
                RunBinding = request.RunId, // 绑定登记即固定（不可改写）
            };
            register = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var capacity = EnsureCapacityForCreate(file, now);
                if (capacity is not null) return capacity;
                file.Handoff ??= new LeaseHandoffSegment();
                file.Handoff.Operations.Add(new OperationRecord
                {
                    RequestIdentity = rid,
                    CandidateId = candidateId,
                    PayloadFingerprint = "",
                    SortKeyFingerprint = SortKeyFingerprintOf(candidate, inner.RunBinding, null, null),
                    Candidate = CloneCandidate(candidate),
                    RunBinding = request.RunId,
                    RequestState = OperationRequestState.InRound, // 恢复无轮次：登记即进入占位校验态
                    LastSendSeq = 0,
                    Zone = OperationZone.Active,
                    UpdatedRevision = file.Revision + 1,
                    UpdatedAtUtc = now,
                    TargetEpoch = targetEpoch,
                    ResourceRef = candidate.ResourceRef ?? "",
                    Intent = "resume",
                });
                return null;
            });
        }
        finally
        {
            _gate.Release();
        }

        if (!register.Success) return ClassifyMutateReject(register.Reason ?? "invalid_request", rid);

        // ③④ 锁内共同闸门+占位（统一校验链）→锁外发送→三态对账（统一提交边界）。
        var occupy = ValidateAndOccupy(inner, lease, stableIdentity, candidateId, targetEpoch,
            OperationRequestState.InRound, mergedIdentities: null, now, out var special);
        if (!occupy.Success) return await ClassifyRecoveryOccupyRejectAsync(inner, lease, occupy.Reason ?? "invalid_request").ConfigureAwait(false);
        if (special is not null) return AdmissionResult.Of(AdmissionResultKind.Error, special, "占位事务异常分支。", rid);

        SendOutcome outcome;
        try
        {
            outcome = await _hooks.Sender(BuildDispatch(inner, occupy.File!, stableIdentity, candidateId, targetEpoch)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new SendOutcome.Unknown("发送回调异常（保守待对账，不重发）：" + ex.GetType().Name); // I1 同口径
        }

        return await ReconcileOutcomeAsync(inner, lease, occupy.File!, outcome).ConfigureAwait(false);
    }

    /// <summary>
    /// 恢复占位拒绝分类（会诊 重要-1 处置——恢复操作无再驱动者：不走 RetryAsync、不入轮次）：
    /// 本地预检类拒绝（未发布发送许可+无未决发送责任+无当前处理者）按 §4.1a 判据表第一行同边界终局，
    /// 不遗留 Queued 孤儿占主槽位/阻塞同 runBinding 同胞的终局回写；执行占用保留可重试拒绝（窗口派生，到期扫描终局）。
    /// </summary>
    private async Task<AdmissionResult> ClassifyRecoveryOccupyRejectAsync(AdmissionRequest request, LeaseSegment lease, string reason)
    {
        switch (reason)
        {
            // 执行占用：无损拒绝类可重试（窗口派生不重置；恢复不重驱动——占用解除后由入口新操作重新发起）。
            case "execution_occupied":
                return (await RetryablePrecheckRejectAsync(request, lease, "execution_occupied", "执行占用（锁内复核——按无损拒绝类可重试处理）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 事实未知：终局中止但按待对账类别返回（禁止换键重跑；恢复操作本身未发布发送许可，不遗留可再驱动占位）。
            case "facts_unknown":
                return (await TerminatePrecheckAsync(request, lease, "facts_unknown", AdmissionResultKind.NeedReconcile, "权威执行事实未知→待对账（恢复操作终局中止：未发布发送许可；对账后由入口新操作重新发起）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 未决发送冲突：并发恢复仅一胜者——本操作终局中止（未发布发送许可）。
            case "submission_conflict":
                return (await TerminatePrecheckAsync(request, lease, "submission_conflict", AdmissionResultKind.Error, "存在未决发送（至多一笔）——恢复操作终局中止（并发恢复仅一胜者，未发布发送许可）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 状态已由其他处理者推进：不回退——返回当前事实分类（B1）。
            case "state_changed":
                return ClassifyCurrentState(request.RequestIdentity);
            // f11 锁内复核：终局中止+F11 类别（前置已拦，此处为竞态复核路径）。
            case "f11_active":
                return (await TerminatePrecheckAsync(request, lease, "f11_active", AdmissionResultKind.F11Blocked, "F11 独立停止闸门激活（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 票据/资格/epoch/绑定/Pending：终局拒绝（恢复无重驱动者，Queued 回退=永久孤儿）。
            case "ticket_suppressed" or "eligibility_lost" or "stale_epoch" or "identity_conflict" or "binding_conflict" or "pending_conflict":
                return (await TerminatePrecheckAsync(request, lease, reason, AdmissionResultKind.TerminalRejected, "恢复准入锁内复核拒绝（" + reason + "）——终局中止（未发布发送许可，修正事实后由入口新操作重新发起）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 切换闸门/租约资格/残件/容量等存取拒绝：同样终局中止（恢复操作不留 Queued）。
            default:
                return (await TerminatePrecheckAsync(request, lease, reason, AdmissionResultKind.Error, "恢复准入拒绝（" + reason + "）——终局中止（未发布发送许可）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
        }
    }

    // ============================================================
    // 仲裁轮次（§3.1：入队与「快照并移除」原子；当轮持续至占位成功或明确失败；排序仅进程内队列）
    // ============================================================

    private async Task DrainRoundAsync()
    {
        // 快照前屏障在串行段外等待（不持 _gate——否则并发提交方在登记段被挡，入队计数永远收不齐=夹具死锁）。
        try
        {
            if (_hooks.Barriers?.BeforeRoundSnapshot is { } beforeSnapshot) await beforeSnapshot().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 屏障/快照前异常：当前队列全部响亮失败（不悬置、不发送——未发布发送许可者按 §4.1a 判据表第一行可终局中止）。
            List<PendingAdmission> stuck;
            lock (_queueLock)
            {
                stuck = _queue.ToList();
                _queue.Clear();
            }

            CompleteAll(stuck, r => AdmissionResult.Of(AdmissionResultKind.Error, "internal_error", "轮次快照前异常（响亮失败不静默）。", r.Request.RequestIdentity));
            lock (_queueLock) foreach (var s in stuck) _inflight.Remove(s.Request.RequestIdentity);
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        List<PendingAdmission>? round = null;
        try
        {
            lock (_queueLock)
            {
                if (_queue.Count == 0) return;
                round = _queue.ToList();
                _queue.Clear(); // 原子快照并移除（串行段内）——该集合即当轮候选
            }

            await ProcessRoundAsync(round).ConfigureAwait(false);
        }
        finally
        {
            if (round is not null)
                lock (_queueLock) foreach (var r in round) _inflight.Remove(r.Request.RequestIdentity);
            _gate.Release();
        }
    }

    private async Task ProcessRoundAsync(List<PendingAdmission> round)
    {
        try
        {
            if (_hooks.Barriers?.AfterRoundSnapshot is { } afterSnapshot) await afterSnapshot().ConfigureAwait(false);
            // 状态迁移（Queued→InRound）在权威串行边界内完成（§3.3 注）。
            var read0 = _store.Read();
            if (read0.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported || read0.File?.Lease is null)
            {
                CompleteAll(round, r => AdmissionResult.Of(AdmissionResultKind.Error, read0.Status == ArbitrationLeaseStatus.Corrupt ? "corrupt" : read0.Status == ArbitrationLeaseStatus.Unsupported ? "unsupported_version" : "lease_not_valid", "轮次开始无可用租约。", r.Request.RequestIdentity));
                return;
            }

            // 流程起点捕获所有者身份——本轮一切变更沿用；所有权更替=旧身份响亮拒绝（不借新身份写入）。
            var lease = read0.File.Lease;
            // B8：逐项核验登记时捕获的所有者身份——不一致=旧排队流程（响亮拒绝，不借新身份继续）；
            // 显式新所有者恢复/新提交重新登记（捕获新身份），不受本拦截影响。
            var staleQueued = round.Where(r =>
                !string.Equals(r.CapturedLeaseId, lease.LeaseId, StringComparison.Ordinal)
                || !string.Equals(r.CapturedOwnerEpoch, lease.OwnerEpoch, StringComparison.Ordinal)).ToList();
            if (staleQueued.Count > 0)
            {
                CompleteAll(staleQueued, r => AdmissionResult.Of(AdmissionResultKind.Error, "lease_stale_generation", "登记时所有者身份已更替（旧排队流程不借新身份继续）。", r.Request.RequestIdentity));
                round = round.Except(staleQueued).ToList();
                if (round.Count == 0) return;
            }

            var ids = round.Select(r => r.Request.RequestIdentity).ToHashSet(StringComparer.Ordinal);
            var advanced = new List<string>(); // 状态已被其他处理者推进（B1：不覆盖——分类返回当前事实）
            var now0 = _utcNow();
            var mark = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                foreach (var op in (file.Handoff?.Operations ?? []).Where(o => ids.Contains(o.RequestIdentity)))
                {
                    if (op.RequestState is OperationRequestState.Queued or OperationRequestState.RetryableRejected)
                    {
                        op.RequestState = OperationRequestState.InRound;
                        op.UpdatedRevision = file.Revision + 1;
                        op.UpdatedAtUtc = now0;
                    }
                    else
                    {
                        advanced.Add(op.RequestIdentity);
                    }
                }
                return null;
            });
            if (!mark.Success)
            {
                CompleteAll(round, r => ClassifyMutateReject(mark.Reason ?? "invalid_request", r.Request.RequestIdentity));
                return;
            }

            if (advanced.Count > 0)
            {
                CompleteAll(round.Where(r => advanced.Contains(r.Request.RequestIdentity)).ToList(),
                    r => ClassifyCurrentState(r.Request.RequestIdentity));
                round = round.Where(r => !advanced.Contains(r.Request.RequestIdentity)).ToList();
                if (round.Count == 0) return;
            }

            var facts = _hooks.FactsProvider();
            var entries = round.Select(r => new CandidateEntry
            {
                Candidate = r.Request.Candidate,
                Eligibility = _hooks.EligibilityProvider(r.Request),
                BindingDiscriminator = (r.Request.RunBinding ?? "~") + "|" + (r.Request.CursorRef ?? "~") + "|" + (r.Request.CursorRevision?.ToString() ?? "~"),
            }).ToList();
            var decision = ArbitrationOrdering.Decide(entries, facts);

            switch (decision.Outcome)
            {
                case ArbitrationOutcome.F11Blocked:
                    await TerminateRoundAsync(round, lease, decision,
                        r => (OperationRequestState.TerminalRejected, "f11_active"),
                        r => AdmissionResult.Of(AdmissionResultKind.F11Blocked, "f11_active", "F11 独立停止闸门激活。", r.Request.RequestIdentity)).ConfigureAwait(false);
                    return;
                case ArbitrationOutcome.NoEligibleCandidate:
                case ArbitrationOutcome.TicketSuppressed:
                {
                    // 身份冲突（同候选号不同载荷/排序键）=终局拒绝（§3.3）；其余未获选=NotSelected。
                    await SettleRejectedAsync(round, lease, decision).ConfigureAwait(false);
                    return;
                }
                case ArbitrationOutcome.NeedReconcile:
                    // 未发布发送许可的操作回 Queued（可经续用重新驱动）——不转 Reconciling（无发送责任则无对账对象）。
                    await TransitionRoundAsync(round, lease, OperationRequestState.Queued,
                        r => new AdmissionResult { Kind = AdmissionResultKind.NeedReconcile, ReasonCode = "facts_unknown", Detail = decision.Reason, RequestIdentity = r.Request.RequestIdentity, SuppressionSource = decision.SuppressionSource, Decision = decision }).ConfigureAwait(false);
                    return;
                case ArbitrationOutcome.NeedPreemptConfirm:
                {
                    await SettleRejectedAsync(round.Where(r => !IsWinner(decision)(r)).ToList(), lease, decision).ConfigureAwait(false);
                    var preemptWinners = round.Where(IsWinner(decision)).ToList();
                    foreach (var w in preemptWinners.Take(1))
                    {
                        // 胜者交接存续：操作回 Queued 待交接闭环（非终局；R5.3 交接状态机接管后续准入）。
                        var back = await TransitionSingleAsync(w.Request.RequestIdentity, lease, OperationRequestState.Queued, expectedStates: OperationRequestState.InRound).ConfigureAwait(false);
                        w.Completion.TrySetResult(back.Success
                            ? new AdmissionResult { Kind = AdmissionResultKind.NeedPreemptConfirm, ReasonCode = "execution_occupied", Detail = "执行占用→需安全交接确认（分流交接状态机，R5.3）。", RequestIdentity = w.Request.RequestIdentity, WinnerCandidateId = decision.WinnerCandidateId, Decision = decision }
                            : ClassifyCurrentState(w.Request.RequestIdentity)); // 状态已推进=不覆盖，返回当前事实
                    }

                    if (preemptWinners.Count > 1)
                        await MirrorMergedAsync(preemptWinners.Skip(1).ToList(), lease, decision,
                            new AdmissionResult { Kind = AdmissionResultKind.NeedPreemptConfirm, ReasonCode = "execution_occupied", Detail = "执行占用→需安全交接确认。", WinnerCandidateId = decision.WinnerCandidateId, Decision = decision }, winnerAccepted: false).ConfigureAwait(false);
                    return;
                }
                case ArbitrationOutcome.AllowRequestExecution:
                {
                    await SettleRejectedAsync(round.Where(r => !IsWinner(decision)(r)).ToList(), lease, decision).ConfigureAwait(false);
                    var winnerPending = round.First(IsWinner(decision));
                    // 去重合并（§3.1：同身份+同载荷+同排序键留一项）——合并项不新增发送者，共享胜者结果（并发重试者合并）。
                    var mergedWin = round.Where(IsWinner(decision)).Skip(1).ToList();
                    var winnerResult = await ProcessWinnerAsync(winnerPending.Request, lease, decision,
                        mergedWin.Select(m => m.Request.RequestIdentity).ToList()).ConfigureAwait(false);
                    winnerPending.Completion.TrySetResult(winnerResult);
                    if (mergedWin.Count > 0)
                        await MirrorMergedAsync(mergedWin, lease, decision, winnerResult, winnerAccepted: winnerResult.Kind == AdmissionResultKind.Accepted).ConfigureAwait(false);
                    return;
                }
                default:
                    CompleteAll(round, r => AdmissionResult.Of(AdmissionResultKind.Error, "internal_error", "未知判定结果。", r.Request.RequestIdentity));
                    return;
            }
        }
        catch (Exception ex)
        {
            CompleteAll(round, r => AdmissionResult.Of(AdmissionResultKind.Error, "internal_error", "轮次处理异常（响亮失败不静默）：" + ex.GetType().Name, r.Request.RequestIdentity));
        }
    }

    /// <summary>
    /// 去重合并项内存通知（落盘与返回一致）：
    /// 胜者已受理→落盘镜像已并入胜者统一关闭事务（B6 共同结清）——此处读回核验落盘事实后完成通知；
    /// 落盘缺失（关闭失败/所有权更替）=不报告未持久化事实（续用可查当前状态）。
    /// 胜者未受理→合并项 NotSelected（merged_duplicate）。
    /// </summary>
    private async Task MirrorMergedAsync(List<PendingAdmission> merged, LeaseSegment lease, ArbitrationDecision decision, AdmissionResult winnerResult, bool winnerAccepted)
    {
        if (winnerAccepted)
        {
            var read = _store.Read();
            CompleteAll(merged, m =>
            {
                var op = read.File?.Handoff?.Operations.FirstOrDefault(o => string.Equals(o.RequestIdentity, m.Request.RequestIdentity, StringComparison.Ordinal));
                return op?.RequestState == OperationRequestState.Accepted
                    ? new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_accepted", Detail = "去重合并：共享胜者受理结果（不新增发送者）。", RequestIdentity = m.Request.RequestIdentity, SubmissionIdentity = winnerResult.SubmissionIdentity, SendSeq = winnerResult.SendSeq, Decision = decision }
                    : AdmissionResult.Of(AdmissionResultKind.Error, "mirror_not_persisted", "合并镜像未落盘（不报告未持久化事实——续用可查当前状态）。", m.Request.RequestIdentity);
            });
            await Task.CompletedTask.ConfigureAwait(false);
        }
        else
        {
            await TerminateRoundAsync(merged, lease, decision,
                _ => (OperationRequestState.NotSelected, "merged_duplicate"),
                m => new AdmissionResult { Kind = winnerResult.Kind, ReasonCode = winnerResult.ReasonCode, Detail = winnerResult.Detail + "（去重合并：共享胜者结果，不新增发送者）", RequestIdentity = m.Request.RequestIdentity, SubmissionIdentity = winnerResult.SubmissionIdentity, SendSeq = winnerResult.SendSeq, WinnerCandidateId = winnerResult.WinnerCandidateId, Decision = winnerResult.Decision }).ConfigureAwait(false);
        }
    }

    /// <summary>合并项同事务镜像（B6 共同结清：胜者终态落盘时，MergedInto 指向胜者的合并项同次原子发布镜像终态——无补写窗口）。</summary>
    private static void MirrorMergedInPlace(LogicalOwnerLeaseFile file, OperationRecord winner, DateTimeOffset now,
        OperationRequestState state, Func<OperationRecord, OperationRecord, OperationResult> resultOf)
    {
        foreach (var m in (file.Handoff?.Operations ?? []).Where(o =>
                     string.Equals(o.MergedInto, winner.RequestIdentity, StringComparison.Ordinal)
                     && o.Zone == OperationZone.Active
                     && o.RequestState is OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected))
        {
            m.RequestState = state;
            m.SubmissionIdentity = winner.SubmissionIdentity;
            m.LastSendSeq = winner.LastSendSeq;
            m.TakeoverRef = winner.TakeoverRef;
            m.LastResult = resultOf(m, winner);
            if (state is OperationRequestState.TerminalRejected or OperationRequestState.NotSelected)
                m.Zone = OperationZone.TerminalPendingTransfer;
            if (state == OperationRequestState.RetryableRejected)
                m.RetryWindowDeadlineUtc ??= now + RetryWindow; // 与胜者同窗（持久化不重置）
            m.UpdatedRevision = file.Revision + 1;
            m.UpdatedAtUtc = now;
        }
    }

    /// <summary>未获选/冲突分流（身份冲突=终局拒绝；其余=NotSelected——混合冲突组逐候选判定，不统一降级）。</summary>
    private async Task SettleRejectedAsync(IReadOnlyList<PendingAdmission> targets, LeaseSegment lease, ArbitrationDecision decision)
    {
        if (targets.Count == 0) return;
        var conflicts = targets.Where(r => RejectionReasonOf(decision, r) == "identity_conflict").ToList();
        var notSelected = targets.Where(r => RejectionReasonOf(decision, r) != "identity_conflict").ToList();
        if (conflicts.Count > 0)
            await TerminateRoundAsync(conflicts, lease, decision,
                _ => (OperationRequestState.TerminalRejected, "identity_conflict"),
                r => new AdmissionResult { Kind = AdmissionResultKind.TerminalRejected, ReasonCode = "identity_conflict", Detail = "同候选号不同载荷/排序键=整组冲突拒绝（终局，不静默成功）。", RequestIdentity = r.Request.RequestIdentity, SuppressionSource = decision.SuppressionSource, Decision = decision }).ConfigureAwait(false);
        if (notSelected.Count > 0)
            await TerminateRoundAsync(notSelected, lease, decision,
                r => (OperationRequestState.NotSelected, RejectionReasonOf(decision, r)),
                r => new AdmissionResult { Kind = AdmissionResultKind.NotSelected, ReasonCode = RejectionReasonOf(decision, r), Detail = "未获选终局（含胜者引用与压制来源）。", RequestIdentity = r.Request.RequestIdentity, WinnerCandidateId = decision.WinnerCandidateId, SuppressionSource = decision.SuppressionSource, Decision = decision }).ConfigureAwait(false);
    }

    private static Func<PendingAdmission, bool> IsWinner(ArbitrationDecision decision)
        => p => string.Equals(
            ArbitrationOrdering.DeriveCandidateId(ArbitrationOrdering.BuildStableIdentity(p.Request.Candidate)),
            decision.WinnerCandidateId ?? "", StringComparison.Ordinal);

    /// <summary>按候选号查当轮结构化拒绝原因（逐候选判定：无逐候选记录=合格落选 not_selected——绝不回退串写他人原因/Outcome 名）。</summary>
    private static string RejectionReasonOf(ArbitrationDecision d, PendingAdmission p)
    {
        var candidateId = ArbitrationOrdering.DeriveCandidateId(ArbitrationOrdering.BuildStableIdentity(p.Request.Candidate));
        return d.Rejections.FirstOrDefault(r => string.Equals(r.CandidateId, candidateId, StringComparison.Ordinal))?.Reason
               ?? "not_selected";
    }

    /// <summary>整轮终局（逐候选状态/原因迁移+Operations 结果记录+终局转区迁移=同次原子发布）。</summary>
    private Task TerminateRoundAsync(IReadOnlyList<PendingAdmission> targets, LeaseSegment lease, ArbitrationDecision decision,
        Func<PendingAdmission, (OperationRequestState State, string Reason)> outcomeOf, Func<PendingAdmission, AdmissionResult> resultOf)
    {
        if (targets.Count == 0) return Task.CompletedTask;
        var byId = targets.ToDictionary(r => r.Request.RequestIdentity, StringComparer.Ordinal);
        var now = _utcNow();
        var skipped = new List<string>(); // B1：状态已推进者不覆盖——分类返回当前事实
        var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var ops = file.Handoff?.Operations ?? [];
            foreach (var op in ops.Where(o => byId.ContainsKey(o.RequestIdentity)))
            {
                if (op.RequestState != OperationRequestState.InRound)
                {
                    skipped.Add(op.RequestIdentity);
                    continue;
                }

                var (state, reason) = outcomeOf(byId[op.RequestIdentity]);
                op.RequestState = state;
                op.LastResult = new OperationResult
                {
                    Outcome = OperationOutcome.Rejected,
                    ReasonCode = reason,
                    Retryable = false,
                    RetryBudgetUsed = op.LastResult?.RetryBudgetUsed ?? 0,
                    EvidenceSource = "arbitration_decision",
                    AnsweredSendSeq = op.LastSendSeq,
                    WinnerRef = state == OperationRequestState.NotSelected ? decision.WinnerCandidateId : null, // I5：压制依据持久化
                    SuppressionSource = state == OperationRequestState.NotSelected ? decision.SuppressionSource : null,
                };
                op.Zone = OperationZone.TerminalPendingTransfer;
                op.UpdatedRevision = file.Revision + 1;
                op.UpdatedAtUtc = now;
            }

            MigrateAndClean(file, now);
            return null;
        });
        CompleteAll(targets, mutate.Success
            ? (r => skipped.Contains(r.Request.RequestIdentity) ? ClassifyCurrentState(r.Request.RequestIdentity) : resultOf(r))
            : (r => ClassifyMutateReject(mutate.Reason ?? "invalid_request", r.Request.RequestIdentity)));
        return Task.CompletedTask;
    }

    /// <summary>整轮状态迁移（非终局：回 Queued 等——可经续用重新驱动）。</summary>
    private Task TransitionRoundAsync(IReadOnlyList<PendingAdmission> targets, LeaseSegment lease,
        OperationRequestState state, Func<PendingAdmission, AdmissionResult> resultOf)
    {
        if (targets.Count == 0) return Task.CompletedTask;
        var ids = targets.Select(r => r.Request.RequestIdentity).ToHashSet(StringComparer.Ordinal);
        var now = _utcNow();
        var skipped = new List<string>(); // B1：状态已推进者不覆盖——分类返回当前事实
        var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            foreach (var op in (file.Handoff?.Operations ?? []).Where(o => ids.Contains(o.RequestIdentity)))
            {
                if (op.RequestState != OperationRequestState.InRound)
                {
                    skipped.Add(op.RequestIdentity);
                    continue;
                }

                op.RequestState = state;
                op.UpdatedRevision = file.Revision + 1;
                op.UpdatedAtUtc = now;
            }

            return null;
        });
        CompleteAll(targets, mutate.Success
            ? (r => skipped.Contains(r.Request.RequestIdentity) ? ClassifyCurrentState(r.Request.RequestIdentity) : resultOf(r))
            : (r => ClassifyMutateReject(mutate.Reason ?? "invalid_request", r.Request.RequestIdentity)));
        return Task.CompletedTask;
    }

    private static void CompleteAll(IEnumerable<PendingAdmission> targets, Func<PendingAdmission, AdmissionResult> resultOf)
    {
        foreach (var p in targets) p.Completion.TrySetResult(resultOf(p));
    }

    // ============================================================
    // 统一提交边界（§4.2：锁内最终校验链→Submission 落盘→锁外发送→三态对账）
    // ============================================================

    /// <summary>胜者提交边界（占位成功或明确失败才结束当轮；合并关联随占位同次原子发布——发送前落盘）。</summary>
    private async Task<AdmissionResult> ProcessWinnerAsync(AdmissionRequest request, LeaseSegment lease, ArbitrationDecision decision, IReadOnlyCollection<string>? mergedIdentities)
    {
        var stableIdentity = ArbitrationOrdering.BuildStableIdentity(request.Candidate);
        var candidateId = ArbitrationOrdering.DeriveCandidateId(stableIdentity);
        var targetEpoch = ExtractEpoch(request.Candidate.Scope);
        var now = _utcNow();

        if (_hooks.Barriers?.BeforeOccupyPublish is { } beforeOccupy) await beforeOccupy().ConfigureAwait(false);
        var occupy = ValidateAndOccupy(request, lease, stableIdentity, candidateId, targetEpoch,
            OperationRequestState.InRound, mergedIdentities, now, out var special);
        if (!occupy.Success) return await ClassifyOccupyRejectAsync(request, lease, occupy.Reason ?? "invalid_request").ConfigureAwait(false);
        if (special == "retry_window_expired_terminal")
            return new AdmissionResult { Kind = AdmissionResultKind.TerminalRejected, ReasonCode = "retry_window_expired", Detail = "重试窗口到期→锁内复核确定未受理，转终局。", RequestIdentity = request.RequestIdentity };
        if (special is not null) return AdmissionResult.Of(AdmissionResultKind.Error, special, "占位事务异常分支。", request.RequestIdentity);

        if (_hooks.Barriers?.AfterOccupyBeforeSend is { } afterOccupy) await afterOccupy().ConfigureAwait(false);
        SendOutcome outcome;
        try
        {
            outcome = await _hooks.Sender(BuildDispatch(request, occupy.File!, stableIdentity, candidateId, targetEpoch)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outcome = new SendOutcome.Unknown("发送回调异常（保守待对账，不重发）：" + ex.GetType().Name); // I1：已发出结果未知=对账，不抛出不悬置
        }

        return await ReconcileOutcomeAsync(request, lease, occupy.File!, outcome).ConfigureAwait(false);
    }

    private SubmissionDispatch BuildDispatch(AdmissionRequest request, LogicalOwnerLeaseFile occupiedFile, string stableIdentity, string candidateId, string targetEpoch)
    {
        var op = occupiedFile.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
        var submission = occupiedFile.Handoff.Submission!;
        return new SubmissionDispatch
        {
            RequestIdentity = request.RequestIdentity,
            SubmissionIdentity = submission.SubmissionIdentity,
            SendSeq = submission.SendSeq,
            CandidateId = candidateId,
            StableIdentity = stableIdentity,
            ActionId = submission.ActionId,
            TargetEpoch = targetEpoch,
            ResourceRef = op.ResourceRef,
            Intent = op.Intent,
            WireSubmitKey = op.WireSubmitKey,
            Candidate = request.Candidate,
            ProcessLocalContext = request.ProcessLocalContext, // §13.10 A2：随获选项原样传给 Sender
        };
    }

    /// <summary>
    /// 锁内最终校验+占位（§4.2 统一提交边界核心——全部校验在 MutateHandoff 权威串行边界内完成：
    /// F11→票据→事实未知→执行占用→资格→epoch→Pending 阶段/身份→未决发送→操作状态→重试窗口/预算→
    /// 持久化快照比对→绑定一致；授权签发/身份绑定/消费关系/Submission 占位=同一次原子发布）。
    /// 校验只消费本地事实（I-3）；isRetry=true 时按 §3.3-6 复核重试资格（窗口到期锁内复核确定未受理+无更新责任→同边界转终局）。
    /// </summary>
    private LeaseMutateResult ValidateAndOccupy(AdmissionRequest request, LeaseSegment lease, string stableIdentity, string candidateId, string targetEpoch,
        OperationRequestState expectedState, IReadOnlyCollection<string>? mergedIdentities, DateTimeOffset now, out string? specialOutcome)
    {
        string? special = null;
        var read = _store.Read();
        if (read.File?.Lease is null)
        {
            specialOutcome = null;
            return new LeaseMutateResult { Success = false, Reason = "lease_not_valid", File = null };
        }

        var result = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            // ① F11 独立停止闸门（锁内复核——校验后激活同样阻断占位与发送）。
            if (_hooks.F11Active()) return "f11_active";
            var facts = _hooks.FactsProvider();
            // ② 票据压制（无关候选不得占位；授权方保留资格）。
            if (facts.ActiveTicket is { } ticket && !string.Equals(ticket.AuthorizedPreemptorIdentity, stableIdentity, StringComparison.Ordinal))
                return "ticket_suppressed";
            // ③ 权威事实未知（禁止换键重跑、禁止占位）。
            if (facts.ExecutionFactsUnknown) return "facts_unknown";
            // ④ 执行占用（锁内复核——按无损拒绝类可重试处理）。
            if (facts.ExecutionOccupied) return "execution_occupied";
            // ⑤ 资格关键事实复核（资格变化不构成确定性失效，但当轮不得占位）。
            var eligibility = _hooks.EligibilityProvider(request);
            if (!eligibility.IsDue || !eligibility.PrerequisiteReady || !eligibility.FlexibleWindowOpen) return "eligibility_lost";
            // ⑥ 目标 bgiEpoch（只比较不重写；身份/时限类不透明重试）。
            if (!string.Equals(targetEpoch, _hooks.BgiEpochProvider(), StringComparison.Ordinal)) return "stale_epoch";

            file.Handoff ??= new LeaseHandoffSegment();
            // ⑦ Pending 组合约束：授权抢占方+目标 epoch 匹配（双字段组合约束），且阶段许可——
            //    SettlePending/RestorePending/ReconcilePending 阶段不得准入抢占方（恢复责任存续期禁止另建替代作业）。
            if (file.Handoff.Pending is { } pending)
            {
                if (!string.Equals(pending.AuthorizedPreemptor, stableIdentity, StringComparison.Ordinal)
                    || !string.Equals(pending.TargetEpoch, targetEpoch, StringComparison.Ordinal))
                    return "pending_conflict";
                if (pending.Phase is not (HandoffPhase.PreemptRequested or HandoffPhase.Confirming))
                    return "pending_conflict";
            }

            // ⑧ 当前未决发送至多一笔（不覆盖占位）。
            if (file.Handoff.Submission is not null) return "submission_conflict";

            var op = (file.Handoff.Operations ?? []).FirstOrDefault(o => string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
            if (op is null || op.Zone != OperationZone.Active) return "stale_operation_identity";
            // ⑨ 不可变消费记录比对（占位按持久化快照校验——调用方后置可变对象不得改变发送目标/载荷）。
            if (!string.Equals(op.CandidateId, candidateId, StringComparison.Ordinal)
                || !string.Equals(op.PayloadFingerprint, request.Candidate.PayloadFingerprint ?? "", StringComparison.Ordinal)
                || !string.Equals(op.SortKeyFingerprint, SortKeyFingerprintOf(request.Candidate, request.RunBinding, request.CursorRef, request.CursorRevision), StringComparison.Ordinal)
                || !string.Equals(op.Candidate?.ActionId ?? "", request.Candidate.ActionId ?? "", StringComparison.Ordinal))
                return "identity_conflict";
            // ⑩ 操作状态原子复核（跨实例一致性：已受理/已在途不得再签发发送许可，§3.2a）。
            if (op.RequestState != expectedState) return "state_changed";
            // ⑪ 曾发送=后续发送（由持久化发送史推导，不由调用路径决定，B2——经 Queued 重入同样受约束）：
            //    最近发送确定未受理（结果对应最后发送轮次）+预算+持久化窗口，全部锁内复核。
            if (op.LastSendSeq > 0)
            {
                if (op.LastResult is not { Outcome: OperationOutcome.Rejected, Retryable: true } lastResult
                    || lastResult.AnsweredSendSeq != op.LastSendSeq)
                    return "state_changed"; // 仅「可重试的确定拒绝」构成重试资格（N5 防御：非可重试拒结果不得签发后续发送）
                if (lastResult.RetryBudgetUsed >= RetryBudgetMax) return "retry_budget_exhausted";
                if (op.RetryWindowDeadlineUtc is { } deadline && deadline <= now)
                {
                    // 窗口到期：锁内复核成立（最近发送确定未受理+无更新发送责任——⑧已排除未决 Submission）→同边界转终局。
                    op.RequestState = OperationRequestState.TerminalRejected;
                    lastResult.ReasonCode = "retry_window_expired";
                    op.Zone = OperationZone.TerminalPendingTransfer;
                    op.UpdatedRevision = file.Revision + 1;
                    op.UpdatedAtUtc = now;
                    // 合并项同边界共终局（N4：共享胜者结果——不留 MergedInto 未落/分类不一致窗口）。
                    if (mergedIdentities is { Count: > 0 })
                    {
                        foreach (var m in (file.Handoff.Operations ?? []).Where(o => mergedIdentities.Contains(o.RequestIdentity)))
                        {
                            if (m.Zone != OperationZone.Active || m.RequestState != OperationRequestState.InRound) return "state_changed";
                            m.MergedInto = op.RequestIdentity;
                            m.RequestState = OperationRequestState.TerminalRejected;
                            m.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = "retry_window_expired", Retryable = false, RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "merged:" + op.RequestIdentity, AnsweredSendSeq = m.LastSendSeq };
                            m.Zone = OperationZone.TerminalPendingTransfer;
                            m.UpdatedRevision = file.Revision + 1;
                            m.UpdatedAtUtc = now;
                        }
                    }

                    MigrateAndClean(file, now);
                    special = "retry_window_expired_terminal";
                    return null;
                }

                lastResult.RetryBudgetUsed += 1; // 无损拒绝重试不增加业务 attempt（预算水位随重试签发递增）
            }

            // ⑪b 游标唯一消费（B7：同 cursorRef+cursorRevision 不得被两个操作消费——锁内核验，防双跑）。
            if (request.CursorRef is { } cursorRefToCheck
                && (file.Handoff.Operations ?? []).Any(other =>
                    !string.Equals(other.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal)
                    && other.Zone == OperationZone.Active
                    && string.Equals(other.CursorRef, cursorRefToCheck, StringComparison.Ordinal)
                    && other.CursorRevision == request.CursorRevision
                    && other.RequestState is OperationRequestState.Granted or OperationRequestState.Sending
                        or OperationRequestState.Reconciling or OperationRequestState.Accepted or OperationRequestState.TerminalCompleted))
                return "cursor_already_consumed";

            // ⑪c runBinding 双跑归属（会诊 重要-2/复核 重要-1 处置——**门面层不自足，无双跑由宿主发送侧兜底**）：
            //    恢复候选 CursorRef=null（⑪b 不适用），同 runBinding 的「启动 op（Accepted，驱动在跑）+恢复 op」属
            //    合法并存（夹具 ResumeRun_Paused 验证）；「op1 已 Accepted 后 op2 再占位」在门面层可达——门面不拒，
            //    无双跑实际由宿主 DispatchResumeViaHostAsync 的 run_state_changed（台账运行态）+task_running
            //    （_reservedWorkflows/_drives 在飞）兜底（恢复路径即终局中止/可重试拒绝）。曾尝试门面内拒
            //    （LastSendSeq>0 在途即拒 run_already_active），但 Granted/Sending/Reconciling 蕴含 Submission≠null
            //    必被 ⑧ 前置吞没=防御性死代码，且无法拒「先行者 Accepted」窗（拒 Accepted 又会误杀合法启动+恢复并存）
            //    ——故门面层不设此守卫，归属关系如实登记（不冒充自足）。

            // ⑫ 绑定一致（runBinding/cursorRef：首绑写入、再绑必须一致，不可改写）。
            if (request.RunBinding is { } runBinding)
            {
                if (op.RunBinding is null) op.RunBinding = runBinding;
                else if (!string.Equals(op.RunBinding, runBinding, StringComparison.Ordinal)) return "binding_conflict";
            }

            if (request.CursorRef is { } cursorRef)
            {
                if (op.CursorRef is null)
                {
                    op.CursorRef = cursorRef;
                    op.CursorRevision = request.CursorRevision;
                }
                else if (!string.Equals(op.CursorRef, cursorRef, StringComparison.Ordinal) || op.CursorRevision != request.CursorRevision)
                    return "binding_conflict";
            }

            // ⑬ 占位（授权签发/身份绑定/消费关系/Submission=同一次原子发布）。
            var sendSeq = op.LastSendSeq + 1;
            var submissionIdentity = "sub:" + request.RequestIdentity + ":" + sendSeq.ToString();
            op.RequestState = OperationRequestState.Granted;
            op.LastSendSeq = sendSeq;
            op.SubmissionIdentity = submissionIdentity;
            op.WireSubmitKey ??= request.WireSubmitKey;
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = now;
            file.Handoff.Submission = new SubmissionRecord
            {
                SubmissionIdentity = submissionIdentity,
                SendSeq = sendSeq,
                ActionId = request.Candidate.ActionId ?? ArbitrationOrdering.DeriveActionId(candidateId),
                TargetEpoch = targetEpoch,
                CandidateId = candidateId,
                State = SubmissionState.Submitting,
                RecordedAtUtc = now,
            };
            // ⑭ 去重合并关联发送前落盘（B6：合并项由本胜者承担发送责任；共同结清/恢复时镜像终态）。
            if (mergedIdentities is { Count: > 0 })
            {
                foreach (var m in (file.Handoff.Operations ?? []).Where(o => mergedIdentities.Contains(o.RequestIdentity)))
                {
                    if (m.Zone != OperationZone.Active || m.RequestState != OperationRequestState.InRound) return "state_changed";
                    m.MergedInto = op.RequestIdentity;
                    m.UpdatedRevision = file.Revision + 1;
                    m.UpdatedAtUtc = now;
                }
            }

            return null;
        }, checkSwitchGate: true);
        specialOutcome = special;
        return result;
    }

    /// <summary>占位拒绝分类（原因→终局/可重试/回队/对账；一切状态迁移同样经权威串行边界且校验发布成功；状态已推进=不覆盖，分类返回当前事实）。</summary>
    private async Task<AdmissionResult> ClassifyOccupyRejectAsync(AdmissionRequest request, LeaseSegment lease, string reason)
    {
        switch (reason)
        {
            // 终局类（本地权威裁决+无未决发送责任，§4.1a 判据表第一行；落盘遇状态已推进→返回当前事实）。
            case "f11_active":
                return (await TerminatePrecheckAsync(request, lease, "f11_active", AdmissionResultKind.F11Blocked, "F11 独立停止闸门激活（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "ticket_suppressed":
                return (await TerminatePrecheckAsync(request, lease, "ticket_suppressed", AdmissionResultKind.TerminalRejected, "票据压制期：无关候选不得占位（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "eligibility_lost":
                return (await TerminatePrecheckAsync(request, lease, "eligibility_lost", AdmissionResultKind.TerminalRejected, "资格关键事实复核失败（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "stale_epoch":
                return (await TerminatePrecheckAsync(request, lease, "stale_epoch", AdmissionResultKind.TerminalRejected, "目标 bgiEpoch 不匹配（只比较不重写）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "identity_conflict":
                return (await TerminatePrecheckAsync(request, lease, "identity_conflict", AdmissionResultKind.TerminalRejected, "持久化快照与请求载荷/排序键不一致（终局）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "binding_conflict":
                return (await TerminatePrecheckAsync(request, lease, "binding_conflict", AdmissionResultKind.TerminalRejected, "runBinding/cursorRef 绑定冲突（不可改写）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "cursor_already_consumed":
                return (await TerminatePrecheckAsync(request, lease, "cursor_already_consumed", AdmissionResultKind.TerminalRejected, "同一游标（cursorRef+cursorRevision）已被其他操作消费（唯一消费约束，防双跑）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "pending_conflict":
                return (await TerminatePrecheckAsync(request, lease, "pending_conflict", AdmissionResultKind.TerminalRejected, "交接责任存续期：非授权方或阶段不许可（SettlePending/RestorePending/ReconcilePending 禁止另建替代作业）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "retry_budget_exhausted":
                return (await TerminatePrecheckAsync(request, lease, "retry_budget_exhausted", AdmissionResultKind.TerminalRejected, "重试预算耗尽（终局拒绝）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 事实未知/占用：未发布发送许可→回 Queued 可再驱动。
            case "facts_unknown":
            {
                var back = await TransitionSingleAsync(request.RequestIdentity, lease, OperationRequestState.Queued, expectedStates: OperationRequestState.InRound).ConfigureAwait(false);
                return back.Success
                    ? AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "facts_unknown", "权威执行事实未知→待对账（禁止换键重跑；操作回 Queued 可再驱动）。", request.RequestIdentity)
                    : ClassifyCurrentState(request.RequestIdentity);
            }
            case "execution_occupied":
                return (await RetryablePrecheckRejectAsync(request, lease, "execution_occupied", "执行占用（锁内复核——按无损拒绝类可重试处理）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            // 状态已由其他处理者推进：不回退——返回当前事实分类（B1）。
            case "state_changed":
                return ClassifyCurrentState(request.RequestIdentity);
            // 未决发送冲突：跨实例/跨流瞬态——回 Queued，响亮拒绝（续用可查当前状态）。
            case "submission_conflict":
            {
                var back = await TransitionSingleAsync(request.RequestIdentity, lease, OperationRequestState.Queued, expectedStates: OperationRequestState.InRound).ConfigureAwait(false);
                return back.Success
                    ? AdmissionResult.Of(AdmissionResultKind.Error, reason, "存在未决发送（至多一笔，不覆盖占位）——操作回 Queued。", request.RequestIdentity)
                    : ClassifyCurrentState(request.RequestIdentity);
            }
            // 切换闸门/租约资格/存取故障：操作回 Queued（未发布发送许可），响亮拒绝。
            default:
            {
                var back = await TransitionSingleAsync(request.RequestIdentity, lease, OperationRequestState.Queued, expectedStates: OperationRequestState.InRound).ConfigureAwait(false);
                return back.Success
                    ? ClassifyMutateReject(reason, request.RequestIdentity)
                    : ClassifyCurrentState(request.RequestIdentity);
            }
        }
    }

    /// <summary>状态已被其他处理者推进时的当前事实分类（B1：不回退不覆盖——返回既有事实；I5：压制依据随持久化结果恢复）。</summary>
    private AdmissionResult ClassifyCurrentState(string requestIdentity)
    {
        var read = _store.Read();
        var op = read.File is null ? null : FindOp(read.File, requestIdentity);
        if (op is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
        return op.RequestState switch
        {
            OperationRequestState.Accepted => new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_accepted", Detail = "已受理（返回既有结果）。", RequestIdentity = requestIdentity, SubmissionIdentity = op.SubmissionIdentity, SendSeq = op.LastSendSeq, WinnerCandidateId = op.CandidateId, SuppressionSource = op.LastResult?.EvidenceSource ?? "" },
            OperationRequestState.TerminalCompleted => new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "already_terminal", Detail = "终局完成（返回既有结果）。", RequestIdentity = requestIdentity, SubmissionIdentity = op.SubmissionIdentity, SendSeq = op.LastSendSeq },
            OperationRequestState.TerminalRejected => AdmissionResult.Of(AdmissionResultKind.TerminalRejected, op.LastResult?.ReasonCode ?? "terminal_rejected", "终局拒绝（返回既有结果）。", requestIdentity),
            OperationRequestState.NotSelected => new AdmissionResult { Kind = AdmissionResultKind.NotSelected, ReasonCode = op.LastResult?.ReasonCode ?? "not_selected", Detail = "未获选终局（返回既有结果）。", RequestIdentity = requestIdentity, WinnerCandidateId = op.LastResult?.WinnerRef, SuppressionSource = op.LastResult?.SuppressionSource ?? "" },
            OperationRequestState.RetryableRejected => AdmissionResult.Of(AdmissionResultKind.RetryableRejected, op.LastResult?.ReasonCode ?? "retryable_rejected", "可重试拒绝（经 RetryAsync 重新 Admit）。", requestIdentity),
            OperationRequestState.Reconciling => AdmissionResult.Of(AdmissionResultKind.Reconciling, "reconciling", "发送结果未知，保守待对账（不重发）。", requestIdentity),
            _ => AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "in_flight", "已有处理在途（合并，不新增发送者）。", requestIdentity),
        };
    }

    /// <summary>终局落盘（本地权威裁决+无未决发送责任；发布失败=不报告未持久化终局，响亮 Error）。</summary>
    private async Task<AdmissionResult?> TerminatePrecheckAsync(AdmissionRequest request, LeaseSegment lease, string reasonCode, AdmissionResultKind kind, string detail)
    {
        var now = _utcNow();
        var read = _store.Read();
        if (read.File?.Lease is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "终局落盘前租约丢失（操作保持 Active，未持久化终局不报告）。", request.RequestIdentity);
        var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = FindOp(file, request.RequestIdentity);
            if (op is null || op.Zone != OperationZone.Active) return "state_changed";
            // B1：仅未发布发送许可的操作可终局预检拒绝（已占位/在途/已受理/待对账=状态已推进，不覆盖不迁墓碑）。
            if (op.RequestState is not (OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected)) return "state_changed";
            op.RequestState = OperationRequestState.TerminalRejected;
            op.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = reasonCode, Retryable = false, RetryBudgetUsed = op.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "final_precheck", AnsweredSendSeq = op.LastSendSeq };
            op.Zone = OperationZone.TerminalPendingTransfer;
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = now;
            MigrateAndClean(file, now);
            return null;
        });
        await Task.CompletedTask.ConfigureAwait(false);
        if (!mutate.Success && mutate.Reason == "state_changed") return null; // 状态已推进→调用方分类当前事实
        return mutate.Success
            ? AdmissionResult.Of(kind, reasonCode, detail, request.RequestIdentity)
            : AdmissionResult.Of(AdmissionResultKind.Error, mutate.Reason ?? "invalid_request", "终局落盘失败（未持久化终局不报告；操作保持 Active）。", request.RequestIdentity);
    }

    /// <summary>可重试拒绝落盘（预算/窗口内经 RetryAsync 重新 Admit；窗口持久化不重置）。</summary>
    private async Task<AdmissionResult?> RetryablePrecheckRejectAsync(AdmissionRequest request, LeaseSegment lease, string reasonCode, string detail)
    {
        var now = _utcNow();
        var read = _store.Read();
        if (read.File?.Lease is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "可重试拒绝落盘前租约丢失。", request.RequestIdentity);
        var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = FindOp(file, request.RequestIdentity);
            if (op is null || op.Zone != OperationZone.Active) return "state_changed";
            // B1：仅未发布发送许可的操作可落可重试拒绝（状态已推进=不覆盖）。
            if (op.RequestState is not (OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected)) return "state_changed";
            op.RequestState = OperationRequestState.RetryableRejected;
            op.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = reasonCode, Retryable = true, RetryBudgetUsed = op.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "final_precheck", AnsweredSendSeq = op.LastSendSeq };
            op.RetryWindowDeadlineUtc ??= now + RetryWindow; // 首次确定拒绝派生；持久化后不得重置（§3.3-6）
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = now;
            return null;
        });
        await Task.CompletedTask.ConfigureAwait(false);
        if (!mutate.Success && mutate.Reason == "state_changed") return null; // 状态已推进→调用方分类当前事实
        return mutate.Success
            ? AdmissionResult.Of(AdmissionResultKind.RetryableRejected, reasonCode, detail, request.RequestIdentity)
            : AdmissionResult.Of(AdmissionResultKind.Error, mutate.Reason ?? "invalid_request", "可重试拒绝落盘失败。", request.RequestIdentity);
    }

    // ============================================================
    // 三态对账与统一关闭（受理→台账→确认可重建→关闭；确定拒绝先关闭再分可重试/终局；未知 Reconciling 不重发）
    // ============================================================

    /// <summary>三态对账（发送回调与显式对账共用——两分支统一关闭接口，无第三路）。</summary>
    private async Task<AdmissionResult> ReconcileOutcomeAsync(AdmissionRequest request, LeaseSegment lease, LogicalOwnerLeaseFile occupiedFile, SendOutcome outcome)
    {
        var op = occupiedFile.Handoff!.Operations.First(o => string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
        var submission = occupiedFile.Handoff.Submission!;
        var now = _utcNow();

        switch (outcome)
        {
            case SendOutcome.Accepted accepted:
            {
                if (_hooks.Barriers?.AfterAcceptBeforeLedger is { } b1) await b1().ConfigureAwait(false);
                // 受理→先持久化接管台账→确认可跨重启重建→关闭 Submission（B-2：两记录不得同时缺失）。
                var ledgerEntry = new ExternalStartLedgerEntry
                {
                    SubmissionIdentity = submission.SubmissionIdentity,
                    SendSeq = submission.SendSeq,
                    CandidateId = submission.CandidateId,
                    ResourceRef = op.ResourceRef,
                    ActionId = submission.ActionId,
                    TargetBgiEpoch = submission.TargetEpoch,
                    AcceptedAtUtc = now,
                    EvidenceSource = accepted.EvidenceSource,
                    State = LedgerEntryState.AcceptedPendingExecution,
                    RunId = accepted.RunId,
                };
                string? persistFailure;
                try
                {
                    persistFailure = await _hooks.TakeoverPersist(ledgerEntry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    persistFailure = "persist_exception:" + ex.GetType().Name; // I1：持久化回调异常=失败原因（Submission 保持未决，保守待对账）
                }

                if (persistFailure is not null)
                {
                    var markPersist = await MarkReconcilingAsync(request.RequestIdentity, lease, submission.SubmissionIdentity, submission.SendSeq).ConfigureAwait(false);
                    return markPersist.Success
                        ? new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = "takeover_persist_failed", Detail = "接管台账持久化失败（" + persistFailure + "）——Submission 保持未决，保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq }
                        : AdmissionResult.Of(AdmissionResultKind.Error, markPersist.Reason ?? "invalid_request", "待对账落盘失败（不报告未持久化状态）。", request.RequestIdentity);
                }

                if (_hooks.Barriers?.AfterLedgerBeforeClose is { } b2) await b2().ConfigureAwait(false);
                var close = CloseSubmission(lease, submission, file =>
                {
                    var op2 = FindOp(file, request.RequestIdentity)!;
                    op2.RequestState = OperationRequestState.Accepted;
                    op2.LastResult = new OperationResult { Outcome = OperationOutcome.Accepted, ReasonCode = "", Retryable = false, RetryBudgetUsed = op2.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = accepted.EvidenceSource, AnsweredSendSeq = submission.SendSeq };
                    op2.TakeoverRef = submission.SubmissionIdentity;
                    MirrorMergedInPlace(file, op2, now, OperationRequestState.Accepted,
                        (m, _) => new OperationResult { Outcome = OperationOutcome.Accepted, ReasonCode = "", Retryable = false, RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "merged:" + submission.SubmissionIdentity, AnsweredSendSeq = submission.SendSeq });
                    return null;
                });
                if (!close.Success)
                    return new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = close.Reason ?? "close_failed", Detail = "Submission 关闭失败——台账已在册（重复接管幂等），保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq };

                return new AdmissionResult { Kind = AdmissionResultKind.Accepted, ReasonCode = "accepted", Detail = "已受理（接管台账已持久化并可重建）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, Decision = null };
            }
            case SendOutcome.Rejected rejected:
            {
                // 一切经关联验证的确定未受理都先关闭对应 Submission（无论是否可重试，§3.3 退出与再入场-1）。
                var budget = op.LastResult?.RetryBudgetUsed ?? 0;
                var canRetry = rejected.Retryable && budget < RetryBudgetMax;
                var close = CloseSubmission(lease, submission, file =>
                {
                    var op2 = FindOp(file, request.RequestIdentity)!;
                    op2.LastResult = new OperationResult
                    {
                        Outcome = OperationOutcome.Rejected,
                        ReasonCode = rejected.ReasonCode,
                        Retryable = rejected.Retryable,
                        RetryBudgetUsed = budget,
                        EvidenceSource = rejected.EvidenceSource,
                        AnsweredSendSeq = submission.SendSeq,
                    };
                    if (canRetry)
                    {
                        op2.RequestState = OperationRequestState.RetryableRejected;
                        op2.RetryWindowDeadlineUtc ??= now + RetryWindow; // 首次确定拒绝派生；持久化后不得重置（§3.3-6）
                    }
                    else
                    {
                        op2.RequestState = OperationRequestState.TerminalRejected;
                        op2.Zone = OperationZone.TerminalPendingTransfer;
                    }

                    MirrorMergedInPlace(file, op2, now, op2.RequestState,
                        (m, _) => new OperationResult
                        {
                            Outcome = OperationOutcome.Rejected,
                            ReasonCode = rejected.ReasonCode,
                            Retryable = rejected.Retryable,
                            RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0,
                            EvidenceSource = "merged:" + submission.SubmissionIdentity,
                            AnsweredSendSeq = submission.SendSeq,
                        });
                    MigrateAndClean(file, now);
                    return null;
                });
                if (!close.Success)
                    return new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = close.Reason ?? "close_failed", Detail = "拒绝关闭失败——保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq };
                return canRetry
                    ? new AdmissionResult { Kind = AdmissionResultKind.RetryableRejected, ReasonCode = rejected.ReasonCode, Detail = "可重试拒绝（窗口内经 RetryAsync 重新 Admit）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq }
                    : new AdmissionResult { Kind = AdmissionResultKind.TerminalRejected, ReasonCode = rejected.ReasonCode, Detail = "终局拒绝。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq };
            }
            default:
            {
                // 未知→Submission.Reconciling（不换键重跑、不重发；持续停驻待对账——处置入口=SettleReconciledAsync）。
                var markUnknown = await MarkReconcilingAsync(request.RequestIdentity, lease, submission.SubmissionIdentity, submission.SendSeq).ConfigureAwait(false);
                return markUnknown.Success
                    ? new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = "send_unknown", Detail = outcome is SendOutcome.Unknown u ? u.Detail : "发送结果未知。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq }
                    : AdmissionResult.Of(AdmissionResultKind.Error, markUnknown.Reason ?? "invalid_request", "待对账落盘失败（不报告未持久化状态）。", request.RequestIdentity);
            }
        }
    }

    /// <summary>
    /// 显式对账结清（§8 待对账处置入口——owner 显式对账动作/权威事实到达；仅两分支，不凭超时或查询未命中）。
    /// 受理分支：台账持久化+可重建→关闭；确定未受理分支：关联验证→关闭（按 §3.3 记可重试/终局）。
    /// </summary>
    public async Task<AdmissionResult> SettleReconciledAsync(string requestIdentity, ReconcileSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            var submission = read.File.Handoff?.Submission;
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            // I1：接受 Reconciling 以及 Granted/Sending（异常/关闭失败中断但发送责任可识别）——责任关联必须匹配。
            if (op.RequestState is not (OperationRequestState.Reconciling or OperationRequestState.Granted or OperationRequestState.Sending)
                || submission is null
                || !string.Equals(submission.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal)
                || submission.SendSeq != op.LastSendSeq)
                return AdmissionResult.Of(AdmissionResultKind.Error, "not_reconciling", "操作不在可对账状态或发送身份不匹配（不凭旧证据消解新责任）。", requestIdentity);
            // B4：证据必须携带原发送关联——与当前轮次不符=旧证据（响亮拒绝，不关闭新轮次责任）。
            var (evidenceSubmission, evidenceSeq) = settlement switch
            {
                ReconcileSettlement.Accepted a => (a.SubmissionIdentity, a.SendSeq),
                ReconcileSettlement.NotAccepted n => (n.SubmissionIdentity, n.SendSeq),
                _ => ("", 0),
            };
            if (!string.Equals(evidenceSubmission, op.SubmissionIdentity, StringComparison.Ordinal) || evidenceSeq != op.LastSendSeq)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_evidence", "对账证据与当前发送轮次不关联（旧证据不得关闭新责任）。", requestIdentity);

            var candidate = op.Candidate ?? new ArbitrationCandidate();
            var request = new AdmissionRequest
            {
                Namespace = candidate.Namespace ?? "manual",
                Kind = AdmissionKind.ContinueUse,
                RequestIdentity = requestIdentity,
                Candidate = CloneCandidate(candidate),
                WireSubmitKey = op.WireSubmitKey,
                RunBinding = op.RunBinding,
                CursorRef = op.CursorRef,
                CursorRevision = op.CursorRevision,
            };
            SendOutcome outcome = settlement switch
            {
                ReconcileSettlement.Accepted a => new SendOutcome.Accepted(a.EvidenceSource, a.RunId),
                ReconcileSettlement.NotAccepted n => new SendOutcome.Rejected(n.ReasonCode, n.Retryable, n.EvidenceSource),
                _ => new SendOutcome.Unknown("未知结清类型"),
            };
            return await ReconcileOutcomeAsync(request, lease, read.File, outcome).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 权威终态完成（§4.1a 判据表第三行：关联执行权威终态+接管台账一致才允许 Accepted→TerminalCompleted——
    /// 正常操作的主槽位出口；无本入口 Accepted 永驻 Active）。
    /// </summary>
    public AdmissionResult MarkOperationTerminal(string requestIdentity, string authoritativeTerminalEvidence)
    {
        if (string.IsNullOrWhiteSpace(authoritativeTerminalEvidence))
            return AdmissionResult.Of(AdmissionResultKind.Error, "evidence_required", "权威终态证据必填（不凭超时/未命中终局）。", requestIdentity);
        _gate.Wait();
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            if (op.RequestState != OperationRequestState.Accepted)
                return AdmissionResult.Of(AdmissionResultKind.Error, "not_accepted", "仅已受理操作可终局完成（其余状态按各自判据）。", requestIdentity);
            // 台账一致交叉确认（关联 job 权威终态；未配置=保守不允许）。
            if (_hooks.TakeoverTerminalConfirmed?.Invoke(op.SubmissionIdentity, op.LastSendSeq) != true)
                return AdmissionResult.Of(AdmissionResultKind.Error, "ledger_not_terminal", "接管台账未确认权威终态（保守不终局）。", requestIdentity);

            var now = _utcNow();
            var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op2 = FindOp(file, requestIdentity);
                if (op2 is null || op2.RequestState != OperationRequestState.Accepted) return "state_changed";
                op2.RequestState = OperationRequestState.TerminalCompleted;
                op2.Zone = OperationZone.TerminalPendingTransfer;
                op2.UpdatedRevision = file.Revision + 1;
                op2.UpdatedAtUtc = now;
                MigrateAndClean(file, now);
                return null;
            });
            return mutate.Success
                ? AdmissionResult.Of(AdmissionResultKind.Accepted, "terminal_completed", "权威终态完成（主槽位经迁移释放）。", requestIdentity)
                : AdmissionResult.Of(AdmissionResultKind.Error, mutate.Reason ?? "invalid_request", "终局落盘失败。", requestIdentity);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>统一关闭接口（§4.2c 两个合法分支共用：当前所有者+当前 revision+目标发送身份匹配；绝不消解 Pending；关闭即同次原子发布更新 Operations 并移除 Submission）。</summary>
    private LeaseMutateResult CloseSubmission(LeaseSegment lease, SubmissionRecord submission, Func<LogicalOwnerLeaseFile, string?> applyOutcome)
    {
        // 修订号锁内就地读取（MutateHandoffLatest）：本方法原「Read()→MutateHandoff(捕获修订)」在并发下会被
        // 任何一次同胞写入顶掉修订号而误判 lease_stale_generation（⑤c 并发实证）。回调内身份关联校验保证
        // 幂等（重复关闭=submission_identity_mismatch 自然拒绝）；所有权真更替/过期仍在锁内身份+TTL 校验处响亮拒绝。
        return _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var current = file.Handoff?.Submission;
            // 旧回执不关闭另一笔 Submission（sendSeq+身份关联）。
            if (current is null
                || !string.Equals(current.SubmissionIdentity, submission.SubmissionIdentity, StringComparison.Ordinal)
                || current.SendSeq != submission.SendSeq)
                return "submission_identity_mismatch";
            var reason = applyOutcome(file);
            if (reason is not null) return reason;
            var op = FindOp(file, submission);
            if (op is not null)
            {
                op.UpdatedRevision = file.Revision + 1;
                op.UpdatedAtUtc = _utcNow();
            }

            file.Handoff!.Submission = null; // Pending 独立存续——绝不顺带消解（票据压制与恢复责任保持至 settle/restore 闭环）
            return null;
        });
    }

    private static OperationRecord? FindOp(LogicalOwnerLeaseFile file, string requestIdentity)
        => (file.Handoff?.Operations ?? []).FirstOrDefault(o => string.Equals(o.RequestIdentity, requestIdentity, StringComparison.Ordinal));

    private static OperationRecord? FindOp(LogicalOwnerLeaseFile file, SubmissionRecord submission)
        => (file.Handoff?.Operations ?? []).FirstOrDefault(o =>
            string.Equals(o.SubmissionIdentity, submission.SubmissionIdentity, StringComparison.Ordinal)
            && o.LastSendSeq == submission.SendSeq);

    /// <summary>
    /// 待对账迁移（会诊 P2 处置：必须校验「结果对应的发送轮次」完整关联身份，不能只看状态枚举）。
    /// 缺失该关联时，迟到的一轮 Unknown 结果会在新一轮已 Granted 时把新轮责任改成 Reconciling
    /// （同时 Latest 变体不再提供修订 CAS 的偶然保护）。故本方法强制要求 submissionIdentity+sendSeq 匹配。
    /// </summary>
    private Task<LeaseMutateResult> MarkReconcilingAsync(string requestIdentity, LeaseSegment lease, string submissionIdentity, int sendSeq)
        => TransitionSingleAsync(requestIdentity, lease, OperationRequestState.Reconciling, markSubmissionReconciling: true,
            expectedSubmissionIdentity: submissionIdentity, expectedSendSeq: sendSeq,
            OperationRequestState.Granted, OperationRequestState.Sending, OperationRequestState.Reconciling);

    private async Task<LeaseMutateResult> TransitionSingleAsync(string requestIdentity, LeaseSegment lease, OperationRequestState state,
        bool markSubmissionReconciling = false, string? expectedSubmissionIdentity = null, int? expectedSendSeq = null,
        params OperationRequestState[] expectedStates)
    {
        var read = _store.Read();
        if (read.File?.Lease is null)
            return new LeaseMutateResult { Success = false, Reason = "lease_not_valid", File = null };
        var now = _utcNow();
        var result = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
        {
            var op = FindOp(file, requestIdentity);
            if (op is null || op.Zone != OperationZone.Active) return "stale_operation_identity";
            // 轮次关联校验（会诊 P2）：迟到结果不得改写更新轮次的责任（answeredSendSeq 语义的写入侧护栏）。
            if (expectedSendSeq is { } seq
                && (op.LastSendSeq != seq
                    || !string.Equals(op.SubmissionIdentity, expectedSubmissionIdentity, StringComparison.Ordinal)))
                return "state_changed";
            // B1：状态已被其他处理者推进=不覆盖（响亮失败，调用方分类返回当前事实）。
            if (expectedStates.Length > 0 && !expectedStates.Contains(op.RequestState)) return "state_changed";
            op.RequestState = state;
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = now;
            if (markSubmissionReconciling
                && file.Handoff?.Submission is { } sub
                && string.Equals(sub.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal))
                sub.State = SubmissionState.Reconciling; // Reconciling→Submitting 仅恢复责任阶段，不得重新触发发送
            return null;
        });
        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    private static AdmissionResult ClassifyMutateReject(string reason, string requestIdentity)
        => AdmissionResult.Of(AdmissionResultKind.Error, reason, reason switch
        {
            "lease_stale_generation" => "租约所有权身份/TTL 校验失败（或调用方快照修订已过期）——响亮拒绝；未决事实不变，所有权更替后旧身份不得借新身份写入。",
            "switch_gate_active" => "切换闸门激活期间禁止意图发布。",
            "residue_reconcile_pending" => "崩窗残件未对账，持续准入约束。",
            "corrupt" => "租约文件损坏（保守待对账）。",
            "unsupported_version" => "租约文件版本不受支持。",
            "stale_operation_identity" => "Operations 记录缺失或非 Active（响亮拒绝，不回退创建）。",
            _ when reason.StartsWith("operations_capacity_full", StringComparison.Ordinal) => "操作容量背压：" + reason,
            _ => "存取拒绝：" + reason,
        }, requestIdentity);

    // ============================================================
    // 重试/窗口到期/重启恢复（§3.3-6/§4.1a/八轮实施关注 1）
    // ============================================================

    /// <summary>
    /// 重试入口（§3.3：预算内重新 Admit——请求身份不变、sendSeq 随占位递增；I2 并发重试合并：
    /// 在途=合并不新增发送者，迟到重试按当前事实分类返回；可重试/排队状态入队走统一轮次（与排队候选同一仲裁面排序）；
    /// 重试资格/窗口/预算在占位事务内按持久化发送史原子复核——经 Queued 重入同样受约束，跨实例一致，§3.2a/B2）。
    /// </summary>
    public async Task<AdmissionResult> RetryAsync(string requestIdentity)
    {
        if (_hooks.F11Active())
            return AdmissionResult.Of(AdmissionResultKind.F11Blocked, "f11_active", "F11 独立停止闸门激活。", requestIdentity);
        // 并发重试合并（I2）：本进程已有在途处理者=合并不新增发送者。
        lock (_queueLock)
        {
            if (_inflight.Contains(requestIdentity))
                return AdmissionResult.Of(AdmissionResultKind.NeedReconcile, "in_flight", "已有重试/处理在途（合并，不新增发送者）。", requestIdentity);
        }

        // N2：读快照+状态分类在同一进程内串行段（跨线程并发不争用存取锁）。
        AdmissionResult? early = null;
        AdmissionRequest? retryRequest = null;
        LeaseSegment? captured = null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.Status == ArbitrationLeaseStatus.Corrupt)
            {
                early = AdmissionResult.Of(AdmissionResultKind.Error, "corrupt", "租约文件损坏（保守待对账）。", requestIdentity);
            }
            else if (read.Status == ArbitrationLeaseStatus.Unsupported)
            {
                early = AdmissionResult.Of(AdmissionResultKind.Error, "unsupported_version", "租约文件版本不受支持。", requestIdentity);
            }
            else if (read.File?.Lease is null)
            {
                early = AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            }
            else
            {
                var op = FindOp(read.File, requestIdentity);
                if (op is null)
                {
                    early = AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
                }
                else
                {
                    switch (op.RequestState)
                    {
                        case OperationRequestState.RetryableRejected:
                        case OperationRequestState.Queued:
                        case OperationRequestState.InRound:
                            // 重试按 Operations 权威快照重建请求（完整身份八段/排序键/显式 ActionId——分派身份内部一致）。
                            var candidate = op.Candidate is not null
                                ? CloneCandidate(op.Candidate)
                                : new ArbitrationCandidate { PayloadFingerprint = op.PayloadFingerprint, ResourceRef = op.ResourceRef, Intent = op.Intent };
                            retryRequest = new AdmissionRequest
                            {
                                Namespace = candidate.Namespace ?? "manual",
                                RequestIdentity = requestIdentity,
                                Kind = AdmissionKind.ContinueUse,
                                Candidate = candidate,
                                WireSubmitKey = op.WireSubmitKey,
                                RunBinding = op.RunBinding,
                                CursorRef = op.CursorRef,
                                CursorRevision = op.CursorRevision,
                            };
                            captured = read.File.Lease;
                            break;
                        default:
                            // I2：迟到/并发重试按当前事实返回——不重复消耗预算、不把已受理误报终局拒绝。
                            early = ClassifyCurrentState(requestIdentity);
                            break;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (early is not null) return early;

        // 入队走统一轮次（I2：与排队候选同一仲裁面排序，并发重试经去重合并）。
        var pending = Enqueue(retryRequest!, captured!);
        _ = Task.Run(() => DrainRoundAsync());
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>重试窗口到期扫描（§3.3-6：仅针对有确定拒绝证据的操作；Unknown/Reconciling 一律不动）。</summary>
    public int SweepExpiredRetryWindows()
    {
        _gate.Wait();
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null) return 0;
            var now = _utcNow();
            var expired = (read.File.Handoff?.Operations ?? [])
                .Where(o => o.RequestState == OperationRequestState.RetryableRejected
                            && o.Zone == OperationZone.Active
                            && o.RetryWindowDeadlineUtc is { } d && d <= now)
                .Select(o => o.RequestIdentity)
                .ToList();
            // B8：流程起点捕获身份——整个扫描沿用（逐项锁内身份/TTL 核验，不逐项重取身份）。
            var captured = read.File.Lease;
            var count = 0;
            foreach (var identity in expired)
            {
                if (ReviewExpiredOperation(captured, identity, now)) count++;
            }

            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>到期锁内复核（流程起点捕获身份 + 锁内最新修订核验；复核不成立=保守停驻不臆断；仅确定拒绝证据可转终局）。</summary>
    private bool ReviewExpiredOperation(LeaseSegment captured, string requestIdentity, DateTimeOffset now)
    {
        var current = _store.Read();
        if (current.File?.Lease is null) return false;
        var mutate = _store.MutateHandoffLatest(captured.LeaseId, captured.OwnerEpoch, file =>
        {
            var op = FindOp(file, requestIdentity);
            if (op is null || op.RequestState != OperationRequestState.RetryableRejected) return "state_changed";
            // 锁内复核：最近发送确定未受理（结果对应最后发送轮次）且无更新发送责任（无匹配未决 Submission）。
            var determinedNotAccepted = op.LastResult is { Outcome: OperationOutcome.Rejected } r
                                        && r.AnsweredSendSeq == op.LastSendSeq;
            var noNewerSendDuty = file.Handoff?.Submission is null
                                  || !string.Equals(file.Handoff.Submission.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal);
            if (!determinedNotAccepted || !noNewerSendDuty) return "review_inconclusive";
            op.RequestState = OperationRequestState.TerminalRejected;
            op.LastResult!.ReasonCode = "retry_window_expired";
            op.Zone = OperationZone.TerminalPendingTransfer;
            op.UpdatedRevision = file.Revision + 1;
            op.UpdatedAtUtc = now;
            MigrateAndClean(file, now);
            return null;
        });
        return mutate.Success;
    }

    /// <summary>
    /// 重启恢复（八轮实施关注 1——「登记后入队前崩溃」窗口）：
    /// ①Submission.Submitting→Reconciling（恢复者不得仅因 Submitting 发送；继承原身份保守对账）；
    /// ②Active 且 Queued/InRound 且从未发布发送许可（LastSendSeq==0）且无未决 Submission 的操作——
    ///   权威串行边界确认「从未发布发送许可+无当前处理者（重启后门面清空+串行段持有）+无更新责任」后
    ///   按 §4.1a 终局判据表第一行明确终局中止（不凭 Submission 为空单独推导）；
    /// ③Unknown/Reconciling 持续停驻不动（处置入口=SettleReconciledAsync）。
    /// 仅在进程重启后、接受新提交前调用（串行段持有=进程内无当前处理者的权威边界）。
    /// </summary>
    public int RecoverAfterRestart()
    {
        _gate.Wait();
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null) return 0;
            var now = _utcNow();
            var recovered = 0;
            var mutate = _store.MutateHandoffLatest(read.File.Lease.LeaseId, read.File.Lease.OwnerEpoch, file =>
            {
                if (file.Handoff is null) return null;
                // ① 未决发送责任保守转对账（不重新触发发送）。
                if (file.Handoff.Submission is { State: SubmissionState.Submitting } sub)
                {
                    sub.State = SubmissionState.Reconciling;
                    var owner = FindOp(file, sub);
                    if (owner is not null)
                    {
                        owner.RequestState = OperationRequestState.Reconciling;
                        owner.UpdatedRevision = file.Revision + 1;
                        owner.UpdatedAtUtc = now;
                    }
                }

                // ② 登记后未发布发送许可的孤儿登记→终局中止（三无确认在本串行边界内完成）。
                foreach (var op in file.Handoff.Operations.Where(o =>
                             o.Zone == OperationZone.Active
                             && o.MergedInto is null // 合并项由 ④ 专属处理（不得按孤儿登记中止）
                             && (o.RequestState == OperationRequestState.Queued || o.RequestState == OperationRequestState.InRound)
                             && o.LastSendSeq == 0
                             && (file.Handoff.Submission is null
                                 || !string.Equals(file.Handoff.Submission.SubmissionIdentity, o.SubmissionIdentity, StringComparison.Ordinal))))
                {
                    op.RequestState = OperationRequestState.TerminalRejected;
                    op.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = "abandoned_before_send", Retryable = false, RetryBudgetUsed = 0, EvidenceSource = "restart_recovery", AnsweredSendSeq = 0 };
                    op.Zone = OperationZone.TerminalPendingTransfer;
                    op.UpdatedRevision = file.Revision + 1;
                    op.UpdatedAtUtc = now;
                    recovered++;
                }

                // ④ 去重合并孤儿（B6 恢复关联）：目标已终局→同边界镜像；目标未终局→保持关联（目标结清时共同镜像）；
                //    目标记录缺失=终局拒绝（不悬置不静默）。
                foreach (var m in file.Handoff.Operations.Where(o =>
                             o.Zone == OperationZone.Active && o.MergedInto is not null
                             && o.RequestState is OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected))
                {
                    var target = file.Handoff.Operations.FirstOrDefault(o => string.Equals(o.RequestIdentity, m.MergedInto, StringComparison.Ordinal));
                    switch (target?.RequestState)
                    {
                        case OperationRequestState.Accepted or OperationRequestState.TerminalCompleted:
                            m.RequestState = OperationRequestState.Accepted;
                            m.SubmissionIdentity = target.SubmissionIdentity;
                            m.LastSendSeq = target.LastSendSeq;
                            m.TakeoverRef = target.TakeoverRef;
                            m.LastResult = new OperationResult { Outcome = OperationOutcome.Accepted, ReasonCode = "", Retryable = false, RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "merged:" + target.SubmissionIdentity, AnsweredSendSeq = target.LastSendSeq };
                            m.UpdatedRevision = file.Revision + 1;
                            m.UpdatedAtUtc = now;
                            recovered++;
                            break;
                        case OperationRequestState.TerminalRejected:
                        case OperationRequestState.NotSelected:
                            m.RequestState = target.RequestState;
                            m.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = target.LastResult?.ReasonCode ?? "terminal_rejected", Retryable = false, RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "merged:restart_recovery", AnsweredSendSeq = m.LastSendSeq, WinnerRef = target.LastResult?.WinnerRef, SuppressionSource = target.LastResult?.SuppressionSource };
                            m.Zone = OperationZone.TerminalPendingTransfer;
                            m.UpdatedRevision = file.Revision + 1;
                            m.UpdatedAtUtc = now;
                            recovered++;
                            break;
                        case null:
                            m.RequestState = OperationRequestState.TerminalRejected;
                            m.LastResult = new OperationResult { Outcome = OperationOutcome.Rejected, ReasonCode = "merged_target_missing", Retryable = false, RetryBudgetUsed = m.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "restart_recovery", AnsweredSendSeq = m.LastSendSeq };
                            m.Zone = OperationZone.TerminalPendingTransfer;
                            m.UpdatedRevision = file.Revision + 1;
                            m.UpdatedAtUtc = now;
                            recovered++;
                            break;
                        default:
                            break; // 目标未终局——保持合并关联（目标结清时共同镜像）
                    }
                }

                MigrateAndClean(file, now);
                return null;
            });

            // ⑤ Accepted 崩溃留滞收敛（会诊 建议-3 处置 + 复核 阻断-1 处置——进程在「受理→驱动终态→终局回写」窗口
            //    崩溃，火忘回写未执行=Active 占槽泄漏）。**不得在 MutateHandoff 变更回调内调用钩子**——宿主
            //    TakeoverTerminalConfirmed 会 Read() 本店，而回调执行时跨进程锁（FileShare.None）仍持有=同进程
            //    共享冲突重入（会诊复核实证）；故改为 mutate 提交后逐操作锁外终局化（MarkOperationTerminal 内部
            //    自带钩子确认+独立事务）。无权威终态=不动（驱动可能在途/留待对账）。
            if (mutate.Success)
            {
                // 钩子必须在锁外调用：宿主 TakeoverTerminalConfirmed 内部会 Read() 本店，而本方法自始持有 _gate、
                // 目任何锁内回调亦持跨进程锁——锁内调用=同进程共享冲突重入（会诊复核实证）。
                // 另：终局化不得走 MarkOperationTerminal（它自取 _gate，SemaphoreSlim 不可重入=自死锁），
                // 故此处直接使用存储层原子变更写入（仍受锁内身份/TTL 校验与残件闸门约束）。
                // 会诊阻断项处置：所有者身份必须沿用首事务（本方法起点）捕获的 leaseId/ownerEpoch，绝不重新取快照里的
                // 当前租约——期间若发生接管，重取会把新所有者的身份当成自己的身份写入（违反「旧流程不得借新身份继续」）。
                // 沿用旧身份时，真发生更替会在锁内身份校验处响亮拒绝 lease_stale_generation（正是期望行为）。
                var ownLease = mutate.File?.Lease;
                var confirmed = ownLease is null
                    ? new List<string>()
                    : (mutate.File!.Handoff?.Operations ?? [])
                        .Where(o => o.Zone == OperationZone.Active && o.RequestState == OperationRequestState.Accepted)
                        .Where(o => o.SubmissionIdentity is not null
                                    && _hooks.TakeoverTerminalConfirmed?.Invoke(o.SubmissionIdentity, o.LastSendSeq) == true)
                        .Select(o => o.RequestIdentity)
                        .ToList();
                foreach (var identity in confirmed)
                {
                    var terminalNow = _utcNow();
                    var terminal = _store.MutateHandoffLatest(ownLease!.LeaseId, ownLease.OwnerEpoch, file =>
                    {
                        var op = FindOp(file, identity);
                        if (op is null || op.Zone != OperationZone.Active || op.RequestState != OperationRequestState.Accepted)
                            return "state_changed";
                        op.RequestState = OperationRequestState.TerminalCompleted;
                        op.LastResult = new OperationResult { Outcome = OperationOutcome.Accepted, ReasonCode = "terminal_confirmed_after_restart", Retryable = false, RetryBudgetUsed = op.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "restart_recovery:terminal_confirmed", AnsweredSendSeq = op.LastSendSeq };
                        op.Zone = OperationZone.TerminalPendingTransfer;
                        op.UpdatedRevision = file.Revision + 1;
                        op.UpdatedAtUtc = terminalNow;
                        MigrateAndClean(file, terminalNow);
                        return null;
                    });
                    if (terminal.Success) recovered++;
                }
            }

            return mutate.Success ? recovered : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    // 容量联动（§4.1a：primarySlotsUsed=Active+TerminalPendingTransfer ≤ 32；检查/清理迁移/新登记同一权威串行边界）
    // ============================================================

    /// <summary>创建准入双条件（先迁移可迁项再重判主槽位；仍不满足=响亮拒绝 operations_capacity_full 含四项诊断）。</summary>
    private string? EnsureCapacityForCreate(LogicalOwnerLeaseFile file, DateTimeOffset now)
    {
        file.Handoff ??= new LeaseHandoffSegment();
        MigrateAndClean(file, now);
        var ops = file.Handoff.Operations;
        var active = ops.Count(o => o.Zone == OperationZone.Active);
        var pendingTransfer = ops.Count(o => o.Zone == OperationZone.TerminalPendingTransfer);
        var tombstones = ops.Count(o => o.Zone == OperationZone.Tombstone);
        if (active + pendingTransfer >= PrimarySlotLimit)
            return CapacityReject(active, pendingTransfer, tombstones, ops, now);
        var cleanable = ops.Where(o => o.Zone == OperationZone.Tombstone && o.UpdatedAtUtc + TombstoneRetain <= now)
            .OrderBy(o => o.UpdatedAtUtc).FirstOrDefault()?.UpdatedAtUtc;
        if (tombstones >= TombstoneLimit && cleanable is null)
            return CapacityReject(active, pendingTransfer, tombstones, ops, now);
        return null;
    }

    private static string CapacityReject(int active, int pendingTransfer, int tombstones, IReadOnlyCollection<OperationRecord> ops, DateTimeOffset now)
    {
        var earliest = ops.Where(o => o.Zone == OperationZone.Tombstone).OrderBy(o => o.UpdatedAtUtc).FirstOrDefault()?.UpdatedAtUtc;
        var earliestCleanable = earliest is { } e ? e + TombstoneRetain : (DateTimeOffset?)null;
        // 拒绝诊断四项：Active 数/待迁移数/墓碑数/最早到期可清理时间（七轮建议②——可观测可处置）。
        return "operations_capacity_full(active=" + active + ",pendingTransfer=" + pendingTransfer + ",tombstone=" + tombstones
               + ",earliestCleanable=" + (earliestCleanable?.ToString("O") ?? "none") + ")";
    }

    /// <summary>清理迁移（权威串行边界内）：到期墓碑（≥24h）确实删除→TerminalPendingTransfer 按终局时刻序迁入墓碑空位（迁入即释放主槽位）。</summary>
    private void MigrateAndClean(LogicalOwnerLeaseFile file, DateTimeOffset now)
    {
        if (file.Handoff is null) return;
        var ops = file.Handoff.Operations;
        ops.RemoveAll(o => o.Zone == OperationZone.Tombstone && o.UpdatedAtUtc + TombstoneRetain <= now);
        var tombstones = ops.Count(o => o.Zone == OperationZone.Tombstone);
        foreach (var op in ops.Where(o => o.Zone == OperationZone.TerminalPendingTransfer).OrderBy(o => o.UpdatedAtUtc))
        {
            if (tombstones >= TombstoneLimit) break;
            op.Zone = OperationZone.Tombstone;
            op.UpdatedAtUtc = now; // 迁入起算 24h 最短保留
            op.UpdatedRevision = file.Revision + 1;
            tombstones++;
        }
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>scope=bgi:{实例id}:{bgiEpoch}——epoch=首次构造时捕获并固定（I-1），提交时只比较不重写。</summary>
    internal static string ExtractEpoch(string scope)
    {
        // 只切前两段：scope=bgi:{实例id}:{bgiEpoch}，而生产 epoch 本身形如 "{ProcessId}:{StartTicksUtc}"
        // （含冒号）——按全量 Split 取 [2] 会把 epoch 截断成 pid，导致 stale_epoch 确定性误拒（会诊阻断项）。
        var parts = (scope ?? "").Split(':', 3);
        return parts.Length >= 3 ? parts[2] : "";
    }

    /// <summary>排序键指纹（tier/priority/scheduledAt/运行绑定——同身份不同排序键=终局拒绝 §3.3；绑定纳入指纹：不同绑定不去重共享）。</summary>
    internal static string SortKeyFingerprintOf(ArbitrationCandidate c, string? runBinding, string? cursorRef, long? cursorRevision)
        => ((int)c.Tier) + ":" + c.Priority + ":" + (c.ScheduledAt?.ToString("O") ?? "~")
           + "|" + (runBinding ?? "~") + "|" + (cursorRef ?? "~") + "|" + (cursorRevision?.ToString() ?? "~");
}
