namespace BgiCoordinatorServer.RoomControl.Events;

public interface IDomainEvent
{
    Guid EventId { get; }
    string AggregateId { get; }
    long Version { get; }
    DateTime Timestamp { get; }
    string EventType { get; }
}

public abstract record DomainEventBase(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp
) : IDomainEvent
{
    public abstract string EventType { get; }
}
