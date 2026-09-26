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
    /// <summary>候选稳定身份（去重与审计的权威身份；**队列本地身份空间**）。</summary>
    [JsonPropertyName("stableIdentity")] public string StableIdentity { get; set; } = "";
    /// <summary>
    /// **[批次 20／C3] 准入面候选号**（与 <see cref="AdmissionIdentity"/> 绑定：必须等于
    /// <c>ArbitrationOrdering.DeriveCandidateId(AdmissionIdentity)</c>）。
    /// **合同前存量**（v1/v2 时期文件）缺 <see cref="AdmissionIdentity"/> ⇒ 登记例外：永不参选
    /// （**Legacy 判据＝AdmissionIdentity 缺失**（见其注释与 C4②）；本字段在存量上可空可非空——
    /// 绑定校验在 <c>LocalWaitIdentityTranslation.Translate</c>（批次 20／C3 翻译层）执行）。
    /// </summary>
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    /// <summary>
    /// **[批次 20／C3/C4①] 准入面稳定身份（9 元组）**——由**权威组成函数**
    /// <c>ArbitrationOrdering.BuildStableIdentity</c> 在**登记时点**求得并持久化（身份翻译在登记时完成：
    /// 队列本地身份——D1 裸拼 4 段——**不足以**事后恢复准入候选，§24.115 IW-05／§24.117 C3）。
    /// **合同前存量（C4②，owner 裁决 D-E3=(c)）**：版本 1/2 时期文件**缺字段**，或**当前格式（v3）
    /// 文件中显式 null**（重激活存量在 v3 文件里合法保留 null 绑定——见 LocalWaitQueueStore
    /// ParseAdmissionIdentity 注释；显式 null 不判损坏、按存量降级为永不参选，fail-closed）⇒
    /// <c>null</c> ⇒ 永不参选＋显式标注（<c>LocalWaitIdentityTranslation.Translate</c> 给出
    /// <c>LegacyPreContract</c> 标注），零新通道、零新工具、不可经 Upsert 原地补全（不可变载荷合同不变）。
    /// **新登记**缺失 ⇒ D1 登记点**拒绝登记**（C4① 登记载荷合同：要么登记完整载荷、要么不登记）。
    /// </summary>
    [JsonPropertyName("admissionIdentity")] public string? AdmissionIdentity { get; set; }
    /// <summary>
    /// **[批次 20／Wave2／D-E2=①] 权威代际载体**：取消→同载荷重登记（重激活）时**递增**；
    /// 0＝D-E2 引入前登记的项（v3 早期文件缺字段 ⇒ 0，合法形态）。消费前复核（C5/IW-04）按它
    /// 判「在途重评请求」是否过期（ABA：请求携带的代际 ≠ 项当前代际 ⇒ 过期）。**不**含发送许可。
    /// </summary>
    [JsonPropertyName("generation")] public int Generation { get; set; }
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
/// **版本 3（批次 20／C3/C4①；Wave2 增补）**：新增 `admissionIdentity` 准入面稳定身份（9 元组）＋
/// `candidateId` 升格为准入绑定字段＋**`generation` 权威代际载体（[Wave2 R35 重要-1] D-E2=①：
/// 重激活时 Store 递增；缺字段 ⇒ 0＝D-E2 引入前登记的合法形态）**；
/// **版本 1/2 旧文件仍可读**（缺字段 ⇒ `PrerequisiteReference`/`AdmissionIdentity` 为 null ⇒
/// 保守按不可判定／登记例外（C4②：永不参选＋显式标注），不得默认就绪；`generation` 同理缺字段 ⇒ 0）。
/// </summary>
public sealed class LocalWaitQueueFile
{
    public const int CurrentVersion = 3;

    /// <summary>仍可读取的最低版本（读兼容下界；低于此值响亮拒绝）。</summary>
    public const int MinimumSupportedVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("items")] public List<LocalWaitItem> Items { get; set; } = [];
}
