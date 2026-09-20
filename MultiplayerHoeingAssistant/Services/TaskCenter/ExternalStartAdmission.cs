using System.Threading;
using System.Threading.Tasks;

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
    /// **实际启动执行**（适配层既有实现，例如 v2 `task.start` 或 ext 队列通道）。
    /// 纪律：**仅在获准后**由门面 Sender 调用一次；调用前不得产生任何启动副作用。
    /// </summary>
    public System.Func<CancellationToken, Task<ExternalStartExecution>> ExecuteAsync { get; init; } =
        _ => throw new System.InvalidOperationException("外部启动执行委托缺失（适配器必须提供）。");
}

/// <summary>外部启动执行结果（适配层对「是否受理」的**关联验证后**结论；不得凭超时/异常推断未受理）。</summary>
public sealed record ExternalStartExecution(bool Accepted, string? JobId, string? RejectReason, bool Uncertain)
{
    public static ExternalStartExecution AcceptedWith(string? jobId = null)
        => new(true, jobId, null, false);

    public static ExternalStartExecution RejectedWith(string reason)
        => new(false, null, reason, false);

    public static ExternalStartExecution UnknownWith(string reason)
        => new(false, null, reason, true);
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
}

/// <summary>准入结论载荷（原因码 + 说明，供适配层生成 `CommandResult`）。</summary>
public sealed record ExternalStartAdmissionOutcome(
    ExternalStartAdmissionStatus Status, string Code, string Message);
