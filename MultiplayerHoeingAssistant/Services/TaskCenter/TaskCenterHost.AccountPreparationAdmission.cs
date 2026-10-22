using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private string? AccountPreparationExecutionEpoch(AdmissionRequest request)
    {
        var runId = request.Candidate.RunId;
        if (string.IsNullOrEmpty(runId) || request.RunBinding != runId) return null;
        var run = _runs.Load(runId);
        if (run is null || run.StopRequested || IsExecutionResetReleased(run)
            || run.AccountPreparationProcessContinuations.Count == 0
            || run.WorkflowId != request.Candidate.WorkflowId
            || run.AdmissionSourceScope != request.Candidate.Scope) return null;
        if (request.OperationType == OperationType.Recovery)
        {
            if (request.Namespace != "resume" || request.Candidate.Intent != "resume"
                || !AccountPreparationRecovery.CanResume(run)) return null;
        }
        else if (request.OperationType == OperationType.NodeExecution)
        {
            if (request.ParentSource is not { } parent || parent.RunId != runId
                || parent.WorkflowId != run.WorkflowId || parent.Scope != run.AdmissionSourceScope
                || run.AdmissionParentSource != parent) return null;
        }
        else return null;
        return WorkflowStopAuthority.Current(run)?.Epoch;
    }
}
