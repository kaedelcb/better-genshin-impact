using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record HoeingPolicyExecution(string JobId, BgiEpoch Epoch, bool RegisteredJob,
    CommandExecutor.ExecutionIdentity? NativeRoot = null);
internal sealed record LocalTaskFailureReport(int Index, int Attempt, string JobId, string RequestKey,
    BgiEpoch Epoch, string Reason);

/// <summary>
/// [P1b] AllReady 批次句柄：收编原 OnAllReadyConfirmedInternal 的 fire-and-forget Task.Run 批次，
/// 使其"有主可寻"——记录 generation、取消令牌（并入原共享 bool _isAllReadySequenceCancelled 的语义：
/// 外部"用户手动停止"与批次内 F11 取消统一走 CTS）、后台任务引用与存活状态。
/// v2 从准备、执行到最终清理始终占用 busy；普通新请求不会取消或取代旧批次。
/// CTS 不 Dispose：批次任务退出路径仍可能读令牌，Dispose 后访问会抛 ObjectDisposedException，批次量少直接放弃回收。
/// </summary>
public sealed class OnlineHoeingBatch
{
    private long _recoveryRevision;
    internal Mistletoe.Shared.CoordinatedStopSignal? RecoveryStopSignal { get; private set; }
    private string? _savedStopSignalName;
    internal bool RecoveryStopSignalMissing => RecoveryStopSignal is null && _savedStopSignalName is not null;
    internal void ConfirmOriginalProcessStopFence(BgiEpoch epoch, long stopVersion)
    {
        lock (_policyGate)
        {
            if (!ViewModels.CoordinatedBatchLoop.SameEpoch(epoch, ExecutionEpoch)
                || stopVersion != _stateContinuationStopVersion || ExplicitStopRequested)
                throw new BgiStateContinuationStoppedException("原进程停止围栏不符，不能重建停止事实");
            _savedStopSignalName = null;
        }
    }
    internal void EnsureRecoveryStopSignal()
    {
        lock (_policyGate)
        {
            if (RecoveryStopSignalMissing)
                throw new System.IO.IOException("原停止事实已丢失，未取得同一原进程停止围栏前不能创建新授权");
            RecoveryStopSignal ??= Mistletoe.Shared.CoordinatedStopSignal.Create();
        }
        PersistRecovery?.Invoke();
    }
    internal void ObserveRecoveryStopSignal()
    {
        if (RecoveryStopSignal?.IsStopped == true) Cancel();
    }
    internal BgiEpoch? ProcessRecoveryPreviousEpoch { get; private set; }
    internal bool CompletionRecoveryReservationAcquired { get; set; }
    internal void RebindExitedProcess(BgiEpoch previous, BgiEpoch replacement)
    {
        lock (_policyGate)
        {
            if (!PriorityRecoveryEnabled || ExplicitStopRequested || IsCancellationRequested
                || RecoveryStopSignal is null || RecoveryStopSignal.IsStopped
                || !ViewModels.CoordinatedBatchLoop.SameEpoch(ExecutionEpoch, previous))
                throw new InvalidOperationException("原批次停止事实不明或已撤销，不能向新进程恢复");
            ProcessRecoveryPreviousEpoch = previous;
            CompletionRecoveryReservationAcquired = false;
            ExecutionEpoch = replacement;
            _stateContinuationStopVersion = 0; // New process must still prove no manual stop before it binds this signal.
            TakeoverTicket = null;
            Prepared = false;
            // TakenOver still describes the old exited owner until the current recovery completes.
        }
    }
    internal bool BeginCompletionProcessRecovery(BgiEpoch previous, BgiEpoch replacement,
        string? previousRequestKey, bool nativeResume, Func<BgiEpoch, bool?> originalProcessAlive, bool managedResume = false)
    {
        lock (_policyGate)
        {
            if (!PriorityRecoveryEnabled || !LocalCompletionReady || ExplicitStopRequested || IsCancellationRequested
                || RecoveryStopSignal is null || RecoveryStopSignal.IsStopped
                || !ViewModels.CoordinatedBatchLoop.SameEpoch(ExecutionEpoch, previous)
                || ViewModels.CoordinatedBatchLoop.SameEpoch(previous, replacement)
                || originalProcessAlive(previous) != false
                || _policyExecution is { } held && !ViewModels.CoordinatedBatchLoop.SameEpoch(held.Epoch, previous)
                || _policyTerminalResult?.Epoch is { } resultEpoch && !ViewModels.CoordinatedBatchLoop.SameEpoch(resultEpoch, previous)
                || !(Policy?.Policy == TaskConflictPolicy.RunSpecified
                    || managedResume && ManagedRunId is not null && Policy?.Policy == TaskConflictPolicy.Resume
                    || nativeResume && Policy?.Policy == TaskConflictPolicy.Resume
                        && CommandExecutor.TryReadNativeResumeSnapshot(_suspendedRawData, out _))
                || _policyTerminalResult is { Status: "completed", Cancelled: false }) return false;
            if (_policyStartAttempted || _policyExecution is not null || _policyTerminalResult is not null)
                _failedPolicyAttempts.Add(_policyTerminalResult ?? new BgiTaskQueueStatus
                {
                    TaskHandle = _policyExecution?.JobId, IdempotencyKey = previousRequestKey, Epoch = previous,
                    Status = "failed", ExecutionExitConfirmed = true, ErrorCode = "executor_exited_result_unknown",
                    ErrorMessage = "原执行进程已确认退出，未取得完成结果；保留原尝试并重跑已知任务。",
                });
            _policyAttempt++;
            _policyExecution = null;
            _policyTerminalResult = null;
            _policyRequestIdentity = null;
            _policyStartAttempted = false;
            _policyResultAcknowledged = false;
            _policyCancellation = null;
            PolicySendUnknown = false;
            PolicyTicketReleased = true; // The exited process cannot retain a live reservation.
            NativeResumeRetryPending = nativeResume;
            RebindExitedProcess(previous, replacement);
            return true;
        }
    }

