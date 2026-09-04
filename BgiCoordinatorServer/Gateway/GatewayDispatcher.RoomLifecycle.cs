using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：房间生命周期族（room.create/join/leave/close/markStarted）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRoomLifecycle()
    {
        _commands[GatewayProtocol.Names.RoomCreate] = async (env, ctx) =>
        {
            var (code, protocol) = await _ops.CreateRoomAsync(ctx,
                GetString(env, "playerName"),
                GetStringList(env, "whitelist"),
                GetString(env, "playerUid"),
                GetInt(env, "expectedPlayerCount", 4),
                GetString(env, "reportedVersion"));
            return new { roomCode = code, roomProtocol = protocol };
        };

        _commands[GatewayProtocol.Names.RoomJoin] = async (env, ctx) =>
        {
            var (success, protocol, error) = await _ops.JoinRoomAsync(ctx,
                env.RoomCode ?? GetString(env, "roomCode"),
                GetString(env, "playerName"),
                GetString(env, "playerUid"),
                GetString(env, "reportedVersion"));
            return error != null
                ? (object)new { success, roomProtocol = protocol, error }
                : new { success, roomProtocol = protocol };
        };

        _commands[GatewayProtocol.Names.RoomLeave] = async (env, ctx) =>
        {
            await _ops.LeaveRoomAsync(ctx);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RoomClose] = async (env, ctx) =>
        {
            await _ops.CloseRoomAsync(ctx);
            return new { ack = true };
        };

        // MarkRoomStarted / MarkRoomStartedWithProgress 聚合：completedHostUids 收进 payload
        _commands[GatewayProtocol.Names.RoomMarkStarted] = async (env, ctx) =>
        {
            await _ops.MarkRoomStartedAsync(ctx, GetStringList(env, "completedHostUids"));
            return new { ack = true };
        };
    }
}
