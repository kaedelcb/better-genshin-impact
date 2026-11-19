#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class RoomEntryHandshakeRecoveryTests
{
    [Theory]
    [InlineData("room.listOnline")]
    [InlineData("room.create")]
    [InlineData("room.join")]
    public async Task ReconnectBetweenRoomsCanDiscoverOrEnterTheNextRoom(string operation)
    {
        await using var client = await ReconnectBetweenRoomsAsync();
        var calls = new List<GatewayEnvelope>();
        var handshaken = false;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            calls.Add(request);
            if (request.Name == "session.hello")
            {
                handshaken = true;
                return Response(request, new { });
            }

            return !handshaken ? HandshakeRequired(request) : request.Name switch
            {
                "room.listOnline" => Response(request, new { rooms = new[] { new { code = "next-room", hostUid = "next-host" } } }),
                "room.create" => Response(request, new { roomCode = "next-room" }),
                "room.join" => Response(request, new { success = request.GetString("roomCode") == "next-room" }),
                _ => throw new InvalidOperationException(request.Name),
            };
        };

        switch (operation)
        {
            case "room.listOnline":
                var room = Assert.Single(await client.GetOnlineRoomsAsync());
                Assert.Equal("next-room", room.Code);
                Assert.Equal("next-host", room.HostUid);
                Assert.False(client.IsInRoom);
                break;
            case "room.create":
                Assert.Equal("next-room", await client.CreateRoomAsync("host", null, "host-uid", 4));
                Assert.True(client.IsInRoom);
                break;
            case "room.join":
                Assert.True(await client.JoinRoomAsync("next-room", "member", "member-uid"));
                Assert.True(client.IsInRoom);
                break;
        }

        Assert.Equal(new[] { operation, "session.hello", operation }, calls.Select(request => request.Name));
        Assert.Equal(calls[0].RequestId, calls[2].RequestId);

        if (operation == "room.listOnline")
        {
            Assert.Single(await client.GetOnlineRoomsAsync());
            Assert.Equal(new[] { operation, "session.hello", operation, operation }, calls.Select(request => request.Name));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forbidden")]
    [InlineData("handshake_required")]
    public async Task CreateOnlyRepeatsARequestExplicitlyRejectedBeforeHandshake(string? error)
    {
        await using var client = await ReconnectBetweenRoomsAsync();
        var calls = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            calls.Add(request.Name);
            if (request.Name == "session.hello") return Response(request, new { });
            return error == null
                ? Task.FromException<GatewayEnvelope>(new IOException("response lost"))
                : Response(request, new { error = new { code = error, message = "rejected" } });
        };

        Assert.Null(await client.CreateRoomAsync("host", null, "host-uid", 4));
        Assert.Equal(error == "handshake_required"
            ? new[] { "room.create", "session.hello", "room.create" }
            : new[] { "room.create" }, calls);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task CancelDuringHelloDoesNotResendJoin()
    {
        await using var client = await ReconnectBetweenRoomsAsync();
        using var stop = new CancellationTokenSource();
        var calls = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            calls.Add(request.Name);
            if (request.Name != "session.hello") return HandshakeRequired(request);
            stop.Cancel();
            return Response(request, new { });
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.JoinRoomAsync("next-room", "member", "member-uid", stop.Token));
        Assert.Equal(new[] { "room.join", "session.hello" }, calls);
        Assert.False(client.IsInRoom);
    }


    [Fact]
    public async Task RetriedCreateUsesTheOriginallyRequestedPlayers()
    {
        await using var client = await ReconnectBetweenRoomsAsync();
        var whitelist = new List<string> { "host", "original-member" };
        var plannedPlayers = new List<string> { "host-uid", "original-uid" };
        var attempts = new List<GatewayEnvelope>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == "session.hello")
            {
                whitelist[1] = "later-member";
                plannedPlayers[1] = "later-uid";
                return Response(request, new { });
            }
            attempts.Add(request);
            return attempts.Count == 1 ? HandshakeRequired(request) : Response(request, new { roomCode = "next-room" });
        };

        Assert.Equal("next-room", await client.CreateRoomAsync("host", whitelist, "host-uid", 4,
            worldPlanParticipantUids: plannedPlayers));
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].RequestId, attempts[1].RequestId);
        Assert.All(attempts, request =>
        {
            Assert.Equal(new[] { "host", "original-member" }, request.Get<string[]>("whitelist"));
            Assert.Equal(new[] { "host-uid", "original-uid" }, request.Get<string[]>("worldPlanParticipantUids"));
        });
    }



    [Fact]
    public async Task CancelledLookupDoesNotReturnALateRoom()
    {
        await using var client = await ReconnectBetweenRoomsAsync();
        using var stop = new CancellationTokenSource();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name != "room.listOnline") throw new InvalidOperationException(request.Name);
            stop.Cancel();
            return Response(request, new { rooms = new[] { new { code = "next-room" } } });
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetOnlineRoomsAsync(stop.Token));
    }


    private static async Task<CoordinatorClient> ReconnectBetweenRoomsAsync()
    {
        var client = new CoordinatorClient { _testIsConnectedOverride = true, _testConnectionIdOverride = "old-connection" };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Response(request, new { success = true });
        Assert.True(await client.JoinRoomAsync("old-room", "player", "player-uid"));
        await client.CloseRoomAsync();
        Assert.False(client.IsInRoom);
        await client.OnReconnecting(null);
        client._testConnectionIdOverride = "new-connection";
        await client.OnReconnected("new-connection");
        return client;
    }

    private static Task<GatewayEnvelope> HandshakeRequired(GatewayEnvelope request) =>
        Response(request, new { error = new { code = "handshake_required", message = "hello first" } });

    private static Task<GatewayEnvelope> Response(GatewayEnvelope request, object payload) => Task.FromResult(new GatewayEnvelope
    {
        Type = GatewayProtocol.MessageTypes.Response,
        Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload)
    });
}
