using BgiCoordinatorServer.Gateway;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 截图按需取图（pull）链路测试：观众请求 → 单播目标端 MemberScreenshotRequested；
/// 目标带 requestId 应答 → 按映射单播回请求方 MemberScreenshot（不广播）；
/// 无映射应答丢弃；同请求方对同一目标限流（每秒 1 次），不同目标可并行请求。
/// 每个测试用随机 roomCode，避开 ControlRoomAuth/限流静态表的跨测试污染。
/// </summary>
public class ScreenshotOnDemandTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("T" + Guid.NewGuid().ToString("N")[..8], "pw");

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static Task JoinAsync(GatewayTestHarness h, string conn, string room, string pwd, string uid, string name)
        => h.Ops.JoinControlRoomAsync(Ctx(conn), room, pwd, uid, name);

    /// <summary>拼装一个合法尺寸内的 base64 负载（内容不重要，链路只认长度上限）。</summary>
    private static string SmallBase64() => Convert.ToBase64String(new byte[64]);

    private sealed class ScreenshotClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private static string RequestId(string session) => $"screen:{session}:{Guid.NewGuid():N}";

    private static void VerifyRequests(GatewayTestHarness h, int count)
        => h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(count));

    private static void VerifyFrames(GatewayTestHarness h, int count)
        => h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(count));

    // 这些测试决定桌面监控能否按固定三分钟停止；推进单调时钟，不实际等待或构建运行产物。
    // 原有测试仅覆盖一帧路由与限流，不能证明边界、重入、迟到帧或旧客户端不会续期。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedWindow_StopsAtThreeMinutes_AndRequestsDoNotRenew(bool legacy)
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        var session = Guid.NewGuid().ToString("N");
        string NextId() => legacy ? Guid.NewGuid().ToString("N") : RequestId(session);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", NextId());
        clock.Advance(TimeSpan.FromSeconds(178));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", NextId());
        clock.Advance(TimeSpan.FromSeconds(1));
        var last = NextId();
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", last);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, last);
        VerifyRequests(h, 3);
        VerifyFrames(h, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", NextId());
        clock.Advance(TimeSpan.FromHours(1));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", NextId());
        VerifyRequests(h, 3);
    }

    [Fact]
    public async Task NewSessionReenters_ButOldSessionAndLateFramesStayExpired()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        var oldSession = Guid.NewGuid().ToString("N");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(oldSession));
        clock.Advance(TimeSpan.FromSeconds(179));
        var late = RequestId(oldSession);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", late);
        clock.Advance(TimeSpan.FromSeconds(1));
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, late);
        VerifyFrames(h, 0);
        var fresh = RequestId(Guid.NewGuid().ToString("N"));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", fresh);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, fresh);
        VerifyFrames(h, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(oldSession));
        VerifyRequests(h, 3);
    }

    [Fact]
    public async Task SameSessionAcrossTargetsAndSameUidViewers_HasIndependentConnectionWindows()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewerA", room, pwd, "sameUid", "观众A");
        await h.Ops.JoinControlRoomAsync(Ctx("viewerB"), room, pwd, "sameUid", "观众B", isRemote: true);
        await JoinAsync(h, "targetB", room, pwd, "uidB", "目标B");
        await JoinAsync(h, "targetC", room, pwd, "uidC", "目标C");
        var session = Guid.NewGuid().ToString("N");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewerA"), room, "uidB", RequestId(session));
        clock.Advance(TimeSpan.FromSeconds(60));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewerA"), room, "uidC", RequestId(session));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewerB"), room, "uidC", RequestId(session));
        VerifyRequests(h, 3);
        clock.Advance(TimeSpan.FromSeconds(120));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewerA"), room, "uidC", RequestId(session));
        var b = RequestId(session);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewerB"), room, "uidC", b);
        var viewerA = new Mock<ISingleClientProxy>();
        var viewerB = new Mock<ISingleClientProxy>();
        foreach (var proxy in new[] { viewerA, viewerB })
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        h.LegacyHub.Setup(x => x.Clients.Client("viewerA")).Returns(viewerA.Object);
        h.LegacyHub.Setup(x => x.Clients.Client("viewerB")).Returns(viewerB.Object);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("targetC"), room, "uidC", SmallBase64(), 1, 1, DateTime.UtcNow, b);
        viewerB.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        viewerA.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyRequests(h, 4);
    }

    [Fact]
    public async Task OfflineFirstRequestStillStartsWindow()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        var session = Guid.NewGuid().ToString("N");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(session));
        clock.Advance(TimeSpan.FromMinutes(3));
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(session));
        VerifyRequests(h, 0);
    }

    [Fact]
    public async Task DisconnectDropsPendingFrames_NewPhysicalConnectionHasIndependentWindow()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        await h.Ops.JoinControlRoomAsync(Ctx("viewer"), room, pwd, "viewerUid", "观众", isRemote: true);
        var session = Guid.NewGuid().ToString("N");
        var pending = RequestId(session);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", pending);
        await h.Ops.HandleDisconnectAsync(Ctx("viewer"), null);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, pending);
        VerifyFrames(h, 0);
        clock.Advance(TimeSpan.FromMinutes(4));
        await h.Ops.JoinControlRoomAsync(Ctx("viewer-new"), room, pwd, "viewerUid", "观众", isRemote: true);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer-new"), room, "targetUid", RequestId(session));
        VerifyRequests(h, 2);
    }

    [Fact]
    public async Task MoreThan128ReentriesRemainAvailable_EvictedFramesDrop_AndCurrentWindowDoesNotRenew()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", "legacy-first");
        clock.Advance(TimeSpan.FromSeconds(1));
        var first = RequestId(Guid.NewGuid().ToString("N"));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", first);
        var currentSession = "";
        var currentRequest = "";
        for (var i = 1; i < 140; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            currentSession = Guid.NewGuid().ToString("N");
            currentRequest = RequestId(currentSession);
            await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", currentRequest);
        }
        VerifyRequests(h, 141);
        // 首帧尚未到180秒，但其会话历史已被淘汰，相关在途帧也必须删除。
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, first);
        VerifyFrames(h, 0);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, currentRequest);
        VerifyFrames(h, 1);
        clock.Advance(TimeSpan.FromMinutes(3));
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(currentSession));
        // 旧格式的连接级窗口永不逐出，混用新格式也不能使它自动续期。
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", "legacy-after-rollover");
        VerifyRequests(h, 141);
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", RequestId(Guid.NewGuid().ToString("N")));
        VerifyRequests(h, 142);
    }

    [Fact]
    public async Task LegacyUnsolicitedReport_NeverBroadcastsOrUnicasts()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", "legacy-request");
        h.GatewayGroupProxy.Invocations.Clear();
        await h.Ops.ReportMemberScreenshotAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow);
        VerifyFrames(h, 0);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        h.GatewayGroupProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GatewayAccepts72CharacterRequestId_AndAppliesDeadlineToReply()
    {
        var clock = new ScreenshotClock();
        var h = new GatewayTestHarness(screenshotTimeProvider: clock);
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "viewer", room, pwd, "viewerUid", "观众");
        await JoinAsync(h, "target", room, pwd, "targetUid", "目标");
        var ctx = GatewayHandlerContext.V3("viewer");
        await h.Dispatcher.QueryAsync(ctx, GatewayTestHarness.HelloEnvelope());
        var id = RequestId(Guid.NewGuid().ToString("N"));
        Assert.Equal(72, id.Length);
        var result = await h.Dispatcher.DispatchAsync(ctx, new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.ScreenshotRequest,
            Payload = GatewayEnvelope.ToPayload(new { roomCode = room, targetUid = "targetUid", requestId = id })
        });
        Assert.Null(GatewayTestHarness.ErrorCode(result));
        VerifyRequests(h, 1);
        clock.Advance(TimeSpan.FromMinutes(3));
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", SmallBase64(), 1, 1, DateTime.UtcNow, id);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Request_ForwardsToTargetConnection_WithRequesterUidAndRequestId()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");
        await JoinAsync(h, "conn-target", room, pwd, "uidB", "目标");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "req1");

        // 单播定位到目标连接
        h.LegacyHub.Verify(x => x.Clients.Client("conn-target"), Times.AtLeastOnce);
        // 事件带 requesterUid + requestId
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.Is<object?[]>(args => args.Length == 2 && (string?)args[0] == "uidA" && (string?)args[1] == "req1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Request_TargetOffline_SilentlyDropped()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB-不在线", "req1");

        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Request_RateLimited_SecondWithinSameSecondDropped()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");
        await JoinAsync(h, "conn-target", room, pwd, "uidB", "目标");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "req1");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "req2");

        // 同请求方对同一目标每秒 1 次：第二次被限流丢弃
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Request_DifferentTargetsInSameSecond_BothForwarded()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");
        await JoinAsync(h, "conn-target-b", room, pwd, "uidB", "成员 B");
        await JoinAsync(h, "conn-target-c", room, pwd, "uidC", "成员 C");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "reqB");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidC", "reqC");

        h.LegacyHub.Verify(x => x.Clients.Client("conn-target-b"), Times.AtLeastOnce);
        h.LegacyHub.Verify(x => x.Clients.Client("conn-target-c"), Times.AtLeastOnce);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target-b"), room, "uidB",
            SmallBase64(), 1280, 720, DateTime.Now, "reqB");
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target-c"), room, "uidC",
            SmallBase64(), 1280, 720, DateTime.Now, "reqC");
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ReportEx_UnicastsToRequester_AndMappingIsOneShot()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");
        await JoinAsync(h, "conn-target", room, pwd, "uidB", "目标");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "req1");
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target"), room, "uidB",
            SmallBase64(), 1280, 720, DateTime.Now, "req1");

        // 应答按映射单播回请求方连接
        h.LegacyHub.Verify(x => x.Clients.Client("conn-viewer"), Times.AtLeastOnce);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);

        // 映射一次性：同 requestId 再次应答被丢弃
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target"), room, "uidB",
            SmallBase64(), 1280, 720, DateTime.Now, "req1");
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReportEx_WithoutMapping_Dropped()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-target", room, pwd, "uidB", "目标");

        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target"), room, "uidB",
            SmallBase64(), 1280, 720, DateTime.Now, "无映射的requestId");

        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReportEx_OversizedPayload_Dropped()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        await JoinAsync(h, "conn-viewer", room, pwd, "uidA", "观众");
        await JoinAsync(h, "conn-target", room, pwd, "uidB", "目标");

        await h.Ops.RequestMemberScreenshotAsync(Ctx("conn-viewer"), room, "uidB", "req1");
        var oversized = new string('A', 512 * 1024 + 1);
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("conn-target"), room, "uidB",
            oversized, 1280, 720, DateTime.Now, "req1");

        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
