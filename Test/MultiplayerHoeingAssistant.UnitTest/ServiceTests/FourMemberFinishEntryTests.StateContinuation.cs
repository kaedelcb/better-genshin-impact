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
    // Called in the existing STA/Application round. The actual MainVM, SDK, gateway and
    // completion worker run unchanged; only the private BGI protocol endpoint is controlled.
    private static async Task RunStateContinuationCompletionEntryAsync(bool configurationRemoved = false)
    {
        string[] uids = ["100000031", "100000032", "100000033", "100000034"];
        const string bound = "隔离增强锄地绑定";
        const string completion = "隔离绑定收尾任务";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var server = await IsolatedCoordinator.StartAsync("retained-bound-completion");
        await using var pipe = new BgiInstancePipeDouble();
        var bgi = new FinishBgiPeer(bound, completion, stateContinuation: true)
        {
            CompletionSlotBusy = true, DropNextCompletionReply = true,
        };
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
        var peers = new[] { new ControlledPeer(second, uids[1], false),
            new ControlledPeer(third, uids[2], false), new ControlledPeer(tail, uids[3], true) };
        Task[] peerTasks = [];
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            Assert.True(external.HasCapability(BgiExternalClient.StateContinuationCapability));
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "小鲨鱼完成动作隔离入口",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-completion-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [bound], OnlineHoeingGroupTypes = ["group"],
                OnlineHoeingCompletionPolicy = "runSpecified", OnlineHoeingSpecifiedTaskType = "group",
                OnlineHoeingSpecifiedTaskName = completion, ExpectedHoeingPlayers = 4,
                DisclaimerAccepted = true, BypassSystemProxy = true,
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
            var room = "CONTINUATION-" + Guid.NewGuid().ToString("N")[..8];
            vm.RoomCode = room;
            local = await ConnectProductionClientAsync(vm);
            SignalRClient[] clients = [local, second, third, tail];
            for (var i = 0; i < clients.Length; i++)
            {
                if (i > 0)
                    await clients[i].ConnectAsync(server.Url, room, config.ControlRoomPassword, uids[i],
                        "增强隔离成员" + i, uids.ToList(), clientInstanceId: Guid.NewGuid().ToString("N"),
                        bypassSystemProxy: true);
                Assert.True(clients[i].SupportsStateContinuation);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "增强隔离成员" + i, BgiStatus = "running",
                    ConfigGroups = [bound, completion], OnlineHoeingGroupNames = [bound],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = i == 0 ? new("runSpecified", "group", completion, 0)
                        : new("stop", "group", "", 0),
                });
            }
            peerTasks = peers.Select(p => p.RunAsync(peersStop.Token)).ToArray();
            var beginMethod = typeof(MainViewModel).GetMethod("BeginCoordinatedWithContinuationAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var started = await (Task<CoordinatedBatchSnapshot>)beginMethod.Invoke(vm,
                [Guid.NewGuid().ToString("N"), uids, true, ""])!;
            Func<string> diagnostics = () => Diagnostics(vm, bgi, peers, server);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running"
                && peers.All(p => p.Last?.Phase == "running"), diagnostics);
            var batch = Active(vm)!;
            Assert.True(batch.StateContinuationEnabled);
            var preparedHost = (TaskCenterHost)typeof(MainViewModel)
                .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var preparedGate = (TaskCenterOnlineSendGate)typeof(TaskCenterHost)
                .GetField("_onlineSendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preparedHost)!;
            Assert.True(preparedGate.IsClosed);
            Assert.Equal(TaskConflictPolicy.RunSpecified, batch.Policy!.Policy);
            Assert.Equal(completion, batch.Policy.SpecifiedTaskName);

            bgi.CompleteCooperative();
            await WaitAsync(() => batch.LocalSucceeded && bgi.CompletionBusyStatusReads >= 2, diagnostics);
            Assert.Equal(0, bgi.ManualStarts);
            Assert.False(batch.IsCancellationRequested);
            Assert.Same(batch, Active(vm));

            // Only the actual BGI availability changes. No reset, second online event or manual start.
            bgi.CompletionConfigMissing = configurationRemoved;
            bgi.CompletionSlotBusy = false;
            if (configurationRemoved)
            {
                peers[2].CompleteTail = true;
                await WaitAsync(() => batch.PolicyRejectedBeforeStart && batch.LocalExecutionReleased && Active(vm) == null, diagnostics);
                var host = (TaskCenterHost)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                var gate = (TaskCenterOnlineSendGate)typeof(TaskCenterHost)
                    .GetField("_onlineSendGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                Assert.False(gate.IsClosed);
                using var permitWait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await using var permit = await gate.EnterAsync(permitWait.Token);
                Assert.Contains("配置不存在", batch.CompletionActionWarning);
                Assert.Equal(0, bgi.ManualStarts);
                Assert.Null(batch.PolicyExecution);
                Assert.Equal(0, bgi.CancelCount);
                return;
            }
            await WaitAsync(() => bgi.ManualStarts == 1 && bgi.CompletionReplyDrops == 1
                && batch.PolicyExecution is not null, diagnostics);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(bgi.ManualJobId, batch.PolicyExecution!.JobId);
            Assert.Equal(started.BatchId, batch.BatchId);
            var submitted = pipe.Received.Where(r => r.Operation == "ext.task.start" && r.Data is { } data
                && data.TryGetProperty("idempotencyKey", out var key) && key.GetString() == batch.PolicyRequestKey).ToArray();
            Assert.Single(submitted);
            Assert.Equal(completion, submitted[0].Data!.Value.GetProperty("groupName").GetString());
            Assert.True(submitted[0].Data!.Value.GetProperty("retainUntilAcknowledged").GetBoolean());

            // A real gateway failure of the unfinished peer and a delayed prior batch notification
            // must preserve this endpoint's actual running completion job.
            peers[2].ReportUnknown = true;
            await WaitAsync(() => peers[2].Last?.Phase == "aborted", diagnostics);
            var terminalTeam = (await local.GetCurrentCoordinatedBatchAsync())!;
            Replay(local, GatewayProtocol.Events.ControlBatchState, terminalTeam);
            await Task.Delay(1300);
            Assert.True(batch.LocalSucceeded);
            Assert.False(batch.IsCancellationRequested);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(1, bgi.CooperativeStarts);

            bgi.CompleteManual();
            await WaitAsync(() => batch.PolicyTerminalResult?.Status == "completed"
                && batch.PolicyResultAcknowledged && batch.LocalExecutionReleased, diagnostics);
            Assert.Equal(1, bgi.CompletionAcknowledgements);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
            Assert.Equal(1, bgi.ManualStarts);
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
