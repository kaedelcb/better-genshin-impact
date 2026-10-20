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
    // Same production legacy entry as the reported four-member completion -> one-dragon -> F11 cancellation.
    private static async Task RunLegacyCompletionCancellationEntryAsync()
    {
        string[] uids = ["100000051", "100000052", "100000053", "100000054"];
        const string bound = "隔离联机取消主体";
        const string completion = "隔离精英取消收尾";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var server = await IsolatedCoordinator.StartAsync("legacy-cancel");
        await using var pipe = new BgiInstancePipeDouble();
        using var bgi = new FinishBgiPeer(bound, completion, stateContinuation: false, manualIsOneDragon: true);
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
            new ControlledPeer(third, uids[2], false), new ControlledPeer(tail, uids[3], false) };
        Task[] peerTasks = [];
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "隔离取消收尾端",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-cancel-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [bound], OnlineHoeingGroupTypes = ["group"],
                OnlineHoeingCompletionPolicy = "runSpecified", OnlineHoeingSpecifiedTaskType = "onedragon",
                OnlineHoeingSpecifiedTaskName = completion, ExpectedHoeingPlayers = 4,
                DisclaimerAccepted = true, BypassSystemProxy = true,
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
            vm.RoomCode = "CANCEL-" + Guid.NewGuid().ToString("N")[..8];
            local = await ConnectProductionClientAsync(vm);
            // Project only this connection's start negotiation to the old server contract.
            // The original MainVM event wiring still receives every identity, phase, member and result unchanged.
            var startEvent = typeof(SignalRClient).GetField(nameof(SignalRClient.OnHoeingBatchStart),
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalStart = (Action<CoordinatedBatchSnapshot>)startEvent.GetValue(local)!;
            Assert.NotNull(originalStart);
            startEvent.SetValue(local, (Action<CoordinatedBatchSnapshot>)(snapshot => originalStart(
                snapshot with { StateContinuation = false, PriorityRecovery = false })));
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
                    HoeingCompletionAction = i == 0 ? new("runSpecified", "onedragon", completion, 0)
                        : new("stop", "group", "", 0),
                });
            }
            peerTasks = peers.Select(p => p.RunAsync(peersStop.Token)).ToArray();
            var started = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: true);
            Func<string> diagnostics = () => Diagnostics(vm, bgi, peers, server);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running", diagnostics);
            var batch = Active(vm)!;
            Assert.False(batch.Start!.StateContinuation);
            Assert.False(batch.StateContinuationEnabled);
            bgi.CompleteCooperative();
            await WaitAsync(() => bgi.ManualStarts == 1 && batch.PolicyExecution?.JobId == bgi.ManualJobId, diagnostics);
            var completionJob = bgi.ManualJobId!;
            var manualSubmit = Assert.Single(pipe.Received.Where(request => request.Operation == "ext.task.start"
                && request.Data is { } data && data.TryGetProperty("configName", out var name)
                && name.GetString() == completion));
            Assert.NotNull(manualSubmit.Data);
            Assert.True(batch.LocalSucceeded);
            Assert.False(batch.LocalExecutionReleased);

            // The private BGI has stopped the exact action, but initially cannot supply its terminal result.
            bgi.ManualTerminalUnavailable = true;
            bgi.CancelManual();
            await WaitAsync(() => bgi.ManualUnknownStatusReads > 0, diagnostics);
            Assert.Same(batch, Active(vm));
            Assert.True(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.False(batch.LocalExecutionReleased);
            Assert.False(batch.RunTask!.IsCompleted);
            var startsBeforeScheduled = pipe.CountOf("ext.task.start");
            var suspendsBeforeScheduled = pipe.CountOf("task.suspend");
            var resumesBeforeScheduled = pipe.CountOf("task.resume");
            var cooperativeBeforeScheduled = bgi.CooperativeStarts;
            var manualBeforeScheduled = bgi.ManualStarts;
            Assert.False(await (Task<bool>)Call(vm, "MarkOnlineAsync", "scheduled")!);
            Assert.Same(batch, Active(vm));
            Assert.Equal(startsBeforeScheduled, pipe.CountOf("ext.task.start"));
            Assert.Equal(suspendsBeforeScheduled, pipe.CountOf("task.suspend"));
            Assert.Equal(resumesBeforeScheduled, pipe.CountOf("task.resume"));
            Assert.Equal(cooperativeBeforeScheduled, bgi.CooperativeStarts);
            Assert.Equal(manualBeforeScheduled, bgi.ManualStarts);
            bgi.ManualTerminalUnavailable = false;

            await WaitAsync(() => Active(vm) is null && batch.RunTask!.IsCompleted, diagnostics);
            Assert.True(batch.LocalSucceeded);
            Assert.Equal("succeeded", batch.Outcome);
            Assert.True(batch.LocalExecutionReleased);
            Assert.True(batch.LocalCleanupConfirmed);
            Assert.False(batch.IsAlive);
            Assert.False(batch.IsCancellationRequested);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.Contains("完成后指定任务已取消", batch.CompletionActionWarning);
            Assert.DoesNotContain("收尾待确认", batch.Message);
            Assert.Null(executor.TakeoverTicketSnapshot);
            var host = (TaskCenterHost)typeof(MainViewModel)
                .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var store = (ArbitrationLeaseStore)typeof(TaskCenterHost)
                .GetField("_admissionStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var handoff = store.Read().File!.Handoff!;
            var cancelledOperation = Assert.Single(handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation))
                .Where(operation => Guid.TryParse(operation.ExecutionResult?.JobId, out var known)
                    && Guid.TryParse(completionJob, out var expected) && known == expected));
            Assert.Equal(ExecutionResultKind.Cancelled, cancelledOperation.ExecutionResult!.Kind);
            Assert.Equal(OperationRequestState.TerminalCompleted, cancelledOperation.RequestState);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));

            // A new actual gateway request must start after the settled cancelled action, without resetting old records.
            var next = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: false);
            Assert.NotEqual(started.BatchId, next.BatchId);
            await WaitAsync(() => bgi.CooperativeStarts == 2 && Active(vm)?.BatchId == next.BatchId, diagnostics);
            bgi.CompleteCooperative();
            await WaitAsync(() => Active(vm) is null, diagnostics);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(ExecutionResultKind.Cancelled, cancelledOperation.ExecutionResult.Kind);
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
