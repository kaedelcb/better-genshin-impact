using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record ExecutionResetProcessObservation(string Disposition, long? ObservedStartTicksUtc = null);

public sealed partial class TaskCenterHost
{
    private readonly HashSet<string> _executionResetWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private int _executionResetCount;
    internal Func<CancellationToken, Task<string?>>? ExecutionResetConnectedEpochForTest { get; set; }
    internal Func<string, ExecutionResetProcessObservation>? ExecutionResetProcessObserverForTest { get; set; }

    public static bool IsExecutionResetReleased(WorkflowRunRecord run) => ExecutionResetEvidence.Valid(run);

    /// <summary>放弃选中计划的旧执行；计划和业务历史不动，只有退出/零发送事实及仲裁隔离均确认后才解除占用。</summary>
    public async Task<HostActionResult> ResetWorkflowExecutionStateAsync(string workflowId)
    {
        DriveEntry? drive;
        try
        {
            lock (_gate)
            {
                if (_shutdown) return HostActionResult.Unavailable("任务中心已关闭，未重置执行状态。");
                if (CapabilityBlockReason() is { } blocked) return HostActionResult.Unavailable(blocked);
                var snapshot = _workflows.LoadSnapshot(workflowId);
                if (!string.Equals(snapshot.Document.WorkflowId, workflowId, StringComparison.Ordinal))
                    return HostActionResult.Unavailable("计划身份不一致，未重置执行状态。");
                _drives.TryGetValue(workflowId, out drive);
                if (_executionResetWorkflows.Contains(workflowId)
                    || _reservedWorkflows.Contains(workflowId) && drive is null)
                    return HostActionResult.Unavailable("计划正在准备或重置，请等待当前操作结束。");
                var integrity = _runs.ListWithIntegrity("execution-reset-prepare");
                if (HasUnattributableResetRecord(integrity, workflowId))
                    return HostActionResult.Unavailable("运行记录损坏或归属不能确认，原件保留，未重置执行状态。");
                _executionResetWorkflows.Add(workflowId);
                _reservedWorkflows.Add(workflowId);
                Interlocked.Increment(ref _executionResetCount);
            }
        }
        catch (Exception ex) { return HostActionResult.Unavailable("重置准备失败，原件保留：" + ex.Message); }

        try
        {
            // A live runner must consume Stop and finish its full observation lifecycle first.
            // ObserveDrive preserves our reservation until this transaction releases it.
            if (drive is not null)
            {
                var runId = drive.RunId ?? _runs.List().Where(r => r.WorkflowId == workflowId
                    && ActiveStates.Contains(r.State) && !IsExecutionResetReleased(r))
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefault()?.RunId;
                if (runId is null) return HostActionResult.Unavailable("在途驱动的运行身份不可确认，未重置执行状态。");
                var stop = await RequestRunActionAsync(runId, WorkflowRunAction.Stop).ConfigureAwait(false);
                if (!stop.Ok) return HostActionResult.Unavailable("运行停止未确认，未重置执行状态：" + stop.Message);
                try { await drive.Completion.Task.WaitAsync(TimeSpan.FromSeconds(35), _shutdownCts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    return HostActionResult.Unavailable("运行仍未确认退出，停止意图已保留；未完成重置，可稍后重试。");
                }
            }

            if (_admissionWired)
                await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            var records = ReadResetRecords(workflowId);
            var changed = 0;
            foreach (var original in records)
            {
                if (IsExecutionResetReleased(original)) continue;
                if (original.ExecutionResetRelease is not null)
                    return HostActionResult.Unavailable("已有重置证据与运行事实冲突，原件保留，未宣告重置完成。");
                // A fully settled historical failure/success is immutable and needs no new audit record.
                if (original.IsTerminal && !RunStore.HasUnresolvedTerminalResponsibility(original))
                {
                    if (HasUnclosedResetAdmission(original.RunId))
                    {
                        var terminalError = RetireResetAdmission(original);
                        if (terminalError is not null) return HostActionResult.Unavailable("历史运行的受理责任未结清：" + terminalError);
                    }
                    continue;
                }

                var run = _runs.Load(original.RunId);
                if (run is null || run.WorkflowId != workflowId)
                    return HostActionResult.Unavailable("运行身份已变化，未宣告重置完成。");
                List<ExecutionResetProcessExitRecord> exits = [];
                string? verifiedCurrentEpoch = null;
                if (RunStore.HasUnresolvedTerminalResponsibility(run))
                {
                    verifiedCurrentEpoch = await ReadTrustedResetEpochAsync(_shutdownCts.Token).ConfigureAwait(false);
                    if (verifiedCurrentEpoch is null)
                        return HostActionResult.Unavailable("BGI 当前实例和执行纪元不可确认，旧执行责任保留；未完成重置。");
                    var epochs = ExecutionResetEvidence.UnresolvedEpochs(run);
                    if (epochs is null)
                        return HostActionResult.Unavailable("旧执行缺少完整原纪元，不能确认是否仍在运行；未完成重置。");
                    if (epochs.Contains(verifiedCurrentEpoch))
                    {
                        // Same-epoch facts must be cancelled and observed through the original job identity.
                        var stop = await RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop).ConfigureAwait(false);
                        if (!stop.Ok) return HostActionResult.Unavailable("原执行停止或退出尚未确认：" + stop.Message);
                        run = _runs.Load(run.RunId)!;
                        if (RunStore.HasUnresolvedTerminalResponsibility(run))
                            return HostActionResult.Unavailable("原执行仍有未确认责任，保留历史，未完成重置。");
                    }
                    else
                    {
                        var scope = TryGetAdmissionScopeForResume(run.RunId, workflowId) ?? run.AdmissionSourceScope;
                        if (scope is null || !scope.StartsWith("bgi:local:", StringComparison.Ordinal)
                            || !ExecutionResetEvidence.TryEpoch(scope[10..], out _, out _)
                            || !epochs.Contains(scope[10..])
                            || run.Target?.InstanceId is { Length: > 0 } targetInstance && targetInstance != "local"
                            || run.Target?.BgiEpoch is { Length: > 0 } targetEpoch && !epochs.Contains(targetEpoch))
                            return HostActionResult.Unavailable("旧执行缺少可核对的本机固定来源，不能按进程退出解除责任。");
                        foreach (var epoch in epochs)
                        {
                            var observed = ObserveResetProcess(epoch);
                            ExecutionResetEvidence.TryEpoch(epoch, out _, out var originalTicks);
                            if (!(observed.Disposition == "process_absent" && observed.ObservedStartTicksUtc is null
                                || observed.Disposition == "process_id_reused" && observed.ObservedStartTicksUtc is > 0
                                    && observed.ObservedStartTicksUtc != originalTicks))
                                return HostActionResult.Unavailable("旧 BGI 进程仍存在或退出事实不可确认，未完成重置。");
                            exits.Add(new ExecutionResetProcessExitRecord
                            {
                                OriginalEpoch = epoch, CurrentEpoch = verifiedCurrentEpoch,
                                Disposition = observed.Disposition, ObservedStartTicksUtc = observed.ObservedStartTicksUtc,
                                ObservedAtUtc = DateTimeOffset.UtcNow,
                            });
                        }
                    }
                }

                // Only exact immutable wait bindings may be cancelled; unrelated items are never touched.
                if (run.LocalWaitDecision?.Binding is { } binding)
                {
                    if (binding.RunId != run.RunId || binding.WorkflowId != workflowId
                        || LocalWaitQueue.Cancel(binding, "用户重置此计划执行状态，保留原运行历史", DateTimeOffset.UtcNow)
                            == LocalWaitBindingCancelResult.PayloadMismatch)
                        return HostActionResult.Unavailable("本地等待身份或载荷冲突，未完成重置。");
                }
                var beforeHash = ExecutionResetEvidence.FactsHash(run);
                if (_admissionWired)
                {
                    var admission = RetireResetAdmission(run);
                    if (admission is not null) return HostActionResult.Unavailable(admission);
                }
                if (verifiedCurrentEpoch is not null
                    && await ReadTrustedResetEpochAsync(_shutdownCts.Token).ConfigureAwait(false) != verifiedCurrentEpoch)
                    return HostActionResult.Unavailable("核对期间 BGI 实例发生变化，未发布重置完成证据，请重试。");
                var fresh = _runs.Load(run.RunId);
                if (fresh is null || ExecutionResetEvidence.FactsHash(fresh) != beforeHash)
                    return HostActionResult.Unavailable("核对期间运行事实发生变化，原件保留，未宣告重置完成。");
                var release = new ExecutionResetReleaseRecord
                {
                    ResetId = Guid.NewGuid().ToString("N"), RunId = fresh.RunId, WorkflowId = workflowId,
                    WireRunId = fresh.WireRunId, OriginalState = fresh.State, FactsHash = beforeHash,
                    CompletedAtUtc = DateTimeOffset.UtcNow, ProcessExits = exits,
                };
                if (_runs.TryPublishExecutionResetRelease(run.RunId, release) is null)
                    return HostActionResult.Unavailable("重置结果未能持久化读回，原历史保留，未宣告重置完成。");
                changed++;
            }

            var finalRecords = ReadResetRecords(workflowId);
            if (finalRecords.Any(run => !IsExecutionResetReleased(run)
                    && (!run.IsTerminal || RunStore.HasUnresolvedTerminalResponsibility(run)))
                || finalRecords.Any(run => HasUnclosedResetAdmission(run.RunId))
                || LocalWaitQueue.Load().Any(item => item.WorkflowId == workflowId && item.State == LocalWaitItemState.Waiting))
                return HostActionResult.Unavailable("仍有未结执行、等待或仲裁责任，已保留处置结果；未宣告重置完成。");
            NotifyStateChanged();
            return HostActionResult.Effective(changed == 0
                ? "当前计划的执行状态已清空；计划内容和失败历史保留，可重新执行或删除。"
                : "已重置当前计划的执行状态；计划内容和原运行历史保留，未重发旧任务，可重新执行或删除。");
        }
        catch (Exception ex) { return HostActionResult.Unavailable("重置未完成，原计划和历史保留：" + ex.Message); }
        finally
        {
            lock (_gate)
            {
                _executionResetWorkflows.Remove(workflowId);
                Interlocked.Decrement(ref _executionResetCount);
                if (!_drives.ContainsKey(workflowId)) _reservedWorkflows.Remove(workflowId);
            }
            NotifyStateChanged();
        }
    }

