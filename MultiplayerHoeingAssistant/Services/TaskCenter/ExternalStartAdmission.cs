using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models; // R5.3 §24：OperationType／ExecutionDisposition／ResponsibilityState

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// **外部启动准入请求（R5.2 B3：E3/E4/E5 适配器 → 仲裁面）**。
/// 适配器（<see cref="CommandExecutor"/>）在**任何启动副作用之前**提交本请求；§2.2 兼容候选的来源字段
/// 由适配器按可信代码位置提供（旧客户端不携合同字段＝兼容候选，不得从远程自报字段推断）。
/// </summary>
public sealed class ExternalStartAdmissionRequest
{
    /// <summary>来源命名空间：E3/E5 远程=v2；E4 本地热键=manual（§2.1 可信上下文）。</summary>
    public string Namespace { get; init; } = "";

    /// <summary>§2.2 workflowId 段：`group:{groupName}` / `onedragon:{configName}` / `hotkey:{configName}`。</summary>
    public string WorkflowId { get; init; } = "";

    /// <summary>§2.2 触发出现身份（含占位符 `{requestIdentity}`，由门面回填为本次操作身份）。</summary>
    public string TriggerOccurrenceId { get; init; } = "";

    /// <summary>§2.2 resourceRef 段（与 workflowId 同形）。</summary>
    public string ResourceRef { get; init; } = "";

    /// <summary>诊断用来源说明（入口名/操作者），不参与判定。</summary>
    public string SourceDetail { get; init; } = "";

    /// <summary>线上提交键（无法确定性推导时显式携带；可空＝由适配器按既有派生规则另行提供）。</summary>
    public string? WireSubmitKey { get; init; }

    /// <summary>
    /// **同次用户操作的内部重试身份**（§2.1：入口适配器首次接纳分配一次，内部重试复用同一身份）。
    /// null/空＝**新建操作**（`Kind=Create`，身份由门面在同一次原子发布中分配并回显）；
    /// 非空＝**续用既有操作**（`Kind=ContinueUse`）：门面按该身份定位 Operations 记录，
    /// **缺失＝`stale_operation_identity` 响亮拒绝**，绝不回退为创建新操作/换身份/重新绑定。
    /// 纪律：重试必须复用该身份并把触发出现身份中的 `{requestIdentity}` 以同一身份回填（适配器无需预填模板）。
    /// </summary>
    public string? RequestIdentity { get; init; }

    /// <summary>
    /// **实际启动执行**（适配层既有实现，例如 v2 `task.start` 或 ext 队列通道）。
    /// 纪律：**仅在获准后**由门面 Sender 调用一次；调用前不得产生任何启动副作用。
    /// </summary>
    public System.Func<CancellationToken, Task<ExternalStartExecution>> ExecuteAsync { get; init; } =
        _ => throw new System.InvalidOperationException("外部启动执行委托缺失（适配器必须提供）。");

    /// <summary>
    /// **可信持久化操作类型**（R5.3 §24.17）：由适配器按可信代码位置提供（E3/E4/E5 外部启动＝`ExternalStart`），
    /// **不得**由远程自报字段推断；门面在创建 Operation 时与记录同次原子发布，后续不可改写。
    /// </summary>
    public OperationType OperationType { get; init; } = OperationType.ExternalStart;
}

/// <summary>外部启动执行结果（适配层对「是否受理」的**关联验证后**结论；不得凭超时/异常推断未受理）。</summary>
public enum ExternalStartExecutionKind
{
    Accepted,
    Rejected,
    Unknown,
}

/// <summary>
/// **发送层结论**（R5.3 §24.2-2）：适配器在「发送调用」内给出——**只回答「是否受理」**，**无 `Terminal` 字段**
/// （终态事实只由完成层 <see cref="ExternalStartCompletion"/> 承载，杜绝第三套终态来源）。
/// **同一发送身份只允许产生一次发送层结论**；`Accepted`（早期受理）之后不得再产出 `Rejected`/`Unknown`（非法序列）。
/// 与门面 `SendOutcome` **一对一、字段级无损**：`Accepted(JobId)`／`Rejected(reason, retryable, evidenceSource)`／`Unknown(detail)`。
/// </summary>
public sealed record ExternalStartExecution
{
    public ExternalStartExecutionKind Kind { get; }
    public string? JobId { get; }
    public string? Reason { get; }
    public bool Retryable { get; }
    public string? EvidenceSource { get; }

    /// <summary>
    /// 主构造器**私有**（§24.2-2／§24.2-2″：判别式必须封闭构造）——只允许经下面四个工厂生成合法组合，
    /// 从而在**构造层**杜绝「已受理却带拒绝原因」「确定未受理却带句柄」等非法载荷。
    /// </summary>
    private ExternalStartExecution(
        ExternalStartExecutionKind kind, string? jobId, string? reason, bool retryable, string? evidenceSource)
    {
        Kind = kind;
        JobId = jobId;
        Reason = reason;
        Retryable = retryable;
        EvidenceSource = evidenceSource;
    }

