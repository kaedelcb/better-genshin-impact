using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class StateContinuationPlanResumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "state-plan-resume-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    private static async Task Until(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(ready());
    }
    private sealed class Port : IBgiExecutionPort
    {
        public BgiJobInfo? Job;
        public int Missing = 2;
        public int Queries;
        public int Sends;
        public int Cancels;
        public bool IsReady => true;
        public BgiEpoch? ServerEpoch => new() { ProcessId = 42, StartTicksUtc = 99 };
        public bool HasCapability(string name) => true;
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        { Interlocked.Increment(ref Sends); throw new InvalidOperationException("不能重发恢复"); }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) { Cancels++; return Task.CompletedTask; }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref Queries);
            return Task.FromResult<(string?, BgiJobInfo?)>(count <= Missing ? ("not_found", null) : (Job?.State, Job));
        }
    }
    private TaskCenterHost Host(out WorkflowStore workflows, out WorkflowDocument document)
    {
        var flows = Path.Combine(_root, "flows");
        workflows = new WorkflowStore(flows);
        document = new WorkflowDocument
        {
            Name = "原计划B继续C", Activation = new WorkflowActivation { Status = "active" },
            Nodes = new[] { "B", "C" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = id, ConfigKey = id + "#k", Revision = "rev-1" },
            }).ToList(),
        };
        workflows.Save(document, null);
        return new TaskCenterHost(flows, Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
            () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
    }
    private static BgiJobInfo Finished(WorkflowRunRecord run, string key, string job) => new()
    {
        Epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 }, JobId = Guid.Parse(job).ToString("N"),
        Source = "Resume", State = "succeeded", WorkflowRunId = run.WireRunId, NodeId = "B",
        Occurrence = 0, Iteration = 0, Attempt = 2, IdempotencyKey = key,
        ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
    };

    [Fact]
    public async Task ParkBindAndTerminalWriteFaults_RetrySameResume_WithoutDuplicatingHistory()
    {
        var host = Host(out var workflows, out var document);
        var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        var original = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1), NodeId = "B", Occurrence = 0,
            LoopIteration = 0, Attempt = 1, JobId = Guid.NewGuid().ToString("D"), Intent = SubmitIntentState.Accepted,
            SendAttempted = true, Epoch = "42:99", WireRunId = run.WireRunId, Fingerprint = "original-frozen",
            ExpiresAtUtc = "2030-01-01T00:00:00Z",
        };
        run.State = WorkflowRunState.Running;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Attempt = 1 };
        run.CurrentSubmission = original;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            StateContinuation = true, Generation = 7, Ticket = "original-ticket", Epoch = "42:99",
            NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            OriginalJobId = original.JobId, OriginalSubmissionKey = original.Key, Phase = "suspended",
        };
        host.Runs.Update(run);
        var faults = new HashSet<string>();
        host.Runs.PublishFaultForTest = candidate =>
        {
            var phase = candidate.OnlineHoeingHandoff?.Phase;
            return phase is "parked" or "observing" or "terminal" && faults.Add(phase)
                ? new IOException("temporary " + phase) : null;
        };
        var port = new Port();
        var boundary = new BgiWorkflowExecutionBoundary(port, host.Runs);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var continuing = host.AwaitOnlineHoeingContinuationAsync(boundary, run, original,
            BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"), ct.Token);
        await Until(() => host.Runs.Load(run.RunId)?.OnlineHoeingHandoff?.Phase == "parked");
        var request = await host.PreparePlanResumeAsync(run.RunId, "original-ticket", ct.Token);
        Assert.NotNull(request);
        var job = Guid.NewGuid().ToString("D");
        port.Job = Finished(run, (string)request!["idempotencyKey"]!, job);
        Assert.True(host.AcceptPlanResumeJob(run.RunId, "original-ticket", job));
        var result = await continuing.WaitAsync(ct.Token);
        Assert.False(result!.Uncertain);
        Assert.Equal("succeeded", result.Terminal);
        Assert.Equal(3, faults.Count);
        Assert.Equal(0, port.Sends);
        Assert.Equal(0, port.Cancels);
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal("terminal", saved.OnlineHoeingHandoff!.Phase);
        Assert.Equal("succeeded", saved.CurrentSubmission!.ObservedTerminal);
        Assert.Single(saved.SubmissionHistory);
        Assert.Single(saved.NodeOutcomes);
        Assert.Equal(2, saved.Cursor!.Attempt);
        var again = await host.AwaitOnlineHoeingContinuationAsync(boundary, run, original,
            BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"), ct.Token);
        Assert.Equal("succeeded", again!.Terminal);
        Assert.Single(host.Runs.Load(run.RunId)!.SubmissionHistory);
        Assert.Single(host.Runs.Load(run.RunId)!.NodeOutcomes);
    }

    [Fact]
    public async Task MissingResumeResult_KeepsOriginalObservation_UntilExplicitCancellation()
    {
        var host = Host(out var workflows, out var document);
        var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        var port = new Port { Missing = int.MaxValue };
        var boundary = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var handoff = new OnlineHoeingHandoffRecord
        { StateContinuation = true, Epoch = "42:99", NodeId = "B", Occurrence = 0, LoopIteration = 0 };
        using var ct = new CancellationTokenSource();
        var observing = boundary.AwaitOnlineResumeJobAsync(run, handoff, Guid.NewGuid().ToString("D"), "same-key", 2, ct.Token);
        await Until(() => port.Queries >= 3);
        Assert.False(observing.IsCompleted);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing);
        Assert.Equal(0, port.Sends);
        Assert.Equal(0, port.Cancels);
    }

    private sealed class Body(RunStore runs) : IWorkflowExecutionBoundary
    {
        internal readonly List<string> Submitted = [];
        private readonly Dictionary<string, string> _jobs = [];
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submitted.Add(request.Node.NodeId!);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            var job = Guid.NewGuid().ToString("D");
            _jobs.Add(job, request.Node.NodeId!);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith(job));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (_jobs[jobId] != "B") return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
            var run = runs.List().Single(candidate => candidate.CurrentSubmission?.JobId == jobId);
            var sub = run.CurrentSubmission!;
            run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
            {
                StateContinuation = true, Generation = 7, Ticket = "original-ticket", Epoch = "42:99",
                NodeId = "B", Occurrence = sub.Occurrence, LoopIteration = sub.LoopIteration, Attempt = sub.Attempt,
                OriginalJobId = jobId, OriginalSubmissionKey = sub.Key, Phase = "suspended",
            };
            runs.Update(run);
            return Task.FromResult(BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"));
        }
    }
    private sealed class Prerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }
    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed("unused"));
    }

    [Fact]
    public async Task OriginalRunner_ConsumesResumeOnce_AndExecutesNextNode_AfterOutcomeWriteFault()
    {
        var host = Host(out var workflows, out var document);
        var port = new Port();
        var resumed = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new Body(host.Runs);
        var runner = new WorkflowRunner(workflows, host.Runs, body, new Prerequisite(), new Terminal(),
            new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, original, result, ct)
                => host.AwaitOnlineHoeingContinuationAsync(resumed, run, original, result, ct) });
        var committedFault = 0;
        var loadFault = 0;
        var loadArmed = 0;
        host.Runs.PublishFaultForTest = candidate =>
        {
            if (candidate.OnlineHoeingHandoff?.Phase == "terminal" && Interlocked.CompareExchange(ref loadArmed, 1, 0) == 0)
                host.Runs.BeforeLoadForTest = _ =>
                {
                    if (Interlocked.CompareExchange(ref loadFault, 1, 0) == 0)
                        throw new IOException("online resume consumer rebase read");
                };
            return candidate.OnlineHoeingHandoff is null
                && candidate.OnlineHoeingHistory.Any(history => history.StateContinuation)
                && Interlocked.CompareExchange(ref committedFault, 1, 0) == 0
                    ? new IOException("online resume outcome publication") : null;
        };
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var driving = runner.StartAsync(document.WorkflowId!);
        WorkflowRunRecord? parked = null;
        await Until(() => (parked = host.Runs.List().FirstOrDefault(run => run.OnlineHoeingHandoff?.Phase == "parked")) != null);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "original-ticket", ct.Token);
        Assert.NotNull(request);
        var job = Guid.NewGuid().ToString("D");
        port.Job = Finished(parked, (string)request!["idempotencyKey"]!, job);
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "original-ticket", job));
        var finished = await driving.WaitAsync(ct.Token);
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(new[] { "B", "C" }, body.Submitted);
        Assert.Equal(1, committedFault);
        Assert.Equal(1, loadFault);
        Assert.Single(finished.NodeOutcomes.Where(outcome => outcome.SubmissionKey == request["idempotencyKey"] as string));
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Equal(0, port.Sends);
        Assert.Equal(0, port.Cancels);
    }
    [Fact]
    public async Task GapReleasePublicationThenReadFailureRetriesSameTicketAndRunsOnlyNextNode()
    {
        var flows = Path.Combine(_root, "gap-flows");
        var runs = Path.Combine(_root, "gap-runs");
        var workflows = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "B已完成只继续C", Activation = new() { Status = "active" },
            Nodes = new[] { "B", "C" }.Select(id => new WorkflowNode
            { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id, ConfigKey = id + "#k", Revision = "rev-1" } }).ToList(),
        };
        workflows.Save(document, null);
        var body = new Body(new RunStore(runs));
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "gap-catalog.json"), () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, body, new Prerequisite(), new Terminal()),
            () => (true, null), onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        run.State = WorkflowRunState.LocalWaitParking; run.TriggerConsumed = true; run.OnlineHoeingGateParked = true;
        run.Cursor = new() { NodeId = "C", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.NodeOutcomes.Add(new() { NodeId = "B", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.OnlineHoeingHandoff = new() { StateContinuation = true, Generation = 7, Ticket = "gap-ticket", Epoch = "42:99",
            NodeId = "C", Occurrence = 0, LoopIteration = 0, Attempt = 1, Phase = "gap" };
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        var armed = 0; var faults = 0;
        host.Runs.PublishFaultForTest = candidate =>
        {
            if (candidate.OnlineHoeingHandoff is null && candidate.OnlineHoeingHistory.Any(h => h.Phase == "released")
                && Interlocked.CompareExchange(ref armed, 1, 0) == 0)
                host.Runs.BeforeLoadForTest = _ =>
                { if (Interlocked.Increment(ref faults) == 1) throw new IOException("read after release publication"); };
            return null;
        };
        await Assert.ThrowsAsync<IOException>(() => host.FinishOnlineGapAfterReleaseAsync(run.RunId, "gap-ticket"));
        Assert.Null(host.Runs.Load(run.RunId)!.OnlineHoeingHandoff);
        Assert.True(await host.FinishOnlineGapAfterReleaseAsync(run.RunId, "gap-ticket"));
        await Until(() => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Succeeded);
        Assert.True(await host.FinishOnlineGapAfterReleaseAsync(run.RunId, "gap-ticket"));
        Assert.Equal(new[] { "C" }, body.Submitted);
        Assert.Single(host.Runs.Load(run.RunId)!.OnlineHoeingHistory);
        Assert.False(await host.FinishOnlineGapAfterReleaseAsync(run.RunId, "other-ticket"));
        await host.ShutdownAsync();
    }

}
