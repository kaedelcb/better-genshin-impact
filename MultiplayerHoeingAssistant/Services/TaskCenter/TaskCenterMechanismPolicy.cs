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
/// 设计冻结前置问题（ASTRA 终审 I4）的逐条答案见 R5.2 接线稿 §19；本类即那些答案的**唯一引用点**。
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
    /// `nextDay` ⇒ 次日同刻。**任何分支都不枚举补跑历史轮次**（停机恢复后已过期者不补跑）。
    /// </summary>
    public static DateTimeOffset? ResolveMissedFire(DateTimeOffset scheduled, DateTimeOffset now, MissPolicy policy)
    {
        if (now <= scheduled) return scheduled;
        return policy == MissPolicy.NextDay ? scheduled.AddDays(1) : null;
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
    /// 稳定身份兜底（§2③1）——`workflowId + 触发出现身份`；出现身份缺失（手工/v2 无计划时刻候选）时
    /// 以**请求身份**确定性派生，使身份**跨恢复稳定、确定性可重放**。
    /// </summary>
    public static string StableFallbackIdentity(string workflowId, string? triggerOccurrenceId, string? requestIdentity)
        => workflowId + "|" + (string.IsNullOrEmpty(triggerOccurrenceId)
            ? "req:" + (requestIdentity ?? "")
            : triggerOccurrenceId);

    /// <summary>
    /// 到点键（时钟前跳/回拨的去重基准，§2③5/§2②6）：`出现身份 + 计划时刻(UTC, 往返格式)`——
    /// 与墙钟读取次数无关，故**回拨不产生重放**、**前跳的多轮错过只取最近一次**（不枚举补跑）。
    /// </summary>
    public static string FireKeyOf(string occurrenceIdentity, DateTimeOffset scheduled)
        => occurrenceIdentity + "@" + scheduled.UtcDateTime.ToString("O");

    /// <summary>同一到点键是否已参选过（同键至多参选一次＝幂等去重；去重依据是**持久化键**而非墙钟）。</summary>
    public static bool IsDuplicateFire(string occurrenceIdentity, DateTimeOffset scheduled, string? lastFiredKey)
        => string.Equals(lastFiredKey, FireKeyOf(occurrenceIdentity, scheduled), StringComparison.Ordinal);
}