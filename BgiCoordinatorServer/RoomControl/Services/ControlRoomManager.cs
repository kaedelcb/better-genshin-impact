using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Events;
using BgiCoordinatorServer.RoomControl.Persistence;

namespace BgiCoordinatorServer.RoomControl.Services;

public interface IControlRoomManager
{
    Task<ControlRoom?> GetAsync(string roomCode, CancellationToken ct = default);
    Task<ControlRoom?> GetByConnectionIdAsync(string connectionId, CancellationToken ct = default);
    Task<ControlRoom?> GetByPlayerUidAsync(string playerUid, CancellationToken ct = default);
    Task<ControlRoom> CreateAsync(string roomCode, string password, string ownerUid, List<string> allowedUids, CancellationToken ct = default);
    Task<ControlRoomMember?> JoinAsync(string roomCode, string password, string playerUid, string playerName, string clientInstanceId, string connectionId, CancellationToken ct = default);
    Task<bool> LeaveAsync(string connectionId, CancellationToken ct = default);
    Task<bool> UpdateDesiredStateAsync(string actorUid, string targetUid, MemberDesiredState state, CancellationToken ct = default);
    Task<bool> UpdateReportedStateAsync(string connectionId, MemberReportedState state, CancellationToken ct = default);
    Task ReportOnlineEventAsync(string connectionId, int generation, CancellationToken ct = default);
    Task<bool> ClearOnlineHistoryAsync(string actorUid, string targetUid, CancellationToken ct = default);
}

public class ControlRoomManager : IControlRoomManager
{
    private readonly IControlRoomRepository _repo;
    private readonly IEventStore _eventStore;

    public ControlRoomManager(IControlRoomRepository repo, IEventStore eventStore)
    {
        _repo = repo;
        _eventStore = eventStore;
    }

    public async Task<ControlRoom?> GetAsync(string roomCode, CancellationToken ct = default)
    {
        return await _repo.GetAsync(roomCode, ct);
    }

    public async Task<ControlRoom?> GetByConnectionIdAsync(string connectionId, CancellationToken ct = default)
    {
        return await _repo.GetByConnectionIdAsync(connectionId, ct);
    }

    public async Task<ControlRoom?> GetByPlayerUidAsync(string playerUid, CancellationToken ct = default)
    {
        return await _repo.GetByPlayerUidAsync(playerUid, ct);
    }

    public async Task<ControlRoom> CreateAsync(string roomCode, string password, string ownerUid, List<string> allowedUids, CancellationToken ct = default)
    {
        var existing = await _repo.GetAsync(roomCode, ct);
        if (existing != null)
            throw new InvalidOperationException($"房间 {roomCode} 已存在");

        var hash = PasswordHasher.Hash(password);
        var room = ControlRoom.Create(roomCode, hash, ownerUid, allowedUids);

        var evt = new ControlRoomCreated(
            Guid.NewGuid(), roomCode, 1, DateTime.UtcNow, hash, ownerUid, allowedUids.ToList());

        await _repo.SaveAsync(room, evt, ct);
        return room;
    }

    public async Task<ControlRoomMember?> JoinAsync(string roomCode, string password, string playerUid,
        string playerName, string clientInstanceId, string connectionId, CancellationToken ct = default)
    {
        var room = await _repo.GetAsync(roomCode, ct);
        if (room == null)
        {
            // 房间不存在时，允许第一个加入者创建房间并设置密码（兼容原房主首次行为）。
            room = ControlRoom.Create(roomCode, PasswordHasher.Hash(password), playerUid, []);
            var createEvt = new ControlRoomCreated(Guid.NewGuid(), roomCode, 1, DateTime.UtcNow, room.PasswordHash, playerUid, []);
            await _repo.SaveAsync(room, createEvt, ct);
        }
        else if (!PasswordHasher.Verify(password, room.PasswordHash))
        {
            return null;
        }

        var member = room.JoinMember(playerUid, playerName, clientInstanceId, connectionId);
        var version = await GetNextVersionAsync(roomCode, ct);
        var evt = new MemberJoined(Guid.NewGuid(), roomCode, version, DateTime.UtcNow,
            playerUid, playerName, clientInstanceId, connectionId);

        await _repo.SaveAsync(room, evt, ct);
        return member;
    }

