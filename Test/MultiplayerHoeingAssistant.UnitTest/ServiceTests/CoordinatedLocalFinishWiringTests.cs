using System.Diagnostics;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CoordinatedLocalFinishWiringTests
{
    private static OnlineHoeingBatch Batch(TaskConflictPolicy policy = TaskConflictPolicy.Stop) => new(
        new CoordinatedBatchSnapshot("local-wire-" + Guid.NewGuid().ToString("N"), 23, 0, 0, 0, "running", "")
        {
            Protocol = "hoeing.start.v2", Source = "manual", ParticipantUids = ["self", "peer"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            {
                ["self"] = [new("本机锄地", "group")], ["peer"] = [new("队友后置脚本", "group")]
            }
        }, "self", new() { Policy = policy, SpecifiedTaskName = "后续任务", SpecifiedTaskType = "group" });

    private static MainViewModel Vm(OnlineHoeingBatch batch, CommandExecutor? executor = null)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var vm = new MainViewModel();
        Set(vm, "_activeBatch", batch);
        Set(vm, "_reportStatusRunning", 1); // The focused VM tests do not start an unrelated status sampler.
        Set(vm, "_config", new AssistConfig { PlayerUid = "self" });
        if (executor is not null) Set(vm, "_commandExecutor", executor);
        return vm;
    }

    [Fact]
    public void NaturalGroupCompletionAloneDoesNotReleaseResponsibilityButCompletedLocalCleanupDoes()
    {
        var batch = Batch();
        var vm = Vm(batch);
        Assert.True(vm.HasLocalCoordinatedExecutionResponsibility);
        batch.MarkLocalSuccess(Epoch());
        Assert.True(vm.HasLocalCoordinatedExecutionResponsibility);
        batch.MarkLocalExecutionReleased();
        Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
        Assert.True(batch.IsAlive); // A new cooperative batch is still guarded until team cleanup.
    }

    [Fact]
    public void PhysicallyCleanedFailedBatchDoesNotBlockManualTasksJustBecauseAckIsPending()
    {
        var batch = Batch();
        var vm = Vm(batch);
        batch.Outcome = "failed";
        batch.LocalCleanupConfirmed = true;
        Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
        Assert.False(batch.LocalSucceeded);
        Assert.True(batch.IsAlive);
    }

    [Fact]
    public async Task DisconnectedAuthorityIsNotProofThatTheOldBatchNoLongerExists()
    {
        var batch = Batch();
        var vm = Vm(batch);
        batch.MarkLocalSuccess(Epoch());
        batch.MarkLocalExecutionReleased();
        batch.LocalCleanupConfirmed = true;
        batch.RunTask = Task.CompletedTask;
        await using var disconnected = new SignalRClient();
        Set(vm, "_signalRClient", disconnected);
        await (Task)typeof(MainViewModel).GetMethod("RetryCoordinatedCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [batch])!;
        Assert.Same(batch, typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));
        Assert.True(batch.IsAlive);
        Assert.True(batch.LocalExecutionReleased);
        Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
        Assert.Equal("succeeded", batch.Outcome);
    }

    [Theory]
    [InlineData("participant_unknown:peer")]
    [InlineData("user_stop_all")]
    [InlineData("manual_reset")]
    public void TeamAbortCannotInferThatThisCompletedEndpointWasSelectedForStop(string reason)
    {
        var batch = Batch();
        var vm = Vm(batch);
        batch.MarkLocalSuccess(Epoch());
        State(vm, batch, reason);
        Assert.False(batch.IsCancellationRequested);
        Assert.Equal("succeeded", batch.Outcome);
        Assert.Equal(reason, batch.TeamFailureReason);
        batch.Cancel(); // Actual local selection enters the local cancellation path.
        State(vm, batch, "user_stop_all");
        Assert.True(batch.IsCancellationRequested);
        Assert.Equal("cancelled", batch.Outcome);
    }

    [Fact]
    public async Task ProductionFinalizerReleasesOnlyOwnTicketBeforeTeamCompletionAndLateCleanupKeepsNewTicket()
    {
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var epoch = Epoch();
        var ticket = Guid.NewGuid().ToString("N");
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        var batch = Batch();
        batch.TakeoverTicket = ticket;
        batch.ExecutionEpoch = epoch;
        batch.TakenOver = true;
        batch.MarkLocalSuccess(epoch);
        var vm = Vm(batch, executor);
        var released = false;
        pipe.BodyForTest = (operation, data) => operation switch
        {
            "task.status" => (true, null, null, Status(epoch, released)),
            "task.resume" => Release(),
            _ => null
        };
        (bool, string?, string?, object?) Release()
        {
            released = true;
            return (true, null, null, new { status = "cleared_not_resumed" });
        }
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            Assert.False(batch.CoordinatedSucceeded);
            await Finalize(vm, batch, executor, epoch);
            Assert.True(batch.LocalExecutionReleased);
            Assert.True(batch.LocalCleanupConfirmed);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            var release = Assert.Single(pipe.Received.Where(r => r.Operation == "task.resume")).Data!.Value;
            Assert.True(release.GetProperty("cancel").GetBoolean());
            Assert.Equal(ticket, release.GetProperty("takeoverTicket").GetString());
            var newTicket = Guid.NewGuid().ToString("N");
            executor.SeedTakeoverStateForTest(newTicket, epoch);
            batch.RunTask = Task.CompletedTask;
            Assert.True(await Reconcile(vm, batch));
            Assert.Equal(newTicket, executor.TakeoverTicketSnapshot);
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.Equal(0, pipe.CountOf("task.stop"));
            Assert.Equal(0, pipe.CountOf("task.start"));
        }
        finally { IpcClient.PipeNameOverrideForTest = previous; batch.MarkSettled(); }
    }

    [Fact]
    public async Task FailedPolicyIsNotReplayedWhenReconnectionOnlySettlesOldCleanup()
    {
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var epoch = Epoch();
        var ticket = Guid.NewGuid().ToString("N");
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        var batch = Batch(TaskConflictPolicy.RunSpecified);
        batch.TakeoverTicket = ticket;
        batch.ExecutionEpoch = epoch;
        batch.TakenOver = true;
        batch.MarkLocalSuccess(epoch);
        var vm = Vm(batch, executor);
        var released = false;
        pipe.BodyForTest = (operation, data) => operation switch
        {
            "task.status" => (true, null, null, Status(epoch, released)),
            "task.resume" => Release(),
            "config.list" => (false, "config_unavailable", "本机配置读取暂时失败", null),
            _ => null
        };
        (bool, string?, string?, object?) Release()
        {
            released = true;
            return (true, null, null, new { status = "cleared_not_resumed" });
        }
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            var finalization = batch.BeginLocalFinalization(() => Finalize(vm, batch, executor, epoch));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => finalization);
            batch.FailureReason = failure.Message;
            batch.RunTask = Task.CompletedTask;
            Assert.True(batch.LocalSucceeded);
            Assert.False(batch.LocalExecutionReleased);
            Assert.True(await Reconcile(vm, batch));
            Assert.True(batch.LocalCleanupConfirmed);
            Assert.Equal("succeeded", batch.Outcome);
            Assert.Same(finalization, batch.BeginLocalFinalization(() => throw new InvalidOperationException("must not retry policy")));
            Assert.Equal(1, pipe.CountOf("config.list"));
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            Assert.Equal(failure.Message, batch.FailureReason);
        }
        finally { IpcClient.PipeNameOverrideForTest = previous; batch.MarkSettled(); }
    }

    [Theory]
    [InlineData(TaskConflictPolicy.Resume, true)]
    [InlineData(TaskConflictPolicy.RunSpecified, true)]
    [InlineData(TaskConflictPolicy.Stop, false)]
    public async Task ExitedOriginalProcessPreservesWholeJobSuccessButNeverReplaysCompletionActionToSuccessor(
        TaskConflictPolicy policy, bool warns)
    {
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var current = Epoch();
        var exited = new BgiEpoch { ProcessId = current.ProcessId, StartTicksUtc = current.StartTicksUtc - 1 };
        Assert.False(CommandExecutor.ProbeOriginalBgiProcess(exited));
        var ticket = Guid.NewGuid().ToString("N");
        var executor = new CommandExecutor(null!, "unused")
        {
            BgiRunningProbeOverride = () => throw new InvalidOperationException("No process launch or live BGI lookup is permitted.")
        };
        executor.SeedTakeoverStateForTest(ticket, exited);
        var batch = Batch(policy);
        batch.TakeoverTicket = ticket;
        batch.ExecutionEpoch = exited;
        batch.TakenOver = true;
        batch.MarkLocalSuccess(exited); // The old whole job terminal was already confirmed before its process exited.
        var vm = Vm(batch, executor);
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            await Finalize(vm, batch, executor, exited);
            Assert.True(batch.LocalSucceeded);
            Assert.True(batch.LocalExecutionReleased);
            Assert.True(batch.LocalCleanupConfirmed);
            Assert.Equal("succeeded", batch.Outcome);
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            if (warns)
                Assert.Equal("原 BGI 已退出，未执行绑定完成动作；未向新进程重放", batch.CompletionActionWarning);
            else Assert.Null(batch.CompletionActionWarning);
            Assert.Empty(pipe.Received); // Neither a normal policy RPC nor a command to a new BGI was sent.

            batch.RunTask = Task.CompletedTask;
            typeof(MainViewModel).GetMethod("CompleteCoordinatedCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [batch]);
            var message = (string?)typeof(MainViewModel)
                .GetField("_lastHoeingStartMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
            if (warns) Assert.Contains(batch.CompletionActionWarning!, message);
            else Assert.Equal("本端联机锄地已完成", message);
            Assert.False(batch.IsAlive);
            Assert.Empty(pipe.Received);
        }
        finally { IpcClient.PipeNameOverrideForTest = previous; batch.MarkSettled(); }
    }

    private static object Status(BgiEpoch epoch, bool released) => new
    {
        bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
        running = false, slotOccupied = false, executionSlotOccupied = false, executionIdentityAvailable = false,
        takeoverActive = !released, hasSuspendedTaskContext = !released
    };

    private static BgiEpoch Epoch()
    {
        using var process = Process.GetCurrentProcess();
        return new() { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
    }
    private static void Set(MainViewModel vm, string name, object value) => typeof(MainViewModel)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, value);
    private static Task Finalize(MainViewModel vm, OnlineHoeingBatch batch, CommandExecutor executor, BgiEpoch epoch) =>
        (Task)typeof(MainViewModel).GetMethod("FinalizeLocalCoordinatedBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [batch, executor, epoch, CancellationToken.None])!;
    private static Task<bool> Reconcile(MainViewModel vm, OnlineHoeingBatch batch) =>
        (Task<bool>)typeof(MainViewModel).GetMethod("ReconcileLocalCoordinatedCleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [batch, CancellationToken.None])!;
    private static void State(MainViewModel vm, OnlineHoeingBatch batch, string reason) => typeof(MainViewModel)
        .GetMethod("OnCoordinatedBatchState", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(vm, [batch.Start! with { Phase = "aborted", Reason = reason }]);
}
