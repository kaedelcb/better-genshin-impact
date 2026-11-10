using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 任务中心流程图「走过的路径」投影：从最近一次运行的节点结果与流程步骤检查点推出本轮运行态。
/// 验证约定行为：只取该计划最近创建的运行、只看本轮；车道按判断结果选路线；
/// 活动运行的游标节点显示执行中；内部判断执行完后按子链检查点推出走了哪一支；旧轮次与其他计划不串入。
/// </summary>
public class TaskCenterRunTraceTests
{
    private static JsonElement J(string s) => JsonSerializer.SerializeToElement(s);

    private static (WorkflowDocument Doc, StartupStep Cond, StartupStep Yes, StartupStep No) Plan()
    {
        var yes = new StartupStep { Kind = StartupStepKinds.Wait, NodeType = "action" };
        var no = new StartupStep { Kind = StartupStepKinds.Wait, NodeType = "action" };
        var cond = new StartupStep { Kind = StartupStepKinds.Weekday, NodeType = "condition", TrueSteps = [yes], FalseSteps = [no] };
        var exit = new StartupStep { Kind = StartupStepKinds.Weekday, NodeType = "condition" };
        var doc = new WorkflowDocument
        {
            WorkflowId = "wf-1",
            Nodes =
            [
                new() { NodeId = "a", Kind = "resource.configGroup" },
                new() { NodeId = "f", Kind = "control.flow", Flow = new() { Steps = [cond] } },
                new() { NodeId = "l", Kind = "control.lanes", Lanes = new() { Decision = new() { Steps = [exit], ExitStepId = exit.Id }, YesNodeIds = ["y"], NoNodeIds = ["n"] } },
                new() { NodeId = "y", Kind = "resource.configGroup" },
                new() { NodeId = "n", Kind = "resource.configGroup" },
            ],
        };
        return (doc, cond, yes, no);
    }

    [Fact]
    public void NoRun_IsEmpty()
    {
        var (doc, _, _, _) = Plan();
        var trace = TaskCenterRunTrace.Build(doc, [new WorkflowRunRecord { RunId = "x", WorkflowId = "other" }]);
        Assert.False(trace.HasRun);
        Assert.Equal(NodeRunState.None, trace.Node("a"));
    }

    [Fact]
    public void CurrentRound_LaneChoiceAndCursor()
    {
        var (doc, cond, yes, _) = Plan();
        var old = new WorkflowRunRecord { RunId = "old", WorkflowId = "wf-1", CreatedAt = DateTimeOffset.Now.AddDays(-1), State = WorkflowRunState.Succeeded,
            NodeOutcomes = [new() { NodeId = "l", LoopIteration = 0, Result = "branchNo" }] };
        var run = new WorkflowRunRecord
        {
            RunId = "new", WorkflowId = "wf-1", CreatedAt = DateTimeOffset.Now, State = WorkflowRunState.Running,
            Cursor = new() { NodeId = "y", LoopIteration = 1 },
            NodeOutcomes =
            [
                new() { NodeId = "a", LoopIteration = 0, Result = "failed" },
                new() { NodeId = "a", LoopIteration = 1, Result = "succeeded" },
                new() { NodeId = "f", LoopIteration = 1, Result = "succeeded" },
                new() { NodeId = "l", LoopIteration = 1, Result = "branchYes" },
            ],
            ExtensionData = new()
            {
                [$"taskFlowStep:f:1:{cond.Id}"] = J("done"),
                [$"taskFlowStep:f:1:{yes.Id}"] = J("done"),
                [$"taskFlowStep:f:0:{cond.Id}"] = J("conditionNo"),
            },
        };
        var trace = TaskCenterRunTrace.Build(doc, [old, run]);
        Assert.Equal("new", trace.RunId);
        Assert.Equal(1, trace.Round);
        Assert.Equal(NodeRunState.Success, trace.Node("a"));
        Assert.Equal(NodeRunState.Running, trace.Node("y"));
        Assert.True(trace.LaneChoice(doc, doc.Nodes[2]));
        // 判断检查点已改写为 done，按「是」子链留下的检查点推出走了「是」。
        Assert.Equal(NodeRunState.CondTrue, trace.Step("f", cond.Id));
        Assert.Equal(NodeRunState.Success, trace.Step("f", yes.Id));
    }

    [Fact]
    public void StoppedRun_ExecutingStepIsNotShownAsRunning()
    {
        var (doc, cond, _, no) = Plan();
        var run = new WorkflowRunRecord
        {
            RunId = "r", WorkflowId = "wf-1", State = WorkflowRunState.Cancelled, Cursor = new() { NodeId = "f" },
            ExtensionData = new() { [$"taskFlowStep:f:0:{cond.Id}"] = J("conditionNo"), [$"taskFlowStep:f:0:{no.Id}"] = J("executing") },
        };
        var trace = TaskCenterRunTrace.Build(doc, [run]);
        Assert.Equal(NodeRunState.None, trace.Node("f"));
        Assert.Equal(NodeRunState.CondFalse, trace.Step("f", cond.Id));
        Assert.Equal(NodeRunState.Failed, trace.Step("f", no.Id));
        Assert.Null(trace.LaneChoice(doc, doc.Nodes[2]));
    }
}
