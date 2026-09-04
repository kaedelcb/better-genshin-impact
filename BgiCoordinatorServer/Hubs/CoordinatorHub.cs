using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BgiCoordinatorServer.Hubs;

public class CoordinatorHub : Hub
{
    private readonly RoomManager _roomManager;
    private readonly ILogger<CoordinatorHub> _logger;
    private readonly IHubContext<CoordinatorHub> _hubContext;
    private readonly RoomOperations _ops;

    // 每个连接当前所属的 SignalR Group 列表（用于轮换房间时清理旧 Group 订阅，
    // 避免上一个房间关闭/广播时串扰到已切换到新房间的连接）。
    private static readonly ConcurrentDictionary<string, HashSet<string>> _connectionGroups = new();

    public CoordinatorHub(RoomManager roomManager, ILogger<CoordinatorHub> logger, IHubContext<CoordinatorHub> hubContext, RoomOperations ops)
    {
        _roomManager = roomManager;
        _logger = logger;
        _hubContext = hubContext;
        _ops = ops;
    }

    /// <summary>
    /// 把当前连接从所有旧 Group 中移除，确保后续广播不会串扰到这个连接。
    /// 多世界轮次切换时，玩家会从旧房间切到新房间，必须先离开旧 Group。
    /// </summary>
    private async Task LeaveAllGroupsAsync(string? excludeGroup = null)
    {
        if (!_connectionGroups.TryGetValue(Context.ConnectionId, out var groups))
            return;
        // 拷贝避免迭代时被并发修改
        string[] toRemove;
        lock (groups)
        {
            toRemove = groups.Where(g => g != excludeGroup).ToArray();
        }
        foreach (var g in toRemove)
        {
            try
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, g);
                lock (groups) { groups.Remove(g); }
                _logger.LogInformation("[GroupCleanup] 连接 {ConnId} 从旧 Group {Group} 移除",
                    Context.ConnectionId, g);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GroupCleanup] 连接 {ConnId} 离开 Group {Group} 失败（忽略）",
                    Context.ConnectionId, g);
            }
        }
    }

    /// <summary>记录某连接已加入指定 Group，供 LeaveAllGroupsAsync 后续清理使用。</summary>
    private void TrackGroup(string groupName)
    {
        var set = _connectionGroups.GetOrAdd(Context.ConnectionId, _ => new HashSet<string>());
        lock (set) { set.Add(groupName); }
    }

    /// <summary>记录某连接已离开指定 Group。</summary>
    private void UntrackGroup(string groupName)
    {
        if (_connectionGroups.TryGetValue(Context.ConnectionId, out var set))
        {
            lock (set) { set.Remove(groupName); }
        }
    }

    /// <summary>创建房间，返回房间码</summary>
    public async Task<string> CreateRoom(string playerName = "", List<string>? whitelist = null, string playerUid = "", int expectedPlayerCount = 4, string reportedVersion = "")
    {
        var (code, _) = await _ops.CreateRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            playerName, whitelist, playerUid, expectedPlayerCount, reportedVersion);
        return code;
    }

    /// <summary>加入房间，广播 PlayerListUpdated</summary>
    public async Task<bool> JoinRoom(string roomCode, string playerName = "", string playerUid = "", string reportedVersion = "")
    {
        var (success, _, _) = await _ops.JoinRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, playerName, playerUid, reportedVersion);
        return success;
    }

    /// <summary>离开房间，广播 PlayerListUpdated</summary>
    public Task LeaveRoom()
        => _ops.LeaveRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报路线清单，所有成员上报后对比 MD5，广播差异或验证通过</summary>
    public Task ReportRouteList(List<RouteHash> routes)
        => _ops.ReportRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routes);

    /// <summary>
    /// 上报本玩家计划要执行的所有路线的变体 schema（route-variant-sync-by-logical-id spec / R6）。
    /// 服务端按 LogicalRouteId 分组比对所有玩家的 SyncPointList + TeleportSyncPointSequence。
    /// 全部一致 → 广播 RouteVariantConsistencyPassed；任一不一致 / 30s 超时 → 广播 RouteVariantConsistencyFailed。
    /// 全员 LogicalRouteId 均为空 → 跳过校验、不广播（老路径零回归 R6.7）。
    /// </summary>
    public Task ReportRouteVariantSchema(List<RouteVariantSchemaItem> items)
        => _ops.ReportRouteVariantSchemaAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), items);

    /// <summary>上报到达集合点，全员到达时广播 AllArrived</summary>
    public Task ReportArrival(string syncPointId)
        => _ops.ReportArrivalAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId);

    /// <summary>
    /// 上报到达集合点（带预期人数），指定人数到达时广播 AllArrived
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    public Task ReportArrivalWithExpectedCount(string syncPointId, int expectedCount)
        => _ops.ReportArrivalWithExpectedCountAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId, expectedCount);

    /// <summary>上报战斗完成，全员完成时广播 AllFightDone</summary>
    public Task ReportFightDone(string syncPointId)
        => _ops.ReportFightDoneAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId);

    /// <summary>上报战斗参与者（multiplayer-shared-fight-end-quorum-sync spec，配额分母）</summary>
    public Task ReportFightParticipant(string syncKey)
        => _ops.ReportFightParticipantAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncKey);

    /// <summary>心跳，更新 LastHeartbeat</summary>
    public Task Heartbeat()
        => _ops.HeartbeatAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>带路线进度信息的心跳（需求 6）</summary>
    public Task HeartbeatWithProgress(int routeIndex, DateTime routeStartTime, double routeEstimatedSeconds)
        => _ops.HeartbeatWithProgressAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routeIndex, routeStartTime, routeEstimatedSeconds);



    /// <summary>查询指定成员的路线进度（需求 6）</summary>
    public Task<MemberProgress?> GetMemberProgress(string playerUid)
        => _ops.GetMemberProgressAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid);



    /// <summary>关闭房间（仅房主可操作）</summary>
    public Task CloseRoom()
        => _ops.CloseRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// （已废弃，保留空实现）旧客户端调用此方法时仅记 deprecated 警告，不影响协议兼容。
    /// kazuha-player-auto-detection: 替换为运行时声明协议 DeclareKazuhaCapability，由各客户端各自识别本地联机队伍是否含万叶并主动声明。
    /// </summary>
    public Task SetKazuhaPlayer(int index = 0)
    {
        _logger.LogWarning("[SetKazuhaPlayer] 调用方使用了已废弃的 Hub 方法（kazuha-player-auto-detection 已替换为 DeclareKazuhaCapability），index={Index}", index);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 客户端声明本地联机队伍含万叶（kazuha-player-auto-detection）。
    /// 幂等：同一 ConnectionId 重复调用直接 return（lock 内做 Any 检查）。
    /// 选举：第一个声明者自动成为 KazuhaConnectionId，触发 KazuhaPlayerUpdated(playerUid) 广播。
    /// 后续声明者仅入候选列表，断线时按列表顺序顶替。
    /// </summary>
    public async Task DeclareKazuhaCapability()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        bool shouldBroadcast = false;
        string broadcastUid = "";
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null) return;

            // 幂等检查：同一 ConnectionId 重复声明直接 return
            if (room.KazuhaCandidates.Any(c => c.ConnectionId == Context.ConnectionId))
            {
                _logger.LogDebug("[DeclareKazuhaCapability] 重复声明，忽略 connId={ConnId}", Context.ConnectionId);
                return;
            }

            room.KazuhaCandidates.Add(new KazuhaCandidate
            {
                ConnectionId = Context.ConnectionId,
                PlayerUid = player.PlayerUid
            });

            // 第一个声明者自动成为当前 Kazuha
            if (room.KazuhaCollect.KazuhaConnectionId == null)
            {
                room.KazuhaCollect.KazuhaConnectionId = Context.ConnectionId;
                broadcastUid = player.PlayerUid;
                shouldBroadcast = true;
            }
        }

        if (shouldBroadcast)
        {
            _logger.LogInformation("[DeclareKazuhaCapability] 房间 {Code} 选出第一位 Kazuha: {Uid}",
                roomCode, broadcastUid);
            await Clients.Group(roomCode).SendAsync("KazuhaPlayerUpdated", broadcastUid);
        }
    }

    // ====== 万叶聚物同步（multiplayer-kazuha-collect-sync）======

    /// <summary>
    /// 万叶玩家广播"开始执行聚物动作"。仅记录 + 广播，不做终态守卫。
    /// multiplayer-kazuha-collect-point-broadcast: 增加 syncKey + 聚物点 (collectX, collectY) 三参。
    /// hoeing-kazuha-collect-drop-terminal-signal: 不再写 room.KazuhaCollect.CurrentCollectPoint（字段已删）；
    /// 广播始终携带 4 参，无效坐标用 NaN 透传，由客户端 IsValid 守卫过滤。
    /// 注意：SignalR 不支持 hub 方法重载，老客户端调 0-参 InvokeAsync 会因 routing 失败
    /// 抛 HubException → 客户端 try/catch 静默 → 走退化路径（不上报聚物点）。
    /// 部署顺序：先服务端、后客户端，最大化平滑过渡。
    /// </summary>
    public async Task NotifyKazuhaCollectStarted(string syncKey, double collectX, double collectY)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        // 鉴权：必须是当前周期的万叶玩家
        lock (room)
        {
            if (room.KazuhaCollect.KazuhaConnectionId != Context.ConnectionId)
            {
                _logger.LogWarning("[KazuhaCollect] NotifyKazuhaCollectStarted 鉴权失败：调用方 {ConnId} 不是万叶 {KazuhaId}",
                    Context.ConnectionId, room.KazuhaCollect.KazuhaConnectionId);
                return;
            }
        }

        // IsValid: NaN / Inf / (0, 0) 全部判无效（与 KazuhaCollectPointDecisions.IsValid 同语义）
        bool collectPointValid = !double.IsNaN(collectX) && !double.IsNaN(collectY)
                              && !double.IsInfinity(collectX) && !double.IsInfinity(collectY)
                              && !(collectX == 0.0 && collectY == 0.0);

        var playerUid = "";
        lock (room)
        {
            playerUid = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId)?.PlayerUid ?? "";
        }

        // hoeing-kazuha-collect-drop-terminal-signal: 删 CurrentSyncKey fallback 与 CurrentCollectPoint 写入
        // （这两个字段随终态状态机一并删除）。syncKey 由万叶客户端直接传入，恒非空（design.md Property 2 守住）。
        _logger.LogInformation(
            "[KazuhaCollect] 房间 {Code} 万叶 {Uid} 开始聚物 syncKey={Key} collectPoint=({X},{Y}) valid={Valid}",
            roomCode, playerUid, syncKey, collectX, collectY, collectPointValid);

        // 始终广播 4-参（无效坐标用 NaN 透传给客户端，客户端 IsValid 守卫会过滤）
        await Clients.Group(roomCode).SendAsync(
            "KazuhaCollectStarted", playerUid, syncKey ?? "", collectX, collectY);
    }

    /// <summary>上报路线验证完成，全员完成时广播 RouteVerificationAllDone</summary>
    public Task ReportRouteVerificationDone()
        => _ops.ReportRouteVerificationDoneAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报本机达经验上限，全员达上限时广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop</summary>
    public async Task ReportExpCapReached()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allReached = _roomManager.RecordExpCapReached(roomCode, Context.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 全员达经验上限，广播终止", roomCode);
            await Clients.Group(roomCode).SendAsync("AllReachedExpCap");
        }
    }

    /// <summary>撤回本机达经验上限（又见经验）。multiplayer-hoeing-exp-cap-stop</summary>
    public Task ReportExpCapCleared()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        _roomManager.RecordExpCapCleared(roomCode, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>成员上报团队 arming（本机吃到经验，或连续 5 场无经验兜底）。置 ExpCapArmed=true；
    /// 若 arming 后已满足全员上报（全员满级兜底场景）则补广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop R7</summary>
    public async Task ReportExpArmed()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allReached = _roomManager.RecordExpArmed(roomCode, Context.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 团队 arming 后全员达经验上限，广播终止", roomCode);
            await Clients.Group(roomCode).SendAsync("AllReachedExpCap");
        }
    }

    /// <summary>
    /// 上报"连续2场无经验预警"（exp-cap-prefinal-stop-by-two-noexp）。
    /// 客户端连续 2 场无经验时调用，服务端将 connectionId 加入 TwoConsecutiveNoExpSet。
    /// 若 arming ∧ 全员 ∈ (ExpCapReachedSet ∪ TwoConsecutiveNoExpSet) → 广播 AllReachedExpCap。
    /// 旧服务端无此方法 → 客户端 HubException 被静默吞掉 → 退化为 4-threshold 行为。
    /// </summary>
    public async Task ReportTwoConsecutiveNoExp()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allReached = _roomManager.RecordTwoConsecutiveNoExp(roomCode, Context.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 连续2场无经验预警触发全员覆盖，广播终止", roomCode);
            await Clients.Group(roomCode).SendAsync("AllReachedExpCap");
        }
    }

    /// <summary>
    /// 撤回"连续2场无经验预警"（又见经验）。exp-cap-prefinal-stop-by-two-noexp。
    /// </summary>
    public Task ReportTwoConsecutiveNoExpCleared()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        _roomManager.RecordTwoConsecutiveNoExpCleared(roomCode, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>更新白名单（仅房主）</summary>
    public Task UpdateWhitelist(List<string>? whitelist = null)
        => _ops.UpdateWhitelistAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), whitelist);

    /// <summary>获取在线房间列表</summary>
    public Task<List<RoomSummary>> GetOnlineRooms()
        => _ops.GetOnlineRoomsAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上传锄地配置</summary>
    public Task SetRoomConfig(RoomConfig config)
        => _ops.SetRoomConfigAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), config);

    /// <summary>成员拉取房主锄地配置</summary>
    public Task<RoomConfig?> GetRoomConfig()
        => _ops.GetRoomConfigAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上报已进入等待状态</summary>
    public Task ReportHostReady()
        => _ops.ReportHostReadyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>查询房主是否就绪</summary>
    public Task<bool> IsHostReady()
        => _ops.IsHostReadyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 房主调用此方法把房间标记为已开锄（spec lock-room-after-start §2）。
    /// 服务端从此 JoinRoom 拒绝非重连新玩家、GetOnlineRooms 也不再返回此房间。
    /// 鉴权：Context.ConnectionId 必须等于 room.HostConnectionId。
    /// 幂等：重复调用直接 return（room.IsStarted 一旦 true 在房间销毁前不复位）。
    /// 非房主调用：LogWarning + return，不抛异常、不修改状态。
    /// </summary>
    public Task MarkRoomStarted()
        => _ops.MarkRoomStartedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), null);

    /// <summary>
    /// 房主重开续跑：上报已完成房主 UID 集合，服务端据此裁剪权威轮换序列。
    /// 旧服务端无此方法 → 客户端 HubException 降级 → 等价 MarkRoomStarted()（全量序列）。
    /// hoeing-multiworld-host-restart-resume-round Req 1.1 / 6.1。
    /// </summary>
    public Task MarkRoomStartedWithProgress(List<string> completedHostUids)
        => _ops.MarkRoomStartedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), completedHostUids);

    /// <summary>返回本房间权威轮换序列（UID 列表）。未生成 / 房间不存在 → 空列表。</summary>
    public Task<List<string>> GetRoundHostOrder()
        => _ops.GetRoundHostOrderAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上传最终路线列表，并广播通知成员</summary>
    public Task SetHostRouteList(List<string> routeNames)
        => _ops.SetHostRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routeNames);

    /// <summary>成员拉取房主路线列表</summary>
    public Task<List<string>> GetHostRouteList()
        => _ops.GetHostRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 查询房主是否已上传过路线列表（含上传空列表）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：成员据此区分
    /// "房主从未上传"（false → 继续等待）与"房主上传了空列表"（true + 列表空 → 优雅跳过本轮）。
    /// </summary>
    public Task<bool> IsHostRouteListUploaded()
        => _ops.IsHostRouteListUploadedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 原子返回房主路线列表状态：(Uploaded, RouteNames) 同一时刻快照。
    /// multiplayer-member-skip-round-stuck-roundend-sync-fix：取代成员侧
    /// GetHostRouteList + IsHostRouteListUploaded 两次独立查询，消除 TOCTOU 竞态
    /// （房主在两次查询之间 SetHostRouteList(非空) 导致成员拿到 uploaded=true+count=0 误判跳过）。
    /// lock(room) 与 SetHostRouteList 写侧互斥，并复制列表快照，确保读到的 Uploaded 与 RouteNames
    /// 来自同一时刻、且返回后不被房主并发改动。
    /// </summary>
    public Task<HostRouteListStatus> GetHostRouteListStatus()
        => _ops.GetHostRouteListStatusAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报已加入世界，全员加入时广播 AllWorldJoined</summary>
    public async Task ReportWorldJoined()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        var allJoined = _roomManager.RecordWorldJoined(roomCode, Context.ConnectionId);
        _logger.LogInformation("连接 {ConnId} 上报已加入世界，房间 {Code}，全员: {All}",
            Context.ConnectionId, roomCode, allJoined);

        if (allJoined)
        {
            await Clients.Group(roomCode).SendAsync("AllWorldJoined");
        }
    }

    /// <summary>获取已加入世界的人数</summary>
    public Task<int> GetWorldJoinedCount()
        => _ops.GetWorldJoinedCountAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>重置已加入世界的记录（多世界模式新轮次开始时调用）</summary>
    public Task ResetWorldJoined()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room != null && roomCode != null && room.HostConnectionId == Context.ConnectionId)
        {
            _roomManager.ResetWorldJoinedSet(roomCode);
            _logger.LogInformation("[ResetWorldJoined] 房间 {Code} WorldJoinedSet 已重置", roomCode);
        }
        return Task.CompletedTask;
    }



    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // 获取断线玩家所在的房间信息
        var (disconnectedRoom, disconnectedRoomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        var wasHost = disconnectedRoom?.HostConnectionId == Context.ConnectionId;

        if (disconnectedRoom != null && disconnectedRoomCode != null)
        {
            if (wasHost)
            {
                // === 房主断线：保持现有逻辑（广播 RoomClosed + 删房）===
                _logger.LogWarning("[OnDisconnectedAsync] 房主断线，广播 RoomClosed: 房间={RoomCode}", disconnectedRoomCode);
                await Clients.Group(disconnectedRoomCode).SendAsync("RoomClosed", "房主已断开连接");
                _roomManager.LeaveRoom(Context.ConnectionId);
                _roomManager.DeleteRoom(disconnectedRoomCode);
            }
            else
            {
                // === 成员断线：进宽限期，不删人、不广播 PlayerListUpdated 缩水 ===
                lock (disconnectedRoom)
                {
                    disconnectedRoom.GracePendingMembers[Context.ConnectionId] = DateTime.UtcNow.AddSeconds(30);
                }
                _logger.LogInformation("[OnDisconnectedAsync] 成员 {ConnId} 进入宽限期(30s)，房间 {Code} 人数保持 {N}",
                    Context.ConnectionId, disconnectedRoomCode, disconnectedRoom.Players.Count);

                // SignalR 会自动从 Group 移除断线连接，room.Players 不删

                // 重新评估所有未完成的同步点（断线的人不应阻塞同步点）
                List<string> satisfiedSyncIds;
                lock (disconnectedRoom)
                {
                    satisfiedSyncIds = disconnectedRoom.ArrivalSets
                        .Where(kvp => RoomOperations.AllOnlineMembersReportedStatic(disconnectedRoom, kvp.Value))
                        .Select(kvp => kvp.Key)
                        .ToList();
                }

                // 广播满足条件的同步点（在 lock 外执行 await）
                foreach (var syncId in satisfiedSyncIds)
                {
                    _logger.LogInformation("[OnDisconnectedAsync] 玩家断线后重新评估：同步点 {SyncId} 条件满足，广播 AllArrived，房间={RoomCode}",
                        syncId, disconnectedRoomCode);
                    await Clients.Group(disconnectedRoomCode).SendAsync("AllArrived", syncId);
                    _roomManager.ClearArrivalSet(disconnectedRoomCode, syncId);
                    lock (disconnectedRoom) { disconnectedRoom.BroadcastedSyncIds.Add(syncId); }
                }

                // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 5）===
                await _ops.EvaluateCollectiveStuckPiggybackAsync(disconnectedRoom, disconnectedRoomCode);

                // 万叶聚物同步：候选切换 + 兜底（kazuha-player-auto-detection requirements 5.5 / Property 10）
                bool shouldBroadcastSwitch = false;
                string switchedToUid = "";
                lock (disconnectedRoom)
                {
                    disconnectedRoom.KazuhaCandidates.RemoveAll(c => c.ConnectionId == Context.ConnectionId);

                    if (disconnectedRoom.KazuhaCollect.KazuhaConnectionId == Context.ConnectionId)
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
                    await Clients.Group(disconnectedRoomCode).SendAsync("KazuhaPlayerUpdated", switchedToUid);
                }
            }
        }

        _logger.LogInformation("连接 {ConnId} 断开，房间={Room}",
            Context.ConnectionId, disconnectedRoomCode ?? "(无)");

        // 清理控制房间成员（必须在移除 _connectionGroups 跟踪之前执行，否则找不到所属 Group）
        try
        {
            // 从 `_connectionGroups` 中找出当前连接所属的所有 Group
            if (_connectionGroups.TryGetValue(Context.ConnectionId, out var groups))
            {
                List<string> groupList;
                lock (groups) { groupList = [.. groups]; }

                foreach (var group in groupList)
                {
                    if (group.StartsWith("CTRL_"))
                    {
                        _roomManager.RemoveFromControlRoom(group, Context.ConnectionId);
                        // 遥控端不入 _controlRooms，RemoveFromControlRoom 对其 no-op；
                        // 需单独清理遥控端连接登记，防止 _remoteControlConnections 残留。
                        _roomManager.RemoveRemoteConnection(group, Context.ConnectionId);
                        var players = _roomManager.GetControlRoomPlayers(group);
                        _ = Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", players);
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
        _connectionGroups.TryRemove(Context.ConnectionId, out _);

        // 日志订阅清理（房间实时日志汇聚）：断线连接从所有订阅中移除，并通知各目标成员最新订阅数
        try
        {
            foreach (var (group, targetUid, count) in _roomManager.RemoveLogSubscriberEverywhere(Context.ConnectionId))
                await NotifyLogSubscriberCount(group, targetUid, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理日志订阅时发生异常");
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 等待点上报（multiplayer-abnormal-wait-coordination 重构）
    /// 玩家跳过线路并在同步点等待时调用
    /// 服务端验证等待点格式、计算统一等待点、广播给所有正常玩家
    /// </summary>
    /// <param name="routeId">路线ID</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="worldRound">世界轮次</param>
    public async Task WaitPointReport(string routeId, string syncPointId, int worldRound)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[WaitPointReport] 连接 {ConnId} 未在任何房间中，忽略等待点上报", Context.ConnectionId);
            return;
        }

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[WaitPointReport] 连接 {ConnId} 不在房间玩家列表中", Context.ConnectionId);
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
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
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
        await Clients.Group(roomCode).SendAsync("UnifiedWaitPoint", 
            finalUnifiedWaitPoint, allAbnormalPlayerUids, expectedWaitCount, finalRouteId);
        
        _logger.LogInformation("[WaitPointReport] 已广播 UnifiedWaitPoint: 房间={RoomCode}, 等待点={WaitPoint}, 异常玩家=[{Players}], 预期人数={Expected}",
            roomCode, finalUnifiedWaitPoint, string.Join(", ", allAbnormalPlayerUids), expectedWaitCount);
    }

    /// <summary>
    /// 多轮世界重置（multiplayer-abnormal-wait-coordination 重构）
    /// 多轮世界新轮次开始时调用，清理所有等待点状态和异常状态
    /// </summary>
    public Task ResetForNewWorldRound(int newRound)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null) return Task.CompletedTask;
        
        lock (room)
        {
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
            room.LastArrivalSetsSnapshot = null;

            // === 房主路线列表上传标志重置（multiplayer-host-empty-route-member-wait-timeout-fix）===
            // 新一轮房主重新筛选并上传路线列表，避免沿用上一轮的"已上传"状态导致成员误判
            room.HostRouteList = [];
            room.HostRouteListUploaded = false;
            room.ObservationStartTime = default;
            room.CollectiveSkipTimer?.Dispose();
            room.CollectiveSkipTimer = null;

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

            _logger.LogInformation("[ResetForNewWorldRound] 房间{RoomCode}进入第{Round}轮，等待点、异常状态、万叶候选已重置", roomCode, newRound);
        }
        
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

    /// <summary>
    /// 到达等待点上报（multiplayer-abnormal-wait-coordination 需求 5）
    /// 正常玩家到达统一等待点时调用，服务端记录到达状态并在全员到达时广播
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    public async Task ReportArrivalAtWaitPoint(string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ReportArrivalAtWaitPoint] 连接 {ConnId} 未在任何房间中，忽略到达上报", Context.ConnectionId);
            return;
        }

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ReportArrivalAtWaitPoint] 连接 {ConnId} 不在房间玩家列表中", Context.ConnectionId);
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
            await Clients.Group(roomCode).SendAsync("AllPlayersArrived", syncPointId);
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
    public async Task ClearAbnormalStatus()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ClearAbnormalStatus] 连接 {ConnId} 未在任何房间中，忽略状态清除", Context.ConnectionId);
            return;
        }

        string playerUid;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ClearAbnormalStatus] 连接 {ConnId} 不在房间玩家列表中", Context.ConnectionId);
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
        await Clients.Group(roomCode).SendAsync("AbnormalPlayerRecovered", playerUid);
        _logger.LogInformation("[ClearAbnormalStatus] 已广播 AbnormalPlayerRecovered: 房间={RoomCode}, 玩家={PlayerUid}",
            roomCode, playerUid);
    }

    /// <summary>
    /// 接收玩家异常通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-3.2, REQ-3.3
    /// </summary>
    public async Task PlayerAnomalyNotify(string playerUid, int routeIndex, bool passedSyncPoint)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
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
        await Clients.Group(roomCode).SendAsync("PlayerAnomalyNotify", playerUid, routeIndex, passedSyncPoint);
    }

    /// <summary>
    /// <summary>
    /// 接收"复苏者附带战斗点"的异常通知并广播（hoeing-route-retry-round-end-refactor v3）。
    /// 纯透传：不解析 fightPointId、不进 AbnormalPlayerInfos（区别于既有 PlayerAnomalyNotify）。
    /// 供客户端做"只跳过复苏那一个战斗点"（requirements.md §9 EB-v3-1 / design.md §9.1）。
    /// </summary>
    public async Task PlayerAnomalyNotifyFightPoint(string playerUid, int routeIndex, int fightPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        _logger.LogInformation(
            "[PlayerAnomalyNotifyFightPoint] 房间={RoomCode}, 玩家={PlayerUid}, 线路={RouteIndex}, 战斗点={FightPointId}",
            roomCode, playerUid, routeIndex, fightPointId);

        // 纯透传广播（发送方也会收到，客户端会过滤自己）
        await Clients.Group(roomCode).SendAsync("PlayerAnomalyNotifyFightPoint", playerUid, routeIndex, fightPointId);
    }

    /// 接收玩家异常恢复通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-2.1, REQ-2.2, REQ-3.4
    /// </summary>
    public async Task PlayerAnomalyRecovered(string playerUid)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        _logger.LogInformation("[PlayerAnomalyRecovered] 房间={RoomCode}, 玩家={PlayerUid}", roomCode, playerUid);

        // 从服务器端异常状态中移除（需求 REQ-3.4）
        lock (room)
        {
            room.AbnormalPlayerInfos.Remove(playerUid);
        }

        // 广播给房间内所有玩家（需求 REQ-2.2）
        await Clients.Group(roomCode).SendAsync("PlayerAnomalyRecovered", playerUid);
    }

    /// <summary>
    /// 更新成员状态。
    /// 当玩家上报 Reviving/Rejoining 时，标记为异常并重新评估 ArrivalSets；
    /// 当玩家上报 Normal 时，清除异常标记。
    /// targetProgress：异常玩家的目标进度值，用于判定其他玩家在某同步点是否需要等他。
    /// </summary>
    public async Task MemberStatusChanged(string playerUid, string status, long targetProgress = -1)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        bool isAbnormalReport = status == "Reviving" || status == "Rejoining";
        bool isNormalReport = status == "Normal";

        // 收集每个同步点的进度值（用于判定）
        // syncId → progress 映射需要从客户端推断，这里用 ArrivalSet 中第一个玩家的 CurrentProgress 作为参考
        // 但更安全的做法是：对每个同步点，用 ShouldBroadcastAllArrived 重新判定
        var satisfiedSyncs = new List<(string syncId, long progress)>();

        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
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

                    if (_ops.ShouldBroadcastAllArrived(room, syncId, arrivals, syncProgress))
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
            await Clients.Group(roomCode).SendAsync("AllArrived", syncId);
            _roomManager.ClearArrivalSet(roomCode, syncId);
            lock (room) { room.BroadcastedSyncIds.Add(syncId); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
        }

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await _ops.EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>
    /// 客户端在跳路线后立即广播自己的新进度（multiplayer-sync-skip-by-progress §2.4）。
    /// 服务端更新对应玩家的 CurrentProgress = routeIndex * 1_000_000，
    /// 并触发对房间所有 ArrivalSets 的全量重评估（与 MemberStatusChanged / WaitForAllPlayers 同一机制）。
    ///
    /// 鉴权（OQ-2 方案 A）：用 Context.ConnectionId 定位本连接对应的玩家，
    ///   校验 player.PlayerUid == playerUid（playerUid 非空时）。不一致直接 LogWarning + return。
    /// 兼容性：旧客户端不调用此方法即可，新增 Hub 方法不破坏旧协议。
    /// </summary>
    public async Task ReportMemberProgress(string playerUid, int routeIndex)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        long newProgress = (long)routeIndex * 1_000_000L;

        List<(string syncId, long progress)> satisfiedSyncs;
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player == null)
            {
                _logger.LogWarning("[ReportMemberProgress] 连接 {ConnId} 不在任何房间玩家列表中，忽略", Context.ConnectionId);
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
            satisfiedSyncs = _ops.CollectSatisfiedSyncsLocked(room);
        }

        foreach (var (sid, sp) in satisfiedSyncs)
        {
            _logger.LogInformation("[ReportMemberProgress] 进度更新后重评估：同步点 {SyncId} 满足条件，广播 AllArrived（房间={RoomCode}, 进度={Progress}）",
                sid, roomCode, sp);
            await Clients.Group(roomCode).SendAsync("AllArrived", sid);
            _roomManager.ClearArrivalSet(roomCode, sid);
            lock (room) { room.BroadcastedSyncIds.Add(sid); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
        }

        // === hoeing-multiplayer-lagging-member-catchup（改动 8）：刷新 CurrentProgress 后广播玩家列表 ===
        // 使客户端 CurrentPlayerList 缓存的段级 CurrentProgress 随同步点推进刷新（落后追赶判定数据源，避免 BUG-C）。
        // lock 外 await，复用已有 PlayerListUpdated 事件，无新增协议；旧客户端忽略多余字段/推送。
        await Clients.Group(roomCode).SendAsync("PlayerListUpdated", room.Players);

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await _ops.EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>
    /// 记录路线跳过
    /// </summary>
    public Task RouteSkipped(string playerUid, int routeIndex)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogInformation("[RouteSkipped] 房间={RoomCode}, 玩家={PlayerUid}, 路线={RouteIndex}",
            roomCode, playerUid, routeIndex);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录等待点到达
    /// </summary>
    public Task WaitPointReached(string playerUid, string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogDebug("[WaitPointReached] 房间={RoomCode}, 玩家={PlayerUid}, 同步点={SyncPointId}",
            roomCode, playerUid, syncPointId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 更新战斗状态
    /// </summary>
    public Task FightingStatusChanged(string playerUid, bool isFighting)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        _logger.LogDebug("[FightingStatusChanged] 玩家={PlayerUid}, 战斗中={IsFighting}", playerUid, isFighting);

        return Task.CompletedTask;
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
    public Task WaitForAllPlayers(string syncId, long syncProgress = -1)
        => _ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncId, syncProgress);

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

    // =========================================================================
    // 控制房间（multiplayer-hoeing-assistant）— 远程控制
    // =========================================================================

    /// <summary>
    /// 加入控制房间。校验密码 + UID 白名单，成功后加入 CTRL_{roomCode} Group
    /// </summary>
    public async Task JoinControlRoom(string roomCode, string password, string playerUid, string playerName, List<string>? allowedUids = null, bool isRemote = false, string clientInstanceId = "")
    {
        try
        {
            var uidWhitelist = allowedUids ?? [];
            if (!ControlRoomAuth.Authenticate(roomCode, password, playerUid, uidWhitelist))
            {
                await Clients.Caller.SendAsync("JoinRejected", "密码错误或UID不在白名单中");
                return;
            }

            var group = $"CTRL_{roomCode}";
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
            TrackGroup(group);
            var isWebClient = playerUid.StartsWith("web_");
            // 遥控端（observerMode 标记，isRemote=true）与 WEB 端类似：不加入 _controlRooms 成员列表，
            // 只加 SignalR Group 收广播。这样遥控端不建独立条目、不被 ResolveTargets 匹配（不接收命令）、
            // 且同 UID 执行端的 ControlRoomPlayer 不被覆盖（解决"同 UID 双端互挤占"）。
            if (!isWebClient && !isRemote)
            {
                _roomManager.AddToControlRoom(group, Context.ConnectionId, playerUid, playerName, clientInstanceId);
            }
            if (isRemote)
            {
                _roomManager.RegisterRemoteConnection(group, Context.ConnectionId);
            }
            _logger.LogInformation("玩家 {PlayerName}({PlayerUid}) 加入控制房间 {RoomCode} (Web={IsWeb}, Remote={IsRemote})", playerName, playerUid, roomCode, isWebClient, isRemote);

            // 广播成员列表
            var players = _roomManager.GetControlRoomPlayers(group);
            await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", players);

            // 遥控端不接收命令（FR-3），故不下发离线缓存命令。WEB 端同样不入 _controlRooms，
            // 但既有行为会下发缓存——保持 WEB 端不变，仅对遥控端跳过。
            if (!isRemote)
            {
                var pending = _roomManager.GetAndClearPendingCommands(playerUid);
                foreach (var cmd in pending)
                {
                    await Clients.Client(Context.ConnectionId).SendAsync("RemoteCommand", cmd);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinControlRoom 失败");
            try
            {
                // 把异常信息传给客户端，让用户能定位具体原因
                await Clients.Caller.SendAsync("JoinRejected", $"加入控制房间失败: {ex.Message}");
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
    public async Task SendRemoteCommand(RemoteCommand command)
    {
        try
        {
            var group = $"CTRL_{command.RoomCode}";
            // WEB 控制端（UID 以 web_ 开头）不会被 AddToControlRoom 加入 _controlRooms，
            // 但它们已通过 JoinControlRoom 的密码校验并加入了 CTRL_ group，应放行发送。
            // PC 端助手（UID 为真实数字）走 _controlRooms 校验，行为不变。
            // 遥控端（isRemote，不在 _controlRooms）用 RegisterRemoteConnection 登记，也放行发送。
            var isWebSender = !string.IsNullOrEmpty(command.SenderUid) && command.SenderUid.StartsWith("web_");
            var isRemoteSender = _roomManager.IsRemoteConnection(group, Context.ConnectionId);
            if (!isWebSender && !isRemoteSender && !_roomManager.IsInControlRoom(group, Context.ConnectionId))
            {
                _logger.LogWarning("玩家 {Sender} 不在控制房间中，拒绝发送命令", command.Sender);
                return;
            }

            // 解析目标
            var targets = _roomManager.ResolveTargets(command);
            var deliveredTo = 0;
            foreach (var connectionId in targets)
            {
                await Clients.Client(connectionId).SendAsync("RemoteCommand", command);
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
            await Clients.Caller.SendAsync("RemoteCommandAck", new
            {
                commandId = command.CommandId,
                deliveredTo,
                message = deliveredTo == 0 ? "没有在线目标" : ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendRemoteCommand 失败");
        }
    }

    /// <summary>
    /// 成员上报自身 BGI 状态与可用配置列表，服务端更新后广播最新成员列表。
    /// </summary>
    public async Task ReportControlStatus(ControlStatus status)
    {
        try
        {
            var group = $"CTRL_{status.RoomCode}";
            // 只更新状态，不做就绪检查（就绪检查由 ReportOnlineEvent 端点统一处理）
            _roomManager.UpdateControlStatus(group, Context.ConnectionId, status);

            // 广播给控制房间所有成员
            var players = _roomManager.GetControlRoomPlayers(group);
            await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", players);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportControlStatus 失败");
        }
    }

    // 成员截图汇聚限流表（嘟嘟可 P5）："CTRL_{roomCode}:{uid}" → 最近一次转发时间（UTC）
    private static readonly ConcurrentDictionary<string, DateTime> ScreenshotRateLimit = new();

    /// <summary>
    /// 成员截图汇聚（嘟嘟可 P5 / 远程成员巡检墙）：成员端助手每 10s 上报一帧 JPEG 缩略图（base64），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储）。
    /// 限流：同 uid 10 秒内只转发一帧，超出丢弃。
    /// </summary>
    public async Task ReportMemberScreenshot(string roomCode, string uid, string jpegBase64, int width, int height, DateTime capturedAt)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            // 校验与 SendRemoteCommand 同款：PC 端在 _controlRooms 里；遥控端登记连接也放行（可只读观看）
            if (!_roomManager.IsInControlRoom(group, Context.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, Context.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发成员截图（uid={Uid}）", roomCode, uid);
                return;
            }

            // 负载上限（审查中危5）：base64 超 512KB（≈384KB JPEG，远超 480px 缩略图正常体积 ~30KB）直接丢弃，
            // 防止异常/恶意端用大图打爆房间广播带宽
            const int MaxJpegBase64Length = 512 * 1024;
            if (string.IsNullOrEmpty(jpegBase64) || jpegBase64.Length > MaxJpegBase64Length)
            {
                _logger.LogWarning("成员截图负载超限或为空（uid={Uid}, {Len} 字符），丢弃", uid, jpegBase64?.Length ?? 0);
                return;
            }

            // 简单限流：同 uid 10 秒内只转发一帧（截图流量大，防止异常端打爆房间广播）
            // 注：TryGetValue + 写回不是原子操作，并发下同 uid 可能偶尔放过多一帧——
            // 限流只是防滥用兜底而非精确配额，可接受，不为它引入锁。
            var key = $"{group}:{uid}";
            var now = DateTime.UtcNow;
            if (ScreenshotRateLimit.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(10))
                return;
            ScreenshotRateLimit[key] = now;
            // 顺带清理过期键（房间/uid 规模小，概率性清理即可，防长期运行字典膨胀）
            if (ScreenshotRateLimit.Count > 200)
            {
                foreach (var kv in ScreenshotRateLimit)
                    if (now - kv.Value > TimeSpan.FromHours(1))
                        ScreenshotRateLimit.TryRemove(kv.Key, out _);
            }

            // 纯转发（发送者自己也在 Group 内会收到自己的帧，客户端按 uid 自行忽略即可）
            await Clients.Group(group).SendAsync("MemberScreenshot", new
            {
                uid,
                jpegBase64,
                width,
                height,
                capturedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberScreenshot 失败");
        }
    }

    // 成员日志汇聚限流表（房间实时日志汇聚）："CTRL_{roomCode}:{uid}" → 最近 1 秒窗口内的批次数（窗口起点, 计数）
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogRateLimit = new();

    /// <summary>
    /// 房间实时日志汇聚：成员端助手每 500ms 合批上报本机 BGI 实时日志行（已渲染为文本行），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储/不过滤内容）。
    /// 限流：同 uid 每秒最多 4 批（正常节奏 500ms 一批，留一倍余量）；负载上限：单批 500 行 / 256KB 字符。
    /// infoOnly：发送端开启省流（仅 INF+）的标志，透传给观看端做状态提示。
    /// </summary>
    public async Task ReportMemberLogBatch(string roomCode, string uid, string senderName, List<string> lines, bool infoOnly)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            // 校验与 ReportMemberScreenshot 同款：PC 端在 _controlRooms 里；遥控端登记连接也放行
            if (!_roomManager.IsInControlRoom(group, Context.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, Context.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发成员日志（uid={Uid}）", roomCode, uid);
                return;
            }

            // 负载上限：单批 500 行 / 总 256KB 字符，超限丢弃（正常批远低于此，超限多半是异常端）
            const int MaxLines = 500;
            const int MaxTotalChars = 256 * 1024;
            if (lines == null || lines.Count == 0) return;
            if (lines.Count > MaxLines || lines.Sum(l => l?.Length ?? 0) > MaxTotalChars)
            {
                _logger.LogWarning("成员日志批负载超限（uid={Uid}, {Count} 行 / {Chars} 字符），丢弃",
                    uid, lines.Count, lines.Sum(l => l?.Length ?? 0));
                return;
            }

            // 兜底门：没有任何订阅者时直接丢弃。目标端按订阅数>0 才上报，但订阅可能刚断开、
            // 或目标端是旧版（不看订阅数照常上报），这里挡住避免白转发/白限流。
            if (_roomManager.GetLogSubscriberCount(group, uid) == 0)
            {
                _logger.LogDebug("成员 {Uid} 的日志批无订阅者，丢弃", uid);
                return;
            }

            // 简单限流：同 uid 每秒最多 4 批。与截图限流同款非原子读写，偶尔多放一批可接受（防滥用兜底而非精确配额）
            var key = $"{group}:{uid}";
            var now = DateTime.UtcNow;
            var state = LogRateLimit.GetOrAdd(key, _ => (now, 0));
            if (now - state.WindowStart > TimeSpan.FromSeconds(1))
                state = (now, 0);
            if (state.Count >= 4) return;
            LogRateLimit[key] = (state.WindowStart, state.Count + 1);
            // 顺带清理过期键（房间/uid 规模小，概率性清理即可）
            if (LogRateLimit.Count > 200)
            {
                foreach (var kv in LogRateLimit)
                    if (now - kv.Value.WindowStart > TimeSpan.FromHours(1))
                        LogRateLimit.TryRemove(kv.Key, out _);
            }

            // 纯转发（发送者自己也在 Group 内会收到自己的批，客户端按 uid 自滤）
            await Clients.Group(group).SendAsync("MemberLogBatch", new
            {
                uid,
                senderName,
                lines,
                infoOnly,
                serverTime = now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogBatch 失败");
        }
    }

    /// <summary>
    /// 订阅某成员的实时日志流（房间日志汇聚·观众驱动：没人订阅时目标端零上报零流量）。
    /// 目标端收到 MemberLogSubscribersChanged(count)，只需要知道"有几个人在看"。幂等。
    /// </summary>
    public async Task SubscribeMemberLog(string roomCode, string targetUid)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!_roomManager.IsInControlRoom(group, Context.ConnectionId)
                && !_roomManager.IsRemoteConnection(group, Context.ConnectionId))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志订阅（target={Uid}）", roomCode, targetUid);
                return;
            }

            var count = _roomManager.SubscribeMemberLog(group, targetUid, Context.ConnectionId);
            if (count < 0)
            {
                _logger.LogWarning("成员 {Uid} 的日志订阅者已达上限，拒绝新订阅", targetUid);
                return;
            }
            await NotifyLogSubscriberCount(group, targetUid, count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubscribeMemberLog 失败");
        }
    }

    /// <summary>退订某成员的实时日志流。未订阅过时静默。</summary>
    public async Task UnsubscribeMemberLog(string roomCode, string targetUid)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            var count = _roomManager.UnsubscribeMemberLog(group, targetUid, Context.ConnectionId);
            if (count == null) return; // 未订阅过
            await NotifyLogSubscriberCount(group, targetUid, count.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UnsubscribeMemberLog 失败");
        }
    }

    /// <summary>把最新订阅数转发给目标成员在房间里的连接（目标端据此启停上报）。</summary>
    private async Task NotifyLogSubscriberCount(string group, string targetUid, int count)
    {
        var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
        if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线：订阅留着，上线后由观看端重订阅流程兜底
        await Clients.Client(targetConn).SendAsync("MemberLogSubscribersChanged", count);
    }

    // ========== 远程成员完整日志下载（按需请求-应答，纯转发不存储） ==========

    /// <summary>日志文件名白名单（含中文）：拒绝路径分隔符/相对路径，防目录穿越。服务端与助手端同款。</summary>
    private static readonly Regex LogFileNameRegex = new(
        @"^[\w\-.一-鿿]{1,120}\.log$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>requestId 长度上限（Guid.N 为 32，留余量防滥用）。</summary>
    private const int MaxRequestIdLength = 64;
    /// <summary>文件列表项数上限（超出截断）。</summary>
    private const int MaxLogFileListItems = 200;
    /// <summary>单块 base64 字符数上限（≈192KB 原始数据）。</summary>
    private const int MaxChunkBase64Chars = 256 * 1024;
    /// <summary>单次下载总块数上限（≈压缩后 75MB 内；超出直接丢）。</summary>
    private const int MaxLogFileChunks = 400;
    /// <summary>分块上行限流：同 uid 每秒最多 30 块。</summary>
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogChunkRateLimit = new();
    /// <summary>忙标记块限流：同 uid 每秒最多 5 个（防洪泛：忙标记不计入数据块限流，独立宽松阈值）。</summary>
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> LogBusyRateLimit = new();

    /// <summary>
    /// 日志传输请求映射：requestId → (请求方 connectionId, 创建时间 UTC)。
    /// RequestMemberLogFiles / RequestMemberLogDownload 时建立；对应应答（列表 / done=true 块）转发后删除；
    /// 兜底：插入新映射时顺带清理 10 分钟前的过期项（如下载中途双方断线，无 done 块可触发删除）。
    /// 转发方式：应答一律按映射<b>单播</b>请求方，不广播（块数据量大，广播会让无关成员白收流量）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string RequesterConnectionId, DateTime CreatedAtUtc)>
        LogTransferRequests = new();
    private static readonly TimeSpan LogTransferRequestTtl = TimeSpan.FromMinutes(10);

    /// <summary>登记请求映射并顺带概率性清理过期项。</summary>
    private static void RegisterLogTransferRequest(string requestId, string requesterConnectionId)
    {
        var now = DateTime.UtcNow;
        LogTransferRequests[requestId] = (requesterConnectionId, now);
        if (LogTransferRequests.Count > 64)
        {
            foreach (var kv in LogTransferRequests)
                if (now - kv.Value.CreatedAtUtc > LogTransferRequestTtl)
                    LogTransferRequests.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>同款非原子窗口计数限流（防滥用兜底而非精确配额）。返回 true=放行。</summary>
    private static bool PassRateLimit(ConcurrentDictionary<string, (DateTime WindowStart, int Count)> table,
        string key, int perSecond)
    {
        var now = DateTime.UtcNow;
        var state = table.GetOrAdd(key, _ => (now, 0));
        if (now - state.WindowStart > TimeSpan.FromSeconds(1))
            state = (now, 0);
        if (state.Count >= perSecond) return false;
        table[key] = (state.WindowStart, state.Count + 1);
        // 顺带清理过期键（概率性清理即可）
        if (table.Count > 200)
        {
            foreach (var kv in table)
                if (now - kv.Value.WindowStart > TimeSpan.FromHours(1))
                    table.TryRemove(kv.Key, out _);
        }
        return true;
    }

    /// <summary>校验请求连接是否在对应控制房（PC 端在 _controlRooms；遥控端登记连接也放行）。</summary>
    private bool IsInControlRoomOrRemote(string group)
        => _roomManager.IsInControlRoom(group, Context.ConnectionId)
           || _roomManager.IsRemoteConnection(group, Context.ConnectionId);

    /// <summary>
    /// 观众端请求目标成员的日志文件列表 → 单播目标端 MemberLogFilesRequested(requesterUid, requestId)。
    /// 目标不在线时静默丢弃（观众端超时兜底提示）。
    /// </summary>
    public async Task RequestMemberLogFiles(string roomCode, string targetUid, string requestId)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志文件列表请求（target={Uid}）", roomCode, targetUid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;

            var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
            if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线
            // 登记 requestId → 请求方连接，应答按映射单播回来
            RegisterLogTransferRequest(requestId, Context.ConnectionId);
            var requesterUid = _roomManager.GetUidByConnectionId(group, Context.ConnectionId) ?? "";
            await Clients.Client(targetConn).SendAsync("MemberLogFilesRequested", requesterUid, requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestMemberLogFiles 失败");
        }
    }

    /// <summary>
    /// 目标端应答日志文件列表 → 按 requestId 映射<b>单播</b>请求方（转发后即删映射）。
    /// 项数超上限截断；文件名不合白名单的项剔除（双保险，助手端已校验）。映射不存在（过期/伪造）直接丢弃。
    /// </summary>
    public async Task ReportMemberLogFiles(string roomCode, string uid, string requestId, List<MemberLogFileDescriptor> files)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发日志文件列表（uid={Uid}）", roomCode, uid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            // 认领并删除映射（一次性应答）；无映射 = 过期或伪造，丢弃
            if (!LogTransferRequests.TryRemove(requestId, out var req)) return;

            files ??= [];
            var cleaned = files
                .Where(f => f != null && LogFileNameRegex.IsMatch(f.Name ?? ""))
                .Take(MaxLogFileListItems)
                .ToList();
            await Clients.Client(req.RequesterConnectionId).SendAsync("MemberLogFileList", new
            {
                uid,
                requestId,
                files = cleaned
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogFiles 失败");
        }
    }

    /// <summary>
    /// 观众端请求下载目标成员的某个日志文件 → 单播目标端 MemberLogDownloadRequested(requesterUid, requestId, fileName)。
    /// fileName 必须过白名单（防目录穿越），否则直接拒绝。
    /// </summary>
    public async Task RequestMemberLogDownload(string roomCode, string targetUid, string requestId, string fileName)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝日志下载请求（target={Uid}）", roomCode, targetUid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            if (string.IsNullOrEmpty(fileName) || !LogFileNameRegex.IsMatch(fileName))
            {
                _logger.LogWarning("日志下载文件名不合白名单（target={Uid}, name={FileName}），拒绝", targetUid, fileName);
                return;
            }

            var targetConn = _roomManager.GetConnectionIdByUid(group, targetUid);
            if (string.IsNullOrEmpty(targetConn)) return; // 目标不在线
            // 登记 requestId → 请求方连接，分块按映射单播回来（done 块转发后删除映射）
            RegisterLogTransferRequest(requestId, Context.ConnectionId);
            var requesterUid = _roomManager.GetUidByConnectionId(group, Context.ConnectionId) ?? "";
            await Clients.Client(targetConn).SendAsync("MemberLogDownloadRequested", requesterUid, requestId, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestMemberLogDownload 失败");
        }
    }

    /// <summary>
    /// 目标端分块上行日志文件（gzip+base64）→ 按 requestId 映射<b>单播</b>请求方（映射不存在=过期/伪造，丢弃）。
    /// 限流：数据块同 uid 每秒 ≤30；忙标记块（totalChunks=0 且 done=true）独立阈值同 uid 每秒 ≤5，防洪泛。
    /// 总块数 ≤400。done=true 块转发后删除映射（下载结束清理）。
    /// </summary>
    public async Task ReportMemberLogChunk(string roomCode, string uid, string requestId,
        int seq, int totalChunks, string chunkBase64, string fileName, bool done)
    {
        try
        {
            var group = $"CTRL_{roomCode}";
            if (!IsInControlRoomOrRemote(group))
            {
                _logger.LogWarning("连接不在控制房间 {RoomCode} 中，拒绝转发日志块（uid={Uid}）", roomCode, uid);
                return;
            }
            if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxRequestIdLength) return;
            if (string.IsNullOrEmpty(fileName) || !LogFileNameRegex.IsMatch(fileName)) return;

            // 认领映射（下载期间保留，done 块转发后才删）；无映射 = 过期或伪造，丢弃
            if (!LogTransferRequests.TryGetValue(requestId, out var req)) return;

            var rateKey = $"{group}:{uid}";
            var isBusyMarker = done && totalChunks == 0;
            if (isBusyMarker)
            {
                // 忙标记也纳入限流（独立宽松阈值 5 个/秒），防止异常端刷忙标记泛洪请求方
                if (!PassRateLimit(LogBusyRateLimit, rateKey, 5)) return;
            }
            else
            {
                if (totalChunks <= 0 || totalChunks > MaxLogFileChunks) return;
                if (seq < 0 || seq >= totalChunks) return;
                if (!string.IsNullOrEmpty(chunkBase64) && chunkBase64.Length > MaxChunkBase64Chars)
                {
                    _logger.LogWarning("成员日志块超限（uid={Uid}, {Chars} 字符），丢弃", uid, chunkBase64.Length);
                    return;
                }

                // 简单限流：同 uid 每秒最多 30 块（正常节奏 ~6 块/秒，留数倍余量；防滥用兜底而非精确配额）
                if (!PassRateLimit(LogChunkRateLimit, rateKey, 30)) return;
            }

            await Clients.Client(req.RequesterConnectionId).SendAsync("MemberLogFileChunk", new
            {
                uid,
                requestId,
                seq,
                totalChunks,
                chunkBase64 = chunkBase64 ?? "",
                fileName,
                done
            });
            // 下载结束（含忙标记）：删除映射，请求生命周期闭环
            if (done) LogTransferRequests.TryRemove(requestId, out _);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportMemberLogChunk 失败");
        }
    }

    /// <summary>清除指定成员的 OnlineHistory（已联机记录）。由本人或房主调用。</summary>
    public async Task ClearOnlineHistory(string targetUid)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(Context.ConnectionId);
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            var roomCode = group.Replace("CTRL_", "");
            _roomManager.ClearOnlineHistory(roomCode, targetUid);

            // 广播更新给所有成员
            var players = _roomManager.GetControlRoomPlayers(group);
            await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", players);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClearOnlineHistory 失败");
        }
    }

    /// <summary>上报上线事件（带 generation 代序号）。由 ReportOnlineEvent 端点统一处理就绪检查。</summary>
    public async Task ReportOnlineEvent(int generation, bool isOnlineReady)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(Context.ConnectionId);
            if (string.IsNullOrEmpty(group))
            {
                return;
            }

            _roomManager.ReportOnlineEvent(group, Context.ConnectionId, generation);

            // 广播玩家列表更新
            var players = _roomManager.GetControlRoomPlayers(group);
            await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", players);

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
                    await Clients.Group(group).SendAsync("AllReady", readyGeneration);
                    await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", _roomManager.GetControlRoomPlayers(group));
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
                    await Clients.Client(connId).SendAsync("AllReadyConfirm", generation);
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
                    await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", _roomManager.GetControlRoomPlayers(group));
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
        await Clients.Group(group).SendAsync("ControlRoomPlayersUpdated", _roomManager.GetControlRoomPlayers(group));
    }

    /// <summary>客户端确认收到 AllReadyConfirm。由客户端收到 AllReadyConfirm 事件后调用。</summary>
    public async Task ConfirmAllReady(int generation)
    {
        try
        {
            var group = _roomManager.GetControlRoomGroup(Context.ConnectionId);
            if (string.IsNullOrEmpty(group)) return;
            var uid = _roomManager.GetUidByConnectionId(group, Context.ConnectionId);
            if (string.IsNullOrEmpty(uid)) return;
            _roomManager.RegisterConfirmAck(group, uid, generation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConfirmAllReady 失败");
        }
    }
}
