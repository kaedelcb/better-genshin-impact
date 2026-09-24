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
    /// **[批次 16／D2] 持久化稳定前置引用**（§24.106 **C4**）：落盘为**稳定引用串**（workflow／node／ticket 等稳定标识组合），
    /// 缺字段／显式 null ⇒ <c>null</c> ⇒ **保守按不可判定**（不得默认「已就绪」）。
    /// **不得**用本地时间戳／随机值填充（重启后不可复核）；就绪**永远**由 evaluator 在读取时求得。
    /// **类型选择依据**：本批内该字段的**全部契约用法**（落盘往返后 `ToString()` 逐字符一致、evaluator 收到后
    /// 与引用串做 `==` 比较）都要求字段**取值即稳定引用串**，故落盘类型取 <see cref="string"/> 而非包装对象
    /// （包装对象会在反射写入时产生类型形状冲突）。引用**结构版本**仍是独立契约面：
    /// 见 <c>PrerequisiteReference.CurrentVersion</c>（本批**未落盘、未参与求值**）。
    /// **不得读强**：<c>LocalWaitQueueStore</c> 响亮拒绝的版本是**队列文件版本**字段
    /// （`"version"`，合法范围 `[MinimumSupportedVersion, CurrentVersion]`），**不是**本字段携带的引用结构版本；
    /// 本字段只承载**引用串本身**，引用结构版本（<c>PrerequisiteReference.CurrentVersion</c>）本批**未落盘**。
    /// </summary>
    [JsonPropertyName("prerequisiteReference")] public string? PrerequisiteReference { get; set; }

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

/// <summary>
/// 等待队列落盘文件（自版本化；更高版本＝响亮拒绝，不降级解析）。
/// **版本 2（批次 16／D2）**：新增 `prerequisiteReference` 持久化稳定前置引用；
/// **版本 1 旧文件仍可读**（缺字段 ⇒ `PrerequisiteReference` 为 null ⇒ 保守按不可判定，不得默认就绪）。
/// </summary>
public sealed class LocalWaitQueueFile
{
    public const int CurrentVersion = 2;

    /// <summary>仍可读取的最低版本（读兼容下界；低于此值响亮拒绝）。</summary>
    public const int MinimumSupportedVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("items")] public List<LocalWaitItem> Items { get; set; } = [];
}
