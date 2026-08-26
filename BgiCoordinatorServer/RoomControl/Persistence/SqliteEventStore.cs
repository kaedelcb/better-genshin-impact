using System.Text.Json;
using BgiCoordinatorServer.RoomControl.Events;
using Microsoft.EntityFrameworkCore;

namespace BgiCoordinatorServer.RoomControl.Persistence;

/// <summary>
/// SQLite 实现的事件存储。当前方案用 SQLite 做本地开发/测试，后续可替换为 PostgreSQL/EventStoreDB。
/// </summary>
public class SqliteEventStore : IEventStore
{
    private readonly ControlRoomDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqliteEventStore(ControlRoomDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(IDomainEvent @event, CancellationToken ct = default)
    {
        var stored = new StoredEvent
        {
            EventId = @event.EventId,
            AggregateId = @event.AggregateId,
            Version = @event.Version,
            EventType = @event.EventType,
            Payload = JsonSerializer.Serialize(@event, @event.GetType(), JsonOptions),
            Timestamp = @event.Timestamp
        };

        _db.Events.Add(stored);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE") == true)
        {
            throw new InvalidOperationException($"并发写入冲突: aggregate {@event.AggregateId} version {@event.Version}", ex);
        }
    }

    public async Task<IReadOnlyList<IDomainEvent>> GetEventsAsync(string aggregateId, long fromVersion = 0, CancellationToken ct = default)
    {
        var stored = await _db.Events
            .Where(e => e.AggregateId == aggregateId && e.Version > fromVersion)
            .OrderBy(e => e.Version)
            .AsNoTracking()
            .ToListAsync(ct);

        return stored.Select(Deserialize).ToList();
    }

    public async Task<long> GetNextVersionAsync(string aggregateId, CancellationToken ct = default)
    {
        var max = await _db.Events
            .Where(e => e.AggregateId == aggregateId)
            .Select(e => (long?)e.Version)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private static IDomainEvent Deserialize(StoredEvent stored)
    {
        var type = ResolveEventType(stored.EventType);
        var obj = JsonSerializer.Deserialize(stored.Payload, type, JsonOptions)
            ?? throw new InvalidOperationException($"无法反序列化事件 {stored.EventType}");
        return (IDomainEvent)obj;
    }

    private static Type ResolveEventType(string eventType) => eventType switch
    {
        nameof(ControlRoomCreated) => typeof(ControlRoomCreated),
        nameof(MemberJoined) => typeof(MemberJoined),
        nameof(MemberLeft) => typeof(MemberLeft),
        nameof(MemberDesiredStateUpdated) => typeof(MemberDesiredStateUpdated),
        nameof(MemberReportedStateUpdated) => typeof(MemberReportedStateUpdated),
        nameof(OnlineEventReported) => typeof(OnlineEventReported),
        nameof(OnlineSessionStarted) => typeof(OnlineSessionStarted),
        nameof(OnlineSessionStateChanged) => typeof(OnlineSessionStateChanged),
        nameof(OnlineSessionMemberReady) => typeof(OnlineSessionMemberReady),
        nameof(OnlineSessionMemberConfirmed) => typeof(OnlineSessionMemberConfirmed),
        nameof(OnlineSessionMemberExecuted) => typeof(OnlineSessionMemberExecuted),
        nameof(OnlineEventConsumed) => typeof(OnlineEventConsumed),
        _ => throw new NotSupportedException($"未知事件类型: {eventType}")
    };
}
