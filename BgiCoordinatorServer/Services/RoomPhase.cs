using System.Collections.Concurrent;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>房间生命周期阶段（《审核方案》§7.1.1）。本切片只观测，不驱动任何行为。</summary>
public enum RoomPhase
{
    /// <summary>刚创建（CreateRoom 后，尚无玩家）。</summary>
    Idle,

    /// <summary>等待成员加入。</summary>
    WaitingForPlayers,

    /// <summary>路线验证中（ReportRouteList/VariantSchema 流动）。</summary>
    RouteVerifying,

    /// <summary>房主就绪（HostReady=true），等待开锄。</summary>
    ReadyToStart,

    /// <summary>MarkRoomStarted 后（IsStarted=true）。</summary>
    Running,

    /// <summary>多世界轮换中（ResetForNewWorldRound 窗口，显式标记）。</summary>
    RoundTransition,

    /// <summary>CloseRoom / 全员达经验上限。</summary>
    Ended,
}

/// <summary>
/// RoomPhase 推导规则（《审核方案》§7.1.1 转换矩阵的现状标志位组合）。
/// 纯函数、不持锁、无副作用；输入由调用方在 lock(room) 内取样。
/// </summary>
public static class RoomPhaseDecisions
{
    /// <summary>
    /// 从现状标志位推导 Phase。
    /// </summary>
    /// <param name="isStarted">room.IsStarted（MarkRoomStarted 后置 true）。</param>
    /// <param name="expCapBroadcasted">room.ExpCapBroadcasted（本轮已广播 AllReachedExpCap）。</param>
    /// <param name="hostReady">room.HostReady。</param>
    /// <param name="hostRouteListUploaded">room.HostRouteListUploaded。</param>
    /// <param name="routeVerificationAllDone">路线验证全员完成（RouteVerificationDoneSet 覆盖在线成员）。</param>
    /// <param name="routeReportActivity">是否观测到路线上报活动（RouteReports/VariantSchemaReports 缓存或验证集合非空）。</param>
    /// <param name="playerCount">room.Players.Count。</param>
    public static RoomPhase Derive(
        bool isStarted,
        bool expCapBroadcasted,
        bool hostReady,
        bool hostRouteListUploaded,
        bool routeVerificationAllDone,
        bool routeReportActivity,
        int playerCount)
    {
        // Running → Ended：AllReachedExpCap 已广播（CloseRoom 的 Ended 走显式标记，房间随即删除无法推导）
        if (isStarted) return expCapBroadcasted ? RoomPhase.Ended : RoomPhase.Running;
        // RouteVerifying → ReadyToStart：HostRouteListUploaded ∧ 路线验证完成 ∧ HostReady
        if (hostReady && hostRouteListUploaded && routeVerificationAllDone) return RoomPhase.ReadyToStart;
        // WaitingForPlayers → RouteVerifying：首个 ReportRouteList/VariantSchema 到达
        if (routeReportActivity) return RoomPhase.RouteVerifying;
        // Idle → WaitingForPlayers：CreateRoom 完成（有玩家在房）
        if (playerCount > 0) return RoomPhase.WaitingForPlayers;
        return RoomPhase.Idle;
    }
}

/// <summary>
/// RoomPhase 观测器（《审核方案》§7.1.1：只推导 + 日志，不驱动）。
/// 每个操作方法入口取样推导一次：Phase 变化打 Information，未变化打 Debug。
/// 观测状态（每房间上次 Phase）仅为去抖服务，绝不参与任何业务判定。
/// </summary>
public sealed class RoomPhaseObserver
{
    private readonly ConcurrentDictionary<string, RoomPhase> _lastPhaseByRoom = new(StringComparer.Ordinal);
    private readonly ILogger<RoomPhaseObserver> _logger;

    public RoomPhaseObserver(ILogger<RoomPhaseObserver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 在操作方法入口观测一次。routeReportActivity 由调用方从路线上报缓存取样（见 RouteReports 等）。
    /// </summary>
    public void Observe(Room room, string roomCode, string trigger, bool routeReportActivity)
    {
        RoomPhase phase;
        lock (room)
        {
            var onlineCount = room.Players.Count(p => !room.GracePendingMembers.ContainsKey(p.ConnectionId));
            phase = RoomPhaseDecisions.Derive(
                room.IsStarted,
                room.ExpCapBroadcasted,
                room.HostReady,
                room.HostRouteListUploaded,
                room.RouteVerificationDoneSet.Count > 0 && room.RouteVerificationDoneSet.Count >= onlineCount,
                routeReportActivity || room.RouteVerificationDoneSet.Count > 0,
                room.Players.Count);
        }
        Publish(roomCode, phase, trigger);
    }

    /// <summary>显式标记一个 Phase（用于无法从标志位推导的瞬态：RoundTransition、CloseRoom 的 Ended）。</summary>
    public void Mark(string roomCode, RoomPhase phase, string trigger) => Publish(roomCode, phase, trigger);

    /// <summary>房间销毁时清除观测记录（防字典泄漏）。</summary>
    public void Forget(string roomCode) => _lastPhaseByRoom.TryRemove(roomCode, out _);

    private void Publish(string roomCode, RoomPhase phase, string trigger)
    {
        var old = _lastPhaseByRoom.TryGetValue(roomCode, out var p) ? p : (RoomPhase?)null;
        if (old == phase)
        {
            _logger.LogDebug("[RoomPhase] 房间 {Code} 保持 {Phase}（触发：{Trigger}）", roomCode, phase, trigger);
            return;
        }
        _lastPhaseByRoom[roomCode] = phase;
        _logger.LogInformation("[RoomPhase] 房间 {Code} {Old} → {New}（触发：{Trigger}）",
            roomCode, old?.ToString() ?? "（首次观测）", phase, trigger);
    }
}
