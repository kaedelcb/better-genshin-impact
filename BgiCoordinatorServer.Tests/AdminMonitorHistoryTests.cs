using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class AdminMonitorHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-monitor-history-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Snapshot_PreservesCompletedRoomAfterDisconnectAndServerRestart()
    {
        var history = new RoomHoeingHistory(_root);
        var members = new[] { new RoomHoeingMember("10001", "甲") };
        var batch = new CoordinatedBatchSnapshot("batch-one", 1, 0, 0, 1, "registering", "")
        { ParticipantUids = ["10001"] };
        history.ObserveBatch("CTRL_TEAM01", batch, members, Now.AddHours(-1));
        history.ObserveMember("CTRL_TEAM01", "10001", "甲", "instance", new ControlStatus
        { AutoHoeingRunning = true, TaskStatusAvailable = true }, "batch-one", Now.AddMinutes(-50));
        history.ObserveBatch("CTRL_TEAM01", batch with { Revision = 2, Phase = "completed" }, members, Now);

        var snapshot = RoomMonitorSnapshot.Create(new RoomManager(), Now, TimeSpan.FromMinutes(2),
            new RoomHoeingHistory(_root));

        Assert.Equal(0, snapshot.ConnectedControlRoomCount);
        Assert.Equal(0, snapshot.ConnectedControlMemberCount);
        var room = Assert.Single(snapshot.ControlRooms);
        Assert.Equal("TEAM01", room.Code);
        Assert.Equal(0, room.OnlineCount);
        Assert.Equal("completed", room.TodayHoeing!.Status);
        Assert.Equal(Now, Assert.Single(room.TodayHoeing.Runs).EndedAtUtc);
        Assert.Equal("甲", Assert.Single(room.Members).Name);
        Assert.False(room.Members[0].Online);
    }

    [Fact]
    public void Snapshot_ConnectedRoomWithNoHistoryGetsExplicitEmptyDay()
    {
        var manager = new RoomManager();
        manager.AddToControlRoom("CTRL_TEAM01", "connection", "10001", "甲");
        var snapshot = RoomMonitorSnapshot.Create(manager, Now, TimeSpan.FromMinutes(2), new RoomHoeingHistory(_root));
        Assert.Equal(1, snapshot.ConnectedControlRoomCount);
        var day = Assert.Single(snapshot.ControlRooms).TodayHoeing!;
        Assert.Equal("2026-10-25", day.Date);
        Assert.Equal("none", day.Status);
        Assert.Empty(day.Runs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
