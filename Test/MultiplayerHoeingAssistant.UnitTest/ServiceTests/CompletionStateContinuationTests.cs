using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CompletionStateContinuationTests
{
    private static readonly BgiEpoch Epoch = new() { ProcessId = 19, StartTicksUtc = 190 };
    private static readonly string Job = Guid.NewGuid().ToString("N");

    private static OnlineHoeingBatch Create(TaskConflictPolicy policy = TaskConflictPolicy.RunSpecified, bool priority = false) => new(new("completion", 7, 0, 0, 1, "completed", "")
    {
        Protocol = "hoeing.start.v2", PriorityRecovery = priority,
        ParticipantUids = ["shark"],
        Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            { ["shark"] = [new("联机精英", "group")] },
    }, "shark", new TaskConflictPolicySettings
    {
        Policy = policy, SpecifiedTaskType = "onedragon", SpecifiedTaskName = "收尾任务",
    }) { ExecutionEpoch = Epoch, StateContinuationEnabled = true };

    private static CommandResult Accepted() => new()
    {
        Status = "success", JobId = Job, TargetProcessId = Epoch.ProcessId,
        TargetStartTicksUtc = Epoch.StartTicksUtc.ToString(),
    };

    private static BgiTaskQueueStatus Terminal(OnlineHoeingBatch batch, string status = "completed", bool cancelled = false,
        string? key = null) => new()
    {
        TaskHandle = Job, Epoch = Epoch, IdempotencyKey = key ?? batch.PolicyRequestKey,
        Status = status, Cancelled = cancelled, ExecutionExitConfirmed = true,
    };

    [Theory]
    [InlineData("alive")]
    [InlineData("unknown")]
    [InlineData("missing-stop")]
    [InlineData("stopped")]
    [InlineData("observed-success")]
    [InlineData("managed")]
    public void CompletionProcessRecoveryDoesNotInventExitPermissionOrRepeatKnownSuccess(string blocked)
    {
        var batch = Create(blocked == "managed" ? TaskConflictPolicy.Resume : TaskConflictPolicy.RunSpecified, priority: true);
        batch.StateContinuationStopVersion = 7;
        batch.TakeoverTicket = "original";
        batch.MarkLocalSuccess(Epoch);
        if (blocked != "missing-stop") batch.EnsureRecoveryStopSignal();
        if (blocked == "stopped") batch.RecoveryStopSignal!.Stop();
        batch.PolicyStartAttempted = true;
        batch.RecordPolicyExecution(new(Job, Epoch, true));
        if (blocked == "observed-success") batch.PolicyTerminalResult = Terminal(batch);
        if (blocked == "managed") batch.SuspendedRawData = System.Text.Json.JsonSerializer.Serialize(new
            { recoverySnapshot = new { TaskType = "group", ConfigName = "original", ManagedByWorkflow = true, NodeId = "B" } });
        var key = batch.PolicyRequestKey;
        try
        {
            Assert.False(batch.BeginCompletionProcessRecovery(Epoch, new() { ProcessId = 20, StartTicksUtc = 200 },
                key, nativeResume: blocked == "managed", _ => blocked == "alive" ? true : blocked == "unknown" ? null : false));
            Assert.Same(Epoch, batch.ExecutionEpoch);
            Assert.Equal(key, batch.PolicyRequestKey);
            Assert.Equal(Job, batch.PolicyExecution!.JobId);
            Assert.Empty(batch.FailedPolicyAttempts);
        }
        finally { batch.RecoveryStopSignal?.Dispose(); }
    }

    [Fact]
    public async Task NativeResumeFailureReentersWithFreshReservationAfterExit_KeepingOriginalSnapshot()
    {
        var batch = Create(TaskConflictPolicy.Resume, priority: true);
        batch.TakeoverTicket = "original-resume";
        batch.PolicyTicketReleased = true;
        batch.PolicyStartAttempted = true;
        batch.SuspendedRawData = System.Text.Json.JsonSerializer.Serialize(new { recoverySnapshot = new
        { TaskType = "solo", ProjectName = "原独立任务", NodeId = (string?)null, ManagedByWorkflow = false,
            SoloSettingsJson = "{\"mode\":\"original\"}" } });
        batch.RecordPolicyExecution(new(Job, Epoch, true));
        var raw = batch.SuspendedRawData;
        var nextJob = Guid.NewGuid().ToString("N");
        var reads = 0; var starts = 0; var reservations = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, CanRetryNativeResume = true,
            ExpectedRequestKey = () => batch.TakeoverTicket is { } ticket ? "resume/" + ticket : null,
            Prepare = _ =>
            {
                Assert.True(reads >= 3); Assert.True(batch.NativeResumeRetryPending);
                Assert.Null(batch.TakeoverTicket);
                Assert.True(CommandExecutor.TryReadNativeResumeSnapshot(batch.SuspendedRawData, out var snapshot));
                Assert.Equal("{\"mode\":\"original\"}", snapshot.GetProperty("SoloSettingsJson").GetString());
                batch.TakeoverTicket = "new-resume-attempt"; batch.PolicyTicketReleased = false; reservations++;
                return Task.FromResult(true);
            },
            Start = _ =>
            {
                starts++; batch.PolicyStartAttempted = true; batch.PolicyTicketReleased = true;
                return Task.FromResult(new CommandResult { Status = "success", JobId = nextJob,
                    TargetProcessId = Epoch.ProcessId, TargetStartTicksUtc = Epoch.StartTicksUtc.ToString() });
            },
            Query = _ =>
            {
                reads++;
                return Task.FromResult<BgiTaskQueueStatus?>(new() { TaskHandle = starts == 0 ? Job : nextJob,
                    Epoch = Epoch, IdempotencyKey = "resume/" + batch.TakeoverTicket,
                    Status = starts == 0 ? reads < 3 ? "running" : "failed" : "completed",
                    ExecutionExitConfirmed = starts > 0 || reads >= 3 });
            },
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = ct => Task.Delay(1, ct)
        }, batch.Cts.Token).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, reservations); Assert.Equal(1, starts);
        Assert.Equal(raw, batch.SuspendedRawData);
        Assert.Equal(nextJob, batch.PolicyExecution!.JobId);
        Assert.Single(batch.FailedPolicyAttempts);
        var restored = OnlineHoeingBatch.Restore(batch.CaptureRecovery());
        Assert.True(restored.NativeResumeRetryPending);
        Assert.Equal(raw, restored.SuspendedRawData);
        Assert.Equal("new-resume-attempt", restored.TakeoverTicket);
    }

    [Fact]
    public void NativeRetryDoesNotAcceptManagedOrUnclassifiedSnapshots()
    {
        static string Raw(bool? managed, string? node) => System.Text.Json.JsonSerializer.Serialize(new
        { recoverySnapshot = new { TaskType = "group", GroupName = "原配置组", ManagedByWorkflow = managed, NodeId = node } });
        Assert.True(CommandExecutor.TryReadNativeResumeSnapshot(Raw(false, null), out _));
        Assert.False(CommandExecutor.TryReadNativeResumeSnapshot(Raw(true, "original-node"), out _));
        Assert.False(CommandExecutor.TryReadNativeResumeSnapshot(Raw(false, "original-node"), out _));
        Assert.False(CommandExecutor.TryReadNativeResumeSnapshot(Raw(null, null), out _));
    }

    [Fact]
    public async Task FailedSpecifiedActionRetriesOnlyAfterConfirmedExit_AndRetainsFailure()
    {
        var batch = Create(priority: true);
        var starts = 0; var reads = 0; var active = 0; var maximumActive = 0;
        var keys = new List<string>();
        string? job = null;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => Task.FromResult(active == 0),
            Start = _ =>
            {
                starts++; active++; maximumActive = Math.Max(maximumActive, active);
                job = Guid.NewGuid().ToString("N"); keys.Add(batch.PolicyRequestKey);
                batch.PolicyStartAttempted = true;
                return Task.FromResult(new CommandResult { Status = "success", JobId = job,
                    TargetProcessId = Epoch.ProcessId, TargetStartTicksUtc = Epoch.StartTicksUtc.ToString() });
            },
            Query = _ =>
            {
                reads++;
                var exited = starts == 2 || reads >= 3;
                if (exited) active = 0;
                return Task.FromResult<BgiTaskQueueStatus?>(new() { TaskHandle = job, Epoch = Epoch,
                    IdempotencyKey = batch.PolicyRequestKey, Status = starts == 1 ? "failed" : "completed",
                    ErrorMessage = starts == 1 ? "ordinary failure" : null, ExecutionExitConfirmed = exited });
            },
            Reconcile = _ => starts == 1 ? throw new IOException("old ledger unavailable") : Task.FromResult(true),
            Acknowledge = (_, _) => Task.FromResult(true), Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, starts);
        Assert.Equal(1, maximumActive);
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Single(batch.FailedPolicyAttempts);
        Assert.Equal("ordinary failure", batch.FailedPolicyAttempts[0].ErrorMessage);
        Assert.Equal("completed", batch.PolicyTerminalResult!.Status);
    }

    [Fact]
    public async Task ExplicitStopAfterFailedExitPreventsNextCompletionAttempt()
    {
        var batch = Create(priority: true);
        var starts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => Task.FromResult(true),
            Start = _ => { starts++; batch.PolicyStartAttempted = true; return Task.FromResult(Accepted()); },
            Query = _ => { batch.Cancel(); return Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch, "failed")); },
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = ct => Task.Delay(1, ct)
        }, batch.Cts.Token));
        Assert.Equal(1, starts);
        Assert.True(batch.ExplicitStopRequested);
    }

    [Fact]
    public async Task SharkBusyThenIdleStartsTheSameFrozenCompletionWithoutAnotherOnlineOrReset()
    {
        var batch = Create();
        var prepares = 0; var starts = 0; var delays = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => Task.FromResult(++prepares > 1),
            Start = _ => { starts++; batch.PolicyStartAttempted = true; return Task.FromResult(Accepted()); },
            Query = _ => Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch)),
            Reconcile = _ => Task.FromResult(true),
            Acknowledge = (_, _) => Task.FromResult(true),
            Delay = _ => { delays++; return Task.CompletedTask; },
        }, batch.Cts.Token);
        Assert.Equal(2, prepares);
        Assert.Equal(1, starts);
        Assert.True(delays > 0);
        Assert.Equal(Job, batch.PolicyExecution!.JobId);
        Assert.Equal("completed", batch.PolicyTerminalResult!.Status);
        Assert.True(batch.PolicyResultAcknowledged);
    }

    [Fact]
    public async Task AcceptedActionSurvivesRecordFailuresAndLostAcknowledgementWithoutStartingAgain()
    {
        var batch = Create();
        var prepares = 0; var starts = 0; var records = 0; var reads = 0; var acknowledgements = 0;
        var ports = new MainViewModel.CompletionContinuationPorts
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => { prepares++; return Task.FromResult(true); },
            Start = _ => { starts++; batch.PolicyStartAttempted = true; return Task.FromResult(Accepted()); },
            Query = _ => { reads++; return Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch)); },
            Reconcile = _ => ++records <= 1 ? throw new IOException("record store unavailable") : Task.FromResult(true),
            Acknowledge = (result, _) =>
            {
                Assert.Same(result, batch.PolicyTerminalResult);
                Assert.True(batch.LocalExecutionReleased);
                return Task.FromResult(++acknowledgements > 1);
            },
            Delay = _ => Task.CompletedTask,
        };
        await MainViewModel.RunCompletionContinuationAsync(batch, ports, batch.Cts.Token);
        await MainViewModel.RunCompletionContinuationAsync(batch, ports, batch.Cts.Token);
        Assert.Equal(1, starts);
        Assert.Equal(1, prepares);
        Assert.Equal(1, reads);
        Assert.Equal(2, acknowledgements);
        Assert.True(batch.PolicyResultAcknowledged);
        Assert.True(batch.LocalExecutionReleased);
    }

    [Theory]
    [InlineData("resume/original-ticket")]
    [InlineData("original-plan-resume-key")]
    public async Task LostResumeReplyUsesItsActualRetainedKeyAndAcknowledgesOnlyThatResult(string resumeKey)
    {
        var batch = Create(TaskConflictPolicy.Resume);
        var starts = 0; var prepares = 0; var acknowledgements = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, ExpectedRequestKey = () => resumeKey,
            Prepare = _ => { prepares++; return Task.FromResult(true); },
            Start = _ =>
            {
                starts++; batch.PolicyStartAttempted = true;
                throw new IOException("original resume accepted but reply lost");
            },
            Query = _ => Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch, key: resumeKey)),
            Reconcile = _ => Task.FromResult(true),
            Acknowledge = (terminal, _) =>
            {
                acknowledgements++;
                Assert.Equal(resumeKey, terminal.IdempotencyKey);
                Assert.NotEqual(batch.PolicyRequestKey, terminal.IdempotencyKey);
                Assert.True(batch.LocalExecutionReleased);
                return Task.FromResult(true);
            },
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token);
        Assert.Equal(1, starts);
        Assert.Equal(1, prepares);
        Assert.Equal(1, acknowledgements);
        Assert.Equal(Job, batch.PolicyExecution!.JobId);
    }

    [Fact]
    public async Task LostStartReplyIsResolvedByOriginalKeyInsteadOfStartingAnotherAction()
    {
        var batch = Create();
        var starts = 0; var prepares = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => { prepares++; return Task.FromResult(true); },
            Start = _ =>
            {
                starts++; batch.PolicyStartAttempted = true;
                throw new IOException("accepted reply lost");
            },
            Query = _ => Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch)),
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token);
        Assert.Equal(1, starts);
        Assert.Equal(1, prepares);
        Assert.Equal(Job, batch.PolicyExecution!.JobId);
        Assert.True(batch.PolicyResultAcknowledged);
    }

    [Fact]
    public async Task ExplicitStopRetainsLateAcceptedJobForExactCancellationAndDoesNotReviveIt()
    {
        var batch = Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0; var cancels = 0;
        batch.ConfigurePolicyCancellation(_ => { Interlocked.Increment(ref cancels); return Task.FromResult(true); });
        var task = MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => Task.FromResult(true),
            Start = _ => { starts++; entered.TrySetResult(); return reply.Task; },
            Query = _ => throw new InvalidOperationException("Stopped action must not be observed as new work"),
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        batch.Cancel(userInitiated: true);
        reply.SetResult(Accepted());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(Job, batch.PolicyExecution!.JobId);
        Assert.True(await batch.WaitPolicyCancellationAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, starts);
        Assert.Equal(1, cancels);
        Assert.Null(batch.PolicyTerminalResult);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("queueCancelled", true)]
    [InlineData("completed", true)]
    public async Task TrueFailureAndCancellationAreRetainedWithoutRetryingTheCompletedExecution(string state, bool cancelled)
    {
        var batch = Create();
        var starts = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => Task.FromResult(true),
            Start = _ => { starts++; return Task.FromResult(Accepted()); },
            Query = _ => Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch, state, cancelled)),
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token);
        Assert.Equal(1, starts);
        Assert.Equal(state, batch.PolicyTerminalResult!.Status);
        Assert.Equal(cancelled, batch.PolicyTerminalResult.Cancelled);
        Assert.NotNull(batch.CompletionActionWarning);
    }
    [Fact]
    public async Task ResumeResultIsNotAcknowledgedUntilOriginalPlanHasStoredIt()
    {
        var batch = Create(TaskConflictPolicy.Resume);
        var ready = false; var acknowledgements = 0; var waits = 0;
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, ExpectedRequestKey = () => "resume/original",
            Prepare = _ => Task.FromResult(true), Start = _ => Task.FromResult(Accepted()),
            Query = _ => Task.FromResult<BgiTaskQueueStatus?>(Terminal(batch, key: "resume/original")),
            Reconcile = _ => Task.FromResult(ready),
            // Count the attempt before asserting outside the worker: its error retry must not swallow a test assertion.
            Acknowledge = (_, _) => { acknowledgements++; return Task.FromResult(true); },
            Delay = _ =>
            {
                if (batch.PolicyTerminalResult is not null && ++waits == 3)
                {
                    Assert.Equal(0, acknowledgements);
                    Assert.False(batch.LocalExecutionReleased);
                    ready = true;
                }
                return Task.CompletedTask;
            },
        }, batch.Cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, acknowledgements);
        Assert.Equal(Job, batch.PolicyExecution!.JobId);
    }

    [Fact]
    public async Task MissingCompletionConfigurationFinishesPhaseAndLaterStopCleansUp()
    {
        var batch = Create(); var starts = 0; var cleanup = 0;
        batch.ConfigurePolicyCleanup(() => { cleanup++; return Task.FromResult(true); });
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, RetainedResult = true,
            Prepare = _ => { batch.PolicyTicketReleased = true; return Task.FromResult(true); },
            Start = _ => { starts++; return Task.FromResult(new CommandResult { ErrorCode = "specified_task_missing", Message = "配置已移除" }); },
            Query = _ => throw new InvalidOperationException("Never accepted"),
            Reconcile = _ => Task.FromResult(true),
            Acknowledge = (_, _) => throw new InvalidOperationException("No accepted result to acknowledge"),
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, starts);
        Assert.Contains("配置已移除", batch.CompletionActionWarning);
        Assert.Null(batch.PolicyExecution);
        batch.Cancel();
        Assert.True(await batch.WaitCancelledPolicyCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, cleanup);
    }

    [Theory]
    [InlineData("manual_stop_fence")]
    [InlineData("manual_stop_cooldown")]
    public async Task BgiExplicitStopDuringResumeCancelsTheWorkerAndCompletesItsPhase(string code)
    {
        var batch = Create(TaskConflictPolicy.Resume); var cleanup = 0;
        batch.ConfigurePolicyCleanup(() => { cleanup++; return Task.FromResult(true); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            RequireAuthority = () => { }, ExpectedRequestKey = () => "resume/original",
            Prepare = _ => Task.FromResult(true),
            Start = _ => Task.FromResult(new CommandResult { ErrorCode = code, Message = "BGI stopped" }),
            Query = _ => throw new InvalidOperationException("No further observation after explicit stop"),
            Reconcile = _ => Task.FromResult(true), Acknowledge = (_, _) => Task.FromResult(true),
            Delay = _ => Task.CompletedTask,
        }, batch.Cts.Token));
        Assert.True(batch.ExplicitStopRequested);
        Assert.True(await batch.WaitCancelledPolicyCleanupAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, cleanup);
    }

}
