using System.IO;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed partial class FourMemberFinishEntryTests
{
    private static async Task RunMidRunStopAndNewIntentEntryAsync()
    {
        string[] uids = ["100000061", "100000062", "100000063", "100000064"];
        const string bound = "隔离中途停止联机";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var server = await IsolatedCoordinator.StartAsync("mid-stop");
        await using var pipe = new BgiInstancePipeDouble();
        using var bgi = new FinishBgiPeer(bound, "隔离未启动后续") { SimulateManualStopFence = true };
        pipe.BodyForTest = bgi.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        await using var second = new SignalRClient();
        await using var third = new SignalRClient();
        await using var tail = new SignalRClient();
        SignalRClient? local = null;
        MainViewModel? vm = null;
        using var peersStop = new CancellationTokenSource();
        var peers = new[] { new ControlledPeer(second, uids[1], false, priorityRecovery: true),
            new ControlledPeer(third, uids[2], false, priorityRecovery: true),
            new ControlledPeer(tail, uids[3], false, priorityRecovery: true) };
        Task[] peerTasks = [];
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "隔离中途停止端",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-mid-stop",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [bound], OnlineHoeingGroupTypes = ["group"],
                ExpectedHoeingPlayers = 4, DisclaimerAccepted = true, BypassSystemProxy = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external, logger: vm.AddLog,
                isBatchInFlight: () => vm.HasLocalCoordinatedExecutionResponsibility);
            executor.TaskStartBgiPresenceProbeOverride = () => true;
            executor.TaskStartBgiLaunchOverride = () => throw new InvalidOperationException("禁止启动真实进程");
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("禁止停止真实进程");
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            vm.RoomCode = "MID-" + Guid.NewGuid().ToString("N")[..8];
            local = await ConnectProductionClientAsync(vm);
            SignalRClient[] clients = [local, second, third, tail];
            for (var i = 0; i < clients.Length; i++)
            {
                if (i > 0)
                    await clients[i].ConnectAsync(server.Url, vm.RoomCode, config.ControlRoomPassword,
                        uids[i], "隔离成员" + i, uids.ToList(),
                        clientInstanceId: Guid.NewGuid().ToString("N"), bypassSystemProxy: true);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "隔离成员" + i, BgiStatus = "running",
                    ConfigGroups = [bound], OnlineHoeingGroupNames = [bound],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
            }
            peerTasks = peers.Select(p => p.RunAsync(peersStop.Token)).ToArray();
            var started = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: false);
            Func<string> diagnostics = () => Diagnostics(vm, bgi, peers, server);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running", diagnostics);
            var stoppedBatch = Active(vm)!;
            Assert.True(stoppedBatch.PriorityRecoveryEnabled);
            var originalTicket = stoppedBatch.TakeoverTicket;
            Assert.NotNull(originalTicket);
            var originalJob = bgi.CooperativeJobId!;
            Assert.Equal(0L, stoppedBatch.StateContinuationStopVersion);
            using var stoppedSignal = Mistletoe.Shared.CoordinatedStopSignal.Open(stoppedBatch.RecoveryStopSignal!.Name)!;
            Assert.NotNull(stoppedSignal);
            var firstSuspend = pipe.Received.Last(request => request.Operation == "task.suspend");
            Assert.Equal(0L, firstSuspend.Data!.Value.GetProperty("expectedStopVersion").GetInt64());

            bgi.ManualStopCooperative(); // Real BGI contract: cancel world job, revoke ticket, advance fence and stop shared intent.
            await WaitAsync(() => stoppedBatch.RunTask!.IsCompleted && Active(vm) is null, diagnostics);
            Assert.True(stoppedBatch.IsCancellationRequested);
            Assert.True(stoppedBatch.LocalCleanupConfirmed);
            Assert.False(stoppedBatch.IsAlive);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.True(stoppedSignal.IsStopped);
            Assert.Equal(1L, bgi.ManualStopVersion);
            Assert.True(bgi.StaleResumeReplies > 0);
            Assert.Equal(1, bgi.RevokedStatusBusyReads); // The internal stale-response check failed once, so the outer cleanup fallback is exercised.
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.Null(external.TakeoverTicket);
            var stoppedState = (await local.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal(started.BatchId, stoppedState.BatchId);
            Assert.Equal("aborted", stoppedState.Phase);
            Replay(local, GatewayProtocol.Events.ControlBatchStart, started);
            Replay(local, GatewayProtocol.Events.ControlBatchState, stoppedState);
            Assert.Null(Active(vm));
            Assert.Equal(1, bgi.CooperativeStarts);

            // Local cleanup is separate from the other members' real server acknowledgements.
            await WaitAsync(() => peers.Any(peer => peer.Last is { CleanupComplete: true } state
                && state.BatchId == started.BatchId), diagnostics);
            Assert.True((await local.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);

            // A fresh explicit request receives its own current fence and ticket; the old intent stays stopped.
            var next = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: false);
            Assert.NotEqual(started.BatchId, next.BatchId);
            await WaitAsync(() => bgi.CooperativeStarts == 2 && Active(vm)?.BatchId == next.BatchId
                && Active(vm)?.Phase == "running", diagnostics);
            var nextBatch = Active(vm)!;
            Assert.NotSame(stoppedBatch, nextBatch);
            Assert.Equal(1L, nextBatch.StateContinuationStopVersion);
            Assert.NotNull(nextBatch.TakeoverTicket);
            Assert.NotEqual(originalTicket, nextBatch.TakeoverTicket);
            Assert.Equal(nextBatch.TakeoverTicket, executor.TakeoverTicketSnapshot);
            var nextSuspend = pipe.Received.Last(request => request.Operation == "task.suspend");
            Assert.Equal(1L, nextSuspend.Data!.Value.GetProperty("expectedStopVersion").GetInt64());
            Assert.Equal(nextBatch.TakeoverTicket, nextSuspend.Data.Value.GetProperty("takeoverTicket").GetString());
            var nextSubmit = pipe.Received.Last(request => request.Operation == "ext.task.start");
            Assert.Equal(1L, nextSubmit.Data!.Value.GetProperty("expectedStopVersion").GetInt64());
            Assert.True(stoppedSignal.IsStopped);
            Assert.False(nextBatch.RecoveryStopSignal!.IsStopped);
            Assert.NotEqual(stoppedSignal.Name, nextBatch.RecoveryStopSignal.Name);
            var (_, oldJob) = await external.QueryJobStatusAsync(originalJob);
            Assert.Equal("cancelled", oldJob!.State);
            Assert.True(oldJob.ExecutionExitConfirmed);
            Replay(local, GatewayProtocol.Events.ControlBatchStart, started);
            Replay(local, GatewayProtocol.Events.ControlBatchState, stoppedState);
            Assert.Same(nextBatch, Active(vm));
            Assert.False(nextBatch.IsCancellationRequested);
            Assert.Equal(2, bgi.CooperativeStarts);
            bgi.CompleteCooperative();
            await WaitAsync(() => nextBatch.RunTask!.IsCompleted && Active(vm) is null, diagnostics);
            Assert.True(nextBatch.LocalSucceeded);
            Assert.True(nextBatch.LocalCleanupConfirmed);
            Assert.Equal(2, bgi.CooperativeStarts);
            Assert.Equal(0, bgi.ManualStarts);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
        }
        finally
        {
            peersStop.Cancel();
            try { await Task.WhenAll(peerTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (vm is not null)
            {
                Active(vm)?.Cancel();
                if (Active(vm)?.RunTask is { } pending)
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(45)); } catch { }
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                Set(vm, "_disposing", true);
                StopFixtureTimers(vm);
                if (host is not null) await host.ShutdownAsync();
            }
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
            if (local is not null) await local.DisposeAsync();
        }
    }
}
