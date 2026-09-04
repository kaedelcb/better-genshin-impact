namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：控制房间族（control.* 共 6 条，与旧方法一一对应）。
/// JoinRejected/RemoteCommandAck 走事件（evt 双发），响应一律 ack。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterControlRoom()
    {
        _commands[GatewayProtocol.Names.ControlJoinRoom] = async (env, ctx) =>
        {
            await _ops.JoinControlRoomAsync(ctx,
                GetString(env, "roomCode"),
                GetString(env, "password"),
                GetString(env, "playerUid"),
                GetString(env, "playerName"),
                GetStringList(env, "allowedUids"),
                GetBool(env, "isRemote"),
                GetString(env, "clientInstanceId"));
            // 旧方法返回 Task 无值，JoinRejected 走事件
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlSendCommand] = async (env, ctx) =>
        {
            var command = Get<Models.RemoteCommand>(env, "command");
            if (command == null)
            {
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "payload.command 缺失或格式错误" } };
            }
            await _ops.SendRemoteCommandAsync(ctx, command);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlReportStatus] = async (env, ctx) =>
        {
            var status = Get<Models.ControlStatus>(env, "status");
            if (status == null)
            {
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "payload.status 缺失或格式错误" } };
            }
            await _ops.ReportControlStatusAsync(ctx, status);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlConfirmAllReady] = async (env, ctx) =>
        {
            await _ops.ConfirmAllReadyAsync(ctx, GetInt(env, "generation"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlReportOnlineEvent] = async (env, ctx) =>
        {
            await _ops.ReportOnlineEventAsync(ctx,
                GetInt(env, "generation"),
                GetBool(env, "isOnlineReady"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlClearOnlineHistory] = async (env, ctx) =>
        {
            await _ops.ClearOnlineHistoryAsync(ctx, GetString(env, "targetUid"));
            return new { ack = true };
        };
    }
}
