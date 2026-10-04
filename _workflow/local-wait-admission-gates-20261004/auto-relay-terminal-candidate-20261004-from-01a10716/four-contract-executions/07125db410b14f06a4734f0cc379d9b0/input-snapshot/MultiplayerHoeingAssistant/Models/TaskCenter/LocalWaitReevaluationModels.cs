using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// **本地等待重评触发点**（R5 批次 15／D3：owner 2026-09-24 裁决 D3＝推荐项 A。
/// 「占用结束／权威退出事件为主，启动恢复与新候选为辅，加低频安全网；每次触发重新走完整准入」）。
///
/// 四类互不重叠、取值唯一（不得用别名表达同一类）：主触发＝<see cref="OccupancyEnded"/>，
/// 辅触发＝<see cref="StartupRecovery"/>／<see cref="NewCandidateArrived"/>，兜底＝<see cref="SafetyNet"/>。
/// 本枚举**只表达"为什么现在重评一次"**，**不含**任何发送许可、优先级或执行承诺。
/// </summary>
public enum LocalWaitReevaluationTriggerPoint
{
    /// <summary>
    /// **占用结束／权威退出事件（主触发）**：当前占用者结束（或经权威退出凭证确认退出）后，
    /// 对被压制在本地等待的等待项重新走一次完整准入。
    /// </summary>
    OccupancyEnded = 0,

    /// <summary>
    /// **启动恢复（辅触发）**：宿主启动/恢复时严格读取等待文件后，对所有 `Waiting` 项重新走一次完整准入。
    /// 注意：这与「重启后按等待项自动恢复执行」不同——本类**只产生重评请求**，是否发送由完整准入决定。
    /// </summary>
    StartupRecovery = 1,

    /// <summary>
    /// **新候选到达（辅触发）**：更高优先级候选到达、可能改变原有排序时，重新比较等待集合
    /// （不把「新候选到达」直接当作可抢占证明——抢占仍由 `RunningOccupancyArbiter` 判定）。
    /// </summary>
    NewCandidateArrived = 2,

    /// <summary>
    /// **低频安全网**：兜住漏事件（事件丢失/宿主长时间无退出事件）。**本批不引入真实定时器/后台线程**：
    /// 「是否到安全网时刻」由**调用方注入的纯判定**给出，时间由调用方传入。
    /// </summary>
    SafetyNet = 3,
}

/// <summary>
/// **重评请求**（R5 批次 15／D3）＝「**须重新走一次完整准入**」的唯一产物形态。
///
/// **结构上不含发送许可**：没有 `SendPermitted`／`SendSeq`／`JobId`／`SubmissionIdentity` 等发送面成员
/// ——与批次 14 `LocalWaitEnqueueDecision.SendPermitted` 恒 false、`AdmissionResultKind.WaitLocally`
/// 「不携带发送身份」同向。消费方**只能**把它交给统一准入面（`ArbitrationAdmissionService.SubmitAsync`
/// ＝Create/ContinueUse 完整准入），**不得**据此直接发送。
/// </summary>
public sealed class LocalWaitReevaluationRequest
{
    /// <summary>
    /// 等待项幂等标识（复用 <c>LocalWaitQueuePolicy.DeriveItemId</c> 口径）。
    ///
    /// **边界（批次 15d 如实披露）**：`LocalWaitQueuePolicy.DeriveItemId` 的编码口径对含**孤立代理项**的
    /// `StableIdentity` **不是单射**（实测 `"a\uD800"` 与 `"a\uFFFD"` 得同值）⇒ "唯一指回等待项"不是全空间
    /// 保证，仅在**权威身份面**（不含非正规码元）假设下成立。其内部实现细节**不**由本文档断言（本批材料未含
    /// 该源码，只按实测行为陈述）；本批不改其口径。详情见 <c>LocalWaitReevaluationTrigger.Decide</c> 的
    /// 契约 ⑤ 与 <see cref="ReevaluationKey"/>。
    /// </summary>
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";

