using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public class HeartbeatMonitor : IHostedService, IDisposable
{
    private readonly RoomManager _roomManager;
    private readonly GatewayBroadcaster _broadcaster;
    private readonly RoomPhaseObserver _phaseObserver;
    private readonly ILogger<HeartbeatMonitor> _logger;
    private Timer? _timer;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(30);
    /// <summary>重对齐超时时间（30秒）</summary>
    private static readonly TimeSpan RealignTimeout = TimeSpan.FromSeconds(30);

    public HeartbeatMonitor(
        RoomManager roomManager,
        GatewayBroadcaster broadcaster,
        RoomPhaseObserver phaseObserver,
        ILogger<HeartbeatMonitor> logger)
    {
        _roomManager = roomManager;
        _broadcaster = broadcaster;
        _phaseObserver = phaseObserver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HeartbeatMonitor 启动，扫描间隔 {Interval}s，超时阈值 {Timeout}s",
            ScanInterval.TotalSeconds, PlayerTimeout.TotalSeconds);
        _timer = new Timer(Scan, null, ScanInterval, ScanInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HeartbeatMonitor 停止");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void Scan(object? state)
    {
        try
        {
            // 1. 检查重对齐超时（必须在 RemoveDeadPlayers 之前，因为需要访问完整玩家列表）
            CheckRealignTimeout();
            
            // 2. 检查线路强制同步（multiplayer-route-enforcement spec）
            CheckRouteEnforcement();
            
            // 3. 检查玩家心跳超时
            var affectedRooms = _roomManager.RemoveDeadPlayers(PlayerTimeout);
            foreach (var roomCode in affectedRooms)
            {
                var removedPlayers = _roomManager.GetLastRemovedPlayers(roomCode);
                var room = _roomManager.GetRoom(roomCode);

                foreach (var removedPlayer in removedPlayers)
                {
                    // Check if the removed player was the host
                    if (room != null && removedPlayer.ConnectionId == room.HostConnectionId)
                    {
                        // Host was removed: broadcast RoomClosed and delete the room
                        _logger.LogWarning("房间 {RoomCode} 房主 {PlayerName} 心跳超时，关闭房间",
                            roomCode, removedPlayer.PlayerName);

                        _phaseObserver.Mark(roomCode, RoomPhase.Ended, "heartbeat.hostTimeout");
                        _phaseObserver.Forget(roomCode);
                        _ = _broadcaster.BroadcastGroupAsync(roomCode, "RoomClosed",
                            new { reason = "房主心跳超时" }, "房主心跳超时");

                        _roomManager.DeleteRoom(roomCode);
                        break; // Room is deleted, no need to process remaining removed players
                    }
                    else
                    {
                        // Member was removed: broadcast MemberStatusChanged
                        _logger.LogWarning("房间 {RoomCode} 成员 {PlayerName}({Uid}) 心跳超时，标记离线",
                            roomCode, removedPlayer.PlayerName, removedPlayer.PlayerUid);

                        _ = _broadcaster.BroadcastGroupAsync(roomCode, "MemberStatusChanged",
                            new { playerUid = removedPlayer.PlayerUid, status = "Offline", targetProgress = long.MaxValue },
                            removedPlayer.PlayerUid, "Offline", long.MaxValue);
                    }
                }

                // Still send PlayerListUpdated for remaining players
                if (room != null)
                {
                    var players = room.Players ?? [];
                    _logger.LogInformation("房间 {RoomCode} 有玩家超时断线，当前剩余 {Count} 人", roomCode, players.Count);

                    _ = _broadcaster.BroadcastGroupAsync(roomCode, "PlayerListUpdated",
                        new { players }, players);
                }
            }

            // 4. 宽限期到期清理（resilience-framework §2d）
            foreach (var (graceRoom, graceRoomCode) in _roomManager.GetAllRoomsWithCodes())
            {
                List<string> expired;
                lock (graceRoom)
                {
                    expired = graceRoom.GracePendingMembers
                        .Where(kvp => kvp.Value < DateTime.UtcNow)
                        .Select(kvp => kvp.Key)
                        .ToList();
                }

                foreach (var connId in expired)
                {
                    lock (graceRoom)
                    {
                        graceRoom.GracePendingMembers.Remove(connId);
                        var player = graceRoom.Players.FirstOrDefault(p => p.ConnectionId == connId);
                        if (player != null)
                        {
                            graceRoom.Players.Remove(player);
                            // 清理该连接在各同步集合中的记录
                            foreach (var set in graceRoom.ArrivalSets.Values)
                                set.Remove(connId);
                            foreach (var set in graceRoom.FightDoneSets.Values)
                                set.Remove(connId);
                            foreach (var set in graceRoom.FightParticipantSets.Values)
                                set.Remove(connId);
                            graceRoom.WorldJoinedSet.Remove(connId);
                            graceRoom.RouteVerificationDoneSet.Remove(connId);

                            _logger.LogWarning("房间 {RoomCode} 成员 {PlayerName}({Uid}) 宽限期满未重连，删除",
                                graceRoomCode, player.PlayerName, player.PlayerUid);

                            _ = _broadcaster.BroadcastGroupAsync(graceRoomCode, "MemberStatusChanged",
                                new { playerUid = player.PlayerUid, status = "Offline", targetProgress = long.MaxValue },
                                player.PlayerUid, "Offline", long.MaxValue);
                            _ = _broadcaster.BroadcastGroupAsync(graceRoomCode, "PlayerListUpdated",
                                new { players = graceRoom.Players }, graceRoom.Players);
                        }
                    }
                }
            }
        // 5. 检查控制房间上线状态过期
            CheckControlRoomTimeout();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HeartbeatMonitor 扫描时发生异常");
        }
    }
    
    /// <summary>
    /// 检查重对齐超时（每10秒执行一次）
    /// </summary>
    private void CheckRealignTimeout()
    {
        try
        {
            foreach (var (room, roomCode) in _roomManager.GetAllRoomsWithCodes())
            {
                var process = room.CurrentRealignProcess;
                if (process == null || process.IsCompleted)
                    continue;
                
                // 检查是否超时（30秒）
                if ((DateTime.UtcNow - process.BroadcastTime).TotalSeconds > RealignTimeout.TotalSeconds)
                {
                    lock (room)
                    {
                        if (process.IsCompleted) return; // 双重检查
                        
                        // 标记未响应的玩家为异常
                        var onlinePlayers = room.Players
                            .Where(p => (DateTime.UtcNow - p.LastHeartbeat) <= TimeSpan.FromSeconds(30))
                            .ToList();
                        
                        var notReadyPlayers = onlinePlayers
                            .Where(p => !process.ReadyPlayers.Contains(p.PlayerUid))
                            .ToList();
                        
                        foreach (var player in notReadyPlayers)
                        {
                            player.IsAbnormal = true; // 标记为异常
                            _logger.LogWarning("[CheckRealignTimeout] 玩家{Player}未响应重对齐，标记为异常", player.PlayerName);
                        }
                        
                        // 广播 StartRoute
                        process.IsCompleted = true;
                        _logger.LogWarning("[CheckRealignTimeout] 房间{RoomCode}重对齐超时，广播 StartRoute，目标路线={Target}",
                            roomCode, process.TargetRouteIndex);
                        
                        _ = _broadcaster.BroadcastGroupAsync(roomCode, "StartRoute",
                            new { routeIndex = process.TargetRouteIndex }, process.TargetRouteIndex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckRealignTimeout 扫描时发生异常");
        }
    }
    
    /// <summary>
    /// 检查线路强制同步（每10秒执行一次）
    /// 检测所有玩家的线路偏差，超过阈值时强制同步（multiplayer-route-enforcement spec）
    /// </summary>
    private void CheckRouteEnforcement()
    {
        try
        {
            foreach (var (room, roomCode) in _roomManager.GetAllRoomsWithCodes())
            {
                // 跳过未启用强制同步的房间
                if (!room.RouteEnforcementEnabled)
                    continue;
                
                // 跳过有重对齐流程进行中的房间（避免冲突）
                if (room.CurrentRealignProcess != null && !room.CurrentRealignProcess.IsCompleted)
                    continue;
                
                List<PlayerInfo> activePlayers;
                lock (room)
                {
                    // 获取活跃玩家（排除异常状态和心跳超时）
                    activePlayers = room.Players
                        .Where(p => !p.IsAbnormal)
                        .Where(p => (DateTime.UtcNow - p.LastHeartbeat) <= TimeSpan.FromSeconds(30))
                        .Where(p => p.CurrentRouteIndex >= 0)
                        .ToList();
                }
                
                if (activePlayers.Count < 2)
                    continue; // 少于2人不需要检测
                
                var routeIndices = activePlayers.Select(p => p.CurrentRouteIndex).ToList();
                var maxRoute = routeIndices.Max();
                var minRoute = routeIndices.Min();
                var deviation = maxRoute - minRoute;
                
                if (deviation > room.RouteEnforcementThreshold)
                {
                    // 线路偏差超过阈值，广播强制同步
                    _logger.LogWarning("[RouteEnforcement] 房间{RoomCode}检测到线路偏差: min={Min}, max={Max}, deviation={Dev}, threshold={Threshold}",
                        roomCode, minRoute, maxRoute, deviation, room.RouteEnforcementThreshold);
                    
                    var deviationInfo = activePlayers
                        .Select(p => $"{p.PlayerName}:{p.CurrentRouteIndex}")
                        .ToList();
                    
                    _logger.LogInformation("[RouteEnforcement] 房间{RoomCode}广播 RouteEnforceSync，目标线路={Target}",
                        roomCode, minRoute);
                    
                    _ = _broadcaster.BroadcastGroupAsync(roomCode, "RouteEnforceSync",
                        new { minRoute, reason = "线路偏差检测", deviationInfo },
                        minRoute, "线路偏差检测", deviationInfo);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckRouteEnforcement 扫描时发生异常");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    /// <summary>检查控制房间上线状态过期（30 分钟超时），过期后自动重置上线状态。R-1 方案 C。</summary>
    private void CheckControlRoomTimeout()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
            var groups = _roomManager.GetAllControlRoomGroups();
            foreach (var group in groups)
            {
                var players = _roomManager.GetControlRoomPlayers(group);
                foreach (var player in players)
                {
                    if (player.OnlineReady && player.OnlineReadyExpireTime < DateTime.UtcNow
                        && player.OnlineReadyExpireTime != DateTime.MinValue)
                    {
                        _logger.LogWarning("控制房间 {Group} 成员 {PlayerName}({Uid}) 上线状态已过期（30分钟），自动重置",
                            group, player.PlayerName, player.PlayerUid);
                        // 标记上线状态过期
                        player.OnlineReady = false;
                        player.OnlineReadyExpireTime = DateTime.MinValue;
                        // 同步重置上线事件代序号（边沿检测：过期后下次重新上线才算新事件)
                        player.OnlineEventGeneration = 0;
                        player.OnlineEventConsumed = true;
                        player.OnlineEventTime = DateTime.MinValue;
                    }

                    // TaskRunning 超时自愈：任务运行态超过 TaskRunningTimeoutSec 未续期 → 复位为未运行（防崩溃/断电残留）
                    if (TaskRunningExpiryDecisions.ShouldResetTaskRunning(
                            player.TaskRunning,
                            DateTime.UtcNow.Ticks,
                            player.TaskRunningExpireTime.Ticks))
                    {
                        _logger.LogWarning("控制房间 {Group} 成员 {PlayerName}({Uid}) 任务运行态已超时，自动复位为未运行",
                            group, player.PlayerName, player.PlayerUid);
                        player.TaskRunning = false;
                        player.CurrentTaskName = null;
                        player.TaskRunningExpireTime = DateTime.MinValue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckControlRoomTimeout 扫描时发生异常");
        }
    }
}
