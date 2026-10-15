using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class QuickHoeingCompletionChoiceTests
{
    private static async Task<(GatewayTestHarness H, GatewayHandlerContext[] C, string Room)> Setup()
    {
        var h = new GatewayTestHarness();
        var room = "A" + Guid.NewGuid().ToString("N")[..8];
        var c = new[] { GatewayHandlerContext.Legacy("c0"), GatewayHandlerContext.Legacy("c1") };
        for (var i = 0; i < c.Length; i++)
        {
            await h.Ops.JoinControlRoomAsync(c[i], room, "pw", "u" + i, "成员" + i, clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(c[i], new ControlStatus
            {
                RoomCode = room, PlayerUid = "u" + i, OnlineHoeingGroupNames = ["锄地"], OnlineHoeingGroupTypes = ["group"],
                HoeingCompletionAction = new(i == 0 ? "resume" : "runSpecified", "group", i == 0 ? "" : "收尾任务", 7)
            });
        }
        return (h, c, room);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChoiceSurvivesBeginReplayAndStatusDisplay(bool useActions)
    {
        var (h, c, room) = await Setup();
        var state = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, useActions);
        Assert.Equal(useActions, state.UseCompletionActions);
        var replay = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, useActions);
        Assert.Equal(state.BatchId, replay.BatchId);
        Assert.Equal(useActions, replay.UseCompletionActions);
        Assert.Equal("resume", h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "u0").HoeingCompletionAction!.Policy);
        Assert.Equal("收尾任务", h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "u1").HoeingCompletionAction!.TaskName);
    }

    [Theory]
    [InlineData("c0")]
    [InlineData("c1")]
    public async Task UnsupportedChoiceRefusesBeforeAnyBatchIsCreated(string unsupported)
    {
        var (h, c, _) = await Setup();
        h.Capabilities.UnsupportedCapabilities.Add((unsupported, GatewayProtocol.CompletionChoiceCapability));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0", "u1"], null, true));
        Assert.Contains("hoeing_completion_choice_capability_required", error.Message);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(c[0]));
        // Default behavior remains usable with the old completion capability.
        Assert.False((await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "default", ["u0", "u1"], null)).UseCompletionActions);
    }

    [Fact]
    public async Task ReusingARequestWithAnotherChoiceCannotMutateAcceptedBatch()
    {
        var (h, c, _) = await Setup();
        var state = await h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0"], null, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.BeginManualCoordinatedBatchAsync(c[0], "request", ["u0"], null, false));
        var current = h.Ops.CurrentCoordinatedBatch(c[0])!;
        Assert.Equal(state.BatchId, current.BatchId);
        Assert.True(current.UseCompletionActions);
        Assert.Equal("registering", current.Phase);
    }

    [Fact]
    public async Task GatewayReadsTheChoiceWithoutASeparateActionConfiguration()
    {
        var (h, c, _) = await Setup();
        await h.Dispatcher.DispatchAsync(c[0], GatewayTestHarness.HelloEnvelope());
        var response = await h.Dispatcher.DispatchAsync(c[0], new GatewayEnvelope
        {
            Type = "command", Name = GatewayProtocol.Names.ControlBatchBegin, RequestId = Guid.NewGuid().ToString("N"),
            Payload = GatewayEnvelope.ToPayload(new { requestId = "wire", targetUids = new[] { "u0" }, useCompletionActions = true })
        });
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        Assert.True(response.Payload!["useCompletionActions"]!.GetValue<bool>());
        Assert.False(response.Payload.ContainsKey("completionActions"));
    }
}
