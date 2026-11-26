#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class MultiplayerCoordinatorHostRoleTests
{
    private static CoordinatorClient CreateClient()
    {
        var client = new CoordinatorClient { _testIsConnectedOverride = true };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) => Task.FromResult(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = request.Name,
            Payload = GatewayEnvelope.ToPayload(request.Name switch
            {
                GatewayProtocol.Names.RoomCreate => (object)new { roomCode = "host-room" },
                GatewayProtocol.Names.RoomGetState => new { value = new { routeIndex = 0 } },
                _ => new { success = true },
            }),
        });
        return client;
    }

    private static void PublishRoster(CoordinatorClient client, string room, string hostUid, string memberUid)
        => client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event,
            Name = GatewayProtocol.Events.RoomPlayerListChanged,
            RoomCode = room,
            Payload = GatewayEnvelope.ToPayload(new
            {
                players = new[]
                {
                    new PlayerInfo { PlayerUid = hostUid, IsHost = true },
                    new PlayerInfo { PlayerUid = memberUid, IsHost = false },
                },
            }),
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateRosterDeterminesCdAndRouteSyncRole(bool isHost)
    {
        await using var client = CreateClient();
        const string ownUid = "own";
        const string peerUid = "peer";
        if (isHost)
            Assert.Equal("host-room", await client.CreateRoomAsync("own", null, ownUid, 2));
        else
            Assert.True(await client.JoinRoomAsync("host-room", "own", ownUid));

        await using var coordinator = new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig());
        // 建房/入房回执已返回，但首份玩家名单尚未到达。
        Assert.False(coordinator.IsHost);
        PublishRoster(client, "host-room", isHost ? ownUid : peerUid, isHost ? peerUid : ownUid);

        Assert.Equal(isHost, coordinator.IsHost);
        Assert.Equal(isHost, SoloDebugDecisions.ShouldRecordCd(true, coordinator.IsHost, false));
        Assert.False(SoloDebugDecisions.ShouldRecordCd(true, coordinator.IsHost, true));
        Assert.True(SoloDebugDecisions.ShouldRecordCd(false, coordinator.IsHost, false));
        Assert.Equal(isHost ? RouteSyncDecision.Wait : RouteSyncDecision.CatchUp,
            await coordinator.RouteSyncCoordinator!.CheckSyncAtRouteStart(1, CancellationToken.None));
    }

    [Fact]
    public async Task OldCoordinatorDoesNotInheritNextRoundHostRole()
    {
        await using var client = CreateClient();
        Assert.True(await client.JoinRoomAsync("first-room", "own", "own"));
        PublishRoster(client, "first-room", "peer", "own");
        await using var old = new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig());
        Assert.False(old.IsHost);

        await client.LeaveRoomAsync();
        Assert.Equal("host-room", await client.CreateRoomAsync("own", null, "own", 2));
        PublishRoster(client, "host-room", "own", "peer");
        await using var current = new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig());
        Assert.True(current.IsHost);
        Assert.False(old.IsHost);
        Assert.False(old.RequestLocalStop("旧轮次迟到的停止"));

        // 房间终止后保留本轮归属，正常停止收尾仍能判断由房主负责关房。
        await client.CloseRoomAsync();
        Assert.True(current.IsHost);
        Assert.False(old.IsHost);
    }
}
