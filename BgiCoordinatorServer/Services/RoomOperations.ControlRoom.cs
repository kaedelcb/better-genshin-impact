using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 控制房间族（自 CoordinatorHub 逐字搬迁：JoinControlRoom/SendRemoteCommand/
/// ReportControlStatus/ConfirmAllReady/ReportOnlineEvent/ClearOnlineHistory，
/// 及统一联机开始/IsInControlRoomOrRemote 辅助）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// 控制房间 Group 名为 "CTRL_{roomCode}"，与锄地房间不同命名空间——
/// RoomPhase 观测（只针对锄地房间）本族一律不加。
/// 注：IsInControlRoomOrRemote 本族方法暂未使用（日志族 RoomOperations.MemberLog.cs 使用本 ctx 版），
/// 旧 Hub 中的同名副本已随日志族迁移删除。
/// 带宽优化（2026-09）：ControlRoomPlayersUpdated 广播统一收口到 BroadcastControlRoomPlayersAsync
/// （全量/增量两态 payload，无变化零发送），各调用点不再直接 BroadcastGroupAsync。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>
    /// 控制房间成员列表广播的统一出口（带宽优化，替代各调用点直接全量广播）。
    /// forceFull=true 或本组尚无快照（首次广播）→ 发 Full=true 全量并刷新快照为基线；
    /// forceFull=false → 快照哈希比对发 Full=false 增量；changed/removed 都空则<b>不发送</b>
    /// （成员周期性状态上报不再产生 N² 放大的全量广播，主优化点）。
    /// internal：HeartbeatMonitor 兜底扫描（同程序集）也走本出口。
    /// </summary>
    internal async Task BroadcastControlRoomPlayersAsync(string group, bool forceFull)
    {
        // 广播前先剪同 UID 离线幽灵条目：存量幽灵（修复前残留）随任意一次广播自愈，
        // 避免离线条目在按 UID 键控的全量/增量广播中覆盖在线状态导致客户端"假离线"
        _roomManager.PruneOfflineControlRoomGhosts(group);
        List<ControlRoomPlayer> players;
        lock (_batchGate)
        {
            _controlBatches.TryGetValue(group, out var current);
            if (current is not null) ReleaseOfflineCompletionWaits(group, current);
            players = _roomManager.RefreshCompletionWaitDisplay(group, current?.Snapshot());
        }
        var revision = _roomManager.NextControlRoomRevision(group);
        if (forceFull || !_roomManager.HasControlRoomSnapshot(group))
        {
            var fullUpdate = new ControlRoomPlayersUpdate { Full = true, Revision = revision, Players = players };
            // evt payload 与 legacy 参数同为单个 update 对象（新旧客户端同步升级，payload 形状允许变更）
            await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", fullUpdate, fullUpdate);
            // 全量下发后刷新快照为基线，后续增量以本次为准（out 丢弃，仅借 ComputeControlRoomDelta 刷快照）
            _roomManager.ComputeControlRoomDelta(group, players, out _, out _);
            return;
        }

        _roomManager.ComputeControlRoomDelta(group, players, out var changed, out var removed);
        // 无变化零发送：revision 已消耗（允许跳号），快照已刷新
        if (changed.Count == 0 && removed.Count == 0) return;
        var deltaUpdate = new ControlRoomPlayersUpdate { Full = false, Revision = revision, Changed = changed, Removed = removed };
        await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", deltaUpdate, deltaUpdate);
    }

    /// <summary>
    /// 加入控制房间。校验密码 + UID 白名单，成功后加入 CTRL_{roomCode} Group
    /// </summary>
    public async Task JoinControlRoomAsync(GatewayHandlerContext ctx, string roomCode, string password, string playerUid, string playerName,
        List<string>? allowedUids = null, bool isRemote = false, string clientInstanceId = "", string machineId = "")
    {
        try
        {
            var uidWhitelist = allowedUids ?? [];
            if (!ControlRoomAuth.Authenticate(roomCode, password, playerUid, uidWhitelist))
            {
                await _broadcaster.SendToCallerAsync(ctx, "JoinRejected", new { reason = "密码错误或UID不在白名单中" }, "密码错误或UID不在白名单中");
                return;
            }

            var group = $"CTRL_{roomCode}";
            await _broadcaster.AddToGroupAsync(ctx, group);
            TrackGroup(ctx, group);
            var isWebClient = playerUid.StartsWith("web_");
            // 遥控端（observerMode 标记，isRemote=true）与 WEB 端类似：不加入 _controlRooms 成员列表，
            // 只加 SignalR Group 收广播。这样遥控端不建独立条目、不被 ResolveTargets 匹配（不接收命令）、
            // 且同 UID 执行端的 ControlRoomPlayer 不被覆盖（解决"同 UID 双端互挤占"）。
            if (!isWebClient && !isRemote)
            {
                _roomManager.AddToControlRoom(group, ctx.ConnectionId, playerUid, playerName, clientInstanceId, machineId);
                ObserveRoomLog(context => context.RegisterControlRoster(roomCode, playerUid, uidWhitelist));
            }
            if (isRemote || isWebClient)
            {
                _roomManager.RegisterRemoteConnection(group, ctx.ConnectionId, playerUid);
            }
            _logger.LogInformation("玩家 {PlayerName}({PlayerUid}) 加入控制房间 {RoomCode} (Web={IsWeb}, Remote={IsRemote})", playerName, playerUid, roomCode, isWebClient, isRemote);

            // 广播成员列表（成员加入：全量）
            await BroadcastControlRoomPlayersAsync(group, forceFull: true);

            // 遥控端不接收命令（FR-3），故不下发离线缓存命令。WEB 端同样不入 _controlRooms，
            // 但既有行为会下发缓存——保持 WEB 端不变，仅对遥控端跳过。
            if (!isRemote && !isWebClient)
            {
                var pending = _roomManager.GetAndClearPendingCommands(playerUid);
                foreach (var cmd in pending)
                {
                    await _broadcaster.SendToConnectionAsync(ctx.ConnectionId, "RemoteCommand", new { command = cmd }, cmd);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinControlRoom 失败");
            try
            {
                // 把异常信息传给客户端，让用户能定位具体原因
                await _broadcaster.SendToCallerAsync(ctx, "JoinRejected", new { reason = $"加入控制房间失败: {ex.Message}" }, $"加入控制房间失败: {ex.Message}");
            }
            catch
            {
                // 客户端可能已断开，忽略二次异常，避免 "Failed to invoke" 模糊错误
            }
        }
    }

    /// <summary>
    /// 向控制房间成员转发远程命令。目标离线时缓存，上线后自动下发。
    /// </summary>
    public async Task SendRemoteCommandAsync(GatewayHandlerContext ctx, RemoteCommand command)
    {
        // Delayed control is bounded; the execution end checks the same deadline again.
        command.ExpiresAtUtc ??= DateTimeOffset.UtcNow.AddMinutes(2);
        if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("request_expired");
        if (command.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            command.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        try
        {
            var group = $"CTRL_{command.RoomCode}";
            // WEB 控制端（UID 以 web_ 开头）不会被 AddToControlRoom 加入 _controlRooms，
            // 但它们已通过 JoinControlRoom 的密码校验并加入了 CTRL_ group，应放行发送。
            // PC 端助手（UID 为真实数字）走 _controlRooms 校验，行为不变。
            // 遥控端（isRemote，不在 _controlRooms）用 RegisterRemoteConnection 登记，也放行发送。
            var isRemoteSender = _roomManager.IsRemoteConnection(group, ctx.ConnectionId);
            if (!isRemoteSender && !_roomManager.IsInControlRoom(group, ctx.ConnectionId))
            {
                _logger.LogWarning("玩家 {Sender} 不在控制房间中，拒绝发送命令", command.Sender);
                return;
            }

            // 旧控制页面仍可能发送快捷键包；不得再把它投递到各端独立foreach执行。
            if (command.Cmd == "start_group" && command.Params?.GetValueOrDefault("key")?.ToString() == "一键锄地")
            {
                string message;
                var accepted = false;
                try
                {
                    var batch = await BeginManualCoordinatedBatchAsync(ctx, command.CommandId, command.Target, command.ExpiresAtUtc);
                    accepted = true;
                    message = "联机批次已受理：" + batch.BatchId;
                }
                catch (InvalidOperationException ex) { message = "联机批次未受理：" + ex.Message; }
                var batchAck = new { commandId = command.CommandId, deliveredTo = 0, accepted, message };
                await _broadcaster.SendToCallerAsync(ctx, "RemoteCommandAck", batchAck, batchAck);
                return;
            }

            var isCenterRequest = command.Cmd == "center.request";
            var isCenterReply = command.Cmd == "center.data";
            if (isCenterRequest || isCenterReply)
            {
                var boundUid = isRemoteSender
                    ? _roomManager.GetRemoteUidByConnectionId(group, ctx.ConnectionId)
                    : _roomManager.GetUidByConnectionId(group, ctx.ConnectionId);
                if (string.IsNullOrEmpty(boundUid) || boundUid != command.SenderUid)
                    throw new InvalidOperationException("center_sender_identity_mismatch");
                if (command.Target.Count != 1 || command.Target[0] == "*")
                    throw new InvalidOperationException("center_requires_one_target");
                if (isCenterRequest && string.IsNullOrWhiteSpace(command.TargetInstanceId))
                    throw new InvalidOperationException("center_requires_target_instance");
            }

            // 解析目标
            var targets = _roomManager.ResolveTargets(command);

            // [监控模式修复] 请求/应答族的回复命令额外投递给目标 UID 的遥控端连接：
            // 监控端不入 _controlRooms、ResolveTargets 匹配不到，回复会被误判"目标离线"进缓存，
            // 导致监控端发起远程编辑永远等不到回包（切回执行模式冲刷缓存才恢复）。
            // 回复命令按 CommandId 关联、只有发起方的等待会话会消费，同 UID 执行端收到只会忽略，双投安全。
            // 执行类命令维持 FR-3 不变：遥控端一律不接收。
            // 历史 bug：本例举只列了 remote_config.* 两条，遗漏结构完全同一的 task_policy.data /
            // task_policy.push_result，导致监控模式下 task_policy.pull 的回复永远投不到发起方，
            // 必然 8s 超时（弹窗显示默认值，保存即覆盖对方配置），而绑定列表正常（走状态广播推送）。
            // 故改为按"请求/应答族"统一判定，新增此类命令无需再改这里。
            var isRemoteConfigReply = IsRequestReplyCommand(command.Cmd);
            if (isRemoteConfigReply)
            {
                var remoteTargets = _roomManager.GetRemoteConnectionIdsByUids(group, command.Target);
                if (remoteTargets.Count > 0)
                {
                    targets = targets.Union(remoteTargets).ToList();
                }
            }
            if (isCenterRequest && targets.Count != 1)
                throw new InvalidOperationException("center_target_unavailable_or_ambiguous");

            var allStopGeneration = 0;
            // 用户全停止也撤销服务端的准备/重试来源；不把广播或批次终态当作成员已退出。
            if (command.Cmd == "stop" && command.Params?.GetValueOrDefault("stopScope")?.ToString() == "all")
            {
                CoordinatedBatchState? stoppedBatch = null;
                CoordinatedBatchSnapshot? stoppedSnapshot = null;
                lock (_batchGate)
                {
                    if (_controlBatches.TryGetValue(group, out var observed)) allStopGeneration = observed.Generation;
                    if (_controlBatches.TryGetValue(group, out var current) && !current.IsTerminal
                        && (command.Target.Contains("*") || current.ParticipantUids.Intersect(command.Target).Any()))
                    {
                        current.Abort("user_stop_all");
                        stoppedBatch = current;
                        stoppedSnapshot = current.Snapshot();
                    }
                }
                if (stoppedBatch is not null)
                    await PublishBatchSnapshotAsync(group, stoppedBatch, stoppedSnapshot!, GatewayProtocol.Events.ControlBatchState);
            }

            var deliveredTo = 0;
            var changedStopTargets = 0;
            foreach (var connectionId in targets)
            {
                var outgoing = command;
                if (command.Cmd == "stop" && command.Params?.GetValueOrDefault("stopScope")?.ToString() == "all")
                {
                    var parameters = new Dictionary<string, object>(command.Params);
                    parameters["hoeingStopGeneration"] = allStopGeneration;
                    outgoing = new RemoteCommand
                    {
                        RoomCode = command.RoomCode, Cmd = command.Cmd, Sender = command.Sender,
                        SenderUid = command.SenderUid, Target = command.Target.ToList(), TargetInstanceId = command.TargetInstanceId,
                        CommandId = command.CommandId, Timestamp = command.Timestamp, ExpiresAtUtc = command.ExpiresAtUtc,
                        Params = parameters
                    };
                }
                if (command.Cmd == "stop" && command.Params?.GetValueOrDefault("stopScope")?.ToString() != "all")
                {
                    outgoing = FreezeStopTarget(group, connectionId, command);
                    if (outgoing is null) { changedStopTargets++; continue; }
                }
                await _broadcaster.SendToConnectionAsync(connectionId, "RemoteCommand", new { command = outgoing }, outgoing);
                deliveredTo++;
            }

            // 缓存离线目标：仅当明确指定的目标不在线时缓存（"*" 全员时不缓存单品）。
            // 回复类命令只对实时等待中的会话有意义，发起方 20s 超时后缓存冲刷出来的只是迟到噪音，不缓存。
            if (!isRemoteConfigReply
                && !isCenterRequest
                && !string.Equals(command.Cmd, "reset_state", StringComparison.Ordinal)
                && !string.Equals(command.Cmd, "stop", StringComparison.Ordinal)
                && command.Target.Count > 0 && !(command.Target.Count == 1 && command.Target[0] == "*"))
            {
                var players = _roomManager.GetControlRoomPlayers(group);
                foreach (var targetUid in command.Target)
                {
                    if (!_roomManager.IsPlayerOnline(group, targetUid))
                    {
                        _roomManager.CachePendingCommand(targetUid, command);
                        _logger.LogInformation("命令 {Cmd} 目标 {Uid} 离线，已缓存", command.Cmd, targetUid);
                    }
                }
            }

            _logger.LogInformation("命令 {Cmd} 已从 {Sender} 转发到 {Count} 个目标", command.Cmd, command.Sender, deliveredTo);
            var ack = new
            {
                commandId = command.CommandId,
                deliveredTo,
                message = changedStopTargets > 0 ? $"{changedStopTargets} 个停止目标已变化，未影响其当前任务"
                    : deliveredTo == 0 ? "没有在线目标" : ""
            };
            await _broadcaster.SendToCallerAsync(ctx, "RemoteCommandAck", ack, ack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendRemoteCommand 失败");
            if (command.Cmd is "center.request" or "center.data") throw;
        }
    }

    private RemoteCommand? FreezeStopTarget(string group, string connectionId, RemoteCommand source)
    {
        var player = _roomManager.GetControlRoomPlayers(group).FirstOrDefault(p => p.ConnectionId == connectionId);
        if (player is null) return null;
        string batchId;
        lock (_batchGate)
        {
            batchId = _controlBatches.TryGetValue(group, out var batch)
                && batch.MatchesExecutor(player.PlayerUid, player.ClientInstanceId)
                && batch.Snapshot().Members.Any(m => m.Uid == player.PlayerUid && m.Stage != "cleaned")
                    ? batch.BatchId : "";
            var hasExpected = TryReadStopExpectation(source, player.PlayerUid, out var expected);
            if (!hasExpected && batchId.Length != 0) return null;
            if (hasExpected && !string.Equals(batchId, expected, StringComparison.Ordinal)) return null;
        }
        var parameters = source.Params is null ? new Dictionary<string, object>() : new(source.Params);
        parameters.Remove("hoeingBatchIds");
        parameters["hoeingBatchId"] = batchId; // Empty is an explicit no-batch observation, never a wildcard.
        return new RemoteCommand
        {
            RoomCode = source.RoomCode, Cmd = source.Cmd, Sender = source.Sender,
            SenderUid = source.SenderUid, Target = [player.PlayerUid], TargetInstanceId = source.TargetInstanceId,
            CommandId = source.CommandId, Timestamp = source.Timestamp, ExpiresAtUtc = source.ExpiresAtUtc,
            Params = parameters
        };
    }

    private static bool TryReadStopExpectation(RemoteCommand command, string uid, out string expected)
    {
        expected = "";
        if (command.Params?.TryGetValue("hoeingBatchIds", out var map) == true && map is not null)
        {
            var element = map is System.Text.Json.JsonElement json ? json : System.Text.Json.JsonSerializer.SerializeToElement(map);
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(uid, out var item))
            {
                expected = item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() ?? "" : "invalid_stop_target";
                return true;
            }
        }
        if (command.Params?.TryGetValue("hoeingBatchId", out var value) != true) return false;
        expected = value?.ToString() ?? "";
        return true;
    }

    /// <summary>
    /// 远端任务下发结果回执转发（纯加法，2026-11）：目标机执行完 start_group / start_oneclick 后上报，
    /// 服务端按 result.SenderUid 反查发起方连接（执行端成员连接 + 遥控端登记连接，与
    /// SendRemoteCommand 的请求/应答回投同一双路由）定向回投。
    /// evt-only 新协议域（对齐 route-anchor 先例）：旧发起方（/hub 连接）不在 /gateway 上，
    /// 发送天然落空，发起方 UI 按 15s 超时降级为"已发送（无回执）"。
    /// 返回实际投递的连接数（0 = 发起方不在线/已离场，仅记日志不报错——回执是尽力而为通道）。
    /// </summary>
    public async Task<int> ReportCommandResultAsync(GatewayHandlerContext ctx, RemoteCommandResult result)
    {
        try
        {
            var group = $"CTRL_{result.RoomCode}";
            // 与 SendRemoteCommand 同款校验：执行端成员在 _controlRooms；同 UID 监控端登记连接也放行
            if (!_roomManager.IsInControlRoom(group, ctx.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, ctx.ConnectionId))
            {
                _logger.LogWarning("连接 {ConnectionId} 不在控制房间中，拒绝转发命令回执（Cmd={Cmd}）",
                    ctx.ConnectionId, result.Cmd);
                return 0;
            }

            var targets = new List<string>();
            // 发起方是执行端成员（在 _controlRooms）
            var senderConn = _roomManager.GetConnectionIdByUid(group, result.SenderUid);
            if (!string.IsNullOrEmpty(senderConn))
            {
                targets.Add(senderConn);
            }
            // 发起方是遥控/监控端（不入 _controlRooms，登记在 _remoteControlConnections）
            foreach (var conn in _roomManager.GetRemoteConnectionIdsByUids(group, [result.SenderUid]))
            {
                if (!targets.Contains(conn)) targets.Add(conn);
            }

            if (targets.Count == 0)
            {
                _logger.LogInformation("命令回执找不到发起方 {SenderUid}（已离线/离场），丢弃（Cmd={Cmd} CommandId={CommandId}）",
                    result.SenderUid, result.Cmd, result.CommandId);
                return 0;
            }

            foreach (var connectionId in targets)
            {
                await _broadcaster.SendEventOnlyToConnectionAsync(
                    connectionId, GatewayProtocol.Events.ControlRemoteCommandResult, result, result.RoomCode);
            }
            _logger.LogInformation("命令回执 {Cmd}({Status}) 已从 {TargetUid} 回投到 {Count} 个发起方连接",
                result.Cmd, result.Status, result.TargetUid, targets.Count);
            return targets.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportCommandResult 失败");
            return 0;
        }
    }

    /// <summary>
    /// 成员上报自身 BGI 状态与可用配置列表，服务端更新后广播最新成员列表。
    /// </summary>
    public async Task ReportControlStatusAsync(GatewayHandlerContext ctx, ControlStatus status)
    {
        try
        {
            var group = $"CTRL_{status.RoomCode}";
            // 只更新状态，不做就绪检查（就绪检查由 ReportOnlineEvent 端点统一处理）
            _roomManager.UpdateControlStatus(group, ctx.ConnectionId, status);

            // 广播给控制房间所有成员（状态上报：增量比对，无变化零发送——主优化点）
            await BroadcastControlRoomPlayersAsync(group, forceFull: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportControlStatus 失败");
        }
    }

    /// <summary>
    /// 清除指定成员的 OnlineHistory（已联机记录）。由本人或房主调用。
    /// 返回是否已执行清除；false 表示找不到调用方所在组（不再静默失败）。
    /// </summary>
    public async Task<bool> ClearOnlineHistoryAsync(GatewayHandlerContext ctx, string targetUid)
    {
        try
        {
            // 监控端（isRemote）不入 _controlRooms，GetControlRoomGroup 反查必落空，
            // 故用带回退的反查（先例：a95a68e6 给 SendRemoteCommand 补 IsRemoteConnection 放行）。
            // WEB 端（web_ 前缀）不登记 _remoteControlConnections，本回退对其无效。
            // 注意：本回退仅限本端点——ReportOnlineEvent/ConfirmAllReady 仍只认正式成员，勿复用。
            var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId);
            if (string.IsNullOrEmpty(group))
            {
                _logger.LogWarning("ClearOnlineHistory 失败: 找不到调用方连接 {ConnectionId} 所在控制房间组, targetUid={TargetUid}",
                    ctx.ConnectionId, targetUid);
                return false;
            }

            // 注意：_controlRooms 的键是组名（"CTRL_xxx"），必须原样传入，不能剥前缀
            // （历史 bug：剥成裸 roomCode 导致 TryGetValue 永远落空，清除静默无效）
            _roomManager.ClearOnlineHistory(group, targetUid);

            // 广播更新给所有成员（OnlineHistory 变更：全量）
            await BroadcastControlRoomPlayersAsync(group, forceFull: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearOnlineHistory 失败");
            return false;
        }
    }

    /// <summary>上报上线事件（带 generation 代序号）。由 ReportOnlineEvent 端点统一处理就绪检查。</summary>
    public async Task ReportOnlineEventAsync(GatewayHandlerContext ctx, int generation, bool isOnlineReady)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            _roomManager.ReportOnlineEvent(group, ctx.ConnectionId, generation);

            // 广播玩家列表更新（上线事件：全量）
            await BroadcastControlRoomPlayersAsync(group, forceFull: true);

            // 检查是否可转换为 ready（凑齐则开一轮：单人直接 AllReady，多人进确认阶段）
            await TryStartRoundAsync(group);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportOnlineEvent 失败");
        }
    }

    /// <summary>就绪检查并在凑齐时开一轮（广播 AllReady 或进入确认阶段）。
    /// 上线意图只选择冻结成员；业务执行权由v2准备与接管阶段授予。</summary>
    private async Task TryStartRoundAsync(string group)
    {
        CoordinatedBatchState? batch = null;
        string? rejection = null;
        List<string> participants;
        int intentGeneration;
        bool created = false;
        lock (_batchGate)
        {
            if (!_roomManager.CheckAndTransition(group, out intentGeneration)) return;
            var players = _roomManager.GetCoordinatedStartPlayers(group)
                .Where(p => p.Online && !p.OnlineEventConsumed && p.OnlineEventGeneration > 0).ToArray();
            participants = players.Select(p => p.PlayerUid).Distinct(StringComparer.Ordinal).ToList();
            var intents = players.GroupBy(p => p.PlayerUid).ToDictionary(g => g.Key, g => g.First().OnlineEventGeneration,
                StringComparer.Ordinal);
            var identity = string.Join("|", intents.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value));
            var requestId = "online_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            try
            {
                batch = BeginCoordinatedBatch(group, "online", requestId, requestId, participants,
                    intents, null, out created);
                // 旧本地counter只用于消费上线意图；执行与重置使用服务端BatchId/generation。
                _roomManager.ConsumeOnlineReady(group, intentGeneration, participants);
            }
            catch (InvalidOperationException ex)
            {
                rejection = ex.Message;
                _roomManager.MarkExhausted(group);
                _roomManager.DisarmOnlineEvents(group, participants);
            }
        }
        if (rejection is not null)
        {
            _logger.LogWarning("联机开始被拒绝：group={Group}, intentGeneration={Generation}, reason={Reason}",
                group, intentGeneration, rejection);
            // 此通知只拒绝本次上线意图，绝不能中止另一BatchId的现有执行。
            await _broadcaster.BroadcastGroupAsync(group, "AllReadyAbort",
                new { generation = intentGeneration, targetUids = participants, reason = rejection }, intentGeneration);
        }
        else if (created && batch is not null)
            await PublishBatchStartAsync(group, batch);
        await BroadcastControlRoomPlayersAsync(group, forceFull: true);
    }
    /// <summary>客户端确认收到 AllReadyConfirm。由客户端收到 AllReadyConfirm 事件后调用。</summary>
    public async Task ConfirmAllReadyAsync(GatewayHandlerContext ctx, int generation)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
            if (string.IsNullOrEmpty(group)) return;
            var uid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId);
            if (string.IsNullOrEmpty(uid)) return;
            _roomManager.RegisterConfirmAck(group, uid, generation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfirmAllReady 失败");
        }
    }

    /// <summary>校验请求连接是否在对应控制房（PC 端在 _controlRooms；遥控端登记连接也放行）。</summary>
    private bool IsInControlRoomOrRemote(GatewayHandlerContext ctx, string group)
        => _roomManager.IsInControlRoom(group, ctx.ConnectionId)
           || _roomManager.IsRemoteConnection(group, ctx.ConnectionId);

    /// <summary>
    /// 是否为"请求/应答族"的回复命令（配置 data/push_result，以及执行确认 ack）。
    /// 这类命令由发起方的等待会话按 CommandId 消费，因此必须投递给发起方连接本身；
    /// 而遥控/监控端不在 _controlRooms，ResolveTargets 匹配不到，必须额外按 UID 回投，
    /// 且不能进离线缓存（只对实时等待中的会话有意义，缓存冲刷出来的是迟到噪音）。
    /// ack 包含状态重置的完成确认，必须回投监控发起端，否则已完成的复位仍会等待超时。
    /// 其余按命令名以 ".data" / ".push_result" 结尾判定（覆盖 remote_config.* 与 task_policy.*，
    /// 以及后续新增的同类命令，避免再次出现"漏登记一条导致监控模式永久超时"）。
    /// 执行类命令不匹配，维持 FR-3（遥控端不接收）。
    /// </summary>
    private static bool IsRequestReplyCommand(string cmd)
        => string.Equals(cmd, "ack", StringComparison.Ordinal)
           || cmd.EndsWith(".data", StringComparison.Ordinal)
           || cmd.EndsWith(".push_result", StringComparison.Ordinal);
}
