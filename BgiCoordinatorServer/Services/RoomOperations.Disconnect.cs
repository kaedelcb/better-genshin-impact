using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 断线清理（自 CoordinatorHub.OnDisconnectedAsync 逐字搬迁）。
/// 旧 CoordinatorHub 与新 GatewayHub 的 OnDisconnectedAsync 都委托到这里——
/// 宽限期/房主关房/同步点重评估/万叶顶替/控制房间清理/日志订阅清理单一事实源。
/// 注意：控制房间清理依赖本类静态 _connectionGroups 跟踪表（TrackGroup 也在本类），
/// 必须与组跟踪同源，否则断线清理找不到所属 Group。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>连接断线清理。两个 Hub 的 OnDisconnectedAsync 共用。</summary>
    public async Task HandleDisconnectAsync(GatewayHandlerContext ctx, Exception? exception)
    {
        // 获取断线玩家所在的房间信息
        var (disconnectedRoom, disconnectedRoomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        var wasHost = disconnectedRoom?.HostConnectionId == ctx.ConnectionId;

        if (disconnectedRoom != null && disconnectedRoomCode != null)
        {
            if (wasHost)
            {
                // === 房主断线：保持现有逻辑（广播 RoomClosed + 删房）===
                _logger.LogWarning("[OnDisconnectedAsync] 房主断线，广播 RoomClosed: 房间={RoomCode}", disconnectedRoomCode);
                _phaseObserver.Mark(disconnectedRoomCode, RoomPhase.Ended, "disconnect.host");
                _phaseObserver.Forget(disconnectedRoomCode);
                await _broadcaster.BroadcastGroupAsync(disconnectedRoomCode, "RoomClosed",
                    new { reason = "房主已断开连接" }, "房主已断开连接");
                _roomManager.LeaveRoom(ctx.ConnectionId);
                _roomManager.DeleteRoom(disconnectedRoomCode);
            }
            else
            {
                // === 成员断线：进宽限期，不删人、不广播 PlayerListUpdated 缩水 ===
                lock (disconnectedRoom)
                {
                    disconnectedRoom.GracePendingMembers[ctx.ConnectionId] = DateTime.UtcNow.AddSeconds(30);
                }
                _logger.LogInformation("[OnDisconnectedAsync] 成员 {ConnId} 进入宽限期(30s)，房间 {Code} 人数保持 {N}",
                    ctx.ConnectionId, disconnectedRoomCode, disconnectedRoom.Players.Count);

                // SignalR 会自动从 Group 移除断线连接，room.Players 不删

                // 重新评估所有未完成的同步点（断线的人不应阻塞同步点）
                List<string> satisfiedSyncIds;
                lock (disconnectedRoom)
                {
                    satisfiedSyncIds = disconnectedRoom.ArrivalSets
                        .Where(kvp => AllOnlineMembersReportedStatic(disconnectedRoom, kvp.Value))
                        .Select(kvp => kvp.Key)
                        .ToList();
                }

                // 广播满足条件的同步点（在 lock 外执行 await）
                foreach (var syncId in satisfiedSyncIds)
                {
                    _logger.LogInformation("[OnDisconnectedAsync] 玩家断线后重新评估：同步点 {SyncId} 条件满足，广播 AllArrived，房间={RoomCode}",
                        syncId, disconnectedRoomCode);
                    await _broadcaster.BroadcastGroupAsync(disconnectedRoomCode, "AllArrived",
                        new { syncPointId = syncId }, syncId);
                    _roomManager.ClearArrivalSet(disconnectedRoomCode, syncId);
                    lock (disconnectedRoom) { disconnectedRoom.BroadcastedSyncIds.Add(syncId); }
                }

                // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 5）===
                await EvaluateCollectiveStuckPiggybackAsync(disconnectedRoom, disconnectedRoomCode);

                // 万叶聚物同步：候选切换 + 兜底（kazuha-player-auto-detection requirements 5.5 / Property 10）
                bool shouldBroadcastSwitch = false;
                string switchedToUid = "";
                lock (disconnectedRoom)
                {
                    disconnectedRoom.KazuhaCandidates.RemoveAll(c => c.ConnectionId == ctx.ConnectionId);

                    if (disconnectedRoom.KazuhaCollect.KazuhaConnectionId == ctx.ConnectionId)
                    {
                        var onlineCandidate = disconnectedRoom.KazuhaCandidates.FirstOrDefault(c =>
                            disconnectedRoom.Players.Any(p => p.ConnectionId == c.ConnectionId
                                && DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2)));

                        if (onlineCandidate != null)
                        {
                            disconnectedRoom.KazuhaCollect.KazuhaConnectionId = onlineCandidate.ConnectionId;
                            switchedToUid = onlineCandidate.PlayerUid;
                            shouldBroadcastSwitch = true;
                        }
                        else
                        {
                            disconnectedRoom.KazuhaCollect.KazuhaConnectionId = null;
                        }
                    }
                }
                if (shouldBroadcastSwitch)
                {
                    _logger.LogInformation("[OnDisconnectedAsync] 万叶玩家断线，切换到下一候选 {Uid}，房间={RoomCode}",
                        switchedToUid, disconnectedRoomCode);
                    await _broadcaster.BroadcastGroupAsync(disconnectedRoomCode, "KazuhaPlayerUpdated",
                        new { playerUid = switchedToUid }, switchedToUid);
                }
            }
        }

        _logger.LogInformation("连接 {ConnId} 断开，房间={Room}",
            ctx.ConnectionId, disconnectedRoomCode ?? "(无)");

        // 清理控制房间成员（必须在移除 _connectionGroups 跟踪之前执行，否则找不到所属 Group）
        try
        {
            // 从 `_connectionGroups` 中找出当前连接所属的所有 Group
            if (_connectionGroups.TryGetValue(ctx.ConnectionId, out var groups))
            {
                List<string> groupList;
                lock (groups) { groupList = [.. groups]; }

                foreach (var group in groupList)
                {
                    if (group.StartsWith("CTRL_"))
                    {
                        _roomManager.RemoveFromControlRoom(group, ctx.ConnectionId);
                        // 遥控端不入 _controlRooms，RemoveFromControlRoom 对其 no-op；
                        // 需单独清理遥控端连接登记，防止 _remoteControlConnections 残留。
                        _roomManager.RemoveRemoteConnection(group, ctx.ConnectionId);
                        // 断线标记离线后全量广播最新成员列表（统一出口，payload 形状与增量路径一致）
                        _ = BroadcastControlRoomPlayersAsync(group, forceFull: true);
                        _logger.LogInformation("控制房间 {Group} 成员断线，已标记离线", group);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 清理 group 跟踪表时发生异常，忽略
            _logger.LogWarning(ex, "清理控制房间 Group 时发生异常");
        }

        // 清理 group 跟踪表，避免静态字典内存泄漏
        _connectionGroups.TryRemove(ctx.ConnectionId, out _);

        // 日志订阅清理（房间实时日志汇聚）：断线连接从所有订阅中移除，并通知各目标成员最新订阅数
        try
        {
            foreach (var (group, targetUid, count) in _roomManager.RemoveLogSubscriberEverywhere(ctx.ConnectionId))
                await NotifyLogSubscriberCountAsync(group, targetUid, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理日志订阅时发生异常");
        }
    }
}
