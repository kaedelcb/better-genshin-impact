using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;
using Authority = BgiCoordinatorServer.Services.CoordinatedBatchState;
using AuthorityBinding = BgiCoordinatorServer.Services.CoordinatedBatchBinding;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CoordinatedLocalTaskFailureTests
{
    private sealed class Scenario
    {
        private readonly Dictionary<string, BgiJobInfo> _jobs = new();
        internal readonly Authority Server;
        internal readonly OnlineHoeingBatch Batch;
        internal readonly BgiEpoch Epoch;
        internal BgiTaskQueueStatus Terminal = Failed(AllNativeSucceeded());
        internal bool? IdentityConfirmed = true;
        internal bool PeerLocalTaskFailure;
        internal bool CancelAtSubmit;
        internal bool LoseFailureReportBeforeApply;
        internal bool LoseFailureReportAfterApply;
        internal bool ReleaseAccepted = true;
        internal int Submits, Confirms, Cancels, Releases, Finalizes;

        internal Scenario(int bindingCount = 1, BgiEpoch? epoch = null, bool continuation = false)
        {
            Epoch = epoch ?? new() { ProcessId = 17, StartTicksUtc = 170 };
            var bindings = Enumerable.Range(0, bindingCount).Select(i => new AuthorityBinding("group" + i, "group")).ToArray();
            Server = new(47, ["self", "peer"], DateTime.UtcNow, bindings: new Dictionary<string, IReadOnlyList<AuthorityBinding>>
            {
                ["self"] = bindings, ["peer"] = bindings
            }, stateContinuation: continuation);
            Batch = new(Client(Server.Snapshot()), "self", new() { Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskName = "must-not-start" });
        }

        private Task<CoordinatedBatchSnapshot> Exchange(string stage, string result, string reason,
            int index, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            const string layout = "group";
            var actualLayout = string.Join(",", Enumerable.Repeat(layout, Batch.Bindings.Count));
            if (result == "local_tasks_failed" && LoseFailureReportBeforeApply) throw new IOException("failure report not delivered");
            Server.Apply("self", Batch.Token, Batch.BatchId, actualLayout, index, attempt, result, now, stage, reason);
            if (result == "local_tasks_failed" && LoseFailureReportAfterApply) throw new IOException("failure report ACK lost");
            if (!Server.ExecutionEnded)
            {
                if (Server.Phase == "registering")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "", now, "registered");
                if (Server.Phase == "preparing")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "", now, "prepared");
                if (Server.Phase == "takeover")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "", now, "takeoverConfirmed");
                if (Server.StateContinuation && Server.Phase == "running" && result is "succeeded" or "local_tasks_failed")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "succeeded", now);
                if (Server.Phase == "running" && PeerLocalTaskFailure && result == "succeeded")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "local_tasks_failed", now);
                if (Server.Phase == "stopping")
                    Server.Apply("peer", "peer-token", Batch.BatchId, actualLayout, index, attempt, "stopped", now);
            }
            return Task.FromResult(Client(Server.Snapshot()));
        }

        internal Task<bool> Run() => new CoordinatedBatchLoop(Batch, new()
        {
            Prepare = _ => Task.CompletedTask,
            Takeover = _ => { Batch.TakeoverTicket = "own-ticket"; return Task.CompletedTask; },
            Exchange = Exchange,
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = Epoch, Jobs = _jobs.Values.ToArray() }),
            ReadTerminal = (id, _) => Task.FromResult<BgiTaskQueueStatus?>(Server.StateContinuation ? new()
            {
                Status = Terminal.Status, Cancelled = Terminal.Cancelled, HoeingEvidence = Terminal.HoeingEvidence,
                ErrorCode = Terminal.ErrorCode, TaskHandle = id, IdempotencyKey = _jobs[id].IdempotencyKey,
                Epoch = Epoch, ExecutionExitConfirmed = true,
            } : Terminal),
            Acknowledge = (_, _, _, _) => Task.FromResult(true),
            Submit = (item, epoch, _) =>
            {
                Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                var id = "job" + ++Submits;
                _jobs[id] = new() { JobId = id, IdempotencyKey = item.RequestKey, Epoch = Epoch };
                if (CancelAtSubmit) Batch.Cancel();
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = id });
            },
            CancelOwned = (_, _) => { Cancels++; return Task.CompletedTask; },
            ReleaseTicket = (ticket, epoch, _) =>
            {
                Assert.Equal("own-ticket", ticket);
                Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                Releases++;
                return Task.FromResult(ReleaseAccepted);
            },
            FinalizeLocal = (_, _) => { Finalizes++; return Task.CompletedTask; },
            RequireFinishEvidence = true,
            RequireLocalTaskFailureIsolation = true,
            ConfirmLocalTasksFailure = (job, epoch, key, _) =>
            {
                Assert.True(_jobs.TryGetValue(job, out var original));
                Assert.Equal(original!.IdempotencyKey, key);
                Assert.True(CoordinatedBatchLoop.SameEpoch(original.Epoch, epoch));
                Confirms++;
                return Task.FromResult(IdentityConfirmed);
            },
            PollInterval = TimeSpan.FromMilliseconds(2),
            StageTimeout = TimeSpan.FromSeconds(1),
            CleanupTimeout = TimeSpan.FromMilliseconds(100),
            ObservationReconcileTimeout = TimeSpan.FromMilliseconds(50),
            AuthorityContactTimeout = TimeSpan.FromMilliseconds(40)
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(4));

        internal void FinishPeer() => Server.Apply("peer", "peer-token", Batch.BatchId,
            string.Join(",", Batch.Bindings.Select(b => b.Type)), Server.Index, Server.Attempt, "succeeded", DateTime.UtcNow);
    }

    [Fact]
    public async Task ConfirmedOrdinaryFailureReleasesOnlyThisEndpointWhilePeerKeepsItsCurrentGroup()
    {
        var s = new Scenario(bindingCount: 2);
        Assert.False(await s.Run());
        Assert.True(s.Batch.LocalTasksFailed);
        Assert.True(s.Batch.LocalTasksFailureAccepted);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.False(s.Batch.CoordinatedSucceeded);
        Assert.Equal("failed", s.Batch.Outcome);
        Assert.True(s.Batch.CleanupConfirmed);
        Assert.Equal(1, s.Submits);
        Assert.Equal(1, s.Confirms);
        Assert.Equal(1, s.Releases);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(0, s.Finalizes);
        Assert.Equal("running", s.Server.Phase);
        Assert.Equal("local_tasks_failed", s.Server.Snapshot().Members.Single(m => m.Uid == "self").Result);
        Assert.Equal("takeoverConfirmed", s.Server.Snapshot().Members.Single(m => m.Uid == "self").Stage);
        s.FinishPeer();
        Assert.Equal("aborted", s.Server.Phase);
        Assert.StartsWith("local_tasks_failed:", s.Server.Reason);
        Assert.Equal(0, s.Server.Index);
        Assert.Equal(0, s.Server.Attempt);
    }

    [Fact]
    public async Task SuccessfulNonfinalCurrentBindingIsPreservedWithoutRunningTheNextBindingOrSuccessPolicy()
    {
        var s = new Scenario(bindingCount: 2)
        {
            Terminal = new() { Status = "completed", HoeingEvidence = AllNativeSucceeded() with { JobResult = "Ran" } },
            PeerLocalTaskFailure = true
        };
        Assert.False(await s.Run());
        Assert.True(s.Batch.CurrentBindingSucceeded);
        Assert.True(s.Batch.PendingBindingsSkipped);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.Equal("partial_completed", s.Batch.Outcome);
        Assert.Equal("当前绑定已完成；因成员普通任务失败，后续绑定未启动", s.Batch.Message);
        Assert.True(s.Batch.CleanupConfirmed);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Finalizes);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(1, s.Releases);
        Assert.Equal(0, s.Server.Index);
        Assert.Equal(0, s.Server.Attempt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectBeforeDeliveryOrAfterAcceptanceRetainsOriginalFailureReportForReconnection(bool beforeApply)
    {
        var s = new Scenario(bindingCount: 2)
        {
            LoseFailureReportBeforeApply = beforeApply, LoseFailureReportAfterApply = !beforeApply
        };
        Assert.False(await s.Run());
        Assert.True(s.Batch.LocalTasksFailed);
        Assert.False(s.Batch.LocalTasksFailureAccepted);
        Assert.True(s.Batch.CleanupConfirmed);
        Assert.Equal("running", s.Server.Phase);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Finalizes);
        var report = Assert.IsType<LocalTaskFailureReport>(s.Batch.LocalTaskFailureReport);
        Assert.Equal(0, report.Index);
        Assert.Equal(0, report.Attempt);
        Assert.Equal("job1", report.JobId);
        Assert.Equal(CoordinatedBatchLoop.RequestKey(s.Batch.BatchId, "self", 0, 0), report.RequestKey);
        Assert.True(CoordinatedBatchLoop.SameEpoch(s.Epoch, report.Epoch));

        // The same helper used by the production reconnect path first adopts a stored receipt.
        var state = Client(s.Server.Snapshot());
        if (!s.Batch.ObserveLocalTasksFailureAccepted(state))
        {
            var pending = Assert.IsType<LocalTaskFailureReport>(s.Batch.PendingLocalTaskFailureReport(state));
            Assert.Same(report, pending);
            state = Client(s.Server.Apply("self", s.Batch.Token, s.Batch.BatchId, "group,group",
                pending.Index, pending.Attempt, "local_tasks_failed", DateTime.UtcNow, "takeoverConfirmed", pending.Reason));
            Assert.True(s.Batch.ObserveLocalTasksFailureAccepted(state));
        }
        Assert.True(s.Batch.LocalTasksFailureAccepted);
        Assert.Null(s.Batch.PendingLocalTaskFailureReport(state));
        Assert.Equal("running", s.Server.Phase);
        Assert.Equal(1, s.Submits);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(1, s.Releases);
        Assert.Null(s.Batch.PendingLocalTaskFailureReport(state with { Index = 1 }));
        Assert.Null(s.Batch.PendingLocalTaskFailureReport(state with { Attempt = 1 }));
        Assert.Null(s.Batch.PendingLocalTaskFailureReport(state with { BatchId = "successor" }));
    }

    [Fact]
    public async Task ReconnectionReleasesAnInitiallyRejectedOldTicketBeforeAllowingManualExecution()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var prior = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        using var process = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
        var s = new Scenario(epoch: epoch) { ReleaseAccepted = false };
        Assert.False(await s.Run());
        Assert.True(s.Batch.LocalTasksFailureAccepted);
        Assert.False(s.Batch.CleanupConfirmed);
        s.Batch.RunTask = Task.CompletedTask; // The actual loop above has exited; no live job is fabricated.
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest("own-ticket", epoch);
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, s.Batch);
        typeof(MainViewModel).GetField("_commandExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, executor);
        typeof(MainViewModel).GetField("_reportStatusRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, 1);
        pipe.BodyForTest = (op, data) => op == "task.resume"
            ? (true, null, null, new { status = "cleared_not_resumed" }) : null;
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            Assert.True(vm.HasLocalCoordinatedExecutionResponsibility);
            var reconcile = (Task<bool>)typeof(MainViewModel)
                .GetMethod("ReconcileLocalCoordinatedCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [s.Batch, CancellationToken.None])!;
            Assert.True(await reconcile.WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.True(s.Batch.LocalCleanupConfirmed);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.Null(executor.TakeoverTicketSnapshot);
            var release = Assert.Single(pipe.Received.Where(frame => frame.Operation == "task.resume")).Data!.Value;
            Assert.True(release.GetProperty("cancel").GetBoolean());
            Assert.Equal("own-ticket", release.GetProperty("takeoverTicket").GetString());
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            Assert.Equal(0, pipe.CountOf("task.stop"));
            Assert.Equal(0, s.Finalizes);
        }
        finally { IpcClient.PipeNameOverrideForTest = prior; s.Batch.MarkSettled(); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task UnconfirmedExactJobIdentityCannotDowngradeToNativeFailureOrAllowReplay(bool? confirmed)
    {
        var s = new Scenario { IdentityConfirmed = confirmed };
        Assert.False(await s.Run());
        Assert.False(s.Batch.LocalTasksFailed);
        Assert.Equal("unknown", s.Batch.Outcome);
        Assert.Equal(1, s.Submits);
        Assert.True(s.Confirms > 1);
        Assert.Equal(0, s.Finalizes);
        Assert.Contains("原作业身份", s.Batch.FailureReason);
    }

    [Theory]
    [InlineData("mixed_solo_or_unknown_mode")]
    [InlineData("second_native_failed")]
    [InlineData("preempted")]
    [InlineData("lowercase_result")]
    public async Task PartialOrDifferentNativeOutcomeUsesExistingCoordinatedFailurePath(string kind)
    {
        var evidence = kind switch
        {
            "mixed_solo_or_unknown_mode" => AllNativeSucceeded() with { Started = 2 },
            "second_native_failed" => AllNativeSucceeded() with { Started = 2, Failed = 1 },
            "preempted" => AllNativeSucceeded() with { JobResult = "Preempted" },
            _ => AllNativeSucceeded() with { JobResult = "failed" }
        };
        var s = new Scenario { Terminal = Failed(evidence) };
        Assert.False(await s.Run());
        Assert.False(s.Batch.LocalTasksFailed);
        Assert.Equal(0, s.Confirms);
        Assert.Equal("failed", s.Batch.Outcome);
        Assert.Equal("aborted", s.Server.Phase);
        Assert.Equal(0, s.Finalizes);
        Assert.Equal(1, s.Submits);
    }

    [Fact]
    public async Task ExplicitLocalCancellationStillWinsOverAnAvailableOrdinaryFailureTerminal()
    {
        var s = new Scenario { CancelAtSubmit = true };
        Assert.False(await s.Run());
        Assert.True(s.Batch.IsCancellationRequested);
        Assert.False(s.Batch.LocalTasksFailed);
        Assert.Equal("cancelled", s.Batch.Outcome);
        Assert.Equal(0, s.Confirms);
        Assert.Equal(0, s.Finalizes);
        Assert.Equal(1, s.Releases);
    }

    [Theory]
    [InlineData("failed", false, "Failed", 1, 1, 0, 0, 0, true, true)]
    [InlineData("failed", false, "Failed", 2, 1, 0, 0, 0, true, false)]
    [InlineData("failed", false, "Failed", 2, 1, 1, 0, 0, true, false)]
    [InlineData("failed", false, "Cancelled", 1, 1, 0, 0, 0, true, false)]
    [InlineData("failed", false, "Preempted", 1, 1, 0, 0, 0, true, false)]
    [InlineData("queueCancelled", true, "Cancelled", 1, 1, 0, 0, 0, true, false)]
    [InlineData("completed", false, "Ran", 1, 1, 0, 0, 0, true, false)]
    [InlineData("failed", true, "Failed", 1, 1, 0, 0, 0, true, false)]
    [InlineData("failed", false, "Failed", 1, 1, 0, 0, 0, false, false)]
    [InlineData("failed", false, "Failed", 2, 1, 0, 0, 1, true, false)]
    [InlineData("failed", false, "Failed", 1, 0, 0, 1, 0, true, false)]
    [InlineData("failed", false, "Failed", 0, 0, 0, 0, 0, true, false)]
    public void IsolationRequiresEveryNativeToBeConfirmedCooperativeSuccessAndTheWholeJobToFailNormally(
        string status, bool cancelled, string jobResult, int started, int completed, int failed, int nativeCancelled,
        int active, bool sealedResult, bool expected)
    {
        var evidence = new CoordinatedHoeingEvidence
        {
            Started = started, Completed = completed, Failed = failed, Cancelled = nativeCancelled,
            Active = active, Sealed = sealedResult, JobResult = jobResult
        };
        Assert.Equal(expected, evidence.IsLocalTasksFailure(new() { Status = status, Cancelled = cancelled, HoeingEvidence = evidence }));
    }

    [Theory]
    [InlineData(null, 1, 1, 0, 0, 0, false)]
    [InlineData(1, 1, 1, 0, 0, 0, true)]
    [InlineData(2, 1, 1, 0, 0, 0, false)]
    [InlineData(1, 2, 2, 0, 0, 0, false)]
    [InlineData(1, 1, 0, 0, 1, 0, false)]
    [InlineData(1, 1, 0, 1, 0, 0, false)]
    [InlineData(1, 1, 0, 0, 0, 1, false)]
    public void CancelledTailNeedsTheEntireFrozenNativePlanToHaveCompleted(int? planned,
        int started, int completed, int failed, int cancelled, int active, bool expected)
    {
        var evidence = new CoordinatedHoeingEvidence
        {
            PlannedNativeCount = planned, Started = started, Completed = completed, Failed = failed,
            Cancelled = cancelled, Active = active, Sealed = true, JobResult = "Cancelled"
        };
        var terminal = new BgiTaskQueueStatus { Status = "completed", Cancelled = true, HoeingEvidence = evidence };
        Assert.Equal(expected, evidence.IsLocalTasksCancellation(terminal));
        Assert.False((evidence with { Sealed = false }).IsLocalTasksCancellation(terminal));
        Assert.False((evidence with { JobResult = "Ran" }).IsLocalTasksCancellation(terminal));
        Assert.False(evidence.IsLocalTasksCancellation(new() { Status = "queueCancelled", Cancelled = true }));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("epoch")]
    [InlineData("request")]
    [InlineData("exit")]
    [InlineData("state")]
    [InlineData("cancellation")]
    public void CancellationIsolationRequiresTheOriginalCancelledJobToHaveExited(string mismatch)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var epoch = new BgiEpoch { ProcessId = 17, StartTicksUtc = 170 };
        var valid = new BgiJobInfo
        {
            JobId = jobId, Epoch = epoch, IdempotencyKey = "original", State = "cancelled",
            WasCancelled = true, ExecutionExitConfirmed = true
        };
        Assert.True(CoordinatedBatchLoop.ConfirmsLocalTaskExit(valid, jobId, epoch, "original", true));
        var wrong = new BgiJobInfo
        {
            JobId = mismatch == "identity" ? Guid.NewGuid().ToString("N") : jobId,
            Epoch = mismatch == "epoch" ? new() { ProcessId = 18, StartTicksUtc = 180 } : epoch,
            IdempotencyKey = mismatch == "request" ? "successor" : "original",
            State = mismatch == "state" ? "running" : "cancelled",
            WasCancelled = mismatch != "cancellation", ExecutionExitConfirmed = mismatch != "exit"
        };
        Assert.False(CoordinatedBatchLoop.ConfirmsLocalTaskExit(wrong, jobId, epoch, "original", true));
    }

    private static CoordinatedHoeingEvidence AllNativeSucceeded() =>
        new() { Started = 1, Completed = 1, Sealed = true, JobResult = "Failed", Revision = 3 };
    private static BgiTaskQueueStatus Failed(CoordinatedHoeingEvidence evidence) =>
        new() { Status = "failed", ErrorCode = "hoeing_incomplete", HoeingEvidence = evidence };
    private static CoordinatedBatchSnapshot Client(BgiCoordinatorServer.Services.CoordinatedBatchSnapshot snapshot) =>
        JsonSerializer.Deserialize<CoordinatedBatchSnapshot>(JsonSerializer.Serialize(snapshot))!;
    [Fact]
    public async Task NewModeOrdinaryFailuresContinueAllBindingsAndTheConfiguredFinalActionOnce()
    {
        var s = new Scenario(bindingCount: 2, continuation: true);
        Assert.True(await s.Run());
        Assert.Equal(2, s.Submits);
        Assert.Equal(2, s.Confirms);
        Assert.Equal(1, s.Finalizes);
        Assert.Equal(0, s.Cancels);
        Assert.Equal(0, s.Releases);
        Assert.True(s.Batch.LocalCompletionReady);
        Assert.False(s.Batch.LocalSucceeded);
        Assert.True(s.Batch.LocalTasksFailed);
        Assert.Equal("completed_with_errors", s.Batch.Outcome);
        Assert.Equal("completed", s.Server.Phase);
        Assert.Equal(2, s.Server.Index);
        Assert.Contains("self", s.Server.Reason);
    }

}
