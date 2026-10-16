using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>Drives the actual client loop across local job exit, team waiting, and local finalization.</summary>
public sealed class CoordinatedBatchLocalFinishTests
{
    private sealed class Scenario
    {
        private readonly Dictionary<string, BgiJobInfo> _jobs = new();
        private string _phase = "preparing";
        private int _index;
        private int _attempt;
        private long _revision;
        private bool _terminalRead;
        internal readonly OnlineHoeingBatch Batch;
        internal readonly BgiEpoch Original = new() { ProcessId = 7, StartTicksUtc = 70 };
        internal BgiEpoch Current = new() { ProcessId = 7, StartTicksUtc = 70 };
        internal readonly TaskCompletionSource SucceededReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinalizeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinalizeAllowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool CompleteTeam;
        internal volatile bool AdvanceFirstItem;
        internal volatile bool AdvanceRecovery;
        internal volatile bool BreakReadsAfterTerminal;
        internal volatile bool FailAuthorityAfterSuccess;
        internal volatile bool AlwaysJobsUnavailable;
        internal volatile bool AlwaysTerminalUnavailable;
        internal int JobReadsUnavailable;
        internal int TerminalReadsUnavailable;
        internal volatile string? AbortReason;
        internal bool? OriginalAlive = true;
        internal BgiTaskQueueStatus Terminal = new() { Status = "completed" };
        internal Exception? FinalizationFailure;
        internal bool HoldFinalization;
        internal bool RequireEvidence;
        internal int Exchanges, ReadJobsAfterTerminal, Submits, Cancels, Releases, Abandons, Finalizes;
        internal string? LastNonemptyResult;

        internal Scenario(int bindings = 1)
        {
            var items = Enumerable.Range(0, bindings).Select(i => new CoordinatedHoeingBinding("group" + i, "group")).ToArray();
            Batch = new OnlineHoeingBatch(new("local-finish", 19, 0, 0, 0, "registering", "")
            {
                Protocol = "hoeing.start.v2", Source = "manual", ParticipantUids = ["self", "peer"],
                Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                {
                    ["self"] = items, ["peer"] = items
                }
            }, "self", new TaskConflictPolicySettings());
        }

        private Task<CoordinatedBatchSnapshot> Exchange(string stage, string result, string reason,
            int index, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Exchanges);
            if (result.Length > 0) LastNonemptyResult = result;
            if (FailAuthorityAfterSuccess && Batch.LocalSucceeded) throw new IOException("team channel unavailable");
            if (stage == "prepared" && _phase == "preparing") _phase = "takeover";
            if (stage == "takeoverConfirmed" && _phase == "takeover") _phase = "running";
            if (result == "succeeded")
            {
                SucceededReported.TrySetResult();
                if (AdvanceFirstItem && _index == 0 && Batch.Bindings.Count > 1) _index = 1;
                else if (CompleteTeam) { _index = Batch.Bindings.Count; _phase = "completed"; }
            }
            if (result == "failed" && AdvanceRecovery && _attempt == 0) _attempt = 1;
            if (result == "local_failed") AbortReason = "participant_local_failed:self";
            if (AbortReason is not null) _phase = "aborted";
            return Task.FromResult(new CoordinatedBatchSnapshot(Batch.BatchId, Batch.Generation,
                _index, _attempt, ++_revision, _phase, AbortReason ?? "")
            {
                Protocol = "hoeing.start.v2", ParticipantUids = Batch.Start!.ParticipantUids,
                Bindings = Batch.Start.Bindings
            });
        }

