using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class StandaloneLaneTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentLaneExecutesItsOwnConditionAndExactlyOneBranch(bool weekend)
    {
        using var state = new State();
        var doc = Document();
        Assert.DoesNotContain(doc.Nodes, n => n.Kind == "control.flow");
        state.Save(doc);
        var result = await state.Runner(weekend).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(new[] { weekend ? "B" : "C", "D" }, state.Port.Visited);
        Assert.Equal(weekend ? "branchYes" : "branchNo", result.NodeOutcomes.Single(o => o.NodeId == "lane").Result);
        Assert.Single(result.NodeOutcomes.Where(o => o.NodeId == "D"));
        var stored = state.Flows.LoadSnapshot(doc.WorkflowId!).Document;
        Assert.Single(stored.Nodes.Where(n => n.Kind == "control.lanes"));
        Assert.DoesNotContain(stored.Nodes, n => n.Kind == "control.flow");
        Assert.Empty(stored.Nodes[0].Lanes!.SourceFlowNodeId);
    }

    [Fact]
    public async Task EmptySelectedLaneJoinsMainlineWithoutEnteringOtherLane()
    {
        using var state = new State();
        var doc = Document();
        doc.Nodes[0].Lanes!.YesNodeIds.Clear();
        doc.Nodes.RemoveAll(n => n.NodeId == "B");
        state.Save(doc);
        var result = await state.Runner(weekend: true).StartAsync(doc.WorkflowId!);
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(new[] { "D" }, state.Port.Visited);
    }

    [Fact]
    public async Task PausedChosenBranchSurvivesRenamingAndConditionParameterChanges()
    {
        using var state = new State();
        var doc = Document(); state.Save(doc);
        var first = state.Runner(weekend: true);
        state.Port.OnSend = request => first.RequestAction(request.Run.RunId, WorkflowRunAction.Pause);
        var paused = await first.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Paused, paused.State);
        Assert.Equal("D", paused.Cursor!.NodeId);
        Assert.Equal(new[] { "B" }, state.Port.Visited);
        var snapshot = state.Flows.LoadSnapshot(doc.WorkflowId!);
        var lane = snapshot.Document.Nodes.Single(n => n.NodeId == "lane").Lanes!;
        lane.Name = "改名后的路线"; lane.YesName = "上路";
        lane.Decision!.Steps.Single().Weekdays = [1, 2, 3, 4, 5];
        state.Flows.Save(snapshot.Document, snapshot.Revision);
        state.Port = new Port();
        var completed = await state.Runner(weekend: false).ResumeAsync(paused.RunId).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, completed.State);
        Assert.Equal(new[] { "D" }, state.Port.Visited);
        Assert.Equal("branchYes", completed.NodeOutcomes.Single(o => o.NodeId == "lane").Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationLogCanFeedAnIndependentLaneWithoutASeparateJudgeTask(bool hit)
    {
        using var state = new State();
        using var registers = new RegisterBankService(Path.Combine(state.Root, "registers.json"), startTimer: false);
        var source = new LogSource();
        if (hit) source.AtFreeze = source.Hit;
        var trigger = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        trigger.TriggerScope = "task"; trigger.TargetNodeId = "A"; trigger.LogKeyword = "今天执行B线"; trigger.WatchRepeat = false;
        trigger.BeforeAttemptSteps = [Write(0)];
        trigger.FireSteps = [Write(1)];
        var condition = StartupStepKinds.Create(StartupStepKinds.JudgeRegister);
        condition.RegisterCondition = new() { Values = Selection(1) };
        var doc = Document();
        doc.Nodes[0].Lanes!.Decision = new() { Steps = [condition], ExitStepId = condition.Id };
        doc.Nodes.InsertRange(0, [
            new WorkflowNode { NodeId = "prep", Kind = "control.flow", Flow = new() { Name = "准备监听", Steps = [trigger] } },
            Resource("A")]);
        Assert.Single(doc.Nodes.Where(n => n.Kind == "control.flow")); // 唯一流程是A前的准备，车道判断没有另一行。
        state.Save(doc);
        var runtime = Runtime(true); runtime.Registers = registers; runtime.ObservationSource = source;
        var result = await state.Runner(runtime).StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(new[] { "A", hit ? "B" : "C", "D" }, state.Port.Visited);
        Assert.Empty(runtime.List());
    }

    [Fact]
    public void InvalidOrAmbiguousInternalConditionIsRejected()
    {
        var doc = Document(); var lane = doc.Nodes[0].Lanes!;
        lane.Decision!.ExitStepId = null;
        Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
        lane.Decision.ExitStepId = lane.Decision.Steps[0].Id;
        lane.Decision.Steps[0].Enabled = false;
        Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
        lane.Decision.Steps[0].Enabled = true;
        lane.SourceFlowNodeId = "other-flow";
        Assert.Contains(WorkflowComposition.Validate(doc), e => e.Contains("同时"));
    }

    [Fact]
    public void InternalTriggerTargetsAreValidatedInsideTheLane()
    {
        var doc = Document(); var lane = doc.Nodes[0].Lanes!;
        var log = StartupStepKinds.Create(StartupStepKinds.LogTrigger);
        log.TriggerScope = "task"; log.TargetNodeId = "missing"; log.LogKeyword = "今天执行B线";
        lane.Decision!.Steps.Insert(0, log);
        Assert.Contains(WorkflowComposition.Validate(doc), e => e.Contains("目标任务不存在"));
    }

    [Fact]
    public async Task ExistingPairedDataKeepsItsSourceIdentityAndStillChoosesOneLane()
    {
        using var state = new State();
        var doc = Document(); var lane = doc.Nodes[0].Lanes!;
        var decision = lane.Decision!; lane.Decision = null; lane.SourceFlowNodeId = "legacy-flow";
        doc.Nodes.Insert(0, new() { NodeId = "legacy-flow", Kind = "control.flow", Flow = decision });
        var oldLayout = JsonSerializer.Serialize(doc.Nodes.Where(n => n.Flow is not null || n.Lanes is not null)
            .Select(n => new { n.NodeId, Exit = n.Flow?.ExitStepId, n.Lanes }));
        Assert.Equal(oldLayout, WorkflowComposition.LayoutKey(doc));
        Assert.Same(decision, WorkflowComposition.GetDecision(doc, doc.Nodes[1]));
        Assert.Equal("legacy-flow", WorkflowComposition.GetDecisionOwnerId(doc, doc.Nodes[1]));
        state.Save(doc);
        var result = await state.Runner(true).StartAsync(doc.WorkflowId!);
        Assert.Equal(WorkflowRunState.Succeeded, result.State);
        Assert.Equal(new[] { "B", "D" }, state.Port.Visited);
        var saved = state.Flows.LoadSnapshot(doc.WorkflowId!).Document;
        Assert.Equal("legacy-flow", saved.Nodes.Single(n => n.Lanes is not null).Lanes!.SourceFlowNodeId);
        Assert.Null(saved.Nodes.Single(n => n.Lanes is not null).Lanes!.Decision);
        Assert.Single(saved.Nodes.Where(n => n.NodeId == "legacy-flow"));
    }

    [Fact]
    public void IndependentLaneOwnsItsIdentityAndLegacyLookupDoesNotGuess()
    {
        var doc = Document(); var lane = doc.Nodes[0];
        Assert.Same(lane.Lanes!.Decision, WorkflowComposition.GetDecision(doc, lane));
        Assert.Equal(lane.NodeId, WorkflowComposition.GetDecisionOwnerId(doc, lane));
        lane.Lanes.Decision = null; lane.Lanes.SourceFlowNodeId = "missing";
        Assert.Null(WorkflowComposition.GetDecision(doc, lane));
    }

    private static WorkflowDocument Document()
    {
        var condition = StartupStepKinds.Create(StartupStepKinds.Weekday); condition.Weekdays = [6, 7];
        return new()
        {
            Name = "独立车道",
            Nodes = [
                new() { NodeId = "lane", Kind = "control.lanes", Lanes = new()
                {
                    Name = "本日安排", Decision = new() { Name = "决定今天的路线", Steps = [condition], ExitStepId = condition.Id },
                    YesNodeIds = ["B"], NoNodeIds = ["C"]
                }},
                Resource("B"), Resource("C"), Resource("D")]
        };
    }

    private static WorkflowNode Resource(string id) => new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id } };
    private static List<int?> Selection(int value) { var values = RegisterBank.EmptySelection(); values[0] = value; return values; }
    private static StartupStep Write(int value) => new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Selection(value) } };
    private static TaskFlowRuntime Runtime(bool weekend) => new()
    {
        Clock = () => new DateTimeOffset(2026, 11, weekend ? 7 : 9, 12, 0, 0, TimeSpan.FromHours(8))
    };

    private sealed class State : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "standalone-lane-" + Guid.NewGuid().ToString("N"));
        public WorkflowStore Flows { get; }
        public RunStore Runs { get; }
        public Port Port = new();
        public State() { Flows = new(Path.Combine(Root, "flows")); Runs = new(Path.Combine(Root, "runs")); }
        public void Save(WorkflowDocument doc) => Flows.Save(doc, null);
        public WorkflowRunner Runner(bool weekend) => Runner(Runtime(weekend));
        public WorkflowRunner Runner(TaskFlowRuntime runtime)
        {
            var actions = new Actions();
            return new(Flows, Runs, Port, actions, actions, new() { FlowRuntime = runtime });
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
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

    private sealed class LogSource : IWorkflowLiveObservationSource
    {
        private Action<WorkflowObservationHit>? _hit;
        public Action? AtFreeze;
        public void Hit() => _hit?.Invoke(new(DateTime.Now, "今天执行B线", "test.log", 1));
        public Task<IWorkflowObservationSession> ArmAsync(string id, string keyword, CancellationToken ct) => ArmLiveAsync(id, keyword, _ => { }, ct);
        public Task<IWorkflowObservationSession> ArmLiveAsync(string id, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct)
        { _hit = hit; return Task.FromResult<IWorkflowObservationSession>(new Session(this, id)); }
        private sealed class Session(LogSource source, string id) : IWorkflowObservationSession
        {
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
            { source.AtFreeze?.Invoke(); return Task.FromResult(new WorkflowObservationFact(id, "frozen", [], null)); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