    internal bool RejectUnrecoverableManagedPlanAfterExit(string reason)
    {
        lock (_policyGate)
        {
            if (!PriorityRecoveryEnabled || ManagedRunId is null || Policy?.Policy != TaskConflictPolicy.Resume
                || ExplicitStopRequested || IsCancellationRequested || RecoveryStopSignal is not { IsStopped: false }
                || (ProcessRecoveryPreviousEpoch ?? ExecutionEpoch) is not { } exited
                || ProcessRecoveryPreviousEpoch is not null && (!CompletionRecoveryReservationAcquired || !PolicyTicketReleased)
                || CommandExecutor.ProbeOriginalBgiProcess(exited) != false)
                return false;
            if (_policyTerminalResult is not { Status: "completed", Cancelled: false })
            {
                if (_policyStartAttempted || _policyExecution is not null || _policyTerminalResult is not null)
                    _failedPolicyAttempts.Add(_policyTerminalResult ?? new BgiTaskQueueStatus
                    {
                        TaskHandle = _policyExecution?.JobId, Epoch = exited, Status = "failed", ExecutionExitConfirmed = true,
                        ErrorCode = "original_plan_lost_result_unknown", ErrorMessage = reason,
                    });
                _policyExecution = null;
                _policyTerminalResult = null;
            }
            PolicyRejectedBeforeStart = true;
            PolicyTicketReleased = true;
            _policyResultAcknowledged = true;
            PolicySendUnknown = false;
            CompletionActionWarning = "原计划无法接续：" + reason;
            return true;
        }
    }

    internal void CompleteProcessRecovery()
    {
        lock (_policyGate)
        {
            Prepared = true;
            TakenOver = true;
            ProcessRecoveryPreviousEpoch = null;
            CompletionRecoveryReservationAcquired = false;
        }
    }
    internal CoordinatedRecoveryProgress? RecoveryProgress { get; set; }
    internal bool RestoredFromRecovery { get; private set; }
    internal int RecoveryOwnerPid { get; private set; }
    internal long RecoveryOwnerStartTicks { get; private set; }
    internal PriorityPlanRecoverySnapshot? ManagedPlanSnapshot { get; set; }
    internal Action? PersistRecovery { get; set; }

    internal CoordinatedRecoverySnapshot CaptureRecovery()
    {
        lock (_policyGate) return new()
        {
            Revision = ++_recoveryRevision,
            Start = Start!, SelfUid = SelfUid, Token = Token, Policy = Policy!, Progress = RecoveryProgress,
            Epoch = ExecutionEpoch, StopVersion = _stateContinuationStopVersion, Ticket = TakeoverTicket,
            RecoveryStopSignalName = RecoveryStopSignal?.Name ?? _savedStopSignalName, ProcessRecoveryPreviousEpoch = ProcessRecoveryPreviousEpoch,
            SuspendReceipt = _suspendedRawData, ConfigRevisions = new(_configRevisions),
            Prepared = Prepared, TakenOver = TakenOver, Stopped = ExplicitStopRequested,
            Settled = Volatile.Read(ref _settled) != 0, LocalSucceeded = LocalCompletionReady,
            LocalTasksFailed = LocalTasksFailed, CoordinatedSucceeded = CoordinatedSucceeded,
            LocalReleased = LocalExecutionReleased, PolicyStartAttempted = _policyStartAttempted,
            NativeResumeRetryPending = NativeResumeRetryPending, ManagedRunId = ManagedRunId,
            ManagedPlanSnapshot = ManagedPlanSnapshot,
            CompletionRecoveryReservationAcquired = CompletionRecoveryReservationAcquired,
            PolicyAttempt = _policyAttempt, FailedPolicyAttempts = _failedPolicyAttempts.ToArray(),
            PolicyRequestIdentity = _policyRequestIdentity,
            PolicyPayload = _policySubmitPayload is null ? null : new(_policySubmitPayload),
            PlanContinuation = _planResumeContinuation is null ? null : new(_planResumeContinuation),
            PolicyExecution = _policyExecution, PolicyTerminal = _policyTerminalResult,
            PolicyAcknowledged = _policyResultAcknowledged, PolicyTicketReleased = PolicyTicketReleased,
            PolicyRejected = PolicyRejectedBeforeStart
        };
    }

