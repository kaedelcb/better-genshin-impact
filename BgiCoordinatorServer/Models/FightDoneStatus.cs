namespace BgiCoordinatorServer.Models;

/// <summary>
/// Shared-fight end state returned by the authoritative coordinator.
/// The query is intentionally idempotent: a client may repeat it after
/// missing the AllFightDone event without creating another vote.
/// </summary>
public sealed record FightDoneStatus(
    bool Terminal,
    int DoneCount,
    int ParticipantCount,
    int RequiredDoneCount,
    bool ShouldBroadcast);
