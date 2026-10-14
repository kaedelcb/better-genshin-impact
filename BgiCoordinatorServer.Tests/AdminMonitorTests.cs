using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class AdminMonitorTests
{
    [Fact]
    public void Snapshot_ShowsConnectedControlRoomBeforeGameplayRoomExists()
    {
        var manager = new RoomManager();
        manager.AddToControlRoom("CTRL_TEAM01", "assistant-connection", "10001", "甲");

        var snapshot = RoomMonitorSnapshot.Create(manager, DateTime.UtcNow, TimeSpan.FromSeconds(120));

        Assert.Equal(0, snapshot.TotalRoomCount);
        Assert.Equal(1, snapshot.ConnectedControlRoomCount);
        Assert.Equal(1, snapshot.ConnectedControlMemberCount);
        var room = Assert.Single(snapshot.ControlRooms);
        Assert.Equal("TEAM01", room.Code);
        Assert.Equal("甲", Assert.Single(room.Members).Name);

        manager.RemoveFromControlRoom("CTRL_TEAM01", "assistant-connection");
        snapshot = RoomMonitorSnapshot.Create(manager, DateTime.UtcNow, TimeSpan.FromSeconds(120));
        Assert.Equal(0, snapshot.ConnectedControlRoomCount);
        Assert.Empty(snapshot.ControlRooms);
    }

    [Fact]
    public void Snapshot_ShowsStartedRoomMembersDurationAndRouteProgress()
    {
        var now = DateTime.UtcNow;
        var manager = new RoomManager();
        var code = manager.CreateRoom("host-connection", "房主", playerUid: "10001", expectedPlayerCount: 2);
        var room = manager.GetRoom(code)!;
        lock (room)
        {
            room.IsStarted = true;
            room.StartedAtUtc = now.AddMinutes(-42);
            room.CurrentWorldRound = 2;
            room.HostRouteListUploaded = true;
            room.HostRouteList = ["路线一", "路线二", "路线三"];
            room.Players[0].CurrentRouteIndex = 1;
            room.Players[0].LastHeartbeat = now.AddSeconds(-5);
        }

        var snapshot = RoomMonitorSnapshot.Create(manager, now, TimeSpan.FromSeconds(120));

        Assert.Equal(1, snapshot.TotalRoomCount);
        Assert.Equal(1, snapshot.StartedRoomCount);
        Assert.Equal(1, snapshot.OnlineMemberCount);
        var displayedRoom = Assert.Single(snapshot.Rooms);
        Assert.Equal("started", displayedRoom.Status);
        Assert.Equal(now.AddMinutes(-42), displayedRoom.StartedAtUtc);
        Assert.Equal(2, displayedRoom.WorldRound);
        Assert.Equal(3, displayedRoom.TotalRoutes);
        var member = Assert.Single(displayedRoom.Members);
        Assert.Equal("房主", member.Name);
        Assert.Equal("10001", member.Uid);
        Assert.Equal(2, member.RouteNumber);
    }

    [Fact]
    public void Snapshot_DoesNotCountStaleOrReconnectingMembersAsOnline()
    {
        var now = DateTime.UtcNow;
        var manager = new RoomManager();
        var code = manager.CreateRoom("host-connection", "房主", playerUid: "10001");
        var room = manager.GetRoom(code)!;
        lock (room)
        {
            room.IsStarted = true;
            room.StartedAtUtc = now.AddMinutes(-10);
            room.Players[0].LastHeartbeat = now.AddMinutes(-3);
            room.Players[0].CurrentRouteIndex = 1;
            room.HostRouteListUploaded = true;
            room.HostRouteList = ["一", "二"];
        }

        var snapshot = RoomMonitorSnapshot.Create(manager, now, TimeSpan.FromSeconds(120));
        Assert.Equal(0, snapshot.StartedRoomCount);
        Assert.Equal(1, snapshot.DisconnectedRoomCount);
        Assert.Null(Assert.Single(Assert.Single(snapshot.Rooms).Members).RouteNumber);

        lock (room)
        {
            room.Players[0].LastHeartbeat = now;
            room.GracePendingMembers["host-connection"] = now.AddSeconds(30);
        }
        snapshot = RoomMonitorSnapshot.Create(manager, now, TimeSpan.FromSeconds(120));
        Assert.Equal(0, snapshot.OnlineMemberCount);
        Assert.True(Assert.Single(Assert.Single(snapshot.Rooms).Members).Reconnecting);
    }

    [Fact]
    public void Snapshot_FirstStartedWorldIsShownAsRoundOne()
    {
        var now = DateTime.UtcNow;
        var manager = new RoomManager();
        var code = manager.CreateRoom("host-connection", "房主");
        var room = manager.GetRoom(code)!;
        lock (room)
        {
            room.IsStarted = true;
            room.StartedAtUtc = now;
            room.Players[0].LastHeartbeat = now;
        }

        Assert.Equal(1, Assert.Single(RoomMonitorSnapshot.Create(manager, now,
            TimeSpan.FromSeconds(120)).Rooms).WorldRound);
    }

    [Fact]
    public void AdminPassword_IsDisabledUntilStrongSecretIsConfigured()
    {
        Assert.False(new AdminMonitorAccess(null).Enabled);
        Assert.False(new AdminMonitorAccess("too-short").Enabled);
        var access = new AdminMonitorAccess("correct-password-123456");
        Assert.True(access.Enabled);
        Assert.True(access.Verify("correct-password-123456"));
        Assert.False(access.Verify("incorrect-password-123456"));
    }
}
