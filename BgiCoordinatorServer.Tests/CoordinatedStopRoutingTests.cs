using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class CoordinatedStopRoutingTests
{
    private static async Task<(GatewayTestHarness H, GatewayHandlerContext[] Clients, string Room)> Setup()
    {
        var h = new GatewayTestHarness();
        var room = "S" + Guid.NewGuid().ToString("N")[..8];
        var clients = Enumerable.Range(0, 2).Select(_ => GatewayHandlerContext.Legacy(Guid.NewGuid().ToString("N"))).ToArray();
        for (var i = 0; i < clients.Length; i++)
        {
            await h.Ops.JoinControlRoomAsync(clients[i], room, "pw", "u" + i, "name" + i, clientInstanceId: "i" + i);
            await h.Ops.ReportControlStatusAsync(clients[i], new ControlStatus
            {
                RoomCode = room, PlayerUid = "u" + i,
                OnlineHoeingGroupNames = ["group" + i], OnlineHoeingGroupTypes = ["group"]
            });
        }
        return (h, clients, room);
    }

    private static RemoteCommand Stop(string room, string[]? targets = null, Dictionary<string, object>? parameters = null) => new()
    {
        RoomCode = room, Cmd = "stop", SenderUid = "u0", Sender = "name0",
        CommandId = Guid.NewGuid().ToString("N"), Target = targets?.ToList() ?? ["*"], Params = parameters
    };

    private static RemoteCommand[] Delivered(GatewayTestHarness h) => h.LegacyClientProxy.Invocations
        .Where(i => i.Method.Name == "SendCoreAsync" && i.Arguments[0] as string == "RemoteCommand")
        .Select(i => Assert.IsType<RemoteCommand>(Assert.IsType<object?[]>(i.Arguments[1])[0])).ToArray();

    [Fact]
    public async Task BroadcastFreezesEachTargetsBatchAndPreservesSourceCommand()
    {
        var (h, clients, room) = await Setup();
        var batch = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "first", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        var source = Stop(room, parameters: new() { ["hoeingBatchIds"] = new Dictionary<string, string>
        { ["u0"] = batch.BatchId, ["u1"] = "" } });
        await h.Ops.SendRemoteCommandAsync(clients[0], source);

        var delivered = Delivered(h);
        Assert.Equal(2, delivered.Length);
        Assert.Equal(batch.BatchId, delivered.Single(c => c.Target.Single() == "u0").Params!["hoeingBatchId"]);
        Assert.Equal("", delivered.Single(c => c.Target.Single() == "u1").Params!["hoeingBatchId"]);
        Assert.True(source.Params!.ContainsKey("hoeingBatchIds"));
        Assert.False(source.Params.ContainsKey("hoeingBatchId"));
        Assert.Equal(new[] { "*" }, source.Target);
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(clients[0])!.Phase);
    }

    [Fact]
    public async Task StaleExpectedBatchCannotBeReboundToSuccessor()
    {
        var (h, clients, room) = await Setup();
        var first = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "first", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        await h.Ops.ResetCoordinatedBatchAsync(clients[0], "reset", ["u0"], expectedBatchId: first.BatchId);
        var next = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "next", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        await h.Ops.SendRemoteCommandAsync(clients[0], Stop(room, ["u0"], new() { ["hoeingBatchId"] = first.BatchId }));
        Assert.Empty(Delivered(h));
        Assert.Equal(next.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);

        await h.Ops.SendRemoteCommandAsync(clients[0], Stop(room, ["u0"], new() { ["hoeingBatchId"] = "" }));
        Assert.Empty(Delivered(h));
        // An unversioned delayed stop must not be rebound to whichever batch is current.
        await h.Ops.SendRemoteCommandAsync(clients[0], Stop(room, ["u0"]));
        Assert.Empty(Delivered(h));
        Assert.Equal(next.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
    }

    [Fact]
    public async Task PerUidCallerExpectationsAreNotSharedAcrossBroadcastTargets()
    {
        var (h, clients, room) = await Setup();
        var batch = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "first", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        var parameters = new Dictionary<string, object>
        {
            ["hoeingBatchIds"] = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["u0"] = batch.BatchId, ["u1"] = "old-peer-batch"
            })
        };
        await h.Ops.SendRemoteCommandAsync(clients[0], Stop(room, parameters: parameters));
        var delivered = Assert.Single(Delivered(h));
        Assert.Equal("u0", Assert.Single(delivered.Target));
        Assert.Equal(batch.BatchId, delivered.Params!["hoeingBatchId"]);
        Assert.False(delivered.Params.ContainsKey("hoeingBatchIds"));
    }

    [Fact]
    public async Task StopIsNeverReplayedAfterAnOfflineMemberReturns()
    {
        var (h, clients, room) = await Setup();
        h.RoomManager.RemoveFromControlRoom("CTRL_" + room, clients[1].ConnectionId);
        await h.Ops.SendRemoteCommandAsync(clients[0], Stop(room, ["u1"]));
        Assert.Empty(Delivered(h));
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("u1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserAllStop_IsDeliveredDespiteStaleBatchOrUnavailableState(bool broadcast)
    {
        var (h, clients, room) = await Setup();
        var batch = await h.Ops.BeginManualCoordinatedBatchAsync(clients[0], "current", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        var stop = Stop(room, broadcast ? ["*"] : ["u0"], new()
        {
            ["stopScope"] = System.Text.Json.JsonSerializer.SerializeToElement("all"),
            ["hoeingBatchIds"] = new Dictionary<string, string> { ["u0"] = "obsolete", ["u1"] = "unknown" }
        });
        await h.Ops.SendRemoteCommandAsync(clients[0], stop);
        Assert.Equal(broadcast ? 2 : 1, Delivered(h).Length);
        Assert.All(Delivered(h), sent => Assert.Equal("all", sent.Params!["stopScope"].ToString()));
        Assert.All(Delivered(h), sent => Assert.Equal(batch.Generation, sent.Params!["hoeingStopGeneration"]));
        Assert.False(stop.Params!.ContainsKey("hoeingStopGeneration"));
        var state = h.Ops.CurrentCoordinatedBatch(clients[0])!;
        Assert.Equal(batch.BatchId, state.BatchId);
        Assert.Equal("aborted", state.Phase);
        Assert.Equal("user_stop_all", state.Reason);
    }
}