    public static ExternalStartExecution AcceptedWith(string? jobId = null, string? evidenceSource = null)
        => new(ExternalStartExecutionKind.Accepted, jobId, null, false, evidenceSource ?? "adapter:accepted");

    /// <summary>
    /// 确定未受理（**必须**携带 `retryable` 与证据来源，§24.2-2）；**只有经关联验证的结论**才可构造本值。
    /// </summary>
    public static ExternalStartExecution RejectedWith(string reason, bool retryable = false, string? evidenceSource = null)
        => new(ExternalStartExecutionKind.Rejected, null,
            string.IsNullOrWhiteSpace(reason) ? "external_rejected" : reason, retryable,
            evidenceSource ?? "adapter:rejected");

    public static ExternalStartExecution UnknownWith(string reason, string? evidenceSource = null)
        => new(ExternalStartExecutionKind.Unknown, null, reason, false, evidenceSource ?? "adapter:unknown");

    /// <summary>[源兼容] 原布尔位 `Uncertain` 由判别式取代（§24.2-2）：`Uncertain` ⇔ `Kind == Unknown`。</summary>
    public bool Uncertain => Kind == ExternalStartExecutionKind.Unknown;

    /// <summary>[源兼容] 原布尔位 `Accepted`：`Accepted` ⇔ `Kind == Accepted`。</summary>
    public bool Accepted => Kind == ExternalStartExecutionKind.Accepted;

    /// <summary>[源兼容] 原 `RejectReason` 属性名。</summary>
    public string? RejectReason => Reason;
}

/// <summary>完成层结果类别（R5.3 §24.2-2）：**不含 `Rejected`**——受理之后不存在「未受理」（类型层杜绝事实反转）。</summary>
public enum ExternalStartCompletionKind
{
    Succeeded,
    Cancelled,
    ExecutionFailed,
    /// <summary>非终态观察（不得生成 `PendingTerminal`，不得借超时/未命中/重启转终态）。</summary>
    Unknown,
}

/// <summary>
/// **完成层结果**（R5.3 §24.2-2）：由句柄 `CompletionTask`／等价权威观察依据承载——**终态事实的唯一载体**。
/// `ObservedAtUtc`＝权威证据**首次被可信观察层接收**的时点（捕获一次、不可改写；§24.2-2″ 两条独立链）。
/// **[验证会诊重要项处置] 主构造器私有＋工厂封闭**：权威终态必须携带 `RawTerminal`／`EvidenceSource`／`ObservedAtUtc`；
/// `ExecutionFailed` 必须携带 `ExecutionErrorCode`；`Unknown` 不得伪装成终态载体。
/// </summary>
public sealed record ExternalStartCompletion
{
    public ExternalStartCompletionKind Kind { get; }
    public string? RawTerminal { get; }
    public string? ExecutionErrorCode { get; }
    public string? JobId { get; }
    public string? EvidenceSource { get; }
    public DateTimeOffset? ObservedAtUtc { get; }

    private ExternalStartCompletion(
        ExternalStartCompletionKind kind, string? rawTerminal, string? executionErrorCode,
        string? jobId, string? evidenceSource, DateTimeOffset? observedAtUtc)
    {
        if (kind != ExternalStartCompletionKind.Unknown)
        {
            if (string.IsNullOrWhiteSpace(rawTerminal))
                throw new ArgumentException("权威终态必须携带原始终态词（RawTerminal）。", nameof(rawTerminal));
            if (string.IsNullOrWhiteSpace(evidenceSource))
                throw new ArgumentException("权威终态必须携带证据来源（EvidenceSource）。", nameof(evidenceSource));
            if (observedAtUtc is null)
                throw new ArgumentException("权威终态必须携带观察时点（ObservedAtUtc）。", nameof(observedAtUtc));
            // [第三轮验证会诊重要项处置] `DateTimeOffset` 值类型可传 `default`——与台账写入的
            // `observed_at_required` 口径一致，构造层同样拒绝默认时间（否则会构造出必然非法的权威终态）。
            if (observedAtUtc.Value == default)
                throw new ArgumentException("权威终态的观察时点不得为默认值（default(DateTimeOffset)）。", nameof(observedAtUtc));
            if (kind == ExternalStartCompletionKind.ExecutionFailed && string.IsNullOrWhiteSpace(executionErrorCode))
                throw new ArgumentException("ExecutionFailed 必须携带 ExecutionErrorCode。", nameof(executionErrorCode));
        }
        Kind = kind;
        RawTerminal = rawTerminal;
        ExecutionErrorCode = executionErrorCode;
        JobId = jobId;
        EvidenceSource = evidenceSource;
        ObservedAtUtc = observedAtUtc;
    }

    public static ExternalStartCompletion SucceededWith(string rawTerminal, string evidenceSource, DateTimeOffset observedAtUtc, string? jobId = null)
        => new(ExternalStartCompletionKind.Succeeded, rawTerminal, null, jobId, evidenceSource, observedAtUtc);

    public static ExternalStartCompletion CancelledWith(string rawTerminal, string evidenceSource, DateTimeOffset observedAtUtc, string? jobId = null)
        => new(ExternalStartCompletionKind.Cancelled, rawTerminal, null, jobId, evidenceSource, observedAtUtc);

