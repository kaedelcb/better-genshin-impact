using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.4 机制一/二/三 schema 与引擎消费**（纯函数夹具，owner 0 点击）：
/// kind 登记与 fail-closed 检出、结构性层级映射（旧流程缺省不变）、节点级优先级归一化、
/// missPolicy（默认 skip 不补跑）、灵活型空闲判定与特殊原因、到点幂等去重（时钟前跳/回拨）与稳定身份兜底。
/// </summary>
public class R54MechanismSchemaTests
{
    private static Dictionary<string, JsonElement> Params(string json)
        => JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static WorkflowNode Node(string id, params WorkflowStrategy[] strategies)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig", Strategies = strategies.ToList() };

    private static WorkflowStrategy Priority(int value)
        => new() { Kind = TaskCenterMechanismPolicy.PriorityStrategyKind, Params = Params("{\"priority\":" + value + "}") };

    private static WorkflowDocument Doc(params WorkflowNode[] nodes) => new()
    {
        Name = "夹具",
        Nodes = nodes.ToList(),
        Triggers = [new WorkflowTrigger { Kind = "trigger.time", Params = Params("{\"at\":\"04:00\"}") }],
        Terminal = [new WorkflowTerminalAction { Kind = "terminal.completionAction" }],
    };

    [Fact]
    public void Catalog_RegistersMechanismKinds()
    {
        Assert.Contains(TaskCenterMechanismPolicy.PriorityStrategyKind, WorkflowKindCatalog.StrategyKinds);
        Assert.Contains(TaskCenterMechanismPolicy.FixedTriggerKind, WorkflowKindCatalog.TriggerKinds);
        Assert.Contains(TaskCenterMechanismPolicy.FlexibleTriggerKind, WorkflowKindCatalog.TriggerKinds);
    }

    /// <summary>D3 缺省=现状：不含新 kind 的旧流程**不被检出**（可执行路径不回归）。</summary>
    [Fact]
    public void UnsupportedKinds_LegacyDocument_NotBlocked()
        => Assert.Empty(WorkflowKindCatalog.FindUnsupportedKinds(Doc(Node("n-1"))));

    /// <summary>D3 第二级 fail-closed：未登记 kind 被检出（可预览、阻止执行、留痕）。</summary>
    [Fact]
    public void UnsupportedKinds_UnknownKind_DetectedForFailClosed()
    {
        var doc = Doc(Node("n-1", new WorkflowStrategy { Kind = "schedule.priorityDraft" }));
        Assert.Contains("strategy:schedule.priorityDraft", WorkflowKindCatalog.FindUnsupportedKinds(doc));
    }

    [Theory]
    [InlineData("trigger.timeFixed", ArbitrationTier.Fixed)]
    [InlineData("trigger.timeFlexible", ArbitrationTier.Plan)]
    [InlineData("trigger.time", ArbitrationTier.Plan)] // 旧触发器：层级不变（缺省=现状）
    [InlineData(null, ArbitrationTier.Plan)]            // 手工/v2 无计划时刻候选
    public void Tier_FixedTriggerIsFixed_OthersPlan(string? triggerKind, ArbitrationTier expected)
        => Assert.Equal(expected, TaskCenterMechanismPolicy.TierOfTrigger(triggerKind));

    [Fact]
    public void Priority_NodeLocalOnly_DefaultsZero()
    {
        Assert.Equal(5, TaskCenterMechanismPolicy.PriorityOfNode(Node("n-1", Priority(5))));
        Assert.Equal(-3, TaskCenterMechanismPolicy.PriorityOfNode(Node("n-2", Priority(-3))));
        Assert.Equal(0, TaskCenterMechanismPolicy.PriorityOfNode(Node("n-3"))); // 缺省 0
        Assert.Equal(0, TaskCenterMechanismPolicy.PriorityOfNode(null));
        // 非数值参数被忽略（严格读取，不猜）
        var bad = new WorkflowStrategy { Kind = TaskCenterMechanismPolicy.PriorityStrategyKind, Params = Params("{\"priority\":\"high\"}") };
        Assert.Equal(0, TaskCenterMechanismPolicy.PriorityOfNode(Node("n-4", bad)));
    }

