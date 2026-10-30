using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private long _controlRevision;
    private sealed record AutomaticLeaseRecovery(string RunId, long ControlRevision);
    private readonly AsyncLocal<AutomaticLeaseRecovery?> _automaticLeaseRecovery = new();

    // Explicit controls invalidate a pending automatic continuation even when the
    // expired writer cannot persist their intent. They never get silently undone.
    private bool AutomaticRecoverySuperseded()
        => _automaticLeaseRecovery.Value is { } recovery
            && (_shutdown || Interlocked.Read(ref _controlRevision) != recovery.ControlRevision);

    private void ThrowIfAutomaticRecoverySuperseded()
    {
        if (AutomaticRecoverySuperseded())
            throw new OperationCanceledException("自动接续已让位于用户控制或宿主退出。");
    }

    private string? FindExpiredDriveRecovery(DriveEntry entry, Exception error)
    {
        if (!_admissionWired || error is not RunRecordConflictException || entry.Cts.IsCancellationRequested)
            return null;
        try
        {
            lock (_gate)
            {
                if (_shutdown || entry.ControlRevision != Interlocked.Read(ref _controlRevision)
                    || _executionResetWorkflows.Contains(entry.WorkflowId)) return null;
            }
            var read = _admissionStore?.Read();
            if (read?.Status != ArbitrationLeaseStatus.Expired || read.UncertainResidue
                || read.File?.Lease is not { } lease || lease.LeaseId != _admissionLeaseId
                || lease.OwnerEpoch != _admissionOwnerEpoch) return null;
            var run = entry.RunId is not null ? _runs.Load(entry.RunId)
                : _runs.List().SingleOrDefault(r => r.WorkflowId == entry.WorkflowId && r.State == WorkflowRunState.Running);
            // Only this live driver's precisely admitted original job is eligible.
            // Historical Unknown, pauses, prerequisites and online handoffs keep
            // their existing recovery owners and are never swept into this path.
            return run is { State: WorkflowRunState.Running, StopRequested: false,
                OnlineHoeingHandoff: null, OnlineHoeingGateParked: false,
                CurrentSubmission: { Intent: SubmitIntentState.Accepted, SendAttempted: true,
                    JobId.Length: > 0, AcceptedSendIdentity.Length: > 0, OriginalRequestEvidence: not null } }
                && !IsExecutionResetReleased(run) ? run.RunId : null;
        }
        catch (Exception ex)
        {
            TryLog("[任务中心] 租约异常自动接续资格未确认，保留原记录：" + ex.Message);
            return null;
        }
    }

    private async Task RecoverExpiredDriveAsync(string runId, long controlRevision)
    {
        _automaticLeaseRecovery.Value = new(runId, controlRevision);
        try
        {
            ThrowIfAutomaticRecoverySuperseded();
            TryLog($"[任务中心] 租约过期，开始核对原任务并尝试自动接续（{runId}）；不会重新发送已完成任务。");
            // The ordinary recovery path re-acquires a NEW capability, fences the
            // old writer, checks F11/source/epoch/payload/exit and settles the old
            // admission before launching a runner from its original cursor.
            var result = await ResumeRunAsync(runId).ConfigureAwait(false);
            if (!result.Ok && await WaitForOriginalRecoveryExitAsync(runId).ConfigureAwait(false))
            {
                ThrowIfAutomaticRecoverySuperseded();
                result = await ResumeRunAsync(runId).ConfigureAwait(false);
            }
            TryLog(result.Ok ? $"[任务中心] 已按原任务身份自动接续（{runId}）。"
                : $"[任务中心] 自动接续未确认，保留原任务且未重发（{runId}）：{result.Message}");
        }
        catch (OperationCanceledException) { TryLog("[任务中心] 自动接续已取消，保留用户控制意图。"); }
        catch (Exception ex) { TryLog("[任务中心] 自动接续未完成，保留原任务：" + ex.Message); }
        finally { _automaticLeaseRecovery.Value = null; }
    }

    private async Task<bool> WaitForOriginalRecoveryExitAsync(string runId)
    {
        // Same observation budget as the normal BGI execution boundary. Each
        // slice observes only the original job and yields to explicit controls;
        // it cannot send or cancel work, or convert uncertainty into completion.
        var deadline = DateTimeOffset.UtcNow.AddHours(24);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ThrowIfAutomaticRecoverySuperseded();
            if (CurrentArbitrationFacts().F11Active || CapabilityBlockReason() is not null) return false;
            var run = _runs.Load(runId);
            if (run is not { State: WorkflowRunState.Unknown, StopRequested: false,
                OnlineHoeingHandoff: null, OnlineHoeingGateParked: false,
                CurrentSubmission: { Intent: SubmitIntentState.Accepted, SendAttempted: true,
                    JobId.Length: > 0, OriginalRequestEvidence: not null } sub }
                || IsExecutionResetReleased(run) || IsDriving(run.WorkflowId)
                || _clientAccessor() is not { } client) return false;
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            slice.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var observed = await CreateProductionBoundary(client)
                    .AwaitSubmissionExitAsync(run, sub, slice.Token).ConfigureAwait(false);
                return !observed.Uncertain && observed.ExecutionExitConfirmed;
            }
            catch (OperationCanceledException) when (!_shutdownCts.IsCancellationRequested && slice.IsCancellationRequested) { }
        }
        return false;
    }
}
