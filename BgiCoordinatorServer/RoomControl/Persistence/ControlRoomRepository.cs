using BgiCoordinatorServer.RoomControl.Domain;
using BgiCoordinatorServer.RoomControl.Events;
using Microsoft.EntityFrameworkCore;

namespace BgiCoordinatorServer.RoomControl.Persistence;

/// <summary>
/// 控制房间仓库。采用"快照 + 事件日志"的混合模式：
/// - 聚合根当前状态以关系型快照存储，便于查询和广播。
/// - 每次状态变更同时追加事件到 EventStore，保留审计与可恢复能力。
/// 后续可切换为纯事件溯源重放。
/// </summary>
public class ControlRoomRepository : IControlRoomRepository
{
    private readonly ControlRoomDbContext _db;
    private readonly IEventStore _eventStore;

    public ControlRoomRepository(ControlRoomDbContext db, IEventStore eventStore)
    {
        _db = db;
        _eventStore = eventStore;
    }

    public async Task<ControlRoom?> GetAsync(string roomCode, CancellationToken ct = default)
    {
        return await _db.ControlRooms
            .AsSplitQuery()
            .Include(r => r.Members)
            .Include(r => r.OnlineSessions)
            .FirstOrDefaultAsync(r => r.RoomCode == roomCode, ct);
    }

    public async Task<ControlRoom?> GetByConnectionIdAsync(string connectionId, CancellationToken ct = default)
    {
        var member = await _db.Members
            .FirstOrDefaultAsync(m => m.ConnectionId == connectionId, ct);
        if (member == null) return null;

        return await GetAsync(member.RoomCode, ct);
    }

    public async Task<ControlRoom?> GetByPlayerUidAsync(string playerUid, CancellationToken ct = default)
    {
        var member = await _db.Members
            .FirstOrDefaultAsync(m => m.PlayerUid == playerUid, ct);
        if (member == null) return null;

        return await GetAsync(member.RoomCode, ct);
    }

    public async Task<IReadOnlyList<ControlRoom>> GetAllWithScheduleAsync(CancellationToken ct = default)
    {
        var roomCodes = await _db.Members
            .Where(m => !string.IsNullOrEmpty(m.ScheduledOnlineTime))
            .Select(m => m.RoomCode)
            .Distinct()
            .ToListAsync(ct);

        var rooms = new List<ControlRoom>();
        foreach (var code in roomCodes)
        {
            var room = await GetAsync(code, ct);
            if (room != null) rooms.Add(room);
        }
        return rooms;
    }

    public async Task SaveAsync(ControlRoom room, IDomainEvent? newEvent = null, CancellationToken ct = default)
    {
        room.UpdateActivity();

        var existing = await _db.ControlRooms.FindAsync(new object[] { room.RoomCode }, ct);
        if (existing == null)
        {
            _db.ControlRooms.Add(room);
        }
        else
        {
            _db.ControlRooms.Update(room);
        }

        await _db.SaveChangesAsync(ct);

        if (newEvent != null)
        {
            await _eventStore.AppendAsync(newEvent, ct);
        }
    }

    public async Task DeleteAsync(string roomCode, CancellationToken ct = default)
    {
        var room = await GetAsync(roomCode, ct);
        if (room == null) return;

        _db.ControlRooms.Remove(room);
        await _db.SaveChangesAsync(ct);
    }
}
