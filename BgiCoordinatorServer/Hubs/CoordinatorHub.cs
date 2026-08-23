using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace BgiCoordinatorServer.Hubs;

public class CoordinatorHub : Hub
{
    private readonly RoomManager _roomManager;
    private readonly ILogger<CoordinatorHub> _logger;
    private readonly IHubContext<CoordinatorHub> _hubContext;

    // 每个房间的路线上报缓存：roomCode → (connectionId → routes)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<RouteHash>>>
        RouteReports = new();

    // 每个房间的变体 schema 上报缓存：roomCode → (connectionId → items)
    // route-variant-sync-by-logical-id spec / R6
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<RouteVariantSchemaItem>>>
        VariantSchemaReports = new();

    // 每个房间的变体校验 30s 超时器（R6.8）
    private static readonly ConcurrentDictionary<string, CancellationTokenSource>
        VariantSchemaTimeouts = new();

    // 每个连接当前所属的 SignalR Group 列表（用于轮换房间时清理旧 Group 订阅，
    // 避免上一个房间关闭/广播时串扰到已切换到新房间的连接）。
    private static readonly ConcurrentDictionary<string, HashSet<string>> _connectionGroups = new();

    public CoordinatorHub(RoomManager roomManager, ILogger<CoordinatorHub> logger, IHubContext<CoordinatorHub> hubContext)
    {
        _roomManager = roomManager;
        _logger = logger;
        _hubContext = hubContext;
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
        _logger.LogInformation("CreateRoom 收到参数: playerName={Name}, playerUid={Uid}, expectedPlayerCount={Count}, whitelist={WL}",
            playerName, playerUid, expectedPlayerCount, whitelist != null ? string.Join(",", whitelist) : "null");
        // 多世界轮次切换：先离开所有旧 Group，避免旧房间广播串扰
        await LeaveAllGroupsAsync();
        // version-compatibility-check 改动 5：透传房主上报版本作为房间基准版本
        var code = _roomManager.CreateRoom(Context.ConnectionId, playerName, whitelist, playerUid, expectedPlayerCount, reportedVersion);
        await Groups.AddToGroupAsync(Context.ConnectionId, code);
        TrackGroup(code);
        _logger.LogInformation("连接 {ConnId}({Name}) 创建房间 {Code}", Context.ConnectionId, playerName, code);

        var room = _roomManager.GetRoom(code)!;
        await Clients.Group(code).SendAsync("PlayerListUpdated", room.Players);
        return code;
    }

    /// <summary>加入房间，广播 PlayerListUpdated</summary>
    public async Task<bool> JoinRoom(string roomCode, string playerName = "", string playerUid = "", string reportedVersion = "")
    {
        var playerId = Context.ConnectionId;

        // === 版本一致性校验（就地，入房之前）version-compatibility-check R1.1/R6.1 改动 7/14 ===
        // 基准 = 房间内第一个非通配玩家版本（ResolveBaselineVersion），而非固定取房主版本：
        // 否则开发者通配版本当房主时，房主通配 → 全员放行，校验失效（Property 7）。
        var room0 = _roomManager.GetRoom(roomCode);
        if (room0 != null)
        {
            List<string> existingVersions;
            lock (room0)
            {
                existingVersions = room0.Players.Select(p => p.ReportedVersion).ToList();
            }
            if (!VersionCompatibilityDecisions.CanJoin(reportedVersion, existingVersions))
            {
                var baseline = VersionCompatibilityDecisions.ResolveBaselineVersion(existingVersions) ?? "";
                var checkResult = BuildVersionCheckResult(reportedVersion, baseline);
                _logger.LogWarning("连接 {ConnId} 版本校验不兼容，阻断加入房间 {Code}：member={Member} baseline={Baseline}",
                    Context.ConnectionId, roomCode, reportedVersion, baseline);
                // 向该加入者单独回传 Check_Result（向后兼容：旧客户端不订阅此事件即忽略，不影响 bool 返回语义 U4.1）
                await Clients.Caller.SendAsync("VersionCheckRejected", checkResult);
                return false; // 硬阻断（R5.1），不调用 RoomManager.JoinRoom，成员不入房
            }
        }

        var (success, error) = _roomManager.JoinRoom(roomCode, Context.ConnectionId, playerId, playerName, playerUid, reportedVersion);

        if (!success)
        {
            _logger.LogWarning("连接 {ConnId} 加入房间 {Code} 失败：{Error}",
                Context.ConnectionId, roomCode, error);
            return false;
        }

        // 多世界轮次切换：先离开所有旧 Group，避免旧房间广播串扰
        await LeaveAllGroupsAsync(excludeGroup: roomCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, roomCode);
        TrackGroup(roomCode);
        _logger.LogInformation("连接 {ConnId} 加入房间 {Code}", Context.ConnectionId, roomCode);

        var room = _roomManager.GetRoom(roomCode)!;
        await Clients.Group(roomCode).SendAsync("PlayerListUpdated", room.Players);
        return true;
    }