    // Reset first proves that the exact old executor is gone. Ordinary startup's
    // historical administrative retirement must not preempt that proof. It resumes
    // on the next ordinary admission entry without omitting any other workflow.
    private void CompleteDeferredAdministrativeRecovery()
    {
        if (Volatile.Read(ref _executionResetCount) != 0) return;
        lock (_admissionInitGate)
        {
            if (!_admissionAdministrativeRecoveryPending || _admission is null
                || Volatile.Read(ref _executionResetCount) != 0) return;
            RetireRecoveredUnknownSubmissionAfterTakeover(_admission);
            _admissionAdministrativeRecoveryPending = false;
        }
    }

    private static bool HasUnattributableResetRecord(RunStoreListSnapshot snapshot, string workflowId)
        => snapshot.UnknownFiles.Any(path => !CanConfirmUnrelatedRun(path, workflowId))
            || snapshot.Records.Any(run => string.IsNullOrWhiteSpace(run.WorkflowId));

    private IReadOnlyList<WorkflowRunRecord> ReadResetRecords(string workflowId)
    {
        var snapshot = _runs.ListWithIntegrity("execution-reset-readback");
        if (HasUnattributableResetRecord(snapshot, workflowId))
            throw new RunRecordConflictException("运行记录损坏或归属不能确认。");
        return snapshot.Records.Where(run => string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static IEnumerable<OperationRecord> ResetAdmissionOperations(LeaseHandoffSegment? handoff)
        => AllAdmissionOperations(handoff).Concat(handoff?.RetiredUnknownOperations.Select(r => r.Operation) ?? []);

    private bool HasUnclosedResetAdmission(string runId)
    {
        if (!_admissionWired) return false;
        var read = _admissionStore?.Read();
        if (read is null || !IsCompleteAdmissionRead(read)) return true;
        var handoff = read.File?.Handoff;
        var operations = AllAdmissionOperations(handoff).Where(op => op.RunBinding == runId).ToList();
        return operations.Any(op => !IsAdmissionTerminalOrClosed(op) || op.ConflictPending || op.PreemptConfirmPending
                || op.Zone == OperationZone.Active && op.RequestState == OperationRequestState.RetryableRejected)
            || handoff?.Pending is { } pending && pending.SuspendedRunIdentity == runId
            || handoff?.Submission is { } submission && operations.Any(op => op.SubmissionIdentity == submission.SubmissionIdentity
                && op.LastSendSeq == submission.SendSeq);
    }

    private string? RetireResetAdmission(WorkflowRunRecord run)
    {
        var read = _admissionStore?.Read();
        if (read is null || !IsCompleteAdmissionRead(read)) return "仲裁存储不可确认，未完成重置。";
        var handoff = read.File?.Handoff;
        var mappings = ResetAdmissionOperations(handoff).Where(op => op.RunBinding == run.RunId).ToList();
        if (!OriginalAdmissionMappingsPresent(run, mappings)) return "原受理映射缺失，不能安全解除执行责任。";
        if (handoff?.Pending is { } pending && pending.SuspendedRunIdentity == run.RunId)
            return "原执行仍有未结交接票据，未解除执行责任。";
        var retired = _admission!.RetireUnknownSubmission(run.RunId, "用户重置执行状态：原运行历史保留，已确认零外部责任或原BGI进程退出，不重发");
        if (retired.Kind != AdmissionResultKind.Accepted
            && retired.ReasonCode is not ("no_submission" or "submission_owner_mismatch"))
            return "原发送责任隔离失败，未完成重置：" + retired.ReasonCode;
        _admission.RetireUnknownOperations(run.RunId,
            "用户重置执行状态：原操作快照保留，已确认零外部责任或原BGI进程退出，不重发", includeRetryableRejected: true);
        var final = _admissionStore!.Read();
        if (!IsCompleteAdmissionRead(final) || HasUnclosedResetAdmission(run.RunId))
            return "仲裁责任未能全部结清或隔离，未完成重置。";
        var preserved = ResetAdmissionOperations(final.File?.Handoff).Where(op => op.RunBinding == run.RunId).ToList();
        if (!OriginalAdmissionMappingsPresent(run, preserved)
            || mappings.Any(old => !preserved.Any(now => now.RequestIdentity == old.RequestIdentity
                && now.OperationType == old.OperationType && now.SubmissionIdentity == old.SubmissionIdentity
                && now.LastSendSeq == old.LastSendSeq)))
            return "原受理责任保留读回未成立，未完成重置。";
        return null;
    }

    private async Task<string?> ReadTrustedResetEpochAsync(CancellationToken ct)
    {
        if (ExecutionResetConnectedEpochForTest is { } seam)
        {
            var tested = await seam(ct).ConfigureAwait(false);
            return ExecutionResetEvidence.TryEpoch(tested, out _, out _) ? tested : null;
        }
        var client = _clientAccessor();
        if (client?.State != BgiExternalLinkState.Ready || client.ServerEpoch is not { } before) return null;
        var epoch = before.ProcessId + ":" + before.StartTicksUtc;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var jobs = await client.QueryJobListAsync(budget.Token).ConfigureAwait(false);
        if (client.State != BgiExternalLinkState.Ready || !ReferenceEquals(client, _clientAccessor())
            || client.ServerEpoch is not { } after || jobs?.Epoch is not { } observed
            || before.ProcessId != after.ProcessId || before.StartTicksUtc != after.StartTicksUtc
            || before.ProcessId != observed.ProcessId || before.StartTicksUtc != observed.StartTicksUtc
            || ObserveResetProcess(epoch).Disposition != "same_process") return null;
        return epoch;
    }

    private ExecutionResetProcessObservation ObserveResetProcess(string epoch)
    {
        if (ExecutionResetProcessObserverForTest is { } seam) return seam(epoch);
        if (!ExecutionResetEvidence.TryEpoch(epoch, out var pid, out var ticks)) return new("unknown");
        try
        {
            using var process = Process.GetProcessById(pid);
            var actualTicks = process.StartTime.ToUniversalTime().Ticks;
            if (actualTicks != ticks) return new("process_id_reused", actualTicks);
            return process.HasExited ? new("process_absent") : new("same_process", actualTicks);
        }
        catch (ArgumentException) { return new("process_absent"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return new("unknown"); }
    }
}
