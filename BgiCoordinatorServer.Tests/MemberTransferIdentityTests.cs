using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class MemberTransferIdentityTests
{
    private static GatewayHandlerContext Ctx(string connection) => GatewayHandlerContext.Legacy(connection);
    private static async Task Join(GatewayTestHarness h, string room, string connection, string uid)
        => await h.Ops.JoinControlRoomAsync(Ctx(connection), room, "pw", uid, uid);
    private static string NewRoom() => "T" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Screenshot_ResponseIsBoundToTargetAndIncludesRequestId()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await Join(h, room, "viewer", "viewerUid");
        await Join(h, room, "target", "targetUid");
        await Join(h, room, "other", "otherUid");
        await h.Ops.RequestMemberScreenshotAsync(Ctx("viewer"), room, "targetUid", "request");

        // Neither a different connection nor a spoofed UID may consume the real request.
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("other"), room, "targetUid", "AA==", 1, 1, DateTime.UtcNow, "request");
        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "otherUid", "AA==", 1, 1, DateTime.UtcNow, "request");
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);

        await h.Ops.ReportMemberScreenshotExAsync(Ctx("target"), room, "targetUid", "AA==", 1, 1, DateTime.UtcNow, "request");
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberScreenshot",
            It.Is<object?[]>(args => args.Length == 1 && HasRequestId(args[0], "request")),
            It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyHub.Verify(x => x.Clients.Client("viewer"), Times.AtLeastOnce);
    }

    private static bool HasRequestId(object? value, string requestId)
        => (string?)value?.GetType().GetProperty("requestId")?.GetValue(value) == requestId;

    [Fact]
    public async Task FileList_WrongRoomTargetOrKindCannotConsumeRequest()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        var otherRoom = NewRoom();
        await Join(h, room, "viewer", "viewerUid");
        await Join(h, room, "target", "targetUid");
        await Join(h, room, "other", "otherUid");
        await Join(h, otherRoom, "outsider", "outsiderUid");
        await h.Ops.RequestMemberLogFilesAsync(Ctx("viewer"), room, "targetUid", "list");
        var files = new List<MemberLogFileDescriptor> { new() { Name = "example.log" } };

        await h.Ops.ReportMemberLogFilesAsync(Ctx("other"), room, "targetUid", "list", files);
        await h.Ops.ReportMemberLogFilesAsync(Ctx("outsider"), otherRoom, "targetUid", "list", files);
        await h.Ops.ReportMemberLogChunkAsync(Ctx("target"), room, "targetUid", "list", 0, 1, "AA==", "example.log", true);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberLogFileList", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberLogFileChunk", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);

        await h.Ops.ReportMemberLogFilesAsync(Ctx("target"), room, "targetUid", "list", files);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberLogFileList", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Download_DuplicateRequestCannotRedirectAndWrongFileCannotConsume()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await Join(h, room, "viewer", "viewerUid");
        await Join(h, room, "otherViewer", "otherViewerUid");
        await Join(h, room, "target", "targetUid");
        await h.Ops.RequestMemberLogDownloadAsync(Ctx("viewer"), room, "targetUid", "download", "one.log");
        await h.Ops.RequestMemberLogDownloadAsync(Ctx("otherViewer"), room, "targetUid", "download", "one.log");
        await h.Ops.ReportMemberLogChunkAsync(Ctx("target"), room, "targetUid", "download", 0, 1, "AA==", "two.log", true);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberLogFileChunk", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
        await h.Ops.ReportMemberLogChunkAsync(Ctx("target"), room, "targetUid", "download", 0, 1, "AA==", "one.log", true);
        h.LegacyHub.Verify(x => x.Clients.Client("viewer"), Times.AtLeastOnce);
        h.LegacyHub.Verify(x => x.Clients.Client("otherViewer"), Times.Never);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("MemberLogFileChunk", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
