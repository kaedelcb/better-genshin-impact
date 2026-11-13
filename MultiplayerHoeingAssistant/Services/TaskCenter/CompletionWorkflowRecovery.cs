using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

// Dedicated execution helper for the successor's own run. The original suspended plan's
// ManagedRunId must not be overwritten to make it look like the successor owns that plan.
public sealed partial class RunStore
{
    internal bool AppendCompletionWorkflowProcessContinuation(string runId, OnlineHoeingBatch batch,
        BgiEpoch previous, BgiEpoch replacement, out WorkflowRunRecord? latest)
    {
        latest = null;
        batch.ObserveRecoveryStopSignal();
        if (!batch.PriorityRecoveryEnabled || batch.IsCancellationRequested || batch.ExplicitStopRequested
            || batch.RecoveryStopSignal is not { IsStopped: false } signal
            || CommandExecutor.ProbeOriginalBgiProcess(previous) != false
            || ViewModels.CoordinatedBatchLoop.SameEpoch(previous, replacement)) return false;
        lock (_gate)
        {
            var original = Load(runId);
            if (original is null || original.IsTerminal || original.StopRequested || TaskCenterHost.IsExecutionResetReleased(original)
                || original.WorkflowId != batch.Policy?.SpecifiedWorkflowId || original.StopAuthority is not { } intent
                || !original.Handoffs.Any(h => h.IntentKey == batch.PolicyRequestKey)
                || IsStartupSourceRevoked(intent, runId)) return false;
            var nextEpoch = WorkflowStopAuthority.Epoch(replacement)!;
            var priorEpoch = WorkflowStopAuthority.Epoch(previous)!;
            if (original.PriorityProcessContinuations.LastOrDefault() is { } bound && bound.Authority.Epoch == nextEpoch)
            { latest = original; return bound.BatchId == batch.BatchId && bound.Generation == batch.Generation; }
            if (WorkflowStopAuthority.Current(original)?.Epoch != priorEpoch) return false;
            var continuation = new PriorityProcessContinuation(priorEpoch, intent with { Epoch = nextEpoch, Version = 0 },
                batch.BatchId, batch.Generation, signal.Name);
            return UpdateMergingCore(runId, run =>
            {
                batch.ObserveRecoveryStopSignal();
                if (batch.IsCancellationRequested || signal.IsStopped || run.StopRequested
                    || WorkflowStopAuthority.Current(run)?.Epoch != priorEpoch) return false;
                if (run.CurrentSubmission is { } submission && submission.Epoch == priorEpoch && !submission.ExecutionExitConfirmed)
                {
                    // Process exit proves physical exit, never business success. Preserve a failed
                    // attempt; the original runner applies its configured retry/failure policy.
                    if (submission.ObservedTerminal is not null || string.IsNullOrEmpty(submission.JobId)) return false;
                    submission.ObservedTerminal = "failed";
                    submission.ExecutionExitConfirmed = true;
                    submission.ExecutionExitDisposition = "execution_exited";
                    submission.EffectState = "failed";
                    run.Note = (run.Note ?? "") + "\nexecutor_exited_result_unknown：绑定后续计划的原进程已退出，业务结果未取得。";
                }
                if (HasUnresolvedExternalFact(run)) return false;
                run.PriorityProcessContinuations.Add(continuation);
                run.State = WorkflowRunState.Interrupted;
                return true;
            }, out latest, authorizedProcess: continuation);
        }
    }
}
