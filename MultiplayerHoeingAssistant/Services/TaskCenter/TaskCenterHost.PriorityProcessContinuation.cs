using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private string? PriorityNodeExecutionEpoch(AdmissionRequest request)
    {
        if (request.OperationType != OperationType.NodeExecution || request.ParentSource is not { } parent
            || request.Candidate.RunId is not { } runId || parent.RunId != runId
            || parent.Scope != request.Candidate.Scope) return null;
        var run = _runs.Load(runId);
        if (run is null || run.StopRequested || IsExecutionResetReleased(run)
            || run.WorkflowId != parent.WorkflowId || run.PriorityProcessContinuations.Count == 0) return null;
        return WorkflowStopAuthority.Current(run)?.Epoch;
    }

    internal bool HasPriorityProcessContinuation(OnlineHoeingBatch batch, string runId)
        => _runs.Load(runId)?.PriorityProcessContinuations.LastOrDefault() is { } continuation
            && continuation.BatchId == batch.BatchId && continuation.Generation == batch.Generation
            && continuation.Authority.Epoch == WorkflowStopAuthority.Epoch(batch.ExecutionEpoch);

    internal async Task<bool> ContinuePriorityPlanOnReplacementAsync(OnlineHoeingBatch batch,
        BgiEpoch previous, BgiEpoch replacement, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        batch.ObserveRecoveryStopSignal();
        if (batch.IsCancellationRequested || batch.ManagedRunId is not { } runId
            || batch.Policy?.Policy != TaskConflictPolicy.Resume || !batch.LocalCompletionReady
            || CommandExecutor.ProbeOriginalBgiProcess(previous) != false) return false;
        if (batch.RestoredFromRecovery && !await RestorePriorityPlanRecoveryAsync(batch).ConfigureAwait(false)) return false;
        var run = _runs.Load(runId);
        if (run is null) return false;
        if (run.StopRequested || IsExecutionResetReleased(run)) { batch.Cancel(); return false; }
        ValidatePriorityPlanIdentity(batch, run);
        var alreadyBound = HasPriorityProcessContinuation(batch, runId);
        lock (_gate)
        {
            if (_shutdown || _manualResetActive != 0) return false;
            if (_drives.TryGetValue(run.WorkflowId, out var live))
                return alreadyBound && live.RunId == runId;
            if (_reservedWorkflows.Contains(run.WorkflowId)) return false;
        }
        // A lost recovery reply must adopt the original driver/result, never restart it.
        if (alreadyBound && run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
            or WorkflowRunState.LocalWaitParking)) return true;
        var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
        if (snapshot.Revision != run.WorkflowRevision) throw new IOException("原计划内容已改变，保留原安排等待原内容");
        var plan = new WorkflowPlan(snapshot.Document);
        if (!_runs.AppendPriorityProcessContinuation(runId, batch, previous, replacement, current =>
        {
            if (current.TerminalRelease is not null || current.StopRequested) return false;
            if (current.CurrentSubmission is { } submission && submission.Epoch == WorkflowStopAuthority.Epoch(previous))
            {
                if (!submission.ExecutionExitConfirmed)
                {
                    // This is an executor failure, with the business result explicitly unknown.
                    // Exact process death proves that no execution from this attempt survives.
                    if (submission.ObservedTerminal is not null || string.IsNullOrEmpty(submission.JobId)) return false;
                    submission.ObservedTerminal = "failed";
                    submission.ExecutionExitConfirmed = true;
                    submission.ExecutionExitDisposition = "execution_exited";
                    submission.EffectState = "failed";
                    current.Note = (current.Note ?? "") + "\nexecutor_exited_result_unknown：原执行进程已退出，业务结果未取得；原任务按新attempt重跑。";
                }
                if (current.OnlineHoeingHandoff is { } h && h.Generation == batch.Generation)
                {
                    if (h.ResumeJobId == submission.JobId && h.ResumeKey == submission.Key)
                    { h.Phase = "terminal"; h.ResumeTerminal = submission.ObservedTerminal; }
                    else if (submission.ObservedTerminal == "cancelled" && submission.Key == h.OriginalSubmissionKey)
                    {
                        if (current.Cursor is not { } cursor || cursor.Attempt != submission.Attempt
                            || !plan.TryLocate(submission.NodeId, submission.Occurrence, submission.LoopIteration, out var occurrence)
                            || submission.Attempt == int.MaxValue || h.ResumeAttempt == int.MaxValue) return false;
                        var nextAttempt = Math.Max(submission.Attempt, h.ResumeAttempt ?? 0) + 1;
                        if (!current.NodeOutcomes.Any(o => o.SubmissionKey == submission.Key && o.Attempt == submission.Attempt))
                            current.NodeOutcomes.Add(new()
                            {
                                NodeId = submission.NodeId, Occurrence = submission.Occurrence,
                                LoopIteration = submission.LoopIteration, SequenceIndex = occurrence.SequenceIndex,
                                Attempt = submission.Attempt, Result = "cancelled", RawTerminal = "cancelled",
                                Reason = "online_hoeing_preempted", SubmissionKey = submission.Key,
                                AcceptedSendIdentity = submission.AcceptedSendIdentity, RetryNextAttempt = nextAttempt,
                            });
                        cursor.Attempt = nextAttempt;
                        h.Phase = "released";
                        current.OnlineHoeingHistory.Add(h.Archived());
                        current.OnlineHoeingHandoff = null;
                    }
                }
            }
            if (RunStore.HasUnresolvedExternalFact(current)) return false;
            current.State = WorkflowRunState.Interrupted;
            return true;
        }, out var retained) || retained is null) return false;
        var ownReservation = false;
        var handedToDriver = false;
        try
        {
            lock (_gate)
            {
                if (_shutdown || _manualResetActive != 0 || _drives.ContainsKey(run.WorkflowId)
                    || !_reservedWorkflows.Add(run.WorkflowId)) return false;
                ownReservation = true;
                _onlineHoeingGeneration = batch.Generation;
                _onlineHoeingRunId = runId;
                _stateContinuationRuns[runId] = batch.Generation;
                _runs.RetainPriorityProgress(retained);
            }
            await OpenOnlineHoeingSendAsync(runId, priorityRecovery: true).ConfigureAwait(false);
            var runner = CreateRunner(_clientAccessor());
            handedToDriver = LaunchDrive(run.WorkflowId, runner, cts => runner.ResumeAsync(runId, cts.Token),
                "原BGI退出后继续同一原计划", runId).Ok;
            return handedToDriver;
        }
        finally
        {
            if (ownReservation && !handedToDriver)
                lock (_gate)
                    if (!_drives.ContainsKey(run.WorkflowId)) _reservedWorkflows.Remove(run.WorkflowId);
        }
    }
}
