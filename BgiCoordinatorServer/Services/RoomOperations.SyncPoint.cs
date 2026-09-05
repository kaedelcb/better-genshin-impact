using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 同步点+战斗+心跳族（自 CoordinatorHub 逐字搬迁：ReportArrival/ReportArrivalWithExpectedCount/
/// WaitForAllPlayers/ReportFightDone/ReportFightParticipant/Heartbeat/HeartbeatWithProgress，
/// 及 CollectSatisfiedSyncsLocked/ShouldBroadcastAllArrived(实例+static)/AllOnlineMembersReportedStatic/
/// AllNormalOnlineMembersReported/IsCollectiveStuckLocked/ArrivalSnapshotEquals/
/// EvaluateCollectiveStuckPiggybackAsync/EvaluateCollectiveStuckTimerCallbackAsync 私有辅助）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// 集体卡死 Timer 回调由 Hub 实例改为本服务方法（singleton，与原捕获 transient Hub 实例语义等价：
/// 原 Hub 被 Timer rooting，行为一致）。
/// MemberStatusChanged/ReportMemberProgress 已于 F6 迁入 RoomOperations.Anomaly.cs，
/// OnDisconnectedAsync 已于 F9 迁入 RoomOperations.Disconnect.cs，
/// 全部调用方与本文件辅助同属一个 partial 类，直接调用（不再经 _ops）。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>上报到达集合点，全员到达时广播 AllArrived</summary>
    public async Task ReportArrivalAsync(GatewayHandlerContext ctx, string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "sync.reportArrival");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allArrived = _roomManager.RecordArrival(roomCode, syncPointId, ctx.ConnectionId, 0);

        if (allArrived)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 全员到达", roomCode, syncPointId);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId }, syncPointId);
        }

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        if (room != null)
        {
            await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
        }
    }

    /// <summary>
    /// 上报到达集合点（带预期人数），指定人数到达时广播 AllArrived
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    public async Task ReportArrivalWithExpectedCountAsync(GatewayHandlerContext ctx, string syncPointId, int expectedCount)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "sync.reportArrival");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allArrived = _roomManager.RecordArrival(roomCode, syncPointId, ctx.ConnectionId, expectedCount);

        if (allArrived)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 到达人数达到预期 {Expected}，触发 AllArrived",
                roomCode, syncPointId, expectedCount);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId }, syncPointId);
        }

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        if (room != null)
        {
            await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
        }
    }

    /// <summary>
    /// 等待所有玩家到达指定同步点（非阻塞模式：记录到达 → 检查条件 → 广播 → 立即返回）
    /// 客户端通过本地 TCS + AllArrived 事件等待，服务端不阻塞 SignalR 连接。
    ///
    /// 判定规则（基于全局进度值）：
    ///   对每个异常玩家 P：
    ///     P.TargetProgress == syncProgress → P 正要去 X → 等他
    ///     P.TargetProgress != syncProgress → P 跳过了 X 或不会到 X → 不等他
    ///   对每个正常玩家 P（multiplayer-sync-skip-by-progress §2.1）：
    ///     P.CurrentProgress > syncProgress → P 已穿过此同步点 → 不等他
    ///     否则 → 等他
    ///
    /// 进度更新后回头重评估（multiplayer-sync-skip-by-progress §2.3）：
    ///   syncProgress >= 0 时 caller.CurrentProgress 被刷新，房间内其他历史 ArrivalSets
    ///   可能因 caller 被新豁免逻辑剔除而满足放行条件，需用 CollectSatisfiedSyncsLocked
    ///   全量评估后批量广播 AllArrived。
    /// </summary>
    public async Task WaitForAllPlayersAsync(GatewayHandlerContext ctx, string syncId, long syncProgress = -1)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;
        ObservePhase(room, roomCode, "sync.waitForAllPlayers");

        _logger.LogDebug("[WaitForAllPlayers] 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}, 连接={ConnId}",
            roomCode, syncId, syncProgress, ctx.ConnectionId);

        // 更新当前玩家的进度 + 记录该同步点的全局进度快照
        if (syncProgress >= 0)
        {
            lock (room)
            {
                var caller = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
                if (caller != null)
                {
                    caller.CurrentProgress = syncProgress;
                }
                // collective-stuck-orphan-arrivalset fix：存该 syncId 的真实全局进度，
                // 供放行/卡死判定使用，避免孤儿集合被成员归约 sp 卡死（见 Room.ArrivalSetProgress 注释）
                room.ArrivalSetProgress[syncId] = syncProgress;
            }
        }

        // 记录当前连接已到达
        _roomManager.RecordArrival(roomCode, syncId, ctx.ConnectionId, 0);

        // === 到达同步点时自动清理异常状态（multiplayer-mutual-wait-collective-skip fix）===
        // 条件与 WillBroadcastHere 的清理逻辑完全对称：
        //   异常玩家 + TargetProgress > 0 + syncProgress >= 0 + TargetProgress <= syncProgress
        // 这样即使 CollectiveSkip 标记了异常，玩家到达目标同步点后也能自动恢复正常，
        // 不会因为 IsAbnormal 残留而导致后续同步点判定异常（requiredPlayers 为空 → 不广播）。
        // 只清理调用方自己（caller），不清理其他玩家（比 WillBroadcastHere 更保守）。
        lock (room)
        {
            var caller = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (caller != null && caller.IsAbnormal && caller.TargetProgress > 0 && syncProgress >= 0 && caller.TargetProgress <= syncProgress)
            {
                _logger.LogInformation("[WaitForAllPlayers] 异常玩家 {Uid} 到达同步点 {SyncId}（progress={SP}），清除异常状态（Target={T}）",
                    caller.PlayerUid, syncId, syncProgress, caller.TargetProgress);
                caller.IsAbnormal = false;
                caller.TargetProgress = -1;
            }
        }

        // fastsync-claim-short-circuit-premature-release-fix（OQ-1=a）：
        // 若该 syncId 本轮已广播过 AllArrived（说明已全员放行、ArrivalSet 已清空），
        // 则对晚到的本调用方单独补发 AllArrived 解锁——它错过了 Clients.Group 广播，
        // 删短路后会订阅一个不会再触发的事件而死等到 120s（bugfix.md 组合 7）。
        bool alreadyBroadcasted;
        bool releaseLaggingCaller;
        lock (room)
        {
            alreadyBroadcasted = room.BroadcastedSyncIds.Contains(syncId);

            // falling-behind-fix 方案 B：判定 caller 是否为孤立落后者。
            // 取除 caller 外、所有在线（心跳<2min）正常玩家的 CurrentProgress；
            // 若 syncProgress 严格小于它们全部，则 caller 落后于所有人、其它人不会再以此 syncId 到达。
            // 异常玩家（IsAbnormal）不纳入比较，保持其分支现状不受影响。
            var others = room.Players
                .Where(p => p.ConnectionId != ctx.ConnectionId
                            && !p.IsAbnormal
                            && DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.CurrentProgress)
                .ToList();
            releaseLaggingCaller = LaggingMemberReleaseDecisions.ShouldReleaseLaggingCaller(syncProgress, others);
        }
        if (SyncReplayDecisions.ShouldReplayAllArrived(alreadyBroadcasted))
        {
            _logger.LogInformation("[WaitForAllPlayers] 该同步点本轮已放行，补发 AllArrived 给晚到调用方: 房间={RoomCode}, 同步点={SyncId}, 连接={ConnId}",
                roomCode, syncId, ctx.ConnectionId);
            await _broadcaster.SendToCallerAsync(ctx, "AllArrived", new { syncPointId = syncId }, syncId);
            // collective-stuck-orphan-arrivalset fix：补发即放行，清掉刚才 RecordArrival 写入的到达记录，
            // 避免在已广播过的 syncId 上留下孤儿集合（该点本轮不会再全量广播，残留永不消）
            _roomManager.RemoveArrival(roomCode, syncId, ctx.ConnectionId);
            // 补发后仍继续走全量重评估（幂等：不改 BroadcastedSyncIds 状态），
            // 保证其他历史 syncId 的放行不被跳过。
        }
        else if (releaseLaggingCaller)
        {
            // 孤立落后者补发（方案 B）：该 syncId 未被全员广播过（alreadyBroadcasted=false），
            // 但 caller 已严格落后所有其他在线玩家，它们不会再以此 syncId 到达 → ArrivalSet 永不齐。
            // 直接对 caller 补发 AllArrived（等价"你落后了，别等了，放你走"），避免死等满 120s。
            // 严格小于(<)防误放：进度相等的正常碰头玩家不会触发本分支（ShouldReleaseLaggingCaller 返回 false）。
            // 仅对 caller 补发，不改 BroadcastedSyncIds、不动其他玩家。
            // collective-stuck-orphan-arrivalset fix：补发后清掉 caller 的到达记录——
            // 集合内只剩 caller 一人且其他玩家不会再到达，残留即孤儿，会虚增集体卡死判定的 C1 计数。
            _logger.LogInformation("[WaitForAllPlayers] caller 为孤立落后者，补发 AllArrived 放行: 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}, 连接={ConnId}",
                roomCode, syncId, syncProgress, ctx.ConnectionId);
            await _broadcaster.SendToCallerAsync(ctx, "AllArrived", new { syncPointId = syncId }, syncId);
            _roomManager.RemoveArrival(roomCode, syncId, ctx.ConnectionId);
        }

        // 全量重评估：当前 syncId 与所有历史 ArrivalSets 一并判定
        List<(string syncId, long progress)> satisfiedSyncs;
        lock (room)
        {
            satisfiedSyncs = CollectSatisfiedSyncsLocked(room);
        }

        bool willBroadcastHere = satisfiedSyncs.Any(t => t.syncId == syncId);

        // 在 lock 外逐个广播 + 清 ArrivalSet
        foreach (var (sid, sp) in satisfiedSyncs)
        {
            _logger.LogInformation("[WaitForAllPlayers] 满足放行条件，广播 AllArrived: 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}",
                roomCode, sid, sp);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId = sid }, sid);
            _roomManager.ClearArrivalSet(roomCode, sid);
            lock (room) { room.BroadcastedSyncIds.Add(sid); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
        }

        // 保留：caller 是异常玩家、刚汇合到 syncProgress 的"恢复"清理（与现状一致）
        // 仅在当前 syncId 触发广播时执行（与改动前条件一致）
        if (willBroadcastHere)
        {
            // 汇合后清理异常状态：所有 TargetProgress ≤ syncProgress 的异常玩家恢复正常
            lock (room)
            {
                foreach (var p in room.Players)
                {
                    if (p.IsAbnormal && p.TargetProgress > 0 && syncProgress >= 0 && p.TargetProgress <= syncProgress)
                    {
                        _logger.LogInformation("[WaitForAllPlayers] 异常玩家 {Uid} 已汇合，清除异常状态", p.PlayerUid);
                        p.IsAbnormal = false;
                        p.TargetProgress = -1;
                    }
                }
            }

            // route_sync_done 完成后重置所有玩家的异常状态（防止跨 JSON 残留）
            if (syncId == "route_sync_done")
            {
                lock (room)
                {
                    foreach (var p in room.Players)
                    {
                        p.IsAbnormal = false;
                        p.TargetProgress = -1;
                    }
                }
                _logger.LogDebug("[WaitForAllPlayers] route_sync_done 完成，已重置所有玩家异常状态");
            }
        }
        // 非阻塞：不满足条件时直接返回，客户端通过 AllArrived 事件等待

        // === hoeing-multiplayer-lagging-member-catchup（改动 8）：刷新 CurrentProgress 后广播玩家列表 ===
        // 使客户端 CurrentPlayerList 缓存的段级 CurrentProgress 随同步点推进刷新（落后追赶判定数据源，避免 BUG-C）。
        // lock 外 await，复用已有 PlayerListUpdated 事件，无新增协议；旧客户端忽略多余字段/推送。
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerListUpdated", new { players = room.Players }, room.Players);

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>上报战斗完成，全员完成时广播 AllFightDone</summary>
    public async Task ReportFightDoneAsync(GatewayHandlerContext ctx, string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "fight.reportDone");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allDone = _roomManager.RecordFightDone(roomCode, syncPointId, ctx.ConnectionId);

        if (allDone)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 全员战斗完成", roomCode, syncPointId);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllFightDone", new { syncPointId }, syncPointId);
        }
    }

    /// <summary>上报战斗参与者（multiplayer-shared-fight-end-quorum-sync spec，配额分母）</summary>
    public Task ReportFightParticipantAsync(GatewayHandlerContext ctx, string syncKey)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        _roomManager.RecordFightParticipant(roomCode, syncKey, ctx.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>心跳，更新 LastHeartbeat</summary>
    public Task HeartbeatAsync(GatewayHandlerContext ctx)
    {
        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>带路线进度信息的心跳（需求 6）</summary>
    public Task HeartbeatWithProgressAsync(GatewayHandlerContext ctx, int routeIndex, DateTime routeStartTime, double routeEstimatedSeconds)
    {
        _roomManager.UpdateHeartbeatWithProgress(ctx.ConnectionId, routeIndex, routeStartTime, routeEstimatedSeconds);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在 lock(room) 内枚举 room.ArrivalSets，收集所有满足 ShouldBroadcastAllArrived=true 的 (syncId, syncProgress) 对。
    /// syncProgress 推断规则（与 MemberStatusChanged 现有推断保持一致）：
    ///   syncId == "route_sync_done" → -1（全局同步点，按"等所有"处理）
    ///   其他 → 优先取 ArrivalSetProgress 存储的真实全局进度；未收录时回退为
    ///          已到达玩家中 CurrentProgress 的最大值（>=-1）
    /// 调用方负责在 lock 外执行 SendAsync + ClearArrivalSet（避免 await 持锁）。
    /// </summary>
    internal List<(string syncId, long progress)> CollectSatisfiedSyncsLocked(Room room)
    {
        var result = new List<(string, long)>();
        foreach (var kvp in room.ArrivalSets)
        {
            var sid = kvp.Key;
            var arrivals = kvp.Value;
            long sp = -1;
            if (sid != "route_sync_done")
            {
                // collective-stuck-orphan-arrivalset fix：优先用存储的真实进度
                if (!room.ArrivalSetProgress.TryGetValue(sid, out sp))
                {
                    sp = room.Players
                        .Where(p => arrivals.Contains(p.ConnectionId))
                        .Select(p => p.CurrentProgress)
                        .DefaultIfEmpty(-1)
                        .Max();
                }
            }
            if (ShouldBroadcastAllArrived(room, sid, arrivals, sp))
            {
                result.Add((sid, sp));
            }
        }
        return result;
    }

    /// <summary>
    /// 判定同步点 X 是否应该广播 AllArrived。
    /// 必须在 lock(room) 内调用。
    ///
    /// 规则：
    ///   X.Progress = syncProgress（当前同步点的全局进度值）
    ///   对每个异常玩家 P：
    ///     P.TargetProgress > syncProgress → P 会到 X → 计入等待
    ///     P.TargetProgress ≤ syncProgress → P 跳过了 X → 不计入
    ///   所有计入的玩家都到达 → 放行
    /// </summary>
    internal bool ShouldBroadcastAllArrived(Room room, string syncId, HashSet<string> arrivals, long syncProgress)
    {
        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();

        _logger.LogInformation("[ShouldBroadcast] syncId={SyncId}, syncProgress={SP}, 在线玩家数={Online}, ArrivalSet={Arrivals}",
            syncId, syncProgress, onlinePlayers.Count, string.Join(",", arrivals));

        if (onlinePlayers.Count == 0)
        {
            _logger.LogInformation("[ShouldBroadcast] 无在线玩家，不广播");
            return false;
        }

        foreach (var p in onlinePlayers)
        {
            _logger.LogInformation("[ShouldBroadcast]   玩家={Uid}, ConnId={CID}, IsAbnormal={Abn}, Target={T}, Current={C}, Arrived={Arr}",
                p.PlayerUid, p.ConnectionId, p.IsAbnormal, p.TargetProgress, p.CurrentProgress, arrivals.Contains(p.ConnectionId));
        }

        // 落后者豁免诊断（falling-behind-fix / 方案 A）：
        // 若 caller 的 syncProgress 严格小于所有其他在线正常玩家的 CurrentProgress，
        // 则它是孤立落后者——这些已走过此点的玩家会被下方现状豁免 CurrentProgress>syncProgress 天然剔除，
        // requiredPlayers 仅剩已到达者 → 立即放行。此处仅记录诊断谓词，不改变 requiredPlayers 集合（零行为变更）。
        // 真正解锁孤立落后者死等的是 WaitForAllPlayers 的方案 B 补发分支。
        bool releaseLaggingCaller = LaggingMemberReleaseDecisions.ShouldReleaseLaggingCaller(
            syncProgress,
            onlinePlayers.Where(p => !p.IsAbnormal && p.CurrentProgress > syncProgress)
                         .Select(p => p.CurrentProgress).ToList());

        // 计算需要等待的玩家
        // 异常玩家分支：保持原逻辑（syncProgress<0 必须等；TargetProgress==syncProgress 必须等；否则豁免）
        // 正常玩家分支（multiplayer-sync-skip-by-progress §2.1 / §2.2）：
        //   syncProgress<0       → 必须等（兼容旧客户端 / route_sync_done）
        //   CurrentProgress > SP → 已穿过此同步点，豁免不阻塞广播
        //   否则                 → 在此同步点或更早，必须等
        var requiredPlayers = onlinePlayers.Where(p =>
        {
            if (p.IsAbnormal)
            {
                if (syncProgress < 0) return true;
                return p.TargetProgress == syncProgress;
            }
            if (syncProgress < 0) return true;
            if (p.CurrentProgress > syncProgress) return false;
            return true;
        }).ToList();

        _logger.LogInformation("[ShouldBroadcast] 需要等待的玩家: {List}",
            string.Join(",", requiredPlayers.Select(p => $"{p.PlayerUid}(Abn={p.IsAbnormal},T={p.TargetProgress},C={p.CurrentProgress})")));
        _logger.LogInformation("[ShouldBroadcast] releaseLaggingCaller={Release}（孤立落后者豁免谓词）", releaseLaggingCaller);

        if (requiredPlayers.Count == 0)
        {
            _logger.LogInformation("[ShouldBroadcast] 无需要等待的玩家，全部放行");
            return true;
        }

        var allArrived = requiredPlayers.All(p => arrivals.Contains(p.ConnectionId));
        _logger.LogInformation("[ShouldBroadcast] 全部到达？{Result}", allArrived);
        return allArrived;
    }

    /// <summary>
    /// 静态版本（兼容旧调用），无日志
    /// </summary>
    private static bool ShouldBroadcastAllArrived(Room room, HashSet<string> arrivals, long syncProgress)
    {
        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();

        if (onlinePlayers.Count == 0) return false;

        // 与实例 overload 对称（multiplayer-sync-skip-by-progress §2.1 / §2.2）
        var requiredPlayers = onlinePlayers.Where(p =>
        {
            if (p.IsAbnormal)
            {
                if (syncProgress < 0) return true;
                return p.TargetProgress == syncProgress;
            }
            if (syncProgress < 0) return true;
            if (p.CurrentProgress > syncProgress) return false;
            return true;
        }).ToList();

        if (requiredPlayers.Count == 0) return true;

        return requiredPlayers.All(p => arrivals.Contains(p.ConnectionId));
    }

    /// <summary>
    /// 静态版本的 AllOnlineMembersReported，用于 OnDisconnectedAsync 中的重新评估。
    /// 必须在 lock(room) 内调用。排除宽限期中的成员（断线的人不应阻塞同步点）。
    /// </summary>
    private static bool AllOnlineMembersReportedStatic(Room room, HashSet<string> reported)
    {
        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .Where(p => !room.GracePendingMembers.ContainsKey(p.ConnectionId))
            .ToList();

        if (onlinePlayers.Count == 0) return false;

        return onlinePlayers.All(p => reported.Contains(p.ConnectionId));
    }

    /// <summary>
    /// 检查所有正常（非异常）在线玩家是否都已到达。
    /// 用于异常玩家上报后的重新评估。必须在 lock(room) 内调用。
    /// </summary>
    private static bool AllNormalOnlineMembersReported(Room room, HashSet<string> reported)
    {
        var normalOnlinePlayers = room.Players
            .Where(p => !p.IsAbnormal)
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();

        if (normalOnlinePlayers.Count == 0) return false;

        return normalOnlinePlayers.All(p => reported.Contains(p.ConnectionId));
    }

    // =========================================================================
    // 集体卡死监测（multiplayer-mutual-wait-collective-skip spec）
    // OQ-1 B / OQ-2 C / OQ-3 A=0.5 / OQ-4 C / OQ-5 A / OQ-6 A / OQ-7 A / OQ-8 B+C
    //
    // 触发链路：
    //   piggyback 评估（在 5 处 Hub 入口末尾）→ 快照变化时刷新 + 重建 Timer
    //   → Timer 到期回调 EvaluateCollectiveStuckTimerCallbackAsync
    //   → lock 内双重检查 IsCollectiveStuckLocked → 决策 + 主动写 IsAbnormal
    //   → lock 外按顺序广播 AllArrived 们 + RequestSkipToProgress
    // =========================================================================

    /// <summary>
    /// 必须在 lock(room) 内调用：判定房间是否处于"集体卡死"状态。
    /// C1 阈值：totalWaiters ≥ ⌈online * MutualWaitMinWaitersRatio⌉
    /// C2 互锁：所有 ArrivalSet 都不满足 ShouldBroadcastAllArrived
    /// C3 稳定：(Now - ObservationStartTime) ≥ MutualWaitStableSeconds
    /// 详见 design.md §4.1 / Property 1。
    /// </summary>
    private bool IsCollectiveStuckLocked(Room room)
    {
        if (room.HostConfig?.EnableMutualWaitCollectiveSkip != true) return false;

        var ratio = Math.Clamp(room.HostConfig.MutualWaitMinWaitersRatio, 0.01, 1.0);
        var stableSeconds = Math.Max(5, room.HostConfig.MutualWaitStableSeconds);

        var onlinePlayers = room.Players
            .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
            .ToList();
        if (onlinePlayers.Count == 0) return false;

        int totalWaiters = room.ArrivalSets.Values.Sum(s => s.Count);
        int threshold = (int)Math.Ceiling(onlinePlayers.Count * ratio);
        if (totalWaiters < threshold) return false;

        // C2: 所有 ArrivalSet 当前都不满足放行
        if (room.ArrivalSets.Count == 0) return false;
        foreach (var kv in room.ArrivalSets)
        {
            var sid = kv.Key;
            long sp = -1;
            if (sid != "route_sync_done")
            {
                // collective-stuck-orphan-arrivalset fix：优先用存储的真实进度，
                // 孤儿集合（成员已走过此点）因此可被"CurrentProgress>sp 已穿过豁免"满足，不再误判卡死
                if (!room.ArrivalSetProgress.TryGetValue(sid, out sp))
                {
                    sp = onlinePlayers
                        .Where(p => kv.Value.Contains(p.ConnectionId))
                        .Select(p => p.CurrentProgress)
                        .DefaultIfEmpty(-1)
                        .Max();
                }
            }
            // 复用静态版本：无日志噪声、性能更优
            if (ShouldBroadcastAllArrived(room, kv.Value, sp)) return false;
        }

        // C3: 状态稳定 ≥ MutualWaitStableSeconds
        if (room.LastArrivalSetsSnapshot == null) return false;
        if ((DateTime.UtcNow - room.ObservationStartTime).TotalSeconds < stableSeconds) return false;

        return true;
    }

    /// <summary>
    /// 比较两个 ArrivalSets 快照内容是否相等（深比较）。
    /// </summary>
    private static bool ArrivalSnapshotEquals(
        Dictionary<string, HashSet<string>>? a,
        Dictionary<string, HashSet<string>> b)
    {
        if (a == null) return false;
        if (a.Count != b.Count) return false;
        foreach (var kv in b)
        {
            if (!a.TryGetValue(kv.Key, out var aSet)) return false;
            if (!aSet.SetEquals(kv.Value)) return false;
        }
        return true;
    }

    /// <summary>
    /// 在 Hub 方法末尾 piggyback 调用：评估房间是否进入"集体卡死症状"，
    /// 必要时刷新 LastArrivalSetsSnapshot / ObservationStartTime / CollectiveSkipTimer。
    /// 实际触发协同跳段的决策由 Timer 到期后调用 EvaluateCollectiveStuckTimerCallbackAsync 完成（OQ-2 C 双层判定）。
    /// 注意：本方法 await 任何调用必须在 lock 外（design §8.4 改动 2）。
    /// </summary>
    private Task EvaluateCollectiveStuckPiggybackAsync(Room room, string roomCode)
    {
        if (room.HostConfig?.EnableMutualWaitCollectiveSkip != true) return Task.CompletedTask;

        var stableSeconds = Math.Max(5, room.HostConfig.MutualWaitStableSeconds);

        lock (room)
        {
            // 计算当前快照（深拷贝，便于"内容相等"比较）
            var currentSnapshot = room.ArrivalSets.ToDictionary(
                kv => kv.Key,
                kv => new HashSet<string>(kv.Value)
            );

            bool snapshotChanged = !ArrivalSnapshotEquals(room.LastArrivalSetsSnapshot, currentSnapshot);
            if (snapshotChanged)
            {
                room.LastArrivalSetsSnapshot = currentSnapshot;
                room.ObservationStartTime = DateTime.UtcNow;
                room.CollectiveSkipTimer?.Dispose();

                // 空快照不需要 Timer
                if (currentSnapshot.Values.Sum(s => s.Count) == 0)
                {
                    room.CollectiveSkipTimer = null;
                }
                else
                {
                    room.CollectiveSkipTimer = new System.Threading.Timer(
                        _ => _ = EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode),
                        null,
                        TimeSpan.FromSeconds(stableSeconds),
                        Timeout.InfiniteTimeSpan
                    );
                }
            }
            // 不变 → Timer 继续走原计时（不重置）
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// CollectiveSkipTimer 到期后的入口（OQ-2 C）。
    /// 双重检查 IsCollectiveStuckLocked（OQ-8 C），命中后做 lock 内决策 + lock 外按顺序广播（OQ-7 A）。
    /// 仅在 lock 内读写 room 字段；广播一律在 lock 外 await（H-2 高风险点：死锁预防）。
    /// </summary>
    private async Task EvaluateCollectiveStuckTimerCallbackAsync(Room room, string roomCode)
    {
        long targetProgress;
        List<(string syncId, long progress)> satisfiedSyncs;
        List<string> laggingPlayerConnIds;
        bool degraded = false;

        try
        {
            lock (room)
            {
                if (room.HostConfig?.EnableMutualWaitCollectiveSkip != true) return;

                // 双重检查（OQ-8 C）：再次评估 IsCollectiveStuckLocked
                if (!IsCollectiveStuckLocked(room)) return;

                // 1) 计算 targetProgress（OQ-1 B：round 到下一条路线开头）
                var maxCurrent = room.Players
                    .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                    .Select(p => p.CurrentProgress)
                    .DefaultIfEmpty(-1)
                    .Max();
                if (maxCurrent < 0) return; // 没有任何玩家上报过进度，跳过本次触发
                targetProgress = (maxCurrent / 1_000_000L + 1L) * 1_000_000L;

                // 2) 收集落后玩家（CurrentProgress < target 且未在任何 ArrivalSet）
                var allArrivedConns = new HashSet<string>(
                    room.ArrivalSets.SelectMany(kv => kv.Value)
                );
                laggingPlayerConnIds = room.Players
                    .Where(p => DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                    .Where(p => p.CurrentProgress < targetProgress)
                    .Where(p => !allArrivedConns.Contains(p.ConnectionId))
                    .Select(p => p.ConnectionId)
                    .ToList();

                // 3) 主动写 IsAbnormal=true / TargetProgress=targetProgress
                foreach (var connId in laggingPlayerConnIds)
                {
                    var p = room.Players.FirstOrDefault(x => x.ConnectionId == connId);
                    if (p == null) continue;
                    p.IsAbnormal = true;
                    p.TargetProgress = targetProgress;
                    _logger.LogWarning("[CollectiveSkip] 服务端主动标记落后玩家：{Uid} → IsAbnormal=true, TargetProgress={T}",
                        p.PlayerUid, targetProgress);
                }

                // 4) 收集 satisfiedSyncs（既有 helper 复用）
                satisfiedSyncs = CollectSatisfiedSyncsLocked(room);

                // 5) 计数器递增 + 降级判断
                // collective-stuck-orphan-arrivalset fix：仅当本次判定确实产生了动作
                // （标记了落后玩家，或有可放行的同步点）才计入连续跳段。
                // 既无落后者也无可放行同步点的"空触发"只是症状快照残留（如孤儿 ArrivalSet），
                // 不代表真实跳段发生，若照常 +1 会把熔断计数喂满 → 误广播 CollectiveSkipDegraded 全员停止。
                // 真实死锁仍有客户端 60s 等待超时 + 连续超时上报路径兜底，不依赖此计数。
                if (laggingPlayerConnIds.Count > 0 || satisfiedSyncs.Count > 0)
                {
                    room.ConsecutiveCollectiveSkipCount += 1;
                }
                else
                {
                    _logger.LogInformation("[CollectiveSkip] 判定命中但无有效跳段动作（无落后者且无可放行同步点），不计入连续跳段计数，房间={RoomCode}", roomCode);
                }
                var maxConsec = Math.Max(1, room.HostConfig.MaxConsecutiveCollectiveSkips);
                if (room.ConsecutiveCollectiveSkipCount >= maxConsec)
                {
                    degraded = true;
                }

                // 重置监测快照（避免本次触发后立即再触发）
                room.LastArrivalSetsSnapshot = null;
                room.ObservationStartTime = default;
                room.CollectiveSkipTimer?.Dispose();
                room.CollectiveSkipTimer = null;
            }
        }
        catch (Exception ex)
        {
            // lock 内任何异常都直接吞，避免影响其他 Hub 调用；记录详细日志便于排查
            _logger.LogError(ex, "[CollectiveSkip] Timer 回调 lock 内决策失败，房间={RoomCode}", roomCode);
            return;
        }

        // === lock 外按顺序广播（OQ-7 A）===
        try
        {
            // ① 先 satisfiedSyncs 们：让大部队第一时间被解封
            foreach (var (sid, sp) in satisfiedSyncs)
            {
                _logger.LogInformation("[CollectiveSkip] 广播 AllArrived: 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}",
                    roomCode, sid, sp);
                await _broadcaster.BroadcastGroupAsync(roomCode, "AllArrived", new { syncPointId = sid }, sid);
                _roomManager.ClearArrivalSet(roomCode, sid);
                lock (room) { room.BroadcastedSyncIds.Add(sid); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
            }

            // ② 后 RequestSkipToProgress：让落后玩家神像跳段
            if (laggingPlayerConnIds.Count > 0)
            {
                _logger.LogWarning("[CollectiveSkip] 广播 RequestSkipToProgress: 房间={RoomCode}, target={Target}, 落后玩家数={N}",
                    roomCode, targetProgress, laggingPlayerConnIds.Count);
                await _broadcaster.BroadcastGroupAsync(roomCode, "RequestSkipToProgress", new { targetProgress }, targetProgress);
            }

            // ③ 降级广播（OQ-5 A）
            if (degraded)
            {
                _logger.LogError("[CollectiveSkip] 连续 {N} 次集体跳段，触发降级",
                    room.ConsecutiveCollectiveSkipCount);
                await _broadcaster.BroadcastGroupAsync(roomCode, "CollectiveSkipDegraded",
                    new { reason = "ConsecutiveCollectiveSkipExceeded" }, "ConsecutiveCollectiveSkipExceeded");
            }
        }
        catch (Exception ex)
        {
            // 广播失败不应让 Timer 回调崩溃；记录日志并放弃本次广播
            _logger.LogError(ex, "[CollectiveSkip] Timer 回调 lock 外广播失败，房间={RoomCode}", roomCode);
        }
    }
}
