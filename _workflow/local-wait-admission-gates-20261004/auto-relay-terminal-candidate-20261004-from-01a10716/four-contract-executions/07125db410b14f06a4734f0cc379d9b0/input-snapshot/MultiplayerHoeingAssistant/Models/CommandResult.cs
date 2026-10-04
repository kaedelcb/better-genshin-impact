namespace MultiplayerHoeingAssistant.Models;

public class CommandResult
{
    public string Status { get; set; } = "failed";  // success / failed
    public string Message { get; set; } = string.Empty;
    /// <summary>BGI 侧信封 errorCode（如 task_busy），用于区分业务拒绝与传输失败；null = 无/旧路径未透传。</summary>
    public string? ErrorCode { get; set; }
    public string? ConfigRevision { get; set; }
    public int? TargetProcessId { get; set; }
    public string? TargetStartTicksUtc { get; set; }

    // ============================================================
    // R5.3 §24.2／§24.6：结果维 × 责任维（**加法字段**；线路词表不改：Status 仍只用 success/failed）
    // ============================================================

    /// <summary>
    /// **是否权威终态**（§24.2-1）：仅当已观察到权威执行终态（ext `Completed`/`QueueCancelled`、v2 明确取消/失败）时为 true。
    /// **v2 仅「发送成功」不得置 true**；`not_found`/超时/通道瞬态一律 false（不得作为终局证据）。
    /// </summary>
    public bool IsTerminal { get; set; }

    /// <summary>远端作业句柄（ext 队列 `taskHandle`；无则 null，**不得臆造**——§24.3）。</summary>
    public string? JobId { get; set; }

    /// <summary>执行结果维（§24.6-1）；`None`＝不适用（未进入执行结果维，如前置门禁阻断）。</summary>
    public ExecutionDisposition ExecutionDisposition { get; set; } = ExecutionDisposition.None;

    /// <summary>责任维（§24.6-5）：`None`＝不适用／`Pending`＝责任未结清／`Settled`＝已结清。</summary>
    public ResponsibilityState ResponsibilityState { get; set; } = ResponsibilityState.None;

    /// <summary>原始终态词（如 `cancelled`／`failed`；**原样保留、不伪造**——§24.2-5／§24.6-2）。</summary>
    public string? RawTerminal { get; set; }

    /// <summary>
    /// **完成层执行错误码**（§24.9／§24.13-1；与既有 <see cref="ErrorCode"/>「BGI 信封错误码」**语义分离**）。
    /// </summary>
    public string? ExecutionErrorCode { get; set; }

    /// <summary>证据来源（原始回执词或对账结论 + 产生端；§24.6-2）。</summary>
    public string? EvidenceSource { get; set; }
}
