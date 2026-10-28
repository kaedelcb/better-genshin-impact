using System.Reflection;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Gateway;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class DodocoGatewayAccessTests
{
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.V3(id);
    private static string Room() => "T" + Guid.NewGuid().ToString("N")[..8];
    private static async Task Join(GatewayTestHarness h, string room, string connection, string uid)
        => await h.Ops.JoinControlRoomAsync(Ctx(connection), room, "pw", uid, uid);
    private static JsonObject Empty() => new();
    private static string? Error(object result) => GatewayEnvelope.ToPayload(result)?["error"]?["code"]?.GetValue<string>();
    private static bool Accepted(object result) => GatewayEnvelope.ToPayload(result)?["accepted"]?.GetValue<bool>() == true;

    [Fact]
    public async Task Request_RequiresJoinedRoomOnlineTargetAndBoundedWhitelistedOperation()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await Join(h, room, "viewer", "web_viewer");
        await Join(h, room, "target", "targetUid");
        Assert.Equal("not_in_room", Error(await h.Ops.RequestDodocoAsync(Ctx("outsider"), room, "targetUid", "r1", "watch.read", Empty())));
        Assert.Equal("target_offline", Error(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "missing", "r2", "watch.read", Empty())));
        Assert.Equal("invalid_args", Error(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "r3", "watch.run_shell", Empty())));
        Assert.Equal("invalid_args", Error(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "r4", "watch.read", new JsonObject { ["large"] = new string('x', 16385) })));
        Assert.Equal("invalid_args", Error(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "bad/id", "watch.read", Empty())));
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Response_IsOnlyFromTargetForSameRoom_AndDeliveredOnceToRequester()
    {
        var h = new GatewayTestHarness(); var room = Room(); var otherRoom = Room();
        await Join(h, room, "viewer", "web_viewer");
        await Join(h, room, "target", "targetUid");
        await Join(h, room, "other", "otherUid");
        await Join(h, otherRoom, "outsider", "outsideUid");
        Assert.True(Accepted(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "request", "watch.read", Empty())));
        Assert.Equal("duplicate_request", Error(await h.Ops.RequestDodocoAsync(Ctx("other"), room, "targetUid", "request", "watch.read", Empty())));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondDodocoAsync(Ctx("other"), room, "request", "ok", "", "", Empty())));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondDodocoAsync(Ctx("outsider"), otherRoom, "request", "ok", "", "", Empty())));
        Assert.Equal("invalid_args", Error(await h.Ops.RespondDodocoAsync(Ctx("target"), room, "request", "sent", "", "", Empty())));
        Assert.Equal("invalid_args", Error(await h.Ops.RespondDodocoAsync(Ctx("target"), room, "request", "ok", "", "", new JsonObject { ["large"] = new string('x', 512 * 1024 + 1) })));
        Assert.True(Accepted(await h.Ops.RespondDodocoAsync(Ctx("target"), room, "request", "ok", "", "", new JsonObject { ["value"] = "actual" })));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondDodocoAsync(Ctx("target"), room, "request", "ok", "", "", Empty())));
        h.GatewayHub.Verify(x => x.Clients.Client("target"), Times.Once);
        h.GatewayHub.Verify(x => x.Clients.Client("viewer"), Times.Once);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsResult(args[0], "request", "actual")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool IsResult(object? value, string requestId, string expected)
        => value is GatewayEnvelope env && env.Name == "dodoco.result"
            && env.Payload?["requestId"]?.GetValue<string>() == requestId
            && env.Payload?["data"]?["value"]?.GetValue<string>() == expected;

    [Fact]
    public async Task ExpiredRequest_RejectsEvenCorrectTarget()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await Join(h, room, "viewer", "web_viewer");
        await Join(h, room, "target", "targetUid");
        Assert.True(Accepted(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "expired", "watch.read", Empty())));
        // Force the stored deadline into the past, avoiding a slow wall-clock wait.
        var pending = h.Ops.GetType().GetField("_dodocoPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Ops)!;
        var arguments = new object?[] { "expired", null };
        Assert.True((bool)pending.GetType().GetMethod("TryGetValue")!.Invoke(pending, arguments)!);
        arguments[1]!.GetType().GetProperty("Expires")!.SetValue(arguments[1], DateTime.UtcNow.AddSeconds(-1));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondDodocoAsync(Ctx("target"), room, "expired", "ok", "", "", Empty())));
        h.GatewayHub.Verify(x => x.Clients.Client("viewer"), Times.Never);
    }
    [Theory]
    [InlineData("watch.diagnostic.start")]
    [InlineData("watch.diagnostic.chunk")]
    public async Task DiagnosticOperations_ForwardAuthenticatedRequesterConnection(string operation)
    {
        var h = new GatewayTestHarness(); var room = Room();
        await Join(h, room, "viewer", "web_viewer");
        await Join(h, room, "target", "targetUid");
        Assert.True(Accepted(await h.Ops.RequestDodocoAsync(Ctx("viewer"), room, "targetUid", "diagnostic", operation, Empty())));
        h.GatewayHub.Verify(x => x.Clients.Client("target"), Times.Once);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsBoundRequest(args[0], operation, "viewer")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool IsBoundRequest(object? value, string operation, string requester)
        => value is GatewayEnvelope env && env.Name == "dodoco.requested"
            && env.Payload?["operation"]?.GetValue<string>() == operation
            && env.Payload?["requesterConnectionId"]?.GetValue<string>() == requester
            && env.Payload?["expiresAtUtc"] != null;

}
