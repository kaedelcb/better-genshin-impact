using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CommandExecutorManualResetTests
{
    private static CommandExecutor Executor() => new(null!, "unused")
    {
        ResetProcessOverrideForTest = () => Task.FromResult(true),
        ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(ReadyResponse()),
        ResetReadyTimeoutForTest = TimeSpan.FromSeconds(2)
    };

    [Fact]
    public async Task ResetBypassesHeldSuspendGate_AndRejectsItsOldWaiterAfterPhysicalReset()
    {
        var executor = Executor();
        executor.SeedTakeoverStateForTest("old-ticket", new() { ProcessId = 10, StartTicksUtc = 100 });
        var gate = (SemaphoreSlim)typeof(CommandExecutor).GetField("_suspendGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(executor)!;
        await gate.WaitAsync();
        Task<CommandResult> oldSuspend;
        try
        {
            oldSuspend = executor.ExecuteSuspendAsync("旧批次");
            var result = await executor.ForceResetAllAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("success", result.Status);
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.False(oldSuspend.IsCompleted);
        }
        finally { gate.Release(); }

        var oldResult = await oldSuspend.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("reset_superseded", oldResult.ErrorCode);
        Assert.Null(executor.TakeoverTicketSnapshot);
    }

    [Fact]
    public async Task FailedPhysicalResetPreservesOldTicket_AndOneLaterRetryCanRecover()
    {
        var executor = Executor();
        executor.SeedTakeoverStateForTest("old-ticket", new() { ProcessId = 10, StartTicksUtc = 100 });
        executor.ResetProcessOverrideForTest = () => Task.FromResult(false);

        Assert.Equal("bgi_reset_failed", (await executor.ForceResetAllAsync()).ErrorCode);
        Assert.Equal("old-ticket", executor.TakeoverTicketSnapshot);
        executor.ResetProcessOverrideForTest = () => Task.FromResult(true);

        Assert.Equal("success", (await executor.ForceResetAllAsync()).Status);
        Assert.Null(executor.TakeoverTicketSnapshot);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"executionIdle\":true}")]
    [InlineData("{\"executionIdle\":true,\"running\":false,\"slotOccupied\":false,\"takeoverActive\":false,\"hasSuspendedTaskContext\":false,\"queueDepth\":1,\"bgiEpoch\":{\"processId\":11,\"startTicksUtc\":200}}")]
    public async Task MissingOrQueuedStatusNeverClaimsReady(string json)
    {
        var executor = Executor();
        executor.ResetReadyTimeoutForTest = TimeSpan.FromMilliseconds(40);
        executor.ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(new() { Success = true, Data = json });

        var result = await executor.ForceResetAllAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("failed", result.Status);
        Assert.Equal("bgi_reset_not_ready", result.ErrorCode);
    }

    [Fact]
    public async Task HungReadinessQueryIsBounded()
    {
        var executor = Executor();
        executor.ResetReadyTimeoutForTest = TimeSpan.FromMilliseconds(40);
        executor.ResetStatusQueryOverrideForTest = _ => new TaskCompletionSource<IpcResponse?>().Task;

        var result = await executor.ForceResetAllAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("bgi_reset_not_ready", result.ErrorCode);
    }

    [Fact]
    public async Task ShortIpcReadyWhileExternalLinkIsStillDownCannotClaimReadyForMultiplayer()
    {
        using var ext = new BgiExternalClient();
        var executor = new CommandExecutor(null!, "unused", () => ext)
        {
            ResetProcessOverrideForTest = () => Task.FromResult(true),
            ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(ReadyResponse()),
            ResetReadyTimeoutForTest = TimeSpan.FromMilliseconds(40)
        };

        var result = await executor.ForceResetAllAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("bgi_reset_not_ready", result.ErrorCode);
    }

    [Fact]
    public void OldEpochCannotConfirmNewExecutionEnvironment()
    {
        using var process = Process.GetCurrentProcess();
        var old = new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
        Assert.False(CommandExecutor.IsManualResetReady(ReadyResponse().Data, old));
        Assert.True(CommandExecutor.IsManualResetReady(ReadyResponse().Data, new() { ProcessId = 10, StartTicksUtc = 100 }));
    }

    [Fact]
    public async Task ConcurrentResetAndNewStartAreRejectedDuringReset_AndFreshStartWorksAfterwards()
    {
        await using var pipe = new BgiInstancePipeDouble();
        pipe.Start();
        var prior = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        var physicalEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPhysical = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = Executor();
        executor.ResetProcessOverrideForTest = () => { physicalEntered.SetResult(true); return finishPhysical.Task; };
        try
        {
            var reset = executor.ForceResetAllAsync();
            await physicalEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("reset_in_progress", (await executor.ForceResetAllAsync()).ErrorCode);
            Assert.Equal("reset_in_progress", (await executor.ExecuteAsync(StartCommand())).ErrorCode);
            Assert.Equal(0, pipe.CountOf("task.start"));
            finishPhysical.SetResult(true);

            Assert.Equal("success", (await reset).Status);
            Assert.Equal("success", (await executor.ExecuteAsync(StartCommand())).Status);
            Assert.Equal(1, pipe.CountOf("task.start"));
        }
        finally { finishPhysical.TrySetResult(true); IpcClient.PipeNameOverrideForTest = prior; }
    }

    [Fact]
    public async Task OldStartWaitingOnRecoveryCannotRunAfterReset_ButANewStartCan()
    {
        await using var pipe = new BgiInstancePipeDouble();
        pipe.Start();
        var prior = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        var recoveryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRecovery = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new CommandExecutor(null!, "unused", recoverOnlineHandoff: () =>
        {
            recoveryEntered.TrySetResult(true);
            return finishRecovery.Task;
        })
        {
            ResetProcessOverrideForTest = () => Task.FromResult(true),
            ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(ReadyResponse())
        };
        try
        {
            var oldStart = executor.ExecuteAsync(StartCommand());
            await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("success", (await executor.ForceResetAllAsync()).Status);
            finishRecovery.SetResult(true);

            Assert.Equal("failed", (await oldStart).Status);
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal("success", (await executor.ExecuteAsync(StartCommand())).Status);
            Assert.Equal(1, pipe.CountOf("task.start"));
        }
        finally { finishRecovery.TrySetResult(true); IpcClient.PipeNameOverrideForTest = prior; }
    }

    [Fact]
    public async Task OldResumeReplyAfterResetCannotClearANewTicket()
    {
        await using var pipe = new BgiInstancePipeDouble();
        var replyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReply = new ManualResetEventSlim();
        pipe.BodyForTest = (operation, _) =>
        {
            if (operation != "task.resume") return null;
            replyEntered.TrySetResult(true);
            releaseReply.Wait(TimeSpan.FromSeconds(5));
            return (true, null, null, new { status = "cleared_not_resumed" });
        };
        pipe.Start();
        var prior = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        var executor = Executor();
        executor.SeedTakeoverStateForTest("old-ticket", new() { ProcessId = 10, StartTicksUtc = 100 });
        try
        {
            var oldResume = executor.ExecuteResumeAsync(expectedTicket: "old-ticket");
            await replyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("success", (await executor.ForceResetAllAsync()).Status);
            executor.SeedTakeoverStateForTest("new-ticket", new() { ProcessId = 11, StartTicksUtc = 200 });
            releaseReply.Set();

            Assert.Equal("reset_superseded", (await oldResume.WaitAsync(TimeSpan.FromSeconds(2))).ErrorCode);
            Assert.Equal("new-ticket", executor.TakeoverTicketSnapshot);
        }
        finally { releaseReply.Set(); IpcClient.PipeNameOverrideForTest = prior; }
    }

    [Fact]
    public async Task OldCommandStartingItsPolicyCleanupAfterResetCannotConsumeANewTicket()
    {
        await using var pipe = new BgiInstancePipeDouble();
        pipe.Start();
        var prior = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        var executor = Executor();
        var context = (AsyncLocal<int?>)typeof(CommandExecutor).GetField("_commandStopVersion",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executor)!;
        context.Value = 0; // 模拟旧启动完成后才进入策略收尾的异步命令上下文。
        try
        {
            Assert.Equal("success", (await executor.ForceResetAllAsync()).Status);
            executor.SeedTakeoverStateForTest("new-ticket", new() { ProcessId = 11, StartTicksUtc = 200 });

            var result = await executor.ExecuteResumeAsync(cancel: true);

            Assert.Equal("reset_superseded", result.ErrorCode);
            Assert.Equal("new-ticket", executor.TakeoverTicketSnapshot);
            Assert.Equal(0, pipe.CountOf("task.resume"));
        }
        finally { context.Value = null; IpcClient.PipeNameOverrideForTest = prior; }
    }

    private static RemoteCommand StartCommand() => new()
    {
        Cmd = "start_oneclick", Params = new() { ["configName"] = "重置后联机" }
    };

    private static IpcResponse ReadyResponse()
    {
        using var process = Process.GetCurrentProcess();
        return new() { Success = true, Data = JsonSerializer.Serialize(new
        {
            executionIdle = true, running = false, slotOccupied = false,
            takeoverActive = false, hasSuspendedTaskContext = false, queueDepth = 0,
            currentTaskHandle = (string?)null,
            bgiEpoch = new { processId = process.Id, startTicksUtc = process.StartTime.ToUniversalTime().Ticks }
        }) };
    }
}
