using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 车道任务在存储中排在后续主计划行之后（往车道里添加任务会追加到节点末尾）时，
/// 「车道任务 → 下一行」不能被当成回边进入新一轮：同一轮内的轮次必须保持不变；
/// 计划有循环时，只有走完最后一行才进入下一轮。非编排的自定义路径计划回边仍算新一轮。
/// </summary>
public class CompositionRoundOrderTests
{
    private static WorkflowNode R(string id) => new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id } };

    private static WorkflowDocument LaneThenD(bool loop)
    {
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday);
        return new WorkflowDocument
        {
            Name = "车道后接任务",
            // 存储顺序：车道, D（主计划下一行）, B, C（车道成员，后添加）
            Nodes = [ new() { NodeId = "lane", Kind = "control.lanes", Lanes = new() { Decision = new() { Steps = [c], ExitStepId = c.Id }, YesNodeIds = ["B", "C"], NoNodeIds = [] } }, R("D"), R("B"), R("C") ],
            Loop = loop ? new WorkflowLoop { Mode = "immediate" } : null,
        };
    }

    private static List<(string Id, int Round)> Walk(WorkflowDocument doc, string laneResult, int steps)
    {
        var plan = new WorkflowPlan(doc);
        var cur = plan.FirstOccurrence();
        var seen = new List<(string, int)>();
        for (var i = 0; i < steps && cur is not null; i++)
        {
            seen.Add((cur.NodeId, cur.LoopIteration));
            cur = plan.Next(cur, cur.NodeId == "lane" ? laneResult : "succeeded");
        }
        return seen;
    }

    [Fact]
    public void LaneMembersStoredAfterNextRow_StayInSameRound()
    {
        Assert.Equal([("lane", 0), ("B", 0), ("C", 0), ("D", 0)], Walk(LaneThenD(loop:false), "branchYes", 10));
    }

    [Fact]
    public void EmptyBranch_ContinuesToNextRowInSameRound()
    {
        Assert.Equal([("lane", 0), ("D", 0)], Walk(LaneThenD(loop:false), "branchNo", 10));
    }

    [Fact]
    public void Loop_EntersNextRoundOnlyAfterLastRow()
    {
        Assert.Equal([("lane", 0), ("B", 0), ("C", 0), ("D", 0), ("lane", 1), ("B", 1)], Walk(LaneThenD(loop:true), "branchYes", 6));
    }

    [Fact]
    public void LegacyPathPlan_BackEdgeStillStartsNewRound()
    {
        WorkflowNode P(string id, string next) { var n = R(id); n.Path = new() { Next = next }; n.Strategies.Add(new() { Kind = "flow.route" }); return n; }
        var doc = new WorkflowDocument { Name = "旧路径", Nodes = [P("A", "B"), P("B", "A")] };
        var plan = new WorkflowPlan(doc);
        var a = plan.FirstOccurrence()!;
        var b = plan.Next(a, "succeeded")!;
        var back = plan.Next(b, "succeeded")!;
        Assert.Equal(("B", 0), (b.NodeId, b.LoopIteration));
        Assert.Equal(("A", 1), (back.NodeId, back.LoopIteration));
    }
}