    public async Task<bool> LeaveAsync(string connectionId, CancellationToken ct = default)
    {
        var room = await _repo.GetByConnectionIdAsync(connectionId, ct);
        if (room == null) return false;

        var member = room.GetMemberByConnectionId(connectionId);
        if (member == null) return false;

        room.LeaveMember(connectionId);
        var version = await GetNextVersionAsync(room.RoomCode, ct);
        var evt = new MemberLeft(Guid.NewGuid(), room.RoomCode, version, DateTime.UtcNow, member.PlayerUid);

        await _repo.SaveAsync(room, evt, ct);
        return true;
    }

    public async Task<bool> UpdateDesiredStateAsync(string actorUid, string targetUid, MemberDesiredState state, CancellationToken ct = default)
    {
        var room = await FindRoomByMemberUidAsync(targetUid, ct);
        if (room == null) return false;

        // 权限检查：房主或本人可修改
        if (room.OwnerUid != actorUid && targetUid != actorUid)
            return false;

        room.SetMemberDesiredState(targetUid, state);
        var version = await GetNextVersionAsync(room.RoomCode, ct);
        var evt = new MemberDesiredStateUpdated(
            Guid.NewGuid(), room.RoomCode, version, DateTime.UtcNow,
            targetUid,
            state.ScheduledOnlineTime,
            state.OnlineHoeingGroupNames,
            state.OnlineHoeingGroupTypes,
            state.ExpectedHoeingPlayers,
            state.QuickCommands);

        await _repo.SaveAsync(room, evt, ct);
        return true;
    }

    public async Task<bool> UpdateReportedStateAsync(string connectionId, MemberReportedState state, CancellationToken ct = default)
    {
        var room = await _repo.GetByConnectionIdAsync(connectionId, ct);
        if (room == null) return false;

        var member = room.GetMemberByConnectionId(connectionId);
        if (member == null) return false;

        member.UpdateReportedState(state);
        var version = await GetNextVersionAsync(room.RoomCode, ct);
        var evt = new MemberReportedStateUpdated(
            Guid.NewGuid(), room.RoomCode, version, DateTime.UtcNow,
            member.PlayerUid,
            state.BgiStatus,
            state.TaskRunning,
            state.CurrentTaskName,
            state.CurrentTaskGroupName,
            state.CurrentRouteDisplay,
            state.AutoHoeingRunning,
            state.AutoHoeingProgress,
            state.ConfigGroups,
            state.OneClickConfigs,
            state.ConfigGroupTasks,
            state.OneClickTasks,
            state.Hotkeys);

        await _repo.SaveAsync(room, evt, ct);
        return true;
    }

    public async Task ReportOnlineEventAsync(string connectionId, int generation, CancellationToken ct = default)
    {
        var room = await _repo.GetByConnectionIdAsync(connectionId, ct);
        if (room == null) return;

        var member = room.GetMemberByConnectionId(connectionId);
        if (member == null) return;

        room.ReportMemberOnlineEvent(member.PlayerUid, generation);
        var version = await GetNextVersionAsync(room.RoomCode, ct);
        var evt = new OnlineEventReported(Guid.NewGuid(), room.RoomCode, version, DateTime.UtcNow, member.PlayerUid, generation);

        await _repo.SaveAsync(room, evt, ct);
    }

    public async Task<bool> ClearOnlineHistoryAsync(string actorUid, string targetUid, CancellationToken ct = default)
    {
        var room = await FindRoomByMemberUidAsync(targetUid, ct);
        if (room == null) return false;

        if (room.OwnerUid != actorUid && targetUid != actorUid)
            return false;

        var ok = room.ClearMemberOnlineHistory(targetUid);
        if (!ok) return false;

        await _repo.SaveAsync(room, null, ct);
        return true;
    }

    private async Task<ControlRoom?> FindRoomByMemberUidAsync(string playerUid, CancellationToken ct)
    {
        return await _repo.GetByPlayerUidAsync(playerUid, ct);
    }

    private async Task<long> GetNextVersionAsync(string roomCode, CancellationToken ct)
    {
        return await _eventStore.GetNextVersionAsync(roomCode, ct);
    }
}
