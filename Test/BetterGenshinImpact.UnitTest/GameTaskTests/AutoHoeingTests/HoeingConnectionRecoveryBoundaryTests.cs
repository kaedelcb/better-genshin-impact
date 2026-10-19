#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>2026-10-19 11:43 strict_2 故障：驱动真实等待、动作边界、路线并发收口与房间恢复。</summary>
public sealed class HoeingConnectionRecoveryBoundaryTests
{
    private static CoordinatorClient Client() => new() { _testIsConnectedOverride = true };
    private static MultiplayerCoordinator Coordinator(CoordinatorClient client, CancellationTokenSource? stop = null)
        => new(client, new SyncPointResolver(), new AutoHoeingConfig()) { StopCts = stop };
    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    { Type = GatewayProtocol.MessageTypes.Response, Name = request.Name, Payload = GatewayEnvelope.ToPayload(payload) };
    private static void Arrive(CoordinatorClient client, string id) => client.DispatchEvt(new()
    {
        Type = GatewayProtocol.MessageTypes.Event, Name = GatewayProtocol.Events.SyncAllArrived,
        // 到达请求字段是 syncId；服务端 AllArrived 事件合同字段是 syncPointId。
        Payload = GatewayEnvelope.ToPayload(new { syncPointId = id })
    });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AcceptRoom(CoordinatorClient client)
    {
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = true }));
    }

    [Fact]
    public async Task Strict2CancellationCannotReachActionStrict3OrNextRoute()
    {
        await using var client = Client();
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testSendOverride = (_, request, _) =>
        {
            sent.Add(request.GetString("syncId"));
            stop.Cancel();
            return Task.CompletedTask;
        };
        var actions = 0;
        var nextRoute = false;
        async Task OriginalSequence()
        {
            await coordinator.WaitForAllPlayers("strict_2", stop.Token);
            await PathExecutor.RunMultiplayerActionAsync(coordinator, false,
                () => { actions++; return Task.CompletedTask; }, stop.Token);
            await coordinator.WaitForAllPlayers("strict_3", stop.Token);
            nextRoute = true;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(OriginalSequence);
        Assert.Equal(new[] { "strict_2" }, sent);
        Assert.Equal(0, actions);
        Assert.False(nextRoute);
    }

    [Fact]
    public async Task SharedActionSwallowingCancellationCannotStartNextWaypoint()
    {
        await using var client = Client();
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var nextWaypoint = false;
        async Task Sequence()
        {
            await PathExecutor.RunMultiplayerActionAsync(coordinator, false, () =>
            {
                stop.Cancel();
                try { stop.Token.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { } // 原共享脚本合同，联机返回边界须拦截。
                return Task.CompletedTask;
            }, stop.Token);
            nextWaypoint = true;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Sequence);
        Assert.False(nextWaypoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAndRetrySendUseTheSameCancelableTotalBudget(bool retry)
    {
        await using var client = Client();
        client._testStrictWaitBudget = TimeSpan.FromMilliseconds(500);
        client._testWaitRetryIntervalMs = 1;
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var sends = 0;
        var sendCancelled = false;
        client.GetOrCreateGatewayForTest()._testSendOverride = async (_, _, token) =>
        {
            Assert.True(token.CanBeCanceled);
            if (retry && ++sends == 1) return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { sendCancelled = token.IsCancellationRequested; }
        };
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() =>
            coordinator.WaitForAllPlayers("strict_2", stop.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(sendCancelled);
        Assert.True(stop.IsCancellationRequested);
        Assert.True(coordinator.IsExitTriggered);
        Assert.Equal(retry ? 2 : 0, sends);
    }

    [Fact]
    public async Task AllArrivedAndStopInSameSendCannotGrantAnAction()
    {
        await using var client = Client();
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        client.GetOrCreateGatewayForTest()._testSendOverride = (_, request, _) =>
        {
            Arrive(client, request.GetString("syncId"));
            stop.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.WaitForAllPlayers("strict_2", stop.Token));
    }

    [Fact]
    public async Task FourMembersReleaseOnlyAfterMatchingAllArrived()
    {
        var clients = Enumerable.Range(0, 4).Select(_ => Client()).ToArray();
        var coordinators = clients.Select(c => Coordinator(c)).ToArray();
        try
        {
            var arrivals = 0;
            foreach (var client in clients)
                client.GetOrCreateGatewayForTest()._testSendOverride = (_, request, _) =>
                {
                    if (++arrivals == 4)
                        foreach (var member in clients) Arrive(member, request.GetString("syncId"));
                    return Task.CompletedTask;
                };
            var waits = coordinators.Take(3).Select(c => c.WaitForAllPlayers("tp", CancellationToken.None)).ToList();
            Assert.All(waits, w => Assert.False(w.IsCompleted));
            foreach (var member in clients) Arrive(member, "wrong-id");
            Assert.All(waits, w => Assert.False(w.IsCompleted));
            waits.Add(coordinators[3].WaitForAllPlayers("tp", CancellationToken.None));
            Assert.All(await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(3)), result => Assert.True(result));
        }
        finally
        {
            foreach (var c in coordinators) await c.DisposeAsync();
            foreach (var c in clients) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task HelloAndJoinSuccessStillDoNotReplaceAllArrived()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old-room", "name", "uid"));
        await using var coordinator = Coordinator(client);
        var sent = Signal();
        client.GetOrCreateGatewayForTest()._testSendOverride = (_, _, _) =>
        { sent.TrySetResult(true); return Task.CompletedTask; };
        var waiting = coordinator.WaitForAllPlayers("strict_2", CancellationToken.None);
        await sent.Task;
        await client.OnReconnecting(null);
        var ready = client.WaitForBusinessReadyAsync(CancellationToken.None);
        Assert.False(ready.IsCompleted);
        await client.OnReconnected("new-connection");
        await ready;
        Assert.False(waiting.IsCompleted);
        Arrive(client, "strict_2");
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task JoinFalseTerminatesSessionInsteadOfLoggingFakeRecovery()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = false }));
        await client.OnReconnecting(null);
        await client.OnReconnected("new-connection");
        Assert.False(client.IsInRoom);
        Assert.True(coordinator.IsExitTriggered);
        Assert.True(stop.IsCancellationRequested);
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() =>
            client.WaitForBusinessReadyAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("batch-original")]
    [InlineData(null)]
    public async Task RecoveryUsesFrozenBatchRoomAndUidInsteadOfSuccessorAmbientScope(string? batch)
    {
        await using var client = Client();
        AcceptRoom(client);
        using (var original = new CoordinatedHoeingScope(batch != null, batch))
            Assert.True(await client.JoinRoomAsync("original-room", "original-name", "original-uid"));
        GatewayEnvelope? recovered = null;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin) recovered = request;
            return Task.FromResult(Response(request, new { success = true }));
        };
        using (var successor = new CoordinatedHoeingScope(true, "successor-batch"))
        {
            await client.OnReconnecting(null);
            await client.OnReconnected("new-connection");
        }
        Assert.NotNull(recovered);
        Assert.Equal("original-room", recovered!.GetString("roomCode"));
        Assert.Equal("original-uid", recovered.GetString("playerUid"));
        Assert.Equal(batch ?? "", recovered.GetString("hoeingBatchId"));
    }

    [Fact]
    public async Task LateOldRecoveryCannotReopenOrReplaceNormalNewWorldEpoch()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old-room", "name", "uid"));
        var oldEpoch = client.CurrentRoomEpoch;
        var helloEntered = Signal();
        var helloResponse = new TaskCompletionSource<GatewayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello)
            { helloEntered.TrySetResult(true); return helloResponse.Task; }
            return Task.FromResult(Response(request, new { success = true }));
        };
        await client.OnReconnecting(null);
        var oldCallback = client.OnReconnected("old-callback");
        await helloEntered.Task;
        var leaving = client.LeaveRoomAsync();
        var joining = client.JoinRoomAsync("new-room", "name", "uid");
        Assert.False(joining.IsCompleted); // 同一房间窄门：旧传输调用未退，不能并发换身份。
        helloResponse.SetResult(new GatewayEnvelope
        { Type = GatewayProtocol.MessageTypes.Response, Name = GatewayProtocol.Names.SessionHello });
        await oldCallback;
        await leaving;
        Assert.True(await joining);
        var newEpoch = client.CurrentRoomEpoch;
        Assert.NotEqual(oldEpoch, newEpoch);
        await client.WaitForBusinessReadyAsync(CancellationToken.None);
        Assert.True(client.IsInRoom);
        Assert.Equal(newEpoch, client.CurrentRoomEpoch);
        client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event, Name = GatewayProtocol.Events.RoomClosed, RoomCode = "old-room",
            Payload = GatewayEnvelope.ToPayload(new { reason = "late-old-close" })
        });
        await client.WaitForBusinessReadyAsync(CancellationToken.None);
        Assert.True(client.IsInRoom);
    }

    [Fact]
    public async Task StopSealsRoomBeforeBoundedNotificationAndLateJoinCannotReopenIt()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var joinEntered = Signal();
        var joinResponse = new TaskCompletionSource<GatewayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        GatewayEnvelope? joining = null;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            { joining = request; joinEntered.TrySetResult(true); return await joinResponse.Task; }
            if (request.Name == GatewayProtocol.Names.SyncReportCoordinatedAbort)
            {
                Assert.True(stop.IsCancellationRequested);
                Assert.False(client.IsInRoom);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(request, new { success = true });
        };
        client._testAuxiliaryBudget = TimeSpan.FromMilliseconds(50);
        await client.OnReconnecting(null);
        var recovery = client.OnReconnected("new-connection");
        await joinEntered.Task;
        var notification = coordinator.TriggerCoordinatedStop(false, "test-stop");
        Assert.True(stop.IsCancellationRequested);
        joinResponse.SetResult(Response(joining!, new { success = true }));
        await recovery;
        await notification.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(client.IsInRoom);
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() =>
            client.WaitForBusinessReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AuxiliaryFightingReportIsCancelableAndDoesNotTriggerRecovery()
    {
        await using var client = Client();
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var entered = Signal();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        { entered.TrySetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(request, new { }); };
        var reporting = coordinator.ReportFightingStatusAsync(false, stop.Token);
        await entered.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(client.IsReconnecting);
        Assert.False(coordinator.IsExitTriggered);
    }

    [Fact]
    public async Task ReplayBypassesTraditionalRoomGateAndSoloKeepsExistingActionContract()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        client.EndRoomSession("normal-world-change");
        await using var coordinator = Coordinator(client);
        var actions = 0;
        await PathExecutor.RunMultiplayerActionAsync(coordinator, true,
            () => { actions++; return Task.CompletedTask; }, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await PathExecutor.RunMultiplayerActionAsync(null, false,
            () => { actions++; return Task.CompletedTask; }, cancelled.Token);
        Assert.Equal(2, actions);
    }

    [Fact]
    public async Task RouteJoinPreservesSessionFailureAndCannotAdvanceToNextRoute()
    {
        await using var client = Client();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var coordinator = Coordinator(client);
        var failure = new MultiplayerSessionFailedException("strict_2 unconfirmed");
        var nextRoute = false;
        async Task Sequence()
        {
            await RouteExecutionEngine.AwaitRouteTasksAsync(new[]
            { Task.FromCanceled(cancelled.Token), Task.FromException(failure) }, coordinator, () => failure, cancelled.Token);
            nextRoute = true;
        }
        var observed = await Assert.ThrowsAsync<MultiplayerSessionFailedException>(Sequence);
        Assert.Same(failure, observed);
        Assert.False(nextRoute);
        // 相同子任务下，单机继续保持既有吞取消/错误口径。
        await RouteExecutionEngine.AwaitRouteTasksAsync(new[] { Task.FromCanceled(cancelled.Token) }, null,
            () => null, cancelled.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendedExplicitJoinOrCreateCannotPublishRoomAfterTaskStop(bool create)
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old-room", "name", "uid"));
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        // 捕获原任务许可。测试停止先发生于显式请求等待回包，而非正常下一轮的新 coordinator。
        var entered = Signal();
        var response = new TaskCompletionSource<GatewayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        GatewayEnvelope? pending = null;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        { pending = request; entered.TrySetResult(true); return response.Task; };
        Task operation = create
            ? client.CreateRoomAsync("name", null, "uid", 4, stop.Token)
            : client.JoinRoomAsync("new-room", "name", "uid", stop.Token);
        await entered.Task;
        // 外部取消先撤销当前请求；随后模拟服务端成功与取消同时到达。
        stop.Cancel();
        response.SetResult(Response(pending!, new { success = true, roomCode = "new-room" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(client.IsInRoom);
        Assert.Null(client.CurrentRoomEpoch);
        Assert.Null(await client.CreateRoomAsync("name", null, "uid", 4));
        Assert.False(client.IsInRoom);
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() =>
            client.WaitForBusinessReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TaskStopRevokesFutureExplicitCreateEvenWithoutCallerCancellationToken()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        Assert.True(coordinator.RequestLocalStop("user-stop"));
        var sent = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        { sent++; return Task.FromResult(Response(request, new { success = true, roomCode = "forbidden" })); };
        Assert.Null(await client.CreateRoomAsync("name", null, "uid", 4));
        Assert.False(await client.JoinRoomAsync("forbidden", "name", "uid"));
        Assert.Equal(0, sent);
        Assert.False(client.IsInRoom);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldCloseAndNewJoinShareOneMutationGate(bool closeFirst)
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old-room", "name", "uid"));
        var oldEpoch = client.CurrentRoomEpoch;
        var entered = Signal();
        var release = Signal();
        var calls = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            calls.Add(request.Name);
            var isFirst = closeFirst ? request.Name == GatewayProtocol.Names.RoomClose
                : request.Name == GatewayProtocol.Names.RoomJoin;
            if (isFirst)
            { entered.TrySetResult(true); await release.Task.WaitAsync(token); }
            return Response(request, new { success = true });
        };
        Task first = closeFirst ? client.CloseRoomAsync(oldEpoch) : client.JoinRoomAsync("new-room", "name", "uid");
        await entered.Task;
        Task second = closeFirst ? client.JoinRoomAsync("new-room", "name", "uid") : client.CloseRoomAsync(oldEpoch);
        Assert.Single(calls); // 后来的身份变更尚不能进入服务器。
        release.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(client.IsInRoom);
        Assert.NotEqual(oldEpoch, client.CurrentRoomEpoch);
        if (closeFirst) Assert.Equal(new[] { GatewayProtocol.Names.RoomClose, GatewayProtocol.Names.RoomJoin }, calls);
        else Assert.Equal(new[] { GatewayProtocol.Names.RoomJoin }, calls); // 持门后重验，旧 Close 被拒。
    }

    [Fact]
    public async Task OldCoordinatorCannotStopOrNotifyTheNextWorld()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old-room", "name", "uid"));
        using var sharedStop = new CancellationTokenSource();
        await using var old = Coordinator(client, sharedStop);
        Assert.True(await client.JoinRoomAsync("new-room", "name", "uid"));
        await using var current = Coordinator(client, sharedStop);
        var nextEpoch = client.CurrentRoomEpoch;
        var notifications = 0;
        var localEvents = 0;
        old.LocalStopRequested += _ => localEvents++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        { notifications++; return Task.FromResult(Response(request, new { success = true })); };
        await old.TriggerCoordinatedStop(true, "late-old-stop");
        Assert.Equal(0, localEvents);
        Assert.Equal(0, notifications);
        Assert.False(sharedStop.IsCancellationRequested);
        Assert.False(current.IsExitTriggered);
        Assert.True(client.IsInRoom);
        Assert.Equal(nextEpoch, client.CurrentRoomEpoch);
    }

    [Fact]
    public async Task NormalCompletionStopAndAllArrivedCannotBecomeASessionFailure()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        using var stop = new CancellationTokenSource();
        await using var coordinator = Coordinator(client, stop);
        var entered = Signal();
        client.GetOrCreateGatewayForTest()._testSendOverride = (_, _, _) =>
        { entered.TrySetResult(true); return Task.CompletedTask; };
        var wait = coordinator.WaitForAllPlayers("strict_2", stop.Token);
        await entered.Task;
        await client.OnReconnecting(null); // 此时业务就绪门也有等待者。
        coordinator.LocalStopRequested += _ => Arrive(client, "strict_2");
        await coordinator.TriggerCoordinatedStop(false, "normal-completion", reportAbortToRoom: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(stop.IsCancellationRequested);
    }

}
