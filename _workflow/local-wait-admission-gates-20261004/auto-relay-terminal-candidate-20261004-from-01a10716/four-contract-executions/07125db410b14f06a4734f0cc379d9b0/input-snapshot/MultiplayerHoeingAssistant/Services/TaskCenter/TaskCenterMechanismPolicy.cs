using System;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>机制二/三 的错过到点处置（§2②6：**默认 skip＋留痕，不排队无限等待、不补跑**）。</summary>
public enum MissPolicy
{
    /// <summary>本次放弃（不补跑、不排队）。</summary>
    Skip,
    /// <summary>顺延到次日同一时刻（仍不补跑历史轮次）。</summary>
    NextDay,
}

/// <summary>灵活型窗口候选的「特殊原因」（§2③6 初始枚举；**开放集合**，锚点 6——新增原因＝加枚举值，不改判定形状）。</summary>
public enum FlexibleBlockReason
{
    /// <summary>联机批次进行中。</summary>
    OnlineBatchRunning,
    /// <summary>人工暂停。</summary>
    ManualPaused,
    /// <summary>恢复票据有效。</summary>
    ActiveTicket,
    /// <summary>F11 冷却。</summary>
    F11Cooldown,
    /// <summary>系统级抢占源活跃。</summary>
    SystemPreemptActive,
}

/// <summary>灵活型空闲判定的**显式事实输入**（纯函数输入：判定不自行探测环境，I-3 同口径）。</summary>
public sealed class FlexibleWindowFacts
{
    /// <summary>当前有在跑任务（仲裁面全局占用）。</summary>
    public bool ExecutionOccupied { get; set; }
    /// <summary>固定型已声明排程占用该时段（**声明**口径，不预测未知执行时长）。</summary>
    public bool FixedScheduleDeclared { get; set; }
    public bool OnlineBatchRunning { get; set; }
    public bool ManualPaused { get; set; }
    public bool ActiveTicket { get; set; }
    public bool F11Cooldown { get; set; }
    public bool SystemPreemptActive { get; set; }
}

/// <summary>
/// 槲寄生 · **R5.4 机制一/二/三 schema 与引擎消费**（纯函数、无 I/O；§2②3/②6、§2③1/③3/③6 的可测实现）。
/// 设计冻结前置问题（ASTRA 终审 I4）的逐条答案见 R5.2 接线稿 §19（R5.4 入档）；本类即那些答案的**唯一引用点**。
/// **范围如实**：本类只提供**纯函数策略与目录登记**——「触发 kind → 候选（tier/priority/scheduledAt）」的**引擎消费接线**
/// 与既有入口贯通尚未落地（见 §19 残余），不得据本类签署「引擎已消费」或「旧流程运行行为不变」（后者由既有回归承接）。
/// </summary>
public static class TaskCenterMechanismPolicy
{
    /// <summary>机制一：节点级优先级修饰 kind（int32 `priority`）。</summary>
    public const string PriorityStrategyKind = "schedule.priority";
    /// <summary>机制二：时间固定型到点触发 kind（结构性层级＝Fixed）。</summary>
    public const string FixedTriggerKind = "trigger.timeFixed";
    /// <summary>机制三：灵活型窗口触发 kind（结构性层级＝Plan）。</summary>
    public const string FlexibleTriggerKind = "trigger.timeFlexible";

    /// <summary>
    /// 结构性层级映射（§2③1）——**只由根级触发器决定**：`trigger.timeFixed` ⇒ `Fixed`；
    /// `trigger.timeFlexible`／旧 `trigger.time`／缺失 ⇒ `Plan`（**旧流程行为逐字不变**，D3 缺省=现状）。
    /// 系统层不由本函数产生：来源自身不证明 system 授权（由动作类型＋既有调用方身份判定，§2.2）。
    /// </summary>
    public static ArbitrationTier TierOfTrigger(string? triggerKind)
        => string.Equals(triggerKind, FixedTriggerKind, StringComparison.Ordinal)
            ? ArbitrationTier.Fixed
            : ArbitrationTier.Plan;

