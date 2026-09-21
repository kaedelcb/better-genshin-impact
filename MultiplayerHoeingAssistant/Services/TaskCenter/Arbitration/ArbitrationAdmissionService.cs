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
    /// <summary>
    /// **恢复分支**（仅恢复专用边界填充：`paused-continue`／`interrupted-relocate`）——
    /// 进程内判定输入（**不序列化**）：用于把「A6 原票据恢复」与「暂停续行」分开，
    /// 避免暂停续行借用 A6 恢复豁免（小节会诊整改）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RecoveryBranch { get; set; }
    /// <summary>运行台账合法游标（Runner 后继授权：游标合法+授权未消费双条件）。</summary>
    public string? CursorRef { get; set; }
    public long? CursorRevision { get; set; }
    /// <summary>
    /// **可信持久化操作类型**（R5.3 §24.17）：由**可信适配器按调用位置**提供（E1/首节点＝`FlowRegistration`、
    /// 后继节点＝`NodeExecution`、E3/E4/E5＝`ExternalStart`、移交＝`Handoff`）；缺失/`Unknown` ⇒ **类型相关判定 fail-closed**
    /// （不得按 `ResourceRef`、`RunId` 空值或运行快照猜测）。
    /// </summary>
    public OperationType OperationType { get; set; } = OperationType.Unknown;
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
    /// <summary>
    /// **取消**（R5.3 §24.2-2：完成层 `Cancelled` 驱动）——入口按取消口径回执并停止批次推进；
    /// 责任是否结清由 <see cref="AdmissionResult.ResponsibilityState"/> 表达（**不得**压成普通失败）。
    /// </summary>
    Cancelled,
    /// <summary>**确定执行失败**（R5.3 §24.9／§24.13-1：完成层 `ExecutionFailed` 驱动；与拒绝/未知分离、**不得触发重发**）。</summary>
    ExecutionFailed,
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

    // ==== R5.3 §24.6-1/2：结果维 × 责任维（加法字段，旧调用默认值兼容） ====
    /// <summary>远端作业句柄（§24.3；无则 null，不得臆造）。</summary>
    public string? JobId { get; set; }
    /// <summary>执行结果维（§24.6-1）。</summary>
    public ExecutionDisposition ExecutionDisposition { get; set; } = ExecutionDisposition.None;
    /// <summary>责任维（§24.6-5：`None`/`Pending`/`Settled`）。</summary>
    public ResponsibilityState ResponsibilityState { get; set; } = ResponsibilityState.None;
    /// <summary>原始终态词（不伪造）。</summary>
    public string? RawTerminal { get; set; }
    /// <summary>完成层执行错误码（与信封 `ErrorCode` 语义分离）。</summary>
    public string? ExecutionErrorCode { get; set; }
    /// <summary>证据来源（原始回执词/对账结论+产生端）。</summary>
    public string? EvidenceSource { get; set; }

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
    /// <summary>
    /// 受理（evidenceSource=原始回执词+产生端；runId=托管流程关联运行）。
    /// **R5.3 §24.2-2**：`jobId`＝远端作业句柄（无则 null，**不得臆造**）；**不设 `Terminal`**——
    /// 终态事实只由完成层 `ExternalStartCompletion`／`SettleCompletionAsync` 承载，杜绝第三套终态来源。
    /// </summary>
    public sealed record Accepted(string EvidenceSource, string? RunId, string? JobId = null) : SendOutcome;
    /// <summary>经关联验证的确定未受理（先关闭 Submission 再按 §3.3 记可重试/终局）。</summary>
    public sealed record Rejected(string ReasonCode, bool Retryable, string EvidenceSource) : SendOutcome;
    /// <summary>
    /// 未知（Submission.Reconciling；不换键重跑）。
    /// **R5.3 §24.6-2**：携带证据来源（原始回执词/产生端），保证发送层三态均为**字段级无损**映射。
    /// </summary>
    public sealed record Unknown(string Detail, string? EvidenceSource = null) : SendOutcome;
}

/// <summary>
/// 显式对账结清证据（§8 待对账处置入口=owner 显式对账动作/权威事实到达；仅两分支——
/// 受理且接管持久化 / 经关联验证的确定未受理，不设第三关闭依据）。
/// </summary>
public abstract record ReconcileSettlement
{
    /// <summary>
    /// 权威事实确认「曾受理」（evidenceSource=对账结论+产生端，保留原始证据来源、不伪造远端回执词）。
    /// 证据必须携带原发送关联——旧轮次证据不得关闭新轮次责任。
    /// **R5.3 §24.3-4**：`jobId`＝远端句柄；`completion`＝可选的完成层结果（**不是**发送层 `Terminal` 布尔）：
    /// 缺省/null ⇒ 普通受理；`Unknown` ⇒ 保持观察依据且**不得**生成 `PendingTerminal`；
    /// `Succeeded/Cancelled/ExecutionFailed` ⇒ 先写 `ExecutionResult`＋`PendingTerminal` 再按 §24.15 结算。
    /// </summary>
    public sealed record Accepted(
        string SubmissionIdentity, int SendSeq, string EvidenceSource, string? RunId,
        string? JobId = null, ExternalStartCompletion? Completion = null) : ReconcileSettlement;
    /// <summary>权威事实确认「确定未受理」。同上携带原发送关联。</summary>
    public sealed record NotAccepted(string SubmissionIdentity, int SendSeq, string ReasonCode, bool Retryable, string EvidenceSource) : ReconcileSettlement;
}

/// <summary>台账句柄读取状态（[第八轮验证会诊]：**读取失败/不可确认**必须与「读取成功但无句柄」严格区分）。</summary>
public enum LedgerHandleState
{
    /// <summary>读取失败/台账损坏/记录不可确认 ⇒ **保守停驻**（不得当作「无句柄」放行终局）。</summary>
    Unreadable = 0,
    /// <summary>读取成功但该发送身份无句柄。</summary>
    Absent = 1,
    /// <summary>读取成功且存在句柄。</summary>
    Present = 2,
}

    /// <summary>台账句柄读取结论（`TakeoverJobIdRead` 返回；三态判别式，避免 fail-open）。</summary>
    public sealed record LedgerHandleProbe(LedgerHandleState State, string? JobId)
{
    public static LedgerHandleProbe Unreadable() => new(LedgerHandleState.Unreadable, null);
    public static LedgerHandleProbe Absent() => new(LedgerHandleState.Absent, null);
    public static LedgerHandleProbe Present(string jobId) => new(LedgerHandleState.Present, jobId);
}

/// <summary>
/// **外部启动台账扫描结论**（`AdmissionHooks.TakeoverLedgerScan` 返回；R5.3 §24.12-3 恢复集合②/③；
/// [Batch B 收尾之五] 新增）：`Readable=false`＝台账不可读/损坏 ⇒ 恢复扫描**保守停驻**（不推导、不释放）。
/// </summary>
public sealed record TakeoverLedgerScan(bool Readable, IReadOnlyList<TakeoverLedgerFact> Facts, string? Detail = null)
{
    public static TakeoverLedgerScan Unreadable(string? detail = null) => new(false, [], detail ?? "ledger_unreadable");
}

/// <summary>
/// **台账记录事实**（恢复扫描只需「完整发送身份 ＋ 是否已终态 ＋ 可查询句柄」；终态**载荷**一律以本地持久化的
/// `PendingTerminal` 为准，不得按原始终态词猜类别）。`JobId` 供**持续观察/重新取证**使用（§24.5-2／§24.16-3）。
/// </summary>
public sealed record TakeoverLedgerFact(string SubmissionIdentity, int SendSeq, bool Terminal, string? JobId = null);

