using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// 任务中心跳转（Docs/design/taskcenter-jump-plan-20261111.md 第 1 阶段）：
/// 流程整行、车道每条路线可设置「执行完后跳到 #N」，只能往后跳到主计划行。
/// 用真实执行器验证执行顺序、轮次、暂停恢复、运行中修改目标；另验证校验、旧版保护标记与旧计划不变。
/// </summary>
public sealed class CompositionJumpTests
{
    private static WorkflowNode R(string id) => new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id } };
    private static WorkflowNode Flow(string id, string? jumpTo = null, bool disabled = false)
    {
        var node = new WorkflowNode { NodeId = id, Kind = "control.flow",
            Flow = new() { Name = "流程" + id, Steps = [Wait0()], JumpTo = jumpTo } };
        if (disabled) { node.ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) }; }
        return node;
    }
    private static StartupStep Wait0() { var w = StartupStepKinds.Create(StartupStepKinds.Wait); w.WaitSeconds = 0; return w; }
    private static WorkflowNode Lane(string id, string[] yes, string[] no, string? yesJump = null, string? noJump = null)
    {
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday); c.Weekdays = [6, 7]; // 周末走「满足」
        return new() { NodeId = id, Kind = "control.lanes", Lanes = new()
        {
            Name = "车道" + id, Decision = new() { Steps = [c], ExitStepId = c.Id },
            YesNodeIds = [.. yes], NoNodeIds = [.. no], YesJumpTo = yesJump, NoJumpTo = noJump
        } };
    }

    // ---------------- 执行顺序 ----------------

    [Fact]
    public async Task FlowJumpSkipsRowsInTheSameRound()
    {
        using var s = new State();
        var doc = new WorkflowDocument { Name = "流程跳转", Nodes = [R("A"), Flow("F", jumpTo: "D"), R("B"), R("C"), R("D")] };
        s.Save(doc);
        var done = await s.Runner().StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { "A", "D" }, s.Port.Visited);
        Assert.All(done.NodeOutcomes, o => Assert.Equal(0, o.LoopIteration));
        Assert.DoesNotContain(done.NodeOutcomes, o => o.NodeId is "B" or "C");
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("filtered")]
    public async Task FlowThatDoesNotRunDoesNotJump(string why)
    {
        using var s = new State();
        var flow = Flow("F", jumpTo: "D", disabled: why == "disabled");
        if (why == "filtered") flow.TimeWindow = new() { Start = "03:00", End = "04:00" };
        var doc = new WorkflowDocument { Name = "未执行不跳", NodeTimeWindowEnabled = why == "filtered", Nodes = [R("A"), flow, R("B"), R("D")] };
        s.Save(doc);
        var done = await s.Runner().StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { "A", "B", "D" }, s.Port.Visited);
        Assert.Contains(done.NodeOutcomes, o => o.NodeId == "F" && o.Result is "skippedFilter" or "skippedUser");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaneRoutesCanJumpAndMeetAtTheSameRow(bool weekend)
    {
        using var s = new State();
        // 两条路线各做自己的任务，最后都接到 D，跳过 X。
        var doc = new WorkflowDocument { Name = "两路汇合", Nodes = [Lane("L", ["Y"], ["N"], yesJump: "D", noJump: "D"), R("X"), R("D"), R("Y"), R("N")] };
        s.Save(doc);
        var done = await s.Runner(weekend).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { weekend ? "Y" : "N", "D" }, s.Port.Visited);
        Assert.All(done.NodeOutcomes, o => Assert.Equal(0, o.LoopIteration));
    }

    [Fact]
    public async Task EmptyLaneRouteJumpsDirectlyAndOtherRouteContinues()
    {
        using var s = new State();
        // 不满足条件时跳过 B、C；满足时照常往下。
        var doc = new WorkflowDocument { Name = "空路线跳转", Nodes = [Lane("L", [], [], noJump: "D"), R("B"), R("C"), R("D")] };
        s.Save(doc);
        var weekday = await s.Runner(weekend: false).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "D" }, s.Port.Visited);
        s.Port = new Port();
        var weekend = await s.Runner(weekend: true).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, weekday.State);
        Assert.Equal(WorkflowRunState.Succeeded, weekend.State);
        Assert.Equal(new[] { "B", "C", "D" }, s.Port.Visited);
    }

    [Fact]
    public async Task JumpToLegacyPairedLaneStillEvaluatesItsCondition()
    {
        using var s = new State();
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday); c.Weekdays = [6, 7];
        var doc = new WorkflowDocument { Name = "跳到旧配对车道", Nodes = [
            Flow("F", jumpTo: "old"), R("B"),
            new() { NodeId = "judge", Kind = "control.flow", Flow = new() { Name = "旧判断", Steps = [c], ExitStepId = c.Id } },
            new() { NodeId = "old", Kind = "control.lanes", Lanes = new() { Name = "旧车道", SourceFlowNodeId = "judge", YesNodeIds = ["Y"], NoNodeIds = ["N"] } },
            R("Y"), R("N")] };
        s.Save(doc);
        var done = await s.Runner(weekend: true).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { "Y" }, s.Port.Visited);
        Assert.Equal("branchYes", done.NodeOutcomes.Single(o => o.NodeId == "judge").Result);
    }

    [Fact]
    public async Task LoopPlanStillStartsNextRoundOnlyAfterTheLastRow()
    {
        using var s = new State();
        var doc = new WorkflowDocument { Name = "循环跳转", Loop = new() { Mode = "immediate" }, Nodes = [Flow("F", jumpTo: "D"), R("B"), R("D")] };
        var plan = new WorkflowPlan(doc);
        var seen = new List<(string, int)>();
        var cur = plan.FirstOccurrence();
        for (var i = 0; i < 4 && cur is not null; i++) { seen.Add((cur.NodeId, cur.LoopIteration)); cur = plan.Next(cur, "succeeded"); }
        Assert.Equal([("F", 0), ("D", 0), ("F", 1), ("D", 1)], seen);
    }

    // ---------------- 暂停 / 恢复 / 运行中修改 ----------------

    [Fact]
    public async Task JumpSurvivesPauseResumeAndDoesNotBackfillSkippedRows()
    {
        using var s = new State();
        var doc = new WorkflowDocument { Name = "跳转后暂停", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("C"), R("D"), R("E")] };
        s.Save(doc);
        var first = s.Runner();
        s.Port.OnSend = r => { if (r.Occurrence.NodeId == "D") first.RequestAction(r.Run.RunId, WorkflowRunAction.Pause); };
        var paused = await first.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Paused, paused.State);
        Assert.Equal(("E", 0), (paused.Cursor!.NodeId, paused.Cursor.LoopIteration));
        s.Port = new Port();
        // 新的执行器实例 = 助手重启后恢复。
        var done = await s.Runner().ResumeAsync(paused.RunId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { "E" }, s.Port.Visited);
        Assert.DoesNotContain(done.NodeOutcomes, o => o.NodeId is "B" or "C");
    }

    [Fact]
    public async Task ChangingJumpTargetWhilePausedKeepsDecidedCursor()
    {
        using var s = new State();
        var doc = new WorkflowDocument { Name = "暂停时改目标", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("C"), R("D"), R("E")] };
        s.Save(doc);
        var first = s.Runner();
        // 流程执行完、跳到 D 后暂停：游标已经定在 D 的下一行 E。
        s.Port.OnSend = r => { if (r.Occurrence.NodeId == "D") first.RequestAction(r.Run.RunId, WorkflowRunAction.Pause); };
        var paused = await first.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("E", paused.Cursor!.NodeId);
        var snapshot = s.Flows.LoadSnapshot(doc.WorkflowId!);
        snapshot.Document.Nodes.Single(n => n.NodeId == "F").Flow!.JumpTo = "C";
        s.Flows.Save(snapshot.Document, snapshot.Revision);
        s.Port = new Port();
        var done = await s.Runner().ResumeAsync(paused.RunId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, done.State);
        Assert.Equal(new[] { "E" }, s.Port.Visited); // 已经决定的去向不变，不回头执行 C
    }

    // ---------------- 校验 ----------------

    [Fact]
    public void BackwardSelfMissingAndLaneMemberTargetsBlockStart()
    {
        WorkflowDocument Doc(string? target) => new() { Name = "校验", Nodes = [R("A"), Flow("F", jumpTo: target), Lane("L", ["Y"], []), R("Y"), R("D")] };
        foreach (var bad in new[] { "A", "F", "missing", "Y" })
        {
            var doc = Doc(bad);
            Assert.NotEmpty(WorkflowComposition.ValidateJumps(doc));
            Assert.Equal("F", Assert.Single(WorkflowComposition.JumpIssues(doc)).SourceNodeId);
            Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
        }
        Assert.Empty(WorkflowComposition.ValidateJumps(Doc("D")));
        Assert.Empty(WorkflowComposition.ValidateJumps(Doc("L")));
        Assert.True(new WorkflowPlan(Doc("D")).Preflight(true).Executable);
    }

    [Fact]
    public void DraggingTargetAboveSourceBlocksStartInsteadOfRewriting()
    {
        var doc = new WorkflowDocument { Name = "拖动", Nodes = [R("A"), R("D"), Flow("F", jumpTo: "D"), R("B")] };
        Assert.Contains(WorkflowComposition.ValidateJumps(doc), e => e.Contains("只能往后跳"));
        Assert.Equal("D", doc.Nodes[2].Flow!.JumpTo); // 不自动改去向
    }

    [Fact]
    public void LegacyPairedLaneAndItsConditionFlowCannotJump()
    {
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday);
        var doc = new WorkflowDocument { Name = "旧车道", Nodes = [
            new() { NodeId = "judge", Kind = "control.flow", Flow = new() { Steps = [c], ExitStepId = c.Id, JumpTo = "D" } },
            new() { NodeId = "old", Kind = "control.lanes", Lanes = new() { SourceFlowNodeId = "judge", YesNodeIds = ["Y"], NoNodeIds = [], YesJumpTo = "D" } },
            R("Y"), R("D")] };
        var issues = WorkflowComposition.JumpIssues(doc);
        Assert.Contains(issues, i => i.SourceNodeId == "judge");
        Assert.Contains(issues, i => i.SourceNodeId == "old");
    }

    [Fact]
    public void UnreachableRowsAreReportedButDoNotBlock()
    {
        var doc = new WorkflowDocument { Name = "不可达", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("D")] };
        Assert.Equal(new[] { "B" }, WorkflowComposition.UnreachableRows(doc));
        Assert.True(new WorkflowPlan(doc).Preflight(true).Executable);
        var reachedByLane = new WorkflowDocument { Name = "车道可达", Nodes = [Lane("L", [], [], yesJump: "D"), R("B"), R("D")] };
        Assert.Empty(WorkflowComposition.UnreachableRows(reachedByLane));
    }

    // ---------------- 旧版保护与旧计划不变 ----------------

    [Fact]
    public void SaveAddsAndRemovesLegacyGuardMarker()
    {
        using var s = new State();
        var doc = new WorkflowDocument { Name = "保护标记", Nodes = [Flow("F", jumpTo: "D"), R("B"), R("D")] };
        s.Save(doc);
        var saved = s.Flows.LoadSnapshot(doc.WorkflowId!);
        Assert.Contains(saved.Document.Nodes.Single(n => n.NodeId == "F").Strategies, x => x.Kind == WorkflowComposition.JumpMarkerKind);
        // 旧版执行端不认识该标记：从已知集合去掉后应被判为不可执行。
        var catalog = (HashSet<string>)WorkflowKindCatalog.StrategyKinds;
        Assert.Contains(WorkflowComposition.JumpMarkerKind, catalog);
        saved.Document.Nodes.Single(n => n.NodeId == "F").Flow!.JumpTo = null;
        s.Flows.Save(saved.Document, saved.Revision);
        Assert.DoesNotContain(s.Flows.LoadSnapshot(doc.WorkflowId!).Document.Nodes.Single(n => n.NodeId == "F").Strategies,
            x => x.Kind == WorkflowComposition.JumpMarkerKind);
    }

    [Fact]
    public void PlansWithoutJumpsSerializeAndCompileExactlyAsBefore()
    {
        var c = StartupStepKinds.Create(StartupStepKinds.Weekday);
        var legacy = new WorkflowDocument { Name = "旧车道布局", Nodes = [
            new() { NodeId = "judge", Kind = "control.flow", Flow = new() { Steps = [c], ExitStepId = c.Id } },
            new() { NodeId = "old", Kind = "control.lanes", Lanes = new() { SourceFlowNodeId = "judge", YesNodeIds = ["Y"], NoNodeIds = [] } },
            R("Y"), Flow("F"), R("D")] };
        var json = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("jumpTo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yesJumpTo", json, StringComparison.OrdinalIgnoreCase);
        // 旧版配对车道的运行布局键与改动前同一公式：只含 NodeId / Exit / 整个 Lanes。
        var expected = JsonSerializer.Serialize(legacy.Nodes.Where(n => n.Flow is not null || n.Lanes is not null)
            .Select(n => new { n.NodeId, Exit = n.Flow?.ExitStepId, n.Lanes }));
        Assert.Equal(expected, WorkflowComposition.LayoutKey(legacy));
        WorkflowComposition.Compile(legacy);
        Assert.All(legacy.Nodes, n => Assert.Null(n.Path!.Jump));
        Assert.DoesNotContain("\"jump\"", JsonSerializer.Serialize(legacy), StringComparison.Ordinal);
        WorkflowComposition.SyncJumpMarkers(legacy);
        Assert.DoesNotContain(legacy.Nodes.SelectMany(n => n.Strategies), x => x.Kind == WorkflowComposition.JumpMarkerKind);
    }

    [Fact]
    public void DeletingTargetResetsJumpsAndUndoRestoresThem()
    {
        var doc = new WorkflowDocument { Name = "删除目标", WorkflowId = "wf-del", Nodes = [Flow("F", jumpTo: "D"), Lane("L", ["Y"], [], yesJump: "D"), R("Y"), R("B"), R("D")] };
        var edit = new MultiplayerHoeingAssistant.ViewModels.WorkflowEditVm(doc, "rev", new ResourceCatalogService(() => null, Path.Combine(Path.GetTempPath(), "jump-catalog-" + Guid.NewGuid().ToString("N"), "catalog.json")));
        var target = edit.Nodes.Single(n => n.NodeId == "D");
        Assert.Equal(2, edit.JumpSourcesTo(new HashSet<string> { "D" }).Count);
        edit.RemoveNodeCommand.Execute(target);
        Assert.Null(edit.Draft.Nodes.Single(n => n.NodeId == "F").Flow!.JumpTo);
        Assert.Null(edit.Draft.Nodes.Single(n => n.NodeId == "L").Lanes!.YesJumpTo);
        Assert.True(edit.UndoSchedule());
        Assert.Equal("D", edit.Draft.Nodes.Single(n => n.NodeId == "F").Flow!.JumpTo);
        Assert.Equal("D", edit.Draft.Nodes.Single(n => n.NodeId == "L").Lanes!.YesJumpTo);
    }

    // ---------------- 夹具 ----------------

    private sealed class State : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "composition-jump-" + Guid.NewGuid().ToString("N"));
        public WorkflowStore Flows { get; }
        public RunStore Runs { get; }
        public Port Port = new();
        public State() { Flows = new(Path.Combine(Root, "flows")); Runs = new(Path.Combine(Root, "runs")); }
        public void Save(WorkflowDocument doc) => Flows.Save(doc, null);
        public WorkflowRunner Runner(bool weekend = true)
        {
            var actions = new Actions();
            var runtime = new TaskFlowRuntime
            {
                Registers = new RegisterBankService(Path.Combine(Root, "registers-" + Guid.NewGuid().ToString("N") + ".json"), startTimer: false),
                Clock = () => new DateTimeOffset(2026, 11, weekend ? 7 : 9, 12, 0, 0, TimeSpan.FromHours(8)),
            };
            return new(Flows, Runs, Port, actions, actions, new()
            {
                FlowRuntime = runtime,
                Clock = () => new DateTimeOffset(2026, 11, weekend ? 7 : 9, 12, 0, 0, TimeSpan.FromHours(8)),
            });
        }
        public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { } }
    }

    private sealed class Port : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public readonly List<string> Visited = [];
        public Action<WorkflowSubmitRequest>? OnSend;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Visited.Add(request.Occurrence.NodeId); TerminalReleaseFixtureFacts.FreezeBody(request); OnSend?.Invoke(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Visited.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct) => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
    }

    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
