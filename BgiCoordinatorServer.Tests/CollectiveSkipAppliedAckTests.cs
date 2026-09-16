using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 集体跳段 Applied 确认回归测试（collective-skip-applied-ack）。
///
/// 修复目标：原实现只广播一次 RequestSkipToProgress（一次性瞬时事件），服务端无法知道
/// 客户端是否收到、是否真正执行 → 部分成员照旧走旧路线，各打各的、没有统一汇合。
/// 修复后：每次跳段有唯一 SkipId；同一房间至多一个活动跳段（重播同一 SkipId，不新建目标）；
/// 客户端回报 Applied，全部必要成员有结论后才广播 CollectiveSkipAppliedAll。
///
/// 本文件断言：
///   1) 创建：确有落后玩家时创建活动跳段并广播带 skipId 的命令；
///   2) 幂等：活动跳段未结束时再次触发只重播同一 SkipId，不创建第二个；
///   3) 回报：非必要成员/未知 skipId/重复回报被忽略，不推进状态；
///   4) 汇总：部分回报不广播，全部有结论才广播 CollectiveSkipAppliedAll 并清除活动跳段；
///   5) 失败：失败者不阻塞放行，也不被当作已跳段；
///   6) 超时：重播到上限后清除活动跳段（不新增停止触发）。
/// </summary>
public class CollectiveSkipAppliedAckTests
{
    private static PlayerInfo OnlinePlayer(string connId, string uid, long currentProgress) => new()
    {
        ConnectionId = connId,
        PlayerId = connId,
        PlayerName = uid,
        PlayerUid = uid,
        Status = PlayerStatus.Pathing,
        LastHeartbeat = DateTime.UtcNow,
        CurrentProgress = currentProgress,
    };

    /// <summary>
    /// 造一个"集体卡死 + 一名落后玩家"的房间：
    ///   conn-1/conn-2 各自停在不同同步点（CurrentProgress=2000000，均已在 ArrivalSet 中），
    ///   conn-3 未到任何同步点且进度更小（1000000）→ 唯一"落后玩家"，
    ///   且 C1（等待人数≥⌈在线×0.5⌉）、C2（所有集合都不满足放行）、C3（快照稳定 10s）全部成立。
    /// </summary>
    private static (GatewayTestHarness Harness, string RoomCode, Room Room) StuckRoom(int laggingCount = 1)
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom("conn-1", "Host", null, "uid-1", 2 + laggingCount);
        h.RoomManager.AddPlayerForTesting(roomCode, OnlinePlayer("conn-2", "uid-2", 2_000_000));
        for (int i = 0; i < laggingCount; i++)
            h.RoomManager.AddPlayerForTesting(roomCode, OnlinePlayer($"conn-{3 + i}", $"uid-{3 + i}", 1_000_000));

        var room = h.RoomManager.GetRoom(roomCode)!;
        room.HostConnectionId = "conn-1";
        room.HostConfig = new RoomConfig
        {
            EnableMutualWaitCollectiveSkip = true,
            MutualWaitMinWaitersRatio = 0.5,
            MutualWaitStableSeconds = 5,
            MaxConsecutiveCollectiveSkips = 3,
        };
        room.Players.First(p => p.ConnectionId == "conn-1").CurrentProgress = 2_000_000;

        // 两个不同的等待点，且都不满足放行（都要等 conn-3）
        room.ArrivalSets["syncA"] = ["conn-1"];
        room.ArrivalSets["syncB"] = ["conn-2"];
        room.ArrivalSetProgress["syncA"] = 2_000_000;
        room.ArrivalSetProgress["syncB"] = 2_000_000;
        room.LastArrivalSetsSnapshot = room.ArrivalSets.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        room.ObservationStartTime = DateTime.UtcNow.AddSeconds(-10);

