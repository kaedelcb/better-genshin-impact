using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskFlowCompositionTests
{
    [Fact]
    public void LegacyMigrationEntryCannotBypassNewFlowOrLaneSelection()
    {
        var doc = Document();
        doc.ExtensionData = new() { ["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "B", once = true }) };
        var plan = new WorkflowPlan(doc);
        Assert.Null(plan.EntrySeed(out var reason));
        Assert.Contains("绕过", reason);
        doc.ExtensionData["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = doc.Nodes[0].NodeId, once = true });
        Assert.NotNull(new WorkflowPlan(doc).EntrySeed(out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ExplicitCompositionEntry_AllowsSelectedBranchAndMainline_AndResolvesLegacyLaneCondition()
    {
        var composed = new WorkflowPlan(Document());
        Assert.Equal("F", composed.ExplicitEntryCursor("F")!.NodeId);
        Assert.Equal("B", composed.ExplicitEntryCursor("B")!.NodeId);
        Assert.Equal("F", composed.ExplicitEntryCursor("L")!.NodeId);
        Assert.Equal("D", composed.ExplicitEntryCursor("D")!.NodeId);
        Assert.Throws<InvalidOperationException>(() => composed.ExplicitEntryCursor("missing"));
        var legacy = new WorkflowPlan(new() { Nodes = [Resource("A"), Resource("B")] });
        Assert.Equal("B", legacy.ExplicitEntryCursor("B")!.NodeId);
    }

    [Fact]
    public void CompositionRoundEntry_UsesMainlineEvenWhenLaneMemberPrecedesItInStorage()
    {
        var doc = Document(); var member = doc.Nodes.Single(n => n.NodeId == "B");
        doc.Nodes.Remove(member); doc.Nodes.Insert(0, member);
        var plan = new WorkflowPlan(doc);
        Assert.True(plan.Preflight(true).Executable);
        Assert.Equal("F", plan.FirstOccurrence()!.NodeId);
        Assert.Equal("F", plan.ExplicitEntryCursor("F")!.NodeId);
        Assert.Equal("B", plan.ExplicitEntryCursor("B")!.NodeId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RealRunner_TailLog_SelectsOnlyOneResourceLane_AndPersistsJoin(bool hit)
    {
        using var state = new Fixture();
        var choice = RegisterExit(); var doc = Document();
        doc.Nodes[0] = choice;
        doc.Nodes[1].Lanes!.SourceFlowNodeId = choice.NodeId;
        doc.Nodes.InsertRange(0, [new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [state.Trigger] } }, Resource("A")]);
        var root = Path.Combine(Path.GetTempPath(), "task-flow-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var flows = new WorkflowStore(Path.Combine(root, "flows")); var runs = new RunStore(Path.Combine(root, "runs"));
            flows.Save(doc, null);
            state.Source.AtFreeze = hit ? state.Source.Hit : null;
            var port = new Port(); var actions = new Actions();
            var runner = new WorkflowRunner(flows, runs, port, actions, actions, new() { FlowRuntime = state.Runtime });
            var result = await runner.StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowRunState.Succeeded, result.State);
            Assert.Equal(new[] { "A", hit ? "B" : "C", "D" }, port.Visited);
            Assert.Single(result.NodeOutcomes.Where(o => o.NodeId == "D"));
            Assert.Equal(hit ? "branchYes" : "branchNo", runs.Load(result.RunId)!.NodeOutcomes.Single(o => o.NodeId == "judge").Result);
            Assert.Empty(state.Runtime.List());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealRunner_DisabledA_CannotDispatchEitherLane()
    {
        using var state = new Fixture(); var doc = Document(); var exit = RegisterExit();
        doc.Nodes[0] = exit; doc.Nodes[1].Lanes!.SourceFlowNodeId = exit.NodeId;
        var target = Resource("A"); target.ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) };
        doc.Nodes.InsertRange(0, [new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [state.Trigger] } }, target]);
        var root = Path.Combine(Path.GetTempPath(), "task-flow-skip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var flows = new WorkflowStore(Path.Combine(root, "flows")); var runs = new RunStore(Path.Combine(root, "runs")); flows.Save(doc, null);
            var port = new Port(); var actions = new Actions();
            var result = await new WorkflowRunner(flows, runs, port, actions, actions, new() { FlowRuntime = state.Runtime }).StartAsync(doc.WorkflowId!);
            Assert.Equal(WorkflowRunState.Unknown, result.State); Assert.Empty(port.Visited);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StableBranches_ReachOnlyChosenLaneAndOneJoin(bool answer)
    {
        var doc = Document();
        var plan = new WorkflowPlan(doc);
        Assert.True(plan.Preflight(true).Executable, string.Join(";", plan.Preflight(true).BlockingReasons));
        var choice = plan.FirstOccurrence()!;
        var selected = plan.Next(choice, answer ? "branchYes" : "branchNo")!;
        Assert.Equal(answer ? "B" : "C", selected.NodeId);
        Assert.Equal("D", plan.Next(selected)!.NodeId);
        Assert.Null(plan.Next(plan.Next(selected)!));
        Assert.Equal(5, doc.Nodes.Count); // 作者的车道行与未选资源仍保留。
    }

    [Fact]
    public void EmptyLane_GoesToJoin_AndDuplicateMembershipIsRejected()
    {
        var doc = Document(); doc.Nodes[1].Lanes!.YesNodeIds.Clear();
        doc.Nodes.RemoveAll(n => n.NodeId == "B");
        var plan = new WorkflowPlan(doc);
        Assert.Equal("D", plan.Next(plan.FirstOccurrence()!, "branchYes")!.NodeId);
        doc.Nodes[1].Lanes!.YesNodeIds.Add("C");
        Assert.Contains(WorkflowComposition.Validate(doc), e => e.Contains("重复引用"));
    }

    [Fact]
    public void MixedLegacyPath_IsRejectedWithoutOverwritingIt()
    {
        var doc = Document(); var old = new WorkflowPath { Next = "C" }; doc.Nodes[2].Path = old;
        Assert.Contains(WorkflowComposition.Validate(doc), e => e.Contains("旧自定义路径"));
        Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
        Assert.Same(old, doc.Nodes[2].Path);
    }

    [Fact]
    public async Task WeekdayExit_UsesNoRegisterService_AndRecordsChoice()
    {
        var doc = Document(); var node = doc.Nodes[0];
        var runtime = new TaskFlowRuntime { Clock = () => new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(8)) };
        var run = new WorkflowRunRecord { RunId = "r", WorkflowId = "w" }; var saves = 0;
        Assert.True(await runtime.ExecuteAsync(run, node, 0, () => saves++, default));
        runtime.Clock = () => new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.True(await runtime.ExecuteAsync(run, node, 0, () => saves++, default)); // 同次到达使用已保存结果。
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TailLogWrite_IsDrainedBeforeRegisterExit(bool hit)
    {
        using var state = new Fixture();
        await state.Prepare();
        state.Source.AtFreeze = hit ? state.Source.Hit : null;
        await (await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default)).DisposeAsync();
        var result = await state.Runtime.ExecuteAsync(state.Run, RegisterExit(), 0, () => { }, default);
        Assert.Equal(hit, result);
        Assert.Empty(state.Runtime.List());
    }

    [Fact]
    public async Task SkippedTarget_HasPendingObservation_AndCannotTakeNoBranch()
    {
        using var state = new Fixture(); await state.Prepare();
        await Assert.ThrowsAsync<WorkflowObservationUnknownException>(() => state.Runtime.ExecuteAsync(state.Run, RegisterExit(), 0, () => { }, default));
        Assert.False(state.Registers.Evaluate(new() { Values = Selection(1) }).Passed);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cancel")]
    public async Task LostOrCancelledObservation_NeverMeansNoHit(string mode)
    {
        using var state = new Fixture(); await state.Prepare();
        var scope = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default);
        if (mode == "unknown") state.Source.State = "unknown";
        else Assert.True(await state.Runtime.CancelAsync(Assert.Single(state.Runtime.List()).RuntimeId));
        await scope.DisposeAsync();
        await Assert.ThrowsAsync<WorkflowObservationUnknownException>(() => state.Runtime.ExecuteAsync(state.Run, RegisterExit(), 0, () => { }, default));
        state.Source.Hit();
        Assert.Equal(0, state.Registers.GetSnapshot().Entries[0].Value);
    }

    [Fact]
    public async Task RepeatedOneShotLogs_DoNotReexecuteCallback_OrWriteAfterCancellation()
    {
        using var state = new Fixture(); await state.Prepare();
        var scope = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default);
        for (var i = 0; i < 10000; i++) state.Source.Hit();
        await scope.DisposeAsync();
        Assert.Equal(1, state.Registers.GetSnapshot().Entries[0].Value);
        state.Registers.Set(new() { Values = Selection(0) });
        state.Source.Hit();
        Assert.Equal(0, state.Registers.GetSnapshot().Entries[0].Value);
    }

    [Fact]
    public async Task ExplicitAttemptInitialization_IsReappliedOnlyForTargetAttempt()
    {
        using var state = new Fixture(); await state.Prepare();
        var first = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default);
        state.Source.AtFreeze = state.Source.Hit; await first.DisposeAsync();
        Assert.Equal(1, state.Registers.GetSnapshot().Entries[0].Value);
        state.Source.AtFreeze = null;
        var retry = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 2, () => { }, default);
        Assert.Equal(0, state.Registers.GetSnapshot().Entries[0].Value);
        await retry.DisposeAsync();
        Assert.False(await state.Runtime.ExecuteAsync(state.Run, RegisterExit(), 0, () => { }, default));
    }

    [Fact]
    public async Task RestartDuringA_CannotReplaceMissingLogWindowWithANewSubscription()
    {
        using var state = new Fixture(); await state.Prepare();
        _ = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default);
        var saved = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(state.Run))!;
        await state.Runtime.CloseRunAsync(state.Run.RunId);
        var restarted = new TaskFlowRuntime { Registers = state.Registers, ObservationSource = state.Source };
        await Assert.ThrowsAsync<WorkflowObservationUnknownException>(() => restarted.BeginTaskAsync(saved, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default));
        Assert.Empty(restarted.List());
    }

    [Fact]
    public async Task InternalCondition_CanMountATaskScopedTrigger()
    {
        using var state = new Fixture();
        var condition = StartupStepKinds.Create(StartupStepKinds.Weekday); condition.Weekdays = [1, 2, 3, 4, 5, 6, 7]; condition.TrueSteps = [state.Trigger];
        await state.Runtime.ExecuteAsync(state.Run, new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [condition] } }, 0, () => { }, default);
        await using var scope = await state.Runtime.BeginTaskAsync(state.Run, Resource("A"), new("A", 1, 0, 0), 1, () => { }, default);
        Assert.Single(state.Runtime.List());
    }

    [Fact]
    public void CallbackCannotMountNestedTriggersOrStartPrograms()
    {
        var trigger = StartupStepKinds.Create(StartupStepKinds.LogTrigger); trigger.LogKeyword = "x";
        trigger.FireSteps = [StartupStepKinds.Create(StartupStepKinds.StartProgram)];
        Assert.Contains(TaskFlowNodeCatalog.Validate(new() { Steps = [trigger] }), e => e.Contains("后台子链"));
    }

    [Fact]
    public void DisabledExit_AndExitWithLocalSubchains_AreRejectedBeforeExecution()
    {
        var doc = Document(); doc.Nodes[0].Flow!.Steps[0].Enabled = false;
        Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
        doc.Nodes[0].Flow!.Steps[0].Enabled = true;
        doc.Nodes[0].Flow!.Steps[0].TrueSteps = [StartupStepKinds.Create(StartupStepKinds.Wait)];
        Assert.False(new WorkflowPlan(doc).Preflight(true).Executable);
    }

    [Theory]
    [InlineData(StartupStepKinds.TimerTrigger)]
    [InlineData(StartupStepKinds.Watchdog)]
    public async Task TimerAndWatchdog_UseSameOwnedRuntime_ExecuteOnceAndCanBeWithdrawn(string kind)
    {
        using var state = new Fixture();
        var trigger = StartupStepKinds.Create(kind); trigger.TriggerScope = "round"; trigger.WatchRepeat = false;
        trigger.RepeatDaily = false; trigger.FireSteps = state.Trigger.FireSteps;
        state.Runtime.DelayAsync = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        state.Runtime.StatusProvider = () => new() { TaskRunning = true, TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "epoch", TaskStatusObservedAtUtc = DateTimeOffset.UtcNow };
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Runtime.Changed += () =>
        {
            if (state.Runtime.List().Any(t => t.Note?.Contains("已触发 1 次") == true)) fired.TrySetResult();
        };
        await state.Runtime.ExecuteAsync(state.Run, new() { NodeId = "timer", Kind = "control.flow", Flow = new() { Steps = [trigger] } }, 0, () => { }, default);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var mounted = Assert.Single(state.Runtime.List());
        Assert.Equal(state.Run.RunId, mounted.RunId);
        Assert.Equal(kind == StartupStepKinds.TimerTrigger ? "timer" : "watchdog", mounted.Kind);
        Assert.Equal(1, state.Registers.GetSnapshot().Entries[0].Value);
        Assert.Contains("已触发 1 次", mounted.Note);
        Assert.True(await state.Runtime.CancelAsync(mounted.RuntimeId));
        Assert.Empty(state.Runtime.List());
    }

    private static WorkflowDocument Document()
    {
        var step = StartupStepKinds.Create(StartupStepKinds.Weekday); step.Weekdays = [6, 7];
        return new() { Name = "分流", Nodes = [
            new() { NodeId = "F", Kind = "control.flow", Flow = new() { Steps = [step], ExitStepId = step.Id } },
            new() { NodeId = "L", Kind = "control.lanes", Lanes = new() { SourceFlowNodeId = "F", YesNodeIds = ["B"], NoNodeIds = ["C"] } },
            Resource("B"), Resource("C"), Resource("D")] };
    }
    private static WorkflowNode Resource(string id) => new() { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id } };
    private static List<int?> Selection(int value) { var selected = RegisterBank.EmptySelection(); selected[0] = value; return selected; }
    private static WorkflowNode RegisterExit()
    {
        var step = StartupStepKinds.Create(StartupStepKinds.JudgeRegister); step.Id = "exit"; step.RegisterCondition = new() { Values = Selection(1) };
        return new() { NodeId = "judge", Kind = "control.flow", Flow = new() { Steps = [step], ExitStepId = step.Id } };
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "task-flow-test-" + Guid.NewGuid().ToString("N"));
        public readonly FakeSource Source = new();
        public readonly TaskFlowRuntime Runtime;
        public readonly RegisterBankService Registers;
        public readonly WorkflowRunRecord Run = new() { RunId = Guid.NewGuid().ToString("N"), WorkflowId = "w" };
        public readonly StartupStep Trigger;
        public Fixture()
        {
            Directory.CreateDirectory(_dir);
            Registers = new(Path.Combine(_dir, "registers.json"), startTimer: false);
            Runtime = new() { Registers = Registers, ObservationSource = Source };
            Trigger = StartupStepKinds.Create(StartupStepKinds.LogTrigger); Trigger.TriggerScope = "task"; Trigger.TargetNodeId = "A"; Trigger.LogKeyword = "今天执行B线"; Trigger.WatchRepeat = false;
            Trigger.FireSteps = [new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Selection(1) } }];
            Trigger.BeforeAttemptSteps = [new() { Kind = StartupStepKinds.SetRegister, RegisterWrite = new() { Values = Selection(0) } }];
        }
        public Task<bool?> Prepare() => Runtime.ExecuteAsync(Run, new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [Trigger] } }, 0, () => { }, default);
        public void Dispose() { Registers.Dispose(); Directory.Delete(_dir, true); }
    }
    private sealed class FakeSource : IWorkflowLiveObservationSource
    {
        private Action<WorkflowObservationHit>? _hit;
        public string State = "frozen";
        public Action? AtFreeze;
        public void Hit() => _hit?.Invoke(new(DateTime.Now, "今天执行B线", "test.log", 1));
        public Task<IWorkflowObservationSession> ArmAsync(string id, string keyword, CancellationToken ct) => ArmLiveAsync(id, keyword, _ => { }, ct);
        public Task<IWorkflowObservationSession> ArmLiveAsync(string id, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct)
        { _hit = hit; return Task.FromResult<IWorkflowObservationSession>(new Session(this, id)); }
        private sealed class Session(FakeSource source, string id) : IWorkflowObservationSession
        {
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct) { source.AtFreeze?.Invoke(); return Task.FromResult(new WorkflowObservationFact(id, source.State, [], null)); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Port : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public List<string> Visited { get; } = [];
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Visited.Add(request.Occurrence.NodeId); TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Visited.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct) => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
    }
    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
