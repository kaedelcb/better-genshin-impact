using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 房间查询族（自 CoordinatorHub 逐字搬迁：GetOnlineRooms/GetRoomConfig/GetRoundHostOrder/
/// IsHostReady/IsHostRouteListUploaded/GetHostRouteList/GetHostRouteListStatus/
/// GetWorldJoinedCount/GetMemberProgress）。仅做 ctx 参数化，业务逻辑不变。
/// 只读查询，按纪律不做 Phase 观测。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>获取在线房间列表</summary>
    public Task<List<RoomSummary>> GetOnlineRoomsAsync(GatewayHandlerContext ctx)
    {
        return Task.FromResult(_roomManager.GetOnlineRooms());
    }

    /// <summary>成员拉取房主锄地配置</summary>
    public Task<RoomConfig?> GetRoomConfigAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        return Task.FromResult(room?.HostConfig);
    }

    /// <summary>返回本房间权威轮换序列（UID 列表）。未生成 / 房间不存在 → 空列表。</summary>
    public Task<List<string>> GetRoundHostOrderAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null) return Task.FromResult(new List<string>());
        lock (room) { return Task.FromResult(new List<string>(room.RoundHostOrder)); }
    }

    /// <summary>查询房主是否就绪</summary>
    public Task<bool> IsHostReadyAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        return Task.FromResult(room?.HostReady ?? false);
    }

    /// <summary>
    /// 查询房主是否已上传过路线列表（含上传空列表）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：成员据此区分
    /// "房主从未上传"（false → 继续等待）与"房主上传了空列表"（true + 列表空 → 优雅跳过本轮）。
    /// </summary>
    public Task<bool> IsHostRouteListUploadedAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        return Task.FromResult(room?.HostRouteListUploaded ?? false);
    }

    /// <summary>成员拉取房主路线列表</summary>
    public Task<List<string>> GetHostRouteListAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        return Task.FromResult(room?.HostRouteList ?? []);
    }

    /// <summary>
    /// 原子返回房主路线列表状态：(Uploaded, RouteNames) 同一时刻快照。
    /// multiplayer-member-skip-round-stuck-roundend-sync-fix：取代成员侧
    /// GetHostRouteList + IsHostRouteListUploaded 两次独立查询，消除 TOCTOU 竞态
    /// （房主在两次查询之间 SetHostRouteList(非空) 导致成员拿到 uploaded=true+count=0 误判跳过）。
    /// lock(room) 与 SetHostRouteList 写侧互斥，并复制列表快照，确保读到的 Uploaded 与 RouteNames
    /// 来自同一时刻、且返回后不被房主并发改动。
    /// </summary>
    public Task<HostRouteListStatus> GetHostRouteListStatusAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
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

    /// <summary>获取已加入世界的人数</summary>
    public Task<int> GetWorldJoinedCountAsync(GatewayHandlerContext ctx)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return Task.FromResult(0);
        return Task.FromResult(_roomManager.GetWorldJoinedCount(roomCode));
    }

    /// <summary>查询指定成员的路线进度（需求 6）</summary>
    public Task<MemberProgress?> GetMemberProgressAsync(GatewayHandlerContext ctx, string playerUid)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
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
}
