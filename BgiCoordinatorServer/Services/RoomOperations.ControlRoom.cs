using System.Collections.Concurrent;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed class ControlPlayersPublication
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ControlRoomPlayersUpdate Baseline = new() { Full = true, Players = [] };
        public long AllocatedRevision;
        public bool NeedsFull;
    }

    private readonly ConcurrentDictionary<string, ControlPlayersPublication> _controlPublications = new(StringComparer.Ordinal);

    private async Task<ControlRoomPlayersUpdate> ReadControlRoomPlayersBaselineAsync(string group)
    {
        var publication = _controlPublications.GetOrAdd(group, _ => new());
        await publication.Gate.WaitAsync();
        try { return publication.Baseline; }
        finally { publication.Gate.Release(); }
    }

    /// <summary>同一房间串行取快照、比对并发送，增量只依赖最后发布的版本。</summary>
    internal async Task<ControlRoomPlayersUpdate> BroadcastControlRoomPlayersAsync(string group, bool forceFull)
    {
        var publication = _controlPublications.GetOrAdd(group, _ => new());
        await publication.Gate.WaitAsync();
        try
        {
            List<ControlRoomPlayer> players;
            lock (_batchGate)
            {
                _roomManager.PruneOfflineControlRoomGhosts(group);
                _roomManager.ExpireOnlineIntents(group, _roomManager.ControlUtcNow);
                _controlBatches.TryGetValue(group, out var current);
                if (current is not null) ReleaseOfflineCompletionWaits(group, current);
                players = _roomManager.RefreshCompletionWaitDisplay(group, current?.Snapshot());
            }
            var revision = ++publication.AllocatedRevision;
            forceFull |= publication.NeedsFull || publication.Baseline.Revision == 0;
            _roomManager.ComputeControlRoomDelta(group, players, out var changed, out var removed);
            if (!forceFull && changed.Count == 0 && removed.Count == 0) return publication.Baseline;

            var full = new ControlRoomPlayersUpdate { Full = true, Revision = revision, Players = players };
            var update = forceFull ? full : new ControlRoomPlayersUpdate
            {
                Full = false, Revision = revision, BaseRevision = publication.Baseline.Revision,
                Changed = changed, Removed = removed,
            };
            publication.Baseline = full;
            try
            {
                await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", update, update);
                publication.NeedsFull = false;
            }
            catch (Exception ex)
            {
                publication.NeedsFull = true;
                _logger.LogWarning(ex, "控制房间 {Group} 成员广播失败，下次补发全量", group);
            }
            return full;
        }
        finally { publication.Gate.Release(); }
    }

    private Task SendCommandInRoomAsync(string group, string connectionId, RemoteCommand command)
        => _broadcaster.SendToConnectionAsync(connectionId, "RemoteCommand", new { command }, [command], roomCode: group[5..]);

    private bool IsControlRoomMemberName(string group, string playerName)
    {
        var name = playerName?.Trim() ?? "";
        return name.Length > 0 && _roomManager.GetControlRoomPlayers(group)
            .Any(p => string.Equals(p.PlayerName?.Trim(), name, StringComparison.Ordinal));
    }

    /// <summary>
    /// 向控制房间成员转发远程命令。目标离线时缓存，上线后自动下发。
    /// </summary>
    public async Task SendRemoteCommandAsync(GatewayHandlerContext ctx, RemoteCommand command)
    {
        // Delayed control is bounded; the execution end checks the same deadline again.
        var now = _roomManager.ControlUtcNow;
        command.ExpiresAtUtc ??= now.AddMinutes(2);
        if (command.ExpiresAtUtc <= now)
            throw new InvalidOperationException("request_expired");
        if (command.ExpiresAtUtc > now.AddMinutes(2))
            command.ExpiresAtUtc = now.AddMinutes(2);
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
                var rejected = new { commandId = command.CommandId, deliveredTo = 0, accepted = false, message = "当前连接未在该房间就绪" };
                await _broadcaster.SendToCallerAsync(ctx, "RemoteCommandAck", rejected, rejected);
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

            if (command.Cmd == "reset_state")
            {
                var authorized = FreezeResetCommand(group, command);
                if (authorized is null)
                {
                    var rejected = new { commandId = command.CommandId, deliveredTo = 0,
                        message = "重置授权已失效或不匹配，未影响当前任务" };
                    await _broadcaster.SendToCallerAsync(ctx, "RemoteCommandAck", rejected, rejected);
                    return;
                }
                command = authorized;
            }

            // 配置/回执仍可回投同房监控端，执行命令只走已就绪执行端。
            var isRemoteConfigReply = IsRequestReplyCommand(command.Cmd);
            var isAllStop = command.Cmd == "stop" && command.Params?.GetValueOrDefault("stopScope")?.ToString() == "all";
            var allStopGeneration = 0;
            CoordinatedBatchState? stoppedBatch = null;
            CoordinatedBatchSnapshot? stoppedSnapshot = null;
            List<string> targets;
            lock (_batchGate)
            {
                if (!IsInControlRoomOrRemote(ctx, group) || command.ExpiresAtUtc <= _roomManager.ControlUtcNow) return;
                targets = _roomManager.ResolveTargets(command);
                if (isRemoteConfigReply)
                    targets = targets.Union(_roomManager.GetRemoteConnectionIdsByUids(group, command.Target)).ToList();
                if (isCenterRequest && targets.Count != 1)
                    throw new InvalidOperationException("center_target_unavailable_or_ambiguous");

                if (isAllStop)
                {
                    // 全停止撤销本房目标的上线意图。中止共享批次时也撤销该批次的其他参与者，
                    // 但不影响别的房间、不把已中止当作已物理清理。
                    IEnumerable<string> disarm = command.Target.Contains("*")
                        ? _roomManager.GetControlRoomPlayers(group).Select(player => player.PlayerUid)
                        : command.Target;
                    if (_controlBatches.TryGetValue(group, out var current))
                    {
                        allStopGeneration = current.Generation;
                        if (!current.IsTerminal && (command.Target.Contains("*") || current.ParticipantUids.Intersect(command.Target).Any()))
                        {
                            current.Abort("user_stop_all");
                            stoppedBatch = current;
                            stoppedSnapshot = current.Snapshot();
                            disarm = disarm.Concat(current.ParticipantUids);
                            _roomManager.MarkExhausted(group);
                        }
                    }
                    _roomManager.DisarmOnlineEvents(group, disarm.Distinct(StringComparer.Ordinal).ToArray());
                }

                // 决定在线投递和缓存时与就绪/离房共锁，避免两条路径都漏掉刚就绪的成员。
                if (!isRemoteConfigReply && !isCenterRequest && command.Cmd is not ("reset_state" or "stop")
                    && !command.Target.Contains("*"))
                {
                    foreach (var uid in command.Target.Distinct(StringComparer.Ordinal))
                        if (!_roomManager.IsPlayerOnline(group, uid)) _roomManager.CachePendingCommand(group, uid, command);
                }
            }
            if (stoppedBatch is not null)
                await PublishBatchSnapshotAsync(group, stoppedBatch, stoppedSnapshot!, GatewayProtocol.Events.ControlBatchState);
            if (isAllStop) await BroadcastControlRoomPlayersAsync(group, forceFull: false);

            var deliveredTo = 0;
            var changedStopTargets = 0;
            foreach (var connectionId in targets)
            {
                if (!IsInControlRoomOrRemote(ctx, group) || command.ExpiresAtUtc <= _roomManager.ControlUtcNow) break;
                if (!_roomManager.IsInControlRoom(group, connectionId)
                    && !(isRemoteConfigReply && _roomManager.IsRemoteConnection(group, connectionId))) continue;
                if (command.Cmd == "reset_state" && !MatchesResetCommandExecutor(group, connectionId, command)) continue;
                var outgoing = command;
                if (isAllStop)
                {
                    var parameters = new Dictionary<string, object>(command.Params!);
                    parameters["hoeingStopGeneration"] = allStopGeneration;
                    outgoing = new RemoteCommand
                    {
                        RoomCode = command.RoomCode, Cmd = command.Cmd, Sender = command.Sender,
                        SenderUid = command.SenderUid, Target = command.Target.ToList(), TargetInstanceId = command.TargetInstanceId,
                        CommandId = command.CommandId, Timestamp = command.Timestamp, ExpiresAtUtc = command.ExpiresAtUtc,
                        Params = parameters
                    };
                }
                if (command.Cmd == "stop" && !isAllStop)
                {
                    outgoing = FreezeStopTarget(group, connectionId, command);
                    if (outgoing is null) { changedStopTargets++; continue; }
                }
                await SendCommandInRoomAsync(group, connectionId, outgoing);
                deliveredTo++;
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

    private RemoteCommand? FreezeResetCommand(string group, RemoteCommand source)
    {
        var resetId = source.Params?.GetValueOrDefault("resetId")?.ToString() ?? source.CommandId;
        var hasGeneration = source.Params?.ContainsKey("generation") == true;
        var generation = 0;
        if (hasGeneration && !int.TryParse(source.Params!["generation"]?.ToString(), out generation)) return null;
        var hasBatchId = source.Params?.ContainsKey("batchId") == true;
        var batchId = source.Params?.GetValueOrDefault("batchId")?.ToString() ?? "";
        lock (_batchGate)
        {
            _controlBatches.TryGetValue(group, out var current);
            if (!_processedResetResults.TryGetValue(group + "\0" + resetId, out var reset))
            {
                // Old independent reset commands carry no authority to cross a batch fence.
                // Keep their no-batch path, but never trust a client-supplied generation.
                return !hasGeneration && batchId.Length == 0
                    && (current is null || !source.Target.Contains("*")
                        && !current.ParticipantUids.Intersect(source.Target, StringComparer.Ordinal).Any())
                    ? source : null;
            }
            if (reset.Reason == "stale_reset" || hasGeneration && generation != reset.Generation
                || hasBatchId && batchId != reset.BatchId) return null;
            var targetUids = source.Target.Contains("*") ? reset.TargetUids : source.Target;
            if (targetUids.Any(uid => !reset.TargetUids.Contains(uid, StringComparer.Ordinal))) return null;
            if (reset.Found)
            {
                if (current is null || current.BatchId != reset.BatchId || current.Generation != reset.Generation
                    || targetUids.Any(uid => current.ParticipantUids.Contains(uid, StringComparer.Ordinal)
                        && !current.MatchesResetCleanup(uid, resetId))) return null;
            }
            else if (current is not null && current.ParticipantUids.Intersect(targetUids, StringComparer.Ordinal).Any())
                return null; // An independent reset issued before this batch cannot stop its successor.
            var parameters = source.Params is null ? new Dictionary<string, object>() : new(source.Params);
            parameters["resetId"] = reset.ResetId;
            parameters["generation"] = reset.Generation;
            parameters["batchId"] = reset.BatchId;
            return new RemoteCommand
            {
                RoomCode = source.RoomCode, Cmd = source.Cmd, Sender = source.Sender,
                SenderUid = source.SenderUid, Target = targetUids.ToList(), TargetInstanceId = source.TargetInstanceId,
                CommandId = source.CommandId, Timestamp = source.Timestamp, ExpiresAtUtc = source.ExpiresAtUtc,
                Params = parameters
            };
        }
    }

    private bool MatchesResetCommandExecutor(string group, string connectionId, RemoteCommand command)
    {
        var resetId = command.Params?.GetValueOrDefault("resetId")?.ToString() ?? command.CommandId;
        lock (_batchGate)
        {
            if (!_processedResetResults.ContainsKey(group + "\0" + resetId))
                return command.Params?.ContainsKey("generation") != true; // Only the unfenced legacy path.
            var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.ConnectionId == connectionId);
            return player is not null && _resetCleanupExecutors.TryGetValue(group + "\0" + resetId, out var executors)
                && executors.TryGetValue(player.PlayerUid, out var instance) && instance == player.ClientInstanceId;
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
            lock (_batchGate)
            {
                // 普通状态不能代替 readyRoom，也不能新建或复活已结清的上线意图；仅对仍存活的意图顺延保活。
                if (!_roomManager.UpdateControlStatus(group, ctx.ConnectionId, status)) return;
            }
            ObserveHoeingMember(group, ctx.ConnectionId, status);

            // 广播给控制房间所有成员（状态上报：增量比对，无变化零发送——主优化点）
            await BroadcastControlRoomPlayersAsync(group, forceFull: false);
            await ReplayPendingBatchStartAsync(group, ctx.ConnectionId);
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

    /// <summary>只有当前就绪连接的成员可登记/撤回上线边沿；ack 不是受理凭据。</summary>
    public async Task<ControlRoomResult> ReportOnlineEventAsync(GatewayHandlerContext ctx, int generation,
        bool isOnlineReady, string roomCode = "")
    {
        string group;
        bool changed;
        lock (_batchGate)
        {
            var joined = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
            if (joined is null || (roomCode.Length > 0 && joined != $"CTRL_{roomCode}" && joined != roomCode))
                return new(false, "当前连接未在该房间就绪");
            group = joined;
            var result = _roomManager.AcceptOnlineEvent(group, ctx.ConnectionId, generation, isOnlineReady, _roomManager.ControlUtcNow);
            if (!result.Accepted) return new(false, "上线代序号无效或不属于本连接的当前意图");
            changed = result.Changed;
        }
        if (changed)
        {
            // 通知的故障不改变在此连接上已落定的业务受理结果。
            try
            {
                if (isOnlineReady) await TryStartRoundAsync(group);
                await BroadcastControlRoomPlayersAsync(group, forceFull: false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "控制房间 {Group} 上线结果通知失败", group); }
        }
        return new(true);
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
            var now = _roomManager.ControlUtcNow;
            _roomManager.ExpireOnlineIntents(group, now);
            if (!_roomManager.CheckAndTransition(group, now, out intentGeneration)) return;
            var players = _roomManager.GetCoordinatedStartPlayers(group)
                .Where(p => RoomManager.HasLiveOnlineIntent(p, now)).ToArray();
            participants = players.Select(p => p.PlayerUid).Distinct(StringComparer.Ordinal).ToList();
            var intents = players.GroupBy(p => p.PlayerUid).ToDictionary(g => g.Key, g => g.First().OnlineEventGeneration,
                StringComparer.Ordinal);
            // 相同 UID 的新连接可以重新使用本地计数器；不能命中旧连接的批次收据。
            var identity = string.Join("\n", players.OrderBy(p => p.PlayerUid, StringComparer.Ordinal)
                .ThenBy(p => p.ConnectionId, StringComparer.Ordinal)
                .Select(p => $"{p.PlayerUid}\0{p.ConnectionId}\0{p.OnlineEventGeneration}"));
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
    // 扫描只补过期通知；真正的准入和离房不依赖扫描时序。
    internal async Task ScanControlRoomTimeoutsAsync()
    {
        foreach (var group in _roomManager.GetAllControlRoomGroups())
        {
            bool expired;
            lock (_batchGate)
            {
                var now = _roomManager.ControlUtcNow;
                expired = _roomManager.ExpireOnlineIntents(group, now);
                _roomManager.ExpireControlRoomTaskStatus(group, now);
            }
            if (expired) await BroadcastControlRoomPlayersAsync(group, forceFull: false);
        }
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
