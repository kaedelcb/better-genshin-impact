using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class CoordinatedBatchGatewayTests
{
    private static GatewayEnvelope Update(string result = "", string batchId = "", int generation = 7,
        string layout = "group,group") => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = GatewayProtocol.Names.ControlBatchUpdate,
        RequestId = Guid.NewGuid().ToString("N"),
        Payload = GatewayEnvelope.ToPayload(new
        { generation, token = "token", batchId, layout, index = 0, attempt = 0, result })
    };

    private static GatewayEnvelope Reset(string resetId) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = GatewayProtocol.Names.ControlBatchReset,
        RequestId = Guid.NewGuid().ToString("N"),
        Payload = GatewayEnvelope.ToPayload(new { resetId, targetUids = new[] { "*" } })
    };

    [Fact]
    public async Task AllReadyCreatesAuthorityAndGatewayReturnsRealSnapshot()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(ctx, GatewayTestHarness.HelloEnvelope());
        var room = "B" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(ctx, room, "pw", "uid", "name");
        var missing = await h.Dispatcher.DispatchAsync(ctx, Update());
        Assert.NotNull(GatewayTestHarness.ErrorCode(missing));
        await h.Ops.ReportControlStatusAsync(ctx, new BgiCoordinatorServer.Models.ControlStatus { RoomCode = room, PlayerUid = "uid", ExpectedHoeingPlayers = 1 });
        await h.Ops.ReportOnlineEventAsync(ctx, 7, true);
        var response = await h.Dispatcher.DispatchAsync(ctx, Update());
        Assert.True(GatewayTestHarness.ErrorCode(response) == null, response.Payload?.ToJsonString());
        var state = response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
        Assert.Equal("running", state.Phase);
        Assert.Equal(7, state.Generation);
        var resetResponse = await h.Dispatcher.DispatchAsync(ctx, Reset("reset-1"));
        Assert.Null(GatewayTestHarness.ErrorCode(resetResponse));
        var reset = resetResponse.Payload!.Deserialize<CoordinatedBatchResetResult>(GatewayJson.Options)!;
        Assert.True(reset.Found);
        Assert.Equal("reset-1", reset.ResetId);
        Assert.Equal("aborted", reset.Phase);
        Assert.Equal("manual_reset", reset.Reason);
        var afterReset = await h.Dispatcher.DispatchAsync(ctx, Update("", state.BatchId));
        Assert.Equal("aborted", afterReset.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!.Phase);
        var done = await h.Dispatcher.DispatchAsync(ctx, Update("succeeded", state.BatchId));
        Assert.Equal("aborted", done.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!.Phase);
        // Joining the control room does not let an unrelated connection claim its roster UID.
        var outsider = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(outsider, GatewayTestHarness.HelloEnvelope());
        Assert.NotNull(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(outsider, Update())));
    }

    [Fact]
    public async Task UnregisteredPreviousRound_NextAllReadyHasMatchingBatchAuthority()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(ctx, GatewayTestHarness.HelloEnvelope());
        var room = "B" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(ctx, room, "pw", "uid", "name");
        await h.Ops.ReportControlStatusAsync(ctx, new BgiCoordinatorServer.Models.ControlStatus
        {
            RoomCode = room, PlayerUid = "uid", ExpectedHoeingPlayers = 1,
        });
        await h.Ops.ReportOnlineEventAsync(ctx, 48, true);
        // 客户端在 task.suspend 之前退出：第 48 代从未向批次服务登记。
        await h.Ops.ReportOnlineEventAsync(ctx, 49, true);

        var response = await h.Dispatcher.DispatchAsync(ctx, Update(generation: 49));
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        var state = response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
        Assert.Equal(49, state.Generation);
        Assert.Equal("running", state.Phase);
        Assert.NotNull(GatewayTestHarness.ErrorCode(
            await h.Dispatcher.DispatchAsync(ctx, Update(generation: 48))));
    }

    [Fact]
    public async Task ActivePreviousRound_NextGenerationDoesNotBroadcastPhantomAllReady()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(ctx, GatewayTestHarness.HelloEnvelope());
        var room = "B" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(ctx, room, "pw", "uid", "name");
        await h.Ops.ReportControlStatusAsync(ctx, new BgiCoordinatorServer.Models.ControlStatus
        {
            RoomCode = room, PlayerUid = "uid", ExpectedHoeingPlayers = 1,
        });
        await h.Ops.ReportOnlineEventAsync(ctx, 48, true);
        var first = await h.Dispatcher.DispatchAsync(ctx, Update(generation: 48));
        Assert.Null(GatewayTestHarness.ErrorCode(first));
        await h.Ops.ReportOnlineEventAsync(ctx, 49, true);

        var allReadyCount = h.GatewayClientProxy.Invocations.Count(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
            && call.Arguments[1] is object?[] { Length: > 0 } args
            && args[0] is GatewayEnvelope { Name: "control.allReady" });
        var abortedCount = h.GatewayGroupProxy.Invocations.Count(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
            && call.Arguments[1] is object?[] { Length: > 0 } args
            && args[0] is GatewayEnvelope { Name: "control.allReadyAbort" });
        Assert.Equal(1, allReadyCount);
        Assert.Equal(1, abortedCount);
        Assert.NotNull(GatewayTestHarness.ErrorCode(
            await h.Dispatcher.DispatchAsync(ctx, Update(generation: 49))));
        var old = await h.Dispatcher.DispatchAsync(ctx, Update(generation: 48));
        Assert.Equal("aborted", old.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!.Phase);
    }

    [Fact]
    public async Task TerminalSameGeneration_DoesNotBroadcastAnotherExecutableAllReady()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(ctx, GatewayTestHarness.HelloEnvelope());
        var room = "B" + Guid.NewGuid().ToString("N")[..8];
        await h.Ops.JoinControlRoomAsync(ctx, room, "pw", "uid", "name");
        await h.Ops.ReportControlStatusAsync(ctx, new BgiCoordinatorServer.Models.ControlStatus
        {
            RoomCode = room, PlayerUid = "uid", ExpectedHoeingPlayers = 1,
        });
        await h.Ops.ReportOnlineEventAsync(ctx, 48, true);
        var first = await h.Dispatcher.DispatchAsync(ctx, Update(generation: 48, layout: "group"));
        var batchId = first.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!.BatchId;
        var completed = await h.Dispatcher.DispatchAsync(ctx, Update("succeeded", batchId, 48, "group"));
        Assert.Equal("completed", completed.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!.Phase);

        await h.Ops.ReportOnlineEventAsync(ctx, 48, true);
        var allReadyCount = h.GatewayClientProxy.Invocations.Count(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
            && call.Arguments[1] is object?[] { Length: > 0 } args
            && args[0] is GatewayEnvelope { Name: "control.allReady" });
        var abortedCount = h.GatewayGroupProxy.Invocations.Count(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
            && call.Arguments[1] is object?[] { Length: > 0 } args
            && args[0] is GatewayEnvelope { Name: "control.allReadyAbort" });
        Assert.Equal(1, allReadyCount);
        Assert.Equal(1, abortedCount);
    }
}
