namespace MultiplayerHoeingAssistant.Services;

/// <summary>Evidence from one numbered BGI job; body progress never proves the whole group exited.</summary>
public sealed record CoordinatedHoeingEvidence
{
    public int Started { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public int Cancelled { get; init; }
    public int Active { get; init; }
    public bool Sealed { get; init; }
    public string? JobResult { get; init; }
    public long Revision { get; init; }
    public int? PlannedNativeCount { get; init; }

    public bool IsValid => Started >= 0 && Completed >= 0 && Failed >= 0 && Cancelled >= 0 && Active >= 0
        && Revision >= 0 && (PlannedNativeCount is null or >= 0)
        && (long)Completed + Failed + Cancelled + Active <= Started;

    /// <summary>Every native invocation in this failed job was a confirmed successful cooperative invocation.</summary>
    public bool IsLocalTasksFailure(BgiTaskQueueStatus terminal) => terminal.Status == "failed"
        && !terminal.Cancelled && IsValid && Sealed && JobResult == "Failed"
        && Started == Completed && Completed > 0 && Failed == 0 && Cancelled == 0 && Active == 0;

    /// <summary>A cancelled tail cannot cancel peers after every frozen native task has completed.</summary>
    public bool IsLocalTasksCancellation(BgiTaskQueueStatus terminal) => IsValid && Sealed
        && PlannedNativeCount is > 0 && PlannedNativeCount == Started && Started == Completed
        && Failed == 0 && Cancelled == 0 && Active == 0
        && ((terminal.Status == "completed" && terminal.Cancelled && JobResult == "Cancelled")
            || (terminal.Status == "failed" && JobResult == "Preempted"));

    public bool MatchesTerminal(BgiTaskQueueStatus terminal)
    {
        if (!IsValid || !Sealed || Active != 0 || string.IsNullOrWhiteSpace(JobResult)) return false;
        var result = JobResult.ToLowerInvariant();
        return terminal.Status switch
        {
            "completed" when !terminal.Cancelled => result == "ran",
            "completed" or "queueCancelled" => result is "cancelled" or "preempted",
            "failed" => result is "failed" or "preempted" or "rejectedslotbusy" or "skipped",
            _ => false
        };
    }
}