    internal static OnlineHoeingBatch Restore(CoordinatedRecoverySnapshot saved)
    {
        var batch = new OnlineHoeingBatch(saved.Start, saved.SelfUid, saved.Policy)
        {
            _recoveryRevision = saved.Revision, _policyAttempt = saved.PolicyAttempt,
            NativeResumeRetryPending = saved.NativeResumeRetryPending, ManagedRunId = saved.ManagedRunId,
            ManagedPlanSnapshot = saved.ManagedPlanSnapshot,
            RecoveryOwnerPid = saved.OwnerPid, RecoveryOwnerStartTicks = saved.OwnerStartTicks,
            CompletionRecoveryReservationAcquired = saved.CompletionRecoveryReservationAcquired,
            Token = saved.Token, RestoredFromRecovery = true, RecoveryProgress = saved.Progress,
            ExecutionEpoch = saved.Epoch, ProcessRecoveryPreviousEpoch = saved.ProcessRecoveryPreviousEpoch,
            StateContinuationEnabled = true,
            StateContinuationStopVersion = saved.StopVersion, TakeoverTicket = saved.Ticket,
            SuspendedRawData = saved.SuspendReceipt, Prepared = saved.Prepared, TakenOver = saved.TakenOver,
            CoordinatedSucceeded = saved.CoordinatedSucceeded,
            PolicyStartAttempted = saved.PolicyStartAttempted, PolicyRequestIdentity = saved.PolicyRequestIdentity,
            PolicySubmitPayload = CoordinatedRecoveryStore.Materialize(saved.PolicyPayload),
            PlanResumeContinuation = CoordinatedRecoveryStore.Materialize(saved.PlanContinuation),
            PolicyTicketReleased = saved.PolicyTicketReleased, PolicyRejectedBeforeStart = saved.PolicyRejected,
            PolicyResultAcknowledged = saved.PolicyAcknowledged
        };
        batch._savedStopSignalName = saved.RecoveryStopSignalName;
        batch.RecoveryStopSignal = Mistletoe.Shared.CoordinatedStopSignal.Open(saved.RecoveryStopSignalName);
        batch._failedPolicyAttempts.AddRange(saved.FailedPolicyAttempts);
        if (saved.LocalTasksFailed) Volatile.Write(ref batch._localTasksFailed, 1);
        foreach (var revision in saved.ConfigRevisions) batch._configRevisions.Add(revision.Key, revision.Value);
        if (saved.LocalSucceeded && saved.Epoch is { } epoch)
        {
            if (saved.LocalTasksFailed) batch.MarkLocalFinishedWithTaskErrors(epoch);
            else batch.MarkLocalSuccess(epoch);
        }
        if (saved.PolicyExecution is { } execution) batch.RecordPolicyExecution(execution);
        if (saved.PolicyTerminal is { } terminal) batch.PolicyTerminalResult = terminal;
        if (saved.LocalReleased) batch.MarkLocalExecutionReleased();
        if (saved.Stopped || batch.RecoveryStopSignal?.IsStopped == true) batch.Cancel();
        return batch;
    }

