using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 清除已联机记录（control.clearOnlineHistory）回归测试。
/// 背景：3046f997 修过"剥 CTRL_ 前缀致清除静默落空"；实机复现"服务器最新仍清不掉"，
/// 疑似重构后回归，本测试锁定服务端清除语义：
/// 1) 基本路径：清除目标成员 OnlineHistory 并广播全量；
/// 2) 网关信封路径：走 Dispatcher + 真实 payload 序列化（camelCase 键核对）；
/// 3) 同 UID 重复条目（重连/多实例幽灵）：清除必须覆盖所有同 UID 条目，
///    不能只清 FirstOrDefault——否则广播里未清的那条会把记录又带回来。
/// </summary>
public class ClearOnlineHistoryTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("C" + Guid.NewGuid().ToString("N")[..8], "pw");

    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static string Group(string room) => $"CTRL_{room}";

    /// <summary>给 conn 对应成员制造一条 OnlineHistory（上线事件 → 消费，与线上 ConsumeOnlineReady 路径一致）。</summary>
    private static void SeedHistory(GatewayTestHarness h, string room, string conn, int generation = 1)
    {
        h.RoomManager.ReportOnlineEvent(Group(room), conn, generation);
        h.RoomManager.ConsumeOnlineReady(Group(room), generation);
    }

    private static int HistoryCount(GatewayTestHarness h, string room, string uid)
        => h.RoomManager.GetControlRoomPlayers(Group(room))
            .Where(p => p.PlayerUid == uid)
            .Sum(p => p.OnlineHistory.Count);

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
    public async Task ClearOnlineHistory_BasicPath_ClearsAndBroadcastsFull()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await h.Ops.JoinControlRoomAsync(Ctx(connA), room, pwd, "uidA", "成员A");
        await h.Ops.JoinControlRoomAsync(Ctx(connB), room, pwd, "uidB", "成员B");
        SeedHistory(h, room, connB);
        Assert.Equal(1, HistoryCount(h, room, "uidB"));

        var updates = CaptureUpdates(h);
        await h.Ops.ClearOnlineHistoryAsync(Ctx(connA), "uidB");

        Assert.Equal(0, HistoryCount(h, room, "uidB"));
        var last = updates[^1];
        Assert.True(last.Full);
        var b = Assert.Single(last.Players!, p => p.PlayerUid == "uidB");
        Assert.Empty(b.OnlineHistory);
    }

    [Fact]
    public async Task ClearOnlineHistory_ViaDispatcherEnvelope_Clears()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await h.Ops.JoinControlRoomAsync(Ctx(connA), room, pwd, "uidA", "成员A");
        await h.Ops.JoinControlRoomAsync(Ctx(connB), room, pwd, "uidB", "成员B");
        SeedHistory(h, room, connB);
        Assert.Equal(1, HistoryCount(h, room, "uidB"));

        // 走网关分发器：hello 握手 → control.clearOnlineHistory（payload 经真实 STJ 序列化）
        var helloResp = await h.Dispatcher.DispatchAsync(Ctx(connA), GatewayTestHarness.HelloEnvelope());
        Assert.Null(GatewayTestHarness.ErrorCode(helloResp));
        var resp = await h.Dispatcher.DispatchAsync(Ctx(connA), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.ControlClearOnlineHistory,
            Payload = GatewayEnvelope.ToPayload(new { targetUid = "uidB" }),
        });

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        Assert.Equal(0, HistoryCount(h, room, "uidB"));
    }

    /// <summary>
    /// 监控模式（isRemote=true）调用方清除成员联机记录。
    /// 背景：监控端不入 _controlRooms（只登记 _remoteControlConnections），
    /// GetControlRoomGroup 反查必落空导致清除静默无效——本用例锁定遥控端回退修复。
    /// </summary>
    [Fact]
    public async Task ClearOnlineHistory_RemoteMonitorCaller_ClearsAndBroadcasts()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connMonitor = Conn("monitor");
        var connB = Conn("b");
        // 监控端先入房（isRemote=true：不加 _controlRooms 成员列表，只登记遥控连接）
        await h.Ops.JoinControlRoomAsync(Ctx(connMonitor), room, pwd, "uidM", "监控端", isRemote: true);
        await h.Ops.JoinControlRoomAsync(Ctx(connB), room, pwd, "uidB", "成员B");
        SeedHistory(h, room, connB);
        Assert.Equal(1, HistoryCount(h, room, "uidB"));

        var updates = CaptureUpdates(h);
        await h.Ops.ClearOnlineHistoryAsync(Ctx(connMonitor), "uidB");

        Assert.Equal(0, HistoryCount(h, room, "uidB"));
        var last = updates[^1];
        Assert.True(last.Full);
        var b = Assert.Single(last.Players!, p => p.PlayerUid == "uidB");
        Assert.Empty(b.OnlineHistory);
    }

    /// <summary>
    /// 失败分支：未入房的陌生连接调 control.clearOnlineHistory 必须返回 error.code=bad_request，
    /// 而不是假 ack——否则客户端在清除未生效时仍提示"已清除"。
    /// </summary>
    [Fact]
    public async Task ClearOnlineHistory_CallerNotInRoom_ReturnsBadRequest()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connB = Conn("b");
        await h.Ops.JoinControlRoomAsync(Ctx(connB), room, pwd, "uidB", "成员B");
        SeedHistory(h, room, connB);
        Assert.Equal(1, HistoryCount(h, room, "uidB"));

        // 陌生连接（从未 JoinControlRoom，监控端反查也落空）走网关分发器调清除
        var stranger = Conn("stranger");
        var helloResp = await h.Dispatcher.DispatchAsync(Ctx(stranger), GatewayTestHarness.HelloEnvelope());
        Assert.Null(GatewayTestHarness.ErrorCode(helloResp));
        var resp = await h.Dispatcher.DispatchAsync(Ctx(stranger), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.ControlClearOnlineHistory,
            Payload = GatewayEnvelope.ToPayload(new { targetUid = "uidB" }),
        });

        Assert.Equal(GatewayProtocol.ErrorCodes.BadRequest, GatewayTestHarness.ErrorCode(resp));
        // 历史必须原样保留（清除未执行）
        Assert.Equal(1, HistoryCount(h, room, "uidB"));
    }

    [Fact]
    public async Task ClearOnlineHistory_DuplicateUidEntries_ClearsAll()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB1 = Conn("b1");
        var connB2 = Conn("b2");
        await h.Ops.JoinControlRoomAsync(Ctx(connA), room, pwd, "uidA", "成员A");
        // 同 UID 两个条目（不同 ClientInstanceId：配置重置/多实例/旧客户端重连产生的幽灵条目）
        await h.Ops.JoinControlRoomAsync(Ctx(connB1), room, pwd, "uidB", "成员B", clientInstanceId: "inst-1");
        await h.Ops.JoinControlRoomAsync(Ctx(connB2), room, pwd, "uidB", "成员B", clientInstanceId: "inst-2");
        // 历史记在活连接条目（列表靠后）上：上线事件按 ConnectionId 匹配，幽灵条目靠前且无历史——
        // FirstOrDefault 清除会清到靠前幽灵条目，活条目历史原样保留并被广播带回客户端
        SeedHistory(h, room, connB2);
        Assert.Equal(1, HistoryCount(h, room, "uidB"));

        var updates = CaptureUpdates(h);
        await h.Ops.ClearOnlineHistoryAsync(Ctx(connA), "uidB");

        // 必须清掉所有同 UID 条目；否则广播中未清条目会把记录带回客户端（upsert 后写覆盖先写）
        Assert.Equal(0, HistoryCount(h, room, "uidB"));
        var last = updates[^1];
        Assert.True(last.Full);
        Assert.All(last.Players!.Where(p => p.PlayerUid == "uidB"), p => Assert.Empty(p.OnlineHistory));
    }
}
