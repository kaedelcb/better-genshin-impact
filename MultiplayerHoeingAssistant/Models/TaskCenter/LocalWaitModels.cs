using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>本地等待项状态（等待不是 BGI 已入队、也不是已受理）。</summary>
public enum LocalWaitItemState
{
    Waiting = 0,
    Cancelled = 1,
}

/// <summary>
/// **本地持久等待项**（R5 批次 6，owner 裁决 B.1："低优先级新任务在调度器本地持久等待，
/// 当前结束后重新比较，等待期间不得抢发，也不得提前放入 BGI 执行队列"）。
/// 只承载调度意愿，**不含任何发送许可**：入队路径恒不产生发送。
/// </summary>
public sealed class LocalWaitItem
{
    /// <summary>幂等标识（由稳定身份确定性派生；同身份重复登记复用同一条）。</summary>
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    /// <summary>候选稳定身份（去重与审计的权威身份）。</summary>
    [JsonPropertyName("stableIdentity")] public string StableIdentity { get; set; } = "";
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    [JsonPropertyName("namespace")] public string Namespace { get; set; } = "";
    [JsonPropertyName("workflowId")] public string WorkflowId { get; set; } = "";
    /// <summary>级别（System&gt;Fixed&gt;Plan）。</summary>
    [JsonPropertyName("tier")] public ArbitrationTier Tier { get; set; } = ArbitrationTier.Plan;
    [JsonPropertyName("priority")] public int Priority { get; set; }
    /// <summary>是否为最高级锄地类（由**可信入口**判定；自报 key 不作证明）。</summary>
    [JsonPropertyName("isHoeingHighest")] public bool IsHoeingHighest { get; set; }
    [JsonPropertyName("scheduledAt")] public DateTimeOffset? ScheduledAt { get; set; }
    /// <summary>
    /// 到来者的级别/优先级是否来自可信入口（false ⇒ 选择时不得据此越级）。
    /// **默认 false（保守）**：调用方遗漏赋值时一律按不可信处理，避免"忘记设置＝默认可信"。
    /// </summary>
    [JsonPropertyName("hasTrustedIdentity")] public bool HasTrustedIdentity { get; set; }
    [JsonPropertyName("enqueuedAtUtc")] public DateTimeOffset EnqueuedAtUtc { get; set; }
    /// <summary>被清理（失效/取消）的时刻；仅审计与保留期裁剪使用。</summary>
    [JsonPropertyName("cancelledAtUtc")] public DateTimeOffset? CancelledAtUtc { get; set; }
    [JsonPropertyName("state")] public LocalWaitItemState State { get; set; } = LocalWaitItemState.Waiting;
    /// <summary>入队原因／取消原因（审计用，不得承载发送许可）。</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>等待队列落盘文件（自版本化；更高版本＝响亮拒绝，不降级解析）。</summary>
public sealed class LocalWaitQueueFile
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("items")] public List<LocalWaitItem> Items { get; set; } = [];
}
