using System.Text.Json;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RoomMemberRecoverySafetyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (string Code, Room Room, PlayerInfo Host, PlayerInfo Member) CreateRoom(RoomManager manager)
    {
        var code = manager.CreateRoom("host-old", "host", playerUid: "u0");
        Assert.True(manager.JoinRoom(code, "member-old", "member-old", "member", "u1").Success);
        var room = manager.GetRoom(code)!;
        return (code, room, room.Players.Single(p => p.PlayerUid == "u0"), room.Players.Single(p => p.PlayerUid == "u1"));
    }

    private static HeartbeatMonitor Monitor(GatewayTestHarness h)
        => new(h.RoomManager,
            new GatewayBroadcaster(h.LegacyHub.Object, h.GatewayHub.Object, h.Tracker,
                new Mock<ILogger<GatewayBroadcaster>>().Object),
            new RoomPhaseObserver(new Mock<ILogger<RoomPhaseObserver>>().Object), h.Ops,
            new Mock<ILogger<HeartbeatMonitor>>().Object);

    [Fact]
    public void GraceProtectsStaleHeartbeatUntilDeadline_ThenCleansOnce()
    {
        var manager = new RoomManager();
        var (code, room, host, member) = CreateRoom(manager);
        var now = DateTime.UtcNow;
        host.LastHeartbeat = now;
        member.LastHeartbeat = now.AddMinutes(-1);
        Assert.NotNull(manager.BeginGameDisconnect(member.ConnectionId, now, Timeout));
        var oldConnection = member.ConnectionId;
        room.ArrivalSets["sync"] = [oldConnection];
        room.FightDoneSets["fight"] = [oldConnection];
        room.FightParticipantSets["fight"] = [oldConnection];
        room.WorldJoinedSet.Add(oldConnection);
        room.RouteVerificationDoneSet.Add(oldConnection);

        Assert.Empty(manager.RemoveDeadPlayers(Timeout, now.AddSeconds(29)));
        Assert.Same(room, manager.GetRoomByConnectionId(oldConnection).Room);
        var expiry = Assert.Single(manager.RemoveDeadPlayers(Timeout, now.AddSeconds(30)));
        Assert.False(expiry.HostTimedOut);
        Assert.Equal("u1", Assert.Single(expiry.RemovedPlayers).PlayerUid);
        Assert.Equal("u0", Assert.Single(expiry.RemainingPlayers).PlayerUid);
        Assert.Null(manager.GetRoomByConnectionId(oldConnection).Room);
        Assert.Single(room.Players);
        Assert.Empty(room.GracePendingMembers);
        Assert.Empty(room.ArrivalSets["sync"]);
        Assert.Empty(room.FightDoneSets["fight"]);
        Assert.Empty(room.FightParticipantSets["fight"]);
        Assert.Empty(room.WorldJoinedSet);
        Assert.Empty(room.RouteVerificationDoneSet);
        Assert.Empty(manager.RemoveDeadPlayers(Timeout, now.AddSeconds(30)));
        Assert.Same(room, manager.GetRoom(code));
    }

    [Fact]
    public async Task ReconnectWins_StaleDisconnectAndOldGraceCannotRemoveNewIdentity()
    {
        var h = new GatewayTestHarness();
        var (code, room, host, member) = CreateRoom(h.RoomManager);
        var now = DateTime.UtcNow;
        room.IsStarted = true;
        member.LastHeartbeat = now.AddMinutes(-1);
        h.RoomManager.BeginGameDisconnect("member-old", now, Timeout);
        room.ArrivalSets["sync"] = ["member-old"];
        Assert.True(h.RoomManager.JoinRoom(code, "member-new", "member-new", "member", "u1").Success);

        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("member-old"), null);
        var before = member.LastHeartbeat;
        h.RoomManager.UpdateHeartbeat("member-new");
        Assert.True(member.LastHeartbeat >= before);
        // 已越过原连接的宽限期限；新身份的有效心跳仍应被保留。
        host.LastHeartbeat = member.LastHeartbeat = now.AddSeconds(31);
        Assert.Empty(h.RoomManager.RemoveDeadPlayers(Timeout, now.AddSeconds(31)));
        Assert.Same(member, room.Players.Single(p => p.PlayerUid == "u1"));
        Assert.Equal("member-new", member.ConnectionId);
        Assert.Same(room, h.RoomManager.GetRoomByConnectionId("member-new").Room);
        Assert.Null(h.RoomManager.GetRoomByConnectionId("member-old").Room);
        Assert.Empty(room.GracePendingMembers);
        Assert.Contains("member-new", room.ArrivalSets["sync"]);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberStatusChanged", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpiryWins_StartedRoomDoesNotResurrectRemovedMember()
    {
        var h = new GatewayTestHarness();
        var (code, room, host, _) = CreateRoom(h.RoomManager);
        var now = DateTime.UtcNow;
        host.LastHeartbeat = now;
        room.IsStarted = true;
        room.GracePendingMembers["member-old"] = now;
        Assert.Single(h.RoomManager.RemoveDeadPlayers(Timeout, now));

        var join = h.RoomManager.JoinRoom(code, "member-new", "member-new", "member", "u1");
        Assert.False(join.Success);
        Assert.Equal("房间已开锄", join.Error);
        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("member-old"), null);
        Assert.Empty(h.RoomManager.RemoveDeadPlayers(Timeout, now));
        Assert.Single(room.Players);
        Assert.Empty(room.GracePendingMembers);
        Assert.Null(h.RoomManager.GetRoomByConnectionId("member-new").Room);
    }

    [Fact]
    public async Task ConcurrentHeartbeatAndExpiry_CommitMemberAndMapTogether()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var manager = new RoomManager();
            var (_, room, host, member) = CreateRoom(manager);
            var now = DateTime.UtcNow;
            host.LastHeartbeat = now;
            member.LastHeartbeat = now.AddMinutes(-1);
            using var start = new Barrier(3);
            var heartbeat = Task.Run(() => { start.SignalAndWait(); manager.UpdateHeartbeat("member-old"); });
            var expiry = Task.Run(() => { start.SignalAndWait(); return manager.RemoveDeadPlayers(Timeout, now); });
            start.SignalAndWait();
            await Task.WhenAll(heartbeat, expiry);
            var retained = room.Players.Any(p => p.ConnectionId == "member-old");
            Assert.Equal(retained, manager.GetRoomByConnectionId("member-old").Room is not null);
            Assert.Equal(retained ? 0 : 1, expiry.Result.Count);
        }
    }

    [Fact]
    public void ExpirySnapshotPreservesWireShapeAndCannotFollowLiveMutations()
    {
        var manager = new RoomManager();
        var (_, room, host, member) = CreateRoom(manager);
        var now = DateTime.UtcNow;
        host.LastHeartbeat = now;
        member.LastHeartbeat = now.AddMinutes(-1);
        var expectedHost = JsonNode.Parse(JsonSerializer.Serialize(host, GatewayJson.Options));
        var expectedMember = JsonNode.Parse(JsonSerializer.Serialize(member, GatewayJson.Options));
        var snapshot = Assert.Single(manager.RemoveDeadPlayers(Timeout, now));
        host.PlayerName = "changed host";
        host.CurrentProgress = 999;
        member.ConnectionId = "changed removed object";
        room.Players.Clear();
        Assert.True(JsonNode.DeepEquals(expectedHost,
            JsonNode.Parse(JsonSerializer.Serialize(Assert.Single(snapshot.RemainingPlayers), GatewayJson.Options))));
        Assert.True(JsonNode.DeepEquals(expectedMember,
            JsonNode.Parse(JsonSerializer.Serialize(Assert.Single(snapshot.RemovedPlayers), GatewayJson.Options))));
        Assert.True(((ICollection<RoomPlayerSnapshot>)snapshot.RemainingPlayers).IsReadOnly);
        Assert.True(((ICollection<RoomPlayerSnapshot>)snapshot.RemovedPlayers).IsReadOnly);
    }

    [Fact]
    public async Task RepeatedMonitorScan_BroadcastsOfflineAndPlayerListExactlyOnceOutsideRoomLock()
    {
        var h = new GatewayTestHarness();
        var (_, room, host, _) = CreateRoom(h.RoomManager);
        var now = DateTime.UtcNow;
        host.LastHeartbeat = now;
        room.GracePendingMembers["member-old"] = now;
        h.LegacyGroupProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => Assert.False(System.Threading.Monitor.IsEntered(room)))
            .Returns(Task.CompletedTask);
        using var monitor = Monitor(h);
        await monitor.ScanExpiredPlayersAsync(now);
        await monitor.ScanExpiredPlayersAsync(now);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberStatusChanged", It.Is<object?[]>(a =>
            a.Length == 3 && (string)a[0]! == "u1" && (string)a[1]! == "Offline"),
            It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("PlayerListUpdated", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("RoomClosed", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OldHostDisconnectCannotCloseReplacement_ActualHostDisconnectClosesAndArchivesOnce()
    {
        var h = new GatewayTestHarness();
        var (code, room, _, _) = CreateRoom(h.RoomManager);
        var ended = 0;
        h.RoomManager.RoomLogSessionEnded += (_, _) =>
        {
            Assert.False(System.Threading.Monitor.IsEntered(room));
            ended++;
        };
        Assert.True(h.RoomManager.JoinRoom(code, "host-new", "host-new", "host", "u0").Success);
        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("host-old"), null);
        Assert.Same(room, h.RoomManager.GetRoom(code));
        Assert.Equal("host-new", room.HostConnectionId);
        Assert.Equal(0, ended);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("RoomClosed", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);

        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("host-new"), null);
        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("host-new"), null);
        Assert.Null(h.RoomManager.GetRoom(code));
        Assert.Null(h.RoomManager.GetRoomByConnectionId("member-old").Room);
        Assert.Null(h.RoomManager.GetRoomByConnectionId("host-new").Room);
        Assert.Equal(1, ended);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("RoomClosed", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HostHeartbeatExpiryClosesRoomOnce_WithoutMemberOfflineOrOrphanMapping()
    {
        var h = new GatewayTestHarness();
        var (code, _, host, member) = CreateRoom(h.RoomManager);
        var now = DateTime.UtcNow;
        host.LastHeartbeat = now.AddMinutes(-1);
        member.LastHeartbeat = now;
        var ended = 0;
        h.RoomManager.RoomLogSessionEnded += (_, _) => ended++;
        using var monitor = Monitor(h);
        await monitor.ScanExpiredPlayersAsync(now);
        await monitor.ScanExpiredPlayersAsync(now);
        await h.Ops.HandleDisconnectAsync(GatewayHandlerContext.V3("host-old"), null);
        Assert.Null(h.RoomManager.GetRoom(code));
        Assert.Null(h.RoomManager.GetRoomByConnectionId("host-old").Room);
        Assert.Null(h.RoomManager.GetRoomByConnectionId("member-old").Room);
        Assert.Equal(1, ended);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("RoomClosed", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberStatusChanged", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
