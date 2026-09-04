using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 控制房间族（自 CoordinatorHub 逐字搬迁：JoinControlRoom/SendRemoteCommand/
/// ReportControlStatus/ConfirmAllReady/ReportOnlineEvent/ClearOnlineHistory，
/// 及 StartConfirmAsync/IsInControlRoomOrRemote 私有辅助）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// 控制房间 Group 名为 "CTRL_{roomCode}"，与锄地房间不同命名空间——
/// RoomPhase 观测（只针对锄地房间）本族一律不加。
/// 注：IsInControlRoomOrRemote 本族方法暂未使用（为后续日志族迁移备好），
/// 旧 Hub 中的同名副本仍被尚未迁移的日志族方法引用，暂时保留。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>
    /// 加入控制房间。校验密码 + UID 白名单，成功后加入 CTRL_{roomCode} Group
    /// </summary>
    public async Task JoinControlRoomAsync(GatewayHandlerContext ctx, string roomCode, string password, string playerUid, string playerName, List<string>? allowedUids = null, bool isRemote = false, string clientInstanceId = "")
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
                _roomManager.AddToControlRoom(group, ctx.ConnectionId, playerUid, playerName, clientInstanceId);
            }
            if (isRemote)
            {
                _roomManager.RegisterRemoteConnection(group, ctx.ConnectionId);
            }
            _logger.LogInformation("玩家 {PlayerName}({PlayerUid}) 加入控制房间 {RoomCode} (Web={IsWeb}, Remote={IsRemote})", playerName, playerUid, roomCode, isWebClient, isRemote);

            // 广播成员列表
            var players = _roomManager.GetControlRoomPlayers(group);
            await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players }, players);

            // 遥控端不接收命令（FR-3），故不下发离线缓存命令。WEB 端同样不入 _controlRooms，
            // 但既有行为会下发缓存——保持 WEB 端不变，仅对遥控端跳过。
            if (!isRemote)
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
        try
        {
            var group = $"CTRL_{command.RoomCode}";
            // WEB 控制端（UID 以 web_ 开头）不会被 AddToControlRoom 加入 _controlRooms，
            // 但它们已通过 JoinControlRoom 的密码校验并加入了 CTRL_ group，应放行发送。
            // PC 端助手（UID 为真实数字）走 _controlRooms 校验，行为不变。
            // 遥控端（isRemote，不在 _controlRooms）用 RegisterRemoteConnection 登记，也放行发送。
            var isWebSender = !string.IsNullOrEmpty(command.SenderUid) && command.SenderUid.StartsWith("web_");
            var isRemoteSender = _roomManager.IsRemoteConnection(group, ctx.ConnectionId);
            if (!isWebSender && !isRemoteSender && !_roomManager.IsInControlRoom(group, ctx.ConnectionId))
            {
                _logger.LogWarning("玩家 {Sender} 不在控制房间中，拒绝发送命令", command.Sender);
                return;
            }

            // 解析目标
            var targets = _roomManager.ResolveTargets(command);
            var deliveredTo = 0;
            foreach (var connectionId in targets)
            {
                await _broadcaster.SendToConnectionAsync(connectionId, "RemoteCommand", new { command }, command);
                deliveredTo++;
            }

            // 缓存离线目标：仅当明确指定的目标不在线时缓存（"*" 全员时不缓存单品）
            if (command.Target.Count > 0 && !(command.Target.Count == 1 && command.Target[0] == "*"))
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
                message = deliveredTo == 0 ? "没有在线目标" : ""
            };
            await _broadcaster.SendToCallerAsync(ctx, "RemoteCommandAck", ack, ack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendRemoteCommand 失败");
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

            // 广播给控制房间所有成员
            var players = _roomManager.GetControlRoomPlayers(group);
            await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players }, players);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportControlStatus 失败");
        }
    }

    /// <summary>清除指定成员的 OnlineHistory（已联机记录）。由本人或房主调用。</summary>
    public async Task ClearOnlineHistoryAsync(GatewayHandlerContext ctx, string targetUid)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            var roomCode = group.Replace("CTRL_", "");
            _roomManager.ClearOnlineHistory(roomCode, targetUid);

            // 广播更新给所有成员
            var players = _roomManager.GetControlRoomPlayers(group);
            await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players }, players);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearOnlineHistory 失败");
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

            // 广播玩家列表更新
            var players = _roomManager.GetControlRoomPlayers(group);
            await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players }, players);

            // 检查是否可转换为 ready
            if (_roomManager.CheckAndTransition(group, out var readyGeneration))
            {
                var onlinePlayers = players
                    .Where(p => p.Online && !p.OnlineEventConsumed && p.OnlineEventGeneration > 0)
                    .Select(p => p.PlayerUid)
                    .ToList();

                // 单人场景（≤1 人）：跳过确认阶段，直接广播 AllReady
                // 确认阶段的设计目的是"等所有成员确认收到 AllReady"，
                // 单人场景不存在"有人没收到"的问题，跳过可避免：
                //   1. 断线重连后消息发到旧 connectionId 导致丢失
                //   2. 30 秒超时等待，延迟开锄
                if (onlinePlayers.Count <= 1)
                {
                    _roomManager.ConsumeOnlineReady(group, readyGeneration);
                    await _broadcaster.BroadcastGroupAsync(group, "AllReady", new { generation = readyGeneration }, readyGeneration);
                    var refreshed = _roomManager.GetControlRoomPlayers(group);
                    await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players = refreshed }, refreshed);
                }
                else
                {
                    _roomManager.BeginConfirming(group, readyGeneration, onlinePlayers);
                    _ = StartConfirmAsync(group, readyGeneration, onlinePlayers);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportOnlineEvent 失败");
        }
    }

    /// <summary>确认阶段主循环：发送 AllReadyConfirm → 等 ack → 超时重试 → 完成或耗尽。</summary>
    private async Task StartConfirmAsync(string group, int generation, List<string> targetUids)
    {
        const int timeoutMs = 30_000;
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var players = _roomManager.GetControlRoomPlayers(group);
            var pendingUids = _roomManager.GetPendingConfirmUids(group, targetUids)
                .Where(uid => players.Any(p => p.PlayerUid == uid))
                .ToList();
            if (pendingUids.Count == 0) break;

            foreach (var uid in pendingUids)
            {
                var connId = _roomManager.GetConnectionIdByUid(group, uid);
                if (connId != null)
                {
                    await _broadcaster.SendToConnectionAsync(connId, "AllReadyConfirm", new { generation }, generation);
                    _logger.LogInformation("确认阶段: 已向 {Uid} 发送 AllReadyConfirm(generation={Gen}), 第{Attempt}次", uid, generation, attempt);
                }
            }

            var waitStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - waitStart).TotalMilliseconds < timeoutMs)
            {
                if (!_roomManager.IsStateConfirming(group))
                {
                    _logger.LogInformation("确认阶段被中断，generation={Gen}", generation);
                    // 中断时消费已确认成员，避免下次重复触发
                    var confirmedUids = targetUids.Where(uid =>
                        _roomManager.GetConfirmedUids(group).Contains(uid)).ToList();
                    if (confirmedUids.Count > 0)
                        _roomManager.ConsumeOnlineReady(group, generation);
                    return;
                }
                if (_roomManager.IsAllConfirmed(group, targetUids))
                {
                    _logger.LogInformation("全员确认完成, generation={Gen}", generation);
                    _roomManager.ConsumeOnlineReady(group, generation);
                    var confirmed = _roomManager.GetControlRoomPlayers(group);
                    await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players = confirmed }, confirmed);
                    return;
                }
                await Task.Delay(500);
            }
            _roomManager.IncrementConfirmAttempts(group);
            _logger.LogWarning("确认阶段: 超时, 第{Attempt}次, generation={Gen}", attempt, generation);
        }

        // 确认超时耗尽：整轮放弃开锄——宁可不锄/漏锄，也不能缺人开锄（用户明确取舍，推翻 P2-G 降级开锄方案）。
        // MarkExhausted 状态记录保留；日志明确标注未确认成员，便于排查是谁的客户端卡住。
        var unconfirmedUids = _roomManager.GetUnconfirmedUids(group, targetUids);
        _logger.LogWarning("确认超时，本轮放弃开锄（缺人不开锄）, generation={Gen}, 未确认成员={Uids}",
            generation, string.Join(",", unconfirmedUids));
        Console.WriteLine("[探针服务端] 确认超时，本轮放弃开锄（缺人不开锄）, group=" + group + " generation=" + generation + " 未确认成员=" + string.Join(",", unconfirmedUids));
        _roomManager.MarkExhausted(group);
        var exhausted = _roomManager.GetControlRoomPlayers(group);
        await _broadcaster.BroadcastGroupAsync(group, "ControlRoomPlayersUpdated", new { players = exhausted }, exhausted);
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
}
