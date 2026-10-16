using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class LocalFinishCapabilityTests
{
    [Fact]
    public async Task FinalPeerCompletionNotifiesAlreadyReleasedMembersWithoutAnotherPoll()
    {
        var h = new GatewayTestHarness();
        var clients = new[] { GatewayHandlerContext.V3("finish-one"), GatewayHandlerContext.V3("finish-two") };
        foreach (var (client, i) in clients.Select((c, i) => (c, i)))
        {
            await h.Dispatcher.DispatchAsync(client, GatewayTestHarness.HelloEnvelope());
            await h.Ops.JoinControlRoomAsync(client, "finish-notify", "pw", "u" + i, "n", clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(client, new ControlStatus
            {
                RoomCode = "finish-notify", PlayerUid = "u" + i, ExpectedHoeingPlayers = 2,
                OnlineHoeingGroupNames = ["group"], OnlineHoeingGroupTypes = ["group"],
            });
        }
        var state = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "finish", ["u0", "u1"], DateTimeOffset.UtcNow.AddMinutes(2));
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            for (var i = 0; i < 2; i++)
                state = await h.Ops.UpdateCoordinatedBatchAsync(clients[i], state.Generation, "u" + i,
                    state.BatchId, "group", 0, 0, "", stage);
        state = await h.Ops.UpdateCoordinatedBatchAsync(clients[0], state.Generation, "u0", state.BatchId, "group", 0, 0, "succeeded");
        Assert.Equal("running", state.Phase);
        var before = h.GatewayClientProxy.Invocations.Count;
        state = await h.Ops.UpdateCoordinatedBatchAsync(clients[1], state.Generation, "u1", state.BatchId, "group", 0, 0, "succeeded");
        Assert.Equal("completed", state.Phase);
        Assert.Equal(before + 2, h.GatewayClientProxy.Invocations.Count);
        Assert.All(state.Members, m => Assert.Equal("succeeded", m.Result));
        state = await h.Ops.UpdateCoordinatedBatchAsync(clients[1], state.Generation, "u1", state.BatchId, "group", 1, 0, "", "cleaned");
        Assert.False(state.CleanupComplete);
        state = await h.Ops.UpdateCoordinatedBatchAsync(clients[0], state.Generation, "u0", state.BatchId, "group", 1, 0, "", "cleaned");
        Assert.True(state.CleanupComplete);
        Assert.Equal(before + 2, h.GatewayClientProxy.Invocations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFinishProtocolsCannotHalfStartManualOrScheduledBatch(bool scheduled)
    {
        var h = new GatewayTestHarness();
        var clients = new[] { GatewayHandlerContext.V3("one"), GatewayHandlerContext.V3("two") };
        foreach (var (client, i) in clients.Select((c, i) => (c, i)))
        {
            await h.Dispatcher.DispatchAsync(client, GatewayTestHarness.HelloEnvelope());
            await h.Ops.JoinControlRoomAsync(client, "finish-cap", "pw", "u" + i, "name" + i, clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(client, new ControlStatus
            {
                RoomCode = "finish-cap", PlayerUid = "u" + i, ExpectedHoeingPlayers = 2,
                OnlineHoeingGroupNames = ["group"], OnlineHoeingGroupTypes = ["group"],
            });
        }
        h.Capabilities.UnsupportedCapabilities.Add(("two", GatewayProtocol.LocalFinishCapability));
        if (scheduled)
        {
            await h.Ops.ReportOnlineEventAsync(clients[0], 1, true);
            await h.Ops.ReportOnlineEventAsync(clients[1], 1, true);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "request", ["u0", "u1"], DateTimeOffset.UtcNow.AddMinutes(2)));
            Assert.Contains("hoeing_local_finish_capability_required:u1", error.Message);
        }
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));
        Assert.Contains(GatewayProtocol.LocalFinishCapability, GatewayProtocol.ServerCapabilities);
    }
}
