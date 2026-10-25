using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>管理员页面专用快照；只复制展示字段，不返回房间对象或控制命令。</summary>
public sealed record RoomMonitorSnapshot(
    DateTime CapturedAtUtc,
    int TotalRoomCount,
    int StartedRoomCount,
    int OnlineMemberCount,
    int DisconnectedRoomCount,
    List<RoomMonitorRoom> Rooms,
    int ConnectedControlRoomCount,
    int ConnectedControlMemberCount,
    List<RoomMonitorControlRoom> ControlRooms)
{
    public static RoomMonitorSnapshot Create(RoomManager manager, DateTime utcNow, TimeSpan onlineTimeout,
        RoomHoeingHistory? history = null)
    {
        // 助手先加入控制房间；游戏锄地房间通常到真正开锄时才创建。
        var controlRooms = manager.GetControlRoomMonitorRooms()
            .Where(room => room.OnlineCount > 0)
            .ToList();
        if (history is not null)
        {
            // 今日发生过锄地的房间离线或服务重启后仍可查询，不依赖当前成员对象存活。
            foreach (var code in history.GetTodayRoomCodes(utcNow))
                if (controlRooms.All(room => room.Code != code))
                    controlRooms.Add(new RoomMonitorControlRoom(code, 0, 0, false, null, []));
            controlRooms = controlRooms.Select(room =>
            {
                var day = history.ReadToday(room.Code, utcNow);
                if (room.Members.Count == 0)
                {
                    var members = day.Runs.SelectMany(run => run.Members)
                        .GroupBy(member => member.Uid, StringComparer.Ordinal)
                        .Select(group => group.Last())
                        .Select(member => new RoomMonitorControlMember(member.Name, member.Uid, false,
                            false, null, null, null)).ToList();
                    room = room with { MemberCount = members.Count, Members = members };
                }
                return room with { TodayHoeing = day };
            }).ToList();
        }
        controlRooms = controlRooms.OrderByDescending(room => room.OnlineCount > 0)
            .ThenBy(room => room.Code, StringComparer.Ordinal).ToList();
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
            rooms,
            controlRooms.Count(room => room.OnlineCount > 0),
            controlRooms.Sum(room => room.OnlineCount),
            controlRooms);
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

public sealed record RoomMonitorControlRoom(
    string Code,
    int MemberCount,
    int OnlineCount,
    bool AutoHoeingRunning,
    DateTime? LastHeartbeatUtc,
    List<RoomMonitorControlMember> Members)
{
    public RoomHoeingDay? TodayHoeing { get; init; }
}

public sealed record RoomMonitorControlMember(
    string Name,
    string Uid,
    bool Online,
    bool AutoHoeingRunning,
    string? RouteDisplay,
    string? Progress,
    DateTime? LastHeartbeatUtc);
