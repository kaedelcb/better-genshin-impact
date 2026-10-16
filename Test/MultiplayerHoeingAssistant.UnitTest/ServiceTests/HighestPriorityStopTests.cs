using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class HighestPriorityStopTests
{
    [Theory]
    [InlineData("preparing")]
    [InlineData("running")]
    [InlineData("cleanupPending")]
    public async Task UserStop_IgnoresStaleBatchAndUnknownStatus_CancelsSelfAndAutomation(string phase)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        await using var pipe = new BgiInstancePipeDouble();
        var stopCount = 0;
        var postStopQueries = 0;
        var preStopQueries = 0;
        pipe.BodyForTest = (op, data) =>
        {
            if (op == "ext.task.stop")
            {
                Assert.Equal("all", data!.Value.GetProperty("stopScope").GetString());
                Assert.True(data.Value.GetProperty("clearQueue").GetBoolean());
                Interlocked.Increment(ref stopCount);
                return (true, null, null, new { status = "stop_requested" });
            }
            if (op is "task.status" or "ext.task.status")
            {
                if (Volatile.Read(ref stopCount) == 0)
                {
                    Interlocked.Increment(ref preStopQueries);
                    return (false, "status_unknown", "无法读取状态", null);
                }
                var busy = Interlocked.Increment(ref postStopQueries) < 3;
                return (true, null, null, Status(busy));
            }
            return null;
        };
        pipe.Start();
        var priorExt = BgiExternalClient.PipeNameOverrideForTest;
        var priorIpc = IpcClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        using var ext = new BgiExternalClient();
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await ext.StartAsync());
            var executor = new CommandExecutor(monitor, Environment.ProcessPath!, () => ext)
            {
                StopProcessOverrideForTest = _ => throw new InvalidOperationException("正常退出不能终止真实进程")
            };
            var vm = new MainViewModel();
            Set(vm, "_config", new AssistConfig { PlayerUid = "self" });
            Set(vm, "_commandExecutor", executor);
            var batch = new OnlineHoeingBatch(new CoordinatedBatchSnapshot("actual-current", 1, 0, 0, 0, phase, "")
            {
                Protocol = "hoeing.start.v2", ParticipantUids = ["self"],
                Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> { ["self"] = [] }
            }, "self", new()) { Phase = phase };
            Set(vm, "_activeBatch", batch);
            var automationStops = 0;
            vm.StopAllAutomationRequested += () => automationStops++;
            var command = new RemoteCommand { Cmd = "stop", CommandId = Guid.NewGuid().ToString("N"),
                Params = new() { ["stopScope"] = JsonSerializer.SerializeToElement("all"), ["hoeingBatchId"] = "stale" } };

            var first = Stop(vm, command);
            var echo = Stop(vm, command);
            var result = await first;
            Assert.Same(result, await echo);
            Assert.Equal("success", result.Status);
            Assert.Equal(1, stopCount);
            Assert.True(postStopQueries >= 3); // A receipt before physical drain cannot complete this stop.
            Assert.Equal(0, preStopQueries);
            Assert.True(batch.IsCancellationRequested);
            Assert.Equal(1, automationStops);
            Assert.Equal(0, pipe.CountOf("task.suspend"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            // The production broadcast entry still stops self when no server is connected.
            await (Task)typeof(MainViewModel).GetMethod("ExecuteUserStopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [null, true])!;
            Assert.Equal(2, stopCount);
            Assert.Equal(2, automationStops);
        }
        finally
        {
            IpcClient.PipeNameOverrideForTest = priorIpc;
            BgiExternalClient.PipeNameOverrideForTest = priorExt;
        }
    }

    [Theory]
    [InlineData(true, "success")]
    [InlineData(false, "failed")]
    public async Task MissingIpc_AlwaysAttemptsProcessFallback_AndNeverInventsSuccess(bool killed, string expected)
    {
        var prior = IpcClient.PipeNameOverrideForTest;
        IpcClient.PipeNameOverrideForTest = "HighestStop.NoServer." + Guid.NewGuid().ToString("N");
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        var called = 0;
        var executor = new CommandExecutor(monitor, Environment.ProcessPath!)
        {
            StopProcessOverrideForTest = _ => { called++; return Task.FromResult(killed); }
        };
        try
        {
            var result = await executor.ExecuteAsync(new() { Cmd = "stop" });
            Assert.Equal(1, called);
            Assert.Equal(expected, result.Status);
            if (!killed) Assert.Equal("stop_unconfirmed", result.ErrorCode);
        }
        finally { IpcClient.PipeNameOverrideForTest = prior; }
    }

    [Fact]
    public async Task StopDuringColdStart_RevokesTheWaitingStartBeforeItCanSubmit()
    {
        await using var pipe = new BgiInstancePipeDouble();
        pipe.BodyForTest = (op, _) => op switch
        {
            "ext.task.stop" => (true, null, null, new { status = "stop_requested" }),
            "ext.task.status" => (true, null, null, Status(false)),
            _ => null
        };
        pipe.Start();
        var prior = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var ext = new BgiExternalClient();
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await ext.StartAsync());
            var executor = new CommandExecutor(monitor, Environment.ProcessPath!, () => ext)
            {
                TaskStartBgiPresenceProbeOverride = () => false,
                TaskStartBgiLaunchOverride = () => true,
                TaskStatusQueryOverride = async _ => { probing.TrySetResult(); await ready.Task; return (false, false, null, null); },
                StopProcessOverrideForTest = _ => throw new InvalidOperationException("禁止终止真实进程")
            };
            var start = executor.ExecuteAsync(new() { Cmd = "start_group", Params = new() { ["groupName"] = "旧启动请求" } });
            await probing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopped = await executor.ExecuteAsync(new() { Cmd = "stop" });
            Assert.Equal("success", stopped.Status);
            ready.TrySetResult();
            var startResult = await start.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("cancelled_user", startResult.ErrorCode);
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            Assert.Equal(0, pipe.CountOf("task.start"));
        }
        finally { ready.TrySetResult(); BgiExternalClient.PipeNameOverrideForTest = prior; }
    }

    [Fact]
    public async Task UserStop_SealsPausedAndUnknownPlansEvenWhenExecutionCapabilitiesAreUnavailable()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var root = System.IO.Path.Combine(AssistantDataDirectory.Root, "stopped-plans-" + Guid.NewGuid().ToString("N"));
        var host = new TaskCenterHost(System.IO.Path.Combine(root, "flows"), System.IO.Path.Combine(root, "runs"),
            System.IO.Path.Combine(root, "catalog.json"), () => null, () => false, () => null);
        var ownerStore = new ArbitrationLeaseStore(System.IO.Path.Combine(root, "arbitration"));
        var acquired = ownerStore.TryAcquire("isolated-user-stop", 60);
        Assert.True(acquired.Success);
        host.Runs.BindOwner(acquired.Ownership!);
        var states = new[] { WorkflowRunState.Paused, WorkflowRunState.Unknown, WorkflowRunState.Interrupted, WorkflowRunState.Waiting };
        foreach (var state in states)
        {
            var run = host.Runs.CreateRun(state.ToString(), "rev");
            run.State = state;
            host.Runs.Update(run);
        }
        Assert.Empty(await host.StopAllRunsForUserAsync());
        var persisted = new RunStore(System.IO.Path.Combine(root, "runs")).List();
        Assert.Equal(states.Length, persisted.Count);
        Assert.All(persisted, run => Assert.True(run.StopRequested));
        Assert.Contains(persisted, run => run.State == WorkflowRunState.Unknown); // Keep the original uncertainty.
        await host.ShutdownAsync();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"running\":false}")]
    [InlineData("{\"running\":false,\"slotOccupied\":false,\"takeoverActive\":false,\"hasSuspendedTaskContext\":false,\"queueDepth\":1}")]
    [InlineData("{\"running\":false,\"slotOccupied\":false,\"takeoverActive\":true,\"hasSuspendedTaskContext\":false,\"queueDepth\":0}")]
    public void PartialOrBusySnapshot_IsNeverStopped(string json) => Assert.False(CommandExecutor.IsAllStopConfirmed(json));

    private static object Status(bool busy)
    {
        using var process = Process.GetCurrentProcess();
        return new { running = busy, slotOccupied = busy, takeoverActive = busy,
            hasSuspendedTaskContext = busy, queueDepth = busy ? 2 : 0,
            currentTaskHandle = busy ? "running-handle" : null,
            bgiEpoch = new { processId = process.Id, startTicksUtc = process.StartTime.ToUniversalTime().Ticks } };
    }

    private static Task<CommandResult> Stop(MainViewModel vm, RemoteCommand command) =>
        (Task<CommandResult>)typeof(MainViewModel).GetMethod("ExecuteCommandWithHoeingStopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [command])!;
    private static void Set(MainViewModel vm, string field, object value) => typeof(MainViewModel)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
}
