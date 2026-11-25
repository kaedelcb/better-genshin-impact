using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private const string RoomOrchestrationHandoffStep = "room-orchestration";
    private const string RoomOrchestrationRoomIdentityKey = "roomOrchestrationRoomIdentity";
    private readonly SemaphoreSlim _roomOrchestrationWorkflowGate = new(1, 1);

    // Explicit new intent notification; automatic admission, online handoff and recovery never issue it.
    internal Action<string>? RoomOrchestrationNewIntent { get; set; }

    private sealed class RoomOrchestrationRegistration(string runId, RoomOrchestrationLaunch launch,
        Func<CancellationToken, Task> verify)
    {
        private int _launchState; // 0 = prepared, 1 = entered driver registration, 2 = revoked before registration
        internal string RunId { get; } = runId;
        internal RoomOrchestrationLaunch Launch { get; } = launch;
        internal Func<CancellationToken, Task> Verify { get; } = verify;
        internal bool TryBeginLaunch() => Interlocked.CompareExchange(ref _launchState, 1, 0) == 0;
        internal bool RevokeBeforeLaunch() => Interlocked.CompareExchange(ref _launchState, 2, 0) == 0;
        internal bool DriverRegistered { get; set; }
    }

    internal static bool IsRoomOrchestrationWorkflowRun(WorkflowRunRecord run)
        => run.Handoffs.Any(h => h.StepId == RoomOrchestrationHandoffStep);

    private static string? ReadRoomOrchestrationRoomIdentity(WorkflowRunRecord run, string intentId)
    {
        var bindings = run.Handoffs.Where(h => h.StepId == RoomOrchestrationHandoffStep && h.IntentKey == intentId).ToArray();
        return bindings.Length == 1 && bindings[0].ExtensionData is { } fields
            && fields.TryGetValue(RoomOrchestrationRoomIdentityKey, out var room) && room.ValueKind == JsonValueKind.String
            ? room.GetString() : null;
    }

    private WorkflowRunRecord? FindRoomOrchestrationWorkflowRun(string intentId)
    {
        var all = _runs.ListWithIntegrity();
        if (all.UnknownFiles.Count != 0) throw new IOException("运行台账不完整，不能推断编排意图尚未登记");
        var matches = all.Records.Where(r => r.Handoffs.Any(h => h.StepId == RoomOrchestrationHandoffStep
            && h.IntentKey == intentId)).Take(2).ToArray();
        if (matches.Length > 1) throw new IOException("同一编排意图存在多个运行，原责任待核对");
        return matches.SingleOrDefault();
    }

    private void RequireRoomOrchestrationAuthority(RoomOrchestrationLaunch launch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCenterDriveAuthority.VerifyCurrent();
        if (_shutdown || _manualResetActive != 0 || _onlineSendGate.IsClosed || CapabilityBlockReason() is not null
            || CurrentBgiEpoch() != WorkflowStopAuthority.Epoch(launch.Epoch)
            || FlowRuntime.IsRoomIdentityCurrent?.Invoke(launch.RoomIdentity) != true)
            throw new BgiStateContinuationStoppedException("原停止/房间授权或目标房间执行准入已变化，撤销编排后继");
    }

    // Use the ordinary FlowRegistration admission path rather than manual StartWorkflow or the old batch's
    // RunSpecified continuation path. A pre-created record is NOT itself a receipt of admission.
    internal async Task<CommandResult> StartRoomOrchestrationWorkflowAsync(RoomOrchestrationLaunch launch,
        Func<CancellationToken, Task> verify, CancellationToken ct)
    {
        var invalid = CommandExecutor.ValidateRoomOrchestrationLaunch(launch);
        if (invalid is not null || launch.Policy.SpecifiedTaskType != "workflow")
            return new() { ErrorCode = "invalid_request", Message = invalid ?? "编排计划表目标、修订或原授权不完整" };
        launch = launch with { Policy = launch.Policy.PostSwitchTask() };
        try { await _roomOrchestrationWorkflowGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new() { ErrorCode = "start_cancelled", Message = "编排已撤销，未登记计划表" }; }
        WorkflowRunRecord? run = null;
        RoomOrchestrationRegistration? registration = null;
        try
        {
            using var authorityScope = EnterExecutionAuthorityScope();
            using var configuration = TryBeginConfigurationOperation();
            if (configuration is null || !_admissionWired)
                return new() { ErrorCode = "state_unavailable", Message = "计划表统一准入尚未开放" };
            RequireRoomOrchestrationAuthority(launch, ct);
            await verify(ct).ConfigureAwait(false);
            await EnsureRecoveredAsync().ConfigureAwait(false);
            RequireRoomOrchestrationAuthority(launch, ct);
            if (FindRoomOrchestrationWorkflowRun(launch.IntentId) is { } existing)
            {
                // Never replay/re-register or ResumeRun from a repeated automatic intent.
                return new() { ErrorCode = "result_unknown", JobId = existing.RunId,
                    TargetProcessId = launch.Epoch.ProcessId, TargetStartTicksUtc = launch.Epoch.StartTicksUtc.ToString(),
                    Message = "原编排意图已有运行记录，只按原编号查询" };
            }

            var policy = launch.Policy;
            WorkflowSnapshot snapshot;
            try { snapshot = _workflows.LoadSnapshot(policy.SpecifiedWorkflowId); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
            { return new() { ErrorCode = "specified_task_missing", Message = ex.Message }; }
            if (snapshot.Document.Activation?.Status == "candidate-ready")
                return new() { ErrorCode = "specified_task_missing", Message = "候选计划表尚未激活" };
            var cursor = ResolveCompletionWorkflowCursor(policy, snapshot);
            var authority = new WorkflowStopAuthorityRecord(WorkflowStopAuthority.Epoch(launch.Epoch)!,
                launch.StopVersion, launch.IntentId, launch.IntentTimestamp, Stopwatch.Frequency);
            var runner = CreateRunner(_clientAccessor());
            await runner.ValidateInheritedStopAuthorityAsync(authority, ct).ConfigureAwait(false);
            runner.PreflightStartable(snapshot, cursor.NodeId);

            await verify(ct).ConfigureAwait(false);
            lock (_gate)
            {
                RequireRoomOrchestrationAuthority(launch, ct);
                if (_reservedWorkflows.Contains(policy.SpecifiedWorkflowId) || _drives.ContainsKey(policy.SpecifiedWorkflowId)
                    || _runs.List().Any(r => r.WorkflowId == policy.SpecifiedWorkflowId && ActiveStates.Contains(r.State) && !IsExecutionUnavailable(r))
                    || HasBlockingUnknownRun(policy.SpecifiedWorkflowId))
                    return new() { ErrorCode = "execution_occupied", Message = "目标计划已有活动运行，未登记编排后继" };
                if (ValidateManagedFlowSnapshot(policy.SpecifiedWorkflowId, snapshot.Revision) is { } changed)
                    return new() { ErrorCode = "configuration_revision_mismatch", Message = changed };
                run = _runs.CreateRun(policy.SpecifiedWorkflowId, snapshot.Revision,
                    note: "切房后继准入预备：" + launch.IntentId,
                    handoff: new HandoffIdentity
                    {
                        IntentKey = launch.IntentId, ExecutionId = launch.IntentId, StepId = RoomOrchestrationHandoffStep,
                        Mode = StartupHandoffModes.Start, ExtensionData = new()
                        {
                            [TaskFlowRuntime.RoomDependencyKey] = JsonSerializer.SerializeToElement(true),
                            [RoomOrchestrationRoomIdentityKey] = JsonSerializer.SerializeToElement(launch.RoomIdentity),
                        },
                    },
                    stopAuthority: authority, initialCursor: cursor);
                FlowRuntime.BindRoomDependency(run.RunId, launch.RoomIdentity);
            }
            var prepared = run;
            registration = new(prepared.RunId, launch, verify);
            var key = CommandExecutor.RoomOrchestrationTaskKey(launch.IntentId);
            var result = await _admission!.SubmitAsync(new AdmissionRequest
            {
                Namespace = "system", Kind = AdmissionKind.Create, SourceDetail = "system:room-orchestration:workflow",
                RunBinding = prepared.RunId, WireSubmitKey = key, OperationType = OperationType.FlowRegistration,
                CallerToken = ct, ProcessLocalContext = registration,
                Candidate = new ArbitrationCandidate
                {
                    Scope = "bgi:local:" + authority.Epoch, Namespace = "system", WorkflowId = prepared.WorkflowId,
                    TriggerOccurrenceId = "system:" + key, ResourceRef = "flow:" + prepared.WorkflowId,
                    Intent = "start", Tier = ArbitrationTier.Plan, Priority = policy.SpecifiedTaskPriority,
                },
            }).ConfigureAwait(false);
            result = await RevokeUnacceptedRoomOrchestrationAdmissionAsync(result, key).ConfigureAwait(false);
            if (result.Kind == AdmissionResultKind.Accepted && registration.DriverRegistered)
                return new() { Status = "success", JobId = prepared.RunId,
                    TargetProcessId = launch.Epoch.ProcessId, TargetStartTicksUtc = launch.Epoch.StartTicksUtc.ToString(),
                    ResponsibilityState = ResponsibilityState.Pending, Message = "编排计划表已受理，运行结果另行核对" };

            // Known pre-registration failures are cancelled and never become OnlineHoeingGateParked waiters.
            if (registration.RevokeBeforeLaunch() && CancelUnregisteredRoomOrchestrationRun(prepared.RunId, result.Detail))
            {
                return new() { ErrorCode = string.IsNullOrWhiteSpace(result.ReasonCode) ? "start_cancelled" : result.ReasonCode,
                    Message = "编排计划表未受理，已撤销：" + result.Detail };
            }
            return RoomOrchestrationWorkflowUnknown(launch, prepared, result.Detail);
        }
        catch (Exception ex)
        {
            if (run is not null)
            {
                if ((registration is null || registration.RevokeBeforeLaunch()) && CancelUnregisteredRoomOrchestrationRun(run.RunId, ex.Message))
                    return new() { ErrorCode = "start_cancelled", Message = "编排计划表未受理：" + ex.Message };
                return RoomOrchestrationWorkflowUnknown(launch, run, ex.Message);
            }
            return new() { ErrorCode = ex is CommandExecutor.CompletionStartException e ? e.Code
                : ex is OperationCanceledException or StartupSourceStoppedException ? "start_cancelled" : "state_unavailable", Message = ex.Message };
        }
        finally { _roomOrchestrationWorkflowGate.Release(); }
    }

    private static CommandResult RoomOrchestrationWorkflowUnknown(RoomOrchestrationLaunch launch, WorkflowRunRecord run, string detail)
        => new() { ErrorCode = "result_unknown", JobId = run.RunId, TargetProcessId = launch.Epoch.ProcessId,
            TargetStartTicksUtc = launch.Epoch.StartTicksUtc.ToString(), Message = "编排计划表受理结果待核对，禁止重发：" + detail };

    private bool CancelUnregisteredRoomOrchestrationRun(string runId, string reason)
    {
        try
        {
            lock (_gate)
            {
                if (_drives.Values.Any(entry => entry.RunId == runId)) return false;
                var cancelled = _runs.UpdateMergingIf(runId, latest =>
                {
                    if (!IsRoomOrchestrationWorkflowRun(latest) || latest.CurrentSubmission is not null
                        || latest.NodeOutcomes.Count != 0 || RunStore.HasUnresolvedTerminalResponsibility(latest)
                        || latest.State is not (WorkflowRunState.Planned or WorkflowRunState.Interrupted or WorkflowRunState.Cancelled)) return false;
                    latest.StopRequested = true;
                    latest.State = WorkflowRunState.Cancelled;
                    latest.OnlineHoeingGateParked = false;
                    latest.Note += "；编排未获准，本次已撤销：" + reason;
                    return true;
                }, out _);
                if (cancelled) FlowRuntime.ForgetRoomDependency(runId);
                return cancelled;
            }
        }
        catch (Exception ex) { TryLog("[任务编排] 未受理运行清理尚未确认：" + ex.Message); return false; }
    }

    private async Task<AdmissionResult> RevokeUnacceptedRoomOrchestrationAdmissionAsync(AdmissionResult result, string key)
    {
        if (result.Kind == AdmissionResultKind.Accepted || _admission is null) return result;
        try
        {
            var revoked = await _admission.RevokeRoomOrchestrationUnacceptedAsync(result.RequestIdentity, key).ConfigureAwait(false);
            if (revoked is { Kind: AdmissionResultKind.TerminalRejected }) return revoked;
        }
        catch (Exception ex) { TryLog("[任务编排] 原未获准操作撤销未确认：" + ex.Message); }
        return result;
    }

    // Called by the existing FlowRegistration sender, AFTER mapping/runner preparation, immediately before registering the drive.
    private async Task<SendOutcome?> CheckRoomOrchestrationRegistrationAsync(SubmissionDispatch dispatch,
        WorkflowRunRecord run, WorkflowRunner runner)
    {
        if (!IsRoomOrchestrationWorkflowRun(run)) return null;
        if (dispatch.ProcessLocalContext is not RoomOrchestrationRegistration context || context.RunId != run.RunId)
            return new SendOutcome.Rejected("start_cancelled", false, "host:room-orchestration-no-original-context");
        var launch = context.Launch;
        var ct = dispatch.CallerToken;
        try
        {
            RequireRoomOrchestrationAuthority(launch, ct);
            await runner.ValidateInheritedStopAuthorityAsync(run.StopAuthority, ct).ConfigureAwait(false);
            await context.Verify(ct).ConfigureAwait(false);
            lock (_gate)
            {
                RequireRoomOrchestrationAuthority(launch, ct);
                var latest = _runs.Load(run.RunId);
                if (latest is not { State: WorkflowRunState.Planned, StopRequested: false }
                    || latest.StopAuthority != run.StopAuthority || latest.StopAuthority?.IntentId != launch.IntentId
                    || latest.StopAuthority?.Version != launch.StopVersion || latest.StopAuthority?.Epoch != WorkflowStopAuthority.Epoch(launch.Epoch)
                    || ReadRoomOrchestrationRoomIdentity(latest, launch.IntentId) != launch.RoomIdentity
                    || !FlowRuntime.HasBoundRoomIdentity(run.RunId, launch.RoomIdentity)
                    || ValidateManagedFlowSnapshot(run.WorkflowId, run.WorkflowRevision) is not null || !context.TryBeginLaunch())
                    return new SendOutcome.Rejected("start_cancelled", false, "host:room-orchestration-registration-revoked");
            }
            return null;
        }
        catch (Exception ex) { return new SendOutcome.Rejected("start_cancelled", false, "host:room-orchestration-final-verify", ex.Message); }
    }

    // Pure observation: no recovery scan, new drive, new permission, or resume. Empty runId is used after an ambiguous registration reply.
    internal Task<BgiTaskQueueStatus?> QueryRoomOrchestrationWorkflowAsync(string runId, string intentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var run = FindRoomOrchestrationWorkflowRun(intentId);
            if (run is null || !string.IsNullOrWhiteSpace(runId) && run.RunId != runId)
                return Task.FromResult<BgiTaskQueueStatus?>(null);
            var room = ReadRoomOrchestrationRoomIdentity(run, intentId);
            if (room is null || run.StopAuthority is not { } authority || authority.IntentId != intentId) return Task.FromResult<BgiTaskQueueStatus?>(null);
            var observed = CompletionWorkflowStatus(run, intentId);
            bool liveDrive;
            lock (_gate) liveDrive = _drives.TryGetValue(run.WorkflowId, out var drive) && drive.RunId == run.RunId;
            var stopped = run.StopRequested || IsExecutionUnavailable(run);
            var status = observed.Status;
            if (!observed.ExecutionExitConfirmed && !stopped)
            {
                // A Planned record by itself is not a registration receipt. After assistant restart,
                // absence of a live binding/driver cannot prove that the last running label is still current.
                var registration = TryReadAdmissionSources(includeRetired: true)?.SingleOrDefault(op =>
                    op.RunBinding == run.RunId && op.OperationType == OperationType.FlowRegistration
                    && op.WireSubmitKey == CommandExecutor.RoomOrchestrationTaskKey(intentId));
                if (!FlowRuntime.HasBoundRoomIdentity(run.RunId, room)
                    || status == "queued" && !liveDrive && registration?.RequestState is not (OperationRequestState.Accepted or OperationRequestState.TerminalCompleted)
                    || status == "running" && !liveDrive)
                    status = "result_unknown";
            }
            return Task.FromResult<BgiTaskQueueStatus?>(new()
            {
                TaskHandle = run.RunId, IdempotencyKey = intentId, Epoch = observed.Epoch, WorkflowRunId = run.WireRunId,
                RoomIdentity = room, StopVersion = authority.Version, StopRequested = stopped, Status = status,
                ExecutionExitConfirmed = observed.ExecutionExitConfirmed, Cancelled = observed.Cancelled,
                ErrorCode = observed.ErrorCode, ErrorMessage = observed.ErrorMessage,
            });
        }
        catch { return Task.FromResult<BgiTaskQueueStatus?>(null); }
    }
}
