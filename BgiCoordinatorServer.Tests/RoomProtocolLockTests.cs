using BgiCoordinatorServer.Gateway;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>§4.7 房间协议锁定测试：同一房间不允许新旧协议混用，按建房者客户端协议锁定。</summary>
public class RoomProtocolLockTests
{
    // 两客户端 reportedVersion 保持一致，避免版本一致性校验干扰协议锁定断言
    private const string Version = "1.0.0";

    [Fact]
    public async Task V3CreatedRoom_LocksV3_LegacyJoinRejected()
    {
        var h = new GatewayTestHarness();

        var (code, protocol) = await h.Ops.CreateRoomAsync(
            GatewayHandlerContext.V3("conn-v3-host"), playerName: "host", reportedVersion: Version);

        Assert.Equal(GatewayProtocol.RoomProtocols.V3, protocol);
        Assert.Equal(GatewayProtocol.RoomProtocols.V3, h.RoomManager.GetRoom(code)!.Protocol);

        var (success, roomProtocol, error) = await h.Ops.JoinRoomAsync(
            GatewayHandlerContext.Legacy("conn-legacy-joiner"), code, playerName: "m", reportedVersion: Version);

        Assert.False(success);
        Assert.Equal("room_protocol_mismatch", error);
        Assert.Equal(GatewayProtocol.RoomProtocols.V3, roomProtocol);
    }

    [Fact]
    public async Task LegacyCreatedRoom_StaysLegacy_V3JoinRejected_LegacyJoinAllowed()
    {
        var h = new GatewayTestHarness();

        var (code, protocol) = await h.Ops.CreateRoomAsync(
            GatewayHandlerContext.Legacy("conn-legacy-host"), playerName: "host", reportedVersion: Version);

        Assert.Equal(GatewayProtocol.RoomProtocols.Legacy, protocol);
        Assert.Equal(GatewayProtocol.RoomProtocols.Legacy, h.RoomManager.GetRoom(code)!.Protocol);

        // V3 加入 legacy 房间 → 拒绝
        var (v3Success, _, v3Error) = await h.Ops.JoinRoomAsync(
            GatewayHandlerContext.V3("conn-v3-joiner"), code, playerName: "m1", reportedVersion: Version);
        Assert.False(v3Success);
        Assert.Equal("room_protocol_mismatch", v3Error);

        // Legacy 加入 legacy 房间 → 放行
        var (legacySuccess, legacyProtocol, legacyError) = await h.Ops.JoinRoomAsync(
            GatewayHandlerContext.Legacy("conn-legacy-joiner"), code, playerName: "m2", reportedVersion: Version);
        Assert.True(legacySuccess);
        Assert.Null(legacyError);
        Assert.Equal(GatewayProtocol.RoomProtocols.Legacy, legacyProtocol);
    }
}