    /// <summary>
    /// 机制一：节点优先级（§2③3「优先级为**节点级**修饰、链内顺序不受其影响」）——
    /// 取**该节点自身** `schedule.priority` 的 int32（同 kind 多实例取**最后一条**），缺省 `0`；
    /// **不跨节点合并、不取整链最大值、不在链内插队**。
    /// </summary>
    public static int PriorityOfNode(WorkflowNode? node)
    {
        if (node?.Strategies is null) return 0;
        var value = 0;
        foreach (var strategy in node.Strategies)
        {
            if (string.Equals(strategy.Kind, PriorityStrategyKind, StringComparison.Ordinal)
                && strategy.GetInt("priority") is { } parsed)
                value = parsed;
        }
        return value;
    }

    /// <summary>missPolicy 解析（§2②6）：`nextDay` ⇒ 顺延次日；其余（含缺失/未知）⇒ `skip`（**默认保守、不排队**）。</summary>
    public static MissPolicy ParseMissPolicy(string? raw)
        => string.Equals(raw, "nextDay", StringComparison.OrdinalIgnoreCase) ? MissPolicy.NextDay : MissPolicy.Skip;

    /// <summary>
    /// 错过到点的处置（§2②6）：未错过 ⇒ 原时刻；`skip` ⇒ `null`（**本次放弃、不补跑**）；
    /// `nextDay` ⇒ 返回**严格晚于 `now` 的最近一个同刻时刻**（跨多日错过同样直接取未来最近一次，
    /// **偏移语义合同**：按 `DateTimeOffset` 的**固定偏移**解析（保留原计划偏移量）；**地区时区／DST 未实现**（残余见 §19.3）。
    /// **绝不返回已过期时刻、绝不枚举补跑历史轮次**——停机恢复后已过期者不补跑）。
    /// </summary>
    public static DateTimeOffset? ResolveMissedFire(DateTimeOffset scheduled, DateTimeOffset now, MissPolicy policy)
    {
        if (now <= scheduled) return scheduled;
        if (policy != MissPolicy.NextDay) return null;

        var localNow = now.ToOffset(scheduled.Offset);
        var candidate = new DateTimeOffset(localNow.Date + scheduled.TimeOfDay, scheduled.Offset);
        if (candidate <= localNow) candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>
    /// 机制三：灵活型窗口候选的空闲判定（§2③6）——占用 = **当前在跑** ∪ **固定型已声明排程**，
    /// **不预测未知执行时长**；返回 `false` 时给出**特殊原因**（`reason == null` ＝ 仅普通占用，非特殊原因）。
    /// 判定顺序＝枚举序（先系统级抢占源，再票据/F11/人工暂停/联机批次，最后普通占用）。
    /// </summary>
    public static bool IsFlexiblyIdle(FlexibleWindowFacts facts, out FlexibleBlockReason? reason)
    {
        if (facts.SystemPreemptActive) { reason = FlexibleBlockReason.SystemPreemptActive; return false; }
        if (facts.ActiveTicket) { reason = FlexibleBlockReason.ActiveTicket; return false; }
        if (facts.F11Cooldown) { reason = FlexibleBlockReason.F11Cooldown; return false; }
        if (facts.ManualPaused) { reason = FlexibleBlockReason.ManualPaused; return false; }
        if (facts.OnlineBatchRunning) { reason = FlexibleBlockReason.OnlineBatchRunning; return false; }
        if (facts.ExecutionOccupied || facts.FixedScheduleDeclared) { reason = null; return false; }
        reason = null;
        return true;
    }

    /// <summary>
    /// 用来源分隔符（会诊整改）：`|` 与 `%` 百分号转义，避免分段歧义（`a|b` 与 `a` + `b` 不再同形）。
    /// </summary>
    private static string EscapeSegment(string value)
        => value.Replace("%", "%25", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal);

    /// <summary>
    /// 稳定身份兜底（§2③1）——`workflowId + 来源标签 + 身份`：**出现身份（`occ:`）与请求身份（`req:`）分别打标**，
    /// 故二者不会互相同形（会诊整改：原实现 `("wf","req:r1",null)` 与 `("wf",null,"r1")` 会碰撞）。
    /// **两者都缺失 ⇒ 返回 `null`**——调用方必须**响亮拒绝**，不得以空身份兜底（空身份会跨请求碰撞）。
    /// 分段经转义后拼接，确定性可重放；其「跨恢复稳定」仍取决于调用方持久化同一身份（不作为本函数结论）。
    /// </summary>
    public static string? StableFallbackIdentity(string? workflowId, string? triggerOccurrenceId, string? requestIdentity)
    {
        var head = EscapeSegment(workflowId ?? "");
        if (!string.IsNullOrEmpty(triggerOccurrenceId))
            return head + "|occ:" + EscapeSegment(triggerOccurrenceId);
        if (!string.IsNullOrEmpty(requestIdentity))
            return head + "|req:" + EscapeSegment(requestIdentity);
        return null;
    }

    /// <summary>到点**水位**（每个出现身份一条，持久化）：记录该出现身份**已消费的最晚计划时刻（UTC）**。</summary>
    public sealed class FireWatermark
    {
        public string OccurrenceIdentity { get; set; } = "";
        public DateTimeOffset LastFiredAtUtc { get; set; }
    }

    /// <summary>
    /// 到点幂等去重（§2③5/§2②6 的**水位合同**，会诊整改：单一「上次键」不能兑现回拨不重放）：
    /// 计划时刻 **≤ 该出现身份水位** ⇒ 重复（**不参选、不重放**）；**严格晚于水位** ⇒ 新到点（可参选并推进水位）。
    /// **范围如实（二轮会诊收窄）**：本函数**只**承担「同出现身份去重 ＋ 水位单调」——
    /// ①**回拨不重放**：t1 已消费、水位已推进到 t2 后再求值 t1 ⇒ 仍 ≤ 水位 ⇒ 判重复（本函数可独立保证）；
    /// ②**前跳不补跑**：**不由本函数保证**——本函数不接收 `now`，无法判断「晚于水位但已过期」的轮次；
    /// 该性质要求调用方**先按当前时间筛除过期轮次并按 `missPolicy` 处置**（`ResolveMissedFire`）**再**求值去重，
    /// 该**调用顺序与调度接线尚未落地**（残余见 §19.3 ①②）。
    /// </summary>
    public static bool IsDuplicateFire(FireWatermark? watermark, string occurrenceIdentity, DateTimeOffset scheduled)
        => watermark is not null
           && string.Equals(watermark.OccurrenceIdentity, occurrenceIdentity, StringComparison.Ordinal)
           && scheduled.UtcDateTime <= watermark.LastFiredAtUtc.UtcDateTime;

    /// <summary>
    /// 推进水位（**纯函数：不修改输入对象**，会诊整改）——**单调**：同出现身份取更晚者；
    /// 重复到点（≤ 现值）⇒ 返回**等价副本**（幂等、不倒退）；null ⇒ 建立首个水位；
    /// 出现身份不同 ⇒ **不跨身份比较**，返回等价副本并要求调用方按身份**分别持久化**。
    /// 调用方保留旧引用即等于推进前快照，便于「先计算、后提交」与提交失败后的状态隔离。
    /// </summary>
    public static FireWatermark? AdvanceWatermark(FireWatermark? watermark, string occurrenceIdentity, DateTimeOffset scheduled)
    {
        if (watermark is null)
            return new FireWatermark { OccurrenceIdentity = occurrenceIdentity, LastFiredAtUtc = scheduled }; // 首个水位
        if (string.Equals(watermark.OccurrenceIdentity, occurrenceIdentity, StringComparison.Ordinal)
            && scheduled.UtcDateTime > watermark.LastFiredAtUtc.UtcDateTime)
            return new FireWatermark { OccurrenceIdentity = occurrenceIdentity, LastFiredAtUtc = scheduled }; // 仅在更晚时推进
        return new FireWatermark { OccurrenceIdentity = watermark.OccurrenceIdentity, LastFiredAtUtc = watermark.LastFiredAtUtc };
    }
}