    private int _settled;
    private int _stateContinuationEnabled;
    private int _explicitStopRequested;
    internal bool StateContinuationEnabled
    {
        get => Volatile.Read(ref _stateContinuationEnabled) != 0;
        set => Volatile.Write(ref _stateContinuationEnabled, value ? 1 : 0);
    }
    internal static bool IsExplicitStopReason(string reason) => reason is "manual_reset" or "user_stop_all";
    internal bool ExplicitStopRequested => Volatile.Read(ref _explicitStopRequested) != 0;
    private long? _stateContinuationStopVersion;
    internal long? StateContinuationStopVersion
    {
        get { lock (_policyGate) return _stateContinuationStopVersion; }
        set
        {
            lock (_policyGate)
            {
                if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value));
                if (_stateContinuationStopVersion is { } frozen && value != frozen)
                    throw new InvalidOperationException("本批停止围栏已经冻结，不能替换或清除");
                _stateContinuationStopVersion = value;
            }
        }
    }
    private string? _suspendedRawData;
    internal string? SuspendedRawData
    {
        get { lock (_policyGate) return _suspendedRawData; }
        set { lock (_policyGate) _suspendedRawData = value; }
    }
    private int _policyAttempt;
    private readonly List<BgiTaskQueueStatus> _failedPolicyAttempts = [];
    internal IReadOnlyList<BgiTaskQueueStatus> FailedPolicyAttempts
    { get { lock (_policyGate) return _failedPolicyAttempts.ToArray(); } }
    internal string PolicyRequestKey => "post-hoeing/" + BatchId + "/" + SelfUid
        + (_policyAttempt == 0 ? "" : "/attempt/" + _policyAttempt);

    internal bool NativeResumeRetryPending { get; set; }
    internal string? ManagedRunId { get; set; }
    internal bool RetryFailedCompletion(bool allowNativeResume = false)
    {
        lock (_policyGate)
        {
            if (!PriorityRecoveryEnabled || IsCancellationRequested || ExplicitStopRequested
                || !(Policy?.Policy == TaskConflictPolicy.RunSpecified
                    || allowNativeResume && Policy?.Policy == TaskConflictPolicy.Resume)
                || _policyTerminalResult is not { ExecutionExitConfirmed: true, Status: "failed", Cancelled: false } failed
                || _policyExecution is not { } execution
                || !Guid.TryParse(failed.TaskHandle, out var failedJob) || !Guid.TryParse(execution.JobId, out var runningJob)
                || failedJob != runningJob || !ViewModels.CoordinatedBatchLoop.SameEpoch(failed.Epoch, execution.Epoch))
                return false;
            _failedPolicyAttempts.Add(failed);
            if (Policy?.Policy == TaskConflictPolicy.Resume)
            {
                NativeResumeRetryPending = true;
                TakeoverTicket = null; // The prior resume released it; a new attempt obtains its own idle reservation.
                PolicyTicketReleased = true;
            }
            _policyAttempt++;
            _policyExecution = null;
            _policyTerminalResult = null;
            _policyRequestIdentity = null;
            _policyStartAttempted = false;
            _policyResultAcknowledged = false;
            _policyCancellation = null;
            PolicySendUnknown = false;
            return true;
        }
    }
    internal bool AdoptManagedCompletionRetry(string runId, string requestKey, HoeingPolicyExecution execution)
    {
        lock (_policyGate)
        {
            if (ManagedRunId != runId || Policy?.Policy != TaskConflictPolicy.Resume || !PriorityRecoveryEnabled
                || IsCancellationRequested || ExplicitStopRequested || string.IsNullOrWhiteSpace(requestKey)
                || _policyTerminalResult is not { Status: "failed", ExecutionExitConfirmed: true } previous
                || _policyExecution is not { } old || old.JobId == execution.JobId) return false;
            _failedPolicyAttempts.Add(previous);
            _policyAttempt++;
            _policyExecution = execution;
            _policyRequestIdentity = requestKey;
            _policyTerminalResult = null;
            _policyStartAttempted = true; // The original Runner already accepted this attempt; never send it here.
            _policyResultAcknowledged = false;
            PolicySendUnknown = false;
            return true;
        }
    }

    private string? _policyRequestIdentity;
    internal string? PolicyRequestIdentity
    {
        get { lock (_policyGate) return _policyRequestIdentity; }
        set
        {
            lock (_policyGate)
            {
                if (!string.IsNullOrEmpty(_policyRequestIdentity) && _policyRequestIdentity != value)
                    throw new InvalidOperationException("完成动作请求身份已经冻结，不能替换");
                _policyRequestIdentity = value;
            }
        }
    }
    private IReadOnlyDictionary<string, object?>? _policySubmitPayload;
    internal IReadOnlyDictionary<string, object?>? PolicySubmitPayload
    {
        get { lock (_policyGate) return _policySubmitPayload; }
        set { lock (_policyGate) _policySubmitPayload ??= value is null ? null : new Dictionary<string, object?>(value); }
    }
    private bool _policyStartAttempted;
    internal bool PolicyStartAttempted
    {
        get { lock (_policyGate) return _policyStartAttempted; }
        set { lock (_policyGate) _policyStartAttempted |= value; }
    }
    private BgiTaskQueueStatus? _policyTerminalResult;
    internal BgiTaskQueueStatus? PolicyTerminalResult
    {
        get { lock (_policyGate) return _policyTerminalResult; }
        set
        {
            lock (_policyGate)
            {
                if (value is null) return;
                if (value.Status is not ("completed" or "failed" or "queueCancelled") || !value.ExecutionExitConfirmed)
                    throw new InvalidOperationException("完成动作尚未确认真实退出，不能保存为终态");
                if (_policyTerminalResult is { } previous && (previous.TaskHandle != value.TaskHandle
                    && !(Guid.TryParse(previous.TaskHandle, out var a) && Guid.TryParse(value.TaskHandle, out var b) && a == b)))
                    throw new InvalidOperationException("完成动作终态对应不同任务，保留原结果");
                _policyTerminalResult ??= value;
            }
        }
    }
    private bool _policyResultAcknowledged;
    internal bool PolicyResultAcknowledged
    {
        get { lock (_policyGate) return _policyResultAcknowledged; }
        set { lock (_policyGate) _policyResultAcknowledged |= value; }
    }
    private IReadOnlyDictionary<string, object?>? _planResumeContinuation;
    internal IReadOnlyDictionary<string, object?>? PlanResumeContinuation
    {
        get { lock (_policyGate) return _planResumeContinuation; }
        set { lock (_policyGate) _planResumeContinuation ??= value is null ? null : new Dictionary<string, object?>(value); }
    }
    internal string ControlContext { get; private set; } = "";
    internal CoordinatedControlAuthority.Grant? ControlGrant { get; set; }
    private TimeProvider _controlClock = TimeProvider.System;
    private long _lastControlContact;
    private bool _hasControlContact;
    private int _controlExecutionEnded;
    private int _controlItemSubmitted;
    internal void SetControlItemSubmitted(bool submitted) =>
        Volatile.Write(ref _controlItemSubmitted, submitted ? 1 : 0);
    internal bool ControlExecutionEnded => Volatile.Read(ref _controlExecutionEnded) != 0;
    internal void EndControlExecution() => Interlocked.Exchange(ref _controlExecutionEnded, 1);
    private string _lastControlPhase = "";
    private TimeSpan _runningContactTimeout = TimeSpan.FromSeconds(90);

    internal void FreezeControlContext(string context)
    {
        if (ControlContext.Length != 0 && ControlContext != context)
            throw new InvalidOperationException("批次控制房间身份不能替换");
        ControlContext = context;
    }

    internal void RecordControlContact(CoordinatedBatchSnapshot state, TimeProvider clock, TimeSpan runningTimeout)
    {
        lock (_policyGate)
        {
            _controlClock = clock;
            _lastControlContact = clock.GetTimestamp();
            _hasControlContact = true;
            _lastControlPhase = state.Phase;
            _runningContactTimeout = runningTimeout;
        }
    }

    internal TimeSpan? RemainingRunningControlBudget
    {
        get
        {
            lock (_policyGate)
                return !StateContinuationEnabled && _hasControlContact && TakenOver && _lastControlPhase == "running"
                    && Volatile.Read(ref _controlItemSubmitted) != 0
                    && !ControlExecutionEnded && !IsCancellationRequested && !LocalExecutionReleased && !LocalCleanupConfirmed
                    && Volatile.Read(ref _settled) == 0
                    ? _runningContactTimeout - _controlClock.GetElapsedTime(_lastControlContact) : null;
        }
    }

    internal void RequireRunningControlBudget()
    {
        if (RemainingRunningControlBudget is { } remaining && remaining <= TimeSpan.Zero)
            throw new TimeoutException("原批次控制连接核对已超过运行阶段总预算");
    }

    private int _localSucceeded;
    private int _localExecutionReleased;
    private int _localTasksFailed;
    private int _currentBindingSucceeded;
    internal bool LocalTasksFailed => Volatile.Read(ref _localTasksFailed) != 0;
    internal bool CurrentBindingSucceeded => Volatile.Read(ref _currentBindingSucceeded) != 0;
    internal bool PendingBindingsSkipped { get; set; }
    internal bool LocalTasksFailureAccepted { get; private set; }
    private LocalTaskFailureReport? _localTaskFailureReport;
    internal LocalTaskFailureReport? LocalTaskFailureReport { get { lock (_policyGate) return _localTaskFailureReport; } }

    internal bool ObserveLocalTasksFailureAccepted(CoordinatedBatchSnapshot state)
    {
        lock (_policyGate)
        {
            if (_localTaskFailureReport is not { } report || state.BatchId != BatchId || state.Generation != Generation
                || state.Index != report.Index || state.Attempt != report.Attempt
                || !state.Members.Any(member => member.Uid == SelfUid && member.Result == "local_tasks_failed")) return false;
            LocalTasksFailureAccepted = true;
            return true;
        }
    }

    internal LocalTaskFailureReport? PendingLocalTaskFailureReport(CoordinatedBatchSnapshot state)
    {
        lock (_policyGate) return !IsCancellationRequested && !LocalTasksFailureAccepted
            && state.BatchId == BatchId && state.Generation == Generation && (state.Phase is "running" or "stopping")
            && _localTaskFailureReport is { } report && state.Index == report.Index && state.Attempt == report.Attempt
                ? report : null;
    }

    internal void MarkCurrentBindingStarted()
    {
        lock (_policyGate)
        {
            Volatile.Write(ref _currentBindingSucceeded, 0);
            if (StateContinuationEnabled) { _localTaskFailureReport = null; LocalTasksFailureAccepted = false; }
        }
    }
    internal void MarkCurrentBindingSucceeded() => Volatile.Write(ref _currentBindingSucceeded, 1);
    internal void RecordLocalTasksFailure(BgiEpoch epoch, string reason, int index, int attempt, string jobId, string requestKey)
    {
        lock (_policyGate)
        {
            if (IsCancellationRequested) return;
            var report = new LocalTaskFailureReport(index, attempt, jobId, requestKey, epoch, reason);
            if (_localTaskFailureReport is { } previous && (previous.Index != index || previous.Attempt != attempt
                || previous.JobId != jobId || previous.RequestKey != requestKey
                || previous.Epoch.ProcessId != epoch.ProcessId || previous.Epoch.StartTicksUtc != epoch.StartTicksUtc))
                throw new InvalidOperationException("普通任务失败回执身份冲突，禁止替换原上报对象");
            _localTaskFailureReport ??= report;
            ExecutionEpoch ??= epoch;
            Volatile.Write(ref _localTasksFailed, 1);
            Outcome = "failed";
            FailureReason ??= reason;
        }
    }
    private Task? _localFinalization;
    private int _localFinishedWithErrors;
    internal bool PriorityRecoveryEnabled => Start?.PriorityRecovery == true;

    // A member's local success is provisional until the joint item has completed for everyone.
    internal void RetryUnfinishedJointItem()
    {
        lock (_policyGate)
        {
            if (!PriorityRecoveryEnabled || _localFinalization is not null || LocalExecutionReleased
                || PolicyExecution is not null || IsCancellationRequested)
                throw new InvalidOperationException("本端后续已经起步或批次已停止，不能重跑共同项");
            Volatile.Write(ref _localSucceeded, 0);
            Volatile.Write(ref _localFinishedWithErrors, 0);
            LocalSuccessEpoch = null;
        }
    }

    internal bool LocalSucceeded => Volatile.Read(ref _localSucceeded) != 0;
    internal bool LocalCompletionReady => LocalSucceeded || Volatile.Read(ref _localFinishedWithErrors) != 0;
    internal string CompletionOutcome => LocalTasksFailed ? "completed_with_errors" : "succeeded";

    internal void MarkLocalFinishedWithTaskErrors(BgiEpoch epoch)
    {
        lock (_policyGate)
        {
            if (IsCancellationRequested) return;
            LocalSuccessEpoch ??= epoch;
            Volatile.Write(ref _localFinishedWithErrors, 1);
            Outcome = "completed_with_errors";
            CompletionActionWarning ??= "锄地已完成，普通任务有错误，仍按设定执行后续：" + FailureReason;
        }
    }
    internal bool LocalExecutionReleased => Volatile.Read(ref _localExecutionReleased) != 0;
    internal BgiEpoch? ExecutionEpoch { get; set; }
    internal BgiEpoch? LocalSuccessEpoch { get; private set; }
    internal string? TeamFailureReason { get; set; }
    internal Task? LocalFinalizationTask { get { lock (_policyGate) return _localFinalization; } }

    // A final binding has completed as a whole. Native-body progress alone cannot enter this state.
    internal void MarkLocalSuccess(BgiEpoch epoch)
    {
        lock (_policyGate)
        {
            if (IsCancellationRequested) return;
            LocalSuccessEpoch ??= epoch;
            Volatile.Write(ref _localSucceeded, 1);
            Outcome = "succeeded";
        }
    }

    internal Task BeginLocalFinalization(Func<Task> finalize)
    {
        lock (_policyGate)
            return _localFinalization ??= Task.Run(finalize, CancellationToken.None);
    }

    internal void MarkLocalExecutionReleased()
    {
        lock (_policyGate)
        {
            if (!IsCancellationRequested) Volatile.Write(ref _localExecutionReleased, 1);
        }
    }

    // A team reason cannot revoke another endpoint's completed result. An explicit local stop cancels Cts first.
    internal bool PreserveLocalSuccess(string reason) => LocalCompletionReady && !IsCancellationRequested;
    private readonly Dictionary<string, string> _configRevisions = new(StringComparer.Ordinal);
    private readonly object _policyGate = new();
    private HoeingPolicyExecution? _policyExecution;
    private Func<HoeingPolicyExecution, Task<bool>>? _cancelPolicy;
    private Task<bool>? _policyCancellation;
    private CancellationTokenRegistration _policyCancellationRegistration;
    private readonly TaskCompletionSource _policyPhaseFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? _policyCleanup;
    private Func<Task<bool>>? _cleanupPolicy;
    internal bool HasPolicyCleanup { get; private set; }
    internal bool CancelledPolicyCleanupConfirmed => _policyCleanup is { IsCompletedSuccessfully: true } task && task.Result;
    internal bool PolicySendUnknown { get; set; }
    internal bool PolicyTicketReleased { get; set; }
    internal bool PolicyRejectedBeforeStart { get; set; }
    internal HoeingPolicyExecution? PolicyExecution { get { lock (_policyGate) return _policyExecution; } }

    internal void ConfigurePolicyCancellation(Func<HoeingPolicyExecution, Task<bool>> cancel)
    {
        lock (_policyGate) _cancelPolicy = cancel;
        _policyCancellationRegistration = Cts.Token.Register(RequestPolicyCancellation);
    }

    internal void RecordPolicyExecution(HoeingPolicyExecution execution)
    {
        lock (_policyGate)
        {
            if (_policyExecution is { } previous && (previous.JobId != execution.JobId
                || previous.Epoch.ProcessId != execution.Epoch.ProcessId
                || previous.Epoch.StartTicksUtc != execution.Epoch.StartTicksUtc))
                throw new InvalidOperationException("同批收尾出现不同后继身份，保留责任待核对");
            _policyExecution = execution;
        }
        if (IsCancellationRequested) RequestPolicyCancellation();
    }

    private void RequestPolicyCancellation()
    {
        lock (_policyGate)
        {
            if (StateContinuationEnabled && !ExplicitStopRequested) return;
            if (_policyExecution is null || _cancelPolicy is null) return;
            if (_policyCancellation is not null && (!_policyCancellation.IsCompleted
                || _policyCancellation.IsCompletedSuccessfully && _policyCancellation.Result)) return;
            var execution = _policyExecution;
            var cancel = _cancelPolicy;
            _policyCancellation = Task.Run(async () =>
            {
                try { return await cancel(execution).ConfigureAwait(false); }
                catch { return false; }
            });
        }
    }

    internal Task<bool> WaitPolicyCancellationAsync()
    {
        if (IsCancellationRequested) RequestPolicyCancellation();
        lock (_policyGate) return _policyCancellation ?? Task.FromResult(!PolicySendUnknown);
    }

    internal void ConfigurePolicyCleanup(Func<Task<bool>> cleanup)
    {
        lock (_policyGate) { HasPolicyCleanup = true; _cleanupPolicy = cleanup; }
        Cts.Token.Register(RequestPolicyCleanup);
    }
    private void RequestPolicyCleanup()
    {
        lock (_policyGate)
        {
            if (!IsCancellationRequested || _cleanupPolicy is null) return;
            if (_policyCleanup is not null && (!_policyCleanup.IsCompleted
                || _policyCleanup.IsCompletedSuccessfully && _policyCleanup.Result)) return;
            var cleanup = _cleanupPolicy;
            _policyCleanup = Task.Run(async () =>
            {
                await _policyPhaseFinished.Task.ConfigureAwait(false);
                try { return await cleanup().ConfigureAwait(false); }
                catch { return false; }
            });
        }
    }
    internal void MarkPolicyPhaseFinished() => _policyPhaseFinished.TrySetResult();
    internal async Task<bool> WaitCancelledPolicyCleanupAsync()
    {
        RequestPolicyCleanup();
        Task<bool>? cleanup;
        lock (_policyGate) cleanup = _policyCleanup;
        return cleanup is not null && await cleanup.ConfigureAwait(false);
    }
    public OnlineHoeingBatch(int generation)
    {
        Generation = generation;
    }

    public OnlineHoeingBatch(CoordinatedBatchSnapshot start, string selfUid, TaskConflictPolicySettings policy)
        : this(start.Generation)
    {
        if (start.Protocol != "hoeing.start.v2" || string.IsNullOrWhiteSpace(start.BatchId)
            || !start.ParticipantUids.Contains(selfUid, StringComparer.Ordinal)
            || !start.Bindings.TryGetValue(selfUid, out var bindings))
            throw new InvalidOperationException("批次身份、成员或绑定快照无效");
        Start = start with
        {
            ParticipantUids = start.ParticipantUids.ToArray(),
            Bindings = start.Bindings.ToDictionary(p => p.Key,
                p => (IReadOnlyList<CoordinatedHoeingBinding>)p.Value.ToArray(), StringComparer.Ordinal),
            IntentGenerations = start.IntentGenerations.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Members = start.Members.ToArray(),
        };
        SelfUid = selfUid;
        Bindings = bindings.ToArray();
        Policy = new TaskConflictPolicySettings
        {
            Policy = policy.Policy, SpecifiedTaskType = policy.SpecifiedTaskType,
            SpecifiedTaskName = policy.SpecifiedTaskName, SpecifiedTaskPriority = policy.SpecifiedTaskPriority,
        };
    }

    public CoordinatedBatchSnapshot? Start { get; }
    public string SelfUid { get; } = "";
    public string BatchId => Start?.BatchId ?? "legacy:" + Generation;
    public string Token { get; private set; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<CoordinatedHoeingBinding> Bindings { get; } = [];
    public TaskConflictPolicySettings? Policy { get; }
    public IReadOnlyDictionary<string, string> ConfigRevisions => _configRevisions;
    public static string ConfigurationKey(string name, bool oneDragon) => (oneDragon ? "onedragon:" : "group:") + name;
    public void FreezeConfigurationRevision(string name, bool oneDragon, string revision)
    {
        if (string.IsNullOrWhiteSpace(revision)) throw new InvalidOperationException("配置修订缺失");
        var key = ConfigurationKey(name, oneDragon);
        if (_configRevisions.TryGetValue(key, out var frozen) && frozen != revision)
            throw new InvalidOperationException("本批配置修订已冻结，不能在执行中替换");
        _configRevisions[key] = revision;
    }
    public string Phase { get; set; } = "preparing";
    public string Message { get; set; } = "正在准备联机锄地";
    public string Outcome { get; set; } = "unknown";
    public string? FailureReason { get; set; }
    internal string? CompletionActionWarning { get; set; }
    private string? _completionPendingMessage;
    internal string? CompletionPendingMessage => Volatile.Read(ref _completionPendingMessage);
    internal bool RecordCompletionPendingMessage(string message) =>
        Interlocked.Exchange(ref _completionPendingMessage, message) != message;
    public string? TakeoverTicket { get; set; }
    public bool CleanupConfirmed { get; set; }
    public bool LocalCleanupConfirmed { get; set; }
    public bool Prepared { get; set; }
    public bool TakenOver { get; set; }
    public void MarkSettled()
    {
        lock (_policyGate) Interlocked.Exchange(ref _settled, 1);
        RecoveryStopSignal?.Stop();
        PersistRecovery?.Invoke();
        _policyCancellationRegistration.Dispose();
        RecoveryStopSignal?.Dispose();
        RecoveryStopSignal = null;
    }

    /// <summary>本轮 AllReady 的代序号（服务端广播值，兼做策略收尾恰好一次守卫的批次键）。</summary>
    public int Generation { get; }
    public bool CoordinatedSucceeded { get; set; }

    /// <summary>批次取消令牌源（原 _isAllReadySequenceCancelled 的承载者）。</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>批次后台任务（Task.Run 发起后由 MainViewModel 同步回填，回填前视为未启动）。</summary>
    public Task? RunTask { get; set; }

    /// <summary>批次是否仍存活（后台任务已发起且未完结）。</summary>
    public bool IsAlive => Start is not null ? Volatile.Read(ref _settled) == 0 : RunTask is { IsCompleted: false };

    /// <summary>批次是否已被请求取消。</summary>
    public bool IsCancellationRequested => Cts.IsCancellationRequested;

    /// <summary>请求取消批次（幂等；批次循环在下一次迭代检查点退出）。</summary>
    public void Cancel(bool userInitiated = true)
    {
        lock (_policyGate)
        {
        if (Volatile.Read(ref _settled) != 0 || !userInitiated && (StateContinuationEnabled || LocalExecutionReleased)) return;
        if (userInitiated)
        {
            Volatile.Write(ref _explicitStopRequested, 1);
            RecoveryStopSignal?.Stop();
            Outcome = "cancelled";
        }
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 防御：理论上不 Dispose 不会命中，保留兜底
        }
        }
        if (userInitiated) PersistRecovery?.Invoke();
    }
}
