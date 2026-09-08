using BgiCoordinatorServer.Gateway;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 截图按需取图（pull）链路测试：观众请求 → 单播目标端 MemberScreenshotRequested；
/// 目标带 requestId 应答 → 按映射单播回请求方 MemberScreenshot（不广播）；
/// 无映射应答丢弃；请求方限流（每秒 1 次）。
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

        // 同请求方每秒 1 次：第二次被限流丢弃
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshotRequested",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
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
