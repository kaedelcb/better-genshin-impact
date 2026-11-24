using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public partial class RoomManager
{
    private static readonly TimeSpan HostWorldRetention = TimeSpan.FromMinutes(2);
    private const int MaxRetainedHostWorlds = 128;
    private readonly object _hostWorldArchiveLock = new();
    private readonly Dictionary<string, EndedHostWorld> _hostWorldArchive = new(StringComparer.Ordinal);

    private sealed record EndedHostWorld(string RoomCode, HostWorldSnapshot Snapshot,
        Dictionary<string, string> MemberUids, DateTime ExpiresUtc);

    // Call while holding the live room lock and before unpublishing it, so a current
    // member can reconcile a lost terminal response across a concurrent close.
    private void RetainHostWorldLocked(Room room)
    {
        if (room.StandbyNegotiation is not { } state || state.Snapshot.Phase == "regular") return;
        state.Abort("room_closed");
        lock (_hostWorldArchiveLock)
        {
            var now = DateTime.UtcNow;
            foreach (var key in _hostWorldArchive.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToArray())
                _hostWorldArchive.Remove(key);
            if (_hostWorldArchive.Count >= MaxRetainedHostWorlds)
                _hostWorldArchive.Remove(_hostWorldArchive.MinBy(p => p.Value.ExpiresUtc).Key);
            _hostWorldArchive[room.SessionId] = new(room.Code, state.Snapshot,
                room.Players.ToDictionary(p => p.ConnectionId, p => p.PlayerUid, StringComparer.Ordinal), now + HostWorldRetention);
        }
    }

    internal (HostWorldSnapshot? State, string? PlayerUid) ReadRetainedHostWorld(string roomCode, string sessionId,
        string connectionId)
    {
        lock (_hostWorldArchiveLock)
        {
            if (!_hostWorldArchive.TryGetValue(sessionId, out var ended) || ended.RoomCode != roomCode
                || ended.ExpiresUtc <= DateTime.UtcNow || !ended.MemberUids.TryGetValue(connectionId, out var uid))
                return (null, null);
            return (ended.Snapshot, uid);
        }
    }

    internal bool CanUseHostWorldRoomLocked(Room room, string connectionId) =>
        _rooms.TryGetValue(room.Code, out var current) && ReferenceEquals(current, room)
        && _connectionRoomMap.TryGetValue(connectionId, out var code) && code == room.Code
        && !room.GracePendingMembers.ContainsKey(connectionId);

    internal int GetLiveJoinedHostWorldCountLocked(Room room) => room.Players.Count(p =>
        DateTime.UtcNow - p.LastHeartbeat < TimeSpan.FromSeconds(30)
        && CanUseHostWorldRoomLocked(room, p.ConnectionId) && room.WorldJoinedSet.Contains(p.ConnectionId));

    private (bool Success, string? Error) JoinHostWorldLocked(Room room, string connectionId, string playerId,
        string playerName, string playerUid, string reportedVersion, bool hasCapability, string? standbySessionId, bool standbyOnly)
    {
        if (!hasCapability) return (false, "host_world_capability_required");
        if (!HostWorldExecution.IsValidUid(playerUid)) return (false, "host_world_uid_required");

        if (_connectionRoomMap.TryGetValue(connectionId, out var mapped) && mapped != room.Code)
            return (false, "host_world_other_room");

        var current = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
        if (current != null)
            return current.PlayerUid == playerUid && current.StandbyOnly == standbyOnly && current.NegotiatesStandby
                ? (true, null) : (false, "player_standby_identity_mismatch");

        var previous = room.Players.FirstOrDefault(p => p.PlayerUid == playerUid);
        if (previous != null)
        {
            if (previous.ConnectionId == room.HostConnectionId || standbySessionId != room.SessionId
                || previous.StandbyOnly != standbyOnly || !previous.NegotiatesStandby)
                return (false, "player_standby_identity_mismatch");
            if (!room.GracePendingMembers.TryGetValue(previous.ConnectionId, out var deadline))
                return (false, "host_world_reconnect_pending");
            var state = room.StandbyNegotiation!;
            if (deadline <= DateTime.UtcNow || state.IsTerminal
                && (state.Snapshot.Phase == "aborted" || room.HostConfig is not { MultiWorldEnabled: true }))
                return (false, "host_world_session_mismatch");

            var oldConnection = previous.ConnectionId;
            room.GracePendingMembers.Remove(oldConnection);
            foreach (var set in room.ArrivalSets.Values)
                if (set.Remove(oldConnection)) set.Add(connectionId);
            foreach (var set in room.FightDoneSets.Values)
                if (set.Remove(oldConnection)) set.Add(connectionId);
            foreach (var set in room.FightParticipantSets.Values)
                if (set.Remove(oldConnection)) set.Add(connectionId);
            if (room.WorldJoinedSet.Remove(oldConnection)) room.WorldJoinedSet.Add(connectionId);
            if (room.RouteVerificationDoneSet.Remove(oldConnection)) room.RouteVerificationDoneSet.Add(connectionId);
            if (room.ExpCapReachedSet.Remove(oldConnection)) room.ExpCapReachedSet.Add(connectionId);
            if (room.TwoConsecutiveNoExpSet.Remove(oldConnection)) room.TwoConsecutiveNoExpSet.Add(connectionId);
            if (room.ActiveCollectiveSkip is { } skip)
            {
                if (skip.RequiredConnectionIds.Remove(oldConnection)) skip.RequiredConnectionIds.Add(connectionId);
                if (skip.AppliedConnectionIds.Remove(oldConnection)) skip.AppliedConnectionIds.Add(connectionId);
                if (skip.FailedConnectionIds.Remove(oldConnection)) skip.FailedConnectionIds.Add(connectionId);
            }
            RemoveConnectionMappingLocked(oldConnection, room.Code);
            previous.ConnectionId = connectionId;
            previous.ReportedVersion = reportedVersion;
            previous.LastHeartbeat = DateTime.UtcNow;
            _connectionRoomMap[connectionId] = room.Code;
            return (true, null);
        }

        if (!string.IsNullOrEmpty(standbySessionId) && room.WorldPlanParticipantUids == null)
            return (false, "host_world_uid_mismatch");
        if (room.StandbyNegotiation!.Snapshot.Phase != "waiting" || room.IsStarted) return (false, "房间已开锄");
        if (standbyOnly && room.Players.Any(p => !p.NegotiatesStandby))
            return (false, "player_standby_peer_unsupported");
        if (room.Whitelist.Count > 0 && !room.Whitelist.Contains(playerName)) return (false, "不在白名单中");
        if (room.Players.Count >= Math.Min(room.ExpectedPlayerCount, MaxPlayers)) return (false, "房间已满");
        room.Players.Add(new PlayerInfo
        {
            ConnectionId = connectionId,
            PlayerId = playerId,
            PlayerName = string.IsNullOrEmpty(playerName) ? $"玩家{room.Players.Count + 1}" : playerName,
            PlayerUid = playerUid,
            NegotiatesStandby = true, StandbyOnly = standbyOnly,
            ReportedVersion = reportedVersion,
            LastHeartbeat = DateTime.UtcNow,
        });
        _connectionRoomMap[connectionId] = room.Code;
        if (standbyOnly) room.StandbyNegotiation.NoteStandbyRequest();
        return (true, null);
    }
}
