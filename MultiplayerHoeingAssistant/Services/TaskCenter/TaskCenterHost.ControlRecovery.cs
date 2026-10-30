using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private readonly SemaphoreSlim _controlOwnershipGate = new(1, 1);
    private readonly AsyncLocal<ControlCall?> _controlCall = new();
    private int _controlCalls;
    private sealed class ControlCall { public bool Active = true; }
    private sealed class ControlScope(Action release) : IDisposable { public void Dispose() => release(); }

    private IDisposable EnterControlCall()
    {
        var previous = _controlCall.Value;
        if (previous?.Active == true) return new ControlScope(() => { });
        if (_automaticLeaseRecovery.Value is null)
            lock (_gate) Interlocked.Increment(ref _controlRevision);
        var call = new ControlCall();
        _controlCall.Value = call;
        Interlocked.Increment(ref _controlCalls);
        return new ControlScope(() =>
        {
            call.Active = false;
            _controlCall.Value = previous;
            Interlocked.Decrement(ref _controlCalls);
        });
    }

    // Explicit or narrowly qualified automatic recovery installs a new capability;
    // it never upgrades a retained runner/store capability.
    private async Task EnsureControlOwnershipAsync()
    {
        if (!_admissionWired) return;
        await _controlOwnershipGate.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfAutomaticRecoverySuperseded();
            if (_admission is null) await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            try { _runs.VerifyBoundOwner(); CompleteDeferredAdministrativeRecovery(); return; }
            catch (RunRecordConflictException) { }

            lock (_gate)
            {
                if (_shutdown || Volatile.Read(ref _controlCalls) > 1 || _drives.Count != 0 || _driveCompletions.Count != 0
                    || _reservedWorkflows.Any(id => !_executionResetWorkflows.Contains(id))
                    || _recoverTask is { IsCompleted: false })
                    throw new InvalidOperationException("旧驱动或受理仍在收敛，保留原身份，请稍后重试。");
                if (_runs.List().Any(run => _runs.HasUnpublishedPriorityProgress(run.RunId)))
                    throw new InvalidOperationException("原联机恢复仍有未落盘进度，不能替换原写者。");
                if (_admission?.TryRetireForRecovery() != true)
                    throw new InvalidOperationException("原仲裁仍有在途操作，不能替换原写者。");

                // Old runners and adapters keep the old RunStore; the fence rejects their writes.
                _runs = new RunStore(_runsDirPath!, requireOwnership: true);
                _admission = null;
                _admissionStore = null;
                _admissionLeaseId = null;
                _admissionOwnerEpoch = null;
            }
            // The existing observer waits a complete TTL and rechecks generation/heartbeat
            // under the physical lock. A different healthy owner is never stolen from.
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            _runs.VerifyBoundOwner();
            ThrowIfAutomaticRecoverySuperseded();
            TryLog("[任务中心] 显式控制已重新取得租约；旧写者仍失效，原任务须按原身份核对，不自动重发。");
        }
        finally { _controlOwnershipGate.Release(); }
    }

    private void NormalizeOrphanRun(string runId)
    {
        lock (_gate)
        {
            var run = _runs.Load(runId);
            if (run is null || IsExecutionResetReleased(run) || run.OnlineHoeingHandoff is not null
                || run.OnlineHoeingGateParked || _drives.ContainsKey(run.WorkflowId)
                || _reservedWorkflows.Contains(run.WorkflowId) && !_executionResetWorkflows.Contains(run.WorkflowId)
                || _driveCompletions.Any(d => d.WorkflowId == run.WorkflowId)
                || run.State is not (WorkflowRunState.Running or WorkflowRunState.Waiting
                    or WorkflowRunState.Planned or WorkflowRunState.Completing)) return;
            _runs.UpdateMergingIf(runId, latest =>
            {
                if (latest.State != run.State || latest.OnlineHoeingHandoff is not null) return false;
                latest.State = RunStore.HasUnresolvedTerminalResponsibility(latest)
                    ? WorkflowRunState.Unknown : WorkflowRunState.Interrupted;
                latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                    + "显式控制核对：原驱动已退出，保留原任务身份与停止意图。";
                return true;
            }, out _);
        }
    }

    private async Task StopExpiredLocalDriveAsync(string runId, string workflowId)
    {
        if (_admission is null) return;
        try { _runs.VerifyBoundOwner(); return; }
        catch (RunRecordConflictException) { }
        DriveEntry? entry;
        lock (_gate) _drives.TryGetValue(workflowId, out entry);
        if (entry?.Runner.HasActiveControl(runId) != true) return;
        // Cancel only this original in-process driver. RequestAction cancels its token even
        // if persisting Stop fails with the expired capability; no replacement owner is used.
        try { entry.Runner.RequestAction(runId, WorkflowRunAction.Stop); }
        catch (RunRecordConflictException) { }
        await entry.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15), _shutdownCts.Token).ConfigureAwait(false);
    }

    // Resume may consume a proven terminal result, never an unconfirmed send or a new epoch.
    private async Task ConfirmOrphanTerminalForResumeAsync(string runId)
    {
        WorkflowRunRecord? run;
        lock (_gate)
        {
            run = _runs.Load(runId);
            if (run?.State != WorkflowRunState.Unknown || run.StopRequested
                || run.OnlineHoeingHandoff is not null || run.OnlineHoeingGateParked
                || _drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId)) return;
        }
        if (run.CurrentSubmission is not { Intent: SubmitIntentState.Accepted, JobId: not null } sub
            || sub.OriginalRequestEvidence is null || !sub.SendAttempted) return;
        var identity = TryResolveNodeSendIdentity(run, sub);
        var client = _clientAccessor();
        if (identity is null || client is null
            || _admissionSeams?.ProductionBoundaryFactory is null && client.State != BgiExternalLinkState.Ready) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        if (!await CreateProductionBoundary(client).ConfirmOriginalSubmissionExitAsync(run, sub, budget.Token).ConfigureAwait(false)) return;
        if (!await SettleOriginalNodeAcceptanceAsync(run, identity, sub.JobId).ConfigureAwait(false)) return;
        lock (_gate)
        {
            if (_drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId)) return;
            _runs.UpdateMergingIf(runId, latest =>
            {
                if (latest.State != WorkflowRunState.Unknown || latest.StopRequested
                    || latest.OnlineHoeingHandoff is not null || latest.OnlineHoeingGateParked
                    || latest.CurrentSubmission is not { } current
                    || TryResolveNodeSendIdentity(latest, current) != identity
                    || RunStore.HasUnresolvedTerminalResponsibility(latest)) return false;
                latest.State = WorkflowRunState.Interrupted;
                latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                    + "显式恢复已核对原作业终态与退出；由原游标消费结果，不重发已完成任务。";
                return true;
            }, out _);
        }
    }
}
