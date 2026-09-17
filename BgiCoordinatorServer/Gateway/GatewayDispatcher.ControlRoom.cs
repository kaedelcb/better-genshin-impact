namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：控制房间族（control.* 共 7 条，JoinControlRoom/SendRemoteCommand/ReportStatus/
/// ReportCommandResult/ConfirmAllReady/ReportOnlineEvent/ClearOnlineHistory 与旧方法一一对应，
/// ReportCommandResult 为纯新增回执口无旧方法）。
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

        _commands[GatewayProtocol.Names.ControlReportCommandResult] = async (env, ctx) =>
        {
            var result = Get<Models.RemoteCommandResult>(env, "result");
            if (result == null)
            {
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "payload.result 缺失或格式错误" } };
            }
            // delivered 回传给上报方仅作观测；0（发起方离线）不算错误——回执是尽力而为通道
            var delivered = await _ops.ReportCommandResultAsync(ctx, result);
            return new { ack = true, delivered };
        };

        _commands[GatewayProtocol.Names.ControlBatchUpdate] = (env, ctx) => Task.FromResult<object?>(
            _ops.UpdateCoordinatedBatch(ctx, GetInt(env, "generation"), GetString(env, "token") ?? "",
                GetString(env, "batchId") ?? "", GetString(env, "layout") ?? "",
                GetInt(env, "index"), GetInt(env, "attempt"), GetString(env, "result") ?? ""));

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
            var cleared = await _ops.ClearOnlineHistoryAsync(ctx, GetString(env, "targetUid"));
            // 失败不再假 ack：调用方不在任何控制房间（或执行异常）时返回错误，
            // 避免客户端在清除未生效时仍提示"已清除"
            return cleared
                ? new { ack = true }
                : new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "调用方不在控制房间中，清除未执行" } };
        };
    }
}
