using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedStateContinuationTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }

    private sealed class Run
    {
        internal readonly BgiEpoch Epoch = new() { ProcessId = 41, StartTicksUtc = 410 };
        internal readonly OnlineHoeingBatch Batch;
        internal readonly Clock Time = new();
        internal readonly TaskCompletionSource Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PrepareAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> SubmittedKeys = [];
        internal volatile bool HoldPrepare, LoseAuthority, LoseReads, AllowFinish, LoseAcknowledgements;
        internal bool OrdinaryRegistryEmpty, OrdinaryRegistryUnavailable, LoseFirstReply, DropFirstRequest;
        internal bool ManualStopBeforeSend, ManualStopFenceReply;
        internal volatile bool BoundEpochUnknown;
        internal BgiEpoch? BoundEpochOverride;
        internal int FirstBusy, PrepareFailures;
        internal int Preparations, Submissions, Executions, Cancels, Releases, Finalizations, KeyQueries, AckAttempts;
        private string _phase = "preparing";
        private string? _key, _job;
        private bool _cancelled;
        private long _revision;

        internal Run()
        {
            string[] members = ["self", "peer1", "peer2", "peer3"];
            Batch = new(new CoordinatedBatchSnapshot("state-continuation", 43, 0, 0, 0, "registering", "")
            {
                Protocol = "hoeing.start.v2", ParticipantUids = members,
                Bindings = members.ToDictionary(uid => uid,
                    _ => (IReadOnlyList<CoordinatedHoeingBinding>)[new("联机锄地", "group")])
            }, "self", new TaskConflictPolicySettings());
        }

        private BgiTaskQueueStatus Status(string key) => new()
        {
            IdempotencyKey = key, Epoch = Epoch, TaskHandle = _job,
            Status = _job is null ? "not_found" : _cancelled ? "queueCancelled" : AllowFinish ? "completed" : "running",
            Cancelled = _cancelled, ExecutionExitConfirmed = _job is not null && (AllowFinish || _cancelled),
            HoeingEvidence = _job is null ? null : new()
            {
                Started = 1, Completed = AllowFinish ? 1 : 0, Active = AllowFinish || _cancelled ? 0 : 1,
                Sealed = AllowFinish || _cancelled, JobResult = _cancelled ? "Cancelled" : AllowFinish ? "Ran" : null
            }
        };

        internal Task<bool> Start() => new CoordinatedBatchLoop(Batch, new()
        {
            StateContinuationEnabled = true,
            Clock = Time,
            PollInterval = TimeSpan.FromMilliseconds(2),
            StageTimeout = TimeSpan.FromMilliseconds(15),
            ObservationReconcileTimeout = TimeSpan.FromMilliseconds(15),
            SubmissionReconcileTimeout = TimeSpan.FromMilliseconds(15),
            AuthorityContactTimeout = TimeSpan.FromMilliseconds(15),
            RunningAuthorityContactTimeout = TimeSpan.FromMilliseconds(15),
            CleanupTimeout = TimeSpan.FromMilliseconds(300),
            Prepare = async ct =>
            {
                var attempt = Interlocked.Increment(ref Preparations);
                Preparing.TrySetResult();
                if (HoldPrepare) await PrepareAllowed.Task.WaitAsync(ct);
                if (attempt <= PrepareFailures) throw new IOException("temporary preparation failure");
            },
            Takeover = _ => { Batch.TakeoverTicket = "same-ticket"; return Task.CompletedTask; },
            Exchange = (stage, result, _, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (LoseAuthority) throw new ControlChannelUnavailableException("temporary control loss");
                if (stage == "prepared") _phase = "takeover";
                if (stage == "takeoverConfirmed") _phase = "running";
                if (result == "succeeded") _phase = "completed";
                return Task.FromResult(new CoordinatedBatchSnapshot(Batch.BatchId, Batch.Generation,
                    _phase == "completed" ? 1 : 0, 0, Interlocked.Increment(ref _revision), _phase, "")
                { Protocol = "hoeing.start.v2", ParticipantUids = Batch.Start!.ParticipantUids, Bindings = Batch.Start.Bindings });
            },
            ReadBoundEpoch = () => BoundEpochUnknown ? null : BoundEpochOverride ?? Epoch,
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(LoseReads || OrdinaryRegistryUnavailable ? null : new()
            {
                Epoch = Epoch,
                Jobs = OrdinaryRegistryEmpty || _job is null ? [] : [new() { JobId = _job, IdempotencyKey = _key }]
            }),
            ResolveOriginal = (key, _) =>
            {
                Interlocked.Increment(ref KeyQueries);
                return Task.FromResult<BgiTaskQueueStatus?>(LoseReads ? null : Status(key));
            },
            ReadTerminal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(LoseReads ? null : Status(_key!)),
            Submit = (item, _, _) =>
            {
                var attempt = Interlocked.Increment(ref Submissions);
                lock (SubmittedKeys) SubmittedKeys.Add(item.RequestKey);
                if (ManualStopBeforeSend) throw new BgiStateContinuationStoppedException("F11 advanced the original batch stop version");
                if (ManualStopFenceReply) return Task.FromResult(new BgiTaskSubmitResult { Success = false, ErrorCode = "manual_stop_fence" });
                if (attempt <= FirstBusy)
                    return Task.FromResult(new BgiTaskSubmitResult { Success = false, ErrorCode = "task_busy" });
                if (DropFirstRequest && attempt == 1) throw new IOException("request never reached BGI");
                _key = item.RequestKey;
                if (_job is null) { Interlocked.Increment(ref Executions); _job = Guid.NewGuid().ToString("N"); }
                if (LoseFirstReply && attempt == 1) throw new IOException("queued; reply lost");
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = _job });
            },
            Acknowledge = (key, job, epoch, _) =>
            {
                Assert.Equal(_key, key); Assert.Equal(_job, job); Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                Interlocked.Increment(ref AckAttempts);
                return Task.FromResult(!LoseAcknowledgements);
            },
            CancelOwned = (job, _) =>
            {
                Assert.Equal(_job, job); Interlocked.Increment(ref Cancels); _cancelled = true;
                return Task.CompletedTask;
            },
            ReleaseTicket = (_, _, _) => { Interlocked.Increment(ref Releases); return Task.FromResult(true); },
            FinalizeLocal = (_, _) => { Interlocked.Increment(ref Finalizations); return Task.CompletedTask; },
            OriginalProcessAlive = _ => true,
            RequireFinishEvidence = true
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PreparationCanWaitPastStageDeadlineAndRetryWithoutDiscardingTheBatch()
    {
        var r = new Run { HoldPrepare = true, PrepareFailures = 1, AllowFinish = true };
        var task = r.Start();
        await r.Preparing.Task.WaitAsync(TimeSpan.FromSeconds(1));
        r.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(50);
        Assert.False(task.IsCompleted); Assert.False(r.Batch.IsCancellationRequested); Assert.Equal(0, r.Cancels);
        r.PrepareAllowed.SetResult();
        Assert.True(await task);
        Assert.Equal(2, r.Preparations); Assert.Equal(1, r.Executions); Assert.Equal(1, r.Finalizations);
    }

    [Fact]
    public async Task ControlAndObservationLossPastOldBudgetsContinueTheOriginalJob()
    {
        var r = new Run();
        var task = r.Start();
        await Until(() => r.Executions == 1);
        r.LoseAuthority = true; r.LoseReads = true;
        r.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(65);
        Assert.False(task.IsCompleted); Assert.Equal(0, r.Cancels); Assert.Equal(0, r.Releases);
        Assert.False(r.Batch.ControlExecutionEnded); Assert.Null(r.Batch.RemainingRunningControlBudget);
        r.LoseAuthority = false; r.LoseReads = false; r.AllowFinish = true;
        Assert.True(await task); Assert.Equal(1, r.Submissions); Assert.Equal(1, r.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostReplyOrUnreceivedRequestResolvesThroughTheSameRetainedKey(bool requestNeverArrived)
    {
        var r = new Run { OrdinaryRegistryEmpty = true, LoseFirstReply = !requestNeverArrived,
            DropFirstRequest = requestNeverArrived, AllowFinish = true };
        Assert.True(await r.Start());
        Assert.Equal(requestNeverArrived ? 2 : 1, r.Submissions);
        Assert.Equal(1, r.Executions); Assert.True(r.KeyQueries > 0); Assert.Equal(0, r.Cancels);
        Assert.Single(r.SubmittedKeys.Distinct());
        Assert.Equal(CoordinatedBatchLoop.RequestKey(r.Batch.BatchId, "self", 0, 0), r.SubmittedKeys[0]);
    }

    [Fact]
    public async Task MissingOrdinaryRegistryUsesRetainedQueueFactsAfterRealTakeover()
    {
        var r = new Run { OrdinaryRegistryUnavailable = true, LoseFirstReply = true, AllowFinish = true };
        Assert.True(await r.Start());
        Assert.Equal(1, r.Executions); Assert.Equal(1, r.Submissions); Assert.True(r.KeyQueries > 0);
        Assert.Equal(1, r.Finalizations); Assert.Equal(0, r.Cancels);
    }

    [Fact]
    public async Task BusyNotAcceptedRequestsKeepTheOriginalItemUntilItCanStart()
    {
        var r = new Run { FirstBusy = 4, AllowFinish = true };
        Assert.True(await r.Start());
        Assert.Equal(5, r.Submissions); Assert.Equal(1, r.Executions); Assert.Single(r.SubmittedKeys.Distinct());
        Assert.Equal(0, r.Cancels); Assert.Equal(1, r.Finalizations);
    }

    [Fact]
    public async Task ResultAcknowledgementFailureDoesNotReplayTheTaskOrTheCompletedAction()
    {
        var r = new Run { AllowFinish = true, LoseAcknowledgements = true };
        var task = r.Start();
        await Until(() => r.Finalizations == 1 && r.AckAttempts > 2);
        Assert.False(task.IsCompleted); Assert.Equal(1, r.Submissions); Assert.Equal(0, r.Cancels);
        r.LoseAcknowledgements = false;
        Assert.True(await task); Assert.Equal(1, r.Finalizations); Assert.Equal(1, r.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitStopStillCancelsTheOriginalRunningJobAfterAProlongedReadFailure(bool ordinaryRegistryUnavailable)
    {
        var r = new Run { OrdinaryRegistryUnavailable = ordinaryRegistryUnavailable };
        var task = r.Start();
        await Until(() => r.Executions == 1);
        r.LoseReads = true; await Task.Delay(40);
        r.LoseReads = false; r.Batch.Cancel();
        Assert.False(await task); Assert.True(r.Batch.ExplicitStopRequested);
        Assert.Equal(1, r.Cancels); Assert.Equal(1, r.Executions); Assert.Equal(0, r.Finalizations);
    }

    [Fact]
    public async Task OrdinaryBatchRevocationCannotCancelAnAlreadyAcceptedCompletionJob()
    {
        var r = new Run(); r.Batch.StateContinuationEnabled = true;
        var cancelled = 0;
        r.Batch.ConfigurePolicyCancellation(_ => { Interlocked.Increment(ref cancelled); return Task.FromResult(true); });
        r.Batch.RecordPolicyExecution(new("accepted-after-hoeing", r.Epoch, true));
        r.Batch.Cancel(userInitiated: false);
        Assert.False(r.Batch.IsCancellationRequested); Assert.False(r.Batch.ExplicitStopRequested);
        Assert.True(await r.Batch.WaitPolicyCancellationAsync()); Assert.Equal(0, cancelled);
        r.Batch.Cancel();
        Assert.True(await r.Batch.WaitPolicyCancellationAsync()); Assert.Equal(1, cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualStopFenceOrPreSendStopEndsTheOriginalBatchInsteadOfRequeryingForever(bool stoppedBeforeSend)
    {
        var r = new Run { ManualStopBeforeSend = stoppedBeforeSend, ManualStopFenceReply = !stoppedBeforeSend };
        Assert.False(await r.Start());
        Assert.True(r.Batch.IsCancellationRequested); Assert.True(r.Batch.ExplicitStopRequested);
        Assert.Equal("cancelled", r.Batch.Outcome);
        Assert.Equal(1, r.Submissions); Assert.Equal(0, r.Executions); Assert.Equal(0, r.Finalizations);
    }

    [Fact]
    public async Task AConfirmedNewProcessRevokesOriginalBatchWithoutReplayingOrStoppingTheNewProcess()
    {
        var r = new Run();
        var task = r.Start();
        await Until(() => r.Executions == 1);
        r.BoundEpochOverride = new() { ProcessId = r.Epoch.ProcessId, StartTicksUtc = r.Epoch.StartTicksUtc + 1 };
        Assert.False(await task);
        Assert.Equal(1, r.Submissions); Assert.Equal(1, r.Executions); Assert.Equal(0, r.Cancels);
        Assert.False(r.Batch.ExplicitStopRequested);
    }

    [Fact]
    public async Task TemporarilyMissingProcessIdentityDoesNotRevokeTheOriginalBatch()
    {
        var r = new Run();
        var task = r.Start();
        await Until(() => r.Executions == 1);
        r.BoundEpochUnknown = true; r.LoseReads = true;
        await Task.Delay(40);
        Assert.False(task.IsCompleted); Assert.False(r.Batch.IsCancellationRequested); Assert.Equal(0, r.Cancels);
        r.BoundEpochUnknown = false; r.LoseReads = false; r.AllowFinish = true;
        Assert.True(await task); Assert.Equal(1, r.Executions);
    }

    [Fact]
    public void BatchStopVersionCannotBeReplacedOrClearedAfterOriginalBinding()
    {
        var r = new Run();
        r.Batch.StateContinuationStopVersion = 7;
        r.Batch.StateContinuationStopVersion = 7;
        Assert.Throws<InvalidOperationException>(() => r.Batch.StateContinuationStopVersion = 8);
        Assert.Throws<InvalidOperationException>(() => r.Batch.StateContinuationStopVersion = null);
        Assert.Equal(7, r.Batch.StateContinuationStopVersion);
    }

    [Fact]
    public void CompletionActionKeepsItsFrozenRequestAndFirstRealTerminalAcrossAcknowledgementRetries()
    {
        var r = new Run();
        r.Batch.PolicyRequestIdentity = "same-operation";
        Assert.Throws<InvalidOperationException>(() => r.Batch.PolicyRequestIdentity = "another-operation");
        var first = new Dictionary<string, object?> { ["idempotencyKey"] = r.Batch.PolicyRequestKey };
        r.Batch.PolicySubmitPayload = first;
        r.Batch.PolicySubmitPayload = new Dictionary<string, object?> { ["idempotencyKey"] = "different" };
        Assert.Equal(r.Batch.PolicyRequestKey, r.Batch.PolicySubmitPayload!["idempotencyKey"]);
        var job = Guid.NewGuid();
        r.Batch.PolicyTerminalResult = new() { TaskHandle = job.ToString("N"), Status = "completed", ExecutionExitConfirmed = true };
        var saved = r.Batch.PolicyTerminalResult;
        r.Batch.PolicyTerminalResult = new() { TaskHandle = job.ToString("D"), Status = "completed", ExecutionExitConfirmed = true };
        Assert.Same(saved, r.Batch.PolicyTerminalResult);
        Assert.Throws<InvalidOperationException>(() => r.Batch.PolicyTerminalResult = new()
        { TaskHandle = Guid.NewGuid().ToString("N"), Status = "completed", ExecutionExitConfirmed = true });
        r.Batch.PolicyResultAcknowledged = true;
        r.Batch.PolicyResultAcknowledged = false;
        Assert.True(r.Batch.PolicyResultAcknowledged);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(2, timeout.Token);
    }
}