    /// <summary>含两个不同节点优先级的**最小合法定义**：归一化为**各自节点的值**（不合并、不取最大值）。</summary>
    [Fact]
    public void MinimalDefinition_TwoNodePriorities_NormalizePerNode()
    {
        var a = Node("n-a", Priority(7));
        var b = Node("n-b", Priority(1));
        var doc = Doc(a, b);

        Assert.Empty(WorkflowKindCatalog.FindUnsupportedKinds(doc)); // 新 kind 已登记 ⇒ 不阻断
        Assert.Equal(7, TaskCenterMechanismPolicy.PriorityOfNode(doc.Nodes[0]));
        Assert.Equal(1, TaskCenterMechanismPolicy.PriorityOfNode(doc.Nodes[1]));
        Assert.NotEqual(TaskCenterMechanismPolicy.PriorityOfNode(doc.Nodes[0]), TaskCenterMechanismPolicy.PriorityOfNode(doc.Nodes[1]));
    }

    [Fact]
    public void MissPolicy_DefaultSkipNoBackfill_NextDayDefers()
    {
        var scheduled = new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.FromHours(8));
        var now = scheduled.AddHours(3);

        Assert.Equal(MissPolicy.Skip, TaskCenterMechanismPolicy.ParseMissPolicy(null));
        Assert.Equal(MissPolicy.Skip, TaskCenterMechanismPolicy.ParseMissPolicy("skip"));
        Assert.Equal(MissPolicy.Skip, TaskCenterMechanismPolicy.ParseMissPolicy("unknown"));
        Assert.Equal(MissPolicy.NextDay, TaskCenterMechanismPolicy.ParseMissPolicy("nextDay"));

        Assert.Equal(scheduled, TaskCenterMechanismPolicy.ResolveMissedFire(scheduled, scheduled.AddMinutes(-1), MissPolicy.Skip));
        Assert.Null(TaskCenterMechanismPolicy.ResolveMissedFire(scheduled, now, MissPolicy.Skip));            // 不补跑

        var next = TaskCenterMechanismPolicy.ResolveMissedFire(scheduled, now, MissPolicy.NextDay);
        Assert.Equal(scheduled.AddDays(1), next);