/// <summary>**外部启动观察恢复报告**（R5.3 §24.12-3 集合②/③；[Batch B 收尾之五] 新增）。</summary>
public sealed record ExternalStartRecoveryReport(
    bool LedgerUnreadable,
    int ObservationKept,
    int TerminalizationCompleted,
    int TerminalizationFailed,
    int TerminalWithoutPendingTerminal,
    int LeaseTerminalButLedgerOpen,
    int OrphanLedgerEntries,
    int ConflictPendingSkipped = 0,
    int IncompletePendingPayload = 0)
{
    public bool AnythingReported =>
        LedgerUnreadable || ObservationKept > 0 || TerminalizationCompleted > 0 || TerminalizationFailed > 0
        || TerminalWithoutPendingTerminal > 0 || LeaseTerminalButLedgerOpen > 0 || OrphanLedgerEntries > 0
        || ConflictPendingSkipped > 0 || IncompletePendingPayload > 0;

    public override string ToString()
        => LedgerUnreadable
            ? "台账不可读（保守停驻：责任保留、不释放、不重发）"
            : "观察中保留 " + ObservationKept + "；终局补写成功 " + TerminalizationCompleted + "／失败 "
              + TerminalizationFailed + "；台账终态缺 PendingTerminal " + TerminalWithoutPendingTerminal
              + "；租约/台账终局不一致 " + LeaseTerminalButLedgerOpen + "；台账孤儿 " + OrphanLedgerEntries
              + "；冲突待决（归裁决入口） " + ConflictPendingSkipped + "；载荷不完整（不驱动终局） "
              + IncompletePendingPayload;
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
    /// <summary>
    /// **台账权威终态写入**（R5.3 §24.15 完成结算事务中的「台账 Terminal」步；[Batch B] 新增）。
    /// 参数＝`submissionIdentity`／`sendSeq`／终态证据词／观察时点／原始终态词／完成层错误码／远端句柄。
    /// 返回 `null`＝已提交（含幂等：同观察时点同载荷）；非 `null`＝失败原因 ⇒ 门面**保守停驻**（不继续后续步骤、不重发）。
    /// **仅外部启动操作**会走本钩子（其余类型不写外部台账）；未配置 ⇒ 门面按「终态回写钩子缺失」保守停驻。
    /// </summary>
    public Func<string, int, string, DateTimeOffset, string?, string?, string?, string?, string?>? TakeoverTerminalPersist { get; set; }
    /// <summary>
    /// **台账终态载荷读回确认**（R5.3 §24.15 读回验证；[第二轮验证会诊阻断处置] 新增）：
    /// 台账回写失败/结果不明时，必须按**同一发送身份**读回并核对终态载荷（原始终态词／错误码／句柄／证据来源／观察时点）
    /// 是否与本次事实**逐字段等值**——仅「记录存在且为 Terminal」不足以判定本次提交成功。
    /// 未配置 ⇒ 门面按「未确认」保守停驻（不使用宽松布尔确认代替）。
    /// </summary>
    public Func<string, int, string?, string?, string?, string?, DateTimeOffset, bool>? TakeoverTerminalPayloadConfirmed { get; set; }
    /// <summary>
    /// **台账已登记句柄读回**（R5.3 §24.3-3「一侧为空＝补齐」；[第四轮验证会诊] 新增；[第八轮] 改为三态）：
    /// 返回该 `submissionIdentity+sendSeq` 在接管台账中的**合并后权威句柄**及其**读取状态**——
    /// `Unreadable`（读取失败/损坏/不可确认）**不得**与 `Absent`（读取成功但无句柄）混淆：前者必须保守停驻。
    /// </summary>
    public Func<string, int, LedgerHandleProbe>? TakeoverJobIdRead { get; set; }
    /// <summary>
    /// **外部启动台账扫描**（R5.3 §24.12-3 恢复集合②/③；[Batch B 收尾之五] 新增）：返回**完整发送身份**维度的
    /// 台账记录集合（至少含 `submissionIdentity`／`sendSeq`／是否已终态）。`Readable=false`＝台账不可读/损坏
    /// ⇒ 恢复扫描**保守停驻**（不推导、不释放）。未配置＝不可读（不得当作「无记录」）。
    /// </summary>
    public Func<TakeoverLedgerScan>? TakeoverLedgerScan { get; set; }
    /// <summary>
    /// **权威未受理观察可信性校验**（R5.3 §24.2-2″；[第五轮验证会诊阻断处置] 新增）：
    /// 由可信观察层提供校验器——返回 `null`＝来源可信；非 `null`＝拒绝原因。
    /// **未配置 ⇒ 一律拒绝**（`observation_source_unverified`）：不得让任意调用方自造观察解除冲突阻断。
    /// </summary>
    public Func<NotAcceptedObservation, string?>? NotAcceptedObservationVerifier { get; set; }
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
        // §24.17-3（[Batch B 收尾]）：**创建必须携带可信操作类型**——缺失/`Unknown` 一律 fail-closed
        // （类型相关判定不得按 `ResourceRef`/`RunId`/快照猜测；旧格式代隔离产物不得凭新登记绕过）。
        if (request.Kind == AdmissionKind.Create
            && (!Enum.IsDefined(request.OperationType) || request.OperationType == OperationType.Unknown))
            return AdmissionResult.Of(AdmissionResultKind.Error, "operation_type_required",
                "创建操作必须由可信适配器提供操作类型（缺失/Unknown/未定义枚举一律 fail-closed）。", request.RequestIdentity);

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
                    // §24.17（[R5.3 落地批次会诊阻断处置]）：**可信操作类型必须随冻结副本一起携带**——
                    // 否则创建时落盘恒为 `Unknown`，外部台账分派与类型相关判定全部失效（fail-closed 方向被绕过）。
                    OperationType = request.OperationType,
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
                        OperationType = frz.OperationType, // §24.17：创建时由可信适配器提供、同次原子发布、后续不可改写
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
        // [第二轮验证会诊阻断处置] **冲突优先**：待决冲突覆盖历史拒绝/可重试分类（不得让调用方把未裁决责任当已结清）。
        if (op.ConflictPending)
            return new AdmissionResult
            {
                Kind = AdmissionResultKind.NeedReconcile,
                ReasonCode = "conflict_pending",
                Detail = "存在待决冲突（待权威裁决：不释放占用、禁止重发）。",
                RequestIdentity = request.RequestIdentity,
                SubmissionIdentity = op.SubmissionIdentity,
                SendSeq = op.LastSendSeq,
                ExecutionDisposition = ExecutionDisposition.Unknown,
                ResponsibilityState = ResponsibilityState.Pending,
            };
        // [Batch B 收尾 会诊阻断处置] **持久化类型 fail-closed 必须早于重新入队/仲裁**——
        // 否则未知类型记录可能先被裁决改写为 `NotSelected`/`TerminalRejected` 并迁区，绕过隔离语义。
        // （顺序纪律：**冲突待决优先于类型隔离**——本记录冲突、**以及合并目标的冲突**都必须先于类型判断。）
        if (op.MergedInto is { } mergedIntoPre)
        {
            var targetPre = ops.FirstOrDefault(o => string.Equals(o.RequestIdentity, mergedIntoPre, StringComparison.Ordinal));
            if (targetPre is { ConflictPending: true })
                return new AdmissionResult
                {
                    Kind = AdmissionResultKind.NeedReconcile,
                    ReasonCode = "conflict_pending",
                    Detail = "去重合并目标存在待决冲突（待权威裁决：不释放占用、禁止重发）。",
                    RequestIdentity = request.RequestIdentity,
                    SubmissionIdentity = targetPre.SubmissionIdentity,
                    SendSeq = targetPre.LastSendSeq,
                    ExecutionDisposition = ExecutionDisposition.Unknown,
                    ResponsibilityState = ResponsibilityState.Pending,
                };
        }
        if (!Enum.IsDefined(op.OperationType) || op.OperationType == OperationType.Unknown)
            // §24.6-5：已登记操作的责任维不得回落 `None`（仍需隔离/人工处置）——带完整发送身份与 `Pending`。
            return new AdmissionResult
            {
                Kind = AdmissionResultKind.Error,
                ReasonCode = "legacy_operation_type_unresolved",
                Detail = "持久化操作类型缺失/未知（旧格式代隔离产物）：不得重新驱动或改写状态，需显式隔离/迁移处置。",
                RequestIdentity = request.RequestIdentity,
                SubmissionIdentity = op.SubmissionIdentity,
                SendSeq = op.LastSendSeq,
                ExecutionDisposition = ExecutionDisposition.Unknown,
                ResponsibilityState = ResponsibilityState.Pending,
            };

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
        if (op.MergedInto is { } mergedInto)
        {
            var target = ops.FirstOrDefault(o => string.Equals(o.RequestIdentity, mergedInto, StringComparison.Ordinal));
            // [第三轮验证会诊阻断处置] 合并目标的冲突待决优先于**任何**本记录状态（含已镜像终态）——
            // 只要胜者责任未裁决，调用方就不得看到「已结清的拒绝结果」。
            if (target is { ConflictPending: true })
                return new AdmissionResult
                {
                    Kind = AdmissionResultKind.NeedReconcile,
                    ReasonCode = "conflict_pending",
                    Detail = "去重合并目标存在待决冲突（待权威裁决：不释放占用、禁止重发）。",
                    RequestIdentity = request.RequestIdentity,
                    SubmissionIdentity = target.SubmissionIdentity,
                    SendSeq = target.LastSendSeq,
                    ExecutionDisposition = ExecutionDisposition.Unknown,
                    ResponsibilityState = ResponsibilityState.Pending,
                };
            // [第五轮验证会诊阻断处置] 目标**已有执行事实**（含裁决后的取消/失败/成功）⇒ 共享目标事实，
            // 无论本（镜像）记录自身处于何种状态（历史镜像的 `TerminalRejected` 不得回放旧事实）。
            if (target is { ExecutionResult: not null }
                && target.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted)
                return ClassifyFromExecutionResult(request.RequestIdentity, target,
                    settled: target.RequestState == OperationRequestState.TerminalCompleted,
                    "already_accepted", "去重合并：共享胜者结果事实（含取消/失败，不新增发送者）。");
            // 未镜像完成前的共享结果分类；已镜像终态的落到下方 switch（按本记录状态分类）。
            if (op.RequestState is OperationRequestState.Queued or OperationRequestState.InRound or OperationRequestState.RetryableRejected)
                return target?.RequestState switch
            {
                OperationRequestState.Accepted or OperationRequestState.TerminalCompleted =>
                    // 共享胜者的**结果事实**（含取消/失败）；不得固定返回 Accepted（§24.6-4）。
                    ClassifyFromExecutionResult(request.RequestIdentity, target,
                        settled: target.RequestState == OperationRequestState.TerminalCompleted,
                        "already_accepted", "去重合并：共享胜者受理结果（不新增发送者）。"),
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
                return ClassifyFromExecutionResult(request.RequestIdentity, op, settled: false,
                    "already_accepted", "已受理（返回既有结果）。", winnerCandidateId: op.CandidateId);
            case OperationRequestState.TerminalCompleted:
                return ClassifyFromExecutionResult(request.RequestIdentity, op, settled: true,
                    "already_terminal", "终局完成（返回既有结果）。");
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
                RecoveryBranch = request.RestoreBranch, // A6 原票据恢复豁免只对 interrupted-relocate 生效
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
                    OperationType = OperationType.Recovery, // §24.17：恢复操作的可信类型
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
            case "ticket_suppressed" or "ticket_malformed" or "ticket_conflict" or "eligibility_lost" or "stale_epoch" or "identity_conflict" or "binding_conflict" or "pending_conflict":
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
        // ── §24.18-2／§24.14-2「两段式 `_gate`」（**仅外部启动**的启动/早期取证走锁外）─────────────────────
        // 第一段（锁内，本函数进入前由 `DrainRoundAsync` 持有 `_gate`）已完成：轮次快照 → 裁决 → **占位** →
        // 发布唯一发送责任与 `PreObservation`/`Submission`（同次权威原子发布；未持久化不得发送，§24.16-2）。
        // 第二段（**锁外**）：调用外部启动适配层的启动/早期受理（`StartAsync`／ext 队列提交）——该网络等待
        // **不得**占用权威串行权（§24.14-2 明文；与 §24.10-2「完成等待不得在门面锁内」同源）。
        // [施工方判定·有实测证据] 锁外窗口**限定 ExternalStart**：流程登记/节点执行的发送若同样放手，会让
        // 「E1 启动发送窗口内提交的节点操作」被并发轮次按占用冲突拒绝（实测 `TaskCenterSuccessorPathGateTests`
        // 两条已验证夹具转红：节点操作停留 `Queued`、`sends=0`、运行收敛 Unknown）——即该两类的并发语义需与
        // §3.1/§4.2a 的「在飞父操作占用判定」一并调整，属 B2-γ 批次范围（登记见 §24.21-C）。故此处按类型分派，
        // 保持流程登记/节点执行的既有单段串行（**逐字不变**）。
        // 锁外期间允许其他请求进入（并发裁决/接管/换主），故返回后必须重新取得串行权再结算（§24.18-3）。
        SendOutcome outcome;
        // [验证会诊阻断处置] 类型来源＝**已持久化的操作类型**（§24.17-2／§24.8-1：外部启动识别只按持久化类型；
        // 不得用调用方请求字段决定生命周期分支——续用/重驱动请求不回填类型、伪造请求更不可信）。
        var occupiedOp = occupy.File!.Handoff!.Operations.First(o =>
            string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
        var releaseGateDuringSend = occupiedOp.OperationType == OperationType.ExternalStart;
        // 占位基线（锁内捕获）：锁外返回后用它复核「本笔责任是否仍属本层」（§24.18-3）。
        // **必须以值快照捕获**（不得依赖可能被后续写入改写的对象引用）。
        var occupyBaseline = new OccupyBaseline(
            lease.LeaseId, lease.OwnerEpoch, occupiedOp.RequestState,
            occupy.File!.Handoff!.Submission!.SubmissionIdentity, occupy.File!.Handoff!.Submission!.SendSeq);
        if (releaseGateDuringSend) _gate.Release();
        try
        {
            try
            {
                // [验证会诊重要项处置] `BuildDispatch` 保留在「捕获异常⇒Unknown」归类内：派发快照缺失/损坏时与既有
                // 行为一致落 `Unknown`（已占位责任→`Reconciling`），不得因本批改变占位后的失败状态。
                outcome = await _hooks.Sender(
                    BuildDispatch(request, occupy.File!, stableIdentity, candidateId, targetEpoch)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                outcome = new SendOutcome.Unknown("发送回调异常（保守待对账，不重发）：" + ex.GetType().Name); // I1：已发出结果未知=对账，不抛出不悬置
            }
        }
        finally
        {
            // 无论发送成功/异常，都必须先回到权威串行边界（本函数返回后由 `DrainRoundAsync` 的 finally 释放）。
            if (releaseGateDuringSend) await _gate.WaitAsync().ConfigureAwait(false);
        }

        // [验证会诊阻断处置] 重取串行权后、**任何台账/适配器副作用之前**复核「本笔发送责任是否仍属本层」：
        // 锁外期间同一发送身份可能已被其他入口推进（换主后的恢复扫描转 `Reconciling`／对账判「确定未受理」／
        // 他人改写本笔轮次）——此时按占位快照写接管台账会造成「租约侧拒绝、台账侧受理」的分裂，旧身份还会
        // 覆盖新所有者名下的责任。仅在**锁外窗口**生效（流程登记/节点执行保持逐字不变）。
        if (releaseGateDuringSend)
        {
            var advanced = DetectSendResponsibilityAdvanced(request, occupyBaseline);
            if (advanced is not null) return advanced;
        }

        // §24.18-3 其余口径由既有机制承担（**以既有已验证夹具为证**）：每次写入都由
        // `_store.MutateHandoffLatest(leaseId, ownerEpoch, …)` 在锁内校验 owner/epoch/revision（换主/接管后
        // 旧身份写入一律响亮拒绝），且各回调内逐笔校验本笔 `submissionIdentity＋sendSeq`；证据＝
        // `OwnershipChanged_OldFlowWrite_LeaseStaleGeneration`（换主 ⇒ Reconciling＋`lease_stale_generation`＋
        // 未决事实不被旧身份推进）与 `StateAdvancedExternally_OldRoundDoesNotOverwrite`。
        return await ReconcileOutcomeAsync(request, lease, occupy.File!, outcome).ConfigureAwait(false);
    }

    /// <summary>
    /// **占位基线（锁内捕获；§24.18-3）**：锁外发送返回后用它复核「本笔责任是否仍属本层」。
    /// 以**值快照**捕获（不得依赖后续可能被改写的对象引用）。
    /// </summary>
    private sealed record OccupyBaseline(
        string LeaseId, string OwnerEpoch, OperationRequestState RequestState,
        string SubmissionIdentity, int SendSeq);

    /// <summary>
    /// **锁外发送后的责任归属复核（R5.3 §24.18-3/4）**：重新取得权威串行权后、**任何台账/适配器副作用之前**，
    /// 逐项核对：①当前租约 `leaseId／ownerEpoch` 仍等于占位时捕获值（未被接管/换代——换主后即使发送身份未变，
    /// 旧层也不得写台账/关闭/释放）；②同名操作的**请求状态**未被他人推进（如恢复扫描转 `Reconciling`、
    /// 对账判「确定未受理」）；③本笔发送身份（`submissionIdentity＋sendSeq`）在操作与 Submission 两侧仍是占位那一轮。
    /// 任一不成立 ⇒ **保守停驻**（`NeedReconcile`／责任 `Pending`／禁止重发／保留占用；**不写**接管台账、
    /// **不**关闭 Submission、**不**释放占用），把结算与冲突裁决交给当前所有者/恢复路径；成立 ⇒ `null`（继续既有
    /// `ReconcileOutcomeAsync`，其写入仍由 `MutateHandoffLatest` 复核 owner/epoch/revision）。
    /// **残余说明（如实登记）**：本复核与随后 `TakeoverPersist`（**独立文件**）之间不存在跨文件原子边界——
    /// 跨进程处理者仍可能在此缝隙内推进；该情形按 §24.12 恢复集合②/③与冲突裁决处置（登记 §24.22-C-8）。
    /// **归属边界**：非终态「曾受理」证据与「确定未受理」之间的冲突词表口径归冲突批次（登记 §24.21-C-1③）。
    /// </summary>
    private AdmissionResult? DetectSendResponsibilityAdvanced(
        AdmissionRequest request, OccupyBaseline baseline)
    {
        var file = _store.Read().File;
        if (file?.Lease is not { } currentLease)
            return Stop(request.RequestIdentity, "lease_unreadable_during_send", "锁外发送期间租约不可读——不得按占位快照写台账/释放占用；保守待对账、禁止重发。", baseline);
        if (!string.Equals(currentLease.LeaseId, baseline.LeaseId, StringComparison.Ordinal)
            || !string.Equals(currentLease.OwnerEpoch, baseline.OwnerEpoch, StringComparison.Ordinal))
            return Stop(request.RequestIdentity, "owner_or_lease_changed_during_send", "锁外发送期间租约代次/所有者已更替——发送责任移交当前所有者对账（不写台账、不关闭、不释放占用、禁止重发）。", baseline);

        var liveOp = file.Handoff?.Operations?.FirstOrDefault(o =>
            string.Equals(o.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal));
        var liveSub = file.Handoff?.Submission;
        var responsibilityIntact = liveOp is not null
            && liveOp.RequestState == baseline.RequestState
            && string.Equals(liveOp.SubmissionIdentity, baseline.SubmissionIdentity, StringComparison.Ordinal)
            && liveOp.LastSendSeq == baseline.SendSeq
            && liveSub is not null
            && string.Equals(liveSub.SubmissionIdentity, baseline.SubmissionIdentity, StringComparison.Ordinal)
            && liveSub.SendSeq == baseline.SendSeq;
        if (responsibilityIntact) return null;

        return Stop(request.RequestIdentity, "send_responsibility_advanced_during_send",
            "锁外发送期间本笔发送责任已被其他入口推进（换主恢复/对账/关闭/轮次改写）——"
            + "不得按占位快照写接管台账或释放占用；保守待对账、禁止重发。", baseline);
    }

    private static AdmissionResult Stop(
        string requestIdentity, string reasonCode, string detail, OccupyBaseline baseline)
        => new()
        {
            Kind = AdmissionResultKind.NeedReconcile,
            ReasonCode = reasonCode,
            Detail = detail,
            RequestIdentity = requestIdentity,
            SubmissionIdentity = baseline.SubmissionIdentity,
            SendSeq = baseline.SendSeq,
            ExecutionDisposition = ExecutionDisposition.Unknown,
            ResponsibilityState = ResponsibilityState.Pending,
            EvidenceSource = "arbitration:responsibility_advanced",
        };

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
            // [Batch B 续 会诊阻断处置] 盘上**任一冲突待决** ⇒ 执行事实未知（墓碑冲突不占主槽位，
            // 必须在此全局阻断新启动；不得只依赖可能漏接线的外部事实源）。
            if ((file.Handoff?.Operations ?? []).Any(o => o is not null && o.ConflictPending))
                return "facts_unknown";
            var pending = file.Handoff?.Pending;
            var bgiEpoch = _hooks.BgiEpochProvider();
            // ② 票据三要素（§5 / P55③）：无关候选不得占位；授权抢占方保留资格，但**须通过三要素校验**。
            //    **两个事实源各自独立校验、互不替代、不取或**（小节会诊整改：原 `facts.ActiveTicket ?? TicketOf(pending)`
            //    取值会让「注入票据与本地责任冲突」被绕过）：
            //    ②-a 注入的资格快照（BGI 侧票据权威面，生产尚未接线）；②-b 锁内已持久化的未决交接责任 `Handoff.Pending`
            //    （本地面：不新增读取、不破「锁内只消费本地事实」）。任一源**缺字段或冲突 ⇒ 保守阻断**，
            //    **绝不**降级为「无票据」放行。
            //    A6 原票据恢复豁免（P55⑥）：只免除「非授权抢占方身份」这一条压制，**不免除**要素校验与共同闸门。
            var authorizedRestore = IsAuthorizedTicketRestore(request, pending, targetEpoch);
            // ②-1 **双源一致性**（小节会诊必改）：两源**同时存在**时必须属于**同一票据关联**（三要素逐字全等），
            //      否则保守阻断——「恢复豁免」只免除「非授权抢占方身份」这一条压制，**不免除**两源关联一致性
            //      （否则「仅授权方不同」或「仅被挂起运行不同」的冲突会被豁免路径绕过）。
            if (facts.ActiveTicket is { } snapBoth && TicketOf(pending) is { } localBoth
                && !(string.Equals(snapBoth.SuspendedRunIdentity, localBoth.SuspendedRunIdentity, StringComparison.Ordinal)
                     && string.Equals(snapBoth.AuthorizedPreemptorIdentity, localBoth.AuthorizedPreemptorIdentity, StringComparison.Ordinal)
                     && string.Equals(snapBoth.Epoch, localBoth.Epoch, StringComparison.Ordinal)))
                return "ticket_conflict";
            // ②-0 同一被挂起运行的恢复但责任阶段未到 ⇒ 不得抢跑（不另建替代作业；也不该用「压制」词混淆责任存续）。
            if (pending is not null && IsResumeRequest(request) && !authorizedRestore
                && string.Equals(pending.SuspendedRunIdentity, request.RunBinding ?? "", StringComparison.Ordinal))
                return "pending_conflict";
            // ②-a BGI 侧票据快照（无条件校验；缺字段=非法票据，不得当「无票据」放行）。
            if (facts.ActiveTicket is { } snap)
            {
                if (string.IsNullOrEmpty(snap.AuthorizedPreemptorIdentity)
                    || string.IsNullOrEmpty(snap.Epoch)
                    || string.IsNullOrEmpty(snap.SuspendedRunIdentity))
                    return "ticket_malformed";
                if (!authorizedRestore && !string.Equals(snap.AuthorizedPreemptorIdentity, stableIdentity, StringComparison.Ordinal))
                    return "ticket_suppressed";
                if (!string.Equals(snap.Epoch, bgiEpoch, StringComparison.Ordinal))
                    return "stale_epoch"; // 票据 epoch ≠ 当前纪元 ⇒ 票据失效：失效 ≠ 清责 ≠ 立即放行
                if (IsResumeRequest(request) && !string.Equals(snap.SuspendedRunIdentity, request.RunBinding ?? "", StringComparison.Ordinal))
                    return "binding_conflict";
            }
            // ②-b 本地未决交接责任（无条件校验；与 ②-a 并列，不互相替代）。
            if (TicketOf(pending) is { } local)
            {
                if (string.IsNullOrEmpty(local.AuthorizedPreemptorIdentity)
                    || string.IsNullOrEmpty(local.Epoch)
                    || string.IsNullOrEmpty(local.SuspendedRunIdentity))
                    return "ticket_malformed";
                if (!authorizedRestore && !string.Equals(local.AuthorizedPreemptorIdentity, stableIdentity, StringComparison.Ordinal))
                    return "ticket_suppressed";
                if (!string.Equals(local.Epoch, bgiEpoch, StringComparison.Ordinal))
                    return "stale_epoch";
                if (IsResumeRequest(request) && !authorizedRestore
                    && !string.Equals(local.SuspendedRunIdentity, request.RunBinding ?? "", StringComparison.Ordinal))
                    return "binding_conflict";
            }
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
            //    SettlePending/RestorePending/ReconcilePending 阶段不得准入抢占方（恢复责任存续期禁止另建替代作业）；
            //    唯一例外＝已授权的**原票据恢复**（P55⑥，②处 authorizedRestore：该动作正是完成交接，不是替代作业）。
            if (pending is not null && !authorizedRestore)
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
            // [Batch B 收尾 会诊阻断处置] **持久化类型 fail-closed**：续用/重试（本回调是唯一签发发送许可处）
            // 一律以**持久化 `OperationType`** 为准——旧格式代隔离产物（`Unknown`）不得重新占位/发送。
            if (!Enum.IsDefined(op.OperationType) || op.OperationType == OperationType.Unknown)
                return "legacy_operation_type_unresolved";
            // [Batch B 续 会诊阻断处置] **冲突待决＝最高优先级阻断**：不得签发新发送轮次（禁止重发，
            // 重试资格在冲突期间一律失效——不得靠外部配置或调用方自觉）。
            if (op.ConflictPending) return "conflict_pending";
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

            // ⑪b 游标唯一消费（B7：同 runBinding+cursorRef+cursorRevision 不得被两个操作消费——锁内核验，防双跑）。
            // **不按 Zone 过滤**（会诊重要项）：消费记录经 TerminalPendingTransfer→Tombstone 迁区后**仍在盘上**，
            // 若只看 Active，则该游标可被再次消费（G8 让节点操作提前终局后尤甚）——消费保护不得随迁区消失。
            // [纠正·2026-09-21 会诊阻断] **必须带运行归属**：`cursorRef`（节点#出现#轮次）与 `cursorRevision`
            // （运行记录修订）都不是全局唯一——两个不同 run 完全可能同时是 `n-1#0#0`＋同一修订，
            // 缺 RunBinding 会把他们互判为「同一游标已消费」而误拒合法提交。加运行归属只会**减少误拒**，
            // 不削弱同 run 内「同一游标只消费一次」的约束。
            if (request.CursorRef is { } cursorRefToCheck
                && (file.Handoff.Operations ?? []).Any(other =>
                    !string.Equals(other.RequestIdentity, request.RequestIdentity, StringComparison.Ordinal)
                    && string.Equals(other.RunBinding, request.RunBinding, StringComparison.Ordinal)
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
            // §24.16-2（[终审会诊阻断处置]）：**预观察记录必须与发送许可占位同次原子发布**——
            // 否则「占位成功→发送→崩溃」窗口内没有可重建的观察依据（不可恢复发送窗口）。
            // 未持久化不得发送：门面只在本次发布成功后才调用 Sender。
            file.Handoff.PreObservations ??= [];
            if (!file.Handoff.PreObservations.Any(p => p is not null
                    && string.Equals(p.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                    && p.SendSeq == sendSeq))
                file.Handoff.PreObservations.Add(new PreObservationRecord
                {
                    SubmissionIdentity = submissionIdentity,
                    SendSeq = sendSeq,
                    OperationType = op.OperationType,
                    TargetEpoch = targetEpoch,
                    WireSubmitKey = op.WireSubmitKey,
                    // 替代查询依据（§24.7-2）：无句柄路径必须登记可查询依据；本批取线上提交键（节点/流程）或台账引用（外部启动）。
                    QueryBasis = op.OperationType == OperationType.ExternalStart
                        ? "external-start-ledger:" + submissionIdentity
                        // 无线上提交键的路径（流程登记等）以**操作记录引用**作为替代查询依据——
                        // 不得登记空依据（§24.7-2：无查询依据＝不得宣称接管可重建）。
                        : (string.IsNullOrEmpty(op.WireSubmitKey)
                            ? "operation-record:" + request.RequestIdentity
                            : "wire-submit-key:" + op.WireSubmitKey),
                    OwnerEpoch = lease.OwnerEpoch,
                    CreatedAtUtc = now,
                    State = "pending",
                });
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
            case "ticket_malformed":
                return (await TerminatePrecheckAsync(request, lease, "ticket_malformed", AdmissionResultKind.TerminalRejected, "票据缺必要要素（被挂起运行/授权抢占方/epoch）——不得当「无票据」放行（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
            case "ticket_conflict":
                return (await TerminatePrecheckAsync(request, lease, "ticket_conflict", AdmissionResultKind.TerminalRejected, "两个票据事实源（BGI 侧快照与本地未决责任）关联不一致——保守阻断（锁内复核）。").ConfigureAwait(false)) ?? ClassifyCurrentState(request.RequestIdentity);
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
            // [Batch B 续 会诊阻断处置] 冲突待决 ⇒ **禁止重发**：不得签发新发送轮次、不得据此终局/释放占用。
            case "conflict_pending":
                return new AdmissionResult
                {
                    Kind = AdmissionResultKind.NeedReconcile,
                    ReasonCode = "conflict_pending",
                    Detail = "存在待决冲突（禁止重发：必须先经权威裁决，冲突期间不签发新发送轮次）。",
                    RequestIdentity = request.RequestIdentity,
                    ExecutionDisposition = ExecutionDisposition.Unknown,
                    ResponsibilityState = ResponsibilityState.Pending,
                };
            // [Batch B 收尾] 持久化类型未知（旧格式代隔离产物）⇒ **不签发发送许可**（fail-closed，责任保留）。
            case "legacy_operation_type_unresolved":
            {
                // [第四轮验证会诊] 占位阶段出口同样必须携带**完整发送关联**（与前置拒绝口径一致）。
                var current = _store.Read();
                var currentOp = current.File is null ? null : FindOp(current.File, request.RequestIdentity);
                return new AdmissionResult
                {
                    Kind = AdmissionResultKind.Error,
                    ReasonCode = "legacy_operation_type_unresolved",
                    Detail = "持久化操作类型缺失/未知：不得重新占位或发送（需显式隔离/迁移处置）。",
                    RequestIdentity = request.RequestIdentity,
                    SubmissionIdentity = currentOp?.SubmissionIdentity,
                    SendSeq = currentOp?.LastSendSeq ?? 0,
                    ExecutionDisposition = ExecutionDisposition.Unknown,
                    ResponsibilityState = ResponsibilityState.Pending,
                };
            }
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
        // [第二轮验证会诊阻断处置] **冲突优先于历史拒绝分类**（冲突状态覆盖历史结果：不得把待决责任当已结清）。
        if (op.ConflictPending)
            return new AdmissionResult
            {
                Kind = AdmissionResultKind.NeedReconcile,
                ReasonCode = "conflict_pending",
                Detail = "存在待决冲突（待权威裁决：不释放占用、禁止重发）。",
                RequestIdentity = requestIdentity,
                SubmissionIdentity = op.SubmissionIdentity,
                SendSeq = op.LastSendSeq,
                ExecutionDisposition = ExecutionDisposition.Unknown,
                ResponsibilityState = ResponsibilityState.Pending,
            };
        return op.RequestState switch
        {
            // [第四轮验证会诊阻断处置] **续用/重启分类必须保持同一结果事实**（§24.6-4／§24.13-2）：
            // 已取得 `ExecutionResult` 者按其类别返回（取消/执行失败不得被改写成成功），责任维按是否结清区分。
            OperationRequestState.Accepted => ClassifyFromExecutionResult(requestIdentity, op, settled: false, "already_accepted", "已受理（返回既有结果）。"),
            OperationRequestState.TerminalCompleted => ClassifyFromExecutionResult(requestIdentity, op, settled: true, "already_terminal", "终局完成（返回既有结果）。"),
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
                    JobId = accepted.JobId, // §24.3-2：JobId 全链传递（不得在准入层丢弃已取得的句柄）
                    OperationType = op.OperationType, // §24.17：类型相关判定只按持久化类型（不得用 RunId 空值推断）
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
                        ? new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = "takeover_persist_failed", Detail = "接管台账持久化失败（" + persistFailure + "）——Submission 保持未决，保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, ExecutionDisposition = ExecutionDisposition.Unknown, ResponsibilityState = ResponsibilityState.Pending, JobId = accepted.JobId, EvidenceSource = accepted.EvidenceSource }
                        // [终审会诊阻断处置] 已登记且已签发发送责任 ⇒ 二次持久化失败**不得**回落 `ResponsibilityState.None`（那是「不适用」），
                        // 责任仍未结清 = `Pending`（`Unknown` 结果维）；只是未能把状态落到盘上。
                        : new AdmissionResult
                        {
                            Kind = AdmissionResultKind.Error,
                            ReasonCode = markPersist.Reason ?? "invalid_request",
                            Detail = "待对账落盘失败（不报告未持久化状态）。",
                            RequestIdentity = request.RequestIdentity,
                            SubmissionIdentity = submission.SubmissionIdentity,
                            SendSeq = submission.SendSeq,
                            ExecutionDisposition = ExecutionDisposition.Unknown,
                            ResponsibilityState = ResponsibilityState.Pending,
                            JobId = accepted.JobId,
                            EvidenceSource = accepted.EvidenceSource,
                        };
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
                    return new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = close.Reason ?? "close_failed", Detail = "Submission 关闭失败——台账已在册（重复接管幂等），保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, ExecutionDisposition = ExecutionDisposition.Unknown, ResponsibilityState = ResponsibilityState.Pending, JobId = accepted.JobId, EvidenceSource = accepted.EvidenceSource };

                // R5.3 §24.6-5：普通受理已关闭、完成层尚未报终态 ⇒ 责任 `Pending`（`Settled` 只在权威终态结算全部完成时给出）。
                return new AdmissionResult
                {
                    Kind = AdmissionResultKind.Accepted,
                    ReasonCode = "accepted",
                    Detail = "已受理（接管台账已持久化并可重建）。",
                    RequestIdentity = request.RequestIdentity,
                    SubmissionIdentity = submission.SubmissionIdentity,
                    SendSeq = submission.SendSeq,
                    Decision = null,
                    JobId = accepted.JobId,
                    ExecutionDisposition = ExecutionDisposition.None,
                    ResponsibilityState = ResponsibilityState.Pending,
                    EvidenceSource = accepted.EvidenceSource,
                };
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
                    return new AdmissionResult { Kind = AdmissionResultKind.Reconciling, ReasonCode = close.Reason ?? "close_failed", Detail = "拒绝关闭失败——保守待对账。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, ExecutionDisposition = ExecutionDisposition.Unknown, ResponsibilityState = ResponsibilityState.Pending, EvidenceSource = rejected.EvidenceSource };
                return canRetry
                    ? new AdmissionResult { Kind = AdmissionResultKind.RetryableRejected, ReasonCode = rejected.ReasonCode, Detail = "可重试拒绝（窗口内经 RetryAsync 重新 Admit）。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, ResponsibilityState = ResponsibilityState.Settled, EvidenceSource = rejected.EvidenceSource }
                    : new AdmissionResult { Kind = AdmissionResultKind.TerminalRejected, ReasonCode = rejected.ReasonCode, Detail = "终局拒绝。", RequestIdentity = request.RequestIdentity, SubmissionIdentity = submission.SubmissionIdentity, SendSeq = submission.SendSeq, ResponsibilityState = ResponsibilityState.Settled, EvidenceSource = rejected.EvidenceSource };
            }
            default:
            {
                // 未知→Submission.Reconciling（不换键重跑、不重发；持续停驻待对账——处置入口=SettleReconciledAsync）。
                var markUnknown = await MarkReconcilingAsync(request.RequestIdentity, lease, submission.SubmissionIdentity, submission.SendSeq).ConfigureAwait(false);
                return markUnknown.Success
                    ? new AdmissionResult
                    {
                        Kind = AdmissionResultKind.Reconciling,
                        ReasonCode = "send_unknown",
                        Detail = outcome is SendOutcome.Unknown u ? u.Detail : "发送结果未知。",
                        RequestIdentity = request.RequestIdentity,
                        SubmissionIdentity = submission.SubmissionIdentity,
                        SendSeq = submission.SendSeq,
                        // §24.6-2（[第三轮验证会诊重要项处置]）：未结清责任必须带责任维与证据来源——
                        // 不得在门面内部把「不可考」压成只有一个字符串。
                        ExecutionDisposition = ExecutionDisposition.Unknown,
                        ResponsibilityState = ResponsibilityState.Pending,
                        EvidenceSource = outcome is SendOutcome.Unknown u2 ? u2.EvidenceSource : null,
                    }
                    // [终审会诊阻断处置] 同上：已签发发送责任的二次持久化失败不得回落 `None`。
                    : new AdmissionResult
                    {
                        Kind = AdmissionResultKind.Error,
                        ReasonCode = markUnknown.Reason ?? "invalid_request",
                        Detail = "待对账落盘失败（不报告未持久化状态）。",
                        RequestIdentity = request.RequestIdentity,
                        SubmissionIdentity = submission.SubmissionIdentity,
                        SendSeq = submission.SendSeq,
                        ExecutionDisposition = ExecutionDisposition.Unknown,
                        ResponsibilityState = ResponsibilityState.Pending,
                        EvidenceSource = outcome is SendOutcome.Unknown u3 ? u3.EvidenceSource : null,
                    };
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
        // [Batch B 会诊阻断处置] 携带**完成层结果**的受理对账统一进入完成结算状态机（§24.3-4 三分支的唯一实现）：
        // 必须在取 `_gate` 之前转派（`SettleCompletionAsync` 自取门面锁；SemaphoreSlim 不可重入）。
        if (settlement is ReconcileSettlement.Accepted { Completion: not null } withCompletion)
            return await SettleCompletionAsync(requestIdentity, withCompletion.SubmissionIdentity, withCompletion.SendSeq,
                withCompletion.Completion,
                // [第二轮验证会诊阻断处置] 外层对账字段必须**完整携带**（否则受理接管会丢证据/运行绑定/句柄）。
                withCompletion.EvidenceSource, withCompletion.RunId, withCompletion.JobId).ConfigureAwait(false);
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
            // [Batch B 续 会诊阻断处置] 冲突待决记录**不得**走普通对账结清（含 NotAccepted）：必须经冲突裁决，
            // 否则会绕过审计/证据留痕并可能释放占用。
            if (op.ConflictPending)
                return LocatedStop(requestIdentity, op, "conflict_requires_adjudication",
                    "冲突待决记录必须经 AdjudicateConflictAsync 裁决（不得以普通对账关闭/结清）。");
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
                // §24.3-2：JobId 全链传递——显式对账取得的句柄同样不得在门面层丢弃。
                ReconcileSettlement.Accepted a => new SendOutcome.Accepted(a.EvidenceSource, a.RunId, a.JobId),
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
    /// **完成层结算入口**（R5.3 §24.3-4／§24.10-3／§24.15；[Batch B] 新增）：由当前所有者（或恢复扫描）在
    /// **权威完成结果到达**时调用。三个分支：
    /// - `completion = null` ⇒ 普通受理（不写终态载体；责任 `Pending`）；
    /// - `Unknown` ⇒ **不写** `PendingTerminal`（§24.19-2），保留观察依据与既有接管台账，对外 `NeedReconcile`；
    /// - `Succeeded`／`Cancelled`／`ExecutionFailed` ⇒ **唯一顺序**：①同次权威发布写 `ExecutionResult`＋`PendingTerminal`
    ///   → ②台账转 `Terminal`（仅外部启动；钩子缺失/失败＝保守停驻）→ ③关闭 Submission（若仍在册）→
    ///   ④同一边界内完成 Operation 终局与迁移（**不重入 `_gate`**，§24.1-5）。
    /// 纪律：只接受按**完整发送身份**（`submissionIdentity`＋`sendSeq`）匹配的完成结果（旧轮次证据不得结算新责任）；
    /// 任一步失败＝保守停驻（不重发、不释放占用），返回可对账原因。
    /// </summary>
    public async Task<AdmissionResult> SettleCompletionAsync(
        string requestIdentity, string submissionIdentity, int sendSeq, ExternalStartCompletion? completion,
        string? acceptanceEvidenceSource = null, string? acceptanceRunId = null, string? acceptanceJobId = null)
    {
        if (string.IsNullOrWhiteSpace(requestIdentity))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "请求身份必填。", requestIdentity);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            // §24.17／§24.1-6（[Batch B 会诊阻断处置]）：本入口**只服务外部启动**（E3/E4/E5）——
            // 其余类型走各自终局入口；`Unknown` fail-closed（不得借完成入口跳过台账步而释放占用）。
            if (op.OperationType != OperationType.ExternalStart)
                return LocatedStop(requestIdentity, op,
                    op.OperationType == OperationType.Unknown ? "legacy_operation_type_unresolved" : "operation_type_not_external_start",
                    op.OperationType == OperationType.Unknown
                        ? "操作类型未知（旧格式代隔离产物）——完成结算 fail-closed。"
                        : "完成结算入口只服务外部启动操作（其余类型走各自终局入口）。");
            // 完整发送身份关联：完成结果必须属于**本笔**发送轮次（旧轮次证据不得结算新责任）。
            if (string.IsNullOrEmpty(submissionIdentity)
                || !string.Equals(op.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                || op.LastSendSeq != sendSeq)
                return LocatedStop(requestIdentity, op, "stale_evidence",
                    "完成结果与当前发送轮次不关联（旧证据不得结算新责任，也不得替换本笔身份）。");
            // 完成层载荷**封闭校验**（[Batch B 会诊阻断处置]）：非法枚举/缺必填字段一律 fail-closed——
            // 必须**先于**任何重放/结算分支（否则非法载荷可借「已终局重放」路径绕过校验）。
            if (completion is not null)
            {
                if (!Enum.IsDefined(completion.Kind))
                    return LocatedStop(requestIdentity, op, "completion_invalid_kind", "完成层结果类别非法（枚举未定义）。");
                if (completion.Kind != ExternalStartCompletionKind.Unknown)
                {
                    if (string.IsNullOrWhiteSpace(completion.RawTerminal)
                        || string.IsNullOrWhiteSpace(completion.EvidenceSource)
                        || completion.ObservedAtUtc is null
                        || completion.ObservedAtUtc.Value == default)
                        return LocatedStop(requestIdentity, op, "completion_payload_incomplete",
                            "权威终态必须携带原始终态词／证据来源／观察时点（非默认值）。");
                    if (completion.Kind == ExternalStartCompletionKind.ExecutionFailed
                        && string.IsNullOrWhiteSpace(completion.ExecutionErrorCode))
                        return LocatedStop(requestIdentity, op, "completion_error_code_required",
                            "ExecutionFailed 必须携带 ExecutionErrorCode（与信封 errorCode 语义分离）。");
                }
            }
            // 已受理/待对账之外的状态不得走完成结算（未占位/终局/未获选各有各的判据）。
            // 已终局者按 §24.13-2 返回**既有终态事实**（幂等重放，不重复写盘、不改变责任状态）。
            if (op.RequestState == OperationRequestState.TerminalCompleted)
            {
                var existing = op.ExecutionResult;
                if (existing is null)
                    return AdmissionResult.Of(AdmissionResultKind.Error, "execution_result_missing",
                        "已终局但缺少 ExecutionResult（损坏：不得据终局状态推断成功）。", requestIdentity);
                // §24.13-3（[Batch B 会诊阻断处置]）：**冲突终态不得静默重放**——迟到/矛盾的终态事实必须进入冲突对账，
                // 不得把调用方的新事实当成既有事实的等价重放（既不是覆盖，也不是接受）。
                if (completion is not null && completion.Kind != ExternalStartCompletionKind.Unknown
                    && !CompletionMatchesExecutionResult(existing, completion, completion.ObservedAtUtc ?? default,
                        submissionIdentity, sendSeq))
                    return LocatedStop(requestIdentity, op, "terminal_conflict",
                        "既有权威终态与本次完成结果不一致（冲突对账：不覆盖、不重放、不释放占用）。", conflictPending: true);
                return new AdmissionResult
                {
                    Kind = existing.Kind switch
                    {
                        ExecutionResultKind.Cancelled => AdmissionResultKind.Cancelled,
                        ExecutionResultKind.Failed => AdmissionResultKind.ExecutionFailed,
                        _ => AdmissionResultKind.Accepted,
                    },
                    ReasonCode = "already_terminal",
                    Detail = "终局已完成（幂等重放既有事实，不重复结算）。",
                    RequestIdentity = requestIdentity,
                    SubmissionIdentity = submissionIdentity,
                    SendSeq = sendSeq,
                    JobId = existing.JobId,
                    ExecutionDisposition = existing.Kind switch
                    {
                        ExecutionResultKind.Cancelled => ExecutionDisposition.Cancelled,
                        ExecutionResultKind.Failed => ExecutionDisposition.ExecutionFailed,
                        ExecutionResultKind.Unknown => ExecutionDisposition.Unknown,
                        _ => ExecutionDisposition.None,
                    },
                    ResponsibilityState = ResponsibilityState.Settled,
                    RawTerminal = existing.RawTerminal,
                    ExecutionErrorCode = existing.ExecutionErrorCode,
                    EvidenceSource = existing.EvidenceSource,
                };
            }
            // [Batch B 续] 冲突裁决里的「曾受理」分支：既有拒绝（`TerminalRejected`/`RetryableRejected`/墓碑）之后
            // 才取得的权威终态同样必须经本入口结算——否则冲突修正无路可走（既有拒绝本体不改写，仅作审计）。
            var conflicted = op.ConflictPending;
            // [验证会诊阻断处置·第三轮] **冲突待决记录不得经普通完成结算**（§24.12-3④／§24.2-2″／§24.15 冲突行）：
            // 授权判据**不看调用方参数、只看本快照内持久化的裁决方向声明**——`ConflictAdjudicationClaim` 只能由
            // `ClaimAdjudicationAsync` 在裁决入口内写入（`ResolvedAcceptedTerminal`）。因此任何外部/新增调用者
            // （含恢复扫描、对账转派）都无法用参数伪造放行，只会得到保守停驻。
            if (conflicted && !string.Equals(
                    op.ConflictAdjudicationClaim, nameof(ConflictResolutionKind.ResolvedAcceptedTerminal),
                    StringComparison.Ordinal))
                return LocatedStop(requestIdentity, op, "conflict_requires_adjudication",
                    "冲突待决记录必须经裁决入口（先声明裁决方向）结算；不得走普通完成结算（保守停驻、责任保留）。");
            if (op.RequestState is not (OperationRequestState.Accepted or OperationRequestState.Reconciling) && !conflicted)
                return LocatedStop(requestIdentity, op, "not_accepted",
                    "操作不在可结算状态（完成结算仅对已受理/待对账操作）。");
            // 外部启动的固定目标纪元必须已持久化（不可改写；未知纪元不签发也不结算）。
            if (op.OperationType == OperationType.ExternalStart && string.IsNullOrEmpty(op.TargetEpoch))
                return LocatedStop(requestIdentity, op, "epoch_missing", "外部启动缺少固定目标纪元（不结算）。");

            // ── 分支一/二：无终态（普通受理）或结果未知 ──
            if (completion is null || completion.Kind == ExternalStartCompletionKind.Unknown)
            {
                // [Batch B 续 会诊阻断处置] 冲突待决记录**只能**经裁决携带权威终态完成结算：
                // 禁止走普通受理/未知分支（那里会假定未决 Submission 存在并误补受理）。
                if (conflicted)
                    return LocatedStop(requestIdentity, op, "conflict_requires_terminal_settlement",
                        "冲突待决记录只能以权威终态完成结算（禁止普通受理/未知分支）。");
                // 已有权威终态者**不得**被 null/Unknown 掩盖或降级（§24.6-2：事实不可反转）——按既有事实返回。
                if (op.ExecutionResult is { } existingFact)
                    return ReplayExistingFact(requestIdentity, op, existingFact);
                var plain = completion is null;
                // 仍未关闭（Reconciling/Granted/Sending）⇒ 先完成**普通受理**：接管台账落盘→关闭 Submission→Accepted。
                if (op.RequestState != OperationRequestState.Accepted)
                {
                    var acceptance = await AcceptOrdinaryAsync(op, lease, read.File,
                        acceptanceEvidenceSource, acceptanceRunId, acceptanceJobId).ConfigureAwait(false);
                    if (acceptance is not null) return acceptance;
                }
                return new AdmissionResult
                {
                    Kind = plain ? AdmissionResultKind.Accepted : AdmissionResultKind.NeedReconcile,
                    ReasonCode = plain ? "accepted_no_terminal" : "completion_unknown",
                    Detail = plain
                        ? "已受理（尚无权威终态：保持 Accepted/Active，台账不转 Terminal）。"
                        : "完成层结果未知（保留观察依据与接管台账，**不写** PendingTerminal，保守待对账）。",
                    RequestIdentity = requestIdentity,
                    SubmissionIdentity = submissionIdentity,
                    SendSeq = sendSeq,
                    // §24.6-2（[Batch B 收尾之三] 修正）：**未知分支同样必须携带已取得的 JobId**——
                    // 受理时拿到的句柄是调用方对账的唯一依据，不得在门面/适配器边界断链
                    // （`completion` 为 `Unknown` 时其 JobId 必空，故取受理/对账携带的 `acceptanceJobId`）。
                    // [验证会诊阻断处置] 恢复/换主等**未随调用方携带** `acceptanceJobId` 的路径，
                    // 仍须从已接管台账读回句柄（§24.7-1：记录合并不等于远端关联可重建，故只在**可确认**时补齐）。
                    JobId = completion?.JobId ?? acceptanceJobId ?? op.ExecutionResult?.JobId
                        ?? (_hooks.TakeoverJobIdRead?.Invoke(submissionIdentity, sendSeq) is
                            { State: LedgerHandleState.Present, JobId: { Length: > 0 } readHandle }
                            ? readHandle
                            : null),
                    ExecutionDisposition = plain ? ExecutionDisposition.None : ExecutionDisposition.Unknown,
                    ResponsibilityState = ResponsibilityState.Pending,
                    RawTerminal = completion?.RawTerminal,
                    ExecutionErrorCode = completion?.ExecutionErrorCode,
                    EvidenceSource = completion?.EvidenceSource,
                };
            }

            var observedAt = completion.ObservedAtUtc!.Value;

            var now = _utcNow();
            var resultKind = completion.Kind switch
            {
                ExternalStartCompletionKind.Succeeded => ExecutionResultKind.Succeeded,
                ExternalStartCompletionKind.Cancelled => ExecutionResultKind.Cancelled,
                _ => ExecutionResultKind.Failed,
            };

            // [第三轮验证会诊阻断处置] 终态先到且发送未关闭时，**唯一顺序**为：
            // 接管事实落盘（**只落接管，不关闭**）→ ExecutionResult＋PendingTerminal → 台账 Terminal → 关闭 Submission → Operation 终局。
            // 关闭由下方 finalize 完成（不得在此提前关闭，否则违反 §24.15 顺序）。
            if (op.RequestState != OperationRequestState.Accepted)
            {
                var takeover = await PersistAcceptanceTakeoverAsync(op, lease,
                    acceptanceEvidenceSource ?? completion.EvidenceSource, acceptanceRunId,
                    acceptanceJobId ?? completion.JobId).ConfigureAwait(false);
                if (takeover is not null) return takeover;
            }
            // [第四轮验证会诊阻断处置] 句柄必须取**台账合并后的权威值**（含正常受理时已登记的句柄），
            // 否则「台账有句柄、两载体为 null」的分裂会被 MarkTerminal 的「一侧为空＝补齐」静默吸收。
            // [第六轮验证会诊阻断处置] 空串同样是「空句柄」：先规范化再按优先级合并（`??` 会让上游空串截断下游有效句柄）。
            // [第八轮验证会诊阻断处置] 台账读回为**三态**：`Unreadable`（读取失败/损坏/不可确认）⇒ 保守停驻，
            // **不得**当作「无句柄」继续（否则读取故障会被当成权威事实而错误释放占用）。
            // 未配置读取器＝**不可确认**（等同 `Unreadable`）：不得把「读不到」折成「无句柄」而放行终局。
            var handleProbe = _hooks.TakeoverJobIdRead?.Invoke(submissionIdentity, sendSeq) ?? LedgerHandleProbe.Unreadable();
            if (handleProbe.State == LedgerHandleState.Unreadable)
                return SettleStop(requestIdentity, submissionIdentity, sendSeq, "terminal_ledger_unreadable",
                    completion, "接管台账不可读/不可确认（不得把读取失败当作无句柄：保守停驻、责任保留）。",
                    acceptanceJobId ?? completion.JobId ?? op.ExecutionResult?.JobId);
            var ledgerHandle = handleProbe is { State: LedgerHandleState.Present } ? handleProbe.JobId : null;
            // [Batch B 收尾之三 验证会诊阻断处置] §24.3-3：**多个非空且不相同**的句柄＝冲突，
            // 必须保守待对账（不得按优先级静默挑一个——那会把冲突句柄写成权威事实）。
            if (!string.IsNullOrEmpty(acceptanceJobId) && !string.IsNullOrEmpty(completion.JobId)
                && !string.Equals(acceptanceJobId, completion.JobId, StringComparison.Ordinal))
                return SettleStop(requestIdentity, submissionIdentity, sendSeq, "terminal_job_id_conflict",
                    completion, "受理句柄与完成层句柄不一致（不得静默择一：保守待对账、责任保留）。",
                    acceptanceJobId);
            var effectiveJobId = new[] { acceptanceJobId, completion.JobId, op.ExecutionResult?.JobId, ledgerHandle }
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));

            // ① 同次权威发布写 ExecutionResult ＋ PendingTerminal（§24.12-7 合法中间态：责任尚未结算）。
            var stageResult = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op2 = FindOp(file, requestIdentity);
                if (op2 is null) return "stale_operation_identity";
                // 冲突裁决允许在**已迁墓碑/待迁移**的记录上补写权威终态载体（既有拒绝/墓碑事实不改写）。
                if (!op2.ConflictPending && op2.Zone != OperationZone.Active) return "state_changed";
                if (!string.Equals(op2.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                    || op2.LastSendSeq != sendSeq)
                    return "state_changed";
                // [Batch B 会诊阻断处置] **已写终态事实不可改写**：重复结算必须逐字段等值（幂等续跑），
                // 不等值＝冲突/损坏，fail-closed（不得用新终态覆盖旧终态，也不得形成新 ExecutionResult＋旧 PendingTerminal）。
                if (op2.ExecutionResult is { } priorResult)
                {
                    if (!CompletionMatchesExecutionResult(priorResult, completion, observedAt, submissionIdentity, sendSeq))
                        return "terminal_conflict";
                    // [第五轮验证会诊阻断处置] **空句柄按 §24.3-3 补齐**（不得让既有载体长期为 null 而与台账分裂）。
                    if (string.IsNullOrEmpty(priorResult.JobId) && !string.IsNullOrEmpty(effectiveJobId))
                        priorResult.JobId = effectiveJobId;
                }
                else
                {
                    op2.ExecutionResult = new ExecutionResult
                    {
                        Kind = resultKind,
                        RawTerminal = completion.RawTerminal ?? "",
                        ExecutionErrorCode = completion.ExecutionErrorCode,
                        JobId = effectiveJobId,
                        EvidenceSource = completion.EvidenceSource ?? "",
                        SubmissionIdentity = submissionIdentity,
                        SendSeq = sendSeq,
                        ObservedAtUtc = observedAt,
                    };
                }
                if (op2.PendingTerminal is { } priorPending)
                {
                    if (!CompletionMatchesPendingTerminal(priorPending, completion, observedAt, submissionIdentity, sendSeq))
                        return "terminal_conflict";
                    if (string.IsNullOrEmpty(priorPending.JobId) && !string.IsNullOrEmpty(effectiveJobId))
                        priorPending.JobId = effectiveJobId;
                }
                else
                {
                    op2.PendingTerminal = new PendingTerminal
                    {
                        Kind = resultKind, // [第三轮验证会诊] 类别与 ExecutionResult 一致（四类事实一致性判据使用）
                        RawTerminal = completion.RawTerminal ?? "",
                        ExecutionErrorCode = completion.ExecutionErrorCode,
                        JobId = effectiveJobId,
                        EvidenceSource = completion.EvidenceSource ?? "",
                        SubmissionIdentity = submissionIdentity,
                        SendSeq = sendSeq,
                        OperationType = op2.OperationType,
                        ObservedAtUtc = observedAt,
                        RecordedAtUtc = now,
                        LocalCancelRequested = op2.LocalCancelRequested,
                    };
                }
                op2.UpdatedRevision = file.Revision + 1;
                op2.UpdatedAtUtc = now;
                return null;
            });
            if (!stageResult.Success)
            {
                // §24.15 读回验证（[Batch B 会诊阻断处置]）：发布结果不明时先读回——载体**实际已提交且与本次终态等值**
                // ⇒ 视为提交成功（继续后续步骤）；确未提交/值不符才保守停驻。
                var readBack = _store.Read();
                var opBack = readBack.File is null ? null : FindOp(readBack.File, requestIdentity);
                // 读回必须**逐字段等值且两载体齐备**（否则冲突载荷可被误判为「本次已提交」）。
                var committed = opBack?.ExecutionResult is { } rb
                    && CompletionMatchesExecutionResult(rb, completion, observedAt, submissionIdentity, sendSeq)
                    && opBack.PendingTerminal is { } rp
                    && CompletionMatchesPendingTerminal(rp, completion, observedAt, submissionIdentity, sendSeq);
                if (!committed)
                    return SettleStop(requestIdentity, submissionIdentity, sendSeq, "terminal_persist_failed:" + (stageResult.Reason ?? "invalid_request"),
                        completion, "终态载体落盘失败或与既有终态冲突（读回确认未提交：保守停驻，不覆盖、不重发）。",
                        effectiveJobId);
            }

            // ② 台账 Terminal（仅外部启动；钩子缺失/失败＝保守停驻，§24.3-5）。
            if (op.OperationType == OperationType.ExternalStart)
            {
                var persistTerminal = _hooks.TakeoverTerminalPersist;
                if (persistTerminal is null)
                    return SettleStop(requestIdentity, submissionIdentity, sendSeq, "terminal_persist_not_configured",
                        completion, "终态回写钩子缺失（接管台账未确认权威终态：保守停驻）。", effectiveJobId);
                string? persistReason;
                try
                {
                    persistReason = persistTerminal(submissionIdentity, sendSeq,
                        completion.RawTerminal ?? "", observedAt,
                        completion.RawTerminal, completion.ExecutionErrorCode, effectiveJobId, completion.EvidenceSource);
                }
                catch (Exception ex)
                {
                    persistReason = "terminal_persist_exception:" + ex.GetType().Name;
                }
                // §24.15 读回验证（[第四轮验证会诊阻断处置]）：**无论回写返回成功或失败**，都必须以
                // 「台账终态**逐字段等值**读回」作为继续关闭/终局的前置条件（仅 API 返回成功不足）。
                var actuallyTerminal = _hooks.TakeoverTerminalPayloadConfirmed?.Invoke(
                    submissionIdentity, sendSeq, completion.RawTerminal, completion.ExecutionErrorCode,
                    effectiveJobId, completion.EvidenceSource, observedAt) == true;
                if (!actuallyTerminal)
                        return SettleStop(requestIdentity, submissionIdentity, sendSeq,
                            "terminal_persist_unconfirmed:" + (persistReason ?? "payload_mismatch"),
                            completion, "接管台账终态未按本次载荷逐字段确认（保守停驻：责任保留、不释放占用、不重发）。",
                            effectiveJobId);
            }

            // ③ 关闭 Submission（若仍在册）＋ ④ 同一边界内完成 Operation 终局与迁移（不重入 `_gate`）。
            var finalize = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op3 = FindOp(file, requestIdentity);
                if (op3 is null) return "state_changed";
                // 冲突裁决：墓碑/待迁移记录同样允许补终局（终局后仍在墓碑区，受保护不被裁剪）。
                if (!op3.ConflictPending && op3.Zone != OperationZone.Active) return "state_changed";
                if (!string.Equals(op3.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                    || op3.LastSendSeq != sendSeq)
                    return "state_changed";
                // [第二轮验证会诊阻断处置] 释放占用前必须复核**四类事实一致**：本次完成结果 ↔ ExecutionResult ↔ PendingTerminal。
                if (op3.ExecutionResult is not { } ready
                    || !CompletionMatchesExecutionResult(ready, completion, observedAt, submissionIdentity, sendSeq)
                    || op3.PendingTerminal is not { } readyPending
                    || !CompletionMatchesPendingTerminal(readyPending, completion, observedAt, submissionIdentity, sendSeq))
                    return "terminal_facts_inconsistent";
                // [第四轮验证会诊阻断处置] 两载体**互相一致**（含句柄等值）且绑定本 Operation 的完整发送身份——
                // 完成层缺句柄时，上面两个判据会把句柄当通配，故必须再由本判据强制。
                if (!TerminalFactsConsistent(op3)) return "terminal_facts_inconsistent";
                // [第五轮验证会诊阻断处置] 两载体句柄必须等于**合并后的权威句柄**（不得仅彼此相等而与台账分裂）。
                if (!string.IsNullOrEmpty(effectiveJobId)
                    && (!string.Equals(ready.JobId, effectiveJobId, StringComparison.Ordinal)
                        || !string.Equals(readyPending.JobId, effectiveJobId, StringComparison.Ordinal)))
                    return "terminal_job_id_mismatch";
                // [第六轮验证会诊阻断处置] **台账句柄 TOCTOU**：提交事务内重读台账句柄——若台账此时已有句柄
                // 而两载体仍为空（并发对账/接管刚刚补入），本轮**不得**终局（先补齐，下一轮一致后再释放占用）。
                // 说明：本钩子只读**台账文件**（不读租约），故在租约变更回调内调用不构成自锁。
                // 未配置读取器同样视为**不可确认**（fail-closed；不得因缺配置而释放占用）。
                var probeNow = _hooks.TakeoverJobIdRead?.Invoke(submissionIdentity, sendSeq) ?? LedgerHandleProbe.Unreadable();
                // 三态：读取失败/不可确认 ⇒ 本轮**不得**终局（防止把读取故障当成「台账无句柄」而释放占用）。
                if (probeNow.State == LedgerHandleState.Unreadable) return "terminal_ledger_unreadable";
                if (probeNow is { State: LedgerHandleState.Present, JobId: { Length: > 0 } handleNow }
                    && (string.IsNullOrEmpty(ready.JobId) || string.IsNullOrEmpty(readyPending.JobId)))
                    return "terminal_job_id_backfill_required";
                var stillOpen = file.Handoff?.Submission;
                if (stillOpen is not null)
                {
                    if (!string.Equals(stillOpen.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                        || stillOpen.SendSeq != sendSeq)
                        return "submission_identity_mismatch"; // 未决发送属于别的轮次：不越权关闭
                    file.Handoff!.Submission = null;
                    var pre = (file.Handoff.PreObservations ?? []).FirstOrDefault(p => p is not null
                        && string.Equals(p.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                        && p.SendSeq == sendSeq);
                    if (pre is not null) pre.State = "completed";
                }
                op3.RequestState = OperationRequestState.TerminalCompleted;
                // [第二轮验证会诊阻断处置] **到达时所在区域冻结**：冲突到达时已在墓碑的记录终局后**仍留在墓碑**
                // （不得重新申请主槽位）；其余（Active）才迁入待迁移区。
                op3.Zone = op3.Zone == OperationZone.Tombstone
                    ? OperationZone.Tombstone
                    : OperationZone.TerminalPendingTransfer;
                op3.PendingTerminal = null; // 责任已结清：终态事实由 ExecutionResult 长期承载（§24.12-7）
                op3.UpdatedRevision = file.Revision + 1;
                op3.UpdatedAtUtc = now;
                MigrateAndClean(file, now); // 主槽位随迁移释放
                return null;
            });
            if (!finalize.Success)
            {
                // §24.15 读回验证（[Batch B 会诊阻断处置]）：发布结果不明时先读回——已终局且发送已关闭 ⇒ 按成功返回；
                // 否则保守停驻（保持 Accepted＋PendingTerminal，不重建 Submission、不重发）。
                var after = _store.Read();
                var opAfter = after.File is null ? null : FindOp(after.File, requestIdentity);
                var closedAfter = after.File?.Handoff?.Submission is not { } stillOpen2
                    || !string.Equals(stillOpen2.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal);
                // [第三轮验证会诊] 读回必须**逐字段等值**（并发写入的不同终态不得被当成本次提交成功）。
                var committedAfter = opAfter?.RequestState == OperationRequestState.TerminalCompleted
                    && closedAfter
                    && opAfter.ExecutionResult is { } rb2
                    && CompletionMatchesExecutionResult(rb2, completion, observedAt, submissionIdentity, sendSeq);
                if (!committedAfter)
                {
                    var reason = finalize.Reason ?? "invalid_request";
                    // [第七轮验证会诊] 特定停驻原因**原样透传**：`terminal_job_id_backfill_required` 是合同规定的
                    // 可观察停驻码，不得被 `finalize_failed:` 包装改变调用方可判定的事实。
                    // `terminal_ledger_unreadable` 同样是合同规定的可观察停驻码（读取失败不得被包装掩盖）。
                    var reasonCode = reason is "terminal_job_id_backfill_required" or "terminal_ledger_unreadable"
                        ? reason
                        : "finalize_failed:" + reason;
                    return SettleStop(requestIdentity, submissionIdentity, sendSeq, reasonCode,
                        completion, "关闭/终局失败（读回确认未提交：保持 Accepted＋PendingTerminal 待对账，不重建 Submission）。",
                        effectiveJobId);
                }
            }

            return new AdmissionResult
            {
                Kind = completion.Kind switch
                {
                    ExternalStartCompletionKind.Succeeded => AdmissionResultKind.Accepted,
                    ExternalStartCompletionKind.Cancelled => AdmissionResultKind.Cancelled,
                    _ => AdmissionResultKind.ExecutionFailed,
                },
                ReasonCode = completion.Kind switch
                {
                    ExternalStartCompletionKind.Succeeded => "terminal_completed",
                    ExternalStartCompletionKind.Cancelled => "cancelled",
                    _ => "execution_failed",
                },
                Detail = "权威终态已结算（台账 Terminal＋Submission 关闭＋Operation 终局）。",
                RequestIdentity = requestIdentity,
                SubmissionIdentity = submissionIdentity,
                SendSeq = sendSeq,
                JobId = effectiveJobId,
                ExecutionDisposition = completion.Kind switch
                {
                    ExternalStartCompletionKind.Cancelled => ExecutionDisposition.Cancelled,
                    ExternalStartCompletionKind.ExecutionFailed => ExecutionDisposition.ExecutionFailed,
                    _ => ExecutionDisposition.None,
                },
                ResponsibilityState = ResponsibilityState.Settled,
                RawTerminal = completion.RawTerminal,
                ExecutionErrorCode = completion.ExecutionErrorCode,
                EvidenceSource = completion.EvidenceSource,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// **冲突证据登记**（R5.3 §24.2-2″；[Batch B 续] 新增）：发送层「确定未受理」之后又出现指向「曾受理/终态」的
    /// 证据时，证据必须**原样追加**并置「冲突待决」——不覆盖既有事实、不释放占用、**禁止重发**（重试资格立即失效）。
    /// 返回 `NeedReconcile`（责任 `Pending`）。
    /// </summary>
    public async Task<AdmissionResult> RegisterConflictEvidenceAsync(
        string requestIdentity, ConflictEvidenceRecord evidence)
    {
        if (string.IsNullOrWhiteSpace(requestIdentity) || evidence is null)
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "冲突登记参数缺失。", requestIdentity);
        // [Batch B 续 会诊阻断处置] 入口**严格限定**：仅外部启动、仅「本笔当前轮已确定拒绝」之后、且证据字段完整。
        if (string.IsNullOrWhiteSpace(evidence.EvidenceId) || string.IsNullOrWhiteSpace(evidence.RawTerminal)
            || string.IsNullOrWhiteSpace(evidence.EvidenceSource) || evidence.ObservedAtUtc == default
            || evidence.SendSeq < 1 || string.IsNullOrWhiteSpace(evidence.SubmissionIdentity))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request",
                "冲突证据字段不完整（需 evidenceId/原始终态词/来源/观察时点/完整发送身份）。", requestIdentity);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            if (op.OperationType != OperationType.ExternalStart)
                return LocatedStop(requestIdentity, op,
                    op.OperationType == OperationType.Unknown ? "legacy_operation_type_unresolved" : "operation_type_not_external_start",
                    "冲突登记只服务外部启动操作（其余类型走各自责任链）。");
            if (!string.Equals(op.SubmissionIdentity, evidence.SubmissionIdentity, StringComparison.Ordinal)
                || op.LastSendSeq != evidence.SendSeq)
                return LocatedStop(requestIdentity, op, "stale_evidence", "冲突证据与本笔发送轮次不关联（不得据旧轮次证据改动本笔责任）。");
            if (op.LastResult is not { Outcome: OperationOutcome.Rejected } rejected
                || rejected.AnsweredSendSeq != op.LastSendSeq)
                return LocatedStop(requestIdentity, op, "conflict_requires_current_rejection",
                    "冲突登记要求「本笔当前轮」确为确定拒绝（不得对未拒绝/旧轮结论登记冲突）。");
            var now = _utcNow();
            var record = new ConflictEvidenceRecord
            {
                EvidenceId = evidence.EvidenceId,
                RawTerminal = evidence.RawTerminal,
                ExecutionErrorCode = evidence.ExecutionErrorCode,
                EvidenceSource = evidence.EvidenceSource,
                ObservedAtUtc = evidence.ObservedAtUtc,
                SubmissionIdentity = evidence.SubmissionIdentity,
                SendSeq = evidence.SendSeq,
                SupersededRawTerminal = rejected.ReasonCode,
                SupersededReasonCode = rejected.ReasonCode,
            };
            var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op2 = FindOp(file, requestIdentity);
                if (op2 is null) return "state_changed";
                if (!string.Equals(op2.SubmissionIdentity, evidence.SubmissionIdentity, StringComparison.Ordinal)
                    || op2.LastSendSeq != evidence.SendSeq)
                    return "state_changed";
                // 追加式且**按 evidenceId 幂等**：同 ID 同载荷＝成功；同 ID 异载荷＝冲突（fail-closed，不覆盖）。
                var existing = (op2.ConflictEvidence ??= []).FirstOrDefault(e => e is not null
                    && string.Equals(e.EvidenceId, record.EvidenceId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (!string.Equals(existing.RawTerminal, record.RawTerminal, StringComparison.Ordinal)
                        || !string.Equals(existing.ExecutionErrorCode, record.ExecutionErrorCode, StringComparison.Ordinal)
                        || !string.Equals(existing.EvidenceSource, record.EvidenceSource, StringComparison.Ordinal)
                        || existing.ObservedAtUtc != record.ObservedAtUtc)
                        return "conflict_evidence_conflict";
                }
                else
                {
                    op2.ConflictEvidence.Add(record);
                }
                op2.ConflictPending = true;
                op2.UpdatedRevision = file.Revision + 1;
                op2.UpdatedAtUtc = now;
                return null;
            });
            if (!mutate.Success)
            {
                var registerReason = mutate.Reason ?? "invalid_request";
                // [第二轮验证会诊] 约定错误码**原样透传**（不得被 `conflict_register_failed:` 包装改变可观察合同）。
                if (registerReason == "conflict_evidence_conflict")
                    return LocatedStop(requestIdentity, op, registerReason,
                        "同一 evidenceId 的载荷与既有记录不一致（冲突/损坏：不覆盖，保守停驻）。");
                return LocatedStop(requestIdentity, op, "conflict_register_failed:" + registerReason,
                    "冲突证据登记失败（保守停驻：不得据此释放占用或重发）。");
            }
            return new AdmissionResult
            {
                Kind = AdmissionResultKind.NeedReconcile,
                ReasonCode = "conflict_registered",
                Detail = "冲突证据已登记（待权威裁决：不覆盖既有事实、不释放占用、禁止重发）。",
                RequestIdentity = requestIdentity,
                SubmissionIdentity = evidence.SubmissionIdentity,
                SendSeq = evidence.SendSeq,
                ExecutionDisposition = ExecutionDisposition.Unknown,
                ResponsibilityState = ResponsibilityState.Pending,
                RawTerminal = evidence.RawTerminal,
                ExecutionErrorCode = evidence.ExecutionErrorCode,
                EvidenceSource = evidence.EvidenceSource,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// **冲突裁决**（R5.3 §24.2-2″；[Batch B 续] 新增）：
    /// `ResolvedAcceptedTerminal` ⇒ 先按 §24.15 完成终态链（复用完成结算入口），再以**一次租约原子发布**完成三项
    /// （追加审计项 ＋ 写 `ConflictResolutionAuditId` ＋ 清 `conflict.pending`）；
    /// `ResolvedNotAccepted` ⇒ 以**一次租约原子发布**完成四项（追加 `ReconciledNotAcceptedEvidence` ＋ 审计项 ＋ 写引用 ＋ 清 `pending`）。
    /// 幂等：`auditId`／`evidenceId` 由完整发送身份确定性派生（重试复用同一 ID；同 ID 同载荷＝幂等，同 ID 异载荷＝损坏）。
    /// 「清冲突」只清活动覆盖层 `pending`——审计、证据记录与拒绝快照**一律保留**。
    /// </summary>
    public async Task<AdmissionResult> AdjudicateConflictAsync(
        string requestIdentity, ConflictResolutionKind resolution, string evidenceSource,
        ExternalStartCompletion? terminalEvidence = null, NotAcceptedObservation? notAccepted = null)
    {
        if (string.IsNullOrWhiteSpace(requestIdentity) || string.IsNullOrWhiteSpace(evidenceSource))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "裁决参数缺失。", requestIdentity);
        if (!Enum.IsDefined(resolution))
            return AdmissionResult.Of(AdmissionResultKind.Error, "invalid_request", "裁决类型非法。", requestIdentity);
        // [Batch B 续 会诊阻断处置] 未受理裁决必须携带**权威观察**（白名单事实类型/原始词/来源/时点/完整身份），
        // 不得现场构造（否则任意非空来源即可解除冲突阻断＝fail-open）。
        if (resolution == ConflictResolutionKind.ResolvedNotAccepted)
        {
            if (notAccepted is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "observation_required",
                    "「确认未受理」裁决必须携带权威未受理观察（不得现场构造证据）。", requestIdentity);
            if (!ReconciledNotAcceptedFactKinds.All.Contains(notAccepted.FactKind))
                return AdmissionResult.Of(AdmissionResultKind.Error, "observation_invalid_fact_kind",
                    "未受理观察的事实类型不在封闭白名单内。", requestIdentity);
            if (string.IsNullOrWhiteSpace(notAccepted.RawEvidenceWord) || string.IsNullOrWhiteSpace(notAccepted.EvidenceSource)
                || notAccepted.ObservedAtUtc == default || notAccepted.SendSeq < 1
                || string.IsNullOrWhiteSpace(notAccepted.SubmissionIdentity))
                return AdmissionResult.Of(AdmissionResultKind.Error, "observation_incomplete",
                    "未受理观察字段不完整（原始词/来源/观察时点/完整发送身份）。", requestIdentity);
            // 可信来源校验（fail-closed：未配置校验器＝不得裁决；校验失败＝拒绝并保留冲突待决）。
            var verifier = _hooks.NotAcceptedObservationVerifier;
            if (verifier is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "observation_source_unverified",
                    "未配置权威未受理观察的可信性校验器（不得据未验证来源裁决）。", requestIdentity);
            var verificationFailure = verifier(notAccepted);
            if (verificationFailure is not null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "observation_source_untrusted:" + verificationFailure,
                    "权威未受理观察未通过可信性校验（不得解除冲突阻断）。", requestIdentity);
        }

        string submissionIdentity;
        int sendSeq;
        {
            var read0 = _store.Read();
            var op0 = read0.File is null ? null : FindOp(read0.File, requestIdentity);
            if (op0 is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            submissionIdentity = op0.SubmissionIdentity;
            sendSeq = op0.LastSendSeq;
            if (!op0.ConflictPending)
            {
                // [Batch B 续] **幂等重放**：已裁决且审计在册（同 resolution／同来源）⇒ 返回既有结论，不重复写盘。
                if (op0.ConflictResolutionAuditId is { Length: > 0 } settledAuditId)
                {
                    var handoff0 = read0.File!.Handoff;
                    var settledAudit = (handoff0?.ConflictResolutionAudits ?? [])
                        .FirstOrDefault(a => a is not null && string.Equals(a.AuditId, settledAuditId, StringComparison.Ordinal));
                    // [第三轮验证会诊阻断处置] 幂等重放同样必须**全载荷**比较（不得只看来源）。
                    var samePayload = settledAudit is not null && (resolution == ConflictResolutionKind.ResolvedAcceptedTerminal
                        ? terminalEvidence is not null && AuditMatchesCompletion(settledAudit, terminalEvidence, op0.ExecutionResult, submissionIdentity, sendSeq)
                        : notAccepted is not null && AuditMatchesNotAccepted(settledAudit,
                            handoff0?.ReconciledNotAcceptedEvidence ?? [], notAccepted, submissionIdentity, sendSeq));
                    if (settledAudit is not null && settledAudit.Resolution != resolution)
                        return LocatedStop(requestIdentity, op0, "conflict_audit_conflict",
                            "既有审计的裁决方向与本请求不一致（冲突：不追加、不改引用）。");
                    if (settledAudit is not null && !samePayload)
                        return LocatedStop(requestIdentity, op0, "conflict_audit_conflict",
                            "既有审计载荷与本请求不一致（冲突：不追加、不改引用）。");
                    if (settledAudit is not null && settledAudit.Resolution == resolution && samePayload)
                        return new AdmissionResult
                        {
                            // 结果维以**已持久化 `ExecutionResult` 为权威**（不得把取消/失败压成 Accepted）。
                            Kind = resolution == ConflictResolutionKind.ResolvedNotAccepted
                                ? AdmissionResultKind.TerminalRejected
                                : MapResultKindFromExecution(op0.ExecutionResult?.Kind),
                            ReasonCode = "already_adjudicated",
                            Detail = "冲突已裁决（幂等重放既有审计事实，不重复写盘）。",
                            RequestIdentity = requestIdentity,
                            SubmissionIdentity = op0.SubmissionIdentity,
                            SendSeq = op0.LastSendSeq,
                            JobId = op0.ExecutionResult?.JobId,
                            ResponsibilityState = ResponsibilityState.Settled,
                            ExecutionDisposition = MapDispositionFromExecution(op0.ExecutionResult?.Kind),
                            RawTerminal = op0.ExecutionResult?.RawTerminal,
                            ExecutionErrorCode = op0.ExecutionResult?.ExecutionErrorCode,
                            EvidenceSource = evidenceSource,
                        };
                }
                return LocatedStop(requestIdentity, op0, "no_pending_conflict", "该操作没有待决冲突（不得据无冲突记录补审计）。");
            }
            if (string.IsNullOrEmpty(submissionIdentity) || sendSeq < 1)
                return LocatedStop(requestIdentity, op0, "identity_missing", "缺少完整发送身份（不得裁决）。");
            // 未受理观察必须与本笔完整发送身份全等（不得据他人/旧轮观察裁决）。
            if (notAccepted is not null
                && (!string.Equals(notAccepted.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
                    || notAccepted.SendSeq != sendSeq))
                return LocatedStop(requestIdentity, op0, "observation_identity_mismatch",
                    "未受理观察的发送身份与本笔不一致（拒绝关联）。");
        }

        // 阶段一（不持 `_gate`）：受理终态分支先按 §24.15 完成终态链，未结清则原样返回（不写审计）。
        if (resolution == ConflictResolutionKind.ResolvedAcceptedTerminal)
        {
            if (terminalEvidence is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "completion_required",
                    "「确认曾受理」裁决必须携带权威终态证据（不得仅凭类型/印象裁决）。", requestIdentity);
            // [第三轮验证会诊阻断处置] **先原子声明裁决方向**：受理终态分支跨越「终态链 + 审计」两个发布，
            // 中间窗口不得被相反方向裁决穿插（否则会出现 ExecutionResult 终态与 ResolvedNotAccepted 审计并存的矛盾状态）。
            var claimResult = await ClaimAdjudicationAsync(requestIdentity, submissionIdentity, sendSeq, resolution).ConfigureAwait(false);
            if (claimResult is not null) return claimResult;
            var settled = await SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq, terminalEvidence,
                evidenceSource, null, terminalEvidence.JobId).ConfigureAwait(false);   // 授权＝上一行已声明的裁决方向
            if (settled.ResponsibilityState != ResponsibilityState.Settled) return settled;
        }

        // 阶段二（持 `_gate`）：一次权威原子发布写审计（未受理分支另加证据记录）并清活动 `pending`。
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            var op = FindOp(read.File, requestIdentity);
            if (op is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "stale_operation_identity", "Operations 记录缺失=响亮拒绝。", requestIdentity);
            if (!string.Equals(op.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal) || op.LastSendSeq != sendSeq)
                return LocatedStop(requestIdentity, op, "stale_evidence", "发送身份已变更（不得据旧身份写审计）。");
            // [Batch B 续 会诊阻断处置] 阶段二必须重新要求**冲突待决**（否则两个不同 resolution 的并发裁决会各写一份审计）。
            if (!op.ConflictPending)
                return LocatedStop(requestIdentity, op, "no_pending_conflict",
                    "阶段二复核：冲突待决已被其他裁决清除（不得追加第二份审计）。");
            // [第三轮验证会诊阻断处置] 阶段二必须复核**裁决方向声明**（受理终态分支跨两个发布，
            // 相反方向不得在本窗口内抢先落审计——否则形成终态载体与未受理审计并存的矛盾状态）。
            if (resolution == ConflictResolutionKind.ResolvedAcceptedTerminal
                && !string.Equals(op.ConflictAdjudicationClaim, nameof(ConflictResolutionKind.ResolvedAcceptedTerminal), StringComparison.Ordinal))
                return LocatedStop(requestIdentity, op, "conflict_adjudication_claim_missing",
                    "阶段二复核：缺少本方向裁决声明（不得跨方向写入审计）。");
            if (resolution == ConflictResolutionKind.ResolvedNotAccepted
                && op.ConflictAdjudicationClaim is { Length: > 0 })
                return LocatedStop(requestIdentity, op, "conflict_adjudication_in_progress",
                    "已有相反方向的裁决声明在处理中（不得并发写入第二份审计）。");
            if (op.LastResult is not { Outcome: OperationOutcome.Rejected } rejectedHistory)
                return LocatedStop(requestIdentity, op, "conflict_requires_rejected_history",
                    "冲突裁决要求存在「被取代的拒绝结果」快照（否则不得声称拒绝了本笔发送）。");
            // 拒绝快照必须属于**本笔当前轮次**（不得把旧轮拒绝伪造成本轮快照）。
            if (rejectedHistory.AnsweredSendSeq != sendSeq)
                return LocatedStop(requestIdentity, op, "conflict_rejection_not_current_round",
                    "既有拒绝结果不属于本笔当前轮次（不得据此写审计）。");

            var now = _utcNow();
            var auditId = DeriveConflictAuditId(requestIdentity, submissionIdentity, sendSeq, resolution);
            var evidenceId = DeriveConflictEvidenceId(requestIdentity, submissionIdentity, sendSeq);
            var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op2 = FindOp(file, requestIdentity);
                if (op2 is null) return "state_changed";
                if (!string.Equals(op2.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal) || op2.LastSendSeq != sendSeq)
                    return "state_changed";
                // [第二轮验证会诊阻断处置] 待决与当前轮拒绝复核必须**在原子回调内**（`_gate` 只保护本进程：
                // 两个进程可能同时通过外层检查，随后依次进入存储锁——缺此复核会写入第二份审计并覆盖引用）。
                if (!op2.ConflictPending) return "no_pending_conflict";
                // [第四轮验证会诊阻断处置] 方向声明必须在**原子回调内**复核（`_gate` 只保护本进程；
                // 相反方向可在外层检查之后、本回调之前写入声明）。
                if (resolution == ConflictResolutionKind.ResolvedNotAccepted
                    && op2.ConflictAdjudicationClaim is { Length: > 0 })
                    return "conflict_adjudication_in_progress";
                if (resolution == ConflictResolutionKind.ResolvedAcceptedTerminal
                    && !string.Equals(op2.ConflictAdjudicationClaim,
                        nameof(ConflictResolutionKind.ResolvedAcceptedTerminal), StringComparison.Ordinal))
                    return "conflict_adjudication_in_progress";
                if (op2.LastResult is not { Outcome: OperationOutcome.Rejected } currentRejection
                    || currentRejection.AnsweredSendSeq != sendSeq)
                    return "conflict_rejection_not_current_round";
                var audits = file.Handoff!.ConflictResolutionAudits ??= [];
                var existingAudit = audits.FirstOrDefault(a => a is not null
                    && string.Equals(a.AuditId, auditId, StringComparison.Ordinal));
                if (existingAudit is not null)
                {
                    // [第三轮验证会诊阻断处置] 幂等必须**全载荷**比较（同 ID 同全载荷＝幂等；异载荷＝损坏，不覆盖）：
                    // 仅比较来源会让「同来源、改事实类型/原始词/观察时点/终态类别」被当成幂等成功。
                    var samePayload = resolution == ConflictResolutionKind.ResolvedAcceptedTerminal
                        ? AuditMatchesCompletion(existingAudit, terminalEvidence!, op2.ExecutionResult, submissionIdentity, sendSeq)
                        : AuditMatchesNotAccepted(existingAudit, file.Handoff.ReconciledNotAcceptedEvidence ?? [],
                            notAccepted!, submissionIdentity, sendSeq);
                    if (existingAudit.Resolution != resolution || !samePayload) return "conflict_audit_conflict";
                }
                else
                {
                    string? evidenceRef = null;
                    if (resolution == ConflictResolutionKind.ResolvedNotAccepted)
                    {
                        var evidences = file.Handoff.ReconciledNotAcceptedEvidence ??= [];
                        var existingEvidence = evidences.FirstOrDefault(e => e is not null
                            && string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
                        if (existingEvidence is null)
                        {
                            evidences.Add(new ReconciledNotAcceptedEvidence
                            {
                                EvidenceId = evidenceId,
                                // [Batch B 续 会诊阻断处置] **逐字段原样复制**权威观察（不得现场构造）。
                                SubmissionIdentity = notAccepted!.SubmissionIdentity,
                                SendSeq = notAccepted.SendSeq,
                                FactKind = notAccepted.FactKind,
                                RawEvidenceWord = notAccepted.RawEvidenceWord,
                                EvidenceSource = notAccepted.EvidenceSource,
                                ObservedAtUtc = notAccepted.ObservedAtUtc,
                            });
                        }
                        else if (!string.Equals(existingEvidence.EvidenceSource, notAccepted!.EvidenceSource, StringComparison.Ordinal)
                                 || !string.Equals(existingEvidence.FactKind, notAccepted.FactKind, StringComparison.Ordinal)
                                 || !string.Equals(existingEvidence.RawEvidenceWord, notAccepted.RawEvidenceWord, StringComparison.Ordinal)
                                 || existingEvidence.ObservedAtUtc != notAccepted.ObservedAtUtc
                                 || !string.Equals(existingEvidence.SubmissionIdentity, notAccepted.SubmissionIdentity, StringComparison.Ordinal)
                                 || existingEvidence.SendSeq != notAccepted.SendSeq)
                        {
                            return "conflict_audit_conflict";
                        }
                        evidenceRef = evidenceId;
                    }

                    audits.Add(new ConflictResolutionAudit
                    {
                        AuditId = auditId,
                        RequestIdentity = requestIdentity,
                        SubmissionIdentity = submissionIdentity,
                        SendSeq = sendSeq,
                        Resolution = resolution,
                        ResolvedAtUtc = now,
                        SupersededRejectedResultSnapshot = new OperationResult
                        {
                            Outcome = OperationOutcome.Rejected,
                            ReasonCode = rejectedHistory.ReasonCode,
                            Retryable = rejectedHistory.Retryable,
                            RetryBudgetUsed = rejectedHistory.RetryBudgetUsed,
                            EvidenceSource = rejectedHistory.EvidenceSource,
                            AnsweredSendSeq = sendSeq,
                        },
                        ResolutionEvidenceSnapshot = resolution == ConflictResolutionKind.ResolvedAcceptedTerminal
                            ? op2.ExecutionResult
                            : null,
                        ResolutionEvidenceRef = evidenceRef is null ? null : new ConflictResolutionEvidenceRef { EvidenceId = evidenceRef },
                    });
                }
                op2.ConflictResolutionAuditId = auditId;
                op2.ConflictPending = false; // 只清活动覆盖层；审计/证据/拒绝快照一律保留
                op2.ConflictResolutionState = null;
                op2.ConflictAdjudicationClaim = null; // 审计落盘同一次发布清方向声明
                // [第五轮验证会诊阻断处置] **镜像合并项到胜者的结果事实**（否则镜像记录会继续回放历史拒绝）。
                foreach (var m in (file.Handoff.Operations ?? []).Where(o => o is not null
                    && string.Equals(o.MergedInto, requestIdentity, StringComparison.Ordinal)))
                {
                    if (m.Zone != OperationZone.Active) continue;
                    m.ExecutionResult = op2.ExecutionResult is null ? null : new ExecutionResult
                    {
                        Kind = op2.ExecutionResult.Kind,
                        RawTerminal = op2.ExecutionResult.RawTerminal,
                        ExecutionErrorCode = op2.ExecutionResult.ExecutionErrorCode,
                        JobId = op2.ExecutionResult.JobId,
                        EvidenceSource = op2.ExecutionResult.EvidenceSource,
                        SubmissionIdentity = op2.ExecutionResult.SubmissionIdentity,
                        SendSeq = op2.ExecutionResult.SendSeq,
                        ObservedAtUtc = op2.ExecutionResult.ObservedAtUtc,
                    };
                    m.RequestState = op2.RequestState;
                    m.Zone = op2.Zone == OperationZone.Tombstone ? OperationZone.TerminalPendingTransfer : op2.Zone;
                    m.UpdatedRevision = file.Revision + 1;
                    m.UpdatedAtUtc = now;
                }
                op2.UpdatedRevision = file.Revision + 1;
                op2.UpdatedAtUtc = now;
                return null;
            });
            if (!mutate.Success)
            {
                var failReason = mutate.Reason ?? "invalid_request";
                // [第四轮验证会诊阻断处置] 已知审计/方向冲突码**原样透传**（不得被 `conflict_adjudication_failed:` 包装）。
                if (failReason is "conflict_audit_conflict" or "conflict_adjudication_in_progress" or "no_pending_conflict"
                    or "conflict_rejection_not_current_round")
                    return LocatedStop(requestIdentity, op, failReason,
                        "裁决审计未提交（保守停驻：保持冲突待决，不释放占用、不重发）。");
                return LocatedStop(requestIdentity, op, "conflict_adjudication_failed:" + failReason,
                    "裁决审计发布失败（保守停驻：保持冲突待决，不释放占用、不重发）。");
            }

            return new AdmissionResult
            {
                Kind = resolution == ConflictResolutionKind.ResolvedNotAccepted
                    ? AdmissionResultKind.TerminalRejected
                    : MapResultKindFromExecution(op.ExecutionResult?.Kind),
                ReasonCode = resolution == ConflictResolutionKind.ResolvedNotAccepted
                    ? "conflict_resolved_not_accepted"
                    : "conflict_resolved_accepted_terminal",
                Detail = "冲突已裁决（审计与证据已原子留痕；活动 `pending` 已清除，历史拒绝仅作审计）。",
                RequestIdentity = requestIdentity,
                SubmissionIdentity = submissionIdentity,
                SendSeq = sendSeq,
                JobId = op.ExecutionResult?.JobId,
                ExecutionDisposition = op.ExecutionResult?.Kind switch
                {
                    ExecutionResultKind.Cancelled => ExecutionDisposition.Cancelled,
                    ExecutionResultKind.Failed => ExecutionDisposition.ExecutionFailed,
                    _ => ExecutionDisposition.None,
                },
                ResponsibilityState = ResponsibilityState.Settled,
                RawTerminal = op.ExecutionResult?.RawTerminal,
                ExecutionErrorCode = op.ExecutionResult?.ExecutionErrorCode,
                EvidenceSource = evidenceSource,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// **原子声明裁决方向**（[第三轮验证会诊阻断处置]）：同方向复用；相反方向 ⇒ `conflict_adjudication_in_progress`；
    /// 冲突已被清除（已裁决）⇒ 交由调用方走幂等重放。声明在审计落盘的**同一次发布**清除。
    /// </summary>
    private async Task<AdmissionResult?> ClaimAdjudicationAsync(
        string requestIdentity, string submissionIdentity, int sendSeq, ConflictResolutionKind resolution)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var read = _store.Read();
            if (read.File?.Lease is null)
                return AdmissionResult.Of(AdmissionResultKind.Error, "lease_not_valid", "未持有租约。", requestIdentity);
            var lease = read.File.Lease;
            AdmissionResult? reject = null;
            var mutate = _store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
            {
                var op = FindOp(file, requestIdentity);
                if (op is null) return "state_changed";
                if (!string.Equals(op.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal) || op.LastSendSeq != sendSeq)
                    return "state_changed";
                if (!op.ConflictPending) return null; // 已裁决：交由幂等重放路径
                var want = resolution.ToString();
                if (op.ConflictAdjudicationClaim is { Length: > 0 } claim
                    && !string.Equals(claim, want, StringComparison.Ordinal))
                {
                    reject = LocatedStop(requestIdentity, op, "conflict_adjudication_in_progress",
                        "已有相反方向的裁决声明在处理中（不得并发裁决）。");
                    return "conflict_adjudication_in_progress";
                }
                op.ConflictAdjudicationClaim = want;
                op.UpdatedRevision = file.Revision + 1;
                op.UpdatedAtUtc = _utcNow();
                return null;
            });
            return reject;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>裁决审计 ↔ 本次受理终态事实的**全载荷**等值判据（[第三轮验证会诊]）。</summary>
    private static bool AuditMatchesCompletion(
        ConflictResolutionAudit audit, ExternalStartCompletion c, ExecutionResult? persisted,
        string submissionIdentity, int sendSeq)
    {
        if (audit.ResolutionEvidenceSnapshot is not { } s) return false;
        // [第五轮验证会诊阻断处置] 审计快照必须**命中已持久化权威事实**（`persisted == null` ⇒ 不是幂等，是损坏）。
        if (persisted is null) return false;
        // ① 审计快照必须与**已持久化**权威执行结果**全等**（含 JobId 与观察时点——不留通配）。
        if (s.Kind != persisted.Kind
            || !string.Equals(s.RawTerminal, persisted.RawTerminal, StringComparison.Ordinal)
            || !string.Equals(s.ExecutionErrorCode, persisted.ExecutionErrorCode, StringComparison.Ordinal)
            || !string.Equals(s.JobId, persisted.JobId, StringComparison.Ordinal)
            || !string.Equals(s.EvidenceSource, persisted.EvidenceSource, StringComparison.Ordinal)
            || s.ObservedAtUtc != persisted.ObservedAtUtc
            || !string.Equals(s.SubmissionIdentity, persisted.SubmissionIdentity, StringComparison.Ordinal)
            || s.SendSeq != persisted.SendSeq)
            return false;
        // ② 本次请求携带的字段必须与快照一致；`JobId` 请求未携带时以**持久化句柄**为准（§24.3-3 合并语义）。
        return s.Kind == (c.Kind switch
               {
                   ExternalStartCompletionKind.Succeeded => ExecutionResultKind.Succeeded,
                   ExternalStartCompletionKind.Cancelled => ExecutionResultKind.Cancelled,
                   _ => ExecutionResultKind.Failed,
               })
               && string.Equals(s.RawTerminal, c.RawTerminal ?? "", StringComparison.Ordinal)
               && string.Equals(s.ExecutionErrorCode, c.ExecutionErrorCode, StringComparison.Ordinal)
               && string.Equals(s.JobId, persisted.JobId, StringComparison.Ordinal)
               && (string.IsNullOrEmpty(c.JobId) || string.Equals(s.JobId, c.JobId, StringComparison.Ordinal))
               && string.Equals(s.EvidenceSource, c.EvidenceSource ?? "", StringComparison.Ordinal)
               && string.Equals(s.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
               && s.SendSeq == sendSeq
               && c.ObservedAtUtc is { } observed && s.ObservedAtUtc == observed;
    }

    /// <summary>裁决审计 ↔ 本次未受理观察的**全载荷**等值判据（[第三轮验证会诊]）。</summary>
    private static bool AuditMatchesNotAccepted(
        ConflictResolutionAudit audit, List<ReconciledNotAcceptedEvidence> evidences,
        NotAcceptedObservation observation, string submissionIdentity, int sendSeq)
    {
        if (audit.ResolutionEvidenceRef?.EvidenceId is not { Length: > 0 } evidenceId) return false;
        var record = evidences.FirstOrDefault(e => e is not null
            && string.Equals(e.EvidenceId, evidenceId, StringComparison.Ordinal));
        return record is not null
               // [第五轮验证会诊阻断处置] 观察的**完整发送身份**同样必须全等（不得「同业务字段、异身份」被当幂等）。
               && string.Equals(observation.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
               && observation.SendSeq == sendSeq
               && string.Equals(record.FactKind, observation.FactKind, StringComparison.Ordinal)
               && string.Equals(record.RawEvidenceWord, observation.RawEvidenceWord, StringComparison.Ordinal)
               && string.Equals(record.EvidenceSource, observation.EvidenceSource, StringComparison.Ordinal)
               && record.ObservedAtUtc == observation.ObservedAtUtc
               && string.Equals(record.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
               && record.SendSeq == sendSeq;
    }

    /// <summary>
    /// 由**已持久化执行事实**构造分类结果（[第四轮验证会诊阻断处置]）：续用/重启/合并共享同一结果事实，
    /// 取消与执行失败不得被改写成成功；责任维按「是否已结清」区分。
    /// </summary>
    private static AdmissionResult ClassifyFromExecutionResult(
        string requestIdentity, OperationRecord op, bool settled, string reasonCode, string detail,
        string? winnerCandidateId = null)
    {
        var r = op.ExecutionResult;
        return new AdmissionResult
        {
            Kind = MapResultKindFromExecution(r?.Kind),
            ReasonCode = reasonCode,
            Detail = detail,
            RequestIdentity = requestIdentity,
            SubmissionIdentity = op.SubmissionIdentity,
            SendSeq = op.LastSendSeq,
            JobId = r?.JobId,
            ExecutionDisposition = MapDispositionFromExecution(r?.Kind),
            ResponsibilityState = settled ? ResponsibilityState.Settled : ResponsibilityState.Pending,
            RawTerminal = r?.RawTerminal,
            ExecutionErrorCode = r?.ExecutionErrorCode,
            EvidenceSource = r?.EvidenceSource,
            WinnerCandidateId = winnerCandidateId,
        };
    }

    /// <summary>已持久化执行结果类别 → 对外结果类别（[Batch B 续 会诊] 结果维不得被压成 Accepted）。</summary>
    private static AdmissionResultKind MapResultKindFromExecution(ExecutionResultKind? kind)
        => kind switch
        {
            ExecutionResultKind.Cancelled => AdmissionResultKind.Cancelled,
            ExecutionResultKind.Failed => AdmissionResultKind.ExecutionFailed,
            ExecutionResultKind.Unknown => AdmissionResultKind.NeedReconcile,
            _ => AdmissionResultKind.Accepted,
        };

    private static ExecutionDisposition MapDispositionFromExecution(ExecutionResultKind? kind)
        => kind switch
        {
            ExecutionResultKind.Cancelled => ExecutionDisposition.Cancelled,
            ExecutionResultKind.Failed => ExecutionDisposition.ExecutionFailed,
            ExecutionResultKind.Unknown => ExecutionDisposition.Unknown,
            _ => ExecutionDisposition.None,
        };

    /// <summary>裁决审计 ID（**确定性派生**：重试复用同一 ID，使「同 ID 同载荷＝幂等」可判定）。</summary>
    private static string DeriveConflictAuditId(string requestIdentity, string submissionIdentity, int sendSeq, ConflictResolutionKind resolution)
        => "audit-" + ShortHash($"{requestIdentity}|{submissionIdentity}|{sendSeq}|{resolution}");

    /// <summary>未受理证据 ID（同上，确定性派生）。</summary>
    private static string DeriveConflictEvidenceId(string requestIdentity, string submissionIdentity, int sendSeq)
        => "ev-" + ShortHash($"{requestIdentity}|{submissionIdentity}|{sendSeq}|notaccepted");

    private static string ShortHash(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant()[..16];

    /// <summary>完成结算中途失败：责任保留（`Pending`），返回可对账原因（不重发、不释放占用）。</summary>
    private static AdmissionResult SettleStop(
        string requestIdentity, string submissionIdentity, int sendSeq, string reasonCode,
        ExternalStartCompletion completion, string detail, string? effectiveJobId = null)
        => new()
        {
            // [Batch B 会诊阻断处置] **结果维保留已观察事实**（§24.6-2：后续持久化失败不得把取消/失败改写成"未知"）；
            // 仅**责任维**保持 `Pending`（未结清），并用 ReasonCode 说明停驻原因。
            Kind = completion.Kind switch
            {
                ExternalStartCompletionKind.Cancelled => AdmissionResultKind.Cancelled,
                ExternalStartCompletionKind.ExecutionFailed => AdmissionResultKind.ExecutionFailed,
                ExternalStartCompletionKind.Succeeded => AdmissionResultKind.NeedReconcile,
                _ => AdmissionResultKind.NeedReconcile,
            },
            ReasonCode = reasonCode,
            Detail = detail,
            RequestIdentity = requestIdentity,
            SubmissionIdentity = submissionIdentity,
            SendSeq = sendSeq,
            JobId = effectiveJobId ?? completion.JobId,
            ExecutionDisposition = completion.Kind switch
            {
                ExternalStartCompletionKind.Cancelled => ExecutionDisposition.Cancelled,
                ExternalStartCompletionKind.ExecutionFailed => ExecutionDisposition.ExecutionFailed,
                _ => ExecutionDisposition.Unknown,
            },
            ResponsibilityState = ResponsibilityState.Pending,
            RawTerminal = completion.RawTerminal,
            ExecutionErrorCode = completion.ExecutionErrorCode,
            EvidenceSource = completion.EvidenceSource,
        };

    /// <summary>
    /// 已定位 Operation 之后的**响亮拒绝**（[Batch B 会诊阻断处置]）：一律带责任维 `Pending`（已登记≠不适用）
    /// 与已知发送身份，避免调用方把「已签发发送责任的拒绝」误读成「未发生」。
    /// </summary>
    private static AdmissionResult LocatedStop(string requestIdentity, OperationRecord op, string reasonCode, string detail,
        bool conflictPending = false)
        => new()
        {
            Kind = AdmissionResultKind.Error,
            ReasonCode = reasonCode,
            Detail = detail,
            RequestIdentity = requestIdentity,
            SubmissionIdentity = op.SubmissionIdentity,
            SendSeq = op.LastSendSeq,
            JobId = op.ExecutionResult?.JobId,
            ExecutionDisposition = ExecutionDisposition.Unknown,
            // 冲突证据到达（`conflictPending=true`）时**不得**报「已结清」：冲突断言本身是需要权威裁决的未结清责任。
            ResponsibilityState = !conflictPending && op.RequestState == OperationRequestState.TerminalCompleted
                ? ResponsibilityState.Settled
                : ResponsibilityState.Pending,
            EvidenceSource = op.ExecutionResult?.EvidenceSource,
        };

    /// <summary>
    /// 已存在权威终态事实时按 §24.13-2 **幂等重放**（不得被 `null`/`Unknown` 掩盖或降级为普通受理）。
    /// </summary>
    private static AdmissionResult ReplayExistingFact(string requestIdentity, OperationRecord op, ExecutionResult existing)
        => new()
        {
            Kind = existing.Kind switch
            {
                ExecutionResultKind.Cancelled => AdmissionResultKind.Cancelled,
                ExecutionResultKind.Failed => AdmissionResultKind.ExecutionFailed,
                _ => AdmissionResultKind.Accepted,
            },
            ReasonCode = "existing_terminal_fact",
            Detail = "已有权威终态事实（幂等重放；不得由 null/未知降级）。",
            RequestIdentity = requestIdentity,
            SubmissionIdentity = op.SubmissionIdentity,
            SendSeq = op.LastSendSeq,
            JobId = existing.JobId,
            ExecutionDisposition = existing.Kind switch
            {
                ExecutionResultKind.Cancelled => ExecutionDisposition.Cancelled,
                ExecutionResultKind.Failed => ExecutionDisposition.ExecutionFailed,
                ExecutionResultKind.Unknown => ExecutionDisposition.Unknown,
                _ => ExecutionDisposition.None,
            },
            ResponsibilityState = op.RequestState == OperationRequestState.TerminalCompleted
                ? ResponsibilityState.Settled
                : ResponsibilityState.Pending,
            RawTerminal = existing.RawTerminal,
            ExecutionErrorCode = existing.ExecutionErrorCode,
            EvidenceSource = existing.EvidenceSource,
        };

    /// <summary>
    /// **只落接管、不关闭**（[第三轮验证会诊阻断处置] §24.15 唯一顺序）：终态先到且发送未关闭时，
    /// 必须先写受理接管台账（否则台账无在册记录 ⇒ `MarkTerminal` 只能失败），但**不得**在此关闭 Submission——
    /// 关闭必须排在同一顺序的「台账 Terminal」之后。返回 `null`＝接管已落盘；非 null＝保守停驻结果。
    /// </summary>
    private async Task<AdmissionResult?> PersistAcceptanceTakeoverAsync(
        OperationRecord op, LeaseSegment lease, string? evidenceSource, string? runId, string? jobId)
    {
        if (op.RequestState == OperationRequestState.Accepted) return null; // 已受理（无需补接管）
        var entry = new ExternalStartLedgerEntry
        {
            SubmissionIdentity = op.SubmissionIdentity,
            SendSeq = op.LastSendSeq,
            CandidateId = op.CandidateId,
            ResourceRef = op.ResourceRef ?? "",
            ActionId = op.Candidate?.ActionId ?? ArbitrationOrdering.DeriveActionId(op.CandidateId),
            TargetBgiEpoch = op.TargetEpoch,
            AcceptedAtUtc = _utcNow(),
            EvidenceSource = evidenceSource ?? "reconcile:accepted",
            State = LedgerEntryState.AcceptedPendingExecution,
            RunId = runId,
            JobId = jobId,
            OperationType = op.OperationType,
        };
        string? failure;
        try
        {
            failure = await _hooks.TakeoverPersist(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = "persist_exception:" + ex.GetType().Name;
        }
        return failure is null
            ? null
            : LocatedStop(op.RequestIdentity, op, "takeover_persist_failed",
                "终态先到：受理接管台账落盘失败（" + failure + "）——保守停驻，不写终态载体、不关闭、不重发。");
    }

    /// <summary>
    /// 普通受理补完（[Batch B]）：`null`/`Unknown` 完成结果到达而 Submission 仍未关闭时，
    /// 必须走「接管台账落盘 → 关闭 Submission → Operation=Accepted」——复用 §4.2c 第一分支既有实现。
    /// 返回 `null`＝已完成；非 null＝保守停驻结果（责任 `Pending`）。
    /// </summary>
    private async Task<AdmissionResult?> AcceptOrdinaryAsync(OperationRecord op, LeaseSegment lease, LogicalOwnerLeaseFile file,
        string? evidenceSource = null, string? runId = null, string? jobId = null)
    {
        var candidate = op.Candidate ?? new ArbitrationCandidate();
        var request = new AdmissionRequest
        {
            Namespace = candidate.Namespace ?? "manual",
            Kind = AdmissionKind.ContinueUse,
            RequestIdentity = op.RequestIdentity,
            Candidate = CloneCandidate(candidate),
            WireSubmitKey = op.WireSubmitKey,
            RunBinding = op.RunBinding,
            CursorRef = op.CursorRef,
            CursorRevision = op.CursorRevision,
        };
        var acceptance = await ReconcileOutcomeAsync(request, lease, file,
            // 对账取得的证据来源/运行绑定/句柄**完整携带**（不得用合成值替代，否则接管台账丢关联字段）。
            new SendOutcome.Accepted(evidenceSource ?? "reconcile:accepted", runId ?? op.RunBinding, jobId))
            .ConfigureAwait(false);
        // [第二轮验证会诊阻断处置] **只有 `Accepted` 才算补完成功**：`Reconciling` 恰恰可能是
        // 接管落盘失败/关闭失败——把它当成功会让上层错误报告「普通受理已完成」。原样返回其失败原因与载荷。
        if (acceptance.Kind == AdmissionResultKind.Accepted) return null;
        if (acceptance.Kind == AdmissionResultKind.Reconciling) return acceptance;
        return LocatedStop(op.RequestIdentity, op, acceptance.ReasonCode, "普通受理补完失败（保守停驻）：" + acceptance.Detail);
    }

    /// <summary>完成层结果 ↔ 执行结果载体的**逐字段等值**判据（[第二轮验证会诊] 唯一实现，供写入幂等、读回验证与恢复共用）。</summary>
    private static bool CompletionMatchesExecutionResult(
        ExecutionResult r, ExternalStartCompletion c, DateTimeOffset observedAt, string submissionIdentity, int sendSeq)
        => r.Kind == (c.Kind switch
            {
                ExternalStartCompletionKind.Succeeded => ExecutionResultKind.Succeeded,
                ExternalStartCompletionKind.Cancelled => ExecutionResultKind.Cancelled,
                _ => ExecutionResultKind.Failed,
            })
           // [第三轮验证会诊] **JobId 必须参与等值比较**（否则「台账有句柄、载体无句柄」的分裂会被当成幂等通过）；
           // 完成层未提供句柄时按 §24.3-3「一侧为空＝补齐」语义处理（有效句柄来自对账/接管侧），
           // 两载体之间的句柄一致性由 `TerminalFactsConsistent` 与台账读回另行强制。
           && (string.IsNullOrEmpty(c.JobId) || string.Equals(r.JobId, c.JobId, StringComparison.Ordinal))
           && string.Equals(r.RawTerminal, c.RawTerminal ?? "", StringComparison.Ordinal)
           && string.Equals(r.ExecutionErrorCode, c.ExecutionErrorCode, StringComparison.Ordinal)
           && string.Equals(r.EvidenceSource, c.EvidenceSource ?? "", StringComparison.Ordinal)
           && string.Equals(r.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
           && r.SendSeq == sendSeq
           && r.ObservedAtUtc == observedAt;

    /// <summary>完成层结果 ↔ 待终局处置载体的**逐字段等值**判据（同上；含 JobId 与证据来源/错误码）。</summary>
    private static bool CompletionMatchesPendingTerminal(
        PendingTerminal p, ExternalStartCompletion c, DateTimeOffset observedAt, string submissionIdentity, int sendSeq)
        => p.Kind == (c.Kind switch
            {
                ExternalStartCompletionKind.Succeeded => ExecutionResultKind.Succeeded,
                ExternalStartCompletionKind.Cancelled => ExecutionResultKind.Cancelled,
                _ => ExecutionResultKind.Failed,
            })
           && string.Equals(p.RawTerminal, c.RawTerminal ?? "", StringComparison.Ordinal)
           && string.Equals(p.ExecutionErrorCode, c.ExecutionErrorCode, StringComparison.Ordinal)
           && string.Equals(p.EvidenceSource, c.EvidenceSource ?? "", StringComparison.Ordinal)
           // 同上：完成层未携带句柄时按「一侧为空＝补齐」处理（有效句柄一致性由两载体互校与台账读回强制）。
           && (string.IsNullOrEmpty(c.JobId) || string.Equals(p.JobId, c.JobId, StringComparison.Ordinal))
           && string.Equals(p.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal)
           && p.SendSeq == sendSeq
           && p.ObservedAtUtc == observedAt;

    /// <summary>
    /// 恢复/终局前的**四类事实一致性**（[第二轮验证会诊阻断处置]）：终态载体与待终局载体必须自洽
    /// （同一发送身份/轮次/观察时点，且终态类别与原始终态词一致）——否则不得释放占用、不得补终局。
    /// </summary>
    private static bool TerminalFactsConsistent(OperationRecord op)
    {
        if (op.ExecutionResult is not { } result || op.PendingTerminal is not { } pending) return false;
        // [第四轮验证会诊阻断处置] 判据必须**绑定本 Operation 的完整发送身份与类型**：
        // 仅证明两载体彼此一致，无法排除「错轮次的成对载体」通过台账确认后错误释放占用。
        return string.Equals(result.SubmissionIdentity, op.SubmissionIdentity, StringComparison.Ordinal)
               && result.SendSeq == op.LastSendSeq
               && pending.OperationType == op.OperationType
               && op.OperationType == OperationType.ExternalStart
               && string.Equals(result.SubmissionIdentity, pending.SubmissionIdentity, StringComparison.Ordinal)
               // [第三轮验证会诊] **终态类别必须一致**（仅「属于三个终态之一」不足）。
               && result.Kind == pending.Kind
               && result.SendSeq == pending.SendSeq
               && result.ObservedAtUtc == pending.ObservedAtUtc
               && string.Equals(result.RawTerminal, pending.RawTerminal, StringComparison.Ordinal)
               && string.Equals(result.ExecutionErrorCode, pending.ExecutionErrorCode, StringComparison.Ordinal)
               && string.Equals(result.JobId, pending.JobId, StringComparison.Ordinal)
               && string.Equals(result.EvidenceSource, pending.EvidenceSource, StringComparison.Ordinal)
               && result.Kind is ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled;
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
            // §24.15／§24.1-6（[Batch B 会诊阻断处置]）：外部启动**禁止**借通用终局入口的「台账布尔确认」旁路——
            // 其终局必须经完成结算入口按唯一顺序完成（ExecutionResult＋PendingTerminal → 台账 Terminal → 关闭 → 终局）。
            if (op.OperationType == OperationType.ExternalStart)
                return AdmissionResult.Of(AdmissionResultKind.Error, "external_start_requires_completion_settlement",
                    "外部启动必须经完成结算入口（SettleCompletionAsync）终局，禁止旁路通用终局入口。", requestIdentity);
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
            // R5.3 §24.16-4：正式接管台账/PendingTerminal 落盘后（本处＝受理或确定拒绝的关闭事务内）同一权威发布内
            // 把对应预观察记录标记 `completed`——清理仍归 R5.6 统一裁决（本批不删除唯一恢复依据）。
            var pre = (file.Handoff!.PreObservations ?? []).FirstOrDefault(p => p is not null
                && string.Equals(p.SubmissionIdentity, submission.SubmissionIdentity, StringComparison.Ordinal)
                && p.SendSeq == submission.SendSeq);
            if (pre is not null) pre.State = "completed";
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
                            // [Batch B 续 会诊阻断处置] 冲突待决 ⇒ **重试资格失效**（不重新入队、不改写状态）。
                            if (op.ConflictPending)
                            {
                                early = new AdmissionResult
                                {
                                    Kind = AdmissionResultKind.NeedReconcile,
                                    ReasonCode = "conflict_pending",
                                    Detail = "存在待决冲突：重试资格失效（禁止重发，必须先经权威裁决）。",
                                    RequestIdentity = requestIdentity,
                                    ExecutionDisposition = ExecutionDisposition.Unknown,
                                    ResponsibilityState = ResponsibilityState.Pending,
                                };
                                break;
                            }
                            // [Batch B 收尾 会诊阻断处置] 持久化类型未知（旧格式代隔离产物）⇒ 不重新入队、不改写状态。
                            if (!Enum.IsDefined(op.OperationType) || op.OperationType == OperationType.Unknown)
                            {
                                early = new AdmissionResult
                                {
                                    Kind = AdmissionResultKind.Error,
                                    ReasonCode = "legacy_operation_type_unresolved",
                                    Detail = "持久化操作类型缺失/未知：不得重新驱动（需显式隔离/迁移处置）。",
                                    RequestIdentity = requestIdentity,
                                    SubmissionIdentity = op.SubmissionIdentity,
                                    SendSeq = op.LastSendSeq,
                                    ExecutionDisposition = ExecutionDisposition.Unknown,
                                    ResponsibilityState = ResponsibilityState.Pending,
                                };
                                break;
                            }
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
            // [Batch B 续 会诊阻断处置] 冲突待决 ⇒ 窗口到期**不得**据此转终局中止（重试资格在冲突期间失效）。
            if (op.ConflictPending) return "conflict_pending";
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
                             && !o.ConflictPending  // [Batch B 续] 第四类集合：冲突待决不得被通用恢复改写
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
                             && !o.ConflictPending  // [Batch B 续] 同上
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
                        // [Batch B 会诊阻断处置] 外部启动**不得**借通用「台账布尔确认」旁路：单独走下方 externalReady。
                        .Where(o => o.OperationType != OperationType.ExternalStart)
                        // [Batch B 续] 冲突待决记录**不得**被补终局（冲突断言需权威裁决；占位与恢复依据保留）。
                        .Where(o => !o.ConflictPending)
                        .Where(o => o.SubmissionIdentity is not null
                                    && _hooks.TakeoverTerminalConfirmed?.Invoke(o.SubmissionIdentity, o.LastSendSeq) == true)
                        .Select(o => o.RequestIdentity)
                        .ToList();
                // 外部启动补终局（§24.15）：**四类事实齐备**（ExecutionResult＋PendingTerminal＋台账 Terminal＋Submission 已关闭）
                // 才在同一边界补 Operation 终局；缺任一事实＝保守停驻（不释放占用、不重发）。
                var externalReady = ownLease is null
                    ? new List<string>()
                    : (mutate.File!.Handoff?.Operations ?? [])
                        .Where(o => o.Zone == OperationZone.Active && o.RequestState == OperationRequestState.Accepted
                                    && o.OperationType == OperationType.ExternalStart
                                    // [第二轮验证会诊阻断处置] **四类事实必须自洽**（身份/轮次/观察时点/类别/原始词/错误码/句柄/来源全等），
                                    // 否则损坏或交叉组合的载体不得被补成终局、不得释放主槽位。
                                    && TerminalFactsConsistent(o)
                                    // [Batch B 续] 冲突待决 ⇒ 不补终局、不释放占用（等待权威裁决）。
                                    && !o.ConflictPending
                                    && o.SubmissionIdentity is not null
                                    // [第三轮验证会诊] 台账侧必须**逐字段**确认（原始词/错误码/句柄/来源/观察时点），
                                    // 仅「记录存在且 Terminal」不足以证明台账终态属于本次载荷。
                                    && _hooks.TakeoverTerminalPayloadConfirmed?.Invoke(o.SubmissionIdentity, o.LastSendSeq,
                                        o.PendingTerminal!.RawTerminal, o.PendingTerminal.ExecutionErrorCode,
                                        o.PendingTerminal.JobId, o.PendingTerminal.EvidenceSource,
                                        o.PendingTerminal.ObservedAtUtc) == true
                                    && (mutate.File!.Handoff?.Submission is not { } stillOpen
                                        || !string.Equals(stillOpen.SubmissionIdentity, o.SubmissionIdentity, StringComparison.Ordinal)))
                        .Select(o => o.RequestIdentity)
                        .ToList();
                foreach (var identity in confirmed.Concat(externalReady))
                {
                    var terminalNow = _utcNow();
                    var terminal = _store.MutateHandoffLatest(ownLease!.LeaseId, ownLease.OwnerEpoch, file =>
                    {
                        var op = FindOp(file, identity);
                        if (op is null || op.Zone != OperationZone.Active || op.RequestState != OperationRequestState.Accepted)
                            return "state_changed";
                        // [第三轮验证会诊] 提交事务内**重新**执行四类事实一致性复核（消除「快照检查→提交」窗口）。
                        if (op.OperationType == OperationType.ExternalStart)
                        {
                            if (!TerminalFactsConsistent(op)) return "terminal_facts_inconsistent";
                            // [第六轮验证会诊] 台账句柄 TOCTOU：台账已有句柄而载体为空/不等 ⇒ 本轮不终局（先补齐）。
                            // 未配置读取器＝不可确认（fail-closed：不补终局）。
                            var probe = _hooks.TakeoverJobIdRead?.Invoke(op.SubmissionIdentity, op.LastSendSeq)
                                ?? LedgerHandleProbe.Unreadable();
                            // 三态：读取失败/不可确认 ⇒ 不补终局（不得把读取故障当作「台账无句柄」而释放占用）。
                            if (probe.State == LedgerHandleState.Unreadable) return "terminal_ledger_unreadable";
                            if (probe is { State: LedgerHandleState.Present, JobId: { Length: > 0 } ledgerJob }
                                && !string.Equals(ledgerJob, op.ExecutionResult!.JobId, StringComparison.Ordinal))
                                return "terminal_job_id_backfill_required";
                        }
                        op.RequestState = OperationRequestState.TerminalCompleted;
                        op.LastResult = new OperationResult { Outcome = OperationOutcome.Accepted, ReasonCode = "terminal_confirmed_after_restart", Retryable = false, RetryBudgetUsed = op.LastResult?.RetryBudgetUsed ?? 0, EvidenceSource = "restart_recovery:terminal_confirmed", AnsweredSendSeq = op.LastSendSeq };
                        op.Zone = OperationZone.TerminalPendingTransfer;
                        // 外部启动：补终局即责任结清 ⇒ 清理待终局处置投影（终态事实由 ExecutionResult 长期承载）。
                        if (op.OperationType == OperationType.ExternalStart) op.PendingTerminal = null;
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
        // 受保护（冲突待决）墓碑**不构成可清理额度**（不得据其宣称容量可用）。
        var cleanable = ops.Where(o => o.Zone == OperationZone.Tombstone && !o.ConflictPending
                                       && o.UpdatedAtUtc + TombstoneRetain <= now)
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
        // §24.2-2″／§24.12-3（[Batch B 续]）：**带冲突待决的墓碑不得被保留期裁剪**（那会删掉唯一恢复依据）。
        // 曾进入冲突覆盖层（已有裁决审计引用）的墓碑只有在该审计引用**唯一命中且身份匹配**时才允许清理。
        var auditsByld = (file.Handoff.ConflictResolutionAudits ?? [])
            .Where(a => a is not null).ToDictionary(a => a.AuditId, a => a, StringComparer.Ordinal);
        ops.RemoveAll(o => o.Zone == OperationZone.Tombstone && !o.ConflictPending
                           && o.UpdatedAtUtc + TombstoneRetain <= now
                           && (o.ConflictResolutionAuditId is not { Length: > 0 } auditId
                               || (auditsByld.TryGetValue(auditId, out var audit)
                                   && string.Equals(audit.RequestIdentity, o.RequestIdentity, StringComparison.Ordinal)
                                   && string.Equals(audit.SubmissionIdentity, o.SubmissionIdentity, StringComparison.Ordinal)
                                   && audit.SendSeq == o.LastSendSeq)));
        var tombstones = ops.Count(o => o.Zone == OperationZone.Tombstone);
        foreach (var op in ops.Where(o => o.Zone == OperationZone.TerminalPendingTransfer).OrderBy(o => o.UpdatedAtUtc))
        {
            // [Batch B 续 会诊] 冲突到达时已在 `TerminalPendingTransfer` 的记录**继续占主槽位**（不得迁墓碑）。
            if (op.ConflictPending) continue;
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

    /// <summary>
    /// 从未决交接责任推导票据三要素（P55④ **本地面**）。BGI 侧票据权威（SuspendedTaskContext/PreemptionGate）
    /// 另需接线（§17 P55 残余）——缺该信号时本推导即当前唯一压制事实源，方向保守：责任存续即压制。
    /// </summary>
    private static TicketBinding? TicketOf(PendingHandoffIntent? pending)
        => pending is null
            ? null
            : new TicketBinding
            {
                SuspendedRunIdentity = pending.SuspendedRunIdentity ?? "",
                AuthorizedPreemptorIdentity = pending.AuthorizedPreemptor ?? "",
                Epoch = pending.TargetEpoch ?? "",
            };

    /// <summary>恢复类请求判定（命名空间或候选意图——两者均由可信适配器在入队时冻结，不取自自报字段）。</summary>
    private static bool IsResumeRequest(AdmissionRequest request)
        => string.Equals(request.Namespace, "resume", StringComparison.Ordinal)
           || string.Equals(request.Candidate?.Intent, "resume", StringComparison.Ordinal);

    /// <summary>
    /// A6 原票据恢复豁免（P55⑥）：**分支限 `interrupted-relocate`**（暂停续行**不得**借用该豁免）
    /// ＋ 恢复目标＝被挂起运行 ＋ 目标 epoch 相符 ＋ 责任阶段=RestorePending（已选择原票据恢复、待确认）。
    /// **范围如实**：本豁免只让其通过「非授权抢占方身份」这一条压制，**不免除**票据要素校验与共同闸门；
    /// 「原票据协议消费与完整恢复闭环」另需验收（本阶段不闭合，见 §17 P57）。
    /// </summary>
    private static bool IsAuthorizedTicketRestore(AdmissionRequest request, PendingHandoffIntent? pending, string targetEpoch)
        => pending is not null
           && IsResumeRequest(request)
           && string.Equals(request.RecoveryBranch, "interrupted-relocate", StringComparison.Ordinal)
           && !string.IsNullOrEmpty(pending.SuspendedRunIdentity)
           && string.Equals(pending.SuspendedRunIdentity, request.RunBinding ?? "", StringComparison.Ordinal)
           && string.Equals(pending.TargetEpoch, targetEpoch, StringComparison.Ordinal)
           && pending.Phase == HandoffPhase.RestorePending;

    /// <summary>
    /// **外部启动观察恢复（R5.3 §24.12-3 集合②/③；[Batch B 收尾之五] 新增）**：进程重启/接管后按**完整发送身份**
    /// （`submissionIdentity＋sendSeq`）把本地租约与外部启动台账对齐：
    /// ①**集合②未终结台账**：**保留观察责任与占用**——不写终态、不释放、不重发（无可用查询依据时长期保守停驻，
    ///   §24.16-3「不可查询时保持 Unknown 且不释放占用」）；
    /// ②**集合③台账已终态、本地 Operation 未终局**：用**已持久化的 `PendingTerminal`**（`Kind`／原词／错误码／
    ///   句柄／证据来源／观察时点）驱动 `SettleCompletionAsync` 补终局（§24.15 唯一顺序；`Unknown`／载荷缺失不驱动）；
    /// ③其余不一致（台账终态但缺 `PendingTerminal`／租约侧已终态而台账未终结／台账孤儿记录）**只登记计数**，
    ///   不改变任何责任（禁止静默释放或补造事实）。
    /// **锁边界**：单次权威串行快照完成分类与待补清单后**释放 `_gate`**，再逐笔调用自取锁的 `SettleCompletionAsync`
    /// （SemaphoreSlim 不可重入）；除集合③补终局外不产生任何状态改动。台账不可读/未配置扫描 ⇒ 保守停驻。
    /// </summary>
    public async Task<ExternalStartRecoveryReport> RecoverExternalStartObservationsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_hooks.TakeoverLedgerScan is not { } scanHook)
            return new ExternalStartRecoveryReport(true, 0, 0, 0, 0, 0, 0);
        TakeoverLedgerScan scan;
        try
        {
            scan = scanHook();
        }
        catch (Exception)
        {
            return new ExternalStartRecoveryReport(true, 0, 0, 0, 0, 0, 0);   // 读取失败＝不可确认（保守停驻）
        }
        if (!scan.Readable) return new ExternalStartRecoveryReport(true, 0, 0, 0, 0, 0, 0);

        var kept = 0;
        var completed = 0;
        var failed = 0;
        var noPending = 0;
        var mismatch = 0;
        var orphans = 0;
        var conflictPendingSkipped = 0;
        var incompletePayload = 0;
        var pendingSettlements = new List<(string RequestIdentity, PendingTerminal Pending, ExternalStartCompletion Completion)>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ops = _store.Read().File?.Handoff?.Operations;
            if (ops is null) return new ExternalStartRecoveryReport(false, 0, 0, 0, 0, 0, 0);
            foreach (var fact in scan.Facts)
            {
                var op = ops.FirstOrDefault(o => o is not null
                    && string.Equals(o.SubmissionIdentity, fact.SubmissionIdentity, StringComparison.Ordinal)
                    && o.LastSendSeq == fact.SendSeq);
                if (op is null)
                {
                    orphans++;   // 台账孤儿（本地无对应发送轮次）：只登记，不删除台账记录
                    continue;
                }
                // §24.12-3④（[验证会诊阻断处置]）：**冲突待决**记录的结算/审计一律归冲突裁决入口
                // （`AdjudicateConflictAsync`）——恢复扫描**不得**走普通完成结算（否则会绕过审计并可能释放占用）。
                if (op.ConflictPending)
                {
                    conflictPendingSkipped++;
                    continue;
                }
                if (!fact.Terminal)
                {
                    if (op.RequestState is OperationRequestState.TerminalCompleted or OperationRequestState.TerminalRejected)
                    {
                        mismatch++;   // 租约侧已终局而台账未终结：保留双方事实，待冲突裁决
                        continue;
                    }
                    kept++;   // 集合②：保留观察责任（禁止超时或重启转终局、禁止重发）
                    continue;
                }
                if (op.RequestState == OperationRequestState.TerminalCompleted) continue;
                if (op.PendingTerminal is not { } pending)
                {
                    noPending++;   // 台账已终态但无 PendingTerminal：保持责任、需重新取证（§24.12-6）
                    continue;
                }
                var completion = CompletionFromPendingTerminal(pending);
                if (completion is null)
                {
                    // `Unknown`／载荷不完整 ⇒ 不得驱动终局（§24.19-2／§24.12-6），且必须**如实登记**（不得静默消失）。
                    incompletePayload++;
                    continue;
                }
                pendingSettlements.Add((op.RequestIdentity, pending, completion));
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var item in pendingSettlements)
        {
            ct.ThrowIfCancellationRequested();   // 取消＝停止后续补终局（已完成的保留；未完成的负责仍保留）
            try
            {
                var result = await SettleCompletionAsync(
                    item.RequestIdentity, item.Pending.SubmissionIdentity, item.Pending.SendSeq, item.Completion,
                    item.Pending.EvidenceSource, acceptanceRunId: null, acceptanceJobId: item.Pending.JobId)
                    .ConfigureAwait(false);
                // [验证会诊重要项处置] **成功判据＝责任已结清**（§24.6-5）：仅凭结果维（`Cancelled`／
                // `ExecutionFailed` 等）会把「台账/关闭/终局任一步失败但结果维保留」误记为补终局成功。
                if (result.ResponsibilityState == ResponsibilityState.Settled
                    && result.Kind is AdmissionResultKind.Accepted or AdmissionResultKind.Cancelled
                        or AdmissionResultKind.ExecutionFailed or AdmissionResultKind.TerminalRejected)
                    completed++;
                else
                    failed++;
            }
            catch (Exception)
            {
                failed++;   // 补终局异常＝责任保留（下一次恢复/对账再试），不影响其他记录
            }
        }

        return new ExternalStartRecoveryReport(false, kept, completed, failed, noPending, mismatch, orphans,
            conflictPendingSkipped, incompletePayload);
    }

    /// <summary>
    /// **`PendingTerminal` → 完成层结果**（§24.15 补终局用；[Batch B 收尾之五] 新增）：`Kind` 是唯一类别判据
    /// （不得按原始终态词猜类别——ext 取消＝`completed`＋取消位）；载荷缺失（原词/观察时点）或 `Unknown`
    /// 一律返回 `null`（**不得**生成终态载体）。
    /// </summary>
    private static ExternalStartCompletion? CompletionFromPendingTerminal(PendingTerminal pending)
    {
        // [验证会诊重要项处置] **不得补造载荷**（§24.2-2／§24.6-2／§24.12-6）：终态所需字段缺失即返回 `null`
        // （只登记异常、不驱动结算）；尤其 `ExecutionFailed` 缺执行错误码时不得用 `"unknown"` 合成。
        if (string.IsNullOrEmpty(pending.RawTerminal) || pending.ObservedAtUtc == default
            || string.IsNullOrEmpty(pending.EvidenceSource))
            return null;
        if (pending.Kind == ExecutionResultKind.Failed && string.IsNullOrEmpty(pending.ExecutionErrorCode))
            return null;
        var source = pending.EvidenceSource;
        return pending.Kind switch
        {
            ExecutionResultKind.Succeeded => ExternalStartCompletion.SucceededWith(
                pending.RawTerminal, source, pending.ObservedAtUtc, pending.JobId),
            ExecutionResultKind.Cancelled => ExternalStartCompletion.CancelledWith(
                pending.RawTerminal, source, pending.ObservedAtUtc, pending.JobId),
            ExecutionResultKind.Failed => ExternalStartCompletion.ExecutionFailedWith(
                // 前置检查已保证 `ExecutionErrorCode` 非空（缺码即返回 null）——此处不得保留 `"unknown"` 合成回退。
                pending.RawTerminal, pending.ExecutionErrorCode!,
                source, pending.ObservedAtUtc, pending.JobId),
            _ => null,
        };
    }

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
