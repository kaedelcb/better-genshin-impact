#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>2026-11-13：新连接先于旧连接进入宽限期，不能将临时占位升级成全队重开。</summary>
public sealed class HoeingRoomRecoveryRetryTests
{
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload)
    };
    private static async Task<CoordinatorClient> JoinedClient(string? connectionId = null)
    {
        var client = new CoordinatorClient
        { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1, _testConnectionIdOverride = connectionId };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = true }));
        using var scope = new CoordinatedHoeingScope(true, "original-batch");
        Assert.True(await client.JoinRoomAsync("original-room", "original-name", "original-uid"));
        return client;
    }

    [Theory]
    [InlineData("world_plan_duplicate_participant")]
    [InlineData("hoeing_start_duplicate_participant")]
    public async Task DuplicateOccupancyWaitsWithoutReleasingBusinessOrChangingIdentity(string error)
    {
        await using var client = await JoinedClient();
        var epoch = client.CurrentRoomEpoch;
        var entered = Signal();
        var allowSuccess = Signal();
        var joins = new List<GatewayEnvelope>();
        var hellos = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello) hellos++;
            if (request.Name != GatewayProtocol.Names.RoomJoin) return Response(request, new { success = true });
            joins.Add(request);
            if (joins.Count <= 2) return Response(request, new { success = false, error });
            entered.TrySetResult(true);
            await allowSuccess.Task.WaitAsync(token);
            return Response(request, new { success = true });
        };
        using var successor = new CoordinatedHoeingScope(true, "successor-batch");
        await client.OnReconnecting(null);
        var recovery = client.OnReconnected("new-connection");
        var business = client.WaitForBusinessReadyAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(client.IsInRoom);
        Assert.True(client.IsReconnecting);
        Assert.False(business.IsCompleted);
        Assert.Equal(epoch, client.CurrentRoomEpoch);
        allowSuccess.SetResult(true);
        await Task.WhenAll(recovery, business).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(client.IsInRoom);
        Assert.False(client.IsReconnecting);
        Assert.Equal(1, hellos);
        Assert.Equal(3, joins.Count);
        Assert.Equal(0, failures);
        Assert.All(joins, request =>
        {
            Assert.Equal("original-room", request.RoomCode);
            Assert.Equal("original-room", request.GetString("roomCode"));
            Assert.Equal("original-name", request.GetString("playerName"));
            Assert.Equal("original-uid", request.GetString("playerUid"));
            Assert.Equal("original-batch", request.GetString("hoeingBatchId"));
        });
    }

    [Fact]
    public async Task DuplicateOccupancyCannotRenewTheTotalRecoveryBudget()
    {
        await using var client = await JoinedClient();
        client._testRoomRecoveryBudget = TimeSpan.FromMilliseconds(150);
        var joins = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin) joins++;
            return Task.FromResult(Response(request, new { success = false, error = "world_plan_duplicate_participant" }));
        };
        await client.OnReconnecting(null);
        await client.OnReconnected("new-connection").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(joins > 1);
        Assert.Equal(1, failures);
        Assert.False(client.IsInRoom);
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => client.WaitForBusinessReadyAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("version_incompatible")]
    [InlineData("world_plan_not_participant")]
    [InlineData("hoeing_start_identity_mismatch")]
    [InlineData("房间不存在")]
    [InlineData("房间已开锄")]
    public async Task OtherJoinRefusalsFailImmediatelyAndKeepTheirReason(string error)
    {
        await using var client = await JoinedClient();
        var joins = 0;
        string? failure = null;
        client.SessionFailed += reason => failure = reason;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin) joins++;
            return Task.FromResult(Response(request, new { success = false, error }));
        };
        await client.OnReconnecting(null);
        await client.OnReconnected("new-connection").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, joins);
        Assert.NotNull(failure);
        Assert.Contains(error, failure!);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task ObjectGatewayErrorDoesNotBecomeAnOccupancyRetry()
    {
        await using var client = await JoinedClient();
        var joins = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name != GatewayProtocol.Names.RoomJoin)
                return Task.FromResult(Response(request, new { success = true }));
            joins++;
            return Task.FromResult(Response(request, new { error = new { code = "forbidden", message = "not admitted" } }));
        };
        await client.OnReconnecting(null);
        await client.OnReconnected("new-connection").WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, joins);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task UserStopCancelsOccupancyDelayWithoutFurtherJoinOrFailureNotification()
    {
        await using var client = await JoinedClient();
        client._testWaitRetryIntervalMs = 30_000;
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        var refused = Signal();
        var joins = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            { joins++; refused.TrySetResult(true); }
            return Task.FromResult(Response(request, new { success = false, error = "world_plan_duplicate_participant" }));
        };
        await client.OnReconnecting(null);
        var recovery = client.OnReconnected("new-connection");
        await refused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Run(() => stop.Cancel()).WaitAsync(TimeSpan.FromSeconds(3));
        await recovery.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, joins);
        Assert.Equal(0, failures);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task ExplicitRoomChangeCancelsOldOccupancyWaitBeforeOpeningNewEpoch()
    {
        await using var client = await JoinedClient();
        client._testWaitRetryIntervalMs = 30_000;
        var oldEpoch = client.CurrentRoomEpoch;
        var refused = Signal();
        var rooms = new List<string>();
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name != GatewayProtocol.Names.RoomJoin)
                return Task.FromResult(Response(request, new { success = true }));
            rooms.Add(request.GetString("roomCode"));
            if (request.GetString("roomCode") == "original-room")
            {
                refused.TrySetResult(true);
                return Task.FromResult(Response(request, new { success = false, error = "world_plan_duplicate_participant" }));
            }
            return Task.FromResult(Response(request, new { success = true }));
        };
        await client.OnReconnecting(null);
        var recovery = client.OnReconnected("new-connection");
        await refused.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(await client.JoinRoomAsync("next-room", "name", "uid").WaitAsync(TimeSpan.FromSeconds(3)));
        await recovery.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "original-room", "next-room" }, rooms);
        Assert.NotEqual(oldEpoch, client.CurrentRoomEpoch);
        Assert.True(client.IsInRoom);
        Assert.Equal(0, failures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TransportRpcFailureBeforeTheNextReconnectingEventDoesNotEndTheRoom(bool failHello, bool ioFailure)
    {
        await using var client = await JoinedClient();
        var interrupted = Signal();
        var acceptLatest = false;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            var failedMethod = failHello ? GatewayProtocol.Names.SessionHello : GatewayProtocol.Names.RoomJoin;
            if (request.Name == failedMethod && !acceptLatest)
            {
                interrupted.TrySetResult(true);
                Exception failure = ioFailure ? new IOException("old RPC failed before event")
                    : new WebSocketException("old RPC failed before event");
                return Task.FromException<GatewayEnvelope>(failure);
            }
            return Task.FromResult(Response(request, new { success = true }));
        };
        await client.OnReconnecting(null);
        var old = client.OnReconnected("transport-B");
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(old.IsCompleted);
        Assert.Equal(0, failures); // SDK 实际顺序：在途调用先失败，第二次 Reconnecting 尚未通知。
        await client.OnReconnecting(null);
        acceptLatest = true;
        var latest = client.OnReconnected("transport-C");
        await Task.WhenAll(old, latest).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, failures);
        Assert.True(client.IsInRoom);
        await client.WaitForBusinessReadyAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedPathAdoptsTheExplicitlyStartedConnectionButCannotReviveAfterStop(bool stopDuringStart)
    {
        await using var client = await JoinedClient("transport-A");
        var epoch = client.CurrentRoomEpoch;
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        client._testIsConnectedOverride = false;
        client._testConnectionIdOverride = null;
        var started = Signal();
        var releaseStart = Signal();
        var hellos = 0;
        var joins = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testReconnectStartOverride = async _ =>
        {
            started.TrySetResult(true);
            await releaseStart.Task; // 用迟到成功同时验证停止在先时不能重新准入。
            client._testIsConnectedOverride = true;
            client._testConnectionIdOverride = "transport-B";
        };
        gateway._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello) hellos++;
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                joins++;
                Assert.Equal("original-room", request.GetString("roomCode"));
                Assert.Equal("original-uid", request.GetString("playerUid"));
                Assert.Equal("original-batch", request.GetString("hoeingBatchId"));
            }
            return Task.FromResult(Response(request, new { success = true }));
        };
        var method = typeof(CoordinatorClient).GetMethod("OnConnectionClosed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var recovery = (Task)method.Invoke(client, new object?[] { null })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (stopDuringStart) stop.Cancel();
        releaseStart.SetResult(true);
        await recovery.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, failures);
        Assert.Equal(epoch, client.CurrentRoomEpoch);
        Assert.Equal(stopDuringStart ? 0 : 1, hellos);
        Assert.Equal(stopDuringStart ? 0 : 1, joins);
        Assert.Equal(!stopDuringStart, client.IsInRoom);
        if (stopDuringStart)
            await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => client.WaitForBusinessReadyAsync(CancellationToken.None));
        else
            await client.WaitForBusinessReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task LateTransportEventsCannotInvalidateTheAlreadyAdoptedConnection()
    {
        await using var client = await JoinedClient("transport-A");
        var hellos = 0;
        var joins = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello) hellos++;
            if (request.Name == GatewayProtocol.Names.RoomJoin) joins++;
            return Task.FromResult(Response(request, new { success = true }));
        };
        // 新连接已建立，但 SDK 的旧 Reconnected / Reconnecting 事件仍排在队列中。
        client._testConnectionIdOverride = "transport-C";
        var business = client.WaitForBusinessReadyAsync(CancellationToken.None);
        Assert.False(business.IsCompleted);
        await client.OnReconnected("transport-B");
        Assert.Equal(0, hellos);
        Assert.Equal(0, joins);
        await client.OnReconnected("transport-C");
        await business.WaitAsync(TimeSpan.FromSeconds(3));
        await client.OnReconnecting(null);
        await client.OnReconnected("transport-B");
        await client.WaitForBusinessReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, hellos);
        Assert.Equal(1, joins);
        Assert.Equal(0, failures);
        Assert.True(client.IsInRoom);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("late-success")]
    [InlineData("late-error")]
    public async Task ASecondDisconnectRequiresNewHelloAndCannotBeTerminatedByTheOldCallback(string oldOutcome)
    {
        await using var client = await JoinedClient();
        client._testWaitRetryIntervalMs = 50;
        var firstJoin = Signal();
        var releaseOld = Signal();
        var latestHello = Signal();
        var releaseLatestHello = Signal();
        var helloForLatest = false;
        var latestTransport = false;
        var hellos = 0;
        var joins = 0;
        var failures = 0;
        client.SessionFailed += _ => failures++;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello)
            {
                hellos++;
                if (latestTransport)
                {
                    latestHello.TrySetResult(true);
                    await releaseLatestHello.Task.WaitAsync(token);
                    helloForLatest = true;
                }
            }
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                if (++joins == 1)
                {
                    firstJoin.TrySetResult(true);
                    if (oldOutcome == "duplicate")
                        return Response(request, new { success = false, error = "world_plan_duplicate_participant" });
                    await releaseOld.Task.WaitAsync(token);
                    return oldOutcome == "late-error"
                        ? Response(request, new { error = new { code = "handshake_required", message = "old transport lost" } })
                        : Response(request, new { success = true });
                }
                Assert.True(helloForLatest, "新连接须先完成自己的 Hello，旧连接握手不算恢复确认。");
            }
            return Response(request, new { success = true });
        };
        await client.OnReconnecting(null);
        var old = client.OnReconnected("transport-B");
        await firstJoin.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var business = client.WaitForBusinessReadyAsync(CancellationToken.None);
        latestTransport = true;
        await client.OnReconnecting(null);
        var latest = client.OnReconnected("transport-C");
        releaseOld.TrySetResult(true);
        await latestHello.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(business.IsCompleted);
        Assert.False(client.IsInRoom);
        Assert.Equal(0, failures);
        releaseLatestHello.SetResult(true);
        await Task.WhenAll(old, latest, business).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, hellos);
        Assert.Equal(2, joins);
        Assert.Equal(0, failures);
        Assert.True(client.IsInRoom);
    }

    [Fact]
    public async Task QueuedRecoveryCallbacksDoNotJoinAgainAfterOriginalRoomIsReady()
    {
        await using var client = await JoinedClient();
        var entered = Signal();
        var release = Signal();
        var joins = 0;
        var hellos = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello) hellos++;
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                joins++;
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return Response(request, new { success = true });
        };
        await client.OnReconnecting(null);
        var first = client.OnReconnected("new-connection");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = client.OnReconnected("same-connection");
        Assert.False(second.IsCompleted);
        release.SetResult(true);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, joins);
        Assert.Equal(1, hellos);
        Assert.True(client.IsInRoom);
    }
}
