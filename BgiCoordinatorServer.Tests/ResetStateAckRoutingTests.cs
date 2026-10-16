using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
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
