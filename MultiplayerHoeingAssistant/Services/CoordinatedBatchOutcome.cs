namespace MultiplayerHoeingAssistant.Services;

/// <summary>Only an executor terminal can acknowledge cleanup; absence is never success.</summary>
public static class CoordinatedBatchOutcome
{
    public static string Classify(BgiTaskQueueStatus? terminal, bool stopping) => terminal?.Status switch
    {
        "pending" or "running" => "",
        "completed" when !terminal.Cancelled => "succeeded",
        "completed" or "queueCancelled" when stopping => "stopped",
        "completed" or "queueCancelled" => "cancelled",
        "failed" when terminal.ErrorCode == "hoeing_incomplete" => "failed",
        "failed" when stopping && terminal.ErrorCode == "task_cancelled" => "stopped",
        _ => "unknown"
    };
}