        // **跨多日错过**（会诊整改）：只取「严格晚于 now 的最近同刻」，**不得返回已过期时刻**、不补跑历史轮次。
        var multiDayNow = scheduled.AddDays(3).AddHours(3);
        var resolved = TaskCenterMechanismPolicy.ResolveMissedFire(scheduled, multiDayNow, MissPolicy.NextDay);
        Assert.NotNull(resolved);
        Assert.True(resolved!.Value > multiDayNow, "nextDay 解析结果必须严格晚于 now");
        Assert.Equal(new DateTimeOffset(multiDayNow.Date.AddDays(1) + scheduled.TimeOfDay, scheduled.Offset), resolved.Value);
    }

    [Theory]
    [InlineData("SystemPreemptActive", FlexibleBlockReason.SystemPreemptActive)]
    [InlineData("ActiveTicket", FlexibleBlockReason.ActiveTicket)]
    [InlineData("F11Cooldown", FlexibleBlockReason.F11Cooldown)]
    [InlineData("ManualPaused", FlexibleBlockReason.ManualPaused)]
    [InlineData("OnlineBatchRunning", FlexibleBlockReason.OnlineBatchRunning)]
    public void FlexibleIdle_SpecialReasons_BlockedWithReason(string flag, FlexibleBlockReason expected)
    {
        var facts = new FlexibleWindowFacts();
        typeof(FlexibleWindowFacts).GetProperty(flag)!.SetValue(facts, true);

        Assert.False(TaskCenterMechanismPolicy.IsFlexiblyIdle(facts, out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void FlexibleIdle_PlainOccupancy_BlockedWithoutSpecialReason()
    {
        Assert.False(TaskCenterMechanismPolicy.IsFlexiblyIdle(new FlexibleWindowFacts { ExecutionOccupied = true }, out var occupied));
        Assert.Null(occupied); // 普通占用＝空闲判据本身，不是「特殊原因」

        Assert.False(TaskCenterMechanismPolicy.IsFlexiblyIdle(new FlexibleWindowFacts { FixedScheduleDeclared = true }, out var declared));
        Assert.Null(declared); // 固定型**已声明**排程（不预测未知执行时长）

        Assert.True(TaskCenterMechanismPolicy.IsFlexiblyIdle(new FlexibleWindowFacts(), out var idle));
        Assert.Null(idle);
    }

    /// <summary>
    /// **到点水位去重（会诊整改：由「单个上次键」改为每出现身份单调水位）**：①同到点至多参选一次；
    /// ②**回拨不重放**——t1 已消费、水位已推进到 t2 后再求值 t1 仍判重复；③**前跳不补跑**——旧轮次全部 ≤ 水位 ⇒ 重复；
    /// ④水位**单调**（重复消费不倒退）。
    /// </summary>
    [Fact]
    public void FireDedup_WatermarkMonotonic_RollbackNoReplay_ForwardJumpNoBackfill()
    {
        var occurrence = "wf-1|occ:trigger-2026-09-21";
        var t1 = new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.FromHours(8));
        var t2 = t1.AddDays(1);
        var t3 = t1.AddDays(3); // 时钟前跳：t2 被跳过

        Assert.False(TaskCenterMechanismPolicy.IsDuplicateFire(null, occurrence, t1)); // 首轮到点：新

        var wm = TaskCenterMechanismPolicy.AdvanceWatermark(null, occurrence, t1);
        Assert.NotNull(wm);
        Assert.True(TaskCenterMechanismPolicy.IsDuplicateFire(wm, occurrence, t1));     // 同到点：重复
        Assert.False(TaskCenterMechanismPolicy.IsDuplicateFire(wm, occurrence, t2));    // 更晚到点：新

        wm = TaskCenterMechanismPolicy.AdvanceWatermark(wm, occurrence, t2);
        Assert.Equal(t2.UtcDateTime, wm!.LastFiredAtUtc.UtcDateTime);

        // ② 回拨：水位在 t2，重放 t1 ⇒ 仍 ≤ 水位 ⇒ 判重复（**不重放**）
        Assert.True(TaskCenterMechanismPolicy.IsDuplicateFire(wm, occurrence, t1));

        // ③ 前跳跳过 t2：t3 为更晚到点（新），而 t2 的补跑尝试仍被水位判为重复（**不补跑**）
        Assert.False(TaskCenterMechanismPolicy.IsDuplicateFire(wm, occurrence, t3));
        wm = TaskCenterMechanismPolicy.AdvanceWatermark(wm, occurrence, t3);
        Assert.True(TaskCenterMechanismPolicy.IsDuplicateFire(wm, occurrence, t2));

        // ④ 单调：重复消费更早到点不倒退水位
        var same = TaskCenterMechanismPolicy.AdvanceWatermark(wm, occurrence, t1);
        Assert.Equal(t3.UtcDateTime, same!.LastFiredAtUtc.UtcDateTime);

        // 身份不同 ⇒ 不跨身份比较（调用方按身份分表持久化）
        Assert.False(TaskCenterMechanismPolicy.IsDuplicateFire(wm, "wf-1|occ:other", t1));
    }

    /// <summary>
    /// **稳定身份兜底（会诊整改）**：来源**分别打标**（`occ:`／`req:`）故不会互相同形；分段转义避免 `|` 歧义；
    /// **两者都缺失 ⇒ `null`**（调用方须响亮拒绝，不得以空身份兜底）。
    /// </summary>
    [Fact]
    public void StableFallback_SourceTagged_NoCollision_RefusesWhenIdentityMissing()
    {
        var withOccurrence = TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "occ-1", "req-1");
        Assert.Equal("wf-1|occ:occ-1", withOccurrence);
        Assert.Equal(withOccurrence, TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "occ-1", "req-OTHER"));

        var manual = TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", null, "req-1");
        Assert.Equal("wf-1|req:req-1", manual);
        Assert.Equal(manual, TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "", "req-1")); // 空串与缺失同口径

        // 会诊反例：原实现下两者同形（`wf-1|req:r1`）——现按来源打标 ⇒ 不再碰撞
        Assert.NotEqual(
            TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "req:r1", null),
            TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", null, "r1"));
        Assert.Equal("wf-1|occ:req:r1", TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "req:r1", null));

        // 分段歧义：`|` 被转义 ⇒ 不同组合不同形
        Assert.NotEqual(
            TaskCenterMechanismPolicy.StableFallbackIdentity("a|b", "c", null),
            TaskCenterMechanismPolicy.StableFallbackIdentity("a", "b|c", null));

        // 两者皆缺 ⇒ 拒绝（null）
        Assert.Null(TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", null, null));
        Assert.Null(TaskCenterMechanismPolicy.StableFallbackIdentity("wf-1", "", ""));
    }
}