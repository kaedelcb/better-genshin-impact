using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private readonly SemaphoreSlim _completionWorkflowGate = new(1, 1);

    internal static WorkflowNodeCursor ResolveCompletionWorkflowCursor(TaskConflictPolicySettings policy, WorkflowSnapshot snapshot)
    {
        if (policy.SpecifiedTaskType != "workflow" || policy.SpecifiedWorkflowId != snapshot.Document.WorkflowId
            || policy.SpecifiedStartMode is not ("beginning" or "node"))
            throw new CommandExecutor.CompletionStartException("invalid_request", "计划表完成动作缺少明确的目标或起点");
        if (string.IsNullOrWhiteSpace(policy.SpecifiedConfigRevision) || policy.SpecifiedConfigRevision != snapshot.Revision)
            throw new CommandExecutor.CompletionStartException("configuration_revision_mismatch", "计划表已改变，请重新选择起点");
        var plan = new WorkflowPlan(snapshot.Document);
        // The structural entry accounts for composition and lanes; storage order is not the head.
        var nodeId = policy.SpecifiedStartMode == "beginning" ? plan.FirstOccurrence()?.NodeId : policy.SpecifiedStartTaskId;
        if (string.IsNullOrWhiteSpace(nodeId))
            throw new CommandExecutor.CompletionStartException("specified_task_missing", "计划表起点已不存在");
        var matches = snapshot.Document.Nodes.Where(n => n.NodeId == nodeId).ToArray();
        if (matches.Length != 1 || policy.SpecifiedStartMode == "node"
            && matches[0].ExtensionData?.TryGetValue("planDisabled", out var disabled) == true
            && disabled.ValueKind == JsonValueKind.True)
            throw new CommandExecutor.CompletionStartException("specified_task_missing", "计划表起点已删除、停用或身份不唯一");
        try { return plan.ExplicitEntryCursor(nodeId)!; }
        catch (InvalidOperationException ex) { throw new CommandExecutor.CompletionStartException("specified_task_missing", ex.Message); }
    }

    private WorkflowRunRecord? FindCompletionWorkflowRun(OnlineHoeingBatch batch)
    {
        var all = _runs.ListWithIntegrity();
        if (all.UnknownFiles.Count != 0) throw new IOException("运行台账不完整，不能把未读到解释为尚未登记");
        var matches = all.Records.Where(r => r.Handoffs.Any(h => h.IntentKey == batch.PolicyRequestKey)).ToArray();
        if (matches.Length > 1) throw new IOException("同一完成动作绑定了多个运行，保留原记录待核对");
        var run = matches.SingleOrDefault();
        if (run is null && (batch.PolicyStartAttempted || batch.PolicyExecution?.WorkflowRunId is not null))
            throw new IOException("原完成动作运行记录暂不可读，保留原 runId，不新建另一运行");
        if (run is not null && (run.WorkflowId != batch.Policy?.SpecifiedWorkflowId
            || run.WorkflowRevision != batch.Policy.SpecifiedConfigRevision
            || run.StopAuthority?.Epoch != WorkflowStopAuthority.Epoch(batch.ExecutionEpoch)
            || run.StopAuthority?.Version != batch.StateContinuationStopVersion
            || run.StopAuthority?.IntentId != batch.PolicyRequestKey
            || run.StopAuthority?.IntentTimestamp != batch.CompletionIntentTimestamp
            || batch.PolicyExecution?.WorkflowRunId is { } held && held != run.RunId))
            throw new CommandExecutor.CompletionStartException("idempotency_conflict", "原完成动作与已登记的计划表运行不一致");
        return run;
    }

    private static void RequireCompletionWorkflowAuthority(OnlineHoeingBatch batch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        batch.ObserveRecoveryStopSignal();
        if (!batch.LocalCompletionReady || batch.IsCancellationRequested || batch.ExplicitStopRequested
            || batch.Policy is not { Policy: TaskConflictPolicy.RunSpecified, SpecifiedTaskType: "workflow" })
            throw new BgiStateContinuationStoppedException("原批次已撤销计划表完成动作");
    }

    // This automatic successor inherits the batch's stop version. It never calls the manual
    // StartWorkflowAsync entry or acquires a fresh explicit intent after a stop.
    internal async Task<CommandResult> StartCompletionWorkflowAsync(OnlineHoeingBatch batch, CancellationToken ct)
    {
        await _completionWorkflowGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireCompletionWorkflowAuthority(batch, ct);
            using var configurationOperation = TryBeginConfigurationOperation();
            if (configurationOperation is null) return new CommandResult { ErrorCode = "state_unavailable", Message = "配置迁移尚未结束" };
            if (!_admissionWired) return new CommandResult { ErrorCode = "state_unavailable", Message = "任务中心统一准入未开放" };
            lock (_gate)
            {
                if (_shutdown || CapabilityBlockReason() is not null)
                    return new CommandResult { ErrorCode = "state_unavailable", Message = "计划表执行宿主尚未就绪" };
            }
            await EnsureRecoveredAsync().ConfigureAwait(false);
            RequireCompletionWorkflowAuthority(batch, ct);
            var run = FindCompletionWorkflowRun(batch);
            if (run is null)
            {
                var policy = batch.Policy!;
                WorkflowSnapshot snapshot;
                try { snapshot = _workflows.LoadSnapshot(policy.SpecifiedWorkflowId); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
                { return new CommandResult { ErrorCode = "specified_task_missing", Message = ex.Message }; }
                if (snapshot.Document.Activation?.Status == "candidate-ready")
                    return new CommandResult { ErrorCode = "specified_task_missing", Message = "候选计划表尚未激活，不能作为完成动作执行" };
                var cursor = ResolveCompletionWorkflowCursor(policy, snapshot);
                if (ValidateManagedFlowSnapshot(policy.SpecifiedWorkflowId, snapshot.Revision) is { } blocked)
                    return new CommandResult { ErrorCode = "specified_task_missing", Message = blocked };
                if (batch.ExecutionEpoch is not { } epoch || batch.StateContinuationStopVersion is not { } version
                    || batch.CompletionIntentTimestamp <= 0 || CurrentBgiEpoch() != WorkflowStopAuthority.Epoch(epoch))
                    return new CommandResult { ErrorCode = "state_unavailable", Message = "原批次停止基线或 BGI 身份尚未确认" };
                var authority = new WorkflowStopAuthorityRecord(WorkflowStopAuthority.Epoch(epoch)!, version,
                    batch.PolicyRequestKey, batch.CompletionIntentTimestamp, Stopwatch.Frequency);
                var runner = CreateRunner(_clientAccessor());
                await runner.ValidateInheritedStopAuthorityAsync(authority, ct).ConfigureAwait(false);
                try { runner.PreflightStartable(snapshot, cursor.NodeId); }
                catch (InvalidOperationException ex)
                { return new CommandResult { ErrorCode = "specified_task_missing", Message = ex.Message }; }
                RequireCompletionWorkflowAuthority(batch, ct);
                lock (_gate)
                {
                    if (_shutdown || _manualResetActive != 0 || _reservedWorkflows.Contains(policy.SpecifiedWorkflowId)
                        || _drives.ContainsKey(policy.SpecifiedWorkflowId)
                        || _runs.List().Any(r => r.WorkflowId == policy.SpecifiedWorkflowId && ActiveStates.Contains(r.State)
                            && !IsExecutionResetReleased(r)) || HasBlockingUnknownRun(policy.SpecifiedWorkflowId))
                        return new CommandResult { ErrorCode = "state_unavailable", Message = "计划表原运行尚未退出，保留同一完成动作" };
                    RequireCompletionWorkflowAuthority(batch, ct);
                    if (ValidateManagedFlowSnapshot(policy.SpecifiedWorkflowId, snapshot.Revision) is { } changed)
                        return new CommandResult { ErrorCode = "configuration_revision_mismatch", Message = changed };
                    // The request key and real runId are bound in the same durable create.
                    run = _runs.CreateRun(policy.SpecifiedWorkflowId, snapshot.Revision,
                        note: "联机批次绑定完成动作：" + batch.BatchId,
                        handoff: new HandoffIdentity { IntentKey = batch.PolicyRequestKey, ExecutionId = batch.BatchId,
                            StepId = "post-hoeing", Mode = StartupHandoffModes.Start },
                        stopAuthority: authority, initialCursor: cursor);
                }
            }
            // A crash after the durable create but before registration leaves an unexecuted
            // Interrupted record. Return only that same record to registration, preserving its
            // key/cursor/authority; an existing operation is continued rather than recreated.
            if (run.State == WorkflowRunState.Interrupted && !run.StopRequested && run.CurrentSubmission is null
                && run.NodeOutcomes.Count == 0 && !RunStore.HasUnresolvedTerminalResponsibility(run))
            {
                bool driving;
                lock (_gate) driving = _drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId);
                if (!driving && _runs.UpdateMergingIf(run.RunId, current =>
                    {
                        if (current.State != WorkflowRunState.Interrupted || current.StopRequested
                            || current.CurrentSubmission is not null || current.NodeOutcomes.Count != 0
                            || RunStore.HasUnresolvedTerminalResponsibility(current)) return false;
                        current.State = WorkflowRunState.Planned;
                        return true;
                    }, out var unexecuted) && unexecuted is not null) run = unexecuted;
            }
            var original = run.StopAuthority;
            if (original is null || !WorkflowStopAuthority.TryEpoch(original.Epoch, out var executionEpoch))
                return new CommandResult { ErrorCode = "state_unavailable", Message = "原运行缺少停止来源" };
            // Record even a late registration so stop can address this exact run. Registration is
            // never reported as completed; only QueryCompletionWorkflowAsync observes its terminal.
            batch.RecordPolicyExecution(new(run.RunId, executionEpoch, false, WorkflowRunId: run.RunId));
            batch.PolicyStartAttempted = true;
            batch.PersistRecovery?.Invoke();
            RequireCompletionWorkflowAuthority(batch, ct);
            if (run.State == WorkflowRunState.Planned)
            {
                var result = await SubmitCompletionWorkflowRegistrationAsync(batch, run, ct).ConfigureAwait(false);
                if (result.Kind is not (AdmissionResultKind.Accepted or AdmissionResultKind.Reconciling))
                    return new CommandResult { ErrorCode = result.ReasonCode ?? "state_unavailable", Message = result.Detail };
            }
            else if (run.State is WorkflowRunState.Interrupted or WorkflowRunState.Unknown)
            {
                bool driving;
                lock (_gate) driving = _drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId);
                if (!driving)
                {
                    // This is automatic recovery of the same run, so explicit controls retain
                    // their control revision and can supersede this call while it waits.
                    var previousRecovery = _automaticLeaseRecovery.Value;
                    var recovery = new AutomaticLeaseRecovery(run.RunId, Interlocked.Read(ref _controlRevision));
                    _automaticLeaseRecovery.Value = recovery;
                    try { await ResumeRunAsync(run.RunId).ConfigureAwait(false); }
                    finally { Volatile.Write(ref recovery.Active, false); _automaticLeaseRecovery.Value = previousRecovery; }
                }
            }
            return new CommandResult { Status = "success", JobId = run.RunId, TargetProcessId = executionEpoch.ProcessId,
                TargetStartTicksUtc = executionEpoch.StartTicksUtc.ToString(), Message = "绑定计划表已登记，等待整段运行结束：" + run.RunId };
        }
        catch (CommandExecutor.CompletionStartException ex) { return new CommandResult { ErrorCode = ex.Code, Message = ex.Message }; }
        catch (StartupSourceStoppedException ex) { return new CommandResult { ErrorCode = "completion_not_authorized", Message = ex.Message }; }
        finally { _completionWorkflowGate.Release(); }
    }

    private async Task<AdmissionResult> SubmitCompletionWorkflowRegistrationAsync(OnlineHoeingBatch batch,
        WorkflowRunRecord run, CancellationToken ct)
    {
        RequireCompletionWorkflowAuthority(batch, ct);
        var read = _admissionStore!.Read();
        if (!IsCompleteAdmissionRead(read)) throw new IOException("原准入存储尚未确认");
        var previous = AllAdmissionOperations(read.File?.Handoff).Where(op => op.RunBinding == run.RunId
            && op.OperationType == OperationType.FlowRegistration).ToArray();
        if (previous.Length > 1) throw new IOException("计划表完成动作准入身份有歧义");
        var prior = previous.SingleOrDefault();
        var request = new AdmissionRequest
        {
            Namespace = "system", Kind = prior is null ? AdmissionKind.Create : AdmissionKind.ContinueUse,
            RequestIdentity = prior?.RequestIdentity ?? "", SourceDetail = "system:post-hoeing:workflow",
            RunBinding = run.RunId, WireSubmitKey = batch.PolicyRequestKey, OperationType = OperationType.FlowRegistration,
            CallerToken = ct,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:local:" + run.StopAuthority!.Epoch, Namespace = "system", WorkflowId = run.WorkflowId,
                TriggerOccurrenceId = "system:post-hoeing:" + batch.PolicyRequestKey,
                ResourceRef = "flow:" + run.WorkflowId, Intent = "start", Tier = ArbitrationTier.Plan,
                Priority = batch.Policy!.SpecifiedTaskPriority,
            },
        };
        // ContinueUse only reports a persisted retryable rejection. Re-enter admission with
        // that exact identity so the facade retains its original window, budget and send history.
        // Other states keep their existing continuation/reconciliation semantics.
        var result = prior?.RequestState == OperationRequestState.RetryableRejected
            ? await _admission!.RetryAsync(prior.RequestIdentity).ConfigureAwait(false)
            : await _admission!.SubmitAsync(request).ConfigureAwait(false);
        if (result.Kind == AdmissionResultKind.Accepted)
        {
            bool driving;
            lock (_gate) driving = _drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId);
            // A recovered accepted registration can have lost its in-memory driver before it
            // executed. Reuse that registration and Planned record, never allocate another run.
            if (!driving && _runs.Load(run.RunId)?.State == WorkflowRunState.Planned)
            {
                RequireCompletionWorkflowAuthority(batch, ct);
                var runner = CreateRunner(_clientAccessor());
                await runner.ValidateInheritedStopAuthorityAsync(run.StopAuthority, ct).ConfigureAwait(false);
                lock (_gate)
                    if (!_shutdown && !_drives.ContainsKey(run.WorkflowId) && _reservedWorkflows.Add(run.WorkflowId))
                        LaunchDrive(run.WorkflowId, runner, lifetime => runner.StartExistingRunAsync(run.RunId, lifetime.Token),
                            "接回原完成动作计划表", run.RunId);
            }
        }
        else if (result.Kind is AdmissionResultKind.TerminalRejected or AdmissionResultKind.NotSelected or AdmissionResultKind.F11Blocked)
            CleanupRejectedFlowRun(run, "绑定完成动作准入未受理：" + result.Detail);
        return result;
    }

    internal async Task<BgiTaskQueueStatus?> QueryCompletionWorkflowAsync(OnlineHoeingBatch batch, CancellationToken ct)
    {
        await EnsureRecoveredAsync().ConfigureAwait(false);
        var run = FindCompletionWorkflowRun(batch);
        if (run is null) return new BgiTaskQueueStatus { Status = "not_found", IdempotencyKey = batch.PolicyRequestKey, Epoch = batch.ExecutionEpoch };
        if (!run.IsTerminal && WorkflowStopAuthority.Current(run)?.Epoch != CurrentBgiEpoch()
            && await ContinueCompletionWorkflowOnReplacementAsync(batch, run, ct).ConfigureAwait(false))
            run = _runs.Load(run.RunId) ?? throw new IOException("接续后的原计划表记录暂不可读");
        if (!run.IsTerminal && !run.StopRequested && run.State is WorkflowRunState.Planned or WorkflowRunState.Interrupted or WorkflowRunState.Unknown)
        {
            await StartCompletionWorkflowAsync(batch, ct).ConfigureAwait(false);
            run = _runs.Load(run.RunId) ?? throw new IOException("原计划表运行暂不可读");
        }
        return CompletionWorkflowStatus(run, batch.PolicyRequestKey);
    }

    private async Task<bool> ContinueCompletionWorkflowOnReplacementAsync(OnlineHoeingBatch batch,
        WorkflowRunRecord run, CancellationToken ct)
    {
        if (!batch.PriorityRecoveryEnabled || run.IsTerminal || run.StopRequested
            || WorkflowStopAuthority.Current(run) is not { } source
            || !WorkflowStopAuthority.TryEpoch(source.Epoch, out var previous)
            || CommandExecutor.ProbeOriginalBgiProcess(previous) != false
            || _clientAccessor() is not { State: BgiExternalLinkState.Ready, ServerEpoch: { } replacement } ext
            || ViewModels.CoordinatedBatchLoop.SameEpoch(previous, replacement)) return false;
        lock (_gate)
            if (_shutdown || _manualResetActive != 0 || _drives.ContainsKey(run.WorkflowId)
                || _reservedWorkflows.Contains(run.WorkflowId) || !HasOriginalAdmissionMapping(run)) return false;
        RequireCompletionWorkflowAuthority(batch, ct);
        await ext.EnsureCoordinatedRecoveryStopAsync(batch, ct).ConfigureAwait(false);
        var stop = await ext.ReadStateContinuationStopVersionAsync(replacement, ct).ConfigureAwait(false);
        if (stop is null) return false;
        if (stop != 0) { batch.Cancel(); throw new BgiStateContinuationStoppedException("后继进程已有明确停止，原完成动作不续跑"); }
        var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
        if (snapshot.Revision != run.WorkflowRevision) return false;
        if (!_runs.AppendCompletionWorkflowProcessContinuation(run.RunId, batch, previous, replacement, out var retained)
            || retained is null) return false;
        var handedToDriver = false;
        try
        {
            lock (_gate)
            {
                if (_shutdown || _manualResetActive != 0 || _drives.ContainsKey(run.WorkflowId)
                    || !_reservedWorkflows.Add(run.WorkflowId)) return false;
                _runs.RetainPriorityProgress(retained);
            }
            RequireCompletionWorkflowAuthority(batch, ct);
            var runner = CreateRunner(_clientAccessor());
            handedToDriver = LaunchDrive(run.WorkflowId, runner, lifetime => runner.ResumeAsync(run.RunId, lifetime.Token),
                "原进程退出后接回同一绑定计划表", run.RunId).Ok;
            return handedToDriver;
        }
        finally
        {
            if (!handedToDriver)
                lock (_gate)
                    if (!_drives.ContainsKey(run.WorkflowId)) _reservedWorkflows.Remove(run.WorkflowId);
        }
    }

    internal static BgiTaskQueueStatus CompletionWorkflowStatus(WorkflowRunRecord run, string key)
    {
        var terminal = run.IsTerminal && !RunStore.HasUnresolvedTerminalResponsibility(run);
        return new BgiTaskQueueStatus
        {
            TaskHandle = run.RunId, IdempotencyKey = key,
            Epoch = WorkflowStopAuthority.TryEpoch(run.StopAuthority?.Epoch, out var epoch) ? epoch : null,
            ExecutionExitConfirmed = terminal, Cancelled = run.State == WorkflowRunState.Cancelled,
            Status = terminal ? run.State == WorkflowRunState.Succeeded ? "completed"
                : run.State == WorkflowRunState.Cancelled ? "queueCancelled" : "failed"
                : run.State switch
                {
                    WorkflowRunState.Planned => "queued", WorkflowRunState.Running => "running",
                    WorkflowRunState.Waiting or WorkflowRunState.Paused or WorkflowRunState.LocalWaitParking
                        or WorkflowRunState.OnlineHoeingParking => "waiting",
                    WorkflowRunState.Interrupted => "interrupted", WorkflowRunState.Completing => "completing",
                    _ => "result_unknown",
                },
            ErrorMessage = terminal && run.State != WorkflowRunState.Succeeded ? run.Note : null,
        };
    }

    internal async Task<bool> CancelCompletionWorkflowAsync(HoeingPolicyExecution execution)
    {
        if (execution.WorkflowRunId is not { } runId || execution.JobId != runId) return false;
        try
        {
            var run = _runs.Load(runId);
            if (run is null || run.StopAuthority?.Epoch != WorkflowStopAuthority.Epoch(execution.Epoch)) return false;
            await RequestRunActionAsync(runId, WorkflowRunAction.Stop).ConfigureAwait(false);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!budget.IsCancellationRequested)
            {
                run = _runs.Load(runId);
                if (run is not null && CompletionWorkflowStatus(run, "").ExecutionExitConfirmed) return true;
                await Task.Delay(100, budget.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { TryLog("[绑定完成动作] 原计划表停止未确认：" + ex.Message); }
        return false;
    }
}
