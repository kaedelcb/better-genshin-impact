using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// Four real SignalR connections and the production local MainVM/CommandExecutor entry.
/// Only BGI is replaced by a private named-pipe protocol peer; no game/user process is operated.
/// Run alone in a fresh testhost with a unique explicit NEXUSBGI_DATA_ROOT.
/// </summary>
[Collection("TaskCenterPipeTransport")]
public sealed partial class FourMemberFinishEntryTests
{
    [Fact]
    public async Task CompletedLocalMemberCanStartManualTaskWhilePeerTailRuns_AndLateTeamFinishCannotStopIt()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "隔离入口必须显式设置 NEXUSBGI_DATA_ROOT。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        // WPF permits one Application per testhost. Both independent four-member
                        // scenarios use this STA, each with its own server, room and private pipe.
                        await RunEntryAsync(peerFails: true, reconnectAfterLocalFinish: false);
                        await RunEntryAsync(peerFails: false, reconnectAfterLocalFinish: true);
                        await RunLocalTaskFailureEntryAsync();
                        await RunStateContinuationCompletionEntryAsync();
                        await RunStateContinuationCompletionEntryAsync(configurationRemoved: true);
                        await RunChangedRoomCancellationEntryAsync(replyLost: false);
                        await RunChangedRoomCancellationEntryAsync(replyLost: true);
                        finished.TrySetResult();
                    }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "FourMemberFinishEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离 Dispatcher 未退出。");
    }

    private static async Task RunEntryAsync(bool peerFails, bool reconnectAfterLocalFinish)
    {
        Directory.CreateDirectory(AssistantDataDirectory.Root);
        string[] uids = ["100000011", "100000012", "100000013", "100000014"];
        const string boundGroup = "隔离联机主体和后置";
        const string manualGroup = "隔离完成后的手动任务";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        var caseName = peerFails ? "peer-abort" : "reconnect-peer-completed";
        await using var server = await IsolatedCoordinator.StartAsync(caseName);
        await using var pipe = new BgiInstancePipeDouble();
        var bgi = new FinishBgiPeer(boundGroup, manualGroup);
        pipe.BodyForTest = bgi.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        SignalRClient? originalLocal = null;
        SignalRClient local = null!;
        SignalRClient? reconnected = null;
        await using var second = new SignalRClient();
        await using var third = new SignalRClient();
        await using var tail = new SignalRClient();
        using var peersStop = new CancellationTokenSource();
        var peers = new[] { new ControlledPeer(second, uids[1], false),
            new ControlledPeer(third, uids[2], false), new ControlledPeer(tail, uids[3], true) };
        Task[] peerTasks = [];
        MainViewModel? vm = null;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "隔离本机完成端",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-finish-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [boundGroup], OnlineHoeingGroupTypes = ["group"],
                ExpectedHoeingPlayers = 4, DisclaimerAccepted = true, BypassSystemProxy = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external, logger: vm.AddLog,
                isBatchInFlight: () => vm.HasLocalCoordinatedExecutionResponsibility);
            executor.TaskStartBgiPresenceProbeOverride = () => true; // The private pipe peer is the fixture's running BGI.
            executor.TaskStartBgiLaunchOverride = () => throw new InvalidOperationException("隔离验收禁止启动真实或额外进程");
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("隔离验收禁止停止真实进程");
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            var room = "FINISH-" + Guid.NewGuid().ToString("N")[..8];
            vm.RoomCode = room;
            originalLocal = local = await ConnectProductionClientAsync(vm);
            SignalRClient[] clients = [local, second, third, tail];
            for (var i = 0; i < clients.Length; i++)
            {
                if (i > 0)
                    await clients[i].ConnectAsync(server.Url, room, config.ControlRoomPassword,
                        uids[i], "隔离成员" + i, uids.ToList(),
                        clientInstanceId: Guid.NewGuid().ToString("N"), bypassSystemProxy: true);
                Assert.True(clients[i].SupportsCoordinatedStart);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "隔离成员" + i, BgiStatus = "running",
                    ConfigGroups = [boundGroup, manualGroup], OnlineHoeingGroupNames = [boundGroup],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
            }
            peerTasks = peers.Select(p => p.RunAsync(peersStop.Token)).ToArray();
            var started = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: false);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running"
                && peers.All(p => p.Last?.Phase == "running"), () => Diagnostics(vm, bgi, peers, server));
            var oldBatch = Active(vm)!;
            var cooperativeJob = bgi.CooperativeJobId!;
            Assert.True(oldBatch.TakenOver);

            if (reconnectAfterLocalFinish)
            {
                // Real production Refresh while the original BGI job is still running:
                // pending join must not end/recreate the loop or submit the same group twice.
                var originalLoop = oldBatch.RunTask;
                var participantToken = oldBatch.Token;
                var originalTicket = oldBatch.TakeoverTicket;
                var previousClient = local;
                await ((Task)Call(vm, "RefreshAsync")!).WaitAsync(TimeSpan.FromSeconds(30));
                local = CurrentClient(vm);
                reconnected = local;
                Assert.NotSame(previousClient, local);
                Assert.True(local.IsControlRoomReady);
                Assert.Same(oldBatch, Active(vm));
                Assert.Same(originalLoop, oldBatch.RunTask);
                Assert.False(originalLoop!.IsCompleted);
                Assert.Equal(participantToken, oldBatch.Token);
                Assert.Equal(originalTicket, oldBatch.TakeoverTicket);
                Assert.Equal(cooperativeJob, bgi.CooperativeJobId);
                Assert.Equal(1, bgi.CooperativeStarts);
                Assert.Equal(0, bgi.CancelCount);
            }

            bgi.CompleteCooperative();
            await WaitAsync(() => oldBatch.LocalExecutionReleased && executor.TakeoverTicketSnapshot == null,
                () => Diagnostics(vm, bgi, peers, server));
            // Local release is not a server ACK. Wait for a real peer response containing our result.
            await WaitAsync(() => peers.Any(p => p.Last?.Members.Any(m => m.Uid == uids[0]
                    && m.Result == "succeeded") == true), () => Diagnostics(vm, bgi, peers, server));
            var waiting = await local.GetCurrentCoordinatedBatchAsync();
            Assert.Equal("running", waiting!.Phase);
            Assert.Equal("succeeded", waiting.Members.Single(m => m.Uid == uids[0]).Result);
            Assert.Equal("", waiting.Members.Single(m => m.Uid == uids[3]).Result);
            Assert.True(oldBatch.LocalSucceeded);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.Null(external.TakeoverTicket);
            var resumesBeforeManual = pipe.CountOf("task.resume");
            Assert.Equal(1, resumesBeforeManual);

            // The real remote-command dispatcher, not a direct fake-start call, starts the new job.
            await second.SendRemoteCommandAsync(new RemoteCommand
            {
                Cmd = "start_group", Target = [uids[0]], CommandId = Guid.NewGuid().ToString("N"),
                Params = new() { ["groupName"] = manualGroup },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            });
            await WaitAsync(() => bgi.ManualStarts == 1, () => Diagnostics(vm, bgi, peers, server));
            var manualJob = bgi.ManualJobId!;
            Assert.NotEqual(cooperativeJob, manualJob);
            Assert.True(bgi.ManualRunning);
            Assert.True(oldBatch.LocalExecutionReleased);

            if (reconnectAfterLocalFinish)
            {
                // The same loop now survives the old 30s contact budget and adopts the reconnected client.
                var originalLoop = oldBatch.RunTask;
                await local.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(31));
                Assert.False(originalLoop!.IsCompleted);
                Assert.True(oldBatch.LocalSucceeded);
                Assert.True(oldBatch.LocalExecutionReleased);
                Assert.True(bgi.ManualRunning);
                reconnected = await ConnectProductionClientAsync(vm);
                local = reconnected;
                await local.ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[0], PlayerName = config.PlayerName, BgiStatus = "running",
                    ConfigGroups = [boundGroup, manualGroup], OnlineHoeingGroupNames = [boundGroup],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
                var restored = await local.GetCurrentCoordinatedBatchAsync();
                Assert.Equal(started.BatchId, restored!.BatchId);
                Assert.Equal("running", restored.Phase);
                Assert.Same(oldBatch, Active(vm));
                Assert.Same(originalLoop, oldBatch.RunTask);
                Assert.False(oldBatch.RunTask!.IsCompleted);
                Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            }

            // The real server either aborts on the unfinished peer's error or completes normally.
            var ackGate = (SemaphoreSlim)typeof(MainViewModel).GetField("_coordinatedCleanupAckGate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            if (reconnectAfterLocalFinish) await ackGate.WaitAsync();
            var terminalPhase = peerFails ? "aborted" : "completed";
            try
            {
                peers[2].ReportUnknown = peerFails;
                peers[2].CompleteTail = !peerFails;
                if (reconnectAfterLocalFinish)
                {
                    // The only completed notification arrives while a preceding cleanup owns the gate.
                    // It must remain queued, not be dropped with a zero-time semaphore wait.
                    await WaitAsync(() => peers[2].Last?.Phase == terminalPhase,
                        () => Diagnostics(vm, bgi, peers, server));
                    Assert.Same(oldBatch, Active(vm));
                }
            }
            finally { if (reconnectAfterLocalFinish) ackGate.Release(); }
            await WaitAsync(() => peers[2].Last?.Phase == terminalPhase && Active(vm) == null,
                () => Diagnostics(vm, bgi, peers, server));
            var terminal = (await local.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal(started.BatchId, terminal.BatchId);
            Assert.Equal(terminalPhase, terminal.Phase);
            Assert.True(oldBatch.LocalSucceeded);
            Assert.Equal("succeeded", oldBatch.Outcome);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(0, bgi.CancelCount);

            // Cleanup ACK over the real connection, plus replay of already received old envelopes.
            await peers[2].SendDelayedCleanupAsync(terminal);
            Replay(local, GatewayProtocol.Events.ControlBatchState, terminal);
            Replay(local, GatewayProtocol.Events.ControlBatchState, terminal with { CleanupComplete = true });
            Replay(local, GatewayProtocol.Events.ControlBatchStart, started);
            await Task.Delay(1300);
            Assert.Null(Active(vm));
            Assert.Equal(manualJob, bgi.ManualJobId);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(1, bgi.CooperativeStarts);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
            Assert.Equal(resumesBeforeManual, pipe.CountOf("task.resume"));

            // End only the private protocol job so its production waiter also finishes cleanly.
            bgi.CompleteManual();
            await Task.Delay(300);
        }
        finally
        {
            peersStop.Cancel();
            try { await Task.WhenAll(peerTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (vm != null)
            {
                Active(vm)?.Cancel();
                if (Active(vm)?.RunTask is { } pending)
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(45)); } catch { }
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                Set(vm, "_disposing", true);
                StopFixtureTimers(vm);
                if (host != null) await host.ShutdownAsync();
            }
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
            if (reconnected != null) await reconnected.DisposeAsync();
            if (originalLocal != null) await originalLocal.DisposeAsync();
        }
    }

    private static async Task RunLocalTaskFailureEntryAsync()
    {
        string[] uids = ["100000021", "100000022", "100000023", "100000024"];
        const string group = "隔离联机及两个神像";
        const string next = "普通失败后继续第二联机项";
        const string manual = "普通失败后仍执行的绑定收尾";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var server = await IsolatedCoordinator.StartAsync("local-task-failure-isolation");
        await using var pipe = new BgiInstancePipeDouble();
        var bgi = new FinishBgiPeer(group, manual, next);
        pipe.BodyForTest = bgi.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        SignalRClient? originalLocal = null;
        SignalRClient local = null!;
        SignalRClient? reconnected = null;
        await using var zeroTailClient = new SignalRClient();
        await using var failedClient = new SignalRClient();
        await using var waitingClient = new SignalRClient();
        using var stopPeers = new CancellationTokenSource();
        var zeroTail = new ControlledPeer(zeroTailClient, uids[1], false);
        var waitingTail = new ControlledPeer(waitingClient, uids[3], true);
        var failedPeer = new LocalFailureLoopPeer(failedClient, uids[2]);
        Task[] peerTasks = [];
        MainViewModel? vm = null;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "两个普通后置任务的生产端",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-local-failure",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [group, next], OnlineHoeingGroupTypes = ["group", "group"],
                OnlineHoeingCompletionPolicy = "runSpecified", OnlineHoeingSpecifiedTaskType = "group",
                OnlineHoeingSpecifiedTaskName = manual,
                ExpectedHoeingPlayers = 4, DisclaimerAccepted = true, BypassSystemProxy = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external, logger: vm.AddLog,
                isBatchInFlight: () => vm.HasLocalCoordinatedExecutionResponsibility);
            executor.TaskStartBgiPresenceProbeOverride = () => true; // The private pipe peer is the fixture's running BGI.
            executor.TaskStartBgiLaunchOverride = () => throw new InvalidOperationException("隔离验收禁止启动真实或额外进程");
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("禁止触碰真实进程");
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            var room = "LOCAL-FAIL-" + Guid.NewGuid().ToString("N")[..8];
            vm.RoomCode = room;
            originalLocal = local = await ConnectProductionClientAsync(vm);
            SignalRClient[] clients = [local, zeroTailClient, failedClient, waitingClient];
            for (var i = 0; i < clients.Length; i++)
            {
                if (i > 0)
                    await clients[i].ConnectAsync(server.Url, room, config.ControlRoomPassword,
                        uids[i], "隔离不同后置成员" + i, uids.ToList(),
                        clientInstanceId: Guid.NewGuid().ToString("N"), bypassSystemProxy: true);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "隔离不同后置成员" + i, BgiStatus = "running",
                    ConfigGroups = [group, next, manual], OnlineHoeingGroupNames = [group, next],
                    OnlineHoeingGroupTypes = ["group", "group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = i == 0 ? new("runSpecified", "group", manual, 0) : new("stop", "group", "", 0),
                });
            }
            peerTasks = [zeroTail.RunAsync(stopPeers.Token), failedPeer.RunAsync(stopPeers.Token),
                waitingTail.RunAsync(stopPeers.Token)];
            Func<string> diagnostics = () => Diagnostics(vm, bgi, [zeroTail, waitingTail], server)
                + "\nproduction-loop-peer=" + failedPeer.Last?.Phase + "/" + failedPeer.Error;
            var started = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: true);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running"
                && failedPeer.SubmitCount == 1, diagnostics);
            var ownBatch = Active(vm)!;

            // Finished native work must not be replayed when an ordinary tail step fails.
            bgi.CompleteCooperativeBody();
            await WaitAsync(() => pipe.Received.Count(r => r.Operation == "ext.task.queueStatus") > 1, diagnostics);
            Assert.True(bgi.CooperativeTailRunning);
            Assert.False(ownBatch.LocalCompletionReady);
            failedPeer.FailTail = true;
            await WaitAsync(() => failedPeer.Last?.Members.Single(member => member.Uid == uids[2]).Result
                == "local_tasks_failed", diagnostics);
            Assert.Equal(0, (await local.GetCurrentCoordinatedBatchAsync())!.Index);
            Assert.Equal(0, bgi.CancelCount);
            bgi.CompleteTailStep();
            Assert.True(bgi.CooperativeTailRunning);

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bgi.BeforeFailedJobIdentityReply = () =>
            {
                originalLocal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                disconnected.TrySetResult();
            };
            bgi.CompleteTailStep(failed: true);
            await WaitAsync(() => disconnected.Task.IsCompleted && ownBatch.LocalTasksFailed, diagnostics);
            Assert.False(ownBatch.RunTask!.IsCompleted);
            Assert.False(ownBatch.LocalSucceeded);
            Assert.Equal(1, bgi.CooperativeStarts);
            Assert.Equal(0, bgi.ManualStarts);

            reconnected = await ConnectProductionClientAsync(vm);
            local = reconnected;
            await local.ReportControlStatusAsync(new ControlStatus
            {
                PlayerUid = uids[0], PlayerName = config.PlayerName, BgiStatus = "running",
                ConfigGroups = [group, next, manual], OnlineHoeingGroupNames = [group, next],
                OnlineHoeingGroupTypes = ["group", "group"], ExpectedHoeingPlayers = 4,
                HoeingCompletionAction = new("runSpecified", "group", manual, 0),
            });
            await WaitAsync(() => ownBatch.LocalTasksFailureAccepted, diagnostics);
            var firstFinished = (await local.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal("running", firstFinished.Phase);
            Assert.Equal(0, firstFinished.Index); // The final peer still owns its real tail.
            Assert.Equal("local_tasks_failed", firstFinished.Members.Single(member => member.Uid == uids[0]).Result);
            waitingTail.CompleteTail = true;
            await WaitAsync(() => bgi.CooperativeStarts == 2 && failedPeer.SubmitCount == 2, diagnostics);
            Assert.Equal(0, bgi.ManualStarts);
            bgi.CompleteCooperative();
            await WaitAsync(() => bgi.ManualStarts == 1 && ownBatch.PolicyExecution is not null, diagnostics);
            Assert.True(ownBatch.LocalCompletionReady);
            Assert.False(ownBatch.LocalSucceeded); // Do not relabel the ordinary failure as success.
            Assert.Equal("completed_with_errors", ownBatch.Outcome);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(0, failedPeer.CancelCount);
            // Local completion starts before the next control heartbeat reports its terminal.
            await WaitAsync(() => waitingTail.Last?.Phase == "completed", diagnostics);
            var completed = (await local.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal("completed", completed.Phase);
            Assert.Equal(2, completed.Index);
            Assert.Contains(uids[0], completed.Reason);
            Assert.Contains(uids[2], completed.Reason);
            var firstResult = await external.QueryTaskQueueStatusAsync(bgi.CooperativeJobId!);
            Assert.Equal("failed", firstResult!.Status);
            Assert.Equal(1, firstResult.HoeingEvidence!.Completed);
            bgi.CompleteManual();
            await WaitAsync(() => Active(vm) == null && ownBatch.LocalExecutionReleased, diagnostics);
            Assert.Equal(2, bgi.CooperativeStarts);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(1, bgi.CompletionAcknowledgements);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
            foreach (var name in new[] { group, next, manual })
                Assert.Single(pipe.Received.Where(request => request.Operation == "ext.task.start"
                    && request.Data is { } data && data.TryGetProperty("groupName", out var value) && value.GetString() == name));
        }

        finally
        {
            stopPeers.Cancel();
            try { await Task.WhenAll(peerTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (vm != null)
            {
                Active(vm)?.Cancel();
                if (Active(vm)?.RunTask is { } pending)
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(45)); } catch { }
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                Set(vm, "_disposing", true);
                StopFixtureTimers(vm);
                if (host != null) await host.ShutdownAsync();
            }
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
            if (reconnected != null) await reconnected.DisposeAsync();
            if (originalLocal != null) await originalLocal.DisposeAsync();
        }
    }

    /// <summary>Real batch loop and real gateway transport; only its BGI observation/action IO is controlled.</summary>
    private sealed class LocalFailureLoopPeer(SignalRClient client, string uid)
    {
        private readonly BgiEpoch _epoch = new() { ProcessId = 8, StartTicksUtc = 80 };
        private string? _jobId, _requestKey;
        public volatile bool FailTail;
        public int SubmitCount, CancelCount;
        public CoordinatedBatchSnapshot? Last { get; private set; }
        public string? Error { get; private set; }
        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                CoordinatedBatchSnapshot? start = null;
                while (start == null)
                { start = await client.GetCurrentCoordinatedBatchAsync(ct); if (start == null) await Task.Delay(100, ct); }
                var batch = new OnlineHoeingBatch(start, uid, new TaskConflictPolicySettings());
                using var cancellation = ct.Register(() => batch.Cancel());
                var loop = new CoordinatedBatchLoop(batch, new CoordinatedBatchRuntime
                {
                    Prepare = _ => Task.CompletedTask,
                    Takeover = _ => { batch.TakeoverTicket = "owned-test-loop-" + uid; return Task.CompletedTask; },
                    Exchange = async (stage, result, reason, index, attempt, token) => Last =
                        await client.UpdateCoordinatedBatchAsync(start.Generation, batch.Token, start.BatchId,
                            "group,group", index, attempt, result, token, stage, reason),
                    ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new()
                    {
                        Epoch = _epoch, Jobs = _jobId == null ? [] : [new BgiJobInfo
                        {
                            JobId = _jobId, IdempotencyKey = _requestKey, Epoch = _epoch,
                            State = FailTail ? "failed" : "running", ExecutionExitConfirmed = FailTail,
                            ExecutionExitDisposition = FailTail ? "terminal" : null,
                        }]
                    }),
                    ReadTerminal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(new()
                    {
                        Status = FailTail ? "failed" : "running", ErrorCode = FailTail ? "task_start_failed" : null,
                        TaskHandle = _jobId, IdempotencyKey = _requestKey, Epoch = _epoch, ExecutionExitConfirmed = FailTail,
                        HoeingEvidence = new() { Started = 1, Completed = 1, Active = 0, Sealed = FailTail,
                            JobResult = FailTail ? "Failed" : null, Revision = FailTail ? 4 : 3 },
                    }),
                    Submit = (item, _, _) =>
                    {
                        SubmitCount++; _jobId = Guid.NewGuid().ToString("N"); _requestKey = item.RequestKey;
                        return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = _jobId });
                    },
                    ConfirmLocalTasksFailure = (id, epoch, key, _) => Task.FromResult<bool?>(FailTail
                        && Guid.TryParse(id, out var requested) && Guid.TryParse(_jobId, out var actual) && requested == actual
                        && CoordinatedBatchLoop.SameEpoch(epoch, _epoch) && key == _requestKey),
                    CancelOwned = (_, _) => { CancelCount++; return Task.CompletedTask; },
                    ReleaseTicket = (_, _, _) => Task.FromResult(true),
                    Acknowledge = (_, _, _, _) => Task.FromResult(true),
                    RequireFinishEvidence = true, RequireLocalTaskFailureIsolation = true,
                    PollInterval = TimeSpan.FromMilliseconds(100),
                });
                await loop.RunAsync();
                while (!ct.IsCancellationRequested)
                {
                    var current = await client.GetCurrentCoordinatedBatchAsync(ct);
                    if (current?.Phase is "completed" or "aborted")
                    {
                        Last = await client.UpdateCoordinatedBatchAsync(start.Generation, batch.Token, start.BatchId,
                            "group,group", current.Index, current.Attempt, "", ct, "cleaned");
                        return;
                    }
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { Error = error.ToString(); throw; }
        }
    }

    private sealed class ControlledPeer(SignalRClient client, string uid, bool hasLongTail)
    {
        private readonly string _token = Guid.NewGuid().ToString("N");
        public volatile bool ReportUnknown;
        public volatile bool CompleteTail;
        public CoordinatedBatchSnapshot? Last { get; private set; }
        public string? Error { get; private set; }
        public async Task RunAsync(CancellationToken ct)
        {
            string? batchId = null;
            var registered = false;
            var stage = "registered";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var current = await client.GetCurrentCoordinatedBatchAsync(ct);
                    if (current != null)
                    {
                        if (batchId != current.BatchId) { batchId = current.BatchId; registered = false; stage = "registered"; }
                        var result = "";
                        if (registered)
                        {
                            if (current.Phase == "preparing") stage = "prepared";
                            if (current.Phase == "takeover") stage = "takeoverConfirmed";
                            if (current.Phase == "running") result = ReportUnknown ? "local_failed"
                                : hasLongTail && !CompleteTail ? "" : "succeeded";
                            if (current.Phase is "completed" or "aborted") stage = "cleaned";
                        }
                        Last = await client.UpdateCoordinatedBatchAsync(current.Generation, _token, current.BatchId,
                            string.Join(",", current.Bindings[uid].Select(b => b.Type)),
                            current.Index, current.Attempt, result, ct, stage,
                            ReportUnknown ? "controlled_peer_after_body_failed" : "");
                        registered = true;
                    }
                    await Task.Delay(150, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { Error = uid + ":" + error; throw; }
        }
        public Task<CoordinatedBatchSnapshot> SendDelayedCleanupAsync(CoordinatedBatchSnapshot snapshot)
            => client.UpdateCoordinatedBatchAsync(snapshot.Generation, _token, snapshot.BatchId,
                string.Join(",", snapshot.Bindings[uid].Select(b => b.Type)),
                snapshot.Index, snapshot.Attempt, "", CancellationToken.None, "cleaned");
    }

    private sealed class FinishBgiPeer(string boundGroup, string manualGroup, string? nextBoundGroup = null, bool stateContinuation = true)
    {
        private sealed record Job(string Id, string Key, string Name, bool Cooperative)
        {
            public bool Completed, Cancelled, Failed, BodyCompleted, NativeFailed;
            public int TailSteps, Revision = 1;
        }
        private readonly object _gate = new();
        private readonly List<Job> _jobs = [];
        private string? _ticket;
        private int _cancelCount;
        public volatile bool CompletionSlotBusy, DropNextCompletionReply, CompletionConfigMissing;
        public int CompletionBusyStatusReads, CompletionReplyDrops, CompletionAcknowledgements;
        private readonly int _pid = Environment.ProcessId;
        private readonly long _ticks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        public Action? BeforeFailedJobIdentityReply;
        private object Epoch => new { processId = _pid, startTicksUtc = _ticks };
        public int CooperativeStarts { get { lock (_gate) return _jobs.Count(j => j.Cooperative); } }
        public int ManualStarts { get { lock (_gate) return _jobs.Count(j => !j.Cooperative); } }
        public string? CooperativeJobId { get { lock (_gate) return _jobs.FirstOrDefault(j => j.Cooperative)?.Id; } }
        public string? ManualJobId { get { lock (_gate) return _jobs.FirstOrDefault(j => !j.Cooperative)?.Id; } }
        public bool ManualRunning { get { lock (_gate) return _jobs.Any(j => !j.Cooperative && !j.Completed && !j.Cancelled && !j.Failed); } }
        public bool CooperativeTailRunning { get { lock (_gate) return _jobs.Any(j => j.Cooperative && j.BodyCompleted && !j.Completed && !j.Cancelled && !j.Failed); } }
        public int TailSteps { get { lock (_gate) return _jobs.FirstOrDefault(j => j.Cooperative)?.TailSteps ?? 0; } }
        public int CancelCount { get { lock (_gate) return _cancelCount; } }
        public void CompleteCooperative()
        {
            lock (_gate)
            {
                var job = _jobs.Last(j => j.Cooperative);
                job.BodyCompleted = job.Completed = true;
                job.Revision++;
            }
        }
        public void CompleteCooperativeBody()
        { lock (_gate) { var job = _jobs.Last(j => j.Cooperative); job.BodyCompleted = true; job.Revision++; } }
        public void CompleteTailStep(bool failed = false)
        {
            lock (_gate)
            {
                var job = _jobs.Last(j => j.Cooperative);
                Assert.True(job.BodyCompleted && !job.Completed && !job.Cancelled && !job.Failed);
                job.TailSteps++;
                job.Failed = failed;
                job.Revision++;
            }
        }
        public void FailCooperativeNative()
        {
            lock (_gate)
            {
                var job = _jobs.Last(j => j.Cooperative);
                job.Failed = job.NativeFailed = true;
                job.BodyCompleted = false;
                job.Revision++;
            }
        }
        public void CompleteManual() { lock (_gate) _jobs.Single(j => !j.Cooperative).Completed = true; }
        private static string? Value(JsonElement? data, string name) => data is { } d && d.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public (bool, string?, string?, object?)? Reply(string operation, JsonElement? data)
        {
            lock (_gate)
            {
                var active = _jobs.LastOrDefault(j => !j.Completed && !j.Cancelled && !j.Failed);
                switch (operation)
                {
                    case "ext.hello":
                        return (true, null, null, new
                        {
                            bgiVersion = "isolated-four-member-finish", sessionId = "private-pipe-only",
                            windowsSessionId = Process.GetCurrentProcess().SessionId, processId = _pid, bgiEpoch = Epoch,
                            capabilities = new Dictionary<string, bool>
                            {
                                ["event.push"] = true, ["task.queue"] = true, ["task.takeover"] = true,
                                ["hoeing.batchOutcome.v1"] = true, ["hoeing.coordinatedStart.v1"] = true,
                                ["hoeing.finishEvidence.v1"] = true,
                                ["task.stateContinuation.v1"] = stateContinuation,
                                ["execution.targetedSuspend.v1"] = true, ["config.applied"] = true,
                            }
                        });
                    case "task.status":
                    case "ext.task.status":
                        var completionBusy = stateContinuation && CompletionSlotBusy && _jobs.Any(j => j.Cooperative && j.Completed);
                        if (completionBusy) CompletionBusyStatusReads++;
                        var queriedRoot = Value(data, "executionInstanceId");
                        var exited = _jobs.Any(j => j.Id == queriedRoot && (j.Completed || j.Cancelled || j.Failed));
                        return (true, null, null, new
                        {
                            running = active != null, taskRunning = active != null,
                            autoHoeingRunning = active?.Cooperative == true,
                            autoHoeingMultiplayer = active?.Cooperative == true ? (bool?)true : null,
                            slotOccupied = active != null || completionBusy, executionSlotOccupied = active != null || completionBusy,
                            executionIdentityAvailable = active != null, executionInstanceId = active?.Id,
                            executionJobId = active?.Id, executionStateRevision = 1, stateRevision = 1,
                            executionIdle = active == null, executionExitConfirmed = exited,
                            executionExitQueryInstanceId = queriedRoot, executionExitAtUtc = DateTime.UtcNow,
                            executionExitOrder = 1L, executionExitReason = exited ? "confirmed" : active == null ? "no_active_root" : "not_exited",
                            bgiEpoch = Epoch, takeoverActive = _ticket != null, hasSuspendedTaskContext = false,
                            queueDepth = 0, currentTaskHandle = active?.Id, stopVersion = 0L,
                        });
                    case "config.list":
                    case "ext.config.list":
                        return (true, null, null, new { configGroups = new[] { boundGroup, manualGroup, nextBoundGroup }
                            .Where(n => n != null && (!CompletionConfigMissing || n != manualGroup)).ToArray(), oneClickConfigs = Array.Empty<string>() });
                    case "ext.config.describe":
                        return (true, null, null, new { bgiEpoch = Epoch, configRevision = "finish-entry-r1",
                            tasks = nextBoundGroup != null && Value(data, "groupName") == boundGroup
                                ? new[] { new { name = "联机native", enabled = true }, new { name = "回神像1", enabled = true }, new { name = "回神像2", enabled = true } }
                                : new[] { new { name = "隔离协议任务", enabled = true } } });
                    case "ext.job.list":
                        return (true, null, null, new { bgiEpoch = Epoch, jobs = _jobs.Select(j => new
                        {
                            jobId = j.Id, idempotencyKey = j.Key, name = j.Name, kind = "group",
                            state = j.Cancelled ? "cancelled" : j.Failed ? "failed" : j.Completed ? "succeeded" : "running", wasCancelled = j.Cancelled,
                            exitConfirmed = j.Completed || j.Cancelled || j.Failed, exitDisposition = j.Completed || j.Cancelled || j.Failed ? "terminal" : null,
                        }).ToArray() });
                    case "ext.job.status":
                        var exactJob = _jobs.SingleOrDefault(j => Guid.TryParse(j.Id, out var known)
                            && Guid.TryParse(Value(data, "jobId"), out var requested) && known == requested);
                        if (exactJob is { Cooperative: true, Failed: true, BodyCompleted: true, NativeFailed: false })
                            Interlocked.Exchange(ref BeforeFailedJobIdentityReply, null)?.Invoke();
                        return (true, null, null, new { status = exactJob == null ? "not_found"
                                : exactJob.Cancelled ? "cancelled" : exactJob.Failed ? "failed" : exactJob.Completed ? "succeeded" : "running", bgiEpoch = Epoch,
                            job = exactJob == null ? null : new
                            {
                                jobId = exactJob.Id, idempotencyKey = exactJob.Key, name = exactJob.Name, kind = "group",
                                state = exactJob.Cancelled ? "cancelled" : exactJob.Failed ? "failed" : exactJob.Completed ? "succeeded" : "running",
                                wasCancelled = exactJob.Cancelled, exitConfirmed = exactJob.Completed || exactJob.Cancelled || exactJob.Failed,
                                exitDisposition = exactJob.Completed || exactJob.Cancelled || exactJob.Failed ? "terminal" : null,
                            } });
                    case "task.suspend":
                        if (active != null || Value(data, "takeoverTicket") is not { } ticket)
                            return (false, "not_current", "替身只接受空闲接管", null);
                        _ticket = ticket;
                        return (true, null, null, new { takeoverTicket = ticket, quiesceConfirmed = true,
                            liveTask = false, expectedExecutionInstanceId = (string?)null, expectedStateRevision = (long?)null });
                    case "ext.task.start":
                        var cooperative = Value(data, "coordinatedHoeing") == "v1";
                        if (stateContinuation && Value(data, "idempotencyKey") is { } key
                            && _jobs.SingleOrDefault(j => j.Key == key) is { } adopted)
                            return (true, null, null, new { status = "adopted", taskHandle = adopted.Id, queuePosition = 0 });
                        if (active != null || cooperative && (_ticket == null || Value(data, "takeoverTicket") != _ticket)
                            || !cooperative && _ticket != null)
                            return (false, "task_busy", "新任务不能借用旧票据或覆盖活动任务", null);
                        var job = new Job(Guid.NewGuid().ToString("N"), Value(data, "idempotencyKey")!,
                            Value(data, "groupName")!, cooperative);
                        _jobs.Add(job);
                        if (stateContinuation && !cooperative && DropNextCompletionReply)
                        {
                            DropNextCompletionReply = false;
                            CompletionReplyDrops++;
                            // The original task is already accepted. Close this private connection before
                            // its reply is written, then let the production client reconnect and query it.
                            throw new IOException("Fixture drops the accepted completion reply");
                        }
                        return (true, null, null, new { status = "queued", taskHandle = job.Id, queuePosition = 0 });
                    case "ext.task.queueStatus":
                        var queryKey = stateContinuation ? Value(data, "idempotencyKey") : null;
                        var queried = _jobs.SingleOrDefault(j => queryKey is not null ? j.Key == queryKey : j.Id == Value(data, "taskHandle"));
                        if (queried is null && stateContinuation)
                            return (true, null, null, new { status = "not_found", taskHandle = (string?)null,
                                idempotencyKey = queryKey, bgiEpoch = Epoch, executionExitConfirmed = false });
                        return queried == null ? (false, "not_found", "未知句柄", null) : (true, null, null, new
                        {
                            taskHandle = queried.Id, idempotencyKey = queried.Key, bgiEpoch = Epoch,
                            executionExitConfirmed = queried.Completed || queried.Cancelled || queried.Failed,
                            status = queried.Failed ? "failed" : queried.Completed || queried.Cancelled ? "completed" : "running",
                            cancelled = queried.Cancelled, errorCode = queried.Cancelled ? "user_cancelled" : queried.Failed ? "task_start_failed" : null,
                            hoeingEvidence = queried.Cooperative ? new
                            {
                                plannedNativeCount = 1, started = 1, completed = queried.BodyCompleted ? 1 : 0, failed = queried.NativeFailed ? 1 : 0,
                                cancelled = queried.Cancelled && !queried.BodyCompleted ? 1 : 0, active = queried.BodyCompleted || queried.Completed || queried.Cancelled || queried.Failed ? 0 : 1,
                                @sealed = queried.Completed || queried.Cancelled || queried.Failed,
                                jobResult = queried.Completed ? "Ran" : queried.Cancelled ? "Cancelled" : queried.Failed ? "Failed" : null, revision = queried.Revision,
                            } : null,
                        });
                    case "ext.task.acknowledge" when stateContinuation:
                        var acknowledged = _jobs.SingleOrDefault(j => j.Key == Value(data, "submissionKey")
                            && Guid.TryParse(j.Id, out var knownHandle)
                            && Guid.TryParse(Value(data, "taskHandle"), out var requestedHandle) && knownHandle == requestedHandle);
                        if (acknowledged is null || !(acknowledged.Completed || acknowledged.Cancelled || acknowledged.Failed))
                            return (false, "result_not_terminal", "替身结果尚未完成，不能确认", null);
                        if (!acknowledged.Cooperative) CompletionAcknowledgements++;
                        return (true, null, null, new { status = "acknowledged" });
                    case "ext.task.cancel":
                        var owned = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        if (owned == null || Value(data, "ownedOnly") != "v1") return (false, "invalid_request", "只允许本句柄取消", null);
                        if (!owned.Cancelled) { owned.Cancelled = true; _cancelCount++; }
                        return (true, null, null, new { status = "cancel_requested" });
                    case "task.resume":
                        if (active != null || _ticket == null || Value(data, "takeoverTicket") != _ticket)
                            return (false, "stale_ticket", "替身收尾条件不满足", null);
                        _ticket = null;
                        return (true, null, null, new { status = "cleared_not_resumed" });
                    case "ext.task.stop":
                        return (false, "invalid_request", "本验收禁止全停止副作用", null);
                    default: return null;
                }
            }
        }
    }

    private static async Task<SignalRClient> ConnectProductionClientAsync(MainViewModel vm)
    {
        var client = (SignalRClient)Call(vm, "CreateWiredSignalRClient")!;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await (Task)Call(vm, "ConnectSignalRWithConfigAsync", client, budget.Token,
                "隔离四成员生产建连入口", false)!;
            Assert.Same(client, CurrentClient(vm));
            Assert.True(client.IsControlRoomReady);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    private static SignalRClient CurrentClient(MainViewModel vm) =>
        (SignalRClient)typeof(MainViewModel).GetField("_signalRClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static void StopFixtureTimers(MainViewModel vm)
    {
        Call(vm, "StopSignalRRecovery");
        foreach (var field in new[] { "_statusTimer", "_retryTimer", "_onlineTimer", "_resumeTimeoutTimer" })
            (typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm) as IDisposable)?.Dispose();
    }

    private static OnlineHoeingBatch? Active(MainViewModel vm) =>
        (OnlineHoeingBatch?)typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
    private static void Set(MainViewModel vm, string field, object value) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    private static object? Call(MainViewModel vm, string method, params object[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);
    private static void Replay(SignalRClient client, string name, CoordinatedBatchSnapshot snapshot) =>
        typeof(SignalRClient).GetMethod("DispatchEvt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client,
            [new GatewayEnvelope { Type = "event", Name = name, Payload = GatewayEnvelope.ToPayload(snapshot) }]);
    private static async Task WaitAsync(Func<bool> predicate, Func<string> diagnostics)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(80);
        Assert.True(predicate(), diagnostics());
    }
    private static string Diagnostics(MainViewModel vm, FinishBgiPeer bgi, ControlledPeer[] peers, IsolatedCoordinator server)
        => $"local={Active(vm)?.BatchId}/{Active(vm)?.Phase}/{Active(vm)?.Message}; jobs={bgi.CooperativeStarts}/{bgi.ManualStarts}; cancels={bgi.CancelCount}\n"
            + string.Join("\n", peers.Select(p => p.Last?.Phase + "/" + p.Error)) + "\n"
            + string.Join("\n", vm.CommandLogs.Take(30)) + "\nserver=" + server.Output;

    private sealed class IsolatedCoordinator : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly string _caseName;
        public string Url { get; }
        public string Output => string.Join("\n", _output.TakeLast(35));
        private IsolatedCoordinator(Process process, string url, string caseName)
        { _process = process; Url = url; _caseName = caseName; }
        public static async Task<IsolatedCoordinator> StartAsync(string caseName)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "BgiCoordinatorServer", "BgiCoordinatorServer.csproj"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var serverDll = Environment.GetEnvironmentVariable("NEXUSBGI_TEST_COORDINATOR_DLL")
                ?? Path.Combine(repo!.FullName, "BgiCoordinatorServer", "bin", "Debug", "net8.0", "BgiCoordinatorServer.dll");
            Assert.True(File.Exists(serverDll), "先使用 DeployToBgiTools=false 构建本次源码的 Debug server。");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var url = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.Combine(repo.FullName, "BgiCoordinatorServer")
            };
            start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
            start.Environment["ROOM_LOG_ROOT"] = Path.Combine(Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")!, "four-member-finish-" + caseName + "-room-logs");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var owned = new IsolatedCoordinator(process, url, caseName);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) owned._output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) owned._output.Enqueue(e.Data); };
            Assert.True(process.Start());
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    Assert.False(process.HasExited, owned.Output);
                    try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) return owned; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(150);
                }
                throw new TimeoutException("隔离服务器未就绪：" + owned.Output);
            }
            catch { await owned.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: false); await _process.WaitForExitAsync(); }
            try
            {
                Directory.CreateDirectory(AssistantDataDirectory.Root);
                await File.WriteAllTextAsync(Path.Combine(AssistantDataDirectory.Root, "four-member-finish-" + _caseName + "-coordinator.log"), string.Join("\n", _output));
            }
            catch (Exception error)
            {
                // Diagnostic persistence must never replace the original entry failure.
                Debug.WriteLine("隔离服务器日志保存失败：" + error);
            }
            _process.Dispose();
        }
    }
}
