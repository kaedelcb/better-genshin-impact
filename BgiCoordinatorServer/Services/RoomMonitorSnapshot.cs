using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>管理员页面专用快照；只复制展示字段，不返回房间对象或控制命令。</summary>
public sealed record RoomMonitorSnapshot(
    DateTime CapturedAtUtc,
    int TotalRoomCount,
    int StartedRoomCount,
    int OnlineMemberCount,
    int DisconnectedRoomCount,
    List<RoomMonitorRoom> Rooms)
{
    public static RoomMonitorSnapshot Create(RoomManager manager, DateTime utcNow, TimeSpan onlineTimeout)
    {
        var rooms = new List<RoomMonitorRoom>();
        foreach (var (room, code) in manager.GetAllRoomsWithCodes())
        {
            lock (room)
            {
                var totalRoutes = room.HostRouteListUploaded ? room.HostRouteList.Count : (int?)null;
                var members = room.Players.Select(player => CreateMember(room, player, utcNow, onlineTimeout, totalRoutes)).ToList();
                var onlineCount = members.Count(member => member.Online);
                var status = room.IsStarted
                    ? room.ExpCapBroadcasted ? "finished" : onlineCount > 0 ? "started" : "disconnected"
                    : "waiting";
                var lastHeartbeat = members
                    .Where(member => member.LastHeartbeatUtc.HasValue)
                    .Select(member => member.LastHeartbeatUtc!.Value)
                    .DefaultIfEmpty()
                    .Max();

                rooms.Add(new RoomMonitorRoom(
                    code,
                    room.Players.FirstOrDefault()?.PlayerName ?? "",
                    status,
                    room.Players.Count,
                    room.ExpectedPlayerCount,
                    onlineCount,
                    room.CreatedAt,
                    room.StartedAtUtc,
                    room.IsStarted ? Math.Max(1, room.CurrentWorldRound) : null,
                    totalRoutes,
                    lastHeartbeat == default ? null : lastHeartbeat,
                    members));
            }
        }

        rooms = rooms
            .OrderBy(room => room.Status switch { "started" => 0, "disconnected" => 1, "waiting" => 2, _ => 3 })
            .ThenByDescending(room => room.StartedAtUtc ?? room.CreatedAtUtc)
            .ToList();
        return new RoomMonitorSnapshot(
            utcNow,
            rooms.Count,
            rooms.Count(room => room.Status == "started"),
            rooms.Sum(room => room.OnlineCount),
            rooms.Count(room => room.Status == "disconnected"),
            rooms);
    }

    private static RoomMonitorMember CreateMember(Room room, PlayerInfo player, DateTime utcNow,
        TimeSpan onlineTimeout, int? totalRoutes)
    {
        var reconnecting = room.GracePendingMembers.ContainsKey(player.ConnectionId);
        var lastHeartbeat = player.LastHeartbeat == default ? (DateTime?)null : player.LastHeartbeat;
        var online = !reconnecting && lastHeartbeat.HasValue && utcNow - lastHeartbeat.Value <= onlineTimeout;
        int? routeNumber = online && totalRoutes > 0 && player.CurrentRouteIndex >= 0
            && player.CurrentRouteIndex < totalRoutes
                ? player.CurrentRouteIndex + 1
                : null;
        return new RoomMonitorMember(
            player.PlayerName,
            player.PlayerUid,
            online,
            reconnecting,
            player.Status.ToString(),
            routeNumber,
            lastHeartbeat);
    }
}

public sealed record RoomMonitorRoom(
    string Code,
    string HostName,
    string Status,
    int MemberCount,
    int ExpectedMemberCount,
    int OnlineCount,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    int? WorldRound,
    int? TotalRoutes,
    DateTime? LastHeartbeatUtc,
    List<RoomMonitorMember> Members);

public sealed record RoomMonitorMember(
    string Name,
    string Uid,
    bool Online,
    bool Reconnecting,
    string Status,
    int? RouteNumber,
    DateTime? LastHeartbeatUtc);
