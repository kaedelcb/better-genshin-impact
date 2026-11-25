using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private readonly SemaphoreSlim _roomOrchestrationDirectAdmissionGate = new(1, 1);
    private readonly SemaphoreSlim _roomOrchestrationReconcileGate = new(1, 1);

    internal async Task<ExternalStartAdmissionOutcome> AdmitRoomOrchestrationStartAsync(ExternalStartAdmissionRequest request, CancellationToken ct)
    {
        if (request.RoomOrchestration is not { } launch || CommandExecutor.ValidateRoomOrchestrationLaunch(launch) is not null
            || request.Namespace != "system" || request.WireSubmitKey != CommandExecutor.RoomOrchestrationTaskKey(launch.IntentId)
            || !string.IsNullOrEmpty(request.RequestIdentity) || !_admissionWired)
            return new(ExternalStartAdmissionStatus.Rejected, "invalid_request", "编排后继准入身份不完整，未发送");
        using var sendLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        try { await _roomOrchestrationDirectAdmissionGate.WaitAsync(sendLifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(ExternalStartAdmissionStatus.Rejected, "start_cancelled", "编排后继已撤销，未发送"); }
        try
        {
            using var authorityScope = EnterExecutionAuthorityScope();
            AdmissionResult result;
            try { RequireRoomOrchestrationAuthority(launch, sendLifetime.Token); }
            catch (Exception ex) { return new(ExternalStartAdmissionStatus.Rejected, "start_cancelled", ex.Message); }
            result = await SubmitExternalStartViaAdmissionCoreAsync(request, sendLifetime.Token).ConfigureAwait(false);
            result = await RevokeUnacceptedRoomOrchestrationAdmissionAsync(result, request.WireSubmitKey!).ConfigureAwait(false);
            var receipt = ProjectExternalStartOutcome(request, result);
            // Observe independently of the UI and the original switch intention. The runtime may also
            // supply the same retained observation while persisting its view; both use the same settlement gate.
            if (result.Kind is (AdmissionResultKind.Accepted or AdmissionResultKind.Reconciling or AdmissionResultKind.NeedReconcile)
                && !string.IsNullOrEmpty(result.SubmissionIdentity))
                _ = ObserveDetachedRoomOrchestrationStartAsync(launch);
            return receipt;
        }
        finally { _roomOrchestrationDirectAdmissionGate.Release(); }
    }

    private async Task ObserveDetachedRoomOrchestrationStartAsync(RoomOrchestrationLaunch launch)
    {
        var ct = _shutdownCts.Token;
        var deadline = DateTime.UtcNow.AddHours(24);
        var epoch = WorkflowStopAuthority.Epoch(launch.Epoch);
        var key = CommandExecutor.RoomOrchestrationTaskKey(launch.IntentId);
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    var ext = _clientAccessor();
                    var current = WorkflowStopAuthority.Epoch(ext?.ServerEpoch);
                    if (current is not null && current != epoch) return; // replacement is not evidence of the original terminal
                    if (ext is { State: BgiExternalLinkState.Ready } && current == epoch
                        && await ext.QueryTaskQueueStatusByKeyAsync(key, ct).ConfigureAwait(false) is { } status
                        && await ReconcileRoomOrchestrationTaskAsync(launch.IntentId, launch.Epoch, status, ct).ConfigureAwait(false)
                        && RoomOrchestrationTerminal(status)) return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { TryLog("[任务编排] 原后继任务观察暂不可用：" + ex.Message); }
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static bool RoomOrchestrationTerminal(BgiTaskQueueStatus status)
        => status.ExecutionExitConfirmed && status.Status is ("completed" or "failed" or "queueCancelled");

    // The retained key and original epoch select the original admission; never replay, retry or
    // generate a second operation. Return true for a settled terminal only after ledger closure.
    internal async Task<bool> ReconcileRoomOrchestrationTaskAsync(string intentId, BgiEpoch epoch,
        BgiTaskQueueStatus status, CancellationToken ct)
    {
        var epochKey = WorkflowStopAuthority.Epoch(epoch);
        var key = CommandExecutor.RoomOrchestrationTaskKey(intentId);
        if (!_admissionWired || string.IsNullOrWhiteSpace(intentId) || status.IdempotencyKey != key
            || WorkflowStopAuthority.Epoch(status.Epoch) != epochKey
            || !Guid.TryParse(status.TaskHandle, out var job) || job == Guid.Empty
            || status.Status is not ("pending" or "running" or "completed" or "failed" or "queueCancelled")) return false;
        await _roomOrchestrationReconcileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var authorityScope = EnterExecutionAuthorityScope();
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var read = _admissionStore!.Read();
            if (!IsCompleteAdmissionRead(read) || read.File?.Lease is not { } lease) return false;
            OperationRecord? ReadOperation(LeaseHandoffSegment? handoff) => AllAdmissionOperations(handoff)
                .SingleOrDefault(op => op.WireSubmitKey == key && op.TargetEpoch == epochKey && op.OperationType == OperationType.ExternalStart);
            var op = ReadOperation(read.File.Handoff);
            if (op is null || op.ConflictPending || op.MergedInto is not null || op.LastSendSeq != 1
                || string.IsNullOrWhiteSpace(op.SubmissionIdentity)
                || op.AcceptanceClaim?.JobId is { } claimed && claimed != status.TaskHandle) return false;
            if (op.RequestState is OperationRequestState.Reconciling or OperationRequestState.Sending or OperationRequestState.Granted
                && op.PendingTerminal is null)
            {
                var accepted = await _admission!.SettleReconciledAsync(op.RequestIdentity,
                    new ReconcileSettlement.Accepted(op.SubmissionIdentity, op.LastSendSeq,
                        op.AcceptanceClaim?.EvidenceSource ?? "ext:room-orchestration-retained-key", null, status.TaskHandle)).ConfigureAwait(false);
                if (accepted.Kind != AdmissionResultKind.Accepted) return false;
                read = _admissionStore.Read();
                if (!IsCompleteAdmissionRead(read) || read.File?.Lease is not { } currentLease
                    || currentLease.LeaseId != lease.LeaseId || currentLease.OwnerEpoch != lease.OwnerEpoch) return false;
                op = ReadOperation(read.File.Handoff);
            }
            if (op is null || op.ConflictPending || op.RequestState is not (OperationRequestState.Accepted or OperationRequestState.Reconciling or OperationRequestState.TerminalCompleted)) return false;

            var root = _admissionRoot;
            if (string.IsNullOrWhiteSpace(root)) return false;
            var readLedger = new ExternalStartLedger(root).Read();
            if (!readLedger.Valid) return false;
            var entry = readLedger.File?.Entries.SingleOrDefault(e => e.SubmissionIdentity == op.SubmissionIdentity && e.SendSeq == op.LastSendSeq);
            if (entry?.JobId != status.TaskHandle || entry.TargetBgiEpoch != epochKey) return false;
            if (!RoomOrchestrationTerminal(status)) return op.RequestState == OperationRequestState.Accepted;

            // A replay of the same terminal uses its first persisted observation timestamp and source.
            // Do not manufacture a new semantic terminal or overwrite conflicting evidence.
            var kind = status.Cancelled || status.Status == "queueCancelled" ? ExecutionResultKind.Cancelled
                : status.Status == "failed" ? ExecutionResultKind.Failed : ExecutionResultKind.Succeeded;
            var error = kind == ExecutionResultKind.Failed ? status.ErrorCode ?? "unknown" : null;
            var prior = op.ExecutionResult;
            if (prior is not null && (prior.Kind != kind || prior.RawTerminal != status.Status || prior.JobId != status.TaskHandle
                || prior.ExecutionErrorCode != error)) return false;
            var source = prior?.EvidenceSource ?? "ext:room-orchestration-retained-result";
            var at = prior?.ObservedAtUtc ?? DateTimeOffset.UtcNow;
            if (op.RequestState == OperationRequestState.TerminalCompleted)
                return prior is not null && entry.State == LedgerEntryState.Terminal && read.File.Handoff?.Submission?.SubmissionIdentity != op.SubmissionIdentity;
            var completion = kind switch
            {
                ExecutionResultKind.Cancelled => ExternalStartCompletion.CancelledWith(status.Status!, source, at, status.TaskHandle),
                ExecutionResultKind.Failed => ExternalStartCompletion.ExecutionFailedWith(status.Status!, error!, source, at, status.TaskHandle),
                _ => ExternalStartCompletion.SucceededWith(status.Status!, source, at, status.TaskHandle),
            };
            var result = await _admission!.SettleCompletionAsync(op.RequestIdentity, op.SubmissionIdentity, op.LastSendSeq, completion,
                acceptanceJobId: status.TaskHandle, expectedOwnerLeaseId: lease.LeaseId, expectedOwnerEpoch: lease.OwnerEpoch).ConfigureAwait(false);
            return result.ResponsibilityState == ResponsibilityState.Settled && result.JobId == status.TaskHandle
                && result.Kind is (AdmissionResultKind.Accepted or AdmissionResultKind.Cancelled or AdmissionResultKind.ExecutionFailed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { TryLog("[任务编排] 后继原发送结算待核对：" + ex.Message); return false; }
        finally { _roomOrchestrationReconcileGate.Release(); }
    }
}
