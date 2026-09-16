using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 路线边界锚点会话测试（route-anchor · 阶段 2 服务端部分）。
///
/// 覆盖正文第 11.1 节（服务端）与第 12 节（阶段 2）要求：
///   · 同房间同一时刻只有一个活动锚点；同边界重复 enroll 幂等、不同边界被拒
///   · 参与者按 UID 冻结；非参与者被拒
///   · 报告必须绑定当前锚点、代际与边界索引（禁止客户端自报推进）
///   · Ready ⇐ Reported；未全员 Ready 不放行
///   · 全员 Ready 才放行，并广播 Released 事件
///   · 终态幂等、终态后可开新边界
///   · 轮次重置清空锚点
/// </summary>
public class RouteAnchorSessionTests
{
    private const string Session = "S1";
    private const int Epoch = 1;
    private const string Plan = "plan-1";
    private const int Boundary = 12;
    private const int Next = 13;

    private static PlayerInfo Online(string connId, string uid) => new()
    {
        ConnectionId = connId,
        PlayerId = connId,
        PlayerName = uid,
        PlayerUid = uid,
        Status = PlayerStatus.Pathing,
        LastHeartbeat = DateTime.UtcNow,
    };

    /// <summary>4 人在线房间：conn-1 为房主（CreateRoom 自带），conn-2/3/4 补入。</summary>
    private static (GatewayTestHarness Harness, string RoomCode, Room Room) Room4(bool allOnline = true)
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom("conn-1", "Host", null, "uid-1", 4);
        foreach (var i in new[] { 2, 3, 4 })
        {
            var p = Online($"conn-{i}", $"uid-{i}");
            if (!allOnline && i == 4) p.LastHeartbeat = DateTime.UtcNow.AddMinutes(-10); // 离线
            h.RoomManager.AddPlayerForTesting(roomCode, p);
        }
        // 房主配置：route-anchor 默认关闭，测试路径显式开启（门控见 RouteAnchorSessionTests 配置门控用例）
        var room = h.RoomManager.GetRoom(roomCode)!;
        room.HostConnectionId = "conn-1";
        room.HostConfig = new RoomConfig { EnableRouteAnchor = true };
        return (h, roomCode, room);
    }

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static RouteAnchorSnapshot SnapshotOf(object result)
    {
        var prop = result.GetType().GetProperty("anchor");
        Assert.NotNull(prop);
        return (RouteAnchorSnapshot)prop!.GetValue(result)!;
    }

    private static string ErrorMessageOf(object result)
    {
        var errProp = result.GetType().GetProperty("error");
        Assert.NotNull(errProp);
        var err = errProp!.GetValue(result)!;
        return err.GetType().GetProperty("message")!.GetValue(err)!.ToString()!;
    }

    private static bool HasError(object result) => result.GetType().GetProperty("error") != null;

    /// <summary>从网关组广播里取出指定 evt 名的信封（evt-only 广播走 SendCoreAsync("evt", [env], ct)）。</summary>
    private static List<GatewayEnvelope> GroupEvents(GatewayTestHarness h, string evtName)
    {
        var result = new List<GatewayEnvelope>();
        foreach (var invocation in h.GatewayGroupProxy.Invocations)
        {
            if (invocation.Method.Name != nameof(IClientProxy.SendCoreAsync)) continue;
            if (invocation.Arguments.Count < 2) continue;
            if (invocation.Arguments[0] as string != GatewayProtocol.Callbacks.Event) continue;
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

    private static async Task<RouteAnchorSnapshot> EnrollAsync(GatewayTestHarness h, string conn)
    {
        var result = await h.Ops.EnrollRouteAnchorAsync(Ctx(conn), Session, Epoch, Plan, Boundary, Next);
        // 注意：Assert 的消息参数会被提前求值，故这里必须先判断再取消息
        if (HasError(result)) Assert.Fail($"enroll 不应失败：{ErrorMessageOf(result)}");
        return SnapshotOf(result);
    }

    /// <summary>服务端权威会话身份：客户端不生成，只回带（从活动锚点读取）。</summary>
    private static (string Session, int Epoch) ServerIdentity(Room room)
    {
        var anchor = room.ActiveRouteAnchor!;
        return (anchor.SessionId, anchor.WorldEpoch);
    }

    /// <summary>
    /// 提交边界报告。boundary 缺省取"当前活动锚点描述的边界"——
    /// 多边界连续场景下若固定用常量 12 会被服务端以 WrongRouteIndex 正确拒绝。
    /// </summary>
    private static async Task<RouteAnchorSnapshot> ReportAsync(
        GatewayTestHarness h, Room room, string conn, string anchorId, int? boundary = null)
    {
        var (session, epoch) = ServerIdentity(room);
        var reported = boundary ?? room.ActiveRouteAnchor!.CompletedRouteIndex;
        var result = await h.Ops.ReportRouteAnchorAsync(
            Ctx(conn), anchorId, session, epoch, Plan, reported, "completed");
        return SnapshotOf(result);
    }

    private static async Task<object> ArriveAsync(GatewayTestHarness h, Room room, string conn, string anchorId)
    {
        var (session, epoch) = ServerIdentity(room);
        return await h.Ops.ArriveRouteAnchorAsync(Ctx(conn), anchorId, session, epoch, Plan);
    }

    // =========================================================================
    // 创建与参与者冻结
    // =========================================================================

    [Fact]
    public async Task Enroll_CreatesAnchor_WithFrozenOnlineParticipants()
    {
        var (h, roomCode, room) = Room4();

        var snapshot = await EnrollAsync(h, "conn-1");

        Assert.True(snapshot.HasAnchor);
        Assert.Equal(RouteAnchorPhase.Collecting.ToString(), snapshot.Phase);
        Assert.Equal(Boundary, snapshot.CompletedRouteIndex);
        Assert.Equal(Next, snapshot.NextRouteIndex);
        Assert.Equal(4, snapshot.Participants.Count);
        Assert.Equal("Pathing", snapshot.MyState);
        Assert.Contains("uid-1", snapshot.Participants);
        Assert.Equal(room.SessionId, snapshot.SessionId);
        Assert.False(snapshot.Released);
        Assert.False(snapshot.Stopped);
        Assert.NotNull(room.ActiveRouteAnchor);
    }

    [Fact]
    public async Task Enroll_SecondMember_SameBoundary_JoinsSameAnchor_WithoutNewAnchor()
    {
        var (h, roomCode, room) = Room4();

        var first = await EnrollAsync(h, "conn-1");
        var second = await EnrollAsync(h, "conn-2");

        Assert.Equal(first.AnchorId, second.AnchorId);
        Assert.Equal(1, room.RouteAnchorSequence);
    }

    [Fact]
    public async Task Enroll_DifferentBoundary_WhileActive_IsRejected()
    {
        var (h, roomCode, room) = Room4();
        await EnrollAsync(h, "conn-1");

        // 另一个边界（13→14）在活动锚点未结束时不得创建
        var result = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-2"), Session, Epoch, Plan, 13, 14);

        Assert.True(HasError(result));
        Assert.Contains("anchor_in_progress", ErrorMessageOf(result));
        Assert.Equal(Boundary, room.ActiveRouteAnchor!.CompletedRouteIndex);
    }

    [Fact]
    public async Task Enroll_MissingIdentity_IsRejected()
    {
        var (h, _, room) = Room4();

        var result = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), "", Epoch, "", Boundary, Next);

        Assert.True(HasError(result));
        Assert.Contains("missing_identity", ErrorMessageOf(result));
    }

    [Fact]
    public async Task Enroll_OfflineMemberIsNotParticipant_AndCannotReport()
    {
        var (h, roomCode, room) = Room4(allOnline: false);

        var snapshot = await EnrollAsync(h, "conn-1");
        Assert.Equal(3, snapshot.Participants.Count);           // conn-4 离线 → 不在参与者内
        Assert.DoesNotContain("uid-4", snapshot.Participants);

        // 该成员随后上线也无法报告（锚点已离开 Collecting 后不予补入；此处先由他人报告推进阶段）
        await ReportAsync(h, room, "conn-2", snapshot.AnchorId);
        var (session, epoch) = ServerIdentity(room);
        var result = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-4"), snapshot.AnchorId, session, epoch, Plan, Boundary, "completed");

        Assert.True(HasError(result));
        Assert.Contains("NotParticipant", ErrorMessageOf(result));
    }

    // =========================================================================
    // 报告校验（服务端权威）
    // =========================================================================

    [Fact]
    public async Task Report_WrongAnchorId_IsRejected()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");

        var (session, epoch) = ServerIdentity(room);
        var result = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-2"), "route-boundary:bogus:1:plan-1:12:9", session, epoch, Plan, Boundary, "completed");

        Assert.True(HasError(result));
        Assert.Contains("NotSameAnchor", ErrorMessageOf(result));
        Assert.NotNull(snapshot.AnchorId);
    }

    [Fact]
    public async Task Report_ClientSelfReportedWrongRouteIndex_IsRejected()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");

        // 客户端谎报已完成 13 想把自己推到下一条路线
        var (session, epoch) = ServerIdentity(room);
        var result = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-2"), snapshot.AnchorId, session, epoch, Plan, 13, "completed");

        Assert.True(HasError(result));
        Assert.Contains("WrongRouteIndex", ErrorMessageOf(result));
        Assert.False(room.ActiveRouteAnchor!.MemberStates.TryGetValue("uid-2", out var s)
                     && s == RouteAnchorMemberState.Reported);
    }

    [Fact]
    public async Task Report_StaleGeneration_IsRejected()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");

        var result = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-2"), snapshot.AnchorId, "S0", Epoch, Plan, Boundary, "completed");

        Assert.True(HasError(result));
        Assert.Contains("StaleGeneration", ErrorMessageOf(result));
    }

    [Fact]
    public async Task Report_Duplicate_IsRejected_ButFirstAccepted()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");

        var first = await ReportAsync(h, room, "conn-2", snapshot.AnchorId);
        Assert.Equal("Reported", first.MemberStates["uid-2"]);

        var (session2, epoch2) = ServerIdentity(room);
        var second = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-2"), snapshot.AnchorId, session2, epoch2, Plan, Boundary, "completed");

        Assert.True(HasError(second));
        Assert.Contains("Duplicate", ErrorMessageOf(second));
        Assert.Equal(RouteAnchorMemberState.Reported, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
    }

    // =========================================================================
    // Ready 与放行
    // =========================================================================

    [Fact]
    public async Task Arrive_WithoutReport_IsRejected()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");

        var result = await ArriveAsync(h, room, "conn-1", snapshot.AnchorId);

        Assert.True(HasError(result));
        Assert.Contains("report_required", ErrorMessageOf(result));
    }

    [Fact]
    public async Task AllMembersReportAndArrive_Releases_AndBroadcastsReleasedEvent()
    {
        var (h, _, room) = Room4();
        var snapshot = await EnrollAsync(h, "conn-1");
        var anchorId = snapshot.AnchorId;

        // 全员先报告边界（此时尚无人 Ready）→ 阶段应为 WaitingArrival
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
        }
        Assert.Equal(RouteAnchorPhase.WaitingArrival, room.ActiveRouteAnchor!.Phase);

        // 3 人就绪：仍不放行
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3" })
        {
            var arrive = await ArriveAsync(h, room, conn, anchorId);
            Assert.False(HasError(arrive));
        }
        Assert.Equal(RouteAnchorPhase.WaitingArrival, room.ActiveRouteAnchor!.Phase);
        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.RouteAnchorReleased));

        // 第 4 人就绪 → 放行
        var last = SnapshotOf(await ArriveAsync(h, room, "conn-4", anchorId));

        Assert.True(last.Released);
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);

        var evt = Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorReleased));
        Assert.Equal(anchorId, PayloadString(evt, "anchorId"));
        Assert.Equal(Next, PayloadLong(evt, "nextRouteIndex"));
        Assert.Equal(Boundary, PayloadLong(evt, "completedRouteIndex"));
        Assert.Equal(room.SessionId, PayloadString(evt, "sessionId"));   // 会话身份由服务端生成
        Assert.Equal(Plan, PayloadString(evt, "planId"));
    }

    [Fact]
    public async Task ReleasedPhase_IsIdempotent_ForFurtherArrivals()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }

        var again = await ArriveAsync(h, room, "conn-1", anchorId);

        Assert.False(HasError(again));
        Assert.True(SnapshotOf(again).Released);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorReleased));   // 不重复广播
    }

    [Fact]
    public async Task AfterRelease_NewBoundaryEnroll_CreatesNewAnchorWithNewId()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }

        var next = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, 13, 14);
        var snapshot = SnapshotOf(next);

        Assert.NotEqual(anchorId, snapshot.AnchorId);
        Assert.Equal(13, snapshot.CompletedRouteIndex);
        Assert.Equal(RouteAnchorPhase.Collecting.ToString(), snapshot.Phase);
        Assert.Equal(2, room.RouteAnchorSequence);
    }

    // =========================================================================
    // 能力门控（禁止半启用）
    // =========================================================================

    [Fact]
    public async Task Enroll_Rejected_WhenAnyOnlineMemberLacksCapability()
    {
        var (h, _, room) = Room4();
        // 只有 conn-1 宣告了能力：其余成员未宣告 → 禁止激活
        h.Capabilities.AllowAll = false;
        h.Capabilities.SupportedConnections.Add("conn-1");

        var result = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, Boundary, Next);

        Assert.True(HasError(result));
        Assert.Contains("capability_required", ErrorMessageOf(result));
        Assert.Null(room.ActiveRouteAnchor);
    }

    [Fact]
    public async Task Enroll_Accepted_WhenAllOnlineMembersDeclareCapability()
    {
        var (h, _, room) = Room4();
        h.Capabilities.AllowAll = false;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
            h.Capabilities.SupportedConnections.Add(conn);

        var snapshot = await EnrollAsync(h, "conn-1");

        Assert.True(snapshot.HasAnchor);
        Assert.Equal(4, snapshot.Participants.Count);
    }

    [Fact]
    public async Task Enroll_Rejected_WhenHostConfigDisabledOrAbsent()
    {
        var (h, _, room) = Room4();
        room.HostConfig = new RoomConfig { EnableRouteAnchor = false };

        var disabled = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, Boundary, Next);
        Assert.True(HasError(disabled));
        Assert.Contains("config_disabled", ErrorMessageOf(disabled));

        // 旧客户端从不发送该字段 → 等同于关闭（零影响）
        room.HostConfig = new RoomConfig();
        var defaultOff = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, Boundary, Next);
        Assert.True(HasError(defaultOff));
        Assert.Contains("config_disabled", ErrorMessageOf(defaultOff));

        // 房主配置缺失
        room.HostConfig = null;
        var noConfig = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, Boundary, Next);
        Assert.True(HasError(noConfig));

        Assert.Null(room.ActiveRouteAnchor);
    }

    [Fact]
    public void HostConfig_EnableRouteAnchor_DefaultsToFalse()
    {
        // 默认关闭是新机制的发布纪律（方案第 10 章）：未实机验证前不得默认开启
        // 服务端侧默认关闭（客户端侧同名字段的默认值在客户端测试工程断言，此处不跨工程引用）
        Assert.False(new RoomConfig().EnableRouteAnchor);
    }

    [Fact]
    public void ServerCapabilities_AdvertiseRouteAnchor()
    {
        Assert.Contains(
            BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability,
            GatewayProtocol.ServerCapabilities);
    }

    /// <summary>让全部 4 名参与者走完一次边界收口（Report + Ready）。</summary>
    private static async Task CompleteBoundaryAsync(GatewayTestHarness h, Room room, string anchorId)
    {
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            var arrive = await ArriveAsync(h, room, conn, anchorId);
            Assert.False(HasError(arrive));
        }
    }

    // =========================================================================
    // 阶段 3：Released 门控的连续边界不变量
    // =========================================================================

    [Fact]
    public async Task ConsecutiveBoundaries_NextCannotStartBeforePreviousReleased()
    {
        var (h, _, room) = Room4();

        var boundary12 = await EnrollAsync(h, "conn-1");
        Assert.Equal(12, boundary12.CompletedRouteIndex);

        // 上一边界未放行前，任何成员都不得建立下一个边界（否则各客户端目标会分叉）
        var tooEarly = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-2"), Session, Epoch, Plan, 13, 14);
        Assert.True(HasError(tooEarly));
        Assert.Contains("anchor_in_progress", ErrorMessageOf(tooEarly));

        await CompleteBoundaryAsync(h, room, boundary12.AnchorId);
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
        Assert.Equal(12, room.LastReleasedRouteBoundary);

        // 放行后才能建立边界 13→14；此时旧边界 12 的重复 enroll 仍是幂等复用
        var boundary13 = SnapshotOf(await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, Epoch, Plan, 13, 14));
        Assert.Equal(13, boundary13.CompletedRouteIndex);
        Assert.Equal(14, boundary13.NextRouteIndex);
        Assert.NotEqual(boundary12.AnchorId, boundary13.AnchorId);

        await CompleteBoundaryAsync(h, room, boundary13.AnchorId);
        Assert.Equal(13, room.LastReleasedRouteBoundary);

        // 轮末边界（最后一条路线完成 → Finished 收口）：nextRouteIndex = -1 表示本轮结束
        var roundEnd = SnapshotOf(await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-2"), Session, Epoch, Plan, 14, -1));
        Assert.Equal(14, roundEnd.CompletedRouteIndex);
        Assert.Equal(-1, roundEnd.NextRouteIndex);

        await CompleteBoundaryAsync(h, room, roundEnd.AnchorId);
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
        Assert.Equal(14, room.LastReleasedRouteBoundary);
    }

    [Fact]
    public async Task Boundary_NotReleased_WhileAnyMemberMissing_EvenIfOthersReady()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;

        // 3 人完成，第 4 人只报告不就绪 → 不放行
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }
        await ReportAsync(h, room, "conn-4", anchorId);

        Assert.NotEqual(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.RouteAnchorReleased));

        // 第 4 人就绪 → 放行
        await ArriveAsync(h, room, "conn-4", anchorId);
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorReleased));
    }

    // =========================================================================
    // 阶段 4：Pull 下发与确认
    // =========================================================================

    /// <summary>把某成员的"有效活动"设为过期（心跳仍新鲜），用于构造"停滞"。</summary>
    private static void MakeMemberStalled(Room room, string uid, TimeSpan stale)
    {
        room.ActiveRouteAnchor!.LastActivityUtc[uid] = DateTime.UtcNow - stale;
        room.ActiveRouteAnchor!.LastHeartbeatUtc[uid] = DateTime.UtcNow;
    }

    private static List<GatewayEnvelope> ClientEvents(GatewayTestHarness h, string evtName)
    {
        var result = new List<GatewayEnvelope>();
        foreach (var invocation in h.GatewayClientProxy.Invocations)
        {
            if (invocation.Method.Name != nameof(IClientProxy.SendCoreAsync)) continue;
            if (invocation.Arguments.Count < 2) continue;
            if (invocation.Arguments[0] as string != GatewayProtocol.Callbacks.Event) continue;
            if (invocation.Arguments[1] is not object?[] args || args.Length == 0) continue;
            if (args[0] is not GatewayEnvelope env) continue;
            if (env.Name == evtName) result.Add(env);
        }
        return result;
    }

    [Fact]
    public async Task Evaluate_StalledMember_GetsTargetedPull()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        // conn-3 停滞（心跳新鲜、有效活动超普通宽限），其余人尚未报告
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));

        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        Assert.Equal(RouteAnchorMemberState.PullRequested, room.ActiveRouteAnchor!.MemberStates["uid-3"]);
        var pull = Assert.Single(ClientEvents(h, GatewayProtocol.Events.RouteAnchorPull));
        Assert.Equal(room.ActiveRouteAnchor.PullCommandIds["uid-3"], PayloadString(pull, "commandId"));
        // 定向发送：只发给 conn-3（不是组广播）
        h.GatewayHub.Verify(x => x.Clients.Client("conn-3"), Times.AtLeastOnce);
        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.RouteAnchorPull));
    }

    [Fact]
    public async Task Evaluate_FightingMemberWithinGrace_IsNotPulled()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        room.ActiveRouteAnchor!.MemberStates["uid-3"] = RouteAnchorMemberState.Fighting;
        // 超过普通宽限但仍在战斗宽限内
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));

        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        Assert.Equal(RouteAnchorMemberState.Fighting, room.ActiveRouteAnchor.MemberStates["uid-3"]);
        Assert.Empty(ClientEvents(h, GatewayProtocol.Events.RouteAnchorPull));
    }

    [Fact]
    public async Task PullApplied_ThenReady_Releases()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));
        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);
        var commandId = room.ActiveRouteAnchor!.PullCommandIds["uid-3"];

        // Pull 执行成功 → PullApplied（仍未 Ready，不放行）
        await h.Ops.ReportRouteAnchorPullAppliedAsync(Ctx("conn-3"), anchorId, commandId, true, "");
        Assert.Equal(RouteAnchorMemberState.PullApplied, room.ActiveRouteAnchor!.MemberStates["uid-3"]);
        Assert.NotEqual(RouteAnchorPhase.Released, room.ActiveRouteAnchor.Phase);

        // 全员 Report + Ready → 放行
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
    }

    [Fact]
    public async Task PullFailed_StopsWholeRoom()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));
        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);
        var commandId = room.ActiveRouteAnchor!.PullCommandIds["uid-3"];

        await h.Ops.ReportRouteAnchorPullAppliedAsync(Ctx("conn-3"), anchorId, commandId, false, "fight-not-interruptible");

        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
    }

    [Fact]
    public async Task PullAck_WithMismatchedCommandId_IsIgnored()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));
        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        var result = await h.Ops.ReportRouteAnchorPullAppliedAsync(Ctx("conn-3"), anchorId, "bogus-command", true, "");

        Assert.True(HasError(result));
        Assert.Contains("command_mismatch", ErrorMessageOf(result));
        Assert.Equal(RouteAnchorMemberState.PullRequested, room.ActiveRouteAnchor!.MemberStates["uid-3"]);
    }

    [Fact]
    public async Task Evaluate_OfflineMember_StopsWholeRoom()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        room.ActiveRouteAnchor!.MemberStates["uid-4"] = RouteAnchorMemberState.Offline;

        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
    }

    // =========================================================================
    // 成员离开房间：不可恢复 → 立即停止整队（不得拖到绝对截止）
    // =========================================================================

    [Fact]
    public async Task ParticipantLeftRoom_EvaluateStopsImmediately_WithoutWaitingDeadline()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        Assert.Equal(4, room.ActiveRouteAnchor!.Participants.Count);

        // conn-4 主动离开房间（RoomManager.LeaveRoom 会从 Players 中移除）
        h.RoomManager.LeaveRoom("conn-4");

        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Equal("member-left-room", room.ActiveRouteAnchor.LatestFailureReason);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
        Assert.Null(room.RouteAnchorEvaluateTimer);   // 终态即解除兜底评估
    }

    [Fact]
    public async Task ParticipantLeftRoom_DoesNotIssuePointlessPull()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        // 让离开的成员同时"停滞"，验证不会尝试给它下发 Pull（它已经没有连接了）
        MakeMemberStalled(room, "uid-4", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(10));
        h.RoomManager.LeaveRoom("conn-4");

        await h.Ops.EvaluateRouteAnchorAsync(room, room.Code);

        Assert.Empty(ClientEvents(h, GatewayProtocol.Events.RouteAnchorPull));
        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
    }

    // =========================================================================
    // 多世界轮换：锚点的每世界状态隔离
    // =========================================================================

    [Fact]
    public async Task NewWorldRound_ResetsReleasedBoundary_SoNextWorldCanEnrollFromZero()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        await CompleteBoundaryAsync(h, room, anchorId);
        Assert.Equal(12, room.LastReleasedRouteBoundary);

        await h.Ops.ResetForNewWorldRoundAsync(Ctx("conn-1"), 1);

        Assert.Null(room.ActiveRouteAnchor);
        Assert.Equal(-1, room.LastReleasedRouteBoundary);   // 关键：每世界状态必须复位

        // 新世界从边界 0 重新开始：必须能建立锚点（否则第二世界每一轮都会被误停）
        var next = SnapshotOf(await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, 1, Plan, 0, 1));
        Assert.Equal(0, next.CompletedRouteIndex);
        Assert.Equal(1, next.WorldEpoch);                   // 世界代际由服务端当前轮次给出
        Assert.NotEqual(anchorId, next.AnchorId);           // 锚点 ID 仍保持单调唯一
    }

    [Fact]
    public async Task NewWorldRound_OldAnchorMessages_AreRejected()
    {
        var (h, _, room) = Room4();
        var oldAnchorId = (await EnrollAsync(h, "conn-1")).AnchorId;

        await h.Ops.ResetForNewWorldRoundAsync(Ctx("conn-1"), 1);
        await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-1"), Session, 1, Plan, 0, 1);

        // 旧世界的锚点 ID 一律拒绝（不污染新世界）
        var stale = await h.Ops.ReportRouteAnchorAsync(Ctx("conn-2"), oldAnchorId, Session, 1, Plan, 0, "completed");
        Assert.True(HasError(stale));
        Assert.Contains("NotSameAnchor", ErrorMessageOf(stale));
        Assert.NotNull(room.ActiveRouteAnchor);
        Assert.Equal(0, room.ActiveRouteAnchor!.CompletedRouteIndex);
    }

    // =========================================================================
    // 阶段 5：取消命令（sync.routeAnchorCancel）
    // =========================================================================

    [Fact]
    public async Task Cancel_StopsAnchor_BroadcastsOnce_AndDisarmsTimer()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        Assert.NotNull(room.RouteAnchorEvaluateTimer);

        var result = await h.Ops.CancelRouteAnchorAsync(Ctx("conn-1"), anchorId, "user-stop");

        Assert.False(HasError(result));
        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Equal("user-stop", room.ActiveRouteAnchor.LatestFailureReason);
        Assert.Null(room.RouteAnchorEvaluateTimer);
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
    }

    [Fact]
    public async Task Cancel_IsIdempotent_NoSecondBroadcast()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        await h.Ops.CancelRouteAnchorAsync(Ctx("conn-1"), anchorId, "first");

        var again = await h.Ops.CancelRouteAnchorAsync(Ctx("conn-2"), anchorId, "second");

        Assert.False(HasError(again));
        Assert.Single(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
        Assert.Equal("first", room.ActiveRouteAnchor!.LatestFailureReason);
    }

    [Fact]
    public async Task Cancel_WrongAnchorOrNonParticipant_IsRejected()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;

        var wrong = await h.Ops.CancelRouteAnchorAsync(Ctx("conn-1"), "bogus-anchor", "x");
        Assert.True(HasError(wrong));
        Assert.Contains("stale_anchor", ErrorMessageOf(wrong));
        Assert.NotEqual(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Empty(GroupEvents(h, GatewayProtocol.Events.RouteAnchorStopped));
    }

    [Fact]
    public async Task Cancel_NoAnchor_IsRejected()
    {
        var (h, _, _) = Room4();

        var result = await h.Ops.CancelRouteAnchorAsync(Ctx("conn-1"), "any", "x");

        Assert.True(HasError(result));
        Assert.Contains("no_anchor", ErrorMessageOf(result));
    }

    [Fact]
    public async Task Cancel_AfterRelease_DoesNotRegressState()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        await CompleteBoundaryAsync(h, room, anchorId);
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);

        var result = await h.Ops.CancelRouteAnchorAsync(Ctx("conn-1"), anchorId, "late-cancel");

        Assert.False(HasError(result));
        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);   // 已放行不得回退
    }

    // =========================================================================
    // 阶段 5：战斗/复苏/进度观察写入锚点状态（不改既有 PlayerStatus/IsAbnormal）
    // =========================================================================

    [Fact]
    public async Task FightingReport_WritesAnchorSideState_WithoutTouchingLegacyFields()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-2");
        var player = room.Players.First(p => p.ConnectionId == "conn-2");
        var legacyStatusBefore = player.Status;
        var legacyAbnormalBefore = player.IsAbnormal;

        await h.Ops.FightingStatusChangedAsync(Ctx("conn-2"), "uid-2", true);

        Assert.Equal(RouteAnchorMemberState.Fighting, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
        // 既有字段零改动（方案 §9.2）
        Assert.Equal(legacyStatusBefore, player.Status);
        Assert.Equal(legacyAbnormalBefore, player.IsAbnormal);
    }

    [Fact]
    public async Task FightingReport_False_ReturnsMemberToPathing()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-2");

        await h.Ops.FightingStatusChangedAsync(Ctx("conn-2"), "uid-2", true);
        await h.Ops.FightingStatusChangedAsync(Ctx("conn-2"), "uid-2", false);

        Assert.Equal(RouteAnchorMemberState.Pathing, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
    }

    [Fact]
    public async Task RevivingReport_WritesRevivingState()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-3")).AnchorId;
        await h.Ops.MemberStatusChangedAsync(Ctx("conn-3"), "uid-3", "Reviving", 1000);

        Assert.Equal(RouteAnchorMemberState.Reviving, room.ActiveRouteAnchor!.MemberStates["uid-3"]);
        Assert.NotNull(anchorId);
    }

    [Fact]
    public async Task ReadyMember_IsNotDowngradedByLaterFightingReport()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-2")).AnchorId;
        await ReportAsync(h, room, "conn-2", anchorId);
        await ArriveAsync(h, room, "conn-2", anchorId);
        Assert.Equal(RouteAnchorMemberState.Ready, room.ActiveRouteAnchor!.MemberStates["uid-2"]);

        // 就绪后仍可能收到战斗上报：不得把它降级回 Fighting，否则锚点永远无法放行
        await h.Ops.FightingStatusChangedAsync(Ctx("conn-2"), "uid-2", true);

        Assert.Equal(RouteAnchorMemberState.Ready, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
    }

    [Fact]
    public async Task ProgressReport_RefreshesActivity_SoStalledMemberBecomesActiveAgain()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-2");
        MakeMemberStalled(room, "uid-2", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(10));

        // 到达同步点 = 真实推进 → 刷新有效活动
        await h.Ops.WaitForAllPlayersAsync(Ctx("conn-2"), "syncX", 1_000_000);

        var decision = RouteAnchorDecisions.ClassifyMissing(
            DateTime.UtcNow,
            routeAnchorHeartbeat(room, "uid-2"),
            room.ActiveRouteAnchor!.LastActivityUtc["uid-2"],
            room.ActiveRouteAnchor!.MemberStates["uid-2"],
            RouteAnchorDecisions.Budgets.Default);
        Assert.Equal(RouteAnchorMissingDecision.Wait, decision);
    }

    [Fact]
    public async Task Observations_WithoutAnchor_AreNoOps()
    {
        var (h, _, room) = Room4();
        Assert.Null(room.ActiveRouteAnchor);

        await h.Ops.FightingStatusChangedAsync(Ctx("conn-2"), "uid-2", true);
        await h.Ops.MemberStatusChangedAsync(Ctx("conn-2"), "uid-2", "Reviving", 1);

        Assert.Null(room.ActiveRouteAnchor);
    }

    private static DateTime routeAnchorHeartbeat(Room room, string uid)
        => room.ActiveRouteAnchor!.LastHeartbeatUtc.TryGetValue(uid, out var hb) ? hb : DateTime.UtcNow;

    // =========================================================================
    // 阶段 4：兜底评估定时器（卡死房间没有新消息，必须靠定时器驱动对账）
    // =========================================================================

    [Fact]
    public async Task Enroll_ArmsEvaluateTimer_AndStoppedOrReleasedDisarmsIt()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        Assert.NotNull(room.RouteAnchorEvaluateTimer);

        // 兜底回调直接驱动（不等待真实时钟）：停滞成员应被下发 Pull
        MakeMemberStalled(room, "uid-2", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(5));
        await h.Ops.EvaluateRouteAnchorTimerCallbackAsync(room, room.Code);

        Assert.Equal(RouteAnchorMemberState.PullRequested, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
        Assert.NotNull(room.RouteAnchorEvaluateTimer);   // 未终结 → 续挂

        // 拉取失败 → 整队停止 → 定时器释清
        var commandId = room.ActiveRouteAnchor.PullCommandIds["uid-2"];
        await h.Ops.ReportRouteAnchorPullAppliedAsync(Ctx("conn-2"), room.ActiveRouteAnchor.AnchorId, commandId, false, "x");

        Assert.Equal(RouteAnchorPhase.Stopped, room.ActiveRouteAnchor!.Phase);
        Assert.Null(room.RouteAnchorEvaluateTimer);
    }

    [Fact]
    public async Task EvaluateTimer_AllReady_Releases_AndDisarmsTimer()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }

        Assert.Equal(RouteAnchorPhase.Released, room.ActiveRouteAnchor!.Phase);
        Assert.Null(room.RouteAnchorEvaluateTimer);
    }

    [Fact]
    public async Task ResetForNewWorldRound_DisposesEvaluateTimer()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        Assert.NotNull(room.RouteAnchorEvaluateTimer);

        await h.Ops.ResetForNewWorldRoundAsync(Ctx("conn-1"), 2);

        Assert.Null(room.RouteAnchorEvaluateTimer);
        Assert.Null(room.ActiveRouteAnchor);
    }

    [Fact]
    public async Task EvaluateTimer_NoNewMessages_StillPulls_ProvingDriverNecessity()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        var pullsBefore = ClientEvents(h, GatewayProtocol.Events.RouteAnchorPull).Count;

        // 之后没有任何协议消息：仅靠消息入口驱动时，停滞成员永远不会被拉
        MakeMemberStalled(room, "uid-2", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(30));
        MakeMemberStalled(room, "uid-3", RouteAnchorDecisions.Budgets.Default.PathingGrace + TimeSpan.FromSeconds(30));

        await h.Ops.EvaluateRouteAnchorTimerCallbackAsync(room, room.Code);

        var pullsAfter = ClientEvents(h, GatewayProtocol.Events.RouteAnchorPull);
        Assert.Equal(pullsBefore + 2, pullsAfter.Count);
        Assert.Equal(RouteAnchorMemberState.PullRequested, room.ActiveRouteAnchor!.MemberStates["uid-2"]);
        Assert.Equal(RouteAnchorMemberState.PullRequested, room.ActiveRouteAnchor!.MemberStates["uid-3"]);
    }

    // =========================================================================
    // 服务端权威会话身份（防止客户端伪造代际）
    // =========================================================================

    [Fact]
    public async Task Enroll_IgnoresClientSuppliedSession_AndUsesServerIdentity()
    {
        var (h, _, room) = Room4();

        // 客户端谎报会话为 "S1"/epoch=1：服务端必须改用自己生成的会话与当前世界代际
        var snapshot = await EnrollAsync(h, "conn-1");

        Assert.Equal(room.SessionId, snapshot.SessionId);
        Assert.Equal(room.CurrentWorldRound, snapshot.WorldEpoch);
        Assert.NotEqual(Session, snapshot.SessionId);

        // 用客户端原本谎报的会话去报告 → 必须被拒（代际不匹配）
        var spoofed = await h.Ops.ReportRouteAnchorAsync(
            Ctx("conn-2"), snapshot.AnchorId, Session, Epoch, Plan, Boundary, "completed");
        Assert.True(HasError(spoofed));
        Assert.Contains("StaleGeneration", ErrorMessageOf(spoofed));
    }

    [Fact]
    public async Task Enroll_SameBoundaryAgain_AfterRelease_IsIdempotent_AndDoesNotCreateSecondAnchor()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }
        var sequenceAfterRelease = room.RouteAnchorSequence;

        // 同一边界再次 enroll（例如某成员迟到/重试）→ 复用已放行锚点，不得新建
        var again = SnapshotOf(await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-2"), Session, Epoch, Plan, Boundary, Next));

        Assert.Equal(anchorId, again.AnchorId);
        Assert.True(again.Released);
        Assert.Equal(sequenceAfterRelease, room.RouteAnchorSequence);
    }

    [Fact]
    public async Task Enroll_StaleBoundary_AfterRelease_IsRejected()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        foreach (var conn in new[] { "conn-1", "conn-2", "conn-3", "conn-4" })
        {
            await ReportAsync(h, room, conn, anchorId);
            await ArriveAsync(h, room, conn, anchorId);
        }
        Assert.Equal(Boundary, room.LastReleasedRouteBoundary);

        // 旧世界/旧轮次客户端迟到：请求更早的边界 → 拒绝
        var stale = await h.Ops.EnrollRouteAnchorAsync(Ctx("conn-3"), Session, Epoch, Plan, Boundary - 1, Boundary);

        Assert.True(HasError(stale));
        Assert.Contains("stale_boundary", ErrorMessageOf(stale));
    }

    // =========================================================================
    // 查询与重置
    // =========================================================================

    [Fact]
    public async Task Query_NoAnchor_ReturnsHasAnchorFalse()
    {
        var (h, _, room) = Room4();

        var snapshot = SnapshotOf(await h.Ops.QueryRouteAnchorStateAsync(Ctx("conn-1")));

        Assert.False(snapshot.HasAnchor);
    }

    [Fact]
    public async Task Query_ReturnsAuthoritativeSnapshot_WithCallerState()
    {
        var (h, _, room) = Room4();
        var anchorId = (await EnrollAsync(h, "conn-1")).AnchorId;
        await ReportAsync(h, room, "conn-2", anchorId);

        var asSelf = SnapshotOf(await h.Ops.QueryRouteAnchorStateAsync(Ctx("conn-2")));
        var asOther = SnapshotOf(await h.Ops.QueryRouteAnchorStateAsync(Ctx("conn-3")));

        Assert.Equal("Reported", asSelf.MyState);
        Assert.Equal("Pathing", asOther.MyState);
        Assert.Equal(4, asSelf.MemberStates.Count);
    }

    [Fact]
    public async Task ResetForNewWorldRound_ClearsRouteAnchor()
    {
        var (h, _, room) = Room4();
        await EnrollAsync(h, "conn-1");
        Assert.NotNull(room.ActiveRouteAnchor);

        await h.Ops.ResetForNewWorldRoundAsync(Ctx("conn-1"), 2);

        Assert.Null(room.ActiveRouteAnchor);
    }

    // =========================================================================
    // 网关注册
    // =========================================================================

    [Fact]
    public void Gateway_RegistersRouteAnchorProtocolSurface()
    {
        var h = new GatewayTestHarness();
        var names = h.Dispatcher.RegisteredNames.ToHashSet(StringComparer.Ordinal);

        Assert.Contains(GatewayProtocol.Names.RouteAnchorEnroll, names);
        Assert.Contains(GatewayProtocol.Names.RouteAnchorReport, names);
        Assert.Contains(GatewayProtocol.Names.RouteAnchorArrived, names);
        Assert.Contains(GatewayProtocol.Names.RouteAnchorStateQuery, names);
    }
}
