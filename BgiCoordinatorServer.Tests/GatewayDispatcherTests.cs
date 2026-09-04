using BgiCoordinatorServer.Gateway;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>GatewayDispatcher 握手时序与路由测试（§4.2/§4.4）。</summary>
public class GatewayDispatcherTests
{
    private const string ConnId = "conn-gw-1";

    private static GatewayEnvelope Envelope(string name, object? payload = null) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command,
        Name = name,
        Payload = GatewayEnvelope.ToPayload(payload),
    };

    private static bool IsEvtEnvelopeNamed(object?[] args, string name)
        => args.Length == 1 && args[0] is GatewayEnvelope e && e.Name == name;

    [Fact]
    public async Task Dispatch_WithoutHello_ReturnsHandshakeRequired()
    {
        var h = new GatewayTestHarness();
        var resp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId),
            Envelope(GatewayProtocol.Names.RoomCreate, new { playerName = "t", expectedPlayerCount = 4 }));

        Assert.Equal("handshake_required", GatewayTestHarness.ErrorCode(resp));
        Assert.False(h.Tracker.IsV3(ConnId));
    }

    [Fact]
    public async Task Hello_V3_NegotiatesCapabilities_AndRegistersSession()
    {
        var h = new GatewayTestHarness();
        var resp = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3(ConnId),
            GatewayTestHarness.HelloEnvelope(protocolVersion: 3));

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        Assert.NotNull(resp.Payload);
        Assert.Equal(GatewayProtocol.MinimumClientProtocol,
            resp.Payload["minimumClientProtocol"]!.GetValue<int>());
        var capabilities = resp.Payload["capabilities"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(GatewayProtocol.ServerCapabilities, capabilities);
        Assert.True(h.Tracker.IsV3(ConnId));
    }

    [Fact]
    public async Task Hello_ProtocolTooOld_Rejected_AndNotRegistered()
    {
        var h = new GatewayTestHarness();
        var resp = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3(ConnId),
            GatewayTestHarness.HelloEnvelope(protocolVersion: 1));

        Assert.Equal("protocol_too_old", GatewayTestHarness.ErrorCode(resp));
        Assert.False(h.Tracker.IsV3(ConnId));
    }

    [Fact]
    public async Task Dispatch_UnknownName_ReturnsUnsupportedOperation()
    {
        var h = new GatewayTestHarness();
        await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId), GatewayTestHarness.HelloEnvelope());

        var resp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId),
            Envelope("不存在的名字"));

        Assert.Equal("unsupported_operation", GatewayTestHarness.ErrorCode(resp));
    }

    [Fact]
    public async Task WrongChannel_QueryNameOnDispatch_AndCommandNameOnQuery()
    {
        var h = new GatewayTestHarness();
        await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId), GatewayTestHarness.HelloEnvelope());

        // 查询名走命令通道
        var r1 = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId),
            Envelope(GatewayProtocol.Names.RoomGetState));
        Assert.Equal("wrong_channel", GatewayTestHarness.ErrorCode(r1));

        // 命令名走查询通道
        var r2 = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3(ConnId),
            Envelope(GatewayProtocol.Names.RoomCreate, new { playerName = "t", expectedPlayerCount = 4 }));
        Assert.Equal("wrong_channel", GatewayTestHarness.ErrorCode(r2));
    }

    [Fact]
    public async Task Dispatch_RoomCreate_ReturnsV3Room_AndDualBroadcasts()
    {
        var h = new GatewayTestHarness();
        await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId), GatewayTestHarness.HelloEnvelope());

        var resp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId),
            Envelope(GatewayProtocol.Names.RoomCreate, new { playerName = "t", expectedPlayerCount = 4 }));

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        var roomCode = resp.Payload!["roomCode"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(roomCode));
        Assert.Equal("v3", resp.Payload["roomProtocol"]!.GetValue<string>());

        // 双发验证：旧名 → /hub 组
        h.LegacyHub.Verify(x => x.Clients.Group(roomCode), Times.AtLeastOnce);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
                "PlayerListUpdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // evt 信封 → /gateway 组
        h.GatewayHub.Verify(x => x.Clients.Group(roomCode), Times.AtLeastOnce);
        h.GatewayGroupProxy.Verify(p => p.SendCoreAsync(
                "evt",
                It.Is<object?[]>(args => IsEvtEnvelopeNamed(args, "room.playerListChanged")),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Query_RoomListOnline_ReturnsRooms()
    {
        var h = new GatewayTestHarness();
        await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(ConnId), GatewayTestHarness.HelloEnvelope());

        var resp = await h.Dispatcher.QueryAsync(GatewayHandlerContext.V3(ConnId),
            Envelope(GatewayProtocol.Names.RoomListOnline));

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        Assert.NotNull(resp.Payload);
        Assert.NotNull(resp.Payload["rooms"]);
    }
}
