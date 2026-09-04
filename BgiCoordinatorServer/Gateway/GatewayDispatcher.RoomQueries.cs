using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：房间查询族（room.listOnline/getConfig/getRoundHostOrder/getState）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRoomQueries()
    {
        _queries[GatewayProtocol.Names.RoomListOnline] = async (env, ctx) =>
        {
            var rooms = await _ops.GetOnlineRoomsAsync(ctx);
            return new { rooms };
        };

        _queries[GatewayProtocol.Names.RoomGetConfig] = async (env, ctx) =>
        {
            var config = await _ops.GetRoomConfigAsync(ctx);
            return new { config };
        };

        _queries[GatewayProtocol.Names.RoomGetRoundHostOrder] = async (env, ctx) =>
        {
            var order = await _ops.GetRoundHostOrderAsync(ctx);
            return new { order };
        };

        // §4.3 分组收敛：6 个状态查询聚合为 room.getState，payload.section 区分
        _queries[GatewayProtocol.Names.RoomGetState] = async (env, ctx) =>
        {
            var section = GetString(env, "section");
            object? value = section switch
            {
                "hostReady" => await _ops.IsHostReadyAsync(ctx),
                "hostRouteListUploaded" => await _ops.IsHostRouteListUploadedAsync(ctx),
                "hostRouteList" => await _ops.GetHostRouteListAsync(ctx),
                "hostRouteListStatus" => await _ops.GetHostRouteListStatusAsync(ctx),
                "worldJoinedCount" => await _ops.GetWorldJoinedCountAsync(ctx),
                "memberProgress" => await _ops.GetMemberProgressAsync(ctx, GetString(env, "playerUid")),
                _ => null,
            };
            if (value == null && section != "memberProgress")
            {
                return (object)new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = $"未知 section: {section}" } };
            }
            return new { section, value };
        };
    }
}
