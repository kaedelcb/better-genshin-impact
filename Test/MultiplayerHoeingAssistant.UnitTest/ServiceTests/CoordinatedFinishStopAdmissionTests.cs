using System.Diagnostics;
using System.IO;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class CoordinatedFinishStopAdmissionTests
{
    [Fact]
    public async Task AllStopBlocksEveryManualStartUntilPhysicalStopFinishesEvenAfterLocalHoeingRelease()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        await using var pipe = new BgiInstancePipeDouble();
        using var allowStopReply = new ManualResetEventSlim();
        var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldIpc = IpcClient.PipeNameOverrideForTest;
        var oldExt = BgiExternalClient.PipeNameOverrideForTest;
        pipe.AcceptTaskStart = false;
        pipe.AcceptV2TaskStart = false;
        pipe.BodyForTest = (operation, data) =>
        {
            if (operation == "ext.task.stop")
            {
                Assert.Equal("all", data!.Value.GetProperty("stopScope").GetString());
                stopReceived.TrySetResult();
                Assert.True(allowStopReply.Wait(TimeSpan.FromSeconds(8)), "The isolated stop reply was never released.");
                return (true, null, null, new { status = "stop_requested" });
            }
            if (operation is "task.status" or "ext.task.status") return (true, null, null, Idle());
            if (operation == "action.execute_hotkey") return (false, "fixture_no_hotkey", "No real hotkey is executed.", null);
            return null;
        };
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var ext = new BgiExternalClient();
        // The path does not exist. Even an unexpected start-bgi path cannot launch an application.
        var nonexistentProgram = Path.Combine(AssistantDataDirectory.Root, "never-created-" + Guid.NewGuid().ToString("N"), "BetterGI.exe");
        Assert.False(File.Exists(nonexistentProgram));
        using var monitor = new BgiProcessMonitor(nonexistentProgram);
        Task<CommandResult>? stopping = null;
        OnlineHoeingBatch? batch = null;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await ext.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var vm = new MainViewModel();
            var executor = new CommandExecutor(monitor, nonexistentProgram, () => ext,
                isBatchInFlight: () => vm.HasLocalCoordinatedExecutionResponsibility)
            {
                TaskStartBgiPresenceProbeOverride = () => true,
                TaskStartBgiLaunchOverride = () => throw new InvalidOperationException("Launching a real process is forbidden."),
                StopProcessOverrideForTest = _ => throw new InvalidOperationException("Killing a real process is forbidden.")
            };
            batch = new(new CoordinatedBatchSnapshot("released-awaiting-peer", 29, 0, 0, 1, "running", "")
            {
                Protocol = "hoeing.start.v2", Source = "manual", ParticipantUids = ["self", "peer"],
                Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                {
                    ["self"] = [new("锄地", "group")], ["peer"] = [new("后置脚本", "group")]
                }
            }, "self", new());
            using (var process = Process.GetCurrentProcess())
                batch.MarkLocalSuccess(new() { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks });
            batch.MarkLocalExecutionReleased();
            Set(vm, "_activeBatch", batch);
            Set(vm, "_commandExecutor", executor);
            Set(vm, "_config", new AssistConfig { PlayerUid = "self", BgiPath = nonexistentProgram });
            Set(vm, "_reportStatusRunning", 1);
            Assert.True(batch.IsAlive);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);

            stopping = Execute(vm, new()
            {
                Cmd = "stop", CommandId = Guid.NewGuid().ToString("N"),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1), Params = new() { ["stopScope"] = "all" }
            });
            await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stopping.IsCompleted);
            foreach (var commandName in new[] { "start_group", "start_oneclick", "start_bgi", "hotkey_execute" })
            {
                var throughVm = await Execute(vm, Start(commandName)).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal("stop_in_progress", throughVm.ErrorCode);
                var throughExecutor = await executor.ExecuteAsync(Start(commandName)).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal("stop_in_progress", throughExecutor.ErrorCode);
            }
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
            Assert.Equal(0, pipe.CountOf("action.execute_hotkey"));
            Assert.Equal(0, pipe.CountOf("task.suspend"));

            allowStopReply.Set();
            Assert.Equal("success", (await stopping.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            var vmAfterStop = await Execute(vm, new() { Cmd = "start_group", Params = new() { ["groupName"] = "" } })
                .WaitAsync(TimeSpan.FromSeconds(3));
            var executorAfterStop = await executor.ExecuteAsync(new() { Cmd = "start_group", Params = new() { ["groupName"] = "" } })
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual("stop_in_progress", vmAfterStop.ErrorCode);
            Assert.NotEqual("stop_in_progress", executorAfterStop.ErrorCode);
            Assert.NotEqual("success", vmAfterStop.Status);
            Assert.NotEqual("success", executorAfterStop.Status);
            Assert.Equal(1, pipe.CountOf("ext.task.stop"));
            Assert.Equal(0, pipe.CountOf("action.execute_hotkey"));
        }
        finally
        {
            allowStopReply.Set();
            if (stopping is not null)
                try { await stopping.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            batch?.MarkSettled();
            IpcClient.PipeNameOverrideForTest = oldIpc;
            BgiExternalClient.PipeNameOverrideForTest = oldExt;
        }
    }

    private static RemoteCommand Start(string name) => new()
    {
        Cmd = name, Params = new()
        {
            ["groupName"] = "isolated-group", ["configName"] = "isolated-one-dragon", ["hotkeyConfigName"] = "isolated-hotkey"
        }
    };

    private static object Idle()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            running = false, slotOccupied = false, takeoverActive = false, hasSuspendedTaskContext = false,
            queueDepth = 0, currentTaskHandle = (string?)null,
            bgiEpoch = new { processId = process.Id, startTicksUtc = process.StartTime.ToUniversalTime().Ticks }
        };
    }
    private static Task<CommandResult> Execute(MainViewModel vm, RemoteCommand command) =>
        (Task<CommandResult>)typeof(MainViewModel).GetMethod("ExecuteCommandWithHoeingStopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [command])!;
    private static void Set(MainViewModel vm, string name, object value) => typeof(MainViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
}
