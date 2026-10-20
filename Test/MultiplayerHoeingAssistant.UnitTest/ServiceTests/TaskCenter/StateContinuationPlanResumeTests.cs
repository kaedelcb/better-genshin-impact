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
    private static BgiJobInfo Finished(WorkflowRunRecord run, string key, string job, string state = "succeeded") => new()
    {
        Epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 }, JobId = Guid.Parse(job).ToString("N"),
        Source = "Resume", State = state, WorkflowRunId = run.WireRunId, NodeId = "B",
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

    private sealed class Body(RunStore runs, bool priorityRecovery = false) : IWorkflowExecutionBoundary
    {
        internal readonly List<string> Submitted = [];
        internal readonly List<string> Keys = [];
        internal int RetryFailures;
        internal Action<WorkflowRunRecord>? BeforeHandoff;
        internal TaskCompletionSource? HostTakeover;
        internal TaskCompletionSource? RetryCompletion;
        private bool _preempted;
        private readonly Dictionary<string, string> _jobs = [];
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submitted.Add(request.Node.NodeId!);
            Keys.Add(request.Run.CurrentSubmission!.Key);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            var job = Guid.NewGuid().ToString("D");
            _jobs.Add(job, request.Node.NodeId!);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith(job));
        }
        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (_jobs[jobId] != "B" || _preempted)
            {
                if (_jobs[jobId] == "B" && RetryCompletion is not null) await RetryCompletion.Task.WaitAsync(ct);
                return BoundaryTerminalResult.Observed(RetryFailures-- > 0 ? "failed" : "succeeded");
            }
            _preempted = true;
            if (HostTakeover is not null)
            {
                await HostTakeover.Task.WaitAsync(ct);
                return BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted");
            }
            var run = runs.List().Single(candidate => candidate.CurrentSubmission?.JobId == jobId);
            var sub = run.CurrentSubmission!;
            BeforeHandoff?.Invoke(run);
            run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
            {
                StateContinuation = true, PriorityRecovery = priorityRecovery, Generation = 7, Ticket = "original-ticket", Epoch = "42:99",
                NodeId = "B", Occurrence = sub.Occurrence, LoopIteration = sub.LoopIteration, Attempt = sub.Attempt,
                OriginalJobId = jobId, OriginalSubmissionKey = sub.Key, Phase = "suspended",
            };
            runs.Update(run);
            return BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted");
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PriorityOriginalRunnerRetriesFailedResumePastOrdinaryLimitAndThenContinues(bool priority)
    {
        var host = Host(out var workflows, out var document);
        var port = new Port { Missing = 0 };
        var resumed = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new Body(host.Runs, priority) { RetryFailures = 4 };
        var runner = new WorkflowRunner(workflows, host.Runs, body, new Prerequisite(), new Terminal(),
            new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, original, result, ct)
                => host.AwaitOnlineHoeingContinuationAsync(resumed, run, original, result, ct) });
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        var driving = runner.StartAsync(document.WorkflowId!, ct: ct.Token);
        WorkflowRunRecord? parked = null;
        await Until(() => (parked = host.Runs.List().FirstOrDefault(r => r.OnlineHoeingHandoff?.Phase == "parked")) != null);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "original-ticket", ct.Token);
        Assert.NotNull(request);
        var job = Guid.NewGuid().ToString("D");
        port.Job = Finished(parked, (string)request!["idempotencyKey"]!, job, "failed");
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "original-ticket", job));
        var finished = await driving.WaitAsync(ct.Token);
        Assert.Equal(priority ? WorkflowRunState.Succeeded : WorkflowRunState.Failed, finished.State);
        Assert.Equal(priority ? new[] { "B", "B", "B", "B", "B", "B", "C" } : new[] { "B" }, body.Submitted);
        Assert.Equal(body.Keys.Count, body.Keys.Distinct().Count());
        Assert.Equal(priority ? 5 : 1, finished.NodeOutcomes.Count(o => o.RawTerminal == "failed"));
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Equal(priority, finished.OnlineHoeingHistory[0].PriorityRecovery);
        Assert.Equal(0, port.Sends);
    }

    [Theory]
    [InlineData("resumeRequested", false)]
    [InlineData("observing", false)]
    [InlineData("terminal", false)]
    [InlineData("resumeRequested", true)]
    public async Task ReconstructedHostKeepsOriginalResumeIdentityAndContinuesCOnce(string savedPhase, bool stopped)
    {
        var originalHost = Host(out var workflows, out var document);
        var run = originalHost.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        var oldJob = Guid.NewGuid().ToString("D");
        var original = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1), NodeId = "B", Attempt = 1,
            JobId = oldJob, Intent = SubmitIntentState.Accepted, SendAttempted = true, Epoch = "42:99",
            WireRunId = run.WireRunId, Fingerprint = "original-frozen", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            ObservedTerminal = "cancelled", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "cancelled",
        };
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.Cursor = new() { NodeId = "B", Attempt = 1 };
        run.CurrentSubmission = original;
        run.OnlineHoeingHandoff = new()
        {
            StateContinuation = true, PriorityRecovery = true, Generation = 7, Ticket = "original-ticket",
            Epoch = "42:99", NodeId = "B", Attempt = 1, OriginalJobId = oldJob,
            OriginalSubmissionKey = original.Key, Phase = "parked",
        };
        originalHost.Runs.Update(run);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = await originalHost.PreparePlanResumeAsync(run.RunId, "original-ticket", budget.Token);
        Assert.NotNull(request);
        var resumeJob = Guid.NewGuid().ToString("D");
        var port = new Port { Missing = 0, Job = Finished(run, (string)request!["idempotencyKey"]!, resumeJob,
            savedPhase == "terminal" ? "succeeded" : "running") };
        if (savedPhase != "resumeRequested")
        {
            Assert.True(originalHost.AcceptPlanResumeJob(run.RunId, "original-ticket", resumeJob));
            using var previousObserver = new CancellationTokenSource();
            var oldObservation = originalHost.AwaitOnlineHoeingContinuationAsync(new(port, originalHost.Runs), run, original,
                BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"), previousObserver.Token);
            if (savedPhase == "terminal") await oldObservation.WaitAsync(budget.Token);
            else
            {
                await Until(() => originalHost.Runs.Load(run.RunId)!.OnlineHoeingHandoff!.Phase == "observing");
                previousObserver.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldObservation);
            }
        }
        if (stopped) originalHost.Runs.UpdateMergingIf(run.RunId, current => { current.StopRequested = true; return true; }, out _);
        await originalHost.ShutdownAsync();
        var originalBatch = new OnlineHoeingBatch(new("restored-plan", 7, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] }
        }, "self", new() { Policy = TaskConflictPolicy.Resume })
        {
            ExecutionEpoch = new() { ProcessId = 42, StartTicksUtc = 99 }, StateContinuationStopVersion = 0,
            StateContinuationEnabled = true, TakenOver = true, TakeoverTicket = "original-ticket", ManagedRunId = run.RunId,
            PolicyStartAttempted = true, PlanResumeContinuation = request,
        };
        originalBatch.MarkLocalSuccess(originalBatch.ExecutionEpoch);
        originalBatch.EnsureRecoveryStopSignal();
        var batch = OnlineHoeingBatch.Restore(originalBatch.CaptureRecovery());
        Body? body = null;
        var builds = 0;
        TaskCenterHost? reconstructed = null;
        reconstructed = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "new-catalog.json"),
            () => null, null, (_, w, r) =>
            {
                Interlocked.Increment(ref builds);
                body = new Body(r);
                var resumed = new BgiWorkflowExecutionBoundary(port, r);
                return new WorkflowRunner(w, r, body, new Prerequisite(), new Terminal(),
                    new WorkflowRunnerOptions { OnlineHoeingContinuation = (current, sub, terminal, token)
                        => reconstructed!.AwaitOnlineHoeingContinuationAsync(resumed, current, sub, terminal, token) });
            }, () => (true, null), onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        try
        {
            var restoredRun = await reconstructed.RestorePriorityPlanAsync(batch);
            if (stopped)
            {
                Assert.Null(restoredRun); Assert.True(batch.IsCancellationRequested); Assert.Equal(0, builds);
                return;
            }
            Assert.Equal(run.RunId, restoredRun);
            Assert.Equal(run.RunId, reconstructed.OnlineHoeingManagedRun(7));
            if (savedPhase != "terminal")
            {
                Assert.Equal(run.RunId, await reconstructed.RestorePriorityPlanAsync(batch));
                Assert.Equal(1, builds);
                Assert.True(reconstructed.AcceptPlanResumeJob(run.RunId, "original-ticket", resumeJob));
                await Until(() => reconstructed.Runs.Load(run.RunId)!.OnlineHoeingHandoff?.Phase == "observing");
                Assert.Empty(body!.Submitted);
                port.Job = Finished(run, (string)request!["idempotencyKey"]!, resumeJob);
            }
            await Until(() => reconstructed.Runs.Load(run.RunId)?.State == WorkflowRunState.Succeeded);
            Assert.Equal(new[] { "C" }, body!.Submitted);
            Assert.Single(reconstructed.Runs.Load(run.RunId)!.OnlineHoeingHistory);
            Assert.Single(reconstructed.Runs.Load(run.RunId)!.NodeOutcomes.Where(o => o.SubmissionKey == request!["idempotencyKey"] as string));
            Assert.Equal(0, port.Sends); Assert.Equal(0, port.Cancels);
        }
        finally
        {
            await reconstructed.ShutdownAsync();
            batch.RecoveryStopSignal?.Dispose(); originalBatch.RecoveryStopSignal?.Dispose();
        }
    }

    [Fact]
    public async Task LiveHostPriorityPreparationRetainsOriginalRunnerWhenRecordDirectoryFails()
    {
        var flows = Path.Combine(_root, "host-flows");
        var runs = Path.Combine(_root, "host-runs");
        var workflows = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "实际宿主原计划", Activation = new() { Status = "active" },
            Nodes = new[] { "B", "C" }.Select(id => new WorkflowNode
            { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id, ConfigKey = id + "#k", Revision = "rev-1" } }).ToList(),
        };
        workflows.Save(document, null);
        var port = new Port { Missing = 0 };
        Body? body = null;
        TaskCenterHost? host = null;
        host = new TaskCenterHost(flows, runs, Path.Combine(_root, "host-catalog.json"), () => null, null,
            (_, w, r) =>
            {
                body = new Body(r, true) { HostTakeover = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                var resumed = new BgiWorkflowExecutionBoundary(port, r);
                return new WorkflowRunner(w, r, body, new Prerequisite(), new Terminal(),
                    new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, sub, result, ct)
                        => host!.AwaitOnlineHoeingContinuationAsync(resumed, run, sub, result, ct) });
            }, () => (true, null), onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        try
        {
            Assert.True((await host.StartWorkflowAsync(document.WorkflowId!)).Ok);
            await Until(() => body?.Submitted.Count == 1 && host.Runs.List().Any(r => r.CurrentSubmission?.JobId is not null));
            var original = host.Runs.List().Single();
            var job = Guid.Parse(original.CurrentSubmission!.JobId!);
            await host.CloseOnlineHoeingSendAsync(priority: true);
            host.Runs.FileOperationFaultForTest = op => op == "enumerate" ? new IOException("directory unreadable") : null;
            host.Runs.PublishFaultForTest = _ => new IOException("progress unwritable");
            var prepared = host.PreparePriorityOnlineHoeing(7, new(true, true, 42, 99,
                new(Guid.NewGuid(), 42, 99, Guid.Parse(original.WireRunId), job)));
            Assert.True(prepared.Ready);
            Assert.Equal(original.RunId, prepared.ManagedRunId);
            Assert.True(host.Runs.HasPriorityProgress(original.RunId));
            Assert.True(host.ConfirmOnlineHoeingSuspend(7, "original-ticket", System.Text.Json.JsonSerializer.Serialize(new
            { status = "suspended", workflowRunId = original.WireRunId, nodeId = "B", originalJobId = job,
                occurrence = 0, iteration = 0, attempt = 1, planCheckpointPhase = "inside", planNextTaskIndex = 0 })));
            body!.HostTakeover!.SetResult();
            await Until(() => host.Runs.Load(original.RunId)!.OnlineHoeingHandoff?.Phase == "parked");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var request = await host.PreparePlanResumeAsync(original.RunId, "original-ticket", budget.Token);
            Assert.NotNull(request);
            var resumedJob = Guid.NewGuid().ToString("D");
            port.Job = Finished(original, (string)request!["idempotencyKey"]!, resumedJob);
            Assert.True(host.AcceptPlanResumeJob(original.RunId, "original-ticket", resumedJob));
            Assert.True(host.CanOpenRetainedPrioritySend(7, original.RunId));
            Assert.False(host.CanOpenRetainedPrioritySend(8, original.RunId));
            await host.OpenOnlineHoeingSendAsync(priorityRecovery: true);
            await Until(() => host.Runs.Load(original.RunId)!.State == WorkflowRunState.Succeeded);
            Assert.Equal(new[] { "B", "C" }, body.Submitted);
            Assert.Equal(0, port.Sends);
        }
        finally
        {
            host.Runs.FileOperationFaultForTest = null;
            host.Runs.PublishFaultForTest = null;
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task BoundCompletionFollowsOriginalRunnerRetryInsteadOfFinishingOnFirstResumeFailure()
    {
        var host = Host(out var workflows, out var document);
        var port = new Port { Missing = 0 };
        var resumed = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new Body(host.Runs, true) { RetryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = new WorkflowRunner(workflows, host.Runs, body, new Prerequisite(), new Terminal(),
            new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, original, result, ct)
                => host.AwaitOnlineHoeingContinuationAsync(resumed, run, original, result, ct) });
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var driving = runner.StartAsync(document.WorkflowId!, ct: budget.Token);
        WorkflowRunRecord? parked = null;
        await Until(() => (parked = host.Runs.List().FirstOrDefault(r => r.OnlineHoeingHandoff?.Phase == "parked")) != null);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "original-ticket", budget.Token);
        var oldJob = Guid.NewGuid().ToString("D");
        port.Job = Finished(parked, (string)request!["idempotencyKey"]!, oldJob, "failed");
        var batch = new OnlineHoeingBatch(new("managed-retry", 7, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] }
        }, "self", new() { Policy = TaskConflictPolicy.Resume })
        { ExecutionEpoch = new() { ProcessId = 42, StartTicksUtc = 99 }, ManagedRunId = parked.RunId,
            PlanResumeContinuation = request, StateContinuationEnabled = true, PolicyTicketReleased = true, PolicyStartAttempted = true };
        batch.MarkLocalSuccess(batch.ExecutionEpoch);
        batch.RecordPolicyExecution(new(oldJob, batch.ExecutionEpoch, true));
        batch.PolicyTerminalResult = new() { TaskHandle = oldJob, Epoch = batch.ExecutionEpoch,
            IdempotencyKey = (string)request["idempotencyKey"]!, Status = "failed", ExecutionExitConfirmed = true };
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "original-ticket", oldJob));
        var worker = ViewModels.MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => budget.Token.ThrowIfCancellationRequested(),
            Prepare = _ => throw new InvalidOperationException("only the original Runner may submit retries"),
            Start = _ => throw new InvalidOperationException("completion worker must not start another task"),
            Reconcile = _ => Task.FromResult(host.ObserveManagedCompletionRetry(batch, parked.RunId)),
            ExpectedRequestKey = () => batch.PolicyRequestIdentity ?? (string)request["idempotencyKey"]!,
            Query = _ =>
            {
                var saved = host.Runs.Load(parked.RunId)!;
                var sub = saved.SubmissionHistory.Concat(saved.CurrentSubmission is { } current ? new[] { current } : [])
                    .Single(s => s.JobId == batch.PolicyExecution!.JobId);
                return Task.FromResult<BgiTaskQueueStatus?>(new() { TaskHandle = sub.JobId,
                    IdempotencyKey = sub.Key, Epoch = batch.ExecutionEpoch, ExecutionExitConfirmed = sub.ExecutionExitConfirmed,
                    Status = sub.ObservedTerminal == "succeeded" ? "completed" : sub.ObservedTerminal ?? "running" });
            },
            Acknowledge = (_, _) => Task.FromResult(true), Delay = ct => Task.Delay(10, ct),
        }, budget.Token);
        try
        {
            await Until(() => body.Submitted.Count == 2 && batch.PolicyRequestIdentity is not null);
            Assert.False(worker.IsCompleted);
            Assert.NotEqual(oldJob, batch.PolicyExecution!.JobId);
            Assert.Equal(oldJob, Assert.Single(batch.FailedPolicyAttempts).TaskHandle);
            body.RetryCompletion.SetResult();
            await worker.WaitAsync(budget.Token);
            Assert.Equal(WorkflowRunState.Succeeded, (await driving.WaitAsync(budget.Token)).State);
            Assert.Equal(new[] { "B", "B", "C" }, body.Submitted);
            Assert.Equal("completed", batch.PolicyTerminalResult!.Status);
        }
        finally { body.RetryCompletion.TrySetResult(); budget.Cancel(); }
    }

    [Fact]
    public async Task OriginalRunnerConsumesResumeAndContinuesWhileProgressReadsAndWritesKeepFailing()
    {
        var host = Host(out var workflows, out var document);
        var port = new Port { Missing = 0 };
        var resumed = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new Body(host.Runs, true);
        byte[]? originalFile = null;
        string? runFile = null;
        body.BeforeHandoff = run =>
        {
            host.Runs.RetainPriorityProgress(run);
            runFile = Path.Combine(_root, "runs", run.RunId + ".run.json");
            originalFile = File.ReadAllBytes(runFile);
            host.Runs.PublishFaultForTest = _ => new IOException("persistent progress publication failure");
            host.Runs.BeforeLoadForTest = _ => throw new IOException("persistent progress read failure");
        };
        var runner = new WorkflowRunner(workflows, host.Runs, body, new Prerequisite(), new Terminal(),
            new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, original, result, ct)
                => host.AwaitOnlineHoeingContinuationAsync(resumed, run, original, result, ct) });
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var driving = runner.StartAsync(document.WorkflowId!, ct: ct.Token);
        WorkflowRunRecord? parked = null;
        await Until(() => (parked = host.Runs.List().FirstOrDefault(r => r.OnlineHoeingHandoff?.Phase == "parked")) != null);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "original-ticket", ct.Token);
        Assert.NotNull(request);
        var job = Guid.NewGuid().ToString("D");
        port.Job = Finished(parked, (string)request!["idempotencyKey"]!, job);
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "original-ticket", job));
        var finished = await driving.WaitAsync(ct.Token);
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(new[] { "B", "C" }, body.Submitted);
        Assert.NotNull(host.Runs.PriorityPersistenceError(finished.RunId));
        Assert.Equal(originalFile, File.ReadAllBytes(runFile!));
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Equal(0, port.Sends);
        host.Runs.PublishFaultForTest = null;
        host.Runs.BeforeLoadForTest = null;
        while (host.Runs.HasUnpublishedPriorityProgress(finished.RunId))
            host.Runs.FlushPriorityProgress(finished.RunId);
        Assert.Null(host.Runs.PriorityPersistenceError(finished.RunId));
        var disk = new RunStore(Path.Combine(_root, "runs")).Load(finished.RunId)!;
        Assert.Equal(finished.RecordRevision, disk.RecordRevision);
        Assert.Equal(WorkflowRunState.Succeeded, disk.State);
        Assert.Equal(finished.NodeOutcomes.Count, disk.NodeOutcomes.Count);
        Assert.Single(disk.OnlineHoeingHistory);
        Assert.Equal(finished.CurrentSubmission!.Key, disk.CurrentSubmission!.Key);
    }

    [Fact]
    public async Task ExplicitStopDuringPriorityRetryDelayPreventsNextSubmission()
    {
        var host = Host(out var workflows, out var document);
        var port = new Port { Missing = 0 };
        var resumed = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new Body(host.Runs, true) { RetryFailures = 100 };
        var runner = new WorkflowRunner(workflows, host.Runs, body, new Prerequisite(), new Terminal(),
            new WorkflowRunnerOptions { OnlineHoeingContinuation = (run, original, result, ct)
                => host.AwaitOnlineHoeingContinuationAsync(resumed, run, original, result, ct) });
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var driving = runner.StartAsync(document.WorkflowId!, ct: ct.Token);
        WorkflowRunRecord? parked = null;
        await Until(() => (parked = host.Runs.List().FirstOrDefault(r => r.OnlineHoeingHandoff?.Phase == "parked")) != null);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "original-ticket", ct.Token);
        var job = Guid.NewGuid().ToString("D");
        port.Job = Finished(parked, (string)request!["idempotencyKey"]!, job, "failed");
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "original-ticket", job));
        await Until(() => host.Runs.Load(parked.RunId)!.NodeOutcomes.Any(o => o.RawTerminal == "failed"));
        ct.Cancel();
        var stopped = await driving.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(stopped.StopRequested);
        Assert.Equal(new[] { "B" }, body.Submitted);
        Assert.DoesNotContain(stopped.NodeOutcomes, o => o.Result == "succeeded");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task PriorityWaiterRecoveryDoesNotReopenANewerBatchGate(bool newerBatch, bool temporarilyUnavailable, bool runningPublicationFails)
    {
        var flows = Path.Combine(_root, "wake-flows");
        var runs = Path.Combine(_root, "wake-runs");
        var workflows = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "等待恢复C", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "C", Kind = "resource.configGroup",
                Ref = new() { Config = "C", ConfigKey = "C#k", Revision = "rev-1" } }],
        };
        workflows.Save(document, null);
        var body = new Body(new RunStore(runs));
        var ready = !temporarilyUnavailable;
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "wake-catalog.json"), () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, body, new Prerequisite(), new Terminal()),
            () => (ready, ready ? null : "controlled environment not ready"), onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var gate = (TaskCenterOnlineSendGate)typeof(TaskCenterHost).GetField("_onlineSendGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
        try
        {
            var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
            run.State = WorkflowRunState.LocalWaitParking;
            run.TriggerConsumed = true;
            run.OnlineHoeingGateParked = true;
            run.Cursor = new() { NodeId = "C", Attempt = 1 };
            host.Runs.Update(run);
            await host.CloseOnlineHoeingSendAsync(priority: true);
            host.Runs.FileOperationFaultForTest = op => op == "enumerate" ? new IOException("old records unavailable") : null;
            await host.OpenOnlineHoeingSendAsync(priorityRecovery: true);
            Assert.False(gate.IsClosed);
            Assert.Empty(body.Submitted);
            if (newerBatch) await host.CloseOnlineHoeingSendAsync(priority: true);
            host.Runs.FileOperationFaultForTest = null;
            var publicationFailures = 0;
            if (runningPublicationFails)
                host.Runs.PublishFaultForTest = current => current.State == WorkflowRunState.Running
                    && Interlocked.CompareExchange(ref publicationFailures, 1, 0) == 0
                        ? new IOException("one Running publication failure") : null;
            host.RetryPriorityWaiterWake();
            if (temporarilyUnavailable || runningPublicationFails)
            {
                await Until(() => typeof(TaskCenterHost).GetField("_priorityWaiterWakeTask",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host) is Task { IsCompleted: true });
                Assert.Empty(body.Submitted);
                Assert.True(host.Runs.Load(run.RunId)!.OnlineHoeingGateParked);
                if (runningPublicationFails)
                {
                    await Until(() => !host.IsDriving(document.WorkflowId!)
                        && host.Runs.Load(run.RunId)!.State == WorkflowRunState.Interrupted);
                    Assert.Equal(1, publicationFailures);
                }
                ready = true;
                host.RetryPriorityWaiterWake();
            }
            if (newerBatch)
            {
                await Task.Delay(100);
                Assert.True(gate.IsClosed);
                Assert.Empty(body.Submitted);
                await host.OpenOnlineHoeingSendAsync(priorityRecovery: true);
            }
            await Until(() => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Succeeded);
            Assert.Equal(new[] { "C" }, body.Submitted);
            Assert.False(host.Runs.Load(run.RunId)!.OnlineHoeingGateParked);
        }
        finally { host.Runs.FileOperationFaultForTest = null; host.Runs.PublishFaultForTest = null; await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task PriorityWaiterRemainsPendingWhenOriginalDriverTakesLongerThanWakeWindowToExit()
    {
        var flows = Path.Combine(_root, "late-drive-flows");
        var runs = Path.Combine(_root, "late-drive-runs");
        var workflows = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "原驱动慢退出", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "C", Kind = "resource.configGroup",
                Ref = new() { Config = "C", ConfigKey = "C#k", Revision = "rev-1" } }],
        };
        workflows.Save(document, null);
        using var release = new ManualResetEventSlim();
        var parkedDriver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var body = new Body(new RunStore(runs));
        TaskCenterOnlineSendGate? gate = null;
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "late-drive-catalog.json"), () => null,
            log: message =>
            {
                if (message.Contains("终态：LocalWaitParking") && Interlocked.CompareExchange(ref held, 1, 0) == 0)
                { parkedDriver.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(12))); }
            },
            runnerFactory: (_, w, r) => new WorkflowRunner(w, r, body, new Prerequisite(), new Terminal(),
                new WorkflowRunnerOptions { OnlineHoeingSendClosed = () => gate!.IsClosed }),
            readinessOverride: () => (true, null), onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        gate = (TaskCenterOnlineSendGate)typeof(TaskCenterHost).GetField("_onlineSendGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
        try
        {
            await host.CloseOnlineHoeingSendAsync(priority: true);
            var starting = Task.Run(() => host.StartWorkflowAsync(document.WorkflowId!));
            await parkedDriver.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var run = host.Runs.List().Single();
            Assert.True(host.IsDriving(document.WorkflowId!));
            await host.OpenOnlineHoeingSendAsync(priorityRecovery: true);
            Assert.True(host.Runs.Load(run.RunId)!.OnlineHoeingGateParked);
            Assert.Empty(body.Submitted);
            release.Set();
            Assert.True((await starting).Ok);
            await Until(() => !host.IsDriving(document.WorkflowId!));
            host.RetryPriorityWaiterWake();
            await Until(() => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Succeeded);
            Assert.Equal(new[] { "C" }, body.Submitted);
        }
        finally { release.Set(); await host.ShutdownAsync(); }
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
