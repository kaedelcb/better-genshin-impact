namespace BgiCoordinatorServer.Gateway;

/// <summary>控制房间命令与业务回执；未 opt-in 的旧入房继续使用 JoinRejected/成员列表事件。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterControlRoom()
    {
        _commands[GatewayProtocol.Names.ControlCreateRoom] = (env, ctx) => Task.FromResult<object?>(
            _ops.CreateControlRoom(ctx, GetString(env, "password"), GetString(env, "playerUid"),
                GetStringList(env, "allowedUids") ?? []));

        _commands[GatewayProtocol.Names.ControlJoinRoom] = async (env, ctx) =>
        {
            var result = await _ops.JoinControlRoomAsync(ctx,
                GetString(env, "roomCode", env.RoomCode ?? ""),
                GetString(env, "password"),
                GetString(env, "playerUid"),
                GetString(env, "playerName"),
                GetStringList(env, "allowedUids"),
                GetBool(env, "isRemote"),
                GetString(env, "clientInstanceId"),
                GetString(env, "machineId"),
                GetBool(env, "requireReady"));
            if (env.Payload?.ContainsKey("requireReady") == true) return result;
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlReadyRoom] = async (env, ctx) =>
            await _ops.ReadyControlRoomAsync(ctx, Get<Models.ControlStatus>(env, "status"));
        _commands[GatewayProtocol.Names.ControlLeaveRoom] = async (env, ctx) =>
            await _ops.LeaveControlRoomAsync(ctx, GetString(env, "roomCode", env.RoomCode ?? ""));
        _commands[GatewayProtocol.Names.ControlRoomSnapshot] = _queries[GatewayProtocol.Names.ControlRoomSnapshot]
            = async (env, ctx) => await _ops.GetControlRoomSnapshotAsync(ctx, GetString(env, "roomCode", env.RoomCode ?? ""));
        _commands[GatewayProtocol.Names.ControlRoomAccess] = _queries[GatewayProtocol.Names.ControlRoomAccess]
            = (env, _) => Task.FromResult<object?>(_ops.GetControlRoomAccess(GetString(env, "roomCode", env.RoomCode ?? ""),
                GetString(env, "password"), GetString(env, "playerUid")));

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

        _commands[GatewayProtocol.Names.ControlBatchBegin] = async (env, ctx) =>
        {
            DateTimeOffset? expiry = null;
            if (env.Payload?["expiresAtUtc"] is { } node)
                expiry = node.GetValue<DateTimeOffset>();
            return await _ops.BeginManualCoordinatedBatchAsync(ctx, GetString(env, "requestId"),
                GetStringList(env, "targetUids") ?? [], expiry, GetBool(env, "useCompletionActions"),
                GetString(env, "quickCommandKey"));
        };

        _commands[GatewayProtocol.Names.ControlBatchCancelBegin] = async (env, ctx) =>
        {
            try
            {
                return await _ops.CancelCoordinatedBeginAsync(ctx, GetString(env, "requestId"),
                    GetString(env, "batchId"), GetInt(env, "generation"));
            }
            catch (InvalidOperationException ex)
            {
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = ex.Message } };
            }
        };

        _commands[GatewayProtocol.Names.ControlBatchCurrent] = (env, ctx) => Task.FromResult<object?>(
            _ops.CurrentCoordinatedBatch(ctx, GetString(env, "batchId")));

        _commands[GatewayProtocol.Names.ControlBatchUpdate] = async (env, ctx) =>
            await _ops.UpdateCoordinatedBatchAsync(ctx, GetInt(env, "generation"), GetString(env, "token") ?? "",
                GetString(env, "batchId") ?? "", GetString(env, "layout") ?? "",
                GetInt(env, "index"), GetInt(env, "attempt"), GetString(env, "result") ?? "",
                GetString(env, "stage"), GetString(env, "reason"));

        _commands[GatewayProtocol.Names.ControlBatchReset] = async (env, ctx) =>
        {
            try
            {
                var expectedGeneration = env.Payload != null
                    && env.Payload.TryGetPropertyValue("expectedGeneration", out var generationNode)
                    && generationNode != null
                    ? generationNode.GetValue<int?>()
                    : null;
                return await _ops.ResetCoordinatedBatchAsync(ctx,
                    GetString(env, "resetId") ?? "",
                    GetStringList(env, "targetUids") ?? [],
                    expectedGeneration,
                    GetString(env, "expectedBatchId"));
            }
            catch (InvalidOperationException ex)
            {
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = ex.Message } };
            }
        };

        _commands[GatewayProtocol.Names.ControlBatchResetCleaned] = async (env, ctx) =>
            await _ops.ConfirmCoordinatedResetCleanedAsync(ctx, GetString(env, "resetId"), GetString(env, "batchId"));

        _commands[GatewayProtocol.Names.ControlConfirmAllReady] = async (env, ctx) =>
        {
            await _ops.ConfirmAllReadyAsync(ctx, GetInt(env, "generation"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.ControlReportOnlineEvent] = async (env, ctx) =>
            await _ops.ReportOnlineEventAsync(ctx, GetInt(env, "generation"),
                GetBool(env, "isOnlineReady"), GetString(env, "roomCode", env.RoomCode ?? ""));

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
