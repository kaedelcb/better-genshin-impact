namespace MultiplayerHoeingAssistant.Services;

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason)
{
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
    public string Protocol { get; init; } = "";
    public string Source { get; init; } = "";
    public string RequestId { get; init; } = "";
    public IReadOnlyList<string> ParticipantUids { get; init; } = [];
    public IReadOnlyDictionary<string, int> IntentGenerations { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> Bindings { get; init; }
        = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>();
    public IReadOnlyList<CoordinatedHoeingMember> Members { get; init; } = [];
    public bool CleanupComplete { get; init; }
}

public sealed record CoordinatedHoeingBinding(string Name, string Type);
public sealed record CoordinatedHoeingMember(string Uid, string Stage, string Reason);

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
