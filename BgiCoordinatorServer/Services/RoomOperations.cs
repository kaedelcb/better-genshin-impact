using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 房间操作方法集（《通信方案》§4.7 兼容层的共享业务路径）。
/// 旧 CoordinatorHub 的 65 个公开方法体逐个搬迁至此（按族分 partial 文件），
/// 旧 Hub 瘦身为 3-5 行强类型转发器、GatewayHub 路由经信封适配后——
/// 两者调用同一组方法，单一事实源。
///
/// 搬迁纪律（行为零变化）：
/// 1. 方法体逐字搬迁，仅把 Context.ConnectionId 替换为 ctx.ConnectionId、
///    Clients.Xxx 替换为 _broadcaster 等价调用、Groups.Xxx 替换为 _broadcaster.AddToGroupAsync 等。
/// 2. 原广播一律走 _broadcaster 双发（旧名 → /hub，evt → /gateway）。
/// 3. 原日志逐字保留；新增观测日志只允许加、不允许改旧行。
/// 4. 每个方法入口做 RoomPhase 观测（只推导 + 日志，不驱动）。
/// </summary>
public sealed partial class RoomOperations
{
    private readonly RoomManager _roomManager;
    private readonly ILogger<RoomOperations> _logger;
    private readonly GatewayBroadcaster _broadcaster;
    private readonly RoomPhaseObserver _phaseObserver;

    public RoomOperations(
        RoomManager roomManager,
        ILogger<RoomOperations> logger,
        GatewayBroadcaster broadcaster,
        RoomPhaseObserver phaseObserver)
    {
        _roomManager = roomManager;
        _logger = logger;
        _broadcaster = broadcaster;
        _phaseObserver = phaseObserver;
    }

    // ====== 连接 Group 跟踪（自 CoordinatorHub 搬迁，语义不变）======

    /// <summary>每个连接当前所属的 SignalR Group 列表（用于轮换房间时清理旧 Group 订阅，
    /// 避免上一个房间关闭/广播时串扰到已切换到新房间的连接）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>>
        _connectionGroups = new();

    /// <summary>
    /// 把当前连接从所有旧 Group 中移除，确保后续广播不会串扰到这个连接。
    /// 多世界轮次切换时，玩家会从旧房间切到新房间，必须先离开旧 Group。
    /// </summary>
    private async Task LeaveAllGroupsAsync(GatewayHandlerContext ctx, string? excludeGroup = null)
    {
        if (!_connectionGroups.TryGetValue(ctx.ConnectionId, out var groups))
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
                await _broadcaster.RemoveFromGroupAsync(ctx, g);
                lock (groups) { groups.Remove(g); }
                _logger.LogInformation("[GroupCleanup] 连接 {ConnId} 从旧 Group {Group} 移除",
                    ctx.ConnectionId, g);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GroupCleanup] 连接 {ConnId} 离开 Group {Group} 失败（忽略）",
                    ctx.ConnectionId, g);
            }
        }
    }

    /// <summary>记录某连接已加入指定 Group，供 LeaveAllGroupsAsync 后续清理使用。</summary>
    private void TrackGroup(GatewayHandlerContext ctx, string groupName)
    {
        var set = _connectionGroups.GetOrAdd(ctx.ConnectionId, _ => new HashSet<string>());
        lock (set) { set.Add(groupName); }
    }

    /// <summary>记录某连接已离开指定 Group。</summary>
    private void UntrackGroup(GatewayHandlerContext ctx, string groupName)
    {
        if (_connectionGroups.TryGetValue(ctx.ConnectionId, out var set))
        {
            lock (set) { set.Remove(groupName); }
        }
    }

    // ====== RoomPhase 观测（《审核方案》§7.1.1：只推导 + 日志，不驱动）======

    /// <summary>
    /// 操作方法入口的 Phase 观测。routeReportActivity 由调用方从本族路线上报缓存取样。
    /// 观测失败绝不冒泡影响业务（纯日志设施）。
    /// </summary>
    private void ObservePhase(Room? room, string? roomCode, string trigger, bool routeReportActivity = false)
    {
        if (room == null || roomCode == null) return;
        try
        {
            _phaseObserver.Observe(room, roomCode, trigger, routeReportActivity);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[RoomPhase] 观测异常（已吞掉，不影响业务）：房间 {Code} 触发 {Trigger}", roomCode, trigger);
        }
    }
}
