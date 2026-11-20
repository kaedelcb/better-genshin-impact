#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>验证关房后重连仍能查询下一轮房间，以及握手响应的连接归属。</summary>
public sealed class GatewayReconnectHandshakeTests
{
    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response,
        Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload),
    };

    private static (BgiGatewayClient Gateway, List<string> Names) NewGateway(string connectionId)
    {
        var gateway = new BgiGatewayClient { _testConnectionIdOverride = connectionId };
        var names = new List<string>();
        gateway._testInvokeOverride = (_, request, _) =>
        {
            names.Add(request.Name);
            return Task.FromResult(Response(request, new
            {
                success = true,
                capabilities = new[] { "hoeing.start.v2" },
                rooms = Array.Empty<object>(),
            }));
        };
        return (gateway, names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentRecoveryWaitsForAutomaticReconnectBeforeStarting(bool automaticSucceeds)
    {
        await using var gateway = new BgiGatewayClient();
        var state = (int)HubConnectionState.Reconnecting;
        var starts = 0;
        gateway._testConnectionStateOverride = () => (HubConnectionState)Volatile.Read(ref state);
        gateway._testReconnectStartOverride = _ =>
        {
            Assert.Equal(HubConnectionState.Disconnected, (HubConnectionState)Volatile.Read(ref state));
            starts++;
            Volatile.Write(ref state, (int)HubConnectionState.Connected);
            return Task.CompletedTask;
        };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = gateway.ReconnectStartAsync(budget.Token);
        var second = gateway.ReconnectStartAsync(budget.Token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        Volatile.Write(ref state, (int)(automaticSucceeds ? HubConnectionState.Connected : HubConnectionState.Disconnected));
        await Task.WhenAll(first, second).WaitAsync(budget.Token);
        Assert.True(gateway.IsConnected);
        Assert.Equal(automaticSucceeds ? 0 : 1, starts);
    }

    [Fact]
    public async Task FirstQueryOnAConnectionSendsHelloExactlyOnce()
    {
        var (gateway, names) = NewGateway("conn-1");
        await using var _ = gateway;

        await gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);
        await gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);

        Assert.Equal(new[]
        {
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomListOnline,
            GatewayProtocol.Names.RoomListOnline,
        }, names);
        Assert.Contains("hoeing.start.v2", gateway.ServerCapabilities);
    }

    [Fact]
    public async Task NewConnectionAfterAutoReconnectSendsHelloAgainBeforeTheRequest()
    {
        var (gateway, names) = NewGateway("conn-1");
        await using var _ = gateway;

        await gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);
        // SignalR 自动重连换了 connectionId：服务端把新连接视为未握手。
        gateway._testConnectionIdOverride = "conn-2";
        await gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomCreate, new { roomCode = "r2" });

        Assert.Equal(new[]
        {
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomListOnline,
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomCreate,
        }, names);
    }

    [Fact]
    public async Task FailedHelloIsNotMarkedCompletedAndTheNextRequestRetriesIt()
    {
        var (gateway, names) = NewGateway("conn-1");
        await using var _ = gateway;
        var failNextHello = true;
        gateway._testInvokeOverride = (_, request, _) =>
        {
            names.Add(request.Name);
            if (request.Name == GatewayProtocol.Names.SessionHello && failNextHello)
            {
                failNextHello = false;
                return Task.FromException<GatewayEnvelope>(new IOException("transport reset during hello"));
            }
            return Task.FromResult(Response(request, new { success = true, capabilities = Array.Empty<string>() }));
        };

        await Assert.ThrowsAsync<IOException>(() =>
            gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null));
        // 首次握手失败不能算作已完成；下一次请求必须重新握手再发。
        await gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);

        Assert.Equal(new[]
        {
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomListOnline,
        }, names);
    }

    [Fact]
    public async Task FireAndForgetAlsoWaitsForTheConnectionHello()
    {
        var (gateway, names) = NewGateway("conn-1");
        await using var _ = gateway;
        var sent = new List<string>();
        gateway._testSendOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            return Task.CompletedTask;
        };

        await gateway.SendCommandFireAndForgetAsync("sync.reportArrival", new { syncId = "s1" });

        Assert.Equal(new[] { GatewayProtocol.Names.SessionHello }, names);
        Assert.Equal(new[] { "sync.reportArrival" }, sent);
    }

    [Fact]
    public async Task RoomListQueryAfterClosingRoomAndReconnectingReturnsNextHost()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        var names = new List<string>();
        gateway._testConnectionIdOverride = "conn-1";
        string? greeted = null;
        gateway._testInvokeOverride = (_, request, _) =>
        {
            names.Add(request.Name);
            if (request.Name == GatewayProtocol.Names.SessionHello)
                greeted = gateway.ConnectionId;
            if (greeted != gateway.ConnectionId)
                return Task.FromResult(Response(request, new { error = new { code = "handshake_required" } }));
            return Task.FromResult(Response(request, new
            {
                success = true,
                rooms = new[] { new { code = "next-room", hostName = "next-host" } },
            }));
        };

        Assert.True(await client.JoinRoomAsync("old-room", "old-host"));
        await client.CloseRoomAsync();
        Assert.False(client.IsInRoom);
        await client.OnReconnecting(null);
        gateway._testConnectionIdOverride = "conn-2";
        await client.OnReconnected("conn-2");

        var room = Assert.Single(await client.GetOnlineRoomsAsync());
        Assert.Equal("next-room", room.Code);
        Assert.Equal("next-host", room.HostName);
        Assert.Equal(new[]
        {
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomJoin,
            GatewayProtocol.Names.RoomClose,
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.RoomListOnline,
        }, names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentQueriesWaitForHelloAndRejectAStaleHandshake(bool reconnectDuringHello)
    {
        var (gateway, names) = NewGateway("conn-1");
        await using var _ = gateway;
        var invoke = gateway._testInvokeOverride!;
        var releaseHello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway._testInvokeOverride = async (method, request, token) =>
        {
            var response = await invoke(method, request, token);
            if (request.Name == GatewayProtocol.Names.SessionHello && names.Count == 1)
                await releaseHello.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return response;
        };

        var first = gateway.HelloAsync();
        var query = gateway.QueryAsync(GatewayProtocol.Names.RoomListOnline, null);
        Assert.Equal(new[] { GatewayProtocol.Names.SessionHello }, names);
        Assert.False(query.IsCompleted);
        if (reconnectDuringHello) gateway._testConnectionIdOverride = "conn-2";
        releaseHello.SetResult(true);

        if (reconnectDuringHello)
            await Assert.ThrowsAsync<IOException>(() => first);
        else
            await first;
        await query.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reconnectDuringHello ? 3 : 2, names.Count);
        Assert.All(names.GetRange(0, names.Count - 1), name => Assert.Equal(GatewayProtocol.Names.SessionHello, name));
        Assert.Equal(GatewayProtocol.Names.RoomListOnline, names[^1]);
    }
}