    /// <summary>候选稳定身份（去重与审计的权威身份）。</summary>
    [JsonPropertyName("stableIdentity")] public string StableIdentity { get; init; } = "";

    /// <summary>候选号（对账用；无则空）。</summary>
    [JsonPropertyName("candidateId")] public string CandidateId { get; init; } = "";

    /// <summary>触发点（本次重评的原因，须回填以便对账）。</summary>
    [JsonPropertyName("trigger")] public LocalWaitReevaluationTriggerPoint Trigger { get; init; }

    /// <summary>
    /// 重评幂等键（同一等待项确定性派生；同键重复触发不得重复产）。
    ///
    /// **边界（批次 15d 如实披露）**：与 <see cref="ItemId"/> **实测同口径**（UTF-8 取 SHA-256 前缀，仅字面前缀
    /// 不同）⇒ 同受"孤立代理项 ⇒ 同摘要"这一**可复现**等价面影响（两个不同身份可同键 ⇒ 同批只产一条、
    /// 跨调用后者被在飞去重屏蔽）。**另一侧**：指纹仅 64 位 ⇒ 另有**概率**碰撞。二者必须分开表述。
    /// **合并的确切条件**：按 <see cref="ItemId"/> 对账/幂等**恒**合并（与作用域、代际无关）；按本键对账/幂等
    /// 仅**同作用域且同代际**时合并；更换作用域/代际只能区分重评键、**不能**区分既有 <see cref="ItemId"/>。
    /// </summary>
    [JsonPropertyName("reevaluationKey")] public string ReevaluationKey { get; init; } = "";

    /// <summary>
    /// 恒为 true：本产物**只**表达「须重新走一次完整准入」。
    /// 属性无 setter ⇒ 不可由调用方伪造成「已获发送许可」或改为 false 来暗示可直接发送。
    /// </summary>

    [JsonPropertyName("requiresFullAdmission")] public bool RequiresFullAdmission => true;
    /// <summary>
    /// **等待项代际**（批次 15b 修复 #1／#4；`null` ⇒ 无代际，沿用批次 15 的**无代际**去重语义）。
    /// 与 <see cref="ReevaluationKey"/> 的关系（**批次 15c 更正，第二轮修订**）：作用域与代际均走
    /// `reval-key-v2|&lt;摘要&gt;|&lt;作用域字段&gt;|&lt;代际字段&gt;` 的**长度前缀编码**（字段载荷为 **UTF-16 码元的
    /// 十六进制**，大写——`char` 级编码，对含孤立代理项的任意字符串**单射**；`null` 编码为单字符 `-`）；
    /// **仅** `Generation == null 且触发器的 StateScope == null` 时键才与批次 15 逐字符相同（`reval-&lt;摘要&gt;`，
    /// 且该旧形状与新形状字面前缀不同 ⇒ 不可能互相相等）。此更正消除了评审第 3 轮必改 #2 的跨作用域**键别名**
    /// （旧的 `:`／`|` 原样拼接可被构造出不同三元组同键）与第 4 轮必改 #2 的 UTF-8 孤立代理项合并别名。
    /// 本字段**只**用于对账与幂等键复核，**不**承载发送许可、优先级或执行承诺。
    /// </summary>
    [JsonPropertyName("generation")] public string? Generation { get; init; }
}

/// <summary>
/// **重评决策**（R5 批次 15／D3，纯函数产物）：本次触发**实际**产出的重评请求集合
/// （已按等待项幂等键去重、已剔除本进程内处理过的项与已取消项、已按取消令牌短路）。
/// **不含**任何发送面成员；空 <see cref="Requests"/> ⇒ 本次触发**什么都不做**（不是失败）。
/// </summary>
public sealed class LocalWaitReevaluationDecision
{
    [JsonPropertyName("trigger")] public LocalWaitReevaluationTriggerPoint Trigger { get; init; }

    [JsonPropertyName("requests")] public IReadOnlyList<LocalWaitReevaluationRequest> Requests { get; init; } = [];

    /// <summary>人类可读原因（审计用；不得承载发送许可或成功/失败断言）。</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}
