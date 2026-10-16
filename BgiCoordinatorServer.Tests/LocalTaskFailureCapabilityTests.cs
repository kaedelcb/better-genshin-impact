using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class LocalTaskFailureCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndScheduledStartRefuseAMixedRosterBeforeAnyTakeover(bool scheduled)
    {
        var h = new GatewayTestHarness();
        var clients = new[] { GatewayHandlerContext.V3("local-new"), GatewayHandlerContext.V3("local-old") };
        foreach (var (client, i) in clients.Select((c, i) => (c, i)))
        {
            await h.Dispatcher.DispatchAsync(client, GatewayTestHarness.HelloEnvelope());
            await h.Ops.JoinControlRoomAsync(client, "local-errors", "pw", "u" + i, "n", clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(client, new ControlStatus
            {
                RoomCode = "local-errors", PlayerUid = "u" + i, ExpectedHoeingPlayers = 2,
                OnlineHoeingGroupNames = ["group"], OnlineHoeingGroupTypes = ["group"],
            });
        }
        h.Capabilities.UnsupportedCapabilities.Add((clients[1].ConnectionId, GatewayProtocol.LocalTaskFailureCapability));
        if (scheduled)
        {
            await h.Ops.ReportOnlineEventAsync(clients[0], 1, true);
            await h.Ops.ReportOnlineEventAsync(clients[1], 1, true);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.BeginManualCoordinatedBatchAsync(
                clients[0], "local-error", ["u0", "u1"], DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.Contains("hoeing_local_task_failure_capability_required:u1", error.Message);
        }
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));
        Assert.Contains(GatewayProtocol.LocalTaskFailureCapability, GatewayProtocol.ServerCapabilities);
    }
}
