using System.Reflection;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Gateway;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>网页槲寄生中转：只按 UID + 助手实例精确送达，回复只认目标连接并只投回发起连接。</summary>
public class MistletoeGatewayRelayTests
{
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.V3(id);
    private static string Room() => "T" + Guid.NewGuid().ToString("N")[..8];
    private static JsonObject Request(string operation = "startup.get") => new() { ["operation"] = operation, ["targetInstanceId"] = "inst-a" };
    private static JsonObject Reply(string marker) => new() { ["status"] = "ok", ["message"] = marker };
    private static string? Error(object result) => GatewayEnvelope.ToPayload(result)?["error"]?["code"]?.GetValue<string>();
    private static bool Accepted(object result) => GatewayEnvelope.ToPayload(result)?["accepted"]?.GetValue<bool>() == true;

    private static async Task<(GatewayTestHarness H, string Room)> RoomWithTwoInstancesAsync()
    {
        var h = new GatewayTestHarness(); var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("viewer"), room, "pw", "web_viewer", "网页");
        // 同一 UID 的执行端与监控端并存：必须按实例区分。
        await h.Ops.JoinControlRoomAsync(Ctx("executor"), room, "pw", "owner", "执行端", clientInstanceId: "inst-a", machineId: "m1");
        await h.Ops.JoinControlRoomAsync(Ctx("second"), room, "pw", "owner", "第二实例", clientInstanceId: "inst-b", machineId: "m2");
        await h.Ops.JoinControlRoomAsync(Ctx("other"), room, "pw", "other", "他人", clientInstanceId: "inst-c", machineId: "m3");
        h.GatewayClientProxy.Invocations.Clear(); h.GatewayHub.Invocations.Clear();
        return (h, room);
    }

    [Fact]
    public async Task Request_IsDeliveredOnlyToTheExactInstance()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        Assert.True(Accepted(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "r1", Request())));
        h.GatewayHub.Verify(x => x.Clients.Client("executor"), Times.Once);
        h.GatewayHub.Verify(x => x.Clients.Client("second"), Times.Never);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsRequested(args[0], "r1", "viewer")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Request_RejectsOutsiderUnknownInstanceDuplicatesAndOversize()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        Assert.Equal("not_in_room", Error(await h.Ops.RequestMistletoeAsync(Ctx("outsider"), room, "owner", "inst-a", "r0", Request())));
        Assert.Equal("target_offline", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-gone", "r1", Request())));
        // UID 与实例必须同时匹配：他人的实例号不能借道。
        Assert.Equal("target_offline", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-c", "r2", Request())));
        Assert.Equal("invalid_args", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "bad/id", Request())));
        Assert.Equal("invalid_args", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "r3", null)));
        var large = Request(); large["payload"] = new string('x', 2 * 1024 * 1024 + 64 * 1024);
        Assert.Equal("invalid_args", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "r4", large)));
        // 2 MB 计划文档（加信封）仍可送达。
        var big = Request("task.flow.save"); big["payload"] = new string('y', 2 * 1024 * 1024 - 1024);
        Assert.True(Accepted(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "r5", big)));
        Assert.Equal("duplicate_request", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "r5", Request())));
        h.GatewayHub.Verify(x => x.Clients.Client("executor"), Times.Once);
    }

    [Fact]
    public async Task OfflineTarget_IsNotQueuedForLaterDelivery()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        await h.Ops.HandleDisconnectAsync(Ctx("executor"), null);
        Assert.Equal("target_offline", Error(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "late", Request())));
        // 同一实例重新连接后，离线期间的请求不会补发。
        await h.Ops.JoinControlRoomAsync(Ctx("executor2"), room, "pw", "owner", "执行端", clientInstanceId: "inst-a", machineId: "m1");
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsNamed(args[0], "mistletoe.requested")), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Accepted(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "fresh", Request())));
        h.GatewayHub.Verify(x => x.Clients.Client("executor2"), Times.Once);
    }

    [Fact]
    public async Task Reply_IsAcceptedOnlyFromTarget_AndDeliveredOnceToRequester()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        var otherRoom = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("foreign"), otherRoom, "pw", "owner", "他房", clientInstanceId: "inst-a", machineId: "m1");
        Assert.True(Accepted(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "req", Request())));
        h.GatewayHub.Invocations.Clear();
        // 同 UID 的另一个实例、他人、其他房间都不能代答。
        Assert.Equal("invalid_request", Error(await h.Ops.RespondMistletoeAsync(Ctx("second"), room, "req", Reply("forged"))));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondMistletoeAsync(Ctx("other"), room, "req", Reply("forged"))));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondMistletoeAsync(Ctx("foreign"), otherRoom, "req", Reply("forged"))));
        Assert.Equal("invalid_args", Error(await h.Ops.RespondMistletoeAsync(Ctx("executor"), room, "req", null)));
        var huge = Reply("huge"); huge["data"] = new string('z', 4 * 1024 * 1024);
        Assert.Equal("invalid_args", Error(await h.Ops.RespondMistletoeAsync(Ctx("executor"), room, "req", huge)));
        Assert.True(Accepted(await h.Ops.RespondMistletoeAsync(Ctx("executor"), room, "req", Reply("actual"))));
        Assert.Equal("invalid_request", Error(await h.Ops.RespondMistletoeAsync(Ctx("executor"), room, "req", Reply("again"))));
        h.GatewayHub.Verify(x => x.Clients.Client("viewer"), Times.Once);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsResult(args[0], "req", "actual")), It.IsAny<CancellationToken>()), Times.Once);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsResult(args[0], "req", "forged")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpiredRequest_RejectsReplyEvenFromTarget()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        Assert.True(Accepted(await h.Ops.RequestMistletoeAsync(Ctx("viewer"), room, "owner", "inst-a", "expired", Request())));
        var pending = h.Ops.GetType().GetField("_mistletoePending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Ops)!;
        var arguments = new object?[] { "expired", null };
        Assert.True((bool)pending.GetType().GetMethod("TryGetValue")!.Invoke(pending, arguments)!);
        var expired = arguments[1]!.GetType().GetMethod("<Clone>$")!.Invoke(arguments[1], null)!;
        expired.GetType().GetProperty("Expires")!.SetValue(expired, DateTime.UtcNow.AddSeconds(-1));
        pending.GetType().GetProperty("Item")!.SetValue(pending, expired, ["expired"]);
        h.GatewayHub.Invocations.Clear();
        Assert.Equal("invalid_request", Error(await h.Ops.RespondMistletoeAsync(Ctx("executor"), room, "expired", Reply("late"))));
        h.GatewayHub.Verify(x => x.Clients.Client("viewer"), Times.Never);
    }

    [Fact]
    public async Task Dispatcher_RoutesBothCommands()
    {
        var (h, room) = await RoomWithTwoInstancesAsync();
        Assert.Null(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(Ctx("viewer"), GatewayTestHarness.HelloEnvelope())));
        Assert.Null(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(Ctx("executor"), GatewayTestHarness.HelloEnvelope())));
        var request = await h.Dispatcher.DispatchAsync(Ctx("viewer"), Command("mistletoe.request", new JsonObject
        {
            ["roomCode"] = room, ["targetUid"] = "owner", ["targetInstanceId"] = "inst-a", ["requestId"] = "via-dispatch", ["request"] = Request()
        }));
        Assert.Null(GatewayTestHarness.ErrorCode(request));
        Assert.True(request.Payload?["accepted"]?.GetValue<bool>() == true);
        var reply = await h.Dispatcher.DispatchAsync(Ctx("executor"), Command("mistletoe.respond",
            new JsonObject { ["roomCode"] = room, ["requestId"] = "via-dispatch", ["reply"] = Reply("dispatched") }));
        Assert.Null(GatewayTestHarness.ErrorCode(reply));
        Assert.True(reply.Payload?["accepted"]?.GetValue<bool>() == true);
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(GatewayProtocol.Callbacks.Event,
            It.Is<object?[]>(args => args.Length == 1 && IsResult(args[0], "via-dispatch", "dispatched")), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static GatewayEnvelope Command(string name, JsonObject payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command, Name = name, RequestId = Guid.NewGuid().ToString("N"), Payload = payload
    };

    private static bool IsNamed(object? value, string name) => value is GatewayEnvelope env && env.Name == name;

    private static bool IsRequested(object? value, string requestId, string requester)
        => value is GatewayEnvelope env && env.Name == "mistletoe.requested"
            && env.Payload?["requestId"]?.GetValue<string>() == requestId
            && env.Payload?["requesterConnectionId"]?.GetValue<string>() == requester
            && env.Payload?["expiresAtUtc"] != null
            && env.Payload?["request"]?["operation"]?.GetValue<string>() == "startup.get";

    private static bool IsResult(object? value, string requestId, string marker)
        => value is GatewayEnvelope env && env.Name == "mistletoe.result"
            && env.Payload?["requestId"]?.GetValue<string>() == requestId
            && env.Payload?["reply"]?["message"]?.GetValue<string>() == marker;
}
