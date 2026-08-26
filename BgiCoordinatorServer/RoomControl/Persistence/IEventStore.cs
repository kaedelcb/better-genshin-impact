using BgiCoordinatorServer.RoomControl.Events;

namespace BgiCoordinatorServer.RoomControl.Persistence;

public interface IEventStore
{
    Task AppendAsync(IDomainEvent @event, CancellationToken ct = default);
    Task<IReadOnlyList<IDomainEvent>> GetEventsAsync(string aggregateId, long fromVersion = 0, CancellationToken ct = default);
    Task<long> GetNextVersionAsync(string aggregateId, CancellationToken ct = default);
}
