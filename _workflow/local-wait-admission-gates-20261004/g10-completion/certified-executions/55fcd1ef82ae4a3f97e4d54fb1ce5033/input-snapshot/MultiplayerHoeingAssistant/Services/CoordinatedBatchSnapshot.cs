namespace MultiplayerHoeingAssistant.Services;

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason);
