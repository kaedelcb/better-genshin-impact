using System.Collections.Concurrent;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

// Exercises the actual Host registration/driver path, including the request-key binding.
// Gates distinguish registration and first-node completion from completion of the entire run.
public sealed class CompletionWorkflowHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "completion-host-" + Guid.NewGuid().ToString("N"));
    private static readonly BgiEpoch Epoch = new() { ProcessId = 41, StartTicksUtc = 410 };
    public void Dispose() { try { Directory.Delete(_directory, recursive: true); } catch { } }

    private sealed class Boundary : IWorkflowExecutionBoundary
    {
        public ConcurrentQueue<string> Nodes { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SingleNativeSupported => false;
        public bool RequiresStopAuthority => true;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string intentId, long timestamp, CancellationToken ct)
            => throw new InvalidOperationException("自动后继不得重签手动启动授权");
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
            => Task.FromResult<bool?>(authority.Epoch == "41:410" && authority.Version == 3);
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            Nodes.Enqueue(request.Occurrence.NodeId);
            if (request.Occurrence.NodeId == "first") FirstStarted.TrySetResult(); else SecondStarted.TrySetResult();
            return Task.FromResult(BoundarySubmitResult.AcceptedWith(request.Occurrence.NodeId));
        }
        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            await (jobId == "first" ? FirstExit.Task : SecondExit.Task).WaitAsync(ct);
            return BoundaryTerminalResult.Observed("succeeded");
        }
        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        { FirstExit.TrySetResult(); SecondExit.TrySetResult(); return Task.CompletedTask; }
    }
    private sealed class Prerequisites : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }
    private sealed class Completion : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed("terminal"));
    }

    private TaskCenterHost Host(Boundary boundary, TaskCenterAdmissionSeams? admissionSeams = null) => new(
        Path.Combine(_directory, "flows"), Path.Combine(_directory, "runs"), Path.Combine(_directory, "catalog.json"),
        () => null, null, (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, new Prerequisites(), new Completion()),
        () => (true, null), admissionWired: true,
        admissionSeams: admissionSeams ?? new TaskCenterAdmissionSeams { Epoch = "41:410", Occupied = false, FactsUnknown = false });

    // The round sees idle, then another task occupies the slot before the final atomic check.
    // The real admission service must persist RetryableRejected; no sender override manufactures it.
    private sealed class RegistrationRace
    {
        private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;
        private int _occupied;
        private int _occupyChecks;
        public int OccupyChecks => Volatile.Read(ref _occupyChecks);
        public TaskCenterAdmissionSeams Seams { get; }
        public RegistrationRace()
        {
            Seams = new()
            {
                Epoch = "41:410", FactsUnknown = false,
                OccupiedProvider = () => Volatile.Read(ref _occupied) != 0,
                UtcNow = () => new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero),
                // Expiring the retry window must not also expire the unrelated owner lease.
                OwnershipTtlSeconds = 120, SuppressLeaseHeartbeatForTest = true,
                Barriers = new()
                {
                    BeforeOccupyPublish = () =>
                    {
                        if (Interlocked.Increment(ref _occupyChecks) == 1) Volatile.Write(ref _occupied, 1);
                        return Task.CompletedTask;
                    },
                },
            };
        }
        public void ReleaseOccupancy() => Volatile.Write(ref _occupied, 0);
        public void AdvancePast(DateTimeOffset deadline) => Interlocked.Exchange(ref _utcTicks, deadline.AddTicks(1).UtcTicks);
    }

    private OperationRecord Registration(RegistrationRace race)
    {
        var read = new ArbitrationLeaseStore(Path.Combine(_directory, "arbitration"), race.Seams.UtcNow).Read();
        Assert.NotNull(read.File?.Handoff);
        return Assert.Single(read.File!.Handoff!.Operations.Where(op => op.OperationType == OperationType.FlowRegistration));
    }

    private async Task<OperationRecord> RejectFirstRegistrationAsync(TaskCenterHost host, Boundary boundary,
        OnlineHoeingBatch batch, RegistrationRace race)
    {
        var rejected = await host.StartCompletionWorkflowAsync(batch, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("execution_occupied", rejected.ErrorCode);
        var run = Assert.Single(host.Runs.List());
        Assert.Equal(WorkflowRunState.Planned, run.State);
        Assert.Equal(run.RunId, batch.PolicyExecution!.WorkflowRunId);
        Assert.True(batch.PolicyStartAttempted);
        var registration = Registration(race);
        Assert.Equal(run.RunId, registration.RunBinding);
        Assert.Equal(batch.PolicyRequestKey, registration.WireSubmitKey);
        Assert.Equal(OperationRequestState.RetryableRejected, registration.RequestState);
        Assert.NotNull(registration.RetryWindowDeadlineUtc);
        Assert.Equal(0, registration.LastSendSeq);
        Assert.Empty(boundary.Nodes);
        return registration;
    }

    private static OnlineHoeingBatch Batch(WorkflowSnapshot snapshot, string mode = "beginning")
    {
        var batch = new OnlineHoeingBatch(new("workflow-successor", 9, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> { ["self"] = [new("锄地", "group")] },
        }, "self", new TaskConflictPolicySettings
        {
            Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = "workflow",
            SpecifiedTaskName = snapshot.Document.Name, SpecifiedWorkflowId = snapshot.Document.WorkflowId,
            SpecifiedConfigRevision = snapshot.Revision, SpecifiedStartMode = mode,
            SpecifiedStartTaskId = mode == "node" ? "second" : "", SpecifiedStartTaskName = mode == "node" ? "第二个" : "",
        }) { ExecutionEpoch = Epoch, StateContinuationEnabled = true, StateContinuationStopVersion = 3, PolicyTicketReleased = true };
        batch.MarkLocalSuccess(Epoch);
        return batch;
    }

    private static WorkflowSnapshot Seed(TaskCenterHost host)
    {
        var document = new WorkflowDocument
        {
            Name = "绑定计划表", Activation = new() { Status = "active" }, Nodes =
            [
                new() { NodeId = "first", Kind = "resource.oneDragonConfig", Ref = new() { Config = "A", Revision = "a" } },
                new() { NodeId = "second", Kind = "resource.oneDragonConfig", Ref = new() { Config = "B", Revision = "b" } },
            ],
        };
        host.SaveFlow(document, null);
        return host.LoadFlowSnapshot(document.WorkflowId!);
    }

    [Fact]
    public async Task SameRequestBindsOneRealRunAndTracksBothNodes()
    {
        var boundary = new Boundary(); var host = Host(boundary);
        try
        {
            var batch = Batch(Seed(host));
            var results = await Task.WhenAll(host.StartCompletionWorkflowAsync(batch, CancellationToken.None),
                host.StartCompletionWorkflowAsync(batch, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => Assert.Equal("success", result.Status));
            Assert.Equal(results[0].JobId, results[1].JobId);
            var run = Assert.Single(host.Runs.List());
            Assert.Equal(run.RunId, results[0].JobId);
            Assert.Equal(batch.PolicyRequestKey, Assert.Single(run.Handoffs).IntentKey);
            Assert.Equal(3, run.StopAuthority!.Version);
            Assert.Equal(batch.CompletionIntentTimestamp, run.StopAuthority.IntentTimestamp);
            await boundary.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await host.QueryCompletionWorkflowAsync(batch, CancellationToken.None))!.ExecutionExitConfirmed);
            boundary.FirstExit.TrySetResult();
            await boundary.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await host.QueryCompletionWorkflowAsync(batch, CancellationToken.None))!.ExecutionExitConfirmed);
            boundary.SecondExit.TrySetResult();
            BgiTaskQueueStatus? status = null;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do
            {
                status = await host.QueryCompletionWorkflowAsync(batch, budget.Token);
                if (!status!.ExecutionExitConfirmed) await Task.Delay(10, budget.Token);
            } while (!status!.ExecutionExitConfirmed);
            Assert.Equal("completed", status.Status);
            Assert.Equal(new[] { "first", "second" }, boundary.Nodes.ToArray());
            var restored = OnlineHoeingBatch.Restore(batch.CaptureRecovery());
            Assert.Equal(run.RunId, restored.PolicyExecution!.WorkflowRunId);
            var replay = await host.StartCompletionWorkflowAsync(restored, CancellationToken.None);
            Assert.Equal(run.RunId, replay.JobId); Assert.Single(host.Runs.List());
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task NodeStartDoesNotRunEarlierNodeAndStopAddressesEntireRun()
    {
        var boundary = new Boundary(); var host = Host(boundary);
        try
        {
            var batch = Batch(Seed(host), "node");
            var accepted = await host.StartCompletionWorkflowAsync(batch, CancellationToken.None);
            Assert.Equal("success", accepted.Status);
            await boundary.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "second" }, boundary.Nodes.ToArray());
            var execution = batch.PolicyExecution!;
            Assert.Equal(accepted.JobId, execution.WorkflowRunId);
            Assert.True(await host.CancelCompletionWorkflowAsync(execution).WaitAsync(TimeSpan.FromSeconds(20)));
            var run = host.Runs.Load(execution.WorkflowRunId!);
            Assert.True(run!.StopRequested);
            Assert.Equal(WorkflowRunState.Cancelled, run.State);
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task CrashAfterCreateContinuesSameUnexecutedRecord()
    {
        var boundary = new Boundary(); var host = Host(boundary);
        try
        {
            var snapshot = Seed(host); var batch = Batch(snapshot, "node");
            host.EnsureRecovered();
            var run = host.Runs.CreateRun(snapshot.Document.WorkflowId!, snapshot.Revision,
                handoff: new HandoffIdentity { IntentKey = batch.PolicyRequestKey, ExecutionId = batch.BatchId,
                    StepId = "post-hoeing", Mode = StartupHandoffModes.Start },
                stopAuthority: new("41:410", 3, batch.PolicyRequestKey, batch.CompletionIntentTimestamp,
                    System.Diagnostics.Stopwatch.Frequency),
                initialCursor: TaskCenterHost.ResolveCompletionWorkflowCursor(batch.Policy!, snapshot));
            run.State = WorkflowRunState.Interrupted; host.Runs.Update(run);
            var accepted = await host.StartCompletionWorkflowAsync(batch, CancellationToken.None);
            Assert.Equal(run.RunId, accepted.JobId);
            await boundary.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(host.Runs.List()); Assert.Equal(new[] { "second" }, boundary.Nodes.ToArray());
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task RetryableRegistrationReentersAdmissionAndDrivesSameRunExactlyOnce()
    {
        var boundary = new Boundary(); var race = new RegistrationRace(); var host = Host(boundary, race.Seams);
        try
        {
            var batch = Batch(Seed(host), "node");
            var rejected = await RejectFirstRegistrationAsync(host, boundary, batch, race);
            var original = host.Runs.Load(rejected.RunBinding!)!;
            race.ReleaseOccupancy();

            // Query is the real completion observer reentry; competing observers must share the registration.
            var queries = await Task.WhenAll(host.QueryCompletionWorkflowAsync(batch, CancellationToken.None),
                host.QueryCompletionWorkflowAsync(batch, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
            await boundary.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(queries, status =>
            {
                Assert.Equal(original.RunId, status!.TaskHandle);
                Assert.False(status.ExecutionExitConfirmed);
            });
            var accepted = Registration(race);
            Assert.Equal(rejected.RequestIdentity, accepted.RequestIdentity);
            Assert.Equal(rejected.RunBinding, accepted.RunBinding);
            Assert.Equal(rejected.WireSubmitKey, accepted.WireSubmitKey);
            Assert.Equal(rejected.RetryWindowDeadlineUtc, accepted.RetryWindowDeadlineUtc);
            Assert.Equal(1, accepted.LastSendSeq);
            Assert.Equal(2, race.OccupyChecks);
            Assert.Equal(original.StopAuthority, host.Runs.Load(original.RunId)!.StopAuthority);
            Assert.Equal(original.RunId, Assert.Single(host.Runs.List()).RunId);
            Assert.Equal(new[] { "second" }, boundary.Nodes.ToArray());

            boundary.SecondExit.TrySetResult();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            BgiTaskQueueStatus? terminal;
            do
            {
                terminal = await host.QueryCompletionWorkflowAsync(batch, budget.Token);
                if (!terminal!.ExecutionExitConfirmed) await Task.Delay(10, budget.Token);
            } while (!terminal!.ExecutionExitConfirmed);
            Assert.Equal("completed", terminal.Status);
            Assert.Equal(original.RunId, terminal.TaskHandle);
            await host.QueryCompletionWorkflowAsync(batch, budget.Token);
            Assert.Equal(1, Registration(race).LastSendSeq);
            Assert.Equal(new[] { "second" }, boundary.Nodes.ToArray());
            Assert.Single(host.Runs.List());
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryableRegistrationDoesNotRestartAfterBatchOrRunStop(bool stopRun)
    {
        var boundary = new Boundary(); var race = new RegistrationRace(); var host = Host(boundary, race.Seams);
        try
        {
            var batch = Batch(Seed(host), "node");
            var rejected = await RejectFirstRegistrationAsync(host, boundary, batch, race);
            var original = host.Runs.Load(rejected.RunBinding!)!;
            if (stopRun)
            {
                await host.RequestRunActionAsync(original.RunId, WorkflowRunAction.Stop).WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(host.Runs.Load(original.RunId)!.StopRequested);
            }
            else batch.Cancel();
            race.ReleaseOccupancy();

            for (var i = 0; i < 2; i++)
                if (stopRun) await host.QueryCompletionWorkflowAsync(batch, CancellationToken.None);
                else await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => host.QueryCompletionWorkflowAsync(batch, CancellationToken.None));
            var retained = Registration(race);
            Assert.Equal(rejected.RequestIdentity, retained.RequestIdentity);
            Assert.Equal(rejected.RetryWindowDeadlineUtc, retained.RetryWindowDeadlineUtc);
            Assert.Equal(0, retained.LastSendSeq);
            Assert.Equal(1, race.OccupyChecks);
            Assert.Equal(original.StopAuthority, host.Runs.Load(original.RunId)!.StopAuthority);
            Assert.Single(host.Runs.List()); Assert.Empty(boundary.Nodes);
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task RetryableRegistrationWindowExpiryCannotStartOrAllocateAnotherRun()
    {
        var boundary = new Boundary(); var race = new RegistrationRace(); var host = Host(boundary, race.Seams);
        try
        {
            var batch = Batch(Seed(host), "node");
            var rejected = await RejectFirstRegistrationAsync(host, boundary, batch, race);
            var original = host.Runs.Load(rejected.RunBinding!)!;
            race.ReleaseOccupancy();
            race.AdvancePast(rejected.RetryWindowDeadlineUtc!.Value);

            var status = await host.QueryCompletionWorkflowAsync(batch, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(status!.ExecutionExitConfirmed);
            Assert.True(status.Cancelled);
            var expired = Registration(race);
            Assert.Equal(OperationRequestState.TerminalRejected, expired.RequestState);
            Assert.Equal("retry_window_expired", expired.LastPrecheckResult!.ReasonCode);
            Assert.Equal(rejected.RequestIdentity, expired.RequestIdentity);
            Assert.Equal(rejected.RetryWindowDeadlineUtc, expired.RetryWindowDeadlineUtc);
            Assert.Equal(0, expired.LastSendSeq);
            await host.QueryCompletionWorkflowAsync(batch, CancellationToken.None);
            await host.StartCompletionWorkflowAsync(batch, CancellationToken.None);
            Assert.Equal(original.RunId, Assert.Single(host.Runs.List()).RunId);
            Assert.Equal(original.StopAuthority, host.Runs.Load(original.RunId)!.StopAuthority);
            Assert.Equal(0, Registration(race).LastSendSeq);
            Assert.Empty(boundary.Nodes);
        }
        finally { boundary.FirstExit.TrySetResult(); boundary.SecondExit.TrySetResult(); await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task StoppedBatchCannotRegisterAutomaticSuccessor()
    {
        var boundary = new Boundary(); var host = Host(boundary);
        try
        {
            var batch = Batch(Seed(host)); batch.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartCompletionWorkflowAsync(batch, CancellationToken.None));
            Assert.Empty(host.Runs.List()); Assert.Empty(boundary.Nodes);
        }
        finally { await host.ShutdownAsync(); }
    }
}
