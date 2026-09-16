using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 世界族（自 CoordinatorHub 逐字搬迁：ReportWorldJoined/ResetWorldJoined/ResetForNewWorldRound）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// ResetForNewWorldRound 另加显式 RoundTransition 观测（Mark + 复位完成后 ObservePhase，
/// 只打日志、不驱动行为）。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>上报已加入世界，全员加入时广播 AllWorldJoined</summary>
    public async Task ReportWorldJoinedAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "world.reportJoined");

        var allJoined = _roomManager.RecordWorldJoined(roomCode, ctx.ConnectionId);
        _logger.LogInformation("连接 {ConnId} 上报已加入世界，房间 {Code}，全员: {All}",
            ctx.ConnectionId, roomCode, allJoined);

        if (allJoined)
        {
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllWorldJoined", null);
        }
    }

    /// <summary>重置已加入世界的记录（多世界模式新轮次开始时调用）</summary>
    public Task ResetWorldJoinedAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        ObservePhase(room, roomCode, "world.resetJoined");
        if (room != null && roomCode != null && room.HostConnectionId == ctx.ConnectionId)
        {
            _roomManager.ResetWorldJoinedSet(roomCode);
            _logger.LogInformation("[ResetWorldJoined] 房间 {Code} WorldJoinedSet 已重置", roomCode);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 多轮世界重置（multiplayer-abnormal-wait-coordination 重构）
    /// 多轮世界新轮次开始时调用，清理所有等待点状态和异常状态
    /// </summary>
    public Task ResetForNewWorldRoundAsync(GatewayHandlerContext ctx, int newRound)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _phaseObserver.Mark(roomCode, RoomPhase.RoundTransition, "world.resetForNewRound");

        lock (room)
        {
            room.RerunExecution?.Abort("WorldReset");
            room.RerunExecution = null;
            room.CurrentWorldRound = newRound;
            room.WaitPoints.Clear(); // 清理所有等待点

            // 清理异常玩家状态（multiplayer-abnormal-wait-coordination 需求 8.5）
            room.AbnormalPlayerStates.Clear();
            room.CurrentUnifiedWaitPoint = null;
            room.WaitPointArrivals.Clear();

            // 清理玩家异常状态标记
            foreach (var player in room.Players)
            {
                player.IsAbnormal = false;
                player.WaitPointId = null;
                // multiplayer-sync-skip-by-progress §3.9 / OQ-1：
                // 同步重置进度字段，避免上一轮残留 CurrentProgress 污染新一轮第一个同步点的豁免判定
                player.TargetProgress = -1;
                player.CurrentProgress = -1;
            }

            // 清理联机锄地异常同步状态（multiplayer-abnormal-sync-server 需求 REQ-6.1）
            room.AbnormalPlayerInfos.Clear();

            // 清理万叶聚物候选 + 状态（kazuha-player-auto-detection: 多世界轮换重置）
            room.KazuhaCandidates.Clear();
            room.KazuhaCollect.KazuhaConnectionId = null;

            // === 集体卡死监测字段重置（multiplayer-mutual-wait-collective-skip §3.10 / §8.4 改动 4）===
            room.ConsecutiveCollectiveSkipCount = 0;
            room.ConsecutiveCollectiveSkipHostConnectionId = "";
            room.LastCollectiveSkipTargetProgress = -1;
            room.LastArrivalSetsSnapshot = null;
            // 同步点进度快照一并清（collective-stuck-orphan-arrivalset fix）：
            // 轮换后 CurrentProgress 复位为 -1，残留进度快照会让跨轮同名 syncId 的旧集合判定失真
            room.ArrivalSetProgress.Clear();

            // === 房主路线列表上传标志重置（multiplayer-host-empty-route-member-wait-timeout-fix）===
            // 新一轮房主重新筛选并上传路线列表，避免沿用上一轮的"已上传"状态导致成员误判
            room.HostRouteList = [];
            room.HostRouteListUploaded = false;
            room.ObservationStartTime = default;
            room.CollectiveSkipTimer?.Dispose();
            room.CollectiveSkipTimer = null;

            // === 集体跳段 Applied 确认状态重置（collective-skip-applied-ack）===
            // 新轮次不得继承上一轮的 SkipId：否则客户端迟到的旧 skipId 回报会与新轮次混淆，
            // 且残留的活动跳段会阻止新轮次生成跳段命令。
            ClearActiveCollectiveSkipLocked(room);

            // === 路线边界锚点重置（route-anchor）===
            // 新世界必须新建会话：旧锚点（含旧代际与旧参与者）无权影响新会话。
            // RouteAnchorSequence 刻意不复位：锚点 ID 在房间生命周期内保持单调唯一。
            room.ActiveRouteAnchor = null;
            room.RouteAnchorEvaluateTimer?.Dispose();
            room.RouteAnchorEvaluateTimer = null;

            // 已放行边界必须复位：这是**每世界轮次**的状态。
            // 缺陷（第二十三轮复查发现）：不复位会让第 2 个世界的边界 0..N 全部
            // < 上一世界的最大边界 → Enroll 被判 stale_boundary 拒绝 → 客户端 gate 判 Failed →
            // 整个第二世界每一轮都被误停。与第十一轮客户端同类缺陷（跨轮状态未复位）同源。
            room.LastReleasedRouteBoundary = -1;

            // fastsync-claim-short-circuit-premature-release-fix（OQ-3=c→落地清理）：
            // syncId 不含轮次标识，同名路线跨轮复用。不清理则上一轮已广播的 syncId 残留，
            // 本轮第一个到达者一调 WaitForAllPlayers 即被补发 AllArrived → 跨轮误放。
            room.BroadcastedSyncIds.Clear();

            // multiplayer-shared-fight-end-quorum-sync: 多世界轮换清空战斗参与者集合，避免陈旧分母
            room.FightParticipantSets.Clear();
            room.FightDoneSets.Clear();
            room.FightDoneBroadcasted.Clear();

            // multiplayer-hoeing-exp-cap-stop: 多世界轮换清空经验上限集合与广播标志
            room.ExpCapReachedSet.Clear();
            room.ExpCapBroadcasted = false;
            // 团队 arming 门控每轮复位（multiplayer-hoeing-exp-cap-stop R7.6）
            room.ExpCapArmed = false;
            // exp-cap-prefinal-stop-by-two-noexp: 新轮清空连续2场无经验预警集合
            room.TwoConsecutiveNoExpSet.Clear();

            // hoeing-multiplayer-coordinated-abort-restart: 多世界轮换复位协同中止幂等标志
            // （房间跨轮复用时，上一轮的中止广播不得吞掉本轮的中止上报）
            room.CoordinatedAbortBroadcasted = false;

            _logger.LogInformation("[ResetForNewWorldRound] 房间{RoomCode}进入第{Round}轮，等待点、异常状态、万叶候选已重置", roomCode, newRound);
        }

        ObservePhase(room, roomCode, "world.resetForNewRound");

        return Task.CompletedTask;
    }
}
