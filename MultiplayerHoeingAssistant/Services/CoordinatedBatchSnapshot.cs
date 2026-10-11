namespace MultiplayerHoeingAssistant.Services;

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason)
{
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
}

public sealed record CoordinatedBatchResetResult
{
    public string ResetId { get; init; } = "";
    public bool Found { get; init; }
    public string BatchId { get; init; } = "";
    public int Generation { get; init; }
    public string Phase { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> TargetUids { get; init; } = [];
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
}