    public static ExternalStartCompletion ExecutionFailedWith(string rawTerminal, string executionErrorCode, string evidenceSource, DateTimeOffset observedAtUtc, string? jobId = null)
        => new(ExternalStartCompletionKind.ExecutionFailed, rawTerminal, executionErrorCode, jobId, evidenceSource, observedAtUtc);

    public static ExternalStartCompletion UnknownWith(string detail, string? evidenceSource = null)
        => new(ExternalStartCompletionKind.Unknown, detail, null, null, evidenceSource, null);
}

/// <summary>
/// **早期受理层回执**（R5.3 §24.14-1）：`StartAsync` 拿到早期回执/句柄后的返回联合类型。
/// 唯一映射（§24.2-2″）：`EarlyAccepted → ExternalStartExecution.Accepted`／`Rejected → Rejected`／`Unknown → Unknown`；
/// **无句柄的早期响应一律 `Unknown`**，不得凭空生成句柄。
/// </summary>
public abstract record ExternalStartReply
{
    public sealed record EarlyAccepted : ExternalStartReply
    {
        public string JobId { get; }
        public Task<ExternalStartCompletion> CompletionTask { get; }

        /// <summary>
        /// §24.14-1／§24.2-2″：**无句柄的早期响应一律 `Unknown`**——`EarlyAccepted` 必须同时具备非空句柄与完成观察任务，
        /// 构造层强校验（不得「凭空生成句柄」也不得丢观察责任）。
        /// </summary>
        public EarlyAccepted(string jobId, Task<ExternalStartCompletion> completionTask)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("EarlyAccepted 必须携带非空句柄（无句柄应报 Unknown）。", nameof(jobId));
            ArgumentNullException.ThrowIfNull(completionTask);
            JobId = jobId;
            CompletionTask = completionTask;
        }
    }

    public sealed record Rejected(string Reason, bool Retryable, string EvidenceSource) : ExternalStartReply;
    public sealed record Unknown(string Detail) : ExternalStartReply;
}

/// <summary>
/// **进程内不可变的「外部启动执行」上下文**：由可信适配器在入队时随 <see cref="AdmissionRequest"/> 冻结携带，
/// 经获选排队项原样传到 Sender（§13.10 A2 同纪律）。**不参与任何序列化**。
/// 节点侧的后继上下文是 <see cref="TaskCenterHost.SuccessorContext"/>；本类型专供 E3/E4/E5 启动操作。
/// </summary>
internal sealed record ExternalStartContext(
    System.Func<CancellationToken, Task<ExternalStartExecution>> ExecuteAsync);

/// <summary>适配层可见的准入结论（**不泄漏门面内部类型**；适配器只按本节三态处置）。</summary>
public enum ExternalStartAdmissionStatus
{
    /// <summary>已获准：适配层启动已由门面 Sender 执行（≤1 次）。</summary>
    Accepted,
    /// <summary>确定未受理（可证实未启动）：入口按既有失败语义回执。</summary>
    Rejected,
    /// <summary>事实不可考（含接管落盘失败/宿主退出）：**不得重发**，保守待对账。</summary>
    NeedReconcile,
    /// <summary>门禁/占用阻断（F11、需安全交接确认）：未启动，按既有门禁文案回执。</summary>
    Blocked,
    /// <summary>
    /// **取消**（R5.3 §24.2-3/4：完成层 `Cancelled` 驱动）：入口按既有取消口径回执并**停止批次推进**；
    /// 责任是否结清由 <see cref="ExternalStartAdmissionOutcome.ResponsibilityState"/> 表达（**不得**把取消压成普通失败）。
    /// </summary>
    Cancelled,
    /// <summary>**确定执行失败**（R5.3 §24.9／§24.13-1：完成层 `ExecutionFailed` 驱动；与 `Rejected`/`Unknown` 分离、**不得触发重发**）。</summary>
    ExecutionFailed,
}

/// <summary>
/// 准入结论载荷（原因码 + 说明 + **本次操作身份**，供适配层生成 `CommandResult`）。
/// `RequestIdentity` 是「同次用户操作的内部重试」唯一可复用凭据（§2.1）：适配器须记录它，
/// 重试时以同一身份经 `ExternalStartAdmissionRequest.RequestIdentity` 发起 `ContinueUse`。
/// </summary>
public sealed record ExternalStartAdmissionOutcome(
    ExternalStartAdmissionStatus Status,
    string Code,
    string Message,
    string RequestIdentity = "",
    // ==== R5.3 §24.6-1/2：结果维 × 责任维（加法字段，旧调用兼容） ====
    string? JobId = null,
    ExecutionDisposition ExecutionDisposition = ExecutionDisposition.None,
    ResponsibilityState ResponsibilityState = ResponsibilityState.None,
    string? RawTerminal = null,
    string? ExecutionErrorCode = null,
    string? EvidenceSource = null,
    string? SubmissionIdentity = null,
    int SendSeq = 0);
