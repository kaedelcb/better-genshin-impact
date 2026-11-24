using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：房间生命周期族（room.create/join/leave/close/markStarted）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRoomLifecycle()
    {
        _commands[GatewayProtocol.Names.RoomCreate] = async (env, ctx) =>
        {
            if (GetBool(env, "hostProvidesWorldOnly")) throw new InvalidOperationException("player_standby_upgrade_required");
            var result = await _ops.CreateRoomAsync(ctx,
                GetString(env, "playerName"),
                GetStringList(env, "whitelist"),
                GetString(env, "playerUid"),
                GetInt(env, "expectedPlayerCount", 4),
                GetString(env, "reportedVersion"), GetString(env, "hoeingBatchId"),
                GetStringList(env, "expectedParticipantUids"), GetStringList(env, "worldPlanParticipantUids"),
                GetBool(env, "negotiateStandby"), GetBool(env, "standbyOnly"));
            return new { roomCode = result.Code, roomProtocol = result.Protocol,
                negotiatesStandby = result.StandbySessionId != null, standbySessionId = result.StandbySessionId };
        };

        _commands[GatewayProtocol.Names.RoomJoin] = async (env, ctx) =>
        {
            var result = await _ops.JoinRoomAsync(ctx,
                env.RoomCode ?? GetString(env, "roomCode"),
                GetString(env, "playerName"),
                GetString(env, "playerUid"),
                GetString(env, "reportedVersion"), GetString(env, "hoeingBatchId"), GetString(env, "standbySessionId"),
                GetBool(env, "acceptsStandby"), GetBool(env, "standbyOnly"));
            return result.Error != null
                ? (object)new { success = false, roomProtocol = result.Protocol, error = result.Error }
                : new { success = true, roomProtocol = result.Protocol,
                    negotiatesStandby = result.StandbySessionId != null, standbySessionId = result.StandbySessionId };
        };

        _commands[GatewayProtocol.Names.RoomHostWorldUpdate] = (env, ctx) => Task.FromResult<object?>(new
        {
            state = _ops.HostWorldOperation(ctx, env.RoomCode, GetString(env, "sessionId"), GetString(env, "action"),
                GetStringList(env, "routeNames"), GetBool(env, "allowPartial"), GetInt(env, "partyCount"),
                Get<string>(env, "manifestDigest"), Get<string>(env, "routeName"), GetDouble(env, "durationSeconds")),
        });
        _queries[GatewayProtocol.Names.RoomHostWorldState] = (env, ctx) => Task.FromResult<object?>(new
        {
            state = _ops.HostWorldOperation(ctx, env.RoomCode, GetString(env, "sessionId")),
        });

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

        _commands[GatewayProtocol.Names.RoomConfirmRoundOrder] = async (env, ctx) => new
        {
            ready = await _ops.ConfirmRoundOrderAsync(ctx, env.RoomCode, GetString(env, "sessionId"),
                GetStringList(env, "order") ?? [], GetInt(env, "roundCount")),
        };

        // MarkRoomStarted / MarkRoomStartedWithProgress 聚合：completedHostUids 收进 payload
        _commands[GatewayProtocol.Names.RoomMarkStarted] = async (env, ctx) =>
        {
            await _ops.MarkRoomStartedAsync(ctx, GetStringList(env, "completedHostUids"),
                GetBool(env, "requireWorldPlanRoster"), env.RoomCode, GetBool(env, "requireRoundOrderStartup"));
            return new { ack = true };
        };
    }
}
