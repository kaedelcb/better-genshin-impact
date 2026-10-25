using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class WorkflowTimeWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "node-time-window-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("10:00", "18:00", "09:59:59", false)]
    [InlineData("10:00", "18:00", "10:00:00", true)]
    [InlineData("10:00", "18:00", "17:59:59", true)]
    [InlineData("10:00", "18:00", "18:00:00", false)]
    [InlineData("22:00", "06:00", "21:59:59", false)]
    [InlineData("22:00", "06:00", "22:00:00", true)]
    [InlineData("22:00", "06:00", "00:00:00", true)]
    [InlineData("22:00", "06:00", "05:59:59", true)]
    [InlineData("22:00", "06:00", "06:00:00", false)]
    [InlineData("07:30", "07:30", "00:00:00", true)]
    [InlineData("07:30", "07:30", "07:30:00", true)]
    public void DailyWindows_IncludeStartExcludeEndAndSupportMidnight(string start, string end, string at, bool expected)
    {
        var window = new WorkflowTimeWindow { Start = start, End = end };
        Assert.True(WorkflowTimeWindowPolicy.TryValidate(window, out _));
        Assert.Equal(expected, WorkflowTimeWindowPolicy.Allows(window, At(at)));
    }

    [Theory]
    [InlineData(false, false, NodeGateAction.Proceed)]
    [InlineData(false, true, NodeGateAction.Proceed)]
    [InlineData(true, false, NodeGateAction.Skip)]
    [InlineData(true, true, NodeGateAction.Skip)]
    public void SwitchControlsPlanAndExplicitSingleTask(bool enabled, bool singleTask, NodeGateAction expected)
    {
        var doc = Document(Node("a", "10:00", "11:00"));
        doc.NodeTimeWindowEnabled = enabled;
        var gate = new WorkflowPlan(doc).EvaluateNode(new("a", 0, 0, 0), At("09:00:00"), true,
            ignorePlanParticipation: singleTask);
        Assert.Equal(expected, gate.Action);
        if (expected == NodeGateAction.Skip) Assert.Contains("允许执行时间段", gate.Reason);
    }

    [Fact]
    public void MissingWindowAndOldDocumentsRemainUnrestricted()
    {
        var old = JsonSerializer.Deserialize<WorkflowDocument>("{\"name\":\"旧计划\",\"nodes\":[{\"nodeId\":\"a\",\"kind\":\"resource.oneDragonConfig\"}]}")!;
        Assert.False(old.NodeTimeWindowEnabled);
        Assert.Null(old.Nodes[0].TimeWindow);
        old.NodeTimeWindowEnabled = true;
        Assert.Equal(NodeGateAction.Proceed, new WorkflowPlan(old).EvaluateNode(new("a", 0, 0, 0), At("03:00:00"), true).Action);
        Assert.Equal("全天", WorkflowTimeWindowPolicy.Display(null));
        Assert.Equal("全天", WorkflowTimeWindowPolicy.Display(new() { Start = "07:30", End = "07:30" }));
        Assert.Equal("22:00–次日 06:00", WorkflowTimeWindowPolicy.Display(new() { Start = "22:00", End = "06:00" }));
    }

    [Theory]
    [InlineData("24:00", "08:00")]
    [InlineData("8:00", "09:00")]
    [InlineData("08:00", "09:60")]
    [InlineData("", "09:00")]
    public void InvalidWindowIsRejectedOnlyWhenSwitchEnabled(string start, string end)
    {
        var doc = Document(Node("a", start, end));
        var plan = new WorkflowPlan(doc);
        doc.NodeTimeWindowEnabled = false;
        Assert.True(plan.Preflight(true).Executable);
        Assert.Equal(NodeGateAction.Proceed, plan.EvaluateNode(new("a", 0, 0, 0), At("08:00:00"), true).Action);
        doc.NodeTimeWindowEnabled = true;
        Assert.False(plan.Preflight(true).Executable);
        Assert.Equal(NodeGateAction.Reject, plan.EvaluateNode(new("a", 0, 0, 0), At("08:00:00"), true).Action);
    }

    [Fact]
    public void TimeWindowDoesNotOverrideExistingWeekdayOrParticipationFilters()
    {
        var node = Node("a", "08:00", "18:00");
        var doc = Document(node);
        node.ExtensionData = new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) };
        var plan = new WorkflowPlan(doc);
        Assert.Equal(NodeGateAction.Skip, plan.EvaluateNode(new("a", 0, 0, 0), At("09:00:00"), true).Action);
        Assert.Equal(NodeGateAction.Proceed, plan.EvaluateNode(new("a", 0, 0, 0), At("09:00:00"), true, true).Action);
        node.ExtensionData = null;
        node.Strategies.Add(new() { Kind = "condition.weekdays", Params = new() { ["days"] = JsonSerializer.SerializeToElement(Array.Empty<string>()) } });
        Assert.Equal(NodeGateAction.Skip, plan.EvaluateNode(new("a", 0, 0, 0), At("09:00:00"), true).Action);
    }

    [Fact]
    public async Task RunnerReevaluatesEachTaskAndDoesNotCancelAlreadyStartedBody()
    {
        var now = At("09:59:00");
        var doc = Document(Node("first", "09:00", "10:00"), Node("opens-later", "10:00", "11:00"), Node("closes-later", "09:00", "10:00"));
        var (runner, boundary, store) = CreateRunner(doc, () => now);
        boundary.AfterTerminal = () => now = At("10:01:00");
        var result = await runner.StartAsync(store.List().Single().WorkflowId);
        Assert.Equal(new[] { "first", "opens-later" }, boundary.Sent);
        Assert.Equal(new[] { "succeeded", "succeeded", "skippedFilter" }, result.NodeOutcomes.Select(outcome => outcome.Result));
        Assert.Empty(boundary.Cancelled);
    }

    [Fact]
    public async Task ExplicitSingleTaskOutsideWindowNeverSubmits()
    {
        var doc = Document(Node("single", "10:00", "11:00"), Node("later", "00:00", "00:00"));
        var (runner, boundary, store) = CreateRunner(doc, () => At("09:00:00"));
        var result = await runner.StartAsync(store.List().Single().WorkflowId, entryNodeId: "single", stopAfterNodeId: "single");
        Assert.Empty(boundary.Sent);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task FilteredTaskIsReevaluatedOnNextLoopInsteadOfPermanentlyCompleted()
    {
        var now = At("08:59:00");
        var doc = Document(Node("windowed", "09:00", "10:00"), Node("advance-clock", "00:00", "00:00"));
        doc.Loop = new() { Mode = "immediate", Params = new() { ["deadline"] = JsonSerializer.SerializeToElement("09:01") } };
        var (runner, boundary, store) = CreateRunner(doc, () => now);
        boundary.AfterTerminal = () => now = boundary.Sent.Count == 1 ? At("09:00:00") : At("09:02:00");
        var result = await runner.StartAsync(store.List().Single().WorkflowId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "advance-clock", "windowed" }, boundary.Sent);
        Assert.Contains(result.NodeOutcomes, outcome => outcome.NodeId == "windowed" && outcome.LoopIteration == 0 && outcome.Result == "skippedFilter");
        Assert.Contains(result.NodeOutcomes, outcome => outcome.NodeId == "windowed" && outcome.LoopIteration == 1 && outcome.Result == "succeeded");
    }

    [Fact]
    public async Task PreparationCrossingWindowEndDoesNotStartBody()
    {
        var now = At("09:59:00");
        var node = Node("a", "09:00", "10:00");
        node.Strategies.Add(new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("123456789") } });
        var preparation = new Preparation { OnExecute = () => now = At("10:00:00") };
        var (runner, boundary, store) = CreateRunner(Document(node), () => now, preparation);
        var result = await runner.StartAsync(store.List().Single().WorkflowId);
        Assert.Equal(1, preparation.Executed);
        Assert.Empty(boundary.Sent);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task ResourceRevisionLookupCrossingWindowEndDoesNotStartBody()
    {
        var now = At("09:59:00");
        var options = new WorkflowRunnerOptions
        {
            Clock = () => now,
            ResolveResourceRevisionAsync = (_, _) => { now = At("10:00:00"); return Task.FromResult("revision-new"); },
        };
        var (runner, boundary, store) = CreateRunner(Document(Node("a", "09:00", "10:00")), () => now, options: options);
        var result = await runner.StartAsync(store.List().Single().WorkflowId);
        Assert.Empty(boundary.Sent);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
    }

    [Fact]
    public async Task ObserverArmingCrossingWindowEndDoesNotCreateSubmissionIntent()
    {
        var now = At("09:59:00");
        var source = new ObservationSource(() => now = At("10:00:00"));
        var node = Node("a", "09:00", "10:00");
        node.Strategies.Add(new() { Kind = "observer.log", Params = new() { ["keyword"] = JsonSerializer.SerializeToElement("完成") } });
        var options = new WorkflowRunnerOptions { Clock = () => now, ObservationSource = source };
        var (runner, boundary, store) = CreateRunner(Document(node), () => now, options: options);
        var result = await runner.StartAsync(store.List().Single().WorkflowId);
        Assert.Empty(boundary.Sent);
        Assert.Null(result.CurrentSubmission);
        Assert.Equal("skippedFilter", Assert.Single(result.NodeOutcomes).Result);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ProductionSendGateWaitCrossingWindowEndPublishesOriginalNoCallProof()
    {
        var now = At("09:59:00");
        var (runs, run, node, occurrence) = SeedBoundary();
        var port = new NeverCalledPort();
        var sendGate = new TaskCenterOnlineSendGate();
        var boundary = new BgiWorkflowExecutionBoundary(port, runs, sendGate);
        var prepared = boundary.PrepareSubmit(new(run, occurrence, node, true, EnforceTimeWindow: true, Clock: () => now));
        Assert.Null(prepared.Rejection);
        sendGate.CloseForPriority();
        var pending = boundary.SendPreparedAsync(prepared, default);
        Assert.False(pending.IsCompleted);
        now = At("10:00:00");
        await sendGate.OpenAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BoundarySubmitKind.Filtered, result.Kind);
        Assert.Equal(0, port.Calls);
        var saved = runs.Load(run.RunId)!;
        Assert.True(saved.CurrentSubmission!.SendAttempted);
        Assert.Equal(SubmitIntentState.Rejected, saved.CurrentSubmission.Intent);
        Assert.Equal(LocalNoSendEvidence.PreparedTimeWindow, saved.CurrentSubmission.LocalNoSendProof!.Kind);
        Assert.Equal(prepared.FrozenPermit!.Nonce, saved.CurrentSubmission.LocalNoSendProof.ConsumptionId);
        Assert.True(TerminalReleaseEvidence.BodySettled(saved, saved.CurrentSubmission));
        Assert.True((await boundary.SendPreparedAsync(prepared, default)).Uncertain);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public void AdmissionContextRetainsFrozenWindowAndReevaluatesAfterQueueDelay()
    {
        var now = At("09:59:00");
        var (runs, run, node, occurrence) = SeedBoundary();
        var request = new WorkflowSubmitRequest(run, occurrence, node, true, EnforceTimeWindow: true, Clock: () => now);
        var context = TaskCenterHost.SuccessorContext.FreezeFrom(TaskCenterHost.FreezeNode(node), request, run.CurrentSubmission!);
        node.TimeWindow!.End = "11:00";
        now = At("10:00:00");
        var boundary = new BgiWorkflowExecutionBoundary(new NeverCalledPort(), runs);
        var prepared = boundary.PrepareSubmit(context.ToSubmitRequest(run));
        Assert.Equal(BoundarySubmitKind.Filtered, prepared.Rejection!.Kind);
        Assert.Equal(SubmitIntentState.IntentRecorded, runs.Load(run.RunId)!.CurrentSubmission!.Intent);
        Assert.False(runs.Load(run.RunId)!.CurrentSubmission!.SendAttempted);
    }

    [Fact]
    public async Task ValidPublishedNoCallProofAllowsNextIntentAndIsArchivedUnchanged()
    {
        var now = At("09:59:00");
        var (runs, run, node, occurrence) = SeedBoundary();
        var boundary = new BgiWorkflowExecutionBoundary(new NeverCalledPort(), runs);
        var prepared = boundary.PrepareSubmit(new(run, occurrence, node, true, EnforceTimeWindow: true, Clock: () => now));
        now = At("10:00:00");
        Assert.Equal(BoundarySubmitKind.Filtered, (await boundary.SendPreparedAsync(prepared, default)).Kind);
        var original = JsonSerializer.Serialize(run.CurrentSubmission);
        runs.RecordIntentForBoundary(run, NextSubmission(run), false);
        var saved = runs.Load(run.RunId)!;
        Assert.Equal("next", saved.CurrentSubmission!.NodeId);
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(saved.SubmissionHistory)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongIdentityNoCallProofCannotAuthorizeNextIntent(bool wrongIdentity)
    {
        var now = At("09:59:00");
        var (runs, run, node, occurrence) = SeedBoundary();
        var boundary = new BgiWorkflowExecutionBoundary(new NeverCalledPort(), runs);
        var prepared = boundary.PrepareSubmit(new(run, occurrence, node, true, EnforceTimeWindow: true, Clock: () => now));
        if (wrongIdentity)
        {
            now = At("10:00:00");
            Assert.Equal(BoundarySubmitKind.Filtered, (await boundary.SendPreparedAsync(prepared, default)).Kind);
            run.CurrentSubmission!.LocalNoSendProof = run.CurrentSubmission.LocalNoSendProof! with { Key = "different-submission" };
        }
        Assert.Throws<InvalidOperationException>(() => runs.RecordIntentForBoundary(run, NextSubmission(run), false));
        Assert.Equal("a", runs.Load(run.RunId)!.CurrentSubmission!.NodeId);
        Assert.Empty(runs.Load(run.RunId)!.SubmissionHistory);
    }

    [Fact]
    public void UnpublishedCallerFabricatedNoCallProofCannotBeLaunderedIntoHistory()
    {
        var (runs, run, node, occurrence) = SeedBoundary();
        var boundary = new BgiWorkflowExecutionBoundary(new NeverCalledPort(), runs);
        var prepared = boundary.PrepareSubmit(new(run, occurrence, node, true));
        Assert.True(prepared.TryConsume());
        Assert.True(runs.TryConsumePreparedSubmission(prepared, out var consumed));
        RunStore.RebaseOnto(run, consumed!);
        // 字段吻合还不是已发布证明；普通意图写入不能把调用者内存中的证明补进旧历史。
        run.CurrentSubmission!.LocalNoSendProof = prepared.CreateNoSendProof(timeWindowClosed: true);
        run.CurrentSubmission.Intent = SubmitIntentState.Rejected;
        Assert.True(LocalNoSendEvidence.IsDischarged(run, run.CurrentSubmission));
        Assert.Throws<RunRecordConflictException>(() => runs.RecordIntentForBoundary(run, NextSubmission(run), false));
        var saved = runs.Load(run.RunId)!;
        Assert.Equal("a", saved.CurrentSubmission!.NodeId);
        Assert.Null(saved.CurrentSubmission.LocalNoSendProof);
        Assert.Empty(saved.SubmissionHistory);
    }

    [Fact]
    public void StoreAndScheduleTransferPreserveTimeSettingsAndUnknownWindowFields()
    {
        var node = Node("a", "22:00", "06:00");
        node.TimeWindow!.ExtensionData = new() { ["futureSetting"] = JsonSerializer.SerializeToElement("保留") };
        var store = new WorkflowStore(Path.Combine(_root, "source", "flows"));
        var doc = Document(node);
        store.Save(doc, null);
        var saved = store.Load(doc.WorkflowId!);
        Assert.True(saved.NodeTimeWindowEnabled);
        Assert.Equal("22:00", saved.Nodes[0].TimeWindow!.Start);
        Assert.Equal("保留", saved.Nodes[0].TimeWindow!.ExtensionData!["futureSetting"].GetString());
        var destination = Path.Combine(_root, "export.json");
        WorkflowScheduleTransfer.Export(store, destination);
        var importedStore = new WorkflowStore(Path.Combine(_root, "imported", "flows"));
        var importedId = Assert.Single(WorkflowScheduleTransfer.Import(importedStore, destination));
        var imported = importedStore.Load(importedId);
        Assert.True(imported.NodeTimeWindowEnabled);
        Assert.Equal("22:00", imported.Nodes[0].TimeWindow!.Start);
        Assert.Equal("06:00", imported.Nodes[0].TimeWindow!.End);
        Assert.Equal("保留", imported.Nodes[0].TimeWindow!.ExtensionData!["futureSetting"].GetString());
    }

    private (WorkflowRunner Runner, Boundary Boundary, WorkflowStore Store) CreateRunner(WorkflowDocument document,
        Func<DateTimeOffset> clock, Preparation? preparation = null, WorkflowRunnerOptions? options = null)
    {
        var store = new WorkflowStore(Path.Combine(_root, "flows"));
        var runs = new RunStore(Path.Combine(_root, "runs"));
        store.Save(document, null);
        var boundary = new Boundary();
        return (new(store, runs, boundary, preparation ?? new(), new Terminal(), options ?? new() { Clock = clock }), boundary, store);
    }

    private static WorkflowDocument Document(params WorkflowNode[] nodes)
        => new() { Name = "时间段测试", NodeTimeWindowEnabled = true, Nodes = nodes.ToList() };

    private static WorkflowNode Node(string id, string start, string end)
        => new() { NodeId = id, Kind = "resource.oneDragonConfig", Ref = new() { Config = id, Revision = "revision" },
            TimeWindow = new() { Start = start, End = end } };

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-25T" + time + "+08:00");

    private static WorkflowSubmission NextSubmission(WorkflowRunRecord run)
        => new() { Key = RunStore.DeriveSubmissionKey(run.RunId, "next", 0, 0, 1), NodeId = "next", Attempt = 1 };

    private (RunStore Runs, WorkflowRunRecord Run, WorkflowNode Node, WorkflowNodeOccurrence Occurrence) SeedBoundary()
    {
        var runs = new RunStore(Path.Combine(_root, "boundary-runs"));
        var run = runs.CreateRun("wf-window", "revision", stopAuthority:
            new WorkflowStopAuthorityRecord("4321:638999999999999999", 0, "window-test-intent", 1, System.Diagnostics.Stopwatch.Frequency));
        var occurrence = new WorkflowNodeOccurrence("a", 0, 0, 0);
        run.Cursor = new() { NodeId = "a", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "a", 0, 0, 1), NodeId = "a", Occurrence = 0,
            LoopIteration = 0, Attempt = 1, Intent = SubmitIntentState.IntentRecorded,
        };
        runs.Update(run);
        return (runs, run, Node("a", "09:00", "10:00"), occurrence);
    }

    private sealed class NeverCalledPort : IBgiExecutionPort
    {
        public int Calls { get; private set; }
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch? ServerEpoch => new() { ProcessId = 4321, StartTicksUtc = 638999999999999999 };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("时间段外不应调用发送端口。"); }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult<(string?, BgiJobInfo?)>((null, null));
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ObservationSource(Action onArm) : IWorkflowObservationSource
    {
        public bool Disposed { get; set; }
        public Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct)
        { onArm(); return Task.FromResult<IWorkflowObservationSession>(new ObservationSession(this, identity)); }
        private sealed class ObservationSession(ObservationSource owner, string identity) : IWorkflowObservationSession
        {
            public Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
                => Task.FromResult(new WorkflowObservationFact(identity, "frozen", [], null));
            public ValueTask DisposeAsync() { owner.Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class Boundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public List<string> Sent { get; } = [];
        public List<string> Cancelled { get; } = [];
        public Action? AfterTerminal { get; set; }
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Sent.Add(request.Occurrence.NodeId);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Sent.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            AfterTerminal?.Invoke();
            return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        }
        public Task RequestCancelAsync(string jobId, CancellationToken ct) { Cancelled.Add(jobId); return Task.CompletedTask; }
    }

    private sealed class Preparation : IWorkflowPrerequisiteAdapter
    {
        public Action? OnExecute { get; init; }
        public int Executed { get; private set; }
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
        { Executed++; OnExecute?.Invoke(); return Task.FromResult(PrerequisiteResult.ProceedInstance); }
    }

    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => throw new InvalidOperationException("本夹具不包含收尾动作。");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
