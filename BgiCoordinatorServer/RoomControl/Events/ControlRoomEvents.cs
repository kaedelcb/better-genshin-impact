namespace BgiCoordinatorServer.RoomControl.Events;

public sealed record ControlRoomCreated(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PasswordHash,
    string OwnerUid,
    List<string> AllowedUids
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(ControlRoomCreated);
}

public sealed record MemberJoined(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid,
    string PlayerName,
    string ClientInstanceId,
    string ConnectionId
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(MemberJoined);
}

public sealed record MemberLeft(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(MemberLeft);
}

public sealed record MemberDesiredStateUpdated(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid,
    string? ScheduledOnlineTime,
    List<string>? OnlineHoeingGroupNames,
    List<string>? OnlineHoeingGroupTypes,
    int? ExpectedHoeingPlayers,
    Dictionary<string, string>? QuickCommands
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(MemberDesiredStateUpdated);
}

public sealed record MemberReportedStateUpdated(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid,
    string? BgiStatus,
    bool? TaskRunning,
    string? CurrentTaskName,
    string? CurrentTaskGroupName,
    string? CurrentRouteDisplay,
    bool? AutoHoeingRunning,
    string? AutoHoeingProgress,
    List<string>? ConfigGroups,
    List<string>? OneClickConfigs,
    Dictionary<string, List<string>>? ConfigGroupTasks,
    Dictionary<string, List<string>>? OneClickTasks,
    Dictionary<string, List<object>>? ConfigGroupTasksWithStatus,
    Dictionary<string, List<object>>? OneClickTasksWithStatus,
    List<object>? Hotkeys
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(MemberReportedStateUpdated);
}

public sealed record OnlineEventReported(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid,
    int Generation
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineEventReported);
}

public sealed record OnlineSessionStarted(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    long SessionId,
    DateTime ScheduledTime,
    int Generation,
    int Threshold,
    DateTime WaitingDeadline
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineSessionStarted);
}

public sealed record OnlineSessionStateChanged(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    long SessionId,
    string NewState,
    string? Reason
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineSessionStateChanged);
}

public sealed record OnlineSessionMemberReady(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    long SessionId,
    string PlayerUid
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineSessionMemberReady);
}

public sealed record OnlineSessionMemberConfirmed(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    long SessionId,
    string PlayerUid
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineSessionMemberConfirmed);
}

public sealed record OnlineSessionMemberExecuted(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    long SessionId,
    string PlayerUid
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineSessionMemberExecuted);
}

public sealed record OnlineEventConsumed(
    Guid EventId,
    string AggregateId,
    long Version,
    DateTime Timestamp,
    string PlayerUid,
    int Generation,
    DateTime ConsumeTime
) : DomainEventBase(EventId, AggregateId, Version, Timestamp)
{
    public override string EventType => nameof(OnlineEventConsumed);
}
