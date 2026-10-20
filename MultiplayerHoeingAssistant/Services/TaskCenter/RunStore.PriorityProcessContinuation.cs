using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    internal bool AppendPriorityProcessContinuation(string runId, OnlineHoeingBatch batch,
        BgiEpoch previous, BgiEpoch replacement, Func<WorkflowRunRecord, bool> settleExited,
        out WorkflowRunRecord? latest)
    {
        latest = null;
        batch.ObserveRecoveryStopSignal();
        if (!batch.PriorityRecoveryEnabled || batch.ManagedRunId != runId || batch.IsCancellationRequested
            || batch.ExplicitStopRequested || batch.RecoveryStopSignal is not { IsStopped: false } signal
            || CommandExecutor.ProbeOriginalBgiProcess(previous) != false
            || ViewModels.CoordinatedBatchLoop.SameEpoch(previous, replacement)) return false;
        lock (_gate)
        {
            var original = Load(runId);
            if (original is null || original.StopRequested || TaskCenterHost.IsExecutionResetReleased(original)
                || original.StopAuthority is not { } intent) return false;
            var nextEpoch = WorkflowStopAuthority.Epoch(replacement)!;
            var priorEpoch = WorkflowStopAuthority.Epoch(previous)!;
            if (original.PriorityProcessContinuations.LastOrDefault() is { } already
                && already.Authority.Epoch == nextEpoch)
            {
                latest = original;
                return already.BatchId == batch.BatchId && already.Generation == batch.Generation;
            }
            if (WorkflowStopAuthority.Current(original)?.Epoch != priorEpoch) return false;
            var continuation = new PriorityProcessContinuation(priorEpoch,
                intent with { Epoch = nextEpoch, Version = 0 }, batch.BatchId, batch.Generation, signal.Name);
            return UpdateMergingCore(runId, run =>
            {
                batch.ObserveRecoveryStopSignal();
                if (batch.IsCancellationRequested || signal.IsStopped || run.StopRequested
                    || WorkflowStopAuthority.Current(run)?.Epoch != priorEpoch || !settleExited(run)) return false;
                run.PriorityProcessContinuations.Add(continuation);
                return true;
            }, out latest, authorizedProcess: continuation);
        }
    }
}
