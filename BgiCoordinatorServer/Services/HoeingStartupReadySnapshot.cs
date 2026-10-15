namespace BgiCoordinatorServer.Services;

public sealed record HoeingStartupReadySnapshot(string BatchId, IReadOnlyList<string> ParticipantUids,
    IReadOnlyList<string> WorldJoinedUids, bool Ready);