        internal Task<bool> Run(bool finalize = true) => new CoordinatedBatchLoop(Batch, new()
        {
            Prepare = _ => Task.CompletedTask,
            Takeover = _ => { Batch.TakeoverTicket = "owned"; return Task.CompletedTask; },
            Exchange = Exchange,
            ReadJobs = _ =>
            {
                if (Submits > 0 && (AlwaysJobsUnavailable || Volatile.Read(ref JobReadsUnavailable) > 0))
                {
                    if (!AlwaysJobsUnavailable) Interlocked.Decrement(ref JobReadsUnavailable);
                    return Task.FromResult<BgiJobListSnapshot?>(null);
                }
                if (_terminalRead)
                {
                    Interlocked.Increment(ref ReadJobsAfterTerminal);
                    if (BreakReadsAfterTerminal) throw new IOException("old BGI no longer reachable");
                }
                return Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = Current, Jobs = _jobs.Values.ToArray() });
            },
            ReadTerminal = (_, _) =>
            {
                if (AlwaysTerminalUnavailable || Volatile.Read(ref TerminalReadsUnavailable) > 0)
                {
                    if (!AlwaysTerminalUnavailable) Interlocked.Decrement(ref TerminalReadsUnavailable);
                    return Task.FromResult<BgiTaskQueueStatus?>(null);
                }
                if (Terminal.Status is "completed" or "failed" or "queueCancelled") _terminalRead = true;
                return Task.FromResult<BgiTaskQueueStatus?>(Terminal);
            },
            Submit = (item, epoch, _) =>
            {
                Assert.True(CoordinatedBatchLoop.SameEpoch(Original, epoch));
                var id = "job" + Interlocked.Increment(ref Submits);
                _jobs[id] = new() { JobId = id, IdempotencyKey = item.RequestKey };
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = id });
            },
            CancelOwned = (job, _) =>
            {
                Assert.True(_jobs.ContainsKey(job));
                Interlocked.Increment(ref Cancels);
                Terminal = new() { Status = "queueCancelled", Cancelled = true };
                return Task.CompletedTask;
            },
            ReleaseTicket = (ticket, epoch, _) =>
            {
                Assert.Equal("owned", ticket);
                Assert.True(CoordinatedBatchLoop.SameEpoch(Original, epoch));
                Interlocked.Increment(ref Releases);
                return Task.FromResult(CoordinatedBatchLoop.SameEpoch(epoch, Current));
            },
            OriginalProcessAlive = epoch =>
            {
                Assert.True(CoordinatedBatchLoop.SameEpoch(Original, epoch));
                return OriginalAlive;
            },
            AbandonExitedOwnership = (ticket, epoch, _) =>
            {
                Assert.Equal("owned", ticket);
                Assert.True(CoordinatedBatchLoop.SameEpoch(Original, epoch));
                Assert.False(OriginalAlive);
                Interlocked.Increment(ref Abandons);
                return Task.FromResult(true);
            },
            FinalizeLocal = !finalize ? null : async (epoch, ct) =>
            {
                Assert.True(CoordinatedBatchLoop.SameEpoch(Original, epoch));
                Assert.True(Batch.LocalSucceeded);
                Interlocked.Increment(ref Finalizes);
                FinalizeStarted.TrySetResult();
                if (HoldFinalization) await FinalizeAllowed.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (FinalizationFailure is not null) throw FinalizationFailure;
            },
            PollInterval = TimeSpan.FromMilliseconds(2),
            CleanupTimeout = TimeSpan.FromMilliseconds(200),
            StageTimeout = TimeSpan.FromSeconds(1),
            ObservationReconcileTimeout = TimeSpan.FromMilliseconds(100),
            AuthorityContactTimeout = TimeSpan.FromMilliseconds(100),
            RequireFinishEvidence = RequireEvidence
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConfirmedFinalJobDoesNotRequeryOldBgiWhileWaitingForPeers()
    {
        var s = new Scenario { BreakReadsAfterTerminal = true };
        var running = s.Run();
        await s.SucceededReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await s.FinalizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntil(() => s.Batch.LocalExecutionReleased);
        s.Current = new() { ProcessId = 8, StartTicksUtc = 80 };
        s.CompleteTeam = true;
        Assert.True(await running);
        Assert.Equal(0, s.ReadJobsAfterTerminal);
        Assert.Equal(1, s.Finalizes);
        Assert.True(s.Batch.CoordinatedSucceeded);
        Assert.True(s.Batch.LocalExecutionReleased);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(0, s.Releases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TemporaryUnavailableJobOrTerminalReadsReconcileWithoutRestartingTheGroup(bool jobs)
    {
        var s = new Scenario { CompleteTeam = true };
        if (jobs) s.JobReadsUnavailable = 3;
        else s.TerminalReadsUnavailable = 3;
        Assert.True(await s.Run());
        Assert.True(s.Batch.LocalSucceeded);
        Assert.Equal(1, s.Submits);
        Assert.Equal(1, s.Finalizes);
        Assert.Equal(0, s.Cancels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistentlyUnavailableReadsEndAfterBoundedReconciliationAndNeverResubmit(bool jobs)
    {
        var s = new Scenario { AlwaysJobsUnavailable = jobs, AlwaysTerminalUnavailable = !jobs };
        Assert.False(await s.Run());
        Assert.False(s.Batch.LocalSucceeded);
        Assert.False(s.Batch.LocalExecutionReleased);
        Assert.Equal("unknown", s.Batch.Outcome);
        Assert.Contains("有界补查", s.Batch.FailureReason);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Finalizes);
    }

    [Fact]
    public async Task AuthorityTimeoutAfterLocalReleaseOnlyLeavesAckPendingAndNeverTouchesSuccessorOwnership()
    {
        var s = new Scenario { BreakReadsAfterTerminal = true };
        var running = s.Run();
        await WaitUntil(() => s.Batch.LocalExecutionReleased);
        s.FailAuthorityAfterSuccess = true;
        Assert.False(await running);
        Assert.True(s.Batch.LocalSucceeded);
        Assert.True(s.Batch.LocalExecutionReleased);
        Assert.True(s.Batch.CleanupConfirmed);
        Assert.Equal("succeeded", s.Batch.Outcome);
        Assert.Equal(1, s.Finalizes);
        Assert.Equal(0, s.ReadJobsAfterTerminal);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(0, s.Releases);
        Assert.Equal(0, s.Abandons);
    }

    [Fact]
    public async Task LocalPolicyRunsOnceWithoutBlockingTeamHeartbeats()
    {
        var s = new Scenario { HoldFinalization = true, CompleteTeam = true };
        var running = s.Run();
        await s.FinalizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = s.Exchanges;
        await WaitUntil(() => s.Exchanges > before + 3);
        Assert.False(running.IsCompleted);
        Assert.False(s.Batch.LocalExecutionReleased);
        s.FinalizeAllowed.SetResult();
        Assert.True(await running);
        Assert.Equal(1, s.Finalizes);
        Assert.True(s.Batch.LocalExecutionReleased);
    }

    [Theory]
    [InlineData("participant_unknown:peer")]
    [InlineData("participant_lease_expired")]
    [InlineData("manual_reset")]
    [InlineData("user_stop_all")]
    public async Task PeerAbortCannotUndoCompletedLocalJobOrCancelReleasedSuccessor(string reason)
    {
        var s = new Scenario();
        var running = s.Run();
        await WaitUntil(() => s.Batch.LocalExecutionReleased);
        s.AbortReason = reason;
        Assert.True(await running);
        Assert.Equal("succeeded", s.Batch.Outcome);
        Assert.Equal(reason, s.Batch.TeamFailureReason);
        Assert.False(s.Batch.CoordinatedSucceeded);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Cancels);
        s.Batch.Cancel(userInitiated: false);
        Assert.False(s.Batch.IsCancellationRequested);
    }

    [Fact]
    public async Task ExplicitLocalStopStillCancelsAnInFlightFinalizer()
    {
        var s = new Scenario { HoldFinalization = true };
        var running = s.Run();
        await s.FinalizeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        s.Batch.Cancel();
        Assert.False(await running);
        Assert.True(s.Batch.IsCancellationRequested);
        Assert.Equal("cancelled", s.Batch.Outcome);
        Assert.False(s.Batch.LocalExecutionReleased);
        Assert.Equal(1, s.Finalizes);
        Assert.Equal(0, s.Cancels); // Only the already completed hoeing job belongs to this loop.
    }

    [Fact]
    public async Task NativeBodyCompletionDoesNotReleaseAnActiveGroupOrStartPolicy()
    {
        var s = new Scenario
        {
            Terminal = new()
            {
                Status = "running",
                HoeingEvidence = new() { Started = 1, Completed = 1, Active = 0, Sealed = false }
            }
        };
        var running = s.Run();
        await WaitUntil(() => s.Submits == 1 && s.Exchanges > 8);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.False(s.Batch.LocalExecutionReleased);
        Assert.Equal(0, s.Finalizes);
        s.Batch.Cancel();
        Assert.False(await running);
        Assert.Equal(1, s.Cancels);
        Assert.Equal(1, s.Releases);
    }

    [Theory]
    [InlineData("hoeing_incomplete")]
    [InlineData("script_error")]
    public async Task GroupFailureAfterCompletedNativeCannotEnterWholeGroupRecovery(string errorCode)
    {
        var s = new Scenario
        {
            AdvanceRecovery = true,
            Terminal = new()
            {
                Status = "failed", ErrorCode = errorCode,
                HoeingEvidence = new() { Started = 1, Completed = 1, Sealed = true, JobResult = "Failed" }
            }
        };
        Assert.False(await s.Run());
        Assert.Equal("local_failed", s.LastNonemptyResult);
        Assert.Equal("failed", s.Batch.Outcome);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Finalizes);
        Assert.Equal(0, s.Cancels);
        Assert.True(s.Batch.CleanupConfirmed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsealed")]
    [InlineData("active")]
    [InlineData("inconsistent")]
    [InlineData("invalid_counts")]
    public async Task RequiredFinishEvidenceCannotTreatMissingOrInconsistentTerminalAsZeroNativeAttempts(string scenario)
    {
        var evidence = scenario switch
        {
            "missing" => null,
            "unsealed" => new CoordinatedHoeingEvidence { Started = 1, Completed = 1, JobResult = "Failed" },
            "active" => new CoordinatedHoeingEvidence { Started = 1, Active = 1, Sealed = true, JobResult = "Failed" },
            "inconsistent" => new CoordinatedHoeingEvidence { Started = 1, Completed = 1, Sealed = true, JobResult = "Ran" },
            _ => new CoordinatedHoeingEvidence { Started = 0, Completed = 1, Sealed = true, JobResult = "Failed" }
        };
        var s = new Scenario
        {
            RequireEvidence = true, AdvanceRecovery = true,
            Terminal = new() { Status = "failed", ErrorCode = "task_start_failed", HoeingEvidence = evidence }
        };
        Assert.False(await s.Run());
        Assert.Equal("unknown", s.Batch.Outcome);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Finalizes);
        Assert.Contains("证据", s.Batch.FailureReason);
    }

    [Fact]
    public async Task RequiredSealedWholeJobSuccessStartsLocalFinalization()
    {
        var s = new Scenario
        {
            RequireEvidence = true, CompleteTeam = true,
            Terminal = new()
            {
                Status = "completed",
                HoeingEvidence = new() { Started = 1, Completed = 1, Sealed = true, JobResult = "Ran" }
            }
        };
        Assert.True(await s.Run());
        Assert.True(s.Batch.LocalSucceeded);
        Assert.True(s.Batch.LocalExecutionReleased);
        Assert.Equal(1, s.Finalizes);
    }

    [Fact]
    public async Task LocalFinalizerFailurePreservesTaskSuccessButDoesNotClaimRelease()
    {
        var s = new Scenario { FinalizationFailure = new IOException("ticket release unconfirmed") };
        Assert.False(await s.Run());
        Assert.True(s.Batch.LocalSucceeded);
        Assert.False(s.Batch.LocalExecutionReleased);
        Assert.Equal("succeeded", s.Batch.Outcome);
        Assert.Contains("ticket release unconfirmed", s.Batch.FailureReason);
        Assert.Equal(1, s.Finalizes);
        Assert.Equal(0, s.Cancels);
    }

    [Fact]
    public async Task NextBindingStillRequiresOriginalEpochAfterPreviousTerminalWasCached()
    {
        var s = new Scenario(bindings: 2);
        var running = s.Run();
        await s.SucceededReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        s.Current = new() { ProcessId = 8, StartTicksUtc = 80 };
        s.AdvanceFirstItem = true;
        Assert.False(await running);
        Assert.Equal(1, s.Submits);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.Equal(0, s.Finalizes);
        Assert.True(s.ReadJobsAfterTerminal > 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public async Task OnlyConfirmedOriginalProcessExitCanSettleAnUnobservedJobOnSuccessorEpoch(bool? alive, bool cleaned)
    {
        var s = new Scenario { Terminal = new() { Status = "running" }, OriginalAlive = alive };
        var running = s.Run();
        await WaitUntil(() => s.Submits == 1);
        s.Current = new() { ProcessId = 8, StartTicksUtc = 80 };
        Assert.False(await running);
        Assert.Equal(cleaned, s.Batch.CleanupConfirmed);
        Assert.Equal(cleaned ? 1 : 0, s.Abandons);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(0, s.Releases);
        Assert.Equal(1, s.Submits);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(2, timeout.Token);
    }
}
