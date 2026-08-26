using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Persistence;
using BgiCoordinatorServer.RoomControl.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests.ControlRoom;

public class ScheduleIntegrationTests
{
    private static ControlRoomDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ControlRoomDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        var db = new ControlRoomDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static (ControlRoomManager manager, ControlRoomRepository repo, SqliteEventStore eventStore) CreateManager()
    {
        var db = CreateInMemoryDb();
        var eventStore = new SqliteEventStore(db);
        var repo = new ControlRoomRepository(db, eventStore);
        var manager = new ControlRoomManager(repo, eventStore);
        return (manager, repo, eventStore);
    }

    [Fact]
    public async Task OfflineMemberJoinsAfterScheduledTime_TriggersSessionImmediately()
    {
        var (manager, repo, _) = CreateManager();
        var room = await manager.CreateAsync("R1", "pass", "owner", ["uid1"]);
        await manager.JoinAsync("R1", "pass", "uid1", "Alice", "inst1", "conn1");

        var member = room.GetMemberByUid("uid1");
        Assert.NotNull(member);
        member.UpdateDesiredState(new MemberDesiredState(
            ScheduledOnlineTime: "09:00",
            OnlineHoeingGroupNames: ["group1"],
            OnlineHoeingGroupTypes: ["group"],
            ExpectedHoeingPlayers: 2,
            QuickCommands: null));

        // 模拟 09:02（上海时间）才上线/检查
        var shanghai = new DateTime(2026, 8, 26, 9, 2, 0);
        var session = room.TryStartSessionForScheduledMember("uid1", shanghai, TimeSpan.FromMinutes(5));

        Assert.NotNull(session);
        Assert.True(member.IsScheduleFiredToday("2026-08-26"));
        Assert.Equal(2, session.Threshold);
    }

    [Fact]
    public async Task LateJoinWithinWindow_TriggersSessionOnlyOncePerDay()
    {
        var (manager, repo, _) = CreateManager();
        var room = await manager.CreateAsync("R1", "pass", "owner", ["uid1"]);
        await manager.JoinAsync("R1", "pass", "uid1", "Alice", "inst1", "conn1");

        var member = room.GetMemberByUid("uid1");
        Assert.NotNull(member);
        member.UpdateDesiredState(new MemberDesiredState(ScheduledOnlineTime: "09:00"));

        var shanghai = new DateTime(2026, 8, 26, 9, 1, 0);
        var session1 = room.TryStartSessionForScheduledMember("uid1", shanghai, TimeSpan.FromMinutes(5));
        Assert.NotNull(session1);

        // 同一天再次检查不应重复触发
        var session2 = room.TryStartSessionForScheduledMember("uid1", shanghai.AddMinutes(1), TimeSpan.FromMinutes(5));
        Assert.Null(session2);

        // 取消当前 session 后，设定新时间可再次触发
        session1.Cancel("test");
        member.UpdateDesiredState(new MemberDesiredState(ScheduledOnlineTime: "09:30"));
        var session3 = room.TryStartSessionForScheduledMember("uid1", shanghai.AddMinutes(30), TimeSpan.FromMinutes(5));
        Assert.NotNull(session3);
    }

    [Fact]
    public async Task OutOfWindow_DoesNotTrigger()
    {
        var (manager, _, _) = CreateManager();
        var room = await manager.CreateAsync("R1", "pass", "owner", ["uid1"]);
        await manager.JoinAsync("R1", "pass", "uid1", "Alice", "inst1", "conn1");

        var member = room.GetMemberByUid("uid1");
        Assert.NotNull(member);
        member.UpdateDesiredState(new MemberDesiredState(ScheduledOnlineTime: "09:00"));

        // 9:06 已超出 5 分钟等待窗口
        var shanghai = new DateTime(2026, 8, 26, 9, 6, 0);
        var session = room.TryStartSessionForScheduledMember("uid1", shanghai, TimeSpan.FromMinutes(5));
        Assert.Null(session);
    }

    [Fact]
    public async Task AllReadyThreshold_ReachesReadyState()
    {
        var (manager, _, _) = CreateManager();
        var room = await manager.CreateAsync("R1", "pass", "owner", ["uid1", "uid2"]);
        await manager.JoinAsync("R1", "pass", "uid1", "Alice", "inst1", "conn1");
        await manager.JoinAsync("R1", "pass", "uid2", "Bob", "inst2", "conn2");

        var m1 = room.GetMemberByUid("uid1")!;
        var m2 = room.GetMemberByUid("uid2")!;
        m1.UpdateDesiredState(new MemberDesiredState(
            ScheduledOnlineTime: "09:00",
            ExpectedHoeingPlayers: 2));
        m2.UpdateDesiredState(new MemberDesiredState(
            ScheduledOnlineTime: "09:00",
            ExpectedHoeingPlayers: 2));

        var shanghai = new DateTime(2026, 8, 26, 9, 1, 0);
        var session = room.TryStartSessionForScheduledMember("uid1", shanghai, TimeSpan.FromMinutes(5));
        Assert.NotNull(session);

        room.ReportMemberOnlineEvent("uid1", 1);
        Assert.Equal(OnlineSessionState.Waiting, session.State);

        room.ReportMemberOnlineEvent("uid2", 2);
        Assert.Equal(OnlineSessionState.Ready, session.State);
    }
}
