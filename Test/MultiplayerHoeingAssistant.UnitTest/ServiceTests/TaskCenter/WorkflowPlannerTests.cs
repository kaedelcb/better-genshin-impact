using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// WorkflowPlanner（R4.4）验收夹具（R4 分解 D9/B5 口径）：
/// 首节点星期不命中第二节点可运行（过滤不阻塞）、跨午夜重求值、惰性推进无业务轮次封顶、
/// 同一资源不同节点挂不同策略实例、trigger.time nextDay、loop scheduled 顺延、
/// 单项拒绝不连坐（D4）、未支持类型阻断、候选激活阻断（D13）、重复节点出现序号独立。
/// </summary>
public class WorkflowPlannerTests
{
    private static readonly DateTimeOffset Wednesday0600 =
        new(2026, 9, 16, 6, 0, 0, TimeSpan.FromHours(8)); // 2026-09-16 是周三

    private static WorkflowNode ResourceNode(string id, string kind, params WorkflowStrategy[] strategies)
        => new()
        {
            NodeId = id,
            Kind = kind,
            Ref = new WorkflowResourceRef { Config = "日常综合", ConfigKey = "日常综合#abc", Revision = "rev-1" },
            Strategies = [.. strategies],
        };

    [Fact]
    public void DeliveryCalendar_ExplicitEmptyWeekdaysDoesNotMeanEveryDay()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.False(WorkflowWeekdayFilter.Matches(Weekdays(), now));
        Assert.True(WorkflowWeekdayFilter.Matches(new WorkflowStrategy { Kind = "condition.weekdays" }, now));
    }

    private static WorkflowStrategy Weekdays(params string[] days)
        => new()
        {
            Kind = "condition.weekdays",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["days"] = System.Text.Json.JsonSerializer.SerializeToElement(days),
                ["dayBoundary"] = System.Text.Json.JsonSerializer.SerializeToElement("localMidnight"),
            },
        };

    [Fact]
    public void WeekdayFilter_FirstNodeMissed_SecondNodeStillRuns()
    {
        var doc = new WorkflowDocument
        {
            Name = "过滤不阻塞",
            Nodes =
            [
                ResourceNode("n-a", "resource.oneDragonConfig", Weekdays("周一", "周五")), // 周三不命中
                ResourceNode("n-b", "resource.oneDragonConfig", Weekdays("周三")),          // 周三命中
            ],
        };
        var plan = new WorkflowPlan(doc);
        var first = plan.FirstOccurrence()!;
        var second = plan.Next(first)!;

        var d1 = plan.EvaluateNode(first, Wednesday0600, singleNativeSupported: false);
        var d2 = plan.EvaluateNode(second, Wednesday0600, singleNativeSupported: false);

        Assert.Equal(NodeGateAction.Skip, d1.Action);   // 过滤跳过
        Assert.Equal(NodeGateAction.Proceed, d2.Action); // 后续节点不受影响
    }

    [Fact]
    public void WeekdayFilter_ReevaluatedAcrossMidnight_NotFrozenAtPlanningTime()
    {
        var doc = new WorkflowDocument
        {
            Name = "跨午夜",
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig", Weekdays("周四"))],
        };
        var plan = new WorkflowPlan(doc);
        var occ = plan.FirstOccurrence()!;

        var lateWednesday = new DateTimeOffset(2026, 9, 16, 23, 30, 0, TimeSpan.FromHours(8));
        var earlyThursday = new DateTimeOffset(2026, 9, 17, 0, 30, 0, TimeSpan.FromHours(8));

        Assert.Equal(NodeGateAction.Skip, plan.EvaluateNode(occ, lateWednesday, false).Action);
        Assert.Equal(NodeGateAction.Proceed, plan.EvaluateNode(occ, earlyThursday, false).Action); // 跨午夜重求值
    }

    [Fact]
    public void LazyLoop_NoBusinessRoundCap_OccurrenceIdentityAdvances()
    {
        var doc = new WorkflowDocument
        {
            Name = "循环",
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig")],
            Loop = new WorkflowLoop { Mode = "immediate" },
        };
        var plan = new WorkflowPlan(doc);

        var occ = plan.FirstOccurrence()!;
        for (var i = 0; i < 100; i++)
        {
            var next = plan.Next(occ);
            Assert.NotNull(next); // 无轮次封顶：业务循环仅由退出条件控制（D9）
            occ = next!;
        }
        Assert.Equal(100, occ.LoopIteration); // 轮次身份递增（§3.4 身份合同）
        Assert.Equal(0, occ.Occurrence);
    }

    [Fact]
    public void NoLoop_EndOfChain_ReturnsNull()
    {
        var doc = new WorkflowDocument
        {
            Name = "单次",
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig")],
        };
        var plan = new WorkflowPlan(doc);
        Assert.Null(plan.Next(plan.FirstOccurrence()!));
    }

    [Fact]
    public void SameResource_DifferentNodes_IndependentStrategyInstances()
    {
        var doc = new WorkflowDocument
        {
            Name = "同资源异策略",
            Nodes =
            [
                ResourceNode("n-a", "resource.oneDragonConfig", Weekdays("周三")), // 同一份配置
                ResourceNode("n-b", "resource.oneDragonConfig", Weekdays("周一")), // 挂不同策略（锚点 4）
            ],
        };
        var plan = new WorkflowPlan(doc);
        var first = plan.FirstOccurrence()!;
        var second = plan.Next(first)!;

        Assert.Equal(NodeGateAction.Proceed, plan.EvaluateNode(first, Wednesday0600, false).Action);
        Assert.Equal(NodeGateAction.Skip, plan.EvaluateNode(second, Wednesday0600, false).Action);
        Assert.NotSame(doc.Nodes[0].Strategies[0], doc.Nodes[1].Strategies[0]); // 独立策略实例（S1 处置）
    }

    [Fact]
    public void DuplicateNodeIds_DistinctOccurrenceIdentities()
    {
        var doc = new WorkflowDocument
        {
            Name = "重复节点",
            Nodes =
            [
                ResourceNode("n-same", "resource.oneDragonConfig"),
                ResourceNode("n-same", "resource.oneDragonConfig"), // 同名不等于同任务
            ],
        };
        var plan = new WorkflowPlan(doc);
        var first = plan.FirstOccurrence()!;
        var second = plan.Next(first)!;

        Assert.Equal(0, first.Occurrence);
        Assert.Equal(1, second.Occurrence);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SingleTaskNode_AcceptedAfterNativeOpen_NoCollectivePunishment()
    {
        var doc = new WorkflowDocument
        {
            Name = "含单项",
            Nodes =
            [
                ResourceNode("n-dragon", "resource.oneDragonConfig"),
                ResourceNode("n-single", "resource.singleTask"),
            ],
        };
        var plan = new WorkflowPlan(doc);

        // 预检：流程可执行（不连坐），单项能力已开放（2026-10-04）
        var preflight = plan.Preflight(singleNativeSupported: true);
        Assert.True(preflight.Executable);
        Assert.DoesNotContain(preflight.Warnings, w => w.Contains("task.single.native"));

        var dragon = plan.FirstOccurrence()!;
        var single = plan.Next(dragon)!;
        Assert.Equal(NodeGateAction.Proceed, plan.EvaluateNode(dragon, Wednesday0600, true).Action);
        var gate = plan.EvaluateNode(single, Wednesday0600, true);
        Assert.Equal(NodeGateAction.Proceed, gate.Action); // 单项能力已开放，允许执行
    }

    [Fact]
    public void UnsupportedKinds_BlockExecution_ButPreviewable()
    {
        var doc = new WorkflowDocument
        {
            Name = "未来类型",
            Nodes = [ResourceNode("n-x", "resource.hologram")],
        };
        var plan = new WorkflowPlan(doc);
        var preflight = plan.Preflight(singleNativeSupported: true);

        Assert.False(preflight.Executable); // D3 第二级：阻止执行
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("resource.hologram"));
        Assert.NotEmpty(plan.UnsupportedKinds); // 但文档可加载可预览
    }

    [Fact]
    public void CandidateActivation_BlocksProductionExecution()
    {
        var doc = new WorkflowDocument
        {
            Name = "候选",
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig")],
            Activation = new WorkflowActivation { Status = "candidate-ready" },
        };
        var preflight = new WorkflowPlan(doc).Preflight(singleNativeSupported: true);
        Assert.False(preflight.Executable); // D13：候选只可预览
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("candidate-ready"));
    }

    [Fact]
    public void TriggerTime_NextDayPolicy_TodayFutureOrTomorrow()
    {
        var trigger = new WorkflowTrigger
        {
            Kind = "trigger.time",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:30"),
                ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("nextDay"),
            },
        };

        var before = new DateTimeOffset(2026, 9, 16, 5, 0, 0, TimeSpan.FromHours(8));
        var after = new DateTimeOffset(2026, 9, 16, 7, 0, 0, TimeSpan.FromHours(8));

        var today = WorkflowTriggerSchedule.NextFire(trigger, before, out var r1);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 6, 30, 0, TimeSpan.FromHours(8)), today);
        Assert.Null(r1);

        var tomorrow = WorkflowTriggerSchedule.NextFire(trigger, after, out _);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 6, 30, 0, TimeSpan.FromHours(8)), tomorrow); // 当日已过顺延明天
    }

    [Fact]
    public void TriggerTime_UnknownMissPolicy_LoudNull()
    {
        var trigger = new WorkflowTrigger
        {
            Kind = "trigger.time",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("06:30"),
                ["missPolicy"] = System.Text.Json.JsonSerializer.SerializeToElement("catchUp15min"),
            },
        };
        Assert.Null(WorkflowTriggerSchedule.NextFire(trigger, Wednesday0600, out var reason));
        Assert.Contains("missPolicy", reason); // 不套用联机 15 分钟宽限（§7.3）
    }

    [Fact]
    public void LoopScheduled_PastCycleTime_SkipsToNextDay()
    {
        var loop = new WorkflowLoop
        {
            Mode = "scheduled",
            Params = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("04:00"),
                ["skipAcrossDays"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            },
        };
        var next = WorkflowLoopSchedule.NextRoundStart(loop, Wednesday0600, out var reason);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 4, 0, 0, TimeSpan.FromHours(8)), next); // 每轮开始重新定义
        Assert.Null(reason);
    }

    [Fact]
    public void LoopScheduled_MissedStart_SkipAcrossDaysFormula()
    {
        // D9/B9 定案公式：轮次窗口=[本地起点,下一起点)；错过起点 → true 排下一起点（不补跑），false 窗口内立即补跑
        WorkflowLoop MakeLoop(bool? skip) => new()
        {
            Mode = "scheduled",
            Params = skip is null
                ? new Dictionary<string, System.Text.Json.JsonElement>
                { ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("04:00") }
                : new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["time"] = System.Text.Json.JsonSerializer.SerializeToElement("04:00"),
                    ["skipAcrossDays"] = System.Text.Json.JsonSerializer.SerializeToElement(skip.Value),
                },
        };

        // 起点未到（03:00 → 今日 04:00）：与 skipAcrossDays 无关
        var early = new DateTimeOffset(2026, 9, 16, 3, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 4, 0, 0, TimeSpan.FromHours(8)),
            WorkflowLoopSchedule.NextRoundStart(MakeLoop(false), early, out _));

        // 已错过起点（06:00 > 04:00）：true=排下一起点；false=立即补跑；缺省=true（保守不补跑）
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 4, 0, 0, TimeSpan.FromHours(8)),
            WorkflowLoopSchedule.NextRoundStart(MakeLoop(true), Wednesday0600, out _));
        Assert.Equal(Wednesday0600, WorkflowLoopSchedule.NextRoundStart(MakeLoop(false), Wednesday0600, out _));
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 4, 0, 0, TimeSpan.FromHours(8)),
            WorkflowLoopSchedule.NextRoundStart(MakeLoop(null), Wednesday0600, out _));
    }

    [Fact]
    public void TryLocate_StableIdentity_SurvivesInsertion()
    {
        var doc = new WorkflowDocument
        {
            Name = "定位",
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig"), ResourceNode("n-b", "resource.oneDragonConfig")],
        };
        var plan = new WorkflowPlan(doc);

        Assert.True(plan.TryLocate("n-b", 0, 2, out var occ));
        Assert.Equal(1, occ.SequenceIndex); // B1：按身份定位坐标
        Assert.Equal(2, occ.LoopIteration);
        Assert.False(plan.TryLocate("n-x", 0, 0, out _));

        // 插入节点后身份重定位（旧 SequenceIndex 不直接寻址新定义）
        doc.Nodes.Insert(0, ResourceNode("n-z", "resource.oneDragonConfig"));
        var newPlan = new WorkflowPlan(doc);
        Assert.True(newPlan.TryLocate("n-b", 0, 2, out var moved));
        Assert.Equal(2, moved.SequenceIndex);
    }

    private static WorkflowStrategy Account(string? uid)
    {
        var s = new WorkflowStrategy { Kind = "prerequisite.account" };
        if (uid is not null)
            s.Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(uid) };
        return s;
    }

    private static WorkflowStrategy Redeem(string? uid)
    {
        var s = new WorkflowStrategy { Kind = "prerequisite.redeemCode" };
        if (uid is not null)
            s.Params = new Dictionary<string, System.Text.Json.JsonElement>
            { ["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(uid) };
        return s;
    }

    [Fact]
    public void Preflight_DuplicateAccount_Blocks()
    {
        // R4.8 §4.5（一轮 I2）：每节点至多一个 prerequisite.account
        var doc = new WorkflowDocument
        {
            Name = "双账号",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig", Account("10001"), Account("10002"))],
        };
        var preflight = new WorkflowPlan(doc).Preflight(singleNativeSupported: false);

        Assert.False(preflight.Executable);
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("至多一个"));
    }

    [Fact]
    public void Preflight_AccountBlankUid_Blocks()
    {
        // 存在账号策略即要求完整身份——不得当无账号策略省略 expectedUid
        var doc = new WorkflowDocument
        {
            Name = "空账号",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig", Account(null))],
        };
        var preflight = new WorkflowPlan(doc).Preflight(singleNativeSupported: false);

        Assert.False(preflight.Executable);
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("prerequisite.account 缺少 uid"));
    }

    [Fact]
    public void Preflight_RedeemUidConflictsAccountUid_Blocks()
    {
        // 账号歧义不猜：兑换码显式 uid 与账号 uid 不一致 → 阻断
        var doc = new WorkflowDocument
        {
            Name = "身份冲突",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig", Account("10001"), Redeem("10002"))],
        };
        var preflight = new WorkflowPlan(doc).Preflight(singleNativeSupported: false);

        Assert.False(preflight.Executable);
        Assert.Contains(preflight.BlockingReasons, r => r.Contains("不一致"));
    }

    [Fact]
    public void Preflight_RedeemUidMatchesAccountUid_Passes()
    {
        var doc = new WorkflowDocument
        {
            Name = "身份一致",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [ResourceNode("n-a", "resource.oneDragonConfig", Account("10001"), Redeem("10001"))],
        };
        var preflight = new WorkflowPlan(doc).Preflight(singleNativeSupported: false);

        Assert.True(preflight.Executable);
    }
}
