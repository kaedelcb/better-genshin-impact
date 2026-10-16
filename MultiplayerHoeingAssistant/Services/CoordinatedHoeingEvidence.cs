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

    public bool IsValid => Started >= 0 && Completed >= 0 && Failed >= 0 && Cancelled >= 0 && Active >= 0
        && Revision >= 0 && (long)Completed + Failed + Cancelled + Active <= Started;

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
