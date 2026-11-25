using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RemoteCommandInstanceRoutingTests
{
    private const string Room = "fixture-room";
    private const string Group = "CTRL_" + Room;
    private const string Uid = "member";

    private static RemoteCommand Request(string cmd, string? instanceId = "instance-a") => new()
    {
        RoomCode = Room, Cmd = cmd, SenderUid = "observer", Sender = "监控端",
        Target = [Uid], TargetInstanceId = instanceId, CommandId = Guid.NewGuid().ToString("N")
    };

    private static void AddMembers(RoomManager manager)
    {
        manager.AddToControlRoom(Group, "member-a", Uid, "A", clientInstanceId: "instance-a");
        manager.AddToControlRoom(Group, "member-b", Uid, "B", clientInstanceId: "instance-b");
        manager.AddToControlRoom("CTRL_other", "other", Uid, "Other", clientInstanceId: "instance-a");
    }

    [Theory]
    [InlineData("start_bgi")]
    [InlineData("start_group")]
    [InlineData("start_oneclick")]
    [InlineData("hotkey_execute")]
    [InlineData("stop")]
    [InlineData("close_game")]
    [InlineData("clear_online")]
    [InlineData("set_scheduled_online_time")]
    [InlineData("bind_hoeing_group")]
    [InlineData("set_quick_command")]
    [InlineData("set_task_enabled")]
    [InlineData("task_policy.pull")]
    [InlineData("task_policy.push")]
    [InlineData("remote_config.pull")]
    [InlineData("remote_config.push")]
    public void ExplicitMemberRequest_ResolvesOnlyFrozenInstance_AndCannotEnterUidCache(string cmd)
    {
        var manager = new RoomManager();
        AddMembers(manager);
        var request = Request(cmd);
        Assert.Equal("member-a", Assert.Single(manager.ResolveTargets(request)));

        manager.CachePendingCommand(Group, Uid, request);
        Assert.Empty(manager.GetAndClearPendingCommands(Group, Uid));
    }

    [Fact]
    public void ExplicitMemberRequest_RejectsInvalidOrAmbiguousSelection()
    {
        var manager = new RoomManager();
        AddMembers(manager);
        var request = Request("task_policy.pull");
        foreach (var targets in new[] { Array.Empty<string>(), new[] { "*" }, new[] { Uid, Uid }, new[] { Uid, "other" }, new[] { "" } })
        {
            request.Target = targets.ToList();
            Assert.Empty(manager.ResolveTargets(request));
        }
        request.Target = [Uid];
        request.TargetInstanceId = " ";
        Assert.Empty(manager.ResolveTargets(request));

        // 人工构造不一致的成员快照，唯一匹配无法成立时保持拒绝。
        request.TargetInstanceId = "instance-a";
        manager.GetControlRoomPlayers(Group).Single(p => p.ConnectionId == "member-b").ClientInstanceId = "instance-a";
        Assert.Empty(manager.ResolveTargets(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInstance_KeepsLegacyUidRouteAndCache(string? instanceId)
    {
        var manager = new RoomManager();
        AddMembers(manager);
        var request = Request("task_policy.pull", instanceId);
        Assert.Equal(new[] { "member-a", "member-b" }, manager.ResolveTargets(request));
        manager.RemoveFromControlRoom(Group, "member-a");
        manager.RemoveFromControlRoom(Group, "member-b");
        manager.CachePendingCommand(Group, Uid, request);
        Assert.Same(request, Assert.Single(manager.GetAndClearPendingCommands(Group, Uid)));
    }

    [Fact]
    public void PendingUidCache_DoesNotReplayRequestThatNowCarriesAnInstance()
    {
        var manager = new RoomManager();
        var request = Request("task_policy.pull", null);
        manager.CachePendingCommand(Group, Uid, request);
        request.TargetInstanceId = "instance-a";
        Assert.Empty(manager.GetAndClearPendingCommands(Group, Uid));
    }

    [Fact]
    public void SharedRoomCommands_KeepTheirExistingTargetResolution()
    {
        var manager = new RoomManager();
        AddMembers(manager);
        var batch = Request("start_group");
        batch.Params = new() { ["key"] = "一键锄地" };
        var allStop = Request("stop");
        allStop.Params = new() { ["stopScope"] = "all" };
        foreach (var request in new[] { batch, allStop, Request("reset_state") })
        {
            request.Target = ["*"];
            Assert.Equal(new[] { "member-a", "member-b" }, manager.ResolveTargets(request));
        }
    }

    private static Mock<ISingleClientProxy> Track(GatewayTestHarness h, string connectionId)
    {
        var proxy = new Mock<ISingleClientProxy>();
        proxy.Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        h.LegacyHub.Setup(x => x.Clients.Client(connectionId)).Returns(proxy.Object);
        return proxy;
    }

    private static void VerifyCommand(Mock<ISingleClientProxy> proxy, Times times)
        => proxy.Verify(x => x.SendCoreAsync("RemoteCommand", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task ExplicitRequest_RemainsBoundWhenSelectedMemberGoesOffline()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.Legacy("observer-connection");
        var memberA = GatewayHandlerContext.Legacy("member-a");
        var memberB = GatewayHandlerContext.Legacy("member-b");
        Assert.True((await h.Ops.JoinControlRoomAsync(memberA, Room, "fixture-pw", Uid, "A",
            clientInstanceId: "instance-a")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(memberB, Room, "fixture-pw", Uid, "B",
            clientInstanceId: "instance-b")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(ctx, Room, "fixture-pw", "observer", "Observer",
            isRemote: true)).Success);
        var a = Track(h, "member-a");
        var b = Track(h, "member-b");

        await h.Ops.SendRemoteCommandAsync(ctx, Request("task_policy.pull"));
        VerifyCommand(a, Times.Once());
        VerifyCommand(b, Times.Never());

        Assert.True((await h.Ops.LeaveControlRoomAsync(memberA, Room)).Success);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.SendRemoteCommandAsync(ctx, Request("task_policy.pull")));
        Assert.Equal("target_instance_unavailable_or_ambiguous", rejected.Message);
        Assert.True((await h.Ops.LeaveControlRoomAsync(memberB, Room)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Ops.SendRemoteCommandAsync(ctx, Request("task_policy.pull")));

        var next = Track(h, "member-next");
        Assert.True((await h.Ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy("member-next"),
            Room, "fixture-pw", Uid, "Next", clientInstanceId: "instance-next")).Success);
        VerifyCommand(a, Times.Once());
        VerifyCommand(b, Times.Never());
        VerifyCommand(next, Times.Never());
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands(Group, Uid));
    }

    [Fact]
    public async Task ExplicitStop_RetainsExistingBatchExpectation()
    {
        var h = new GatewayTestHarness();
        var ctx = GatewayHandlerContext.Legacy("observer-connection");
        Assert.True((await h.Ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy("member-a"),
            Room, "fixture-pw", Uid, "A", clientInstanceId: "instance-a")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy("member-b"),
            Room, "fixture-pw", Uid, "B", clientInstanceId: "instance-b")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(ctx, Room, "fixture-pw", "observer", "Observer",
            isRemote: true)).Success);
        var a = Track(h, "member-a");
        var b = Track(h, "member-b");

        var stale = Request("stop");
        stale.Params = new() { ["hoeingBatchId"] = "old-batch" };
        await h.Ops.SendRemoteCommandAsync(ctx, stale);
        VerifyCommand(a, Times.Never());
        VerifyCommand(b, Times.Never());

        var current = Request("stop");
        current.Params = new() { ["hoeingBatchId"] = "" };
        await h.Ops.SendRemoteCommandAsync(ctx, current);
        VerifyCommand(a, Times.Once());
        VerifyCommand(b, Times.Never());
    }

    [Theory]
    [InlineData("center.data")]
    [InlineData("remote_config.data")]
    [InlineData("remote_config.push_result")]
    [InlineData("task_policy.data")]
    [InlineData("task_policy.push_result")]
    [InlineData("ack")]
    public async Task ReplyWithSourceInstanceMetadata_KeepsExecutorAndObserverRoutes(string cmd)
    {
        var h = new GatewayTestHarness();
        var source = GatewayHandlerContext.Legacy("source");
        Assert.True((await h.Ops.JoinControlRoomAsync(source, Room, "fixture-pw", "source-uid", "Source",
            clientInstanceId: "source-instance")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy("executor"), Room, "fixture-pw", Uid, "Executor",
            clientInstanceId: "receiver-instance")).Success);
        Assert.True((await h.Ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy("observer-connection"), Room, "fixture-pw", Uid, "Observer",
            isRemote: true)).Success);
        var executor = Track(h, "executor");
        var observer = Track(h, "observer-connection");
        var reply = Request(cmd, "source-instance");
        reply.SenderUid = "source-uid";

        await h.Ops.SendRemoteCommandAsync(source, reply);
        VerifyCommand(executor, Times.Once());
        VerifyCommand(observer, Times.Once());
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands(Group, Uid));
    }
}
