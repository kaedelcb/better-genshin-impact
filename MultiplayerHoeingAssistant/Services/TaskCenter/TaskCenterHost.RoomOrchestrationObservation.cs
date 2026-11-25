namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    // Pure classification; the actual wire run id is distinct from the assistant's RunId.
    internal bool IsRoomOrchestrationWorkflowSignal(string wireRunId)
    {
        if (!Guid.TryParse(wireRunId, out var wire) || wire == Guid.Empty) return false;
        try
        {
            return _runs.List().Any(r => Guid.TryParse(r.WireRunId, out var candidate)
                && candidate == wire && IsRoomOrchestrationWorkflowRun(r));
        }
        catch { return false; }
    }
}