        return (h, roomCode, room);
    }

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    /// <summary>从 mock 的组广播里取出指定事件名的 evt 载荷（skipId / targetProgress）。</summary>
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

    private static string PayloadString(GatewayEnvelope env, string key)
        => env.Payload?[key]?.GetValue<string>() ?? "";

    private static long PayloadLong(GatewayEnvelope env, string key)
        => env.Payload?[key]?.GetValue<long>() ?? long.MinValue;

    // =========================================================================
    // 1) 创建 + 幂等：同一房间至多一个活动跳段
    // =========================================================================

    [Fact]
    public async Task StuckDetection_CreatesSingleActiveSkip_AndBroadcastsSkipId()
    {
        var (h, roomCode, room) = StuckRoom();

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        var active = room.ActiveCollectiveSkip;
        Assert.NotNull(active);
        Assert.Equal(3_000_000, active!.TargetProgress);
        Assert.Equal(CollectiveSkipPhase.Requested, active.Phase);
        Assert.Equal(1, active.BroadcastCount);
        Assert.Equal(["conn-3"], active.RequiredConnectionIds);
        Assert.Empty(active.AppliedConnectionIds);

        // 落后玩家被标记异常（保持既有语义）
        var lagging = room.Players.First(p => p.ConnectionId == "conn-3");
        Assert.True(lagging.IsAbnormal);
        Assert.Equal(3_000_000, lagging.TargetProgress);

        // 广播了带 skipId 的跳段命令；旧名参数仍是 targetProgress（旧客户端零感知）
        var evt = Assert.Single(GroupEvents(h, "sync.requestSkipToProgress"));
        Assert.Equal(active.SkipId, PayloadString(evt, "skipId"));
        Assert.Equal(3_000_000, PayloadLong(evt, "targetProgress"));
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "RequestSkipToProgress",
            It.Is<object?[]>(a => a.Length == 1 && (long)a[0]! == 3_000_000),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StuckDetection_WhileActiveSkipPending_RebroadcastsSameSkipId_NeverCreatesSecond()
    {
        var (h, roomCode, room) = StuckRoom();

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);
        var firstSkipId = room.ActiveCollectiveSkip!.SkipId;

        // 再次触发（模拟既有 CollectiveSkipTimer 二次到期）
        room.LastArrivalSetsSnapshot = room.ArrivalSets.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        room.ObservationStartTime = DateTime.UtcNow.AddSeconds(-10);
        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        var active = room.ActiveCollectiveSkip;
        Assert.NotNull(active);
        Assert.Equal(firstSkipId, active!.SkipId);   // 同一 SkipId，不新建目标
        Assert.Equal(2, active.BroadcastCount);

        var events = GroupEvents(h, "sync.requestSkipToProgress");
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(firstSkipId, PayloadString(e, "skipId")));
    }

    // =========================================================================
    // 2) 回报：校验与幂等
    // =========================================================================

    private static async Task<(GatewayTestHarness Harness, string RoomCode, Room Room, string SkipId)> RoomWithActiveSkipAsync()
    {
        var (h, roomCode, room) = StuckRoom();
        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);
        return (h, roomCode, room, room.ActiveCollectiveSkip!.SkipId);
    }

    [Fact]
    public async Task ReportApplied_UnknownSkipId_Ignored()
    {
        var (h, roomCode, room, _) = await RoomWithActiveSkipAsync();

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), "stale-skip-id", 3_000_000, true, "");

        Assert.NotNull(room.ActiveCollectiveSkip);
        Assert.Empty(room.ActiveCollectiveSkip!.AppliedConnectionIds);
        Assert.Empty(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    [Fact]
    public async Task ReportApplied_FromNonRequiredMember_Ignored()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();

        // conn-1 不在 Required（它在 ArrivalSet 里，不是落后玩家）
        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-1"), skipId, 3_000_000, true, "");

        Assert.NotNull(room.ActiveCollectiveSkip);
        Assert.Empty(room.ActiveCollectiveSkip!.AppliedConnectionIds);
        Assert.Empty(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    [Fact]
    public async Task ReportApplied_DuplicateFromSameMember_CountedOnce()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), skipId, 2_100_000, true, "");
        // 已全部有结论 → 状态被清除；再回报一次应被忽略且不重复广播
        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), skipId, 2_100_000, true, "");

        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Single(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    [Fact]
    public async Task ReportApplied_NoActiveSkip_Ignored()
    {
        var (h, roomCode, room) = StuckRoom();

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), "any-skip", 3_000_000, true, "");

        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Empty(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    // =========================================================================
    // 3) 汇总：部分回报不广播，全部有结论才广播
    // =========================================================================

    [Fact]
    public async Task ReportApplied_PartialMembers_DoesNotBroadcastAppliedAll()
    {
        // 两名落后玩家（conn-3/conn-4）→ Required 两人，用于验证"部分回报不放行"
        var (h, roomCode, room) = StuckRoom(laggingCount: 2);

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);
        var active = room.ActiveCollectiveSkip!;
        Assert.Equal(2, active.RequiredConnectionIds.Count);

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), active.SkipId, 3_000_000, true, "");

        Assert.NotNull(room.ActiveCollectiveSkip);
        Assert.Equal(CollectiveSkipPhase.Requested, room.ActiveCollectiveSkip!.Phase);
        Assert.Empty(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    [Fact]
    public async Task ReportApplied_AllRequiredMembersResolved_BroadcastsAppliedAllAndClears()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), skipId, 2_100_000, true, "");

        Assert.Null(room.ActiveCollectiveSkip);
        var evt = Assert.Single(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
        Assert.Equal(skipId, PayloadString(evt, "skipId"));
        Assert.Equal(3_000_000, PayloadLong(evt, "targetProgress"));
    }

    [Fact]
    public async Task ReportApplied_FailureResolvesMember_WithoutBeingTreatedAsApplied()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();

        await h.Ops.ReportCollectiveSkipAppliedAsync(Ctx("conn-3"), skipId, -1, false, "target-not-found");

        // 失败者也"有结论"：不阻塞其余成员放行、广播 AppliedAll，但绝不进入 AppliedConnectionIds
        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Single(GroupEvents(h, "sync.collectiveSkipAppliedAll"));
    }

    // =========================================================================
    // 4) 超时：有界重播，耗尽后清除（不新增停止触发）
    // =========================================================================

    [Fact]
    public async Task ApplyTimeout_UnderLimit_RebroadcastsSameSkipId()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();
        var beforeCount = room.ActiveCollectiveSkip!.BroadcastCount;

        await h.Ops.EvaluateCollectiveSkipApplyTimeoutAsync(room, roomCode);

        Assert.Equal(skipId, room.ActiveCollectiveSkip!.SkipId);
        Assert.Equal(beforeCount + 1, room.ActiveCollectiveSkip.BroadcastCount);
        // 首次创建 1 次 + 超时重播 1 次
        Assert.Equal(2, GroupEvents(h, "sync.requestSkipToProgress").Count);
    }

    [Fact]
    public async Task ApplyTimeout_Exhausted_ClearsActiveSkip_WithoutStopBroadcast()
    {
        var (h, roomCode, room, _) = await RoomWithActiveSkipAsync();
        room.ActiveCollectiveSkip!.BroadcastCount = 3;   // 已达上限

        await h.Ops.EvaluateCollectiveSkipApplyTimeoutAsync(room, roomCode);

        Assert.Null(room.ActiveCollectiveSkip);
        // 交回既有超时/降级路径：不在此新增 CollectiveSkipDegraded（避免误停本可自愈的会话）
        Assert.Empty(GroupEvents(h, "sync.collectiveSkipDegraded"));
        Assert.Single(GroupEvents(h, "sync.requestSkipToProgress"));
    }

    [Fact]
    public async Task ApplyTimeout_AllRequiredResolved_ClearsWithoutRebroadcast()
    {
        var (h, roomCode, room, skipId) = await RoomWithActiveSkipAsync();
        room.ActiveCollectiveSkip!.AppliedConnectionIds.Add("conn-3");

        await h.Ops.EvaluateCollectiveSkipApplyTimeoutAsync(room, roomCode);

        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Single(GroupEvents(h, "sync.requestSkipToProgress"));   // 不重播
    }

    // =========================================================================
    // 5) 网关路由：新命令已注册且可端到端调用
    // =========================================================================

    [Fact]
    public async Task Gateway_RouteRegistered_ForReportCollectiveSkipApplied()
    {
        var h = new GatewayTestHarness();
        Assert.Contains(GatewayProtocol.Names.SyncReportCollectiveSkipApplied, h.Dispatcher.RegisteredNames);

        var (harness, roomCode, room, skipId) = await RoomWithActiveSkipAsync();
        var env = new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.SyncReportCollectiveSkipApplied,
            Payload = GatewayEnvelope.ToPayload(new
            {
                skipId,
                actualProgress = 2_100_000L,
                success = true,
                reason = "",
            }),
        };

        await harness.Dispatcher.DispatchAsync(GatewayHandlerContext.V3("conn-3"), GatewayTestHarness.HelloEnvelope());
        var resp = await harness.Dispatcher.DispatchAsync(GatewayHandlerContext.V3("conn-3"), env);

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Single(GroupEvents(harness, "sync.collectiveSkipAppliedAll"));
    }

    // =========================================================================
    // 7) 阶段 6 隔离：route-anchor 激活时旧集体跳段不再推进
    // =========================================================================

    [Fact]
    public async Task AnchorModeEnabled_CollectiveSkipDoesNotEngage()
    {
        var (h, roomCode, room) = StuckRoom();
        // 房主开启路线边界锚点：路线推进权归锚点，旧集体跳段必须完全退出
        room.HostConfig!.EnableRouteAnchor = true;

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Empty(GroupEvents(h, "sync.requestSkipToProgress"));
        Assert.Empty(GroupEvents(h, "sync.allArrived"));
        // 也不得把落后玩家标记为异常（那是旧机制的副作用）
        Assert.False(room.Players.First(p => p.ConnectionId == "conn-3").IsAbnormal);
    }

    [Fact]
    public async Task AnchorModeEnabled_PiggybackDoesNotArmCollectiveSkipTimer()
    {
        var (h, roomCode, room) = StuckRoom();
        room.HostConfig!.EnableRouteAnchor = true;

        // piggyback 通过公开入口间接触发：直接调用内部方法验证不武装定时器
        await h.Ops.EvaluateCollectiveStuckPiggybackAsync(room, roomCode);

        Assert.Null(room.CollectiveSkipTimer);
    }

    [Fact]
    public async Task AnchorModeEnabled_AppliedTimeoutRebroadcastIsSuppressed()
    {
        var (h, roomCode, room) = StuckRoom();
        // 先按旧模式造出一个活动跳段，再打开锚点模式：在途命令不得继续重播
        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);
        Assert.NotNull(room.ActiveCollectiveSkip);
        var before = GroupEvents(h, "sync.requestSkipToProgress").Count;

        room.HostConfig!.EnableRouteAnchor = true;
        await h.Ops.EvaluateCollectiveSkipApplyTimeoutAsync(room, roomCode);

        Assert.Equal(before, GroupEvents(h, "sync.requestSkipToProgress").Count);
    }

    // =========================================================================
    // 6) 轮次隔离：新轮次不继承旧 SkipId
    // =========================================================================

    [Fact]
    public async Task ResetForNewWorldRound_ClearsActiveSkip()
    {
        var (h, roomCode, room, _) = await RoomWithActiveSkipAsync();
        Assert.NotNull(room.ActiveCollectiveSkip);

        await h.Ops.ResetForNewWorldRoundAsync(Ctx("conn-1"), 1);

        Assert.Null(room.ActiveCollectiveSkip);
    }
}
