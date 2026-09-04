using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 异常协调/重对齐族（自 CoordinatorHub 逐字搬迁：WaitPointReport/ReportArrivalAtWaitPoint/
/// ClearAbnormalStatus/PlayerAnomalyNotify/PlayerAnomalyNotifyFightPoint/PlayerAnomalyRecovered/
/// MemberStatusChanged/ReportMemberProgress/RouteSkipped/WaitPointReached/FightingStatusChanged，
/// 及 ValidateWaitPointIsTeleport/GetFirstTeleportPoint/CalculateUnifiedWaitPoint/
/// CalculateExpectedWaitCount/CalculateFinalUnifiedWaitPoint/CalculateExpectedWaitCountAll/
/// ExtractRouteIdFromSyncPoint 私有辅助）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// MemberStatusChanged/ReportMemberProgress 的 AllArrived 重评估循环搬迁后与 F4 辅助
/// （CollectSatisfiedSyncsLocked/ShouldBroadcastAllArrived/EvaluateCollectiveStuckPiggybackAsync）
/// 同属一个 partial 类，直接调用（去掉 _ops. 前缀）。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>
    /// 等待点上报（multiplayer-abnormal-wait-coordination 重构）
    /// 玩家跳过线路并在同步点等待时调用
    /// 服务端验证等待点格式、计算统一等待点、广播给所有正常玩家
    /// </summary>
    /// <param name="routeId">路线ID</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="worldRound">世界轮次</param>
    public async Task WaitPointReportAsync(GatewayHandlerContext ctx, string routeId, string syncPointId, int worldRound)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[WaitPointReport] 连接 {ConnId} 未在任何房间中，忽略等待点上报", ctx.ConnectionId);
            return;
        }
        ObservePhase(room, roomCode, "anomaly.reportWaitPoint");

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[WaitPointReport] 连接 {ConnId} 不在房间玩家列表中", ctx.ConnectionId);
                return;
            }
            playerUid = player.PlayerUid;

            // 多轮世界验证：确保worldRound与房间当前轮次匹配
            if (worldRound != room.CurrentWorldRound)
            {
                _logger.LogWarning("[WaitPointReport] 等待点上报轮次不匹配：玩家{PlayerUid}上报轮次{ReportedRound}，房间轮次{RoomRound}",
                    playerUid, worldRound, room.CurrentWorldRound);
                return; // 忽略跨轮上报
            }
        }

        _logger.LogInformation("[WaitPointReport] 玩家 {Uid} 上报等待点：路线={Route}，同步点={Sync}，轮次={Round}，房间={Code}",
            playerUid, routeId, syncPointId, worldRound, roomCode);

        // 验证等待点格式（需求 2.2, 7.1 - 7.2）
        if (!ValidateWaitPointIsTeleport(syncPointId, out var validationError))
        {
            _logger.LogWarning("[WaitPointReport] 等待点验证失败: {Error}，尝试选择第一个传送点", validationError);
            // 选择该线路的第一个传送点（需求 7.2 - 7.3）
            syncPointId = GetFirstTeleportPoint(routeId);
        }

        // 计算统一等待点（需求 2.1）
        var unifiedWaitPoint = CalculateUnifiedWaitPoint(routeId, syncPointId);

        // 计算预期等待人数（需求 2.3）
        // 更新房间状态
        string finalUnifiedWaitPoint;
        int expectedWaitCount;
        List<string> allAbnormalPlayerUids;

        lock (room)
        {
            // 记录异常玩家状态（需求 1.3）
            room.AbnormalPlayerStates[playerUid] = new AbnormalPlayerState(
                playerUid, routeId, unifiedWaitPoint, worldRound
            );

            // 更新玩家异常状态
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player != null)
            {
                player.IsAbnormal = true;
                player.WaitPointId = unifiedWaitPoint;
            }

            // 存储等待点（用于记录和兼容旧逻辑）
            room.WaitPoints[playerUid] = new WaitPointReport
            {
                PlayerUid = playerUid,
                RouteId = routeId,
                SyncPointId = unifiedWaitPoint,
                WorldRound = worldRound,
                ReportedTime = DateTime.UtcNow,
                ExpiryTime = DateTime.UtcNow.AddMinutes(5) // 5分钟超时
            };

            // === 多异常玩家统一等待点计算 ===
            // 选择路线索引最大的等待点作为统一等待点，合并所有异常玩家
            finalUnifiedWaitPoint = CalculateFinalUnifiedWaitPoint(room, unifiedWaitPoint, routeId, playerUid);

            // 计算预期等待人数（所有在线玩家）
            expectedWaitCount = CalculateExpectedWaitCountAll(room);

            // 获取所有异常玩家UID列表
            allAbnormalPlayerUids = room.AbnormalPlayerStates.Keys.ToList();

            // 设置当前统一等待点（需求 2.1）
            room.CurrentUnifiedWaitPoint = new UnifiedWaitPoint(
                finalUnifiedWaitPoint,
                ExtractRouteIdFromSyncPoint(finalUnifiedWaitPoint),
                worldRound,
                expectedWaitCount
            );
            room.CurrentUnifiedWaitPoint.AbnormalPlayerUids.Clear();
            foreach (var uid in allAbnormalPlayerUids)
            {
                room.CurrentUnifiedWaitPoint.AbnormalPlayerUids.Add(uid);
            }

            _logger.LogInformation("[WaitPointReport] 异常玩家{Uid}上报等待点，最终统一等待点={WaitPoint}，所有异常玩家=[{AbnormalPlayers}]，预期人数={Expected}",
                playerUid, finalUnifiedWaitPoint, string.Join(", ", allAbnormalPlayerUids), expectedWaitCount);
        }

        // 广播 UnifiedWaitPoint 给所有玩家（需求 2.3）
        // 所有玩家（异常+正常）将收到消息并在指定位置汇合
        // 注意：在 lock 外执行 await，避免死锁
        var finalRouteId = ExtractRouteIdFromSyncPoint(finalUnifiedWaitPoint);
        await _broadcaster.BroadcastGroupAsync(roomCode, "UnifiedWaitPoint",
            new { unifiedWaitPoint = finalUnifiedWaitPoint, abnormalPlayerUids = allAbnormalPlayerUids, expectedWaitCount, routeId = finalRouteId },
            finalUnifiedWaitPoint, allAbnormalPlayerUids, expectedWaitCount, finalRouteId);

        _logger.LogInformation("[WaitPointReport] 已广播 UnifiedWaitPoint: 房间={RoomCode}, 等待点={WaitPoint}, 异常玩家=[{Players}], 预期人数={Expected}",
            roomCode, finalUnifiedWaitPoint, string.Join(", ", allAbnormalPlayerUids), expectedWaitCount);
    }

    /// <summary>
    /// 到达等待点上报（multiplayer-abnormal-wait-coordination 需求 5）
    /// 正常玩家到达统一等待点时调用，服务端记录到达状态并在全员到达时广播
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    public async Task ReportArrivalAtWaitPointAsync(GatewayHandlerContext ctx, string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ReportArrivalAtWaitPoint] 连接 {ConnId} 未在任何房间中，忽略到达上报", ctx.ConnectionId);
            return;
        }
        ObservePhase(room, roomCode, "anomaly.reportArrivalAtWaitPoint");

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ReportArrivalAtWaitPoint] 连接 {ConnId} 不在房间玩家列表中", ctx.ConnectionId);
                return;
            }
            playerUid = player.PlayerUid;

            // 记录到达状态
            _roomManager.RecordWaitPointArrival(roomCode, syncPointId, playerUid, player.IsAbnormal);
        }

        _logger.LogInformation("[ReportArrivalAtWaitPoint] 玩家 {Uid} 到达等待点 {SyncPointId}，房间 {RoomCode}",
            playerUid, syncPointId, roomCode);

        // 检查是否全员到达
        var allArrived = _roomManager.CheckAllWaitPointArrived(roomCode, syncPointId);

        if (allArrived)
        {
            _logger.LogInformation("[ReportArrivalAtWaitPoint] 全员到达等待点 {SyncPointId}，房间 {RoomCode}",
                syncPointId, roomCode);

            // 清除异常状态（需求 5.4）
            lock (room)
            {
                var unifiedWaitPoint = room.CurrentUnifiedWaitPoint;
                if (unifiedWaitPoint != null && unifiedWaitPoint.SyncPointId == syncPointId)
                {
                    foreach (var uid in unifiedWaitPoint.AbnormalPlayerUids)
                    {
                        if (room.AbnormalPlayerStates.TryGetValue(uid, out var state))
                        {
                            state.MarkAsRecovered();
                            _logger.LogInformation("[ReportArrivalAtWaitPoint] 异常玩家 {Uid} 已恢复正常", uid);
                        }

                        // 更新玩家状态
                        var abnormalPlayer = room.Players.FirstOrDefault(p => p.PlayerUid == uid);
                        if (abnormalPlayer != null)
                        {
                            abnormalPlayer.IsAbnormal = false;
                            abnormalPlayer.WaitPointId = null;
                        }
                    }

                    // 清除当前统一等待点
                    room.CurrentUnifiedWaitPoint = null;
                }
            }

            // 清除等待点到达记录，防止后续轮次数据污染
            _roomManager.ClearWaitPointArrivals(roomCode);

            // 广播 AllPlayersArrived（需求 5.4）
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllPlayersArrived", new { syncPointId }, syncPointId);
            _logger.LogInformation("[ReportArrivalAtWaitPoint] 已广播 AllPlayersArrived: 房间={RoomCode}, 等待点={SyncPointId}",
                roomCode, syncPointId);
        }
        else
        {
            // 记录当前进度
            var (arrived, expected) = _roomManager.GetWaitPointArrivalStatus(roomCode, syncPointId);
            _logger.LogDebug("[ReportArrivalAtWaitPoint] 等待点 {SyncPointId} 到达进度: {Arrived}/{Expected}",
                syncPointId, arrived, expected);
        }
    }

    /// <summary>
    /// 清除异常状态（需求 5.3, 5.5）
    /// 异常玩家恢复正常后调用，服务端更新状态并广播
    /// </summary>
    public async Task ClearAbnormalStatusAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ClearAbnormalStatus] 连接 {ConnId} 未在任何房间中，忽略状态清除", ctx.ConnectionId);
            return;
        }

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ClearAbnormalStatus] 连接 {ConnId} 不在房间玩家列表中", ctx.ConnectionId);
                return;
            }
            playerUid = player.PlayerUid;

            // 清除异常状态
            if (room.AbnormalPlayerStates.TryGetValue(playerUid, out var state))
            {
                state.MarkAsRecovered();
                _logger.LogInformation("[ClearAbnormalStatus] 异常玩家 {Uid} 的状态已标记为恢复", playerUid);
            }

            // 更新玩家信息
            player.IsAbnormal = false;
            player.WaitPointId = null;
        }

        _logger.LogInformation("[ClearAbnormalStatus] 异常玩家 {Uid} 已恢复正常", playerUid);

        // 广播 AbnormalPlayerRecovered（需求 5.3）
        await _broadcaster.BroadcastGroupAsync(roomCode, "AbnormalPlayerRecovered", new { playerUid }, playerUid);
        _logger.LogInformation("[ClearAbnormalStatus] 已广播 AbnormalPlayerRecovered: 房间={RoomCode}, 玩家={PlayerUid}",
            roomCode, playerUid);
    }

    /// <summary>
    /// 接收玩家异常通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-3.2, REQ-3.3
    /// </summary>
    public async Task PlayerAnomalyNotifyAsync(GatewayHandlerContext ctx, string playerUid, int routeIndex, bool passedSyncPoint)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        // 计算目标汇合线路（需求 REQ-1.3, REQ-1.4）
        int targetRouteIndex = passedSyncPoint ? routeIndex + 1 : routeIndex;

        _logger.LogInformation(
            "[PlayerAnomalyNotify] 房间={RoomCode}, 玩家={PlayerUid}, 线路={RouteIndex}, 已过同步点={Passed}, 目标汇合线路={Target}",
            roomCode, playerUid, routeIndex, passedSyncPoint, targetRouteIndex);

        // 更新服务器端异常状态（需求 REQ-3.2, REQ-3.3）
        lock (room)
        {
            room.AbnormalPlayerInfos[playerUid] = new AbnormalPlayerInfo
            {
                PlayerUid = playerUid,
                RouteIndex = routeIndex,
                PassedSyncPoint = passedSyncPoint,
                TargetRouteIndex = targetRouteIndex,
                ReportTime = DateTime.UtcNow
            };
        }

        // 广播给房间内所有玩家（发送方也会收到，但客户端会过滤自己）（需求 REQ-1.2）
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerAnomalyNotify",
            new { playerUid, routeIndex, passedSyncPoint }, playerUid, routeIndex, passedSyncPoint);
    }

    /// <summary>
    /// 接收"复苏者附带战斗点"的异常通知并广播（hoeing-route-retry-round-end-refactor v3）。
    /// 纯透传：不解析 fightPointId、不进 AbnormalPlayerInfos（区别于既有 PlayerAnomalyNotify）。
    /// 供客户端做"只跳过复苏那一个战斗点"（requirements.md §9 EB-v3-1 / design.md §9.1）。
    /// </summary>
    public async Task PlayerAnomalyNotifyFightPointAsync(GatewayHandlerContext ctx, string playerUid, int routeIndex, int fightPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        _logger.LogInformation(
            "[PlayerAnomalyNotifyFightPoint] 房间={RoomCode}, 玩家={PlayerUid}, 线路={RouteIndex}, 战斗点={FightPointId}",
            roomCode, playerUid, routeIndex, fightPointId);

        // 纯透传广播（发送方也会收到，客户端会过滤自己）
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerAnomalyNotifyFightPoint",
            new { playerUid, routeIndex, fightPointId }, playerUid, routeIndex, fightPointId);
    }

    /// <summary>
    /// 接收玩家异常恢复通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-2.1, REQ-2.2, REQ-3.4
    /// </summary>
    public async Task PlayerAnomalyRecoveredAsync(GatewayHandlerContext ctx, string playerUid)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        _logger.LogInformation("[PlayerAnomalyRecovered] 房间={RoomCode}, 玩家={PlayerUid}", roomCode, playerUid);

        // 从服务器端异常状态中移除（需求 REQ-3.4）
        lock (room)
        {
            room.AbnormalPlayerInfos.Remove(playerUid);
        }

        // 广播给房间内所有玩家（需求 REQ-2.2）
        // 注：旧事件名为 "PlayerAnomalyRecovered"（区别于 ClearAbnormalStatus 的 "AbnormalPlayerRecovered"），
        // LegacyEventMap 未收录该旧名，evt 一侧会记警告并跳过双发（行为与搬迁前一致，见 F6 迁移报告）。
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerAnomalyRecovered", new { playerUid }, playerUid);
    }

    /// <summary>
    /// 更新成员状态。
    /// 当玩家上报 Reviving/Rejoining 时，标记为异常并重新评估 ArrivalSets；
    /// 当玩家上报 Normal 时，清除异常标记。
    /// targetProgress：异常玩家的目标进度值，用于判定其他玩家在某同步点是否需要等他。
    /// </summary>
    public async Task MemberStatusChangedAsync(GatewayHandlerContext ctx, string playerUid, string status, long targetProgress = -1)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        bool isAbnormalReport = status == "Reviving" || status == "Rejoining";
        bool isNormalReport = status == "Normal";

        // 收集每个同步点的进度值（用于判定）
        // syncId → progress 映射需要从客户端推断，这里用 ArrivalSet 中第一个玩家的 CurrentProgress 作为参考
        // 但更安全的做法是：对每个同步点，用 ShouldBroadcastAllArrived 重新判定
        var satisfiedSyncs = new List<(string syncId, long progress)>();

        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null) return;

            if (Enum.TryParse<PlayerStatus>(status, out var parsedStatus))
            {
                player.Status = parsedStatus;
            }

            if (isAbnormalReport)
            {
                player.IsAbnormal = true;
                player.TargetProgress = targetProgress;
                _logger.LogInformation("[MemberStatusChanged] 玩家={PlayerUid} 上报异常={Status}，目标进度={Target}",
                    playerUid, status, targetProgress);

                // 重新评估所有未完成的同步点
                // 用每个同步点中已到达玩家的最大 CurrentProgress 作为 syncProgress
                _logger.LogInformation("[MemberStatusChanged] 开始重评估，房间 ArrivalSets 数量: {N}", room.ArrivalSets.Count);
                foreach (var kvp in room.ArrivalSets)
                {
                    var syncId = kvp.Key;
                    var arrivals = kvp.Value;

                    // route_sync_done 是全局同步点，使用 -1（按"等所有"处理）
                    long syncProgress = -1;
                    if (syncId != "route_sync_done")
                    {
                        // 用已到达玩家的最大 CurrentProgress
                        syncProgress = room.Players
                            .Where(p => arrivals.Contains(p.ConnectionId))
                            .Select(p => p.CurrentProgress)
                            .DefaultIfEmpty(-1)
                            .Max();
                    }

                    _logger.LogInformation("[MemberStatusChanged] 评估同步点 {SyncId}, syncProgress={SP}, ArrivalSet={Arr}",
                        syncId, syncProgress, string.Join(",", arrivals));

                    if (ShouldBroadcastAllArrived(room, syncId, arrivals, syncProgress))
                    {
                        _logger.LogWarning("[MemberStatusChanged] 同步点 {SyncId} 满足放行条件！", syncId);
                        satisfiedSyncs.Add((syncId, syncProgress));
                    }
                }
            }
            else if (isNormalReport)
            {
                player.IsAbnormal = false;
                player.TargetProgress = -1;
                _logger.LogInformation("[MemberStatusChanged] 玩家={PlayerUid} 恢复正常状态", playerUid);
            }
            else
            {
                _logger.LogDebug("[MemberStatusChanged] 玩家={PlayerUid}, 状态={Status}", playerUid, status);
            }
        }

        // 广播满足条件的同步点（在 lock 外执行 await）
        foreach (var (syncId, progress) in satisfiedSyncs)
        {
            _logger.LogInformation("[MemberStatusChanged] 异常上报后重评估：同步点 {SyncId} 满足条件，广播 AllArrived（房间={RoomCode}, 进度={Progress}）",
                syncId, roomCode, progress);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId = syncId }, syncId);
            _roomManager.ClearArrivalSet(roomCode, syncId);
            lock (room) { room.BroadcastedSyncIds.Add(syncId); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
        }

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>
    /// 客户端在跳路线后立即广播自己的新进度（multiplayer-sync-skip-by-progress §2.4）。
    /// 服务端更新对应玩家的 CurrentProgress = routeIndex * 1_000_000，
    /// 并触发对房间所有 ArrivalSets 的全量重评估（与 MemberStatusChanged / WaitForAllPlayers 同一机制）。
    ///
    /// 鉴权（OQ-2 方案 A）：用 ctx.ConnectionId 定位本连接对应的玩家，
    ///   校验 player.PlayerUid == playerUid（playerUid 非空时）。不一致直接 LogWarning + return。
    /// 兼容性：旧客户端不调用此方法即可，新增 Hub 方法不破坏旧协议。
    /// </summary>
    public async Task ReportMemberProgressAsync(GatewayHandlerContext ctx, string playerUid, int routeIndex)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        long newProgress = (long)routeIndex * 1_000_000L;

        List<(string syncId, long progress)> satisfiedSyncs;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ReportMemberProgress] 连接 {ConnId} 不在任何房间玩家列表中，忽略", ctx.ConnectionId);
                return;
            }

            // 鉴权：禁止以他人身份上报
            if (!string.IsNullOrEmpty(playerUid) && player.PlayerUid != playerUid)
            {
                _logger.LogWarning("[ReportMemberProgress] 鉴权失败：调用方 PlayerUid={ActualUid} 与上报 PlayerUid={ReportedUid} 不一致，忽略",
                    player.PlayerUid, playerUid);
                return;
            }

            var oldProgress = player.CurrentProgress;
            player.CurrentProgress = newProgress;
            _logger.LogInformation("[ReportMemberProgress] 玩家={Uid}, 路线={Index}, CurrentProgress: {Old} → {New}",
                player.PlayerUid, routeIndex, oldProgress, newProgress);

            // 全量重评估：进度更新后历史同步点可能因豁免而满足放行
            satisfiedSyncs = CollectSatisfiedSyncsLocked(room);
        }

        foreach (var (sid, sp) in satisfiedSyncs)
        {
            _logger.LogInformation("[ReportMemberProgress] 进度更新后重评估：同步点 {SyncId} 满足条件，广播 AllArrived（房间={RoomCode}, 进度={Progress}）",
                sid, roomCode, sp);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId = sid }, sid);
            _roomManager.ClearArrivalSet(roomCode, sid);
            lock (room) { room.BroadcastedSyncIds.Add(sid); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
        }

        // === hoeing-multiplayer-lagging-member-catchup（改动 8）：刷新 CurrentProgress 后广播玩家列表 ===
        // 使客户端 CurrentPlayerList 缓存的段级 CurrentProgress 随同步点推进刷新（落后追赶判定数据源，避免 BUG-C）。
        // lock 外 await，复用已有 PlayerListUpdated 事件，无新增协议；旧客户端忽略多余字段/推送。
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerListUpdated", new { players = room.Players }, room.Players);

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>
    /// 记录路线跳过
    /// </summary>
    public Task RouteSkippedAsync(GatewayHandlerContext ctx, string playerUid, int routeIndex)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogInformation("[RouteSkipped] 房间={RoomCode}, 玩家={PlayerUid}, 路线={RouteIndex}",
            roomCode, playerUid, routeIndex);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录等待点到达
    /// </summary>
    public Task WaitPointReachedAsync(GatewayHandlerContext ctx, string playerUid, string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogDebug("[WaitPointReached] 房间={RoomCode}, 玩家={PlayerUid}, 同步点={SyncPointId}",
            roomCode, playerUid, syncPointId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 更新战斗状态
    /// </summary>
    public Task FightingStatusChangedAsync(GatewayHandlerContext ctx, string playerUid, bool isFighting)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogDebug("[FightingStatusChanged] 玩家={PlayerUid}, 战斗中={IsFighting}", playerUid, isFighting);

        return Task.CompletedTask;
    }

    // === 等待点验证与计算方法（multiplayer-abnormal-wait-coordination 需求 2、7）===

    /// <summary>
    /// 验证等待点是否为传送点格式（需求 7.1 - 7.2）
    /// 等待点必须包含 _tp_ 标识符
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="errorMessage">错误信息（验证失败时填充）</param>
    /// <returns>是否为有效的传送点格式</returns>
    private bool ValidateWaitPointIsTeleport(string syncPointId, out string errorMessage)
    {
        errorMessage = "";

        if (string.IsNullOrEmpty(syncPointId))
        {
            errorMessage = "等待点ID为空";
            return false;
        }

        // 检查是否包含 _tp_ 标识符（需求 7.1）
        if (!syncPointId.Contains("_tp_"))
        {
            errorMessage = $"等待点 {syncPointId} 不包含 _tp_ 标识符，不是有效的传送点";
            return false;
        }

        // 验证格式：{routeId}_tp_{listIdx}_{wpIdx} 或 {fileName}_{routeId}_tp_{listIdx}_{wpIdx}
        var parts = syncPointId.Split('_');
        var tpIndex = Array.IndexOf(parts, "tp");

        if (tpIndex < 0 || tpIndex >= parts.Length - 2)
        {
            errorMessage = $"等待点 {syncPointId} 格式不正确，缺少索引部分";
            return false;
        }

        // 验证 listIdx 和 wpIdx 是否为数字
        if (!int.TryParse(parts[tpIndex + 1], out _) || !int.TryParse(parts[tpIndex + 2], out _))
        {
            errorMessage = $"等待点 {syncPointId} 的索引部分不是有效数字";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 获取指定路线的第一个传送点（需求 7.2 - 7.3）
    /// 优先选择 _tp_0_0 格式
    /// </summary>
    /// <param name="routeId">路线ID</param>
    /// <returns>第一个传送点ID</returns>
    private string GetFirstTeleportPoint(string routeId)
    {
        // 默认返回 _tp_0_0 格式的传送点
        return $"{routeId}_tp_0_0";
    }

    /// <summary>
    /// 计算统一等待点（需求 2.1）
    /// 规则：验证上报的等待点，如果不是传送点则回退到该线路的第一个传送点
    /// </summary>
    /// <param name="routeId">路线ID</param>
    /// <param name="reportedSyncPointId">上报的同步点ID</param>
    /// <returns>统一等待点ID</returns>
    private string CalculateUnifiedWaitPoint(string routeId, string reportedSyncPointId)
    {
        // 验证上报的等待点
        if (!ValidateWaitPointIsTeleport(reportedSyncPointId, out var errorMessage))
        {
            _logger.LogWarning("[CalculateUnifiedWaitPoint] 上报的等待点验证失败: {Error}，回退到该线路的第一个传送点", errorMessage);
            // 回退到该线路的第一个传送点
            return GetFirstTeleportPoint(routeId);
        }

        // 等待点有效，使用该点
        _logger.LogInformation("[CalculateUnifiedWaitPoint] 统一等待点: {SyncPointId}", reportedSyncPointId);
        return reportedSyncPointId;
    }

    /// <summary>
    /// 计算预期等待人数（需求 2.3）
    /// 规则：已到达该线路的正常玩家数 + 异常玩家数
    /// </summary>
    /// <param name="room">房间实例</param>
    /// <param name="abnormalPlayerUid">异常玩家UID</param>
    /// <returns>预期等待人数</returns>
    private int CalculateExpectedWaitCount(Room room, string abnormalPlayerUid)
    {
        lock (room)
        {
            int normalPlayersAtRoute = 0;
            int abnormalPlayersAtRoute = 0;

            foreach (var player in room.Players)
            {
                // 跳过离线玩家（超过2分钟无心跳）
                if (DateTime.UtcNow - player.LastHeartbeat > TimeSpan.FromMinutes(2))
                {
                    _logger.LogDebug("[CalculateExpectedWaitCount] 跳过离线玩家: {PlayerUid}", player.PlayerUid);
                    continue;
                }

                if (player.PlayerUid == abnormalPlayerUid)
                {
                    abnormalPlayersAtRoute++;
                    _logger.LogDebug("[CalculateExpectedWaitCount] 异常玩家: {PlayerUid}", player.PlayerUid);
                }
                else if (!player.IsAbnormal)
                {
                    normalPlayersAtRoute++;
                    _logger.LogDebug("[CalculateExpectedWaitCount] 正常玩家: {PlayerUid}", player.PlayerUid);
                }
            }

            int expectedCount = normalPlayersAtRoute + abnormalPlayersAtRoute;
            _logger.LogInformation("[CalculateExpectedWaitCount] 正常玩家={Normal}, 异常玩家={Abnormal}, 总计={Total}",
                normalPlayersAtRoute, abnormalPlayersAtRoute, expectedCount);

            return Math.Max(1, expectedCount);
        }
    }

    // === 缺少的辅助方法（暂时添加存根以修复编译错误）===
    // TODO: 这些方法应该在 multiplayer-sync-refactor 清理计划中删除或正确实现

    /// <summary>
    /// 计算最终统一等待点（多异常玩家场景）
    /// </summary>
    private string CalculateFinalUnifiedWaitPoint(Room room, string currentWaitPoint, string routeId, string playerUid)
    {
        // 简单实现：返回当前等待点
        // 完整实现应根据路线索引选择最远的等待点
        return currentWaitPoint;
    }

    /// <summary>
    /// 计算预期等待人数（所有在线玩家）
    /// </summary>
    private int CalculateExpectedWaitCountAll(Room room)
    {
        lock (room)
        {
            return room.Players.Count(p =>
                DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>
    /// 从同步点ID中提取路线ID
    /// </summary>
    private string ExtractRouteIdFromSyncPoint(string syncPointId)
    {
        if (string.IsNullOrEmpty(syncPointId))
            return "";

        // 格式：{routeId}_tp_{listIdx}_{wpIdx} 或 {fileName}_{routeId}_tp_{listIdx}_{wpIdx}
        var parts = syncPointId.Split('_');
        var tpIndex = Array.IndexOf(parts, "tp");

        if (tpIndex > 0)
        {
            // 路线ID在 _tp_ 之前
            return string.Join("_", parts.Take(tpIndex));
        }

        return syncPointId;
    }
}
