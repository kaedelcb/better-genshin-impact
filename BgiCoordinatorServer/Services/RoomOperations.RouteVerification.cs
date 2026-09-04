using System.Collections.Concurrent;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 路线验证族（自 CoordinatorHub 逐字搬迁：ReportRouteList/ReportRouteVariantSchema/
/// ReportRouteVerificationDone，及 EvaluateVariantSchemaAsync/OnVariantSchemaTimeoutAsync/
/// SyncPointListEquals/TeleportSeqEquals/ComputeRouteDiff 私有辅助与三个静态缓存）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// </summary>
public sealed partial class RoomOperations
{
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

    /// <summary>上报路线清单，所有成员上报后对比 MD5，广播差异或验证通过</summary>
    public async Task ReportRouteListAsync(GatewayHandlerContext ctx, List<RouteHash> routes)
    {
        _logger.LogInformation("[ReportRouteList] 连接 {ConnId} 上报路线清单，共 {Count} 条", ctx.ConnectionId, routes?.Count ?? 0);
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[ReportRouteList] 连接 {ConnId} 未在任何房间中，忽略路线上报", ctx.ConnectionId);
            return;
        }
        _logger.LogInformation("[ReportRouteList] 连接 {ConnId} 在房间 {Code} 中上报路线", ctx.ConnectionId, roomCode);
        ObservePhase(room, roomCode, "route.reportList", routeReportActivity: true);

        var roomReports = RouteReports.GetOrAdd(roomCode, _ => new ConcurrentDictionary<string, List<RouteHash>>());
        roomReports[ctx.ConnectionId] = routes;

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
                await _broadcaster.BroadcastGroupAsync(roomCode, "RouteVerificationPassed", null);
            }
            else
            {
                _logger.LogWarning("房间 {Code} 路线存在差异：{Files}", roomCode, string.Join(", ", diffFiles));
                await _broadcaster.BroadcastGroupAsync(roomCode, "RouteDiffReceived", new { diffFiles }, diffFiles);
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
                await _broadcaster.BroadcastGroupAsync(
                    roomCode,
                    "RouteDiffReceived",
                    new { diffFiles = new List<string> { "__route_verification_error__" } },
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
    public async Task ReportRouteVariantSchemaAsync(GatewayHandlerContext ctx, List<RouteVariantSchemaItem> items)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[变体校验] 连接 {ConnId} 未在房间内", ctx.ConnectionId);
            return;
        }
        ObservePhase(room, roomCode, "route.reportVariantSchema", routeReportActivity: true);

        items ??= new List<RouteVariantSchemaItem>();
        var roomReports = VariantSchemaReports.GetOrAdd(roomCode,
            _ => new ConcurrentDictionary<string, List<RouteVariantSchemaItem>>());
        roomReports[ctx.ConnectionId] = items;

        _logger.LogInformation("[变体校验] 连接 {ConnId} 在房间 {Code} 上报 {Count} 条 schema（含非空 LogicalRouteId {NonEmpty} 条）",
            ctx.ConnectionId, roomCode, items.Count, items.Count(i => !string.IsNullOrEmpty(i.LogicalRouteId)));

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
            await _broadcaster.BroadcastGroupAsync(roomCode, "RouteVariantConsistencyPassed", null);
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
                    await _broadcaster.BroadcastGroupAsync(
                        roomCode, "RouteVariantConsistencyFailed",
                        new { logicalId, playerItems }, logicalId, playerItems);
                    VariantSchemaReports.TryRemove(roomCode, out _);
                    return;
                }
            }
        }

        _logger.LogInformation("[变体校验] 房间 {Code} 通过（{Count} 个 LogicalRouteId 分组）",
            roomCode, groupedByLogicalId.Count);
        await _broadcaster.BroadcastGroupAsync(roomCode, "RouteVariantConsistencyPassed", null);
        VariantSchemaReports.TryRemove(roomCode, out _);
    }

    private async Task OnVariantSchemaTimeoutAsync(string roomCode)
    {
        if (!VariantSchemaReports.TryRemove(roomCode, out _)) return;
        VariantSchemaTimeouts.TryRemove(roomCode, out var cts);
        cts?.Dispose();
        _logger.LogWarning("[变体校验] 房间 {Code} 30s 上报超时，广播 Failed", roomCode);
        await _broadcaster.BroadcastGroupAsync(
            roomCode,
            "RouteVariantConsistencyFailed",
            new { logicalId = "", playerItems = new Dictionary<string, RouteVariantSchemaItem>() },
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

    /// <summary>上报路线验证完成，全员完成时广播 RouteVerificationAllDone</summary>
    public async Task ReportRouteVerificationDoneAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "route.reportVerificationDone");

        // 更新心跳确保玩家状态为在线
        _roomManager.UpdateHeartbeat(ctx.ConnectionId);

        var allDone = _roomManager.RecordRouteVerificationDone(roomCode, ctx.ConnectionId);

        if (allDone)
        {
            _logger.LogInformation("房间 {Code} 路线验证全员完成", roomCode);
            await _broadcaster.BroadcastGroupAsync(roomCode, "RouteVerificationAllDone", null);
        }
        else
        {
            // 记录当前状态用于调试
            var (onlineCount, reportedCount) = _roomManager.GetRouteVerificationStatus(roomCode);
            _logger.LogDebug("房间 {Code} 路线验证进度: {Reported}/{Online}", roomCode, reportedCount, onlineCount);
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
}