    /// <summary>
    /// 构造版本校验失败的 Check_Result（version-compatibility-check 改动 7 / R5.2–R5.6）。
    /// 含双方版本号、双方是否通配标记、统一版本引导文案。
    /// </summary>
    private static Models.VersionCheckResult BuildVersionCheckResult(string memberVersion, string baselineVersion)
    {
        return new Models.VersionCheckResult
        {
            Compatible = false,
            MemberVersion = memberVersion ?? "",
            BaselineVersion = baselineVersion ?? "",
            MemberIsWildcard = VersionCompatibilityDecisions.IsWildcard(memberVersion),
            BaselineIsWildcard = VersionCompatibilityDecisions.IsWildcard(baselineVersion),
            // R5.6 引导：请将房内所有玩家更新到完全相同的版本后重试
            Hint = "版本不一致，已阻止加入。请将房内所有玩家更新到完全相同的版本后重试。"
        };
    }

    /// <summary>离开房间，广播 PlayerListUpdated</summary>
    public async Task LeaveRoom()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        var affectedCodes = _roomManager.LeaveRoom(Context.ConnectionId);

        foreach (var code in affectedCodes)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, code);
            UntrackGroup(code);
            var updatedRoom = _roomManager.GetRoom(code);
            var players = updatedRoom?.Players ?? [];
            await Clients.Group(code).SendAsync("PlayerListUpdated", players);
        }

        _logger.LogInformation("连接 {ConnId} 离开房间", Context.ConnectionId);
    }

    /// <summary>上报路线清单，所有成员上报后对比 MD5，广播差异或验证通过</summary>
    public async Task ReportRouteList(List<RouteHash> routes)
    {
        _logger.LogInformation("[ReportRouteList] 连接 {ConnId} 上报路线清单，共 {Count} 条", Context.ConnectionId, routes?.Count ?? 0);
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ReportRouteList] 连接 {ConnId} 未在任何房间中，忽略路线上报", Context.ConnectionId);
            return;
        }
        _logger.LogInformation("[ReportRouteList] 连接 {ConnId} 在房间 {Code} 中上报路线", Context.ConnectionId, roomCode);

        var roomReports = RouteReports.GetOrAdd(roomCode, _ => new ConcurrentDictionary<string, List<RouteHash>>());
        roomReports[Context.ConnectionId] = routes;

        // 检查是否所有在线成员都已上报
        List<string> onlineConnIds;
        lock (room)
        {
            onlineConnIds = room.Players.Select(p => p.ConnectionId).ToList();
        }

        if (!onlineConnIds.All(id => roomReports.ContainsKey(id)))
        {
            _logger.LogInformation("[ReportRouteList] 房间 {Code} 等待其他玩家上报，已上报: {Reported}/{Total}",
                roomCode, roomReports.Count, onlineConnIds.Count);
            return; // 还有人未上报
        }

        // 所有人都上报了，开始对比
        var allReports = onlineConnIds
            .Select(id => roomReports[id])
            .ToList();

        try
        {
            var diffFiles = ComputeRouteDiff(allReports);

            if (diffFiles.Count == 0)
            {
                _logger.LogInformation("房间 {Code} 路线验证通过", roomCode);
                await Clients.Group(roomCode).SendAsync("RouteVerificationPassed");
            }
            else
            {
                _logger.LogWarning("房间 {Code} 路线存在差异：{Files}", roomCode, string.Join(", ", diffFiles));
                await Clients.Group(roomCode).SendAsync("RouteDiffReceived", diffFiles);
            }

            // 清理缓存
            RouteReports.TryRemove(roomCode, out _);
        }
        catch (Exception ex)
        {
            // 兜底：比对/广播过程出现未预期异常时，绝不让客户端无限等待至 90s 超时。
            // 复用现有 RouteDiffReceived 事件（不新增协议），携带哨兵差异项，
            // 让客户端走 verified == false 路径主动停止锄地（比放行更安全）。
            _logger.LogError(ex, "[ReportRouteList] 房间 {Code} 路线比对/广播发生未预期异常，按校验失败兜底处理", roomCode);

            try
            {
                await Clients.Group(roomCode).SendAsync(
                    "RouteDiffReceived",
                    new List<string> { "__route_verification_error__" });
            }
            catch (Exception broadcastEx)
            {
                // 二次异常（兜底广播本身失败，如连接已断）：仅记日志吞掉，
                // 不再向外抛——此处已是最后防线，逃逸无意义且会再次包成 HubException。
                _logger.LogError(broadcastEx, "[ReportRouteList] 房间 {Code} 兜底广播 RouteDiffReceived 失败", roomCode);
            }

            // 始终清理缓存，避免脏数据残留影响下一轮校验。
            RouteReports.TryRemove(roomCode, out _);
        }
    }

    /// <summary>
    /// 上报本玩家计划要执行的所有路线的变体 schema（route-variant-sync-by-logical-id spec / R6）。
    /// 服务端按 LogicalRouteId 分组比对所有玩家的 SyncPointList + TeleportSyncPointSequence。
    /// 全部一致 → 广播 RouteVariantConsistencyPassed；任一不一致 / 30s 超时 → 广播 RouteVariantConsistencyFailed。
    /// 全员 LogicalRouteId 均为空 → 跳过校验、不广播（老路径零回归 R6.7）。
    /// </summary>
    public async Task ReportRouteVariantSchema(List<RouteVariantSchemaItem> items)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[变体校验] 连接 {ConnId} 未在房间内", Context.ConnectionId);
            return;
        }

        items ??= new List<RouteVariantSchemaItem>();
        var roomReports = VariantSchemaReports.GetOrAdd(roomCode,
            _ => new ConcurrentDictionary<string, List<RouteVariantSchemaItem>>());
        roomReports[Context.ConnectionId] = items;

        _logger.LogInformation("[变体校验] 连接 {ConnId} 在房间 {Code} 上报 {Count} 条 schema（含非空 LogicalRouteId {NonEmpty} 条）",
            Context.ConnectionId, roomCode, items.Count, items.Count(i => !string.IsNullOrEmpty(i.LogicalRouteId)));

        // 启动 30s 超时器（首个上报触发）
        VariantSchemaTimeouts.GetOrAdd(roomCode, code =>
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => _ = OnVariantSchemaTimeoutAsync(code));
            return cts;
        });

        List<string> onlineConnIds;
        lock (room) { onlineConnIds = room.Players.Select(p => p.ConnectionId).ToList(); }
        if (!onlineConnIds.All(id => roomReports.ContainsKey(id)))
        {
            return;   // 还有人未上报
        }

        if (VariantSchemaTimeouts.TryRemove(roomCode, out var timeoutCts))
        {
            // 注意：不能调用 timeoutCts.Cancel()！
            // 超时回调是通过 cts.Token.Register(...) 注册的，Cancel() 会同步触发该回调
            // → OnVariantSchemaTimeoutAsync 广播 RouteVariantConsistencyFailed，
            // 与紧随其后的 EvaluateVariantSchemaAsync 广播 Passed 形成"既发 Failed 又发 Passed"竞态，
            // 客户端先收到 Failed 误判校验失败（单人房主场景必现）。
            // Dispose() 会停掉底层 30s 计时器且不触发已注册回调，正是我们需要的"静默取消计时器"。
            timeoutCts.Dispose();
        }

        await EvaluateVariantSchemaAsync(roomCode, onlineConnIds, roomReports);
    }

    private async Task EvaluateVariantSchemaAsync(string roomCode,
        List<string> onlineConnIds,
        ConcurrentDictionary<string, List<RouteVariantSchemaItem>> roomReports)
    {
        var groupedByLogicalId = new Dictionary<string, List<(string connId, RouteVariantSchemaItem item)>>();
        foreach (var connId in onlineConnIds)
        {
            if (!roomReports.TryGetValue(connId, out var items)) continue;
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.LogicalRouteId)) continue;
                if (!groupedByLogicalId.TryGetValue(it.LogicalRouteId, out var list))
                {
                    list = new List<(string, RouteVariantSchemaItem)>();
                    groupedByLogicalId[it.LogicalRouteId] = list;
                }
                list.Add((connId, it));
            }
        }

        if (groupedByLogicalId.Count == 0)
        {
            // 全员老路径（无任何非空 LogicalRouteId）：没有变体 schema 需要比对。
            // 必须广播 Passed 而不是沉默——客户端 VerifyRouteVariantSchemaAsync 在
            // subscribe-before-action 后等待 Passed/Failed 事件，若服务端不广播，
            // 客户端会一直等到 30s 超时并误判失败（全员老线路联机必现）。
            // 老线路的文件一致性已由 ReportRouteList 的 MD5 校验覆盖，这里广播 Passed 表示
            // "无变体可校验、放行"，与变体场景的 Passed 语义一致，混合/变体场景不受影响。
            _logger.LogInformation("[变体校验] 房间 {Code} 全员老路径（无变体），广播 Passed 放行", roomCode);
            await Clients.Group(roomCode).SendAsync("RouteVariantConsistencyPassed");
            VariantSchemaReports.TryRemove(roomCode, out _);
            return;
        }

        foreach (var (logicalId, entries) in groupedByLogicalId)
        {
            if (entries.Count <= 1) continue;
            var first = entries[0].item;
            for (int i = 1; i < entries.Count; i++)
            {
                var other = entries[i].item;
                if (!SyncPointListEquals(first.SyncPointList, other.SyncPointList)
                    || !TeleportSeqEquals(first.TeleportSyncPointSequence, other.TeleportSyncPointSequence))
                {
                    var playerItems = entries.ToDictionary(e => e.connId, e => e.item);
                    _logger.LogWarning("[变体校验] 房间 {Code} LogicalRouteId={LRI} schema 不一致，广播 Failed",
                        roomCode, logicalId);
                    await Clients.Group(roomCode).SendAsync(
                        "RouteVariantConsistencyFailed", logicalId, playerItems);
                    VariantSchemaReports.TryRemove(roomCode, out _);
                    return;
                }
            }
        }

        _logger.LogInformation("[变体校验] 房间 {Code} 通过（{Count} 个 LogicalRouteId 分组）",
            roomCode, groupedByLogicalId.Count);
        await Clients.Group(roomCode).SendAsync("RouteVariantConsistencyPassed");
        VariantSchemaReports.TryRemove(roomCode, out _);
    }

    private async Task OnVariantSchemaTimeoutAsync(string roomCode)
    {
        if (!VariantSchemaReports.TryRemove(roomCode, out _)) return;
        VariantSchemaTimeouts.TryRemove(roomCode, out var cts);
        cts?.Dispose();
        _logger.LogWarning("[变体校验] 房间 {Code} 30s 上报超时，广播 Failed", roomCode);
        await _hubContext.Clients.Group(roomCode).SendAsync(
            "RouteVariantConsistencyFailed",
            "", new Dictionary<string, RouteVariantSchemaItem>());
    }

    private static bool SyncPointListEquals(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool TeleportSeqEquals(List<int[]> a, List<int[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] == null || b[i] == null) return false;
            if (a[i].Length != 2 || b[i].Length != 2) return false;
            if (a[i][0] != b[i][0] || a[i][1] != b[i][1]) return false;
        }
        return true;
    }

    /// <summary>上报到达集合点，全员到达时广播 AllArrived</summary>
    public async Task ReportArrival(string syncPointId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allArrived = _roomManager.RecordArrival(roomCode, syncPointId, Context.ConnectionId, 0);

        if (allArrived)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 全员到达", roomCode, syncPointId);
            await Clients.Group(roomCode).SendAsync("AllArrived", syncPointId);
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
    public async Task ReportArrivalWithExpectedCount(string syncPointId, int expectedCount)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allArrived = _roomManager.RecordArrival(roomCode, syncPointId, Context.ConnectionId, expectedCount);

        if (allArrived)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 到达人数达到预期 {Expected}，触发 AllArrived", 
                roomCode, syncPointId, expectedCount);
            await Clients.Group(roomCode).SendAsync("AllArrived", syncPointId);
        }

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        if (room != null)
        {
            await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
        }
    }

    /// <summary>上报战斗完成，全员完成时广播 AllFightDone</summary>
    public async Task ReportFightDone(string syncPointId)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        var allDone = _roomManager.RecordFightDone(roomCode, syncPointId, Context.ConnectionId);

        if (allDone)
        {
            _logger.LogInformation("房间 {Code} 同步点 {SyncId} 全员战斗完成", roomCode, syncPointId);
            await Clients.Group(roomCode).SendAsync("AllFightDone", syncPointId);
        }
    }

    /// <summary>上报战斗参与者（multiplayer-shared-fight-end-quorum-sync spec，配额分母）</summary>
    public Task ReportFightParticipant(string syncKey)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        _roomManager.RecordFightParticipant(roomCode, syncKey, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>心跳，更新 LastHeartbeat</summary>
    public Task Heartbeat()
    {
        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>带路线进度信息的心跳（需求 6）</summary>
    public Task HeartbeatWithProgress(int routeIndex, DateTime routeStartTime, double routeEstimatedSeconds)
    {
        _roomManager.UpdateHeartbeatWithProgress(Context.ConnectionId, routeIndex, routeStartTime, routeEstimatedSeconds);
        return Task.CompletedTask;
    }



    /// <summary>查询指定成员的路线进度（需求 6）</summary>
    public Task<MemberProgress?> GetMemberProgress(string playerUid)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null) return Task.FromResult<MemberProgress?>(null);

        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.PlayerUid == playerUid);
            if (player == null || player.CurrentRouteIndex < 0)
                return Task.FromResult<MemberProgress?>(null);

            return Task.FromResult<MemberProgress?>(new MemberProgress
            {
                RouteIndex = player.CurrentRouteIndex,
                RouteStartTime = player.RouteStartTime,
                RouteEstimatedSeconds = player.RouteEstimatedSeconds
            });
        }
    }



    /// <summary>关闭房间（仅房主可操作）</summary>
    public async Task CloseRoom()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[CloseRoom] 连接 {ConnId} 未在任何房间中", Context.ConnectionId);
            return;
        }

        if (room.HostConnectionId != Context.ConnectionId)
        {
            _logger.LogWarning("[CloseRoom] 连接 {ConnId} 不是房主，无法关闭房间 {Code}", Context.ConnectionId, roomCode);
            return;
        }

        _logger.LogInformation("[CloseRoom] 房主 {ConnId} 关闭房间 {Code}", Context.ConnectionId, roomCode);
        await Clients.Group(roomCode).SendAsync("RoomClosed", "房主已关闭房间");
        // 删除整个房间，防止玩家重连后重新加入已关闭的房间
        _roomManager.DeleteRoom(roomCode);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode);
        UntrackGroup(roomCode);
    }

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
    public async Task ReportRouteVerificationDone()
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return;

        // 更新心跳确保玩家状态为在线
        _roomManager.UpdateHeartbeat(Context.ConnectionId);
        
        var allDone = _roomManager.RecordRouteVerificationDone(roomCode, Context.ConnectionId);

        if (allDone)
        {
            _logger.LogInformation("房间 {Code} 路线验证全员完成", roomCode);
            await Clients.Group(roomCode).SendAsync("RouteVerificationAllDone");
        }
        else
        {
            // 记录当前状态用于调试
            var (onlineCount, reportedCount) = _roomManager.GetRouteVerificationStatus(roomCode);
            _logger.LogDebug("房间 {Code} 路线验证进度: {Reported}/{Online}", roomCode, reportedCount, onlineCount);
        }
    }

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
    public async Task UpdateWhitelist(List<string>? whitelist = null)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        if (room.HostConnectionId != Context.ConnectionId)
        {
            _logger.LogWarning("[UpdateWhitelist] 连接 {ConnId} 不是房主，忽略", Context.ConnectionId);
            return;
        }

        _roomManager.UpdateWhitelist(roomCode, whitelist ?? []);
        _logger.LogInformation("[UpdateWhitelist] 房间 {Code} 白名单已更新", roomCode);
    }

    /// <summary>获取在线房间列表</summary>
    public Task<List<RoomSummary>> GetOnlineRooms()
    {
        return Task.FromResult(_roomManager.GetOnlineRooms());
    }

    /// <summary>房主上传锄地配置</summary>
    public Task SetRoomConfig(RoomConfig config)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room != null && room.HostConnectionId == Context.ConnectionId)
        {
            room.HostConfig = config;
            _logger.LogInformation("房间 {Code} 房主配置已更新", roomCode);
        }
        return Task.CompletedTask;
    }

    /// <summary>成员拉取房主锄地配置</summary>
    public Task<RoomConfig?> GetRoomConfig()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        return Task.FromResult(room?.HostConfig);
    }

    /// <summary>房主上报已进入等待状态</summary>
    public async Task ReportHostReady()
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room != null && roomCode != null && room.HostConnectionId == Context.ConnectionId)
        {
            room.HostReady = true;
            _logger.LogInformation("房间 {Code} 房主已就绪", roomCode);
            await Clients.Group(roomCode).SendAsync("HostReadyChanged", true);
        }
    }

    /// <summary>查询房主是否就绪</summary>
    public Task<bool> IsHostReady()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        return Task.FromResult(room?.HostReady ?? false);
    }

    /// <summary>
    /// 房主调用此方法把房间标记为已开锄（spec lock-room-after-start §2）。
    /// 服务端从此 JoinRoom 拒绝非重连新玩家、GetOnlineRooms 也不再返回此房间。
    /// 鉴权：Context.ConnectionId 必须等于 room.HostConnectionId。
    /// 幂等：重复调用直接 return（room.IsStarted 一旦 true 在房间销毁前不复位）。
    /// 非房主调用：LogWarning + return，不抛异常、不修改状态。
    /// </summary>
    public Task MarkRoomStarted() => MarkRoomStartedCore(null);

    /// <summary>
    /// 房主重开续跑：上报已完成房主 UID 集合，服务端据此裁剪权威轮换序列。
    /// 旧服务端无此方法 → 客户端 HubException 降级 → 等价 MarkRoomStarted()（全量序列）。
    /// hoeing-multiworld-host-restart-resume-round Req 1.1 / 6.1。
    /// </summary>
    public Task MarkRoomStartedWithProgress(List<string> completedHostUids)
        => MarkRoomStartedCore(completedHostUids);

    private Task MarkRoomStartedCore(List<string>? completedHostUids)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[MarkRoomStarted] 连接 {ConnId} 未在任何房间中，忽略", Context.ConnectionId);
            return Task.CompletedTask;
        }
        if (room.HostConnectionId != Context.ConnectionId)
        {
            _logger.LogWarning("[MarkRoomStarted] 连接 {ConnId} 不是房主，忽略（房间 {Code}）",
                Context.ConnectionId, roomCode);
            return Task.CompletedTask;
        }
        if (room.IsStarted)
        {
            _logger.LogDebug("[MarkRoomStarted] 房间 {Code} 已经 IsStarted=true，幂等返回", roomCode);
            return Task.CompletedTask;
        }
        room.IsStarted = true;
        _logger.LogInformation("[MarkRoomStarted] 房间 {Code} 已锁定，IsStarted=true", roomCode);

        // multiplayer-server-authoritative-round-order：首轮锁房时全员已在房间
        // （客户端 MarkRoomStarted 在 AllWorldJoined 之后），此刻 Players 是全集，
        // 生成权威轮换序列（首项=首任房主，其余 UID 升序）。整场只生成一次（幂等）。
        // hoeing-multiworld-host-restart-resume-round：completedHostUids 非空时排除已完成房主世界（裁剪）。
        lock (room)
        {
            if (room.RoundHostOrder.Count == 0)
            {
                var hostUid = room.Players.Count > 0 ? room.Players[0].PlayerUid : "";
                IReadOnlySet<string>? exclude =
                    (completedHostUids != null && completedHostUids.Count > 0)
                        ? new HashSet<string>(completedHostUids)
                        : null;
                room.RoundHostOrder = RoundHostOrderDecisions.Build(room.Players, hostUid, exclude);
                _logger.LogInformation("[RoundOrder] 房间 {Code} 生成权威轮换序列（排除 {N} 已完成）：{Order}",
                    roomCode, exclude?.Count ?? 0, string.Join(" -> ", room.RoundHostOrder));
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>返回本房间权威轮换序列（UID 列表）。未生成 / 房间不存在 → 空列表。</summary>
    public Task<List<string>> GetRoundHostOrder()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null) return Task.FromResult(new List<string>());
        lock (room) { return Task.FromResult(new List<string>(room.RoundHostOrder)); }
    }

    /// <summary>房主上传最终路线列表，并广播通知成员</summary>
    public async Task SetHostRouteList(List<string> routeNames)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room != null && room.HostConnectionId == Context.ConnectionId)
        {
            // lock(room)：与 GetHostRouteListStatus 读侧互斥，保证 (HostRouteList, HostRouteListUploaded)
            // 两字段的写入对读侧表现为单一原子快照（multiplayer-member-skip-round-stuck-roundend-sync-fix）。
            lock (room)
            {
                room.HostRouteList = routeNames;
                room.HostRouteListUploaded = true;
            }
            _logger.LogInformation("房间 {Code} 房主路线列表已上传，共 {Count} 条", roomCode, routeNames.Count);
            // 广播通知成员路线列表已就绪
            await Clients.Group(roomCode).SendAsync("HostRouteListReady", routeNames);
        }
    }

    /// <summary>成员拉取房主路线列表</summary>
    public Task<List<string>> GetHostRouteList()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        return Task.FromResult(room?.HostRouteList ?? []);
    }

    /// <summary>
    /// 查询房主是否已上传过路线列表（含上传空列表）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：成员据此区分
    /// "房主从未上传"（false → 继续等待）与"房主上传了空列表"（true + 列表空 → 优雅跳过本轮）。
    /// </summary>
    public Task<bool> IsHostRouteListUploaded()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        return Task.FromResult(room?.HostRouteListUploaded ?? false);
    }

    /// <summary>
    /// 原子返回房主路线列表状态：(Uploaded, RouteNames) 同一时刻快照。
    /// multiplayer-member-skip-round-stuck-roundend-sync-fix：取代成员侧
    /// GetHostRouteList + IsHostRouteListUploaded 两次独立查询，消除 TOCTOU 竞态
    /// （房主在两次查询之间 SetHostRouteList(非空) 导致成员拿到 uploaded=true+count=0 误判跳过）。
    /// lock(room) 与 SetHostRouteList 写侧互斥，并复制列表快照，确保读到的 Uploaded 与 RouteNames
    /// 来自同一时刻、且返回后不被房主并发改动。
    /// </summary>
    public Task<HostRouteListStatus> GetHostRouteListStatus()
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null)
        {
            return Task.FromResult(new HostRouteListStatus { Uploaded = false, RouteNames = [] });
        }
        lock (room)
        {
            return Task.FromResult(new HostRouteListStatus
            {
                Uploaded = room.HostRouteListUploaded,
                RouteNames = room.HostRouteList != null ? new List<string>(room.HostRouteList) : [],
            });
        }
    }

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
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (roomCode == null) return Task.FromResult(0);
        return Task.FromResult(_roomManager.GetWorldJoinedCount(roomCode));
    }

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
                    disconnectedRoom.GracePendingMembers[Context.ConnectionId] = DateTime.UtcNow.AddSeconds(15);
                }
                _logger.LogInformation("[OnDisconnectedAsync] 成员 {ConnId} 进入宽限期(15s)，房间 {Code} 人数保持 {N}",
                    Context.ConnectionId, disconnectedRoomCode, disconnectedRoom.Players.Count);

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
                    await Clients.Group(disconnectedRoomCode).SendAsync("AllArrived", syncId);
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

        await base.OnDisconnectedAsync(exception);
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
            await Clients.Group(roomCode).SendAsync("AllArrived", syncId);
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
            satisfiedSyncs = CollectSatisfiedSyncsLocked(room);
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
        await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
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
    public async Task WaitForAllPlayers(string syncId, long syncProgress = -1)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(Context.ConnectionId);
        if (room == null || roomCode == null) return;

        _logger.LogDebug("[WaitForAllPlayers] 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}, 连接={ConnId}",
            roomCode, syncId, syncProgress, Context.ConnectionId);

        // 更新当前玩家的进度
        if (syncProgress >= 0)
        {
            lock (room)
            {
                var caller = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
                if (caller != null)
                {
                    caller.CurrentProgress = syncProgress;
                }
            }
        }

        // 记录当前连接已到达
        _roomManager.RecordArrival(roomCode, syncId, Context.ConnectionId, 0);

        // === 到达同步点时自动清理异常状态（multiplayer-mutual-wait-collective-skip fix）===
        // 条件与 WillBroadcastHere 的清理逻辑完全对称：
        //   异常玩家 + TargetProgress > 0 + syncProgress >= 0 + TargetProgress <= syncProgress
        // 这样即使 CollectiveSkip 标记了异常，玩家到达目标同步点后也能自动恢复正常，
        // 不会因为 IsAbnormal 残留而导致后续同步点判定异常（requiredPlayers 为空 → 不广播）。
        // 只清理调用方自己（caller），不清理其他玩家（比 WillBroadcastHere 更保守）。
        lock (room)
        {
            var caller = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
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
                .Where(p => p.ConnectionId != Context.ConnectionId
                            && !p.IsAbnormal
                            && DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromMinutes(2))
                .Select(p => p.CurrentProgress)
                .ToList();
            releaseLaggingCaller = LaggingMemberReleaseDecisions.ShouldReleaseLaggingCaller(syncProgress, others);
        }
        if (SyncReplayDecisions.ShouldReplayAllArrived(alreadyBroadcasted))
        {
            _logger.LogInformation("[WaitForAllPlayers] 该同步点本轮已放行，补发 AllArrived 给晚到调用方: 房间={RoomCode}, 同步点={SyncId}, 连接={ConnId}",
                roomCode, syncId, Context.ConnectionId);
            await Clients.Caller.SendAsync("AllArrived", syncId);
            // 补发后仍继续走全量重评估（幂等：不改 BroadcastedSyncIds 状态），
            // 保证其他历史 syncId 的放行不被跳过。
        }
        else if (releaseLaggingCaller)
        {
            // 孤立落后者补发（方案 B）：该 syncId 未被全员广播过（alreadyBroadcasted=false），
            // 但 caller 已严格落后所有其他在线玩家，它们不会再以此 syncId 到达 → ArrivalSet 永不齐。
            // 直接对 caller 补发 AllArrived（等价"你落后了，别等了，放你走"），避免死等满 120s。
            // 严格小于(<)防误放：进度相等的正常碰头玩家不会触发本分支（ShouldReleaseLaggingCaller 返回 false）。
            // 仅对 caller 补发，不改 BroadcastedSyncIds、不动其他玩家、不清 ArrivalSet（幂等、不影响后续到齐路径）。
            _logger.LogInformation("[WaitForAllPlayers] caller 为孤立落后者，补发 AllArrived 放行: 房间={RoomCode}, 同步点={SyncId}, 进度={Progress}, 连接={ConnId}",
                roomCode, syncId, syncProgress, Context.ConnectionId);
            await Clients.Caller.SendAsync("AllArrived", syncId);
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
            await Clients.Group(roomCode).SendAsync("AllArrived", sid);
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
        await Clients.Group(roomCode).SendAsync("PlayerListUpdated", room.Players);

        // === 集体卡死监测 piggyback（multiplayer-mutual-wait-collective-skip §8.4 改动 1）===
        await EvaluateCollectiveStuckPiggybackAsync(room, roomCode);
    }

    /// <summary>
    /// 在 lock(room) 内枚举 room.ArrivalSets，收集所有满足 ShouldBroadcastAllArrived=true 的 (syncId, syncProgress) 对。
    /// syncProgress 推断规则（与 MemberStatusChanged 现有推断保持一致）：
    ///   syncId == "route_sync_done" → -1（全局同步点，按"等所有"处理）
    ///   其他 → 已到达玩家中 CurrentProgress 的最大值（>=-1）
    /// 调用方负责在 lock 外执行 SendAsync + ClearArrivalSet（避免 await 持锁）。
    /// </summary>
    private List<(string syncId, long progress)> CollectSatisfiedSyncsLocked(Room room)
    {
        var result = new List<(string, long)>();
        foreach (var kvp in room.ArrivalSets)
        {
            var sid = kvp.Key;
            var arrivals = kvp.Value;
            long sp = -1;
            if (sid != "route_sync_done")
            {
                sp = room.Players
                    .Where(p => arrivals.Contains(p.ConnectionId))
                    .Select(p => p.CurrentProgress)
                    .DefaultIfEmpty(-1)
                    .Max();
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
    private bool ShouldBroadcastAllArrived(Room room, string syncId, HashSet<string> arrivals, long syncProgress)
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
                sp = onlinePlayers
                    .Where(p => kv.Value.Contains(p.ConnectionId))
                    .Select(p => p.CurrentProgress)
                    .DefaultIfEmpty(-1)
                    .Max();
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
                room.ConsecutiveCollectiveSkipCount += 1;
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
                await Clients.Group(roomCode).SendAsync("AllArrived", sid);
                _roomManager.ClearArrivalSet(roomCode, sid);
                lock (room) { room.BroadcastedSyncIds.Add(sid); }   // fastsync-claim-short-circuit-premature-release-fix: 记录本轮已广播，供晚到抢报方补发
            }

            // ② 后 RequestSkipToProgress：让落后玩家神像跳段
            if (laggingPlayerConnIds.Count > 0)
            {
                _logger.LogWarning("[CollectiveSkip] 广播 RequestSkipToProgress: 房间={RoomCode}, target={Target}, 落后玩家数={N}",
                    roomCode, targetProgress, laggingPlayerConnIds.Count);
                await Clients.Group(roomCode).SendAsync("RequestSkipToProgress", targetProgress);
            }

            // ③ 降级广播（OQ-5 A）
            if (degraded)
            {
                _logger.LogError("[CollectiveSkip] 连续 {N} 次集体跳段，触发降级",
                    room.ConsecutiveCollectiveSkipCount);
                await Clients.Group(roomCode).SendAsync("CollectiveSkipDegraded", "ConsecutiveCollectiveSkipExceeded");
            }
        }
        catch (Exception ex)
        {
            // 广播失败不应让 Timer 回调崩溃；记录日志并放弃本次广播
            _logger.LogError(ex, "[CollectiveSkip] Timer 回调 lock 外广播失败，房间={RoomCode}", roomCode);
        }
    }

    /// <summary>计算多份路线清单的差异文件名列表</summary>
    internal static List<string> ComputeRouteDiff(List<List<RouteHash>> allReports)
    {
        if (allReports.Count == 0) return [];

        var diffFiles = new HashSet<string>();

        // 收集所有文件名
        var allFileNames = allReports
            .SelectMany(r => r.Select(h => h.FileName))
            .ToHashSet();

        foreach (var fileName in allFileNames)
        {
            var md5Values = allReports
                .Select(r => r.FirstOrDefault(h => h.FileName == fileName)?.Md5)
                .ToList();

            // 有任何一份缺失或 MD5 不同则标记为差异
            if (md5Values.Any(m => m == null) || md5Values.Distinct().Count() > 1)
                diffFiles.Add(fileName);
        }

        return [.. diffFiles];
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

        _logger.LogError("重试耗尽, 放弃本轮次, generation={Gen}, 未确认成员={Uids}",
            generation, string.Join(",", _roomManager.GetUnconfirmedUids(group, targetUids)));
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