using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RemoteCenterRoutingTests
{
    private static GatewayHandlerContext Ctx(string id) => GatewayHandlerContext.Legacy(id);
    private static string Room() => "C" + Guid.NewGuid().ToString("N")[..8];

    private static RemoteCommand Request(string room, string sender, string targetInstance) => new()
    {
        Cmd = "center.request", RoomCode = room, SenderUid = sender, Sender = "监控端",
        Target = ["sameUid"], TargetInstanceId = targetInstance,
        CommandId = Guid.NewGuid().ToString("N"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20)
    };

    [Fact]
    public async Task CenterRequest_TargetsOneInstance_AndNeverQueuesOffline()
    {
        var h = new GatewayTestHarness();
        var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("exec-a"), room, "pw", "sameUid", "A",
            clientInstanceId: "instance-a", machineId: "machine-a");
        await h.Ops.JoinControlRoomAsync(Ctx("exec-b"), room, "pw", "sameUid", "B",
            clientInstanceId: "instance-b", machineId: "machine-b");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "sameUid", "监控端", isRemote: true);
        var a = new Mock<Microsoft.AspNetCore.SignalR.ISingleClientProxy>();
        var b = new Mock<Microsoft.AspNetCore.SignalR.ISingleClientProxy>();
        a.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        b.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        h.LegacyHub.Setup(x => x.Clients.Client("exec-a")).Returns(a.Object);
        h.LegacyHub.Setup(x => x.Clients.Client("exec-b")).Returns(b.Object);

        await h.Ops.SendRemoteCommandAsync(Ctx("observer"), Request(room, "sameUid", "instance-a"));
        a.Verify(x => x.SendCoreAsync("RemoteCommand", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        b.Verify(x => x.SendCoreAsync("RemoteCommand", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("sameUid"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.SendRemoteCommandAsync(Ctx("observer"), Request(room, "sameUid", "missing-instance")));
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("sameUid"));
    }

    [Fact]
    public async Task CenterRequest_RejectsSenderUidSpoofing()
    {
        var h = new GatewayTestHarness();
        var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("exec"), room, "pw", "sameUid", "执行端",
            clientInstanceId: "instance-a");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "sameUid", "监控端", isRemote: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.SendRemoteCommandAsync(Ctx("observer"), Request(room, "otherUid", "instance-a")));
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("sameUid"));
    }

    [Fact]
    public async Task CenterReply_ReachesObserverConnection_AndIsNeverCached()
    {
        var h = new GatewayTestHarness();
        var room = Room();
        await h.Ops.JoinControlRoomAsync(Ctx("exec"), room, "pw", "sameUid", "执行端",
            clientInstanceId: "instance-a", machineId: "machine-a");
        await h.Ops.JoinControlRoomAsync(Ctx("observer"), room, "pw", "sameUid", "监控端",
            isRemote: true);
        var observer = new Mock<Microsoft.AspNetCore.SignalR.ISingleClientProxy>();
        observer.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        h.LegacyHub.Setup(x => x.Clients.Client("observer")).Returns(observer.Object);
        var reply = new RemoteCommand
        {
            Cmd = "center.data", RoomCode = room, SenderUid = "sameUid", Sender = "执行端",
            Target = ["sameUid"], CommandId = Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20)
        };
        await h.Ops.SendRemoteCommandAsync(Ctx("exec"), reply);
        observer.Verify(x => x.SendCoreAsync("RemoteCommand", It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("sameUid"));
    }
}
