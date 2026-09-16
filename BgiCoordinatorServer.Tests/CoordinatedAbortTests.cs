using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 协同中止房间级协议回归测试（hoeing-multiplayer-coordinated-abort-restart）。
///
/// 目标：任一成员触发真异常中止时上报 ReportCoordinatedAbortAsync，服务端校验调用连接
/// 确为房内玩家（reporterUid 防冒名）后做房间级幂等广播 sync.coordinatedAborted（首报 wins，
/// 多端近同时上报只广播一次，防风暴），让全组秒级同步收口。
/// 全员强制升级，无旧协议兼容：只走 gateway evt 信封通道（BroadcastGroupEventOnlyAsync）。
///
/// 本文件断言：
///   1) 房内成员上报 → GatewayGroupProxy 收到 evt sync.coordinatedAborted（payload reason/reporterUid 正确）；
///   2) 重复上报 → 只广播一次（幂等，首报 wins）；
///   3) 非房间连接上报 → 零广播；
///   4) reporterUid 与调用连接玩家 uid 不一致 → 零广播（防冒名）。
///   5) reporterUid=null（客户端未填 UID）→ 跳过冒名校验，正常广播。
/// </summary>
public class CoordinatedAbortTests
{
    private static PlayerInfo OnlinePlayer(string connId, string uid) => new()
    {
        ConnectionId = connId,
        PlayerId = connId,
        PlayerName = uid,
        PlayerUid = uid,
        Status = PlayerStatus.Pathing,
        LastHeartbeat = DateTime.UtcNow,
        CurrentProgress = 1_000_000,
    };

    /// <summary>造一个双人房间：conn-1（房主，uid-1）+ conn-2（uid-2）。</summary>
    private static (GatewayTestHarness Harness, string RoomCode) TwoPlayerRoom()
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom("conn-1", "Host", null, "uid-1", 2);
        h.RoomManager.AddPlayerForTesting(roomCode, OnlinePlayer("conn-2", "uid-2"));
        return (h, roomCode);
    }

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    /// <summary>从 mock 的组广播里取出指定事件名的 evt 载荷。</summary>
    private static List<GatewayEnvelope> GroupEvents(GatewayTestHarness h, string evtName)
    {
        var result = new List<GatewayEnvelope>();
        foreach (var invocation in h.GatewayGroupProxy.Invocations)
        {
            if (invocation.Method.Name != nameof(IClientProxy.SendCoreAsync)) continue;
            if (invocation.Arguments.Count < 2) continue;
            if (invocation.Arguments[0] as string != GatewayProtocol.Callbacks.Event) continue;
            // SendAsync 扩展方法把参数装进 object?[] 后调 SendCoreAsync，故此处需再取第 0 项
            if (invocation.Arguments[1] is not object?[] args || args.Length == 0) continue;
            if (args[0] is not GatewayEnvelope env) continue;
            if (env.Name == evtName) result.Add(env);
        }
        return result;
    }

    private static string? PayloadString(GatewayEnvelope env, string key)
        => env.Payload?[key]?.GetValue<string>();

    // =========================================================================
    // 1) 房内成员上报 → evt 广播一次，payload 正确
    // =========================================================================

    [Fact]
    public async Task ReportAbort_FromRoomMember_BroadcastsEvt()
    {
        var (h, roomCode) = TwoPlayerRoom();

        await h.Ops.ReportCoordinatedAbortAsync(Ctx("conn-2"), "锚点未放行", "uid-2");

        var evt = Assert.Single(GroupEvents(h, GatewayProtocol.Events.SyncCoordinatedAborted));
        Assert.Equal("锚点未放行", PayloadString(evt, "reason"));
        Assert.Equal("uid-2", PayloadString(evt, "reporterUid"));
        Assert.Equal(roomCode, evt.RoomCode);
        Assert.True(h.RoomManager.GetRoom(roomCode)!.CoordinatedAbortBroadcasted);
    }

    // =========================================================================
    // 2) 幂等：多端近同时上报只广播一次（首报 wins）
    // =========================================================================

    [Fact]
    public async Task ReportAbort_DuplicateReports_BroadcastsOnlyOnce()
    {
        var (h, roomCode) = TwoPlayerRoom();

        await h.Ops.ReportCoordinatedAbortAsync(Ctx("conn-1"), "掉出房间", "uid-1");
        await h.Ops.ReportCoordinatedAbortAsync(Ctx("conn-2"), "成员 Offline", "uid-2");

        var events = GroupEvents(h, GatewayProtocol.Events.SyncCoordinatedAborted);
        Assert.Single(events);
        // 首报 wins：payload 保留第一次上报的内容
        Assert.Equal("掉出房间", PayloadString(events[0], "reason"));
        Assert.Equal("uid-1", PayloadString(events[0], "reporterUid"));
    }

    // =========================================================================
    // 3) 非房间连接上报 → 零广播
    // =========================================================================

    [Fact]
    public async Task ReportAbort_FromNonRoomConnection_Ignored()
    {
        var (h, roomCode) = TwoPlayerRoom();

        await h.Ops.ReportCoordinatedAbortAsync(Ctx("stranger-conn"), "掉出房间", "uid-x");

        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.SyncCoordinatedAborted));
        Assert.False(h.RoomManager.GetRoom(roomCode)!.CoordinatedAbortBroadcasted);
    }

    // =========================================================================
    // 4) reporterUid 冒名 → 零广播
    // =========================================================================

    [Fact]
    public async Task ReportAbort_ReporterUidMismatch_Ignored()
    {
        var (h, roomCode) = TwoPlayerRoom();

        // conn-2 以 uid-1（房主）身份上报 → 鉴权失败
        await h.Ops.ReportCoordinatedAbortAsync(Ctx("conn-2"), "掉出房间", "uid-1");

        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.SyncCoordinatedAborted));
        Assert.False(h.RoomManager.GetRoom(roomCode)!.CoordinatedAbortBroadcasted);
    }

    // =========================================================================
    // 5) reporterUid=null（客户端未填 UID 的真实形态）→ 跳过冒名校验，正常广播
    // =========================================================================

    [Fact]
    public async Task ReportAbort_NullReporterUid_SkipsAuthAndBroadcasts()
    {
        var (h, roomCode) = TwoPlayerRoom();

        await h.Ops.ReportCoordinatedAbortAsync(Ctx("conn-2"), "锚点未放行", null);

        var evt = Assert.Single(GroupEvents(h, GatewayProtocol.Events.SyncCoordinatedAborted));
        Assert.Equal("锚点未放行", PayloadString(evt, "reason"));
        Assert.True(h.RoomManager.GetRoom(roomCode)!.CoordinatedAbortBroadcasted);
    }
}
