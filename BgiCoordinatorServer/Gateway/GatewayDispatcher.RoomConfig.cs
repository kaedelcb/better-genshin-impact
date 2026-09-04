using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：房间配置族（room.setConfig/setWhitelist/setHostRouteList/reportHostReady）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRoomConfig()
    {
        _commands[GatewayProtocol.Names.RoomSetConfig] = async (env, ctx) =>
        {
            var config = Get<RoomConfig>(env, "config");
            if (config == null)
            {
                return (object)new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "config 缺失或格式错误" } };
            }
            await _ops.SetRoomConfigAsync(ctx, config);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RoomSetWhitelist] = async (env, ctx) =>
        {
            await _ops.UpdateWhitelistAsync(ctx, GetStringList(env, "whitelist"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RoomSetHostRouteList] = async (env, ctx) =>
        {
            await _ops.SetHostRouteListAsync(ctx, GetStringList(env, "routeNames") ?? []);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RoomReportHostReady] = async (env, ctx) =>
        {
            await _ops.ReportHostReadyAsync(ctx);
            return new { ack = true };
        };
    }
}
