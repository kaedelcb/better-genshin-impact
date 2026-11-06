using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>严格房间身份校验启用后，真实晚到补发仍携带当前房间码，旧协议参数保持原合同。</summary>
public sealed class WorldPlanSyncReplayTests
{
    private static (GatewayTestHarness Harness, string Code) Room()
    {
        var h = new GatewayTestHarness();
        var code = h.RoomManager.CreateRoom("host", "A", null, "A", 2);
        h.RoomManager.AddPlayerForTesting(code, new PlayerInfo
        {
            ConnectionId = "member", PlayerId = "member", PlayerUid = "B", PlayerName = "B",
            Status = PlayerStatus.Pathing, LastHeartbeat = DateTime.UtcNow,
        });
        return (h, code);
    }

    private static List<GatewayEnvelope> CaptureCaller(GatewayTestHarness h)
    {
        var messages = new List<GatewayEnvelope>();
        h.GatewayClientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((name, args, _) =>
            {
                Assert.Equal(GatewayProtocol.Callbacks.Event, name);
                messages.Add(Assert.IsType<GatewayEnvelope>(Assert.Single(args)));
            }).Returns(Task.CompletedTask);
        return messages;
    }

    [Fact]
    public async Task AlreadyReleasedArrivalReplaysCurrentRoomIdentity()
    {
        var (h, code) = Room();
        var messages = CaptureCaller(h);
        h.RoomManager.GetRoom(code)!.BroadcastedSyncIds.Add("done");
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.V3("member"), "done", 1000000);
        var replay = Assert.Single(messages);
        Assert.Equal(code, replay.RoomCode);
        Assert.Equal(GatewayProtocol.LegacyEventMap["AllArrived"], replay.Name);
        Assert.Equal("done", replay.Payload!["syncPointId"]!.GetValue<string>());
    }

    [Fact]
    public async Task LaggingCallerArrivalReplayCarriesCurrentRoomIdentity()
    {
        var (h, code) = Room();
        var messages = CaptureCaller(h);
        h.RoomManager.GetRoom(code)!.Players.Single(p => p.ConnectionId == "host").CurrentProgress = 2000000;
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.V3("member"), "behind", 1000000);
        var replay = Assert.Single(messages);
        Assert.Equal(code, replay.RoomCode);
        Assert.Equal("behind", replay.Payload!["syncPointId"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedStatueBarrierReplayCarriesCurrentRoomIdentity()
    {
        var (h, code) = Room();
        var messages = CaptureCaller(h);
        h.RoomManager.GetRoom(code)!.BarrierFailures["round_rerun_statue_1"] = "not-ready";
        await h.Ops.ReportArrivalAsync(GatewayHandlerContext.V3("member"), "round_rerun_statue_1");
        var replay = Assert.Single(messages);
        Assert.Equal(code, replay.RoomCode);
        Assert.Equal(GatewayProtocol.LegacyEventMap["SyncBarrierFailed"], replay.Name);
        Assert.Equal("not-ready", replay.Payload!["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReplayRetainsPositionalArguments(bool failedBarrier)
    {
        var (h, code) = Room();
        var room = h.RoomManager.GetRoom(code)!;
        if (failedBarrier)
        {
            room.BarrierFailures["round_rerun_statue_1"] = "not-ready";
            await h.Ops.ReportArrivalAsync(GatewayHandlerContext.Legacy("member"), "round_rerun_statue_1");
            h.LegacyClientProxy.Verify(p => p.SendCoreAsync("SyncBarrierFailed",
                It.Is<object?[]>(a => a.Length == 2 && (string)a[0]! == "round_rerun_statue_1" && (string)a[1]! == "not-ready"),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            room.BroadcastedSyncIds.Add("done");
            await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("member"), "done", 1000000);
            h.LegacyClientProxy.Verify(p => p.SendCoreAsync("AllArrived",
                It.Is<object?[]>(a => a.Length == 1 && (string)a[0]! == "done"),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        h.GatewayClientProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
