using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class CoordinatedBatchGatewayTests
{
    private static GatewayEnvelope Update(string result = "", string batchId = "") => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = GatewayProtocol.Names.ControlBatchUpdate,
        RequestId = Guid.NewGuid().ToString("N"),
        Payload = GatewayEnvelope.ToPayload(new
        { generation = 7, token = "token", batchId, layout = "group,group", index = 0, attempt = 0, result })
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
}
