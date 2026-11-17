using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class ResetStateAckRoutingTests
{
    [Fact]
    public async Task ResetAck_FromOwnExecutor_ReturnsToSameUidObserver()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("executor"), room, "pw", "owner", "执行端");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "owner", "监控端", isRemote: true);
        var executor = TrackCommands(h, "executor");
        var observer = TrackCommands(h, "observer");
        var ack = NewCommand(room, "owner", "owner", "ack");

        await h.Ops.SendRemoteCommandAsync(Ctx("executor"), ack);

        VerifyCommand(observer, ack, Times.Once());
        VerifyCommand(executor, ack, Times.Once());
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("owner"));
    }

    [Fact]
    public async Task ResetAck_FromMember_ReturnsToRemoteOnlyRequester()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("member"), room, "pw", "memberUid", "队员");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "owner", "遥控端", isRemote: true);
        var observer = TrackCommands(h, "observer");
        var ack = NewCommand(room, "memberUid", "owner", "ack");

        await h.Ops.SendRemoteCommandAsync(Ctx("member"), ack);

        VerifyCommand(observer, ack, Times.Once());
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("owner"));
    }

    [Fact]
    public async Task ResetAck_WhenRequesterOffline_IsNeverCached()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("member"), room, "pw", "memberUid", "队员");
        var ack = NewCommand(room, "memberUid", "offlineOwner", "ack");

        await h.Ops.SendRemoteCommandAsync(Ctx("member"), ack);

        h.LegacyClientProxy.Verify(x => x.SendCoreAsync("RemoteCommand", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("offlineOwner"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetStateRequest_GoesOnlyToOnlineExecutor_AndIsNeverCached(bool executorOnline)
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("member"), room, "pw", "memberUid", "发起方");
        if (executorOnline)
            await h.Ops.JoinControlRoomAsync(Ctx("executor"), room, "pw", "target", "执行端");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "target", "监控端", isRemote: true);
        var executor = TrackCommands(h, "executor");
        var observer = TrackCommands(h, "observer");
        var request = NewCommand(room, "memberUid", "target", "reset_state");

        await h.Ops.SendRemoteCommandAsync(Ctx("member"), request);

        VerifyCommand(observer, request, Times.Never());
        VerifyCommand(executor, request, executorOnline ? Times.Once() : Times.Never());
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("target"));
    }

    [Fact]
    public async Task IssuedResetState_NormalizesServerIdentityAndLimitsWildcardToAuthorizedTargets()
    {
        var (h, room, batch) = await SetupBatch();
        var reset = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "issued", ["u0"], expectedBatchId: batch.BatchId);
        var command = NewCommand(room, "u0", "*", "reset_state");
        command.Params = new() { ["resetId"] = reset.ResetId };
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), command);
        var sent = Assert.Single(Delivered(h));
        Assert.Equal("u0", Assert.Single(sent.Target));
        Assert.Equal(batch.BatchId, sent.Params!["batchId"]);
        Assert.Equal(batch.Generation, sent.Params["generation"]);
        Assert.False(command.Params.ContainsKey("generation"));
        Assert.Equal("*", Assert.Single(command.Target));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("batchId")]
    [InlineData("target")]
    [InlineData("unissued")]
    public async Task ResetState_RejectsForgedOrOutOfScopeAuthority(string mismatch)
    {
        var (h, room, batch) = await SetupBatch();
        var reset = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "issued", ["u0"], expectedBatchId: batch.BatchId);
        var command = NewCommand(room, "u0", mismatch == "target" ? "u1" : "u0", "reset_state");
        command.Params = new()
        {
            ["resetId"] = mismatch == "unissued" ? "forged" : reset.ResetId,
            ["generation"] = mismatch == "generation" || mismatch == "unissued" ? batch.Generation + 100 : batch.Generation,
            ["batchId"] = mismatch == "batchId" ? "other-batch" : batch.BatchId
        };
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), command);
        Assert.Empty(Delivered(h));
        Assert.Equal(batch.BatchId, h.Ops.CurrentCoordinatedBatch(Ctx("owner"))!.BatchId);
        Assert.False(h.Ops.CurrentCoordinatedBatch(Ctx("owner"))!.CleanupComplete);
    }

    [Fact]
    public async Task ResetState_RejectsStaleSupersededAndReplacedExecutorReceipts()
    {
        var (h, room, batch) = await SetupBatch();
        var old = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "old", ["u0"], expectedBatchId: batch.BatchId);
        var later = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "later", ["u0"], expectedBatchId: batch.BatchId);
        var stale = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "stale", ["u0"],
            expectedBatchId: batch.BatchId, expectedGeneration: batch.Generation + 1);
        foreach (var reset in new[] { old, stale })
        {
            var command = NewCommand(room, "u0", "u0", "reset_state");
            command.Params = new() { ["resetId"] = reset.ResetId };
            await h.Ops.SendRemoteCommandAsync(Ctx("owner"), command);
        }
        await h.Ops.HandleDisconnectAsync(Ctx("owner"), null);
        await h.Ops.JoinControlRoomAsync(Ctx("replacement"), room, "pw", "u0", "替换执行端", clientInstanceId: "replacement");
        var replaced = NewCommand(room, "u1", "u0", "reset_state");
        replaced.Params = new() { ["resetId"] = later.ResetId };
        await h.Ops.SendRemoteCommandAsync(Ctx("peer"), replaced);
        Assert.Empty(Delivered(h));
    }

    [Fact]
    public async Task IndependentResetState_HasTrustedFenceButCannotTouchLaterBatch()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("owner"), room, "pw", "u0", "执行端", clientInstanceId: "instance0");
        var reset = await h.Ops.ResetCoordinatedBatchAsync(Ctx("owner"), "independent", ["u0"]);
        Assert.False(reset.Found);
        Assert.True(reset.Generation > 0);
        var command = NewCommand(room, "u0", "u0", "reset_state");
        command.Params = new() { ["resetId"] = reset.ResetId };
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), command);
        var sent = Assert.Single(Delivered(h));
        Assert.Equal("", sent.Params!["batchId"]);
        Assert.Equal(reset.Generation, sent.Params["generation"]);
        await h.Ops.ReportControlStatusAsync(Ctx("owner"), new ControlStatus
        { RoomCode = room, PlayerUid = "u0", OnlineHoeingGroupNames = ["group"], OnlineHoeingGroupTypes = ["group"] });
        var next = await h.Ops.BeginManualCoordinatedBatchAsync(Ctx("owner"), "next", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(next.Generation > reset.Generation);
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), command);
        Assert.Single(Delivered(h)); // The old independent receipt was not forwarded into its successor.
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(Ctx("owner"))!.Phase);
    }

    [Fact]
    public async Task LegacyUnfencedResetState_CannotStopCurrentBatchOrInventGeneration()
    {
        var (h, room, batch) = await SetupBatch();
        var currentTarget = NewCommand(room, "u1", "u0", "reset_state");
        await h.Ops.SendRemoteCommandAsync(Ctx("peer"), currentTarget);
        var independent = NewCommand(room, "u0", "u1", "reset_state");
        independent.Params!["generation"] = batch.Generation + 100;
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), independent);
        Assert.Empty(Delivered(h));
        independent.Params.Remove("generation");
        await h.Ops.SendRemoteCommandAsync(Ctx("owner"), independent);
        Assert.Same(independent, Assert.Single(Delivered(h)));
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(Ctx("owner"))!.Phase);
    }

    private static async Task<(GatewayTestHarness H, string Room, CoordinatedBatchSnapshot Batch)> SetupBatch()
    {
        var h = new GatewayTestHarness();
        var room = NewRoom();
        await h.Ops.JoinControlRoomAsync(Ctx("owner"), room, "pw", "u0", "执行端", clientInstanceId: "instance0");
        await h.Ops.JoinControlRoomAsync(Ctx("peer"), room, "pw", "u1", "队友", clientInstanceId: "instance1");
        await h.Ops.ReportControlStatusAsync(Ctx("owner"), new ControlStatus
        { RoomCode = room, PlayerUid = "u0", OnlineHoeingGroupNames = ["group"], OnlineHoeingGroupTypes = ["group"] });
        var batch = await h.Ops.BeginManualCoordinatedBatchAsync(Ctx("owner"), "first", ["u0"], DateTimeOffset.UtcNow.AddMinutes(1));
        return (h, room, batch);
    }

    private static RemoteCommand[] Delivered(GatewayTestHarness h) => h.LegacyClientProxy.Invocations
        .Where(i => i.Method.Name == "SendCoreAsync" && i.Arguments[0] as string == "RemoteCommand")
        .Select(i => Assert.IsType<RemoteCommand>(Assert.IsType<object?[]>(i.Arguments[1])[0])).ToArray();

    private static string NewRoom() => "C" + Guid.NewGuid().ToString("N")[..8];

    private static GatewayHandlerContext Ctx(string connectionId) => GatewayHandlerContext.Legacy(connectionId);

    private static RemoteCommand NewCommand(string room, string senderUid, string targetUid, string cmd)
    {
        var resetId = "reset_" + Guid.NewGuid().ToString("N");
        return new RemoteCommand
        {
            RoomCode = room,
            Cmd = cmd,
            Sender = "队员",
            SenderUid = senderUid,
            Target = [targetUid],
            CommandId = resetId,
            Params = cmd == "ack"
                ? new Dictionary<string, object> { ["status"] = "success", ["message"] = "执行环境已复位" }
                : new Dictionary<string, object> { ["resetId"] = resetId }
        };
    }

    private static Mock<ISingleClientProxy> TrackCommands(GatewayTestHarness h, string connectionId)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        h.LegacyHub.Setup(x => x.Clients.Client(connectionId)).Returns(proxy.Object);
        return proxy;
    }

    private static void VerifyCommand(Mock<ISingleClientProxy> proxy, RemoteCommand command, Times times)
        => proxy.Verify(x => x.SendCoreAsync("RemoteCommand",
            It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], command)),
            It.IsAny<CancellationToken>()), times);
}
