using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 房间配置族（自 CoordinatorHub 逐字搬迁：SetRoomConfig/UpdateWhitelist/
/// SetHostRouteList/ReportHostReady）。仅做 ctx 参数化与双发改造，业务逻辑不变。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>房主上传锄地配置</summary>
    public Task SetRoomConfigAsync(GatewayHandlerContext ctx, RoomConfig config)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room != null && room.HostConnectionId == ctx.ConnectionId)
        {
            room.HostConfig = config;
            _logger.LogInformation("房间 {Code} 房主配置已更新", roomCode);
        }
        return Task.CompletedTask;
    }

    /// <summary>更新白名单（仅房主）</summary>
    public Task UpdateWhitelistAsync(GatewayHandlerContext ctx, List<string>? whitelist = null)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.CompletedTask;

        if (room.HostConnectionId != ctx.ConnectionId)
        {
            _logger.LogWarning("[UpdateWhitelist] 连接 {ConnId} 不是房主，忽略", ctx.ConnectionId);
            return Task.CompletedTask;
        }

        _roomManager.UpdateWhitelist(roomCode, whitelist ?? []);
        _logger.LogInformation("[UpdateWhitelist] 房间 {Code} 白名单已更新", roomCode);
        return Task.CompletedTask;
    }

    /// <summary>房主上传最终路线列表，并广播通知成员</summary>
    public async Task SetHostRouteListAsync(GatewayHandlerContext ctx, List<string> routeNames)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room != null && room.HostConnectionId == ctx.ConnectionId)
        {
            // lock(room)：与 GetHostRouteListStatus 读侧互斥，保证 (HostRouteList, HostRouteListUploaded)
            // 两字段的写入对读侧表现为单一原子快照（multiplayer-member-skip-round-stuck-roundend-sync-fix）。
            lock (room)
            {
                room.HostRouteList = routeNames;
                room.HostRouteListUploaded = true;
            }
            _logger.LogInformation("房间 {Code} 房主路线列表已上传，共 {Count} 条", roomCode, routeNames.Count);
            ObservePhase(room, roomCode, "room.setHostRouteList");
            // 广播通知成员路线列表已就绪
            await _broadcaster.BroadcastGroupAsync(roomCode, "HostRouteListReady", new { routeNames }, routeNames);
        }
    }

    /// <summary>房主上报已进入等待状态</summary>
    public async Task ReportHostReadyAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room != null && roomCode != null && room.HostConnectionId == ctx.ConnectionId)
        {
            room.HostReady = true;
            _logger.LogInformation("房间 {Code} 房主已就绪", roomCode);
            ObservePhase(room, roomCode, "room.reportHostReady");
            await _broadcaster.BroadcastGroupAsync(roomCode, "HostReadyChanged", new { ready = true }, true);
        }
    }
}
