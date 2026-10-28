using BgiCoordinatorServer.Gateway;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class MemberReportIdentityTests
{
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.Legacy(id);
    private static string Room() => "T" + Guid.NewGuid().ToString("N")[..8];
    private static Task Join(GatewayTestHarness h, string room, string conn, string uid, bool remote = false)
        => h.Ops.JoinControlRoomAsync(Ctx(conn), room, "pw", uid, uid, isRemote: remote);

    [Fact]
    public async Task Reports_CannotImpersonateOtherMember_ButOriginalMemberAndSameUidObserverWork()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await Join(h, room, "executor", "uidA");
        await Join(h, room, "observer", "uidA", remote: true);
        await Join(h, room, "other", "uidB");
        await h.Ops.SubscribeMemberLogAsync(Ctx("observer"), room, "uidA");
        Assert.Equal(1, h.RoomManager.GetLogSubscriberCount("CTRL_" + room, "uidA"));

        await h.Ops.ReportMemberLogBatchAsync(Ctx("other"), room, "uidA", "fake", ["spoof"], false);
        await h.Ops.ReportMemberScreenshotAsync(Ctx("other"), room, "uidA", "AA==", 1, 1, DateTime.UtcNow);
        h.LegacyMultiClientProxy.Verify(p => p.SendCoreAsync("MemberLogBatch", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);

        await h.Ops.ReportMemberLogBatchAsync(Ctx("executor"), room, "uidA", "real", ["real"], false);
        await h.Ops.ReportMemberScreenshotAsync(Ctx("executor"), room, "uidA", "AA==", 1, 1, DateTime.UtcNow);
        h.LegacyMultiClientProxy.Verify(p => p.SendCoreAsync("MemberLogBatch", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoteReport_UsesRegisteredUid_NotPayloadUid()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await Join(h, room, "executor", "uidA");
        await Join(h, room, "observer", "uidA", remote: true);
        await Join(h, room, "viewer", "uidC", remote: true);
        await h.Ops.SubscribeMemberLogAsync(Ctx("viewer"), room, "uidA");
        Assert.Equal("uidA", h.RoomManager.GetRemoteUidByConnectionId("CTRL_" + room, "observer"));
        await h.Ops.ReportMemberLogBatchAsync(Ctx("observer"), room, "uidB", "observer", ["wrong uid"], false);
        await h.Ops.ReportMemberLogBatchAsync(Ctx("observer"), room, "uidA", "observer", ["same uid"], false);
        h.LegacyMultiClientProxy.Verify(p => p.SendCoreAsync("MemberLogBatch", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        await h.Ops.ReportMemberScreenshotAsync(Ctx("observer"), room, "uidA", "AA==", 1, 1, DateTime.UtcNow);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
