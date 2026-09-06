using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 控制房间带宽优化测试：ControlRoomPlayersUpdated 快照哈希去重 + 增量推送，
/// MemberLogBatch 只发订阅者。
/// 每个测试用随机 roomCode + 唯一 connectionId，避开 ControlRoomAuth/限流表/
/// _connectionGroups 静态表的跨测试污染（风格同 ScreenshotOnDemandTests）。
/// </summary>
public class ControlRoomDeltaBroadcastTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("D" + Guid.NewGuid().ToString("N")[..8], "pw");

    /// <summary>唯一 connectionId（_connectionGroups 是静态表，跨测试复用 connId 会串扰断线清理路径）。</summary>
    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static Task JoinAsync(GatewayTestHarness h, string conn, string room, string pwd, string uid, string name)
        => h.Ops.JoinControlRoomAsync(Ctx(conn), room, pwd, uid, name);

    /// <summary>构造一份状态上报（BgiStatus=running，与加入时默认 unknown 不同 → 首次上报必产生增量）。</summary>
    private static ControlStatus NewStatus(string room, string uid, string name) => new()
    {
        RoomCode = room,
        PlayerUid = uid,
        PlayerName = name,
        BgiStatus = "running",
    };

    /// <summary>捕获本测试内所有 ControlRoomPlayersUpdated 广播的 update 对象（后写的 Setup 覆盖 harness 的默认 Setup）。</summary>
    private static List<ControlRoomPlayersUpdate> CaptureUpdates(GatewayTestHarness h)
    {
        var updates = new List<ControlRoomPlayersUpdate>();
        h.LegacyGroupProxy.Setup(p => p.SendCoreAsync("ControlRoomPlayersUpdated",
                It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) =>
            {
                if (args.Length == 1 && args[0] is ControlRoomPlayersUpdate u) updates.Add(u);
            })
            .Returns(Task.CompletedTask);
        return updates;
    }

    [Fact]
    public async Task IdenticalStatusReport_SecondReport_NoBroadcast()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a1");
        var updates = CaptureUpdates(h);
        await JoinAsync(h, connA, room, pwd, "uidA", "成员A");

        // 同一连接连续两次完全相同的状态上报
        await h.Ops.ReportControlStatusAsync(Ctx(connA), NewStatus(room, "uidA", "成员A"));
        await h.Ops.ReportControlStatusAsync(Ctx(connA), NewStatus(room, "uidA", "成员A"));

        // 仅 2 次广播：加入全量 + 首次上报增量；第二次上报无变化零发送
        Assert.Equal(2, updates.Count);
        Assert.True(updates[0].Full);
        Assert.NotNull(updates[0].Players);
        Assert.False(updates[1].Full);
        var changed = Assert.Single(updates[1].Changed!);
        Assert.Equal("uidA", changed.PlayerUid);
        // revision 从 1 开始单调递增
        Assert.Equal(1, updates[0].Revision);
        Assert.True(updates[1].Revision > updates[0].Revision);
    }

    [Fact]
    public async Task FieldChange_DeltaBroadcast_OnlyChangedMember()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a2");
        var connB = Conn("b2");
        var updates = CaptureUpdates(h);
        await JoinAsync(h, connA, room, pwd, "uidA", "成员A");
        await JoinAsync(h, connB, room, pwd, "uidB", "成员B");
        // 基线上报，刷掉加入后与默认值的差异
        await h.Ops.ReportControlStatusAsync(Ctx(connA), NewStatus(room, "uidA", "成员A"));
        await h.Ops.ReportControlStatusAsync(Ctx(connB), NewStatus(room, "uidB", "成员B"));

        // A 改一个字段：TaskRunning false → true
        var status = NewStatus(room, "uidA", "成员A");
        status.TaskRunning = true;
        status.CurrentTaskName = "测试任务";
        await h.Ops.ReportControlStatusAsync(Ctx(connA), status);

        var last = updates[^1];
        Assert.False(last.Full);
        var changed = Assert.Single(last.Changed!);
        Assert.Equal("uidA", changed.PlayerUid);
        Assert.True(changed.TaskRunning);
        Assert.Empty(last.Removed!);
    }

    [Fact]
    public async Task Disconnect_BroadcastsFull_AndSnapshotSynced()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a3");
        var connB = Conn("b3");
        var updates = CaptureUpdates(h);
        await JoinAsync(h, connA, room, pwd, "uidA", "成员A");
        await JoinAsync(h, connB, room, pwd, "uidB", "成员B");
        var statusA = NewStatus(room, "uidA", "成员A");
        await h.Ops.ReportControlStatusAsync(Ctx(connA), statusA);
        await h.Ops.ReportControlStatusAsync(Ctx(connB), NewStatus(room, "uidB", "成员B"));
        var before = updates.Count;

        // B 断线：标记离线后广播 Full=true
        await h.Ops.HandleDisconnectAsync(Ctx(connB), null);
        Assert.Equal(before + 1, updates.Count);
        Assert.True(updates[^1].Full);

        // 快照已随全量广播同步：A 再上报相同状态 → 无变化零发送
        await h.Ops.ReportControlStatusAsync(Ctx(connA), NewStatus(room, "uidA", "成员A"));
        Assert.Equal(before + 1, updates.Count);
    }

    [Fact]
    public async Task MemberLogBatch_OnlySubscribersReceive()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a4"); // 日志上报者
        var connB = Conn("b4"); // 订阅者
        var connC = Conn("c4"); // 未订阅
        await JoinAsync(h, connA, room, pwd, "uidA", "成员A");
        await JoinAsync(h, connB, room, pwd, "uidB", "成员B");
        await JoinAsync(h, connC, room, pwd, "uidC", "成员C");

        // B 订阅 A 的日志流；C 不订阅；A 未订阅自己
        await h.Ops.SubscribeMemberLogAsync(Ctx(connB), room, "uidA");
        await h.Ops.ReportMemberLogBatchAsync(Ctx(connA), room, "uidA", "成员A", ["line1"], false);

        // 只发订阅者：连接列表恰好只含 B（A 自己/C 均不收）
        h.LegacyHub.Verify(x => x.Clients.Clients(
            It.Is<IReadOnlyList<string>>(l => l.Count == 1 && l[0] == connB)), Times.Once);
        h.LegacyMultiClientProxy.Verify(p => p.SendCoreAsync("MemberLogBatch",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VolatileFieldsOnly_TreatedAsNoChange()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a5");
        var updates = CaptureUpdates(h);
        await JoinAsync(h, connA, room, pwd, "uidA", "成员A");

        // TaskRunning=true 时服务端每次上报都刷新 TaskRunningExpireTime（+60s）与 LastHeartbeat——
        // 这两个易变字段被规范化剔除，内容相同的两连报应视为无变化
        ControlStatus Status() => new()
        {
            RoomCode = room,
            PlayerUid = "uidA",
            PlayerName = "成员A",
            BgiStatus = "running",
            TaskRunning = true,
            CurrentTaskName = "测试任务",
        };
        await h.Ops.ReportControlStatusAsync(Ctx(connA), Status());
        await h.Ops.ReportControlStatusAsync(Ctx(connA), Status());

        // 仅 2 次广播：加入全量 + 首次上报增量；第二次（仅易变字段被服务端刷新）不广播
        Assert.Equal(2, updates.Count);
        Assert.True(updates[0].Full);
        Assert.False(updates[1].Full);
    }
}
