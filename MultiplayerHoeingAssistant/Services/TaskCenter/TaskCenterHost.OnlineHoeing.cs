using System.Text.Json;
using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record OnlineHoeingPreparation(bool Ready, string? ManagedRunId, string? Reason);
internal sealed record OnlineHandoffRecoveryResult(int SettledRuns, int RemainingHandoffs,
    int PendingAdmissionReconciliations);

public sealed partial class TaskCenterHost
{
    private string? _onlineHoeingRunId;
    private int _onlineHoeingGeneration;
    private readonly Func<BgiEpoch?>? _onlineEpochProvider;
    private readonly Func<int, long, bool?>? _onlineHandoffProcessAlive;
    private sealed record OnlineResumeSignal(string? JobId, string? FailureReason);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OnlineResumeSignal>> _onlineResumeSignals = new();
    private int _manualResetActive;
    private int _priorityWaiterWakePending;
    private Task? _priorityWaiterWakeTask;
    private readonly SemaphoreSlim _onlineWaiterWakeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _stateContinuationRuns = new();

    private bool OriginalOnlineHandoffProcessExited(OnlineHoeingHandoffRecord handoff, string? currentEpoch)
    {
        if (currentEpoch is not null && string.Equals(handoff.Epoch, currentEpoch, StringComparison.Ordinal))
            return false;
        var parts = handoff.Epoch?.Split(':');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || pid <= 0 || ticks <= 0) return false;
        return (_onlineHandoffProcessAlive is null
            ? ProbeOriginalBgiProcess(pid, ticks)
            : _onlineHandoffProcessAlive(pid, ticks)) == false;
    }

    /// <summary>
    /// 联机任务启动前或状态重置后，按原进程身份恢复旧记录。只有原作业已确认取消和退出、
    /// 没有恢复作业及其他未决责任时才自动终止旧计划；未知事实仍留 Unknown，不重发。
    /// </summary>
    internal async Task<OnlineHandoffRecoveryResult> RecoverOnlineHandoffsBeforeStartAsync()
    {
        var currentEpoch = WorkflowStopAuthority.Epoch(
            _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch);
        IReadOnlyList<WorkflowRunRecord> before = _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList();
        var pendingTerminal = (_admissionWired ? before : Array.Empty<WorkflowRunRecord>()).Where(r => r.State == WorkflowRunState.Cancelled
            && r.StopRequested && r.TerminalRelease is null
            && r.OnlineHoeingHistory.Any(h => h.Phase == "stopped"
                && OriginalOnlineHandoffProcessExited(h, currentEpoch)))
            .Select(r => r.RunId).ToArray();
        if (pendingTerminal.Length == 0 && !before.Any(r => r.OnlineHoeingHandoff is { } handoff
            && OriginalOnlineHandoffProcessExited(handoff, currentEpoch)))
            return new OnlineHandoffRecoveryResult(0,
                before.Count(r => r.OnlineHoeingHandoff is not null), 0);

        await EnsureRecoveredAsync().ConfigureAwait(false);
        // 同一助手进程中 BGI 单独退出时，启动恢复扫描已执行过；无驱动的旧停驻记录
        // 仍需在原身份下标 Unknown，随后才可按已确认的取消/退出事实结清。
        foreach (var run in _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList())
        {
            if (run.State != WorkflowRunState.OnlineHoeingParking
                || run.OnlineHoeingHandoff is not { } old
                || !OriginalOnlineHandoffProcessExited(old, currentEpoch)) continue;
            lock (_gate)
            {
                if (_drives.ContainsKey(run.WorkflowId)) continue;
                _runs.UpdateMergingIf(run.RunId, latest =>
                {
                    if (latest.State != WorkflowRunState.OnlineHoeingParking
                        || latest.OnlineHoeingHandoff is not { } current
                        || !OriginalOnlineHandoffProcessExited(current, currentEpoch)) return false;
                    latest.State = WorkflowRunState.Unknown;
                    latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                        + "原 BGI 进程已退出，旧联机接管进入身份核对；未确认的结果保留。";
                    return true;
                }, out _);
            }
        }
        var settled = new List<string>();
        foreach (var run in _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList())
        {
            if (run.OnlineHoeingHandoff is not { } handoff
                || !OriginalOnlineHandoffProcessExited(handoff, currentEpoch)) continue;
            if (!_runs.UpdateMergingIf(run.RunId, latest =>
                {
                    var old = latest.OnlineHoeingHandoff;
                    var original = latest.CurrentSubmission;
                    if (latest.State != WorkflowRunState.Unknown
                        || latest.OnlineHoeingGateParked
                        || old is not { Phase: "parked", ResumeJobId: null, Ticket: not null }
                        || !OriginalOnlineHandoffProcessExited(old, currentEpoch)
                        || original is not { ObservedTerminal: "cancelled", ExecutionExitConfirmed: true }
                        || original.Key != old.OriginalSubmissionKey
                        || original.JobId != old.OriginalJobId
                        || original.Epoch != old.Epoch || original.NodeId != old.NodeId
                        || original.Occurrence != old.Occurrence
                        || original.LoopIteration != old.LoopIteration
                        || original.Attempt != old.Attempt) return false;

                    var candidate = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(latest))!;
                    candidate.StopRequested = true;
                    candidate.State = WorkflowRunState.Cancelled;
                    var archived = candidate.OnlineHoeingHandoff!;
                    archived.Phase = "stopped";
                    candidate.OnlineHoeingHistory.Add(archived.Archived());
                    candidate.OnlineHoeingHandoff = null;
                    if (RunStore.HasUnresolvedTerminalResponsibility(candidate)
                        || !TerminalReleaseEvidence.ValidOnlineHoeingHandoff(
                            candidate, candidate.OnlineHoeingHistory[^1])
                        || !TerminalReleaseEvidence.RunSettled(candidate)) return false;

                    old.Phase = "stopped";
                    latest.OnlineHoeingHistory.Add(old.Archived());
                    latest.OnlineHoeingHandoff = null;
                    latest.StopRequested = true;
                    latest.State = WorkflowRunState.Cancelled;
                    latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                        + "原 BGI 进程已退出且原作业取消/退出已确认；自动结束旧计划，未把未执行节点记为成功。";
                    return true;
                }, out _)) continue;
            settled.Add(run.RunId);
            _log?.Invoke("[任务中心] 已自动结清旧联机接管运行：" + run.RunId);
        }
        if (settled.Count > 0) NotifyStateChanged();
        var pendingReconciliations = 0;
        foreach (var runId in settled.Concat(pendingTerminal).Distinct(StringComparer.Ordinal))
        {
            var result = await ReconcileAdmissionTerminalForExplicitStopAsync(
                runId, "旧联机接管运行及受理登记已自动结清").ConfigureAwait(false);
            if (!result.Ok)
            {
                pendingReconciliations++;
                _log?.Invoke("[任务中心] 旧运行已结束，受理登记仍待自动对账："
                    + runId + " " + result.Message);
            }
        }
        var remaining = _runs.List().Where(run => !IsExecutionResetReleased(run)).Count(r => r.OnlineHoeingHandoff is not null);
        return new OnlineHandoffRecoveryResult(settled.Count, remaining, pendingReconciliations);
    }

    // 旧运行仍是 Unknown 时，只要原进程身份已确认退出，就不再占用新批次；
    // 当前 BGI 尚未启动也可据此安全走冷启动。仍存活或不可考一律阻断。
    private bool BlocksCurrentOnlineBatch(WorkflowRunRecord run, string? currentEpoch)
    {
        var handoff = run.OnlineHoeingHandoff;
        if (handoff is null) return false;
        return run.State != WorkflowRunState.Unknown
            || !OriginalOnlineHandoffProcessExited(handoff, currentEpoch);
    }

    internal static bool? ProbeOriginalBgiProcess(int pid, long startTicksUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicksUtc;
        }
        catch (ArgumentException) { return false; } // PID 不存在，原执行进程确定已退出。
        catch { return null; } // 权限/进程状态不可考时保持阻断。
    }

    /// <summary>当前进程动作的阻断判断；旧进程已退出的 Unknown 仍完整留在 RunStore，未被结清。</summary>
    internal bool HasUnsettledOnlineHoeingHandoff
    {
        get
        {
            try
            {
                var current = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
                var epoch = WorkflowStopAuthority.Epoch(current);
                return _runs.List().Where(run => !IsExecutionResetReleased(run)).Any(r => BlocksCurrentOnlineBatch(r, epoch));
            }
            catch { return true; } // 运行台账不可读不等于没有待恢复责任。
        }
    }

    internal bool CanOpenOnlineSendAfterBatch
    {
        get
        {
            if (Volatile.Read(ref _manualResetActive) != 0) return false;
            try
            {
                var current = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
                var epoch = WorkflowStopAuthority.Epoch(current);
                return _runs.List().Where(run => !IsExecutionResetReleased(run)).All(r => !BlocksCurrentOnlineBatch(r, epoch)
                    || r.OnlineHoeingHandoff?.Phase is "resumeAccepted" or "observing" or "terminal");
            }
            catch { return false; }
        }
    }

    /// <summary>先封住全部任务中心发送，再读取 BGI 的同纪元执行根。</summary>
    internal Task CloseOnlineHoeingSendAsync(CancellationToken ct = default, bool priority = false)
    {
        ct.ThrowIfCancellationRequested();
        if (!priority) return _onlineSendGate.CloseAsync(ct);
        // BGI's retained takeover excludes already-sent ordinary work at execution admission.
        // A lost reply on that old send must not delay claiming the physical execution slot.
        _onlineSendGate.CloseForPriority();
        return Task.CompletedTask;
    }

    internal async Task BeginManualResetAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _manualResetActive, 1);
        try { await _onlineSendGate.CloseAsync(ct).ConfigureAwait(false); }
        catch
        {
            Interlocked.Exchange(ref _manualResetActive, 0);
            throw;
        }
    }

    // Physical exclusion belongs to the closed send gate and BGI's targeted suspend.
    // The old plan's recovery bookkeeping must never veto a priority hoeing request.
    internal OnlineHoeingPreparation PreparePriorityOnlineHoeing(int generation,
        CommandExecutor.ExecutionBoundarySnapshot snapshot)
    {
        if (!_onlineSendGate.IsClosed || !snapshot.Known
            || snapshot.Active is null && snapshot.SlotOccupied)
            return new OnlineHoeingPreparation(false, null, "等待普通任务派发暂停及 BGI 实际执行状态");
        try
        {
            var prepared = PrepareOnlineHoeing(generation, snapshot, stateContinuation: true, priorityRecovery: true);
            if (_runs.PriorityPreparationReadError is { } readError)
                prepared = prepared with { Reason = readError + (prepared.Reason is null ? "" : "；" + prepared.Reason) };
            if (prepared.Ready) return prepared;
            _log?.Invoke("[联机优先] 原计划恢复信息待补，不阻止 BGI 让位：" + prepared.Reason);
            return new OnlineHoeingPreparation(true, null, prepared.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke("[联机优先] 原计划记录暂不可用，不阻止 BGI 让位：" + ex.Message);
            return new OnlineHoeingPreparation(true, null, ex.Message);
        }
    }

    internal OnlineHoeingPreparation PrepareOnlineHoeing(int generation,
        CommandExecutor.ExecutionBoundarySnapshot snapshot, bool stateContinuation = false, bool priorityRecovery = false)
    {
        if (!_onlineSendGate.IsClosed)
            return new OnlineHoeingPreparation(false, null, "任务中心接管闸门尚未关闭");
        if (!snapshot.Known || snapshot.Active is null && snapshot.SlotOccupied)
            return new OnlineHoeingPreparation(false, null, "BGI 执行根或任务槽位不可考");
        var epoch = $"{snapshot.ProcessId}:{snapshot.StartTicksUtc}";
        if ((_onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch) is not { } current
            || epoch != $"{current.ProcessId}:{current.StartTicksUtc}")
            return new OnlineHoeingPreparation(false, null, "BGI 进程纪元在接管前发生变化");

        WorkflowRunRecord? target = null;
        var currentRuns = (priorityRecovery ? _runs.ReadPriorityPreparationRecords() : _runs.List())
            .Where(run => !IsExecutionResetReleased(run)).ToList();
        if (currentRuns.Any(r => BlocksCurrentOnlineBatch(r, epoch)))
            return new OnlineHoeingPreparation(false, null, "上一轮计划接管或恢复作业尚未结清");
        var runs = currentRuns.Where(r => !r.IsTerminal).ToList();
        if (snapshot.Active is { RunId: { } wire, JobId: { } job })
        {
            var sameWire = runs.Where(r => Guid.TryParse(r.WireRunId, out var candidate)
                && candidate == wire).ToList();
            var matches = runs.Where(r => Guid.TryParse(r.WireRunId, out var candidate) && candidate == wire
                && Guid.TryParse(r.CurrentSubmission?.JobId, out var submitted) && submitted == job).ToList();
            if (matches.Count > 1 || sameWire.Count > 0 && matches.Count != 1)
                return new OnlineHoeingPreparation(false, null, "BGI 运行身份与计划提交身份不能唯一对应");
            target = matches.SingleOrDefault();
        }
        else if (snapshot.Active is { RunId: { } unmatchedRun })
        {
            if (runs.Any(r => Guid.TryParse(r.WireRunId, out var candidate) && candidate == unmatchedRun))
                return new OnlineHoeingPreparation(false, null, "计划运行已起步但 BGI 作业句柄未确认");
        }
        else if (snapshot.Active is null && !snapshot.SlotOccupied)
        {
            // 两个任务之间 BGI 无活体根；必须从本宿主仍在驱动的唯一计划判断，而非把 no_task 当计划结束。
            lock (_gate)
            {
                var driving = runs.Where(r => _drives.ContainsKey(r.WorkflowId)
                    && r.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.LocalWaitParking)
                    .ToList();
                if (driving.Count > 1)
                    return new OnlineHoeingPreparation(false, null, "空隙中存在多条在驱动计划，无法唯一绑定恢复对象");
                target = driving.SingleOrDefault();
            }
        }
        if (target is null)
        {
            lock (_gate) { _onlineHoeingRunId = null; _onlineHoeingGeneration = generation; }
            return new OnlineHoeingPreparation(true, null, null);
        }
        if (_onlineEpochProvider is null
            && _clientAccessor()?.HasCapability("task.planContinuation.v1") != true)
            return new OnlineHoeingPreparation(false, null,
                "BGI 尚未声明计划表联机续跑能力，未打断当前计划");
        if (snapshot.Active is null && RunStore.HasUnresolvedExternalFact(target))
            return new OnlineHoeingPreparation(false, null,
                "任务间隙仍有未确认的节点提交或终态，不能按空闲接管");
        if (target.Cursor is not { } cursor)
            return new OnlineHoeingPreparation(false, null, "计划运行缺当前节点游标");
        if (priorityRecovery)
        {
            lock (_gate)
                if (_drives.TryGetValue(target.WorkflowId, out var originalDriver)
                    && !originalDriver.Cts.IsCancellationRequested && !originalDriver.Task.IsCompleted)
                    _runs.RetainPriorityProgress(target);
        }
        var expectedJob = target.CurrentSubmission?.JobId;
        if (!_runs.UpdateMergingIf(target.RunId, latest =>
            {
                if (latest.IsTerminal || latest.OnlineHoeingHandoff is not null
                    || latest.Cursor is not { } currentCursor
                    || currentCursor.NodeId != cursor.NodeId
                    || currentCursor.Occurrence != cursor.Occurrence
                    || currentCursor.LoopIteration != cursor.LoopIteration
                    || currentCursor.Attempt != cursor.Attempt
                    || latest.CurrentSubmission?.JobId != expectedJob) return false;
                latest.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
                {
                    Generation = generation,
                    StateContinuation = stateContinuation,
                    PriorityRecovery = priorityRecovery,
                    Epoch = epoch,
                    NodeId = cursor.NodeId,
                    Occurrence = cursor.Occurrence,
                    LoopIteration = cursor.LoopIteration,
                    Attempt = cursor.Attempt,
                    OriginalJobId = snapshot.Active is null ? null : expectedJob,
                    OriginalSubmissionKey = target.CurrentSubmission?.Key,
                    Phase = snapshot.Active is null ? "preparedGap" : "prepared",
                };
                return true;
            }, out _))
            return new OnlineHoeingPreparation(false, null, "计划游标在接管登记期间发生变化");
        if (stateContinuation) _stateContinuationRuns[target.RunId] = generation;
        lock (_gate) { _onlineHoeingRunId = target.RunId; _onlineHoeingGeneration = generation; }
        _onlineResumeSignals.TryAdd(target.RunId,
            new TaskCompletionSource<OnlineResumeSignal>(TaskCreationOptions.RunContinuationsAsynchronously));
        NotifyStateChanged();
        return new OnlineHoeingPreparation(true, target.RunId, null);
    }

    // Only a targeted suspend's explicit never-accepted reply may discard this preparation.
    // The original plan and its cursor remain intact; no parked/resumed handoff can enter here.
    internal bool RefreshUnacceptedOnlineHoeingPreparation(int generation)
    {
        string? runId;
        lock (_gate)
            runId = _onlineHoeingGeneration == generation ? _onlineHoeingRunId : null;
        if (runId is null) return true;
        if (!_runs.UpdateMergingIf(runId, latest =>
            {
                if (latest.StopRequested) return false;
                if (latest.OnlineHoeingHandoff is null) return true;
                var current = latest.OnlineHoeingHandoff;
                if (current.Generation != generation || current.Phase is not ("prepared" or "preparedGap")
                    || current.Ticket is not null || current.ResumeJobId is not null
                    || latest.State == WorkflowRunState.OnlineHoeingParking) return false;
                latest.OnlineHoeingHistory.Add(current.Archived());
                latest.OnlineHoeingHandoff = null;
                return true;
            }, out _)) return false;
        lock (_gate)
            if (_onlineHoeingGeneration == generation && _onlineHoeingRunId == runId)
                _onlineHoeingRunId = null;
        _onlineResumeSignals.TryRemove(runId, out _);
        return true;
    }

    internal bool ConfirmOnlineHoeingSuspend(int generation, string? ticket, string? rawData)
    {
        string? runId;
        lock (_gate) { runId = _onlineHoeingGeneration == generation ? _onlineHoeingRunId : null; }
        if (runId is null) return true;
        return ConfirmOnlineHoeingSuspendForRun(runId, generation, ticket, rawData);
    }

    private bool ConfirmOnlineHoeingSuspendForRun(string runId, int generation, string? ticket, string? rawData)
    {
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(rawData)) return false;
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(rawData); }
        catch (JsonException) { return false; }
        if (data.ValueKind != JsonValueKind.Object) return false;
        static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        static int? Int(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
        var status = String(data, "status");
        // The runner can park the same original job while this receipt is being merged.
        // Validate the latest identity atomically; a forward phase transition is not an identity mismatch.
        return _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.StopRequested || latest.OnlineHoeingHandoff is not { } current
                || current.Generation != generation
                || current.Ticket is { } existingTicket && existingTicket != ticket) return false;
            var gap = current.Phase is "preparedGap" or "gap";
            var matched = gap ? status == "no_task"
                : current.Phase is "prepared" or "suspended" or "parked"
                  && status == "suspended"
                  && Guid.TryParse(String(data, "workflowRunId"), out var wire)
                  && Guid.TryParse(latest.WireRunId, out var expectedWire) && wire == expectedWire
                  && String(data, "nodeId") == current.NodeId
                  && Guid.TryParse(String(data, "originalJobId"), out var original)
                  && Guid.TryParse(current.OriginalJobId, out var expectedOriginal) && original == expectedOriginal
                  && Int(data, "occurrence") == current.Occurrence
                  && Int(data, "iteration") == current.LoopIteration
                  && Int(data, "attempt") == current.Attempt;
            if (!matched) return false;
            current.Ticket = ticket;
            current.Phase = gap ? "gap" : current.Phase == "parked" ? "parked" : "suspended";
            current.CheckpointPhase = String(data, "planCheckpointPhase");
            current.NextTaskIndex = Int(data, "planNextTaskIndex");
            return true;
        }, out _);
    }

    internal async Task<string?> RestorePriorityPlanAsync(OnlineHoeingBatch batch)
    {
        if (!batch.RestoredFromRecovery || !batch.PriorityRecoveryEnabled || batch.IsCancellationRequested
            || batch.ExplicitStopRequested || batch.TakeoverTicket is not { } ticket) return null;
        batch.ObserveRecoveryStopSignal();
        batch.Cts.Token.ThrowIfCancellationRequested();
        var currentEpoch = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
        if (!ViewModels.CoordinatedBatchLoop.SameEpoch(batch.ExecutionEpoch, currentEpoch)) return null;
        await EnsureRecoveredAsync().ConfigureAwait(false);
        if (batch.ManagedRunId is { } managed && _runs.Load(managed) is { } savedIntent
            && (savedIntent.StopRequested || IsExecutionResetReleased(savedIntent)))
        { batch.Cancel(); return null; }
        string? wire = null;
        if (batch.SuspendedRawData is { } raw)
        {
            try
            {
                using var receipt = JsonDocument.Parse(raw);
                if (receipt.RootElement.TryGetProperty("workflowRunId", out var field) && field.ValueKind == JsonValueKind.String)
                    wire = field.GetString();
            }
            catch (JsonException) { return null; }
        }
        var matches = _runs.List().Where(run => !run.StopRequested && !IsExecutionResetReleased(run)
            && (batch.ManagedRunId is null || run.RunId == batch.ManagedRunId)
            && run.OnlineHoeingHandoff is { StateContinuation: true, PriorityRecovery: true } h
            && h.Generation == batch.Generation && h.Epoch == WorkflowStopAuthority.Epoch(currentEpoch)
            && (h.Ticket == ticket || h.Ticket is null && Guid.TryParse(wire, out var originalWire)
                && Guid.TryParse(run.WireRunId, out var savedWire) && originalWire == savedWire)).ToArray();
        if (matches.Length != 1) return null;
        var original = matches[0];
        var handoff = original.OnlineHoeingHandoff!;
        if (batch.Policy?.Policy == TaskConflictPolicy.Resume && handoff.Phase is not
            ("preparedGap" or "gap" or "prepared" or "suspended" or "parked" or "resumeRequested" or "resumeAccepted" or "observing" or "terminal"))
            return null;
        if (handoff.Ticket is null && !ConfirmOnlineHoeingSuspendForRun(original.RunId, batch.Generation,
                ticket, batch.SuspendedRawData)) return null;
        lock (_gate)
        {
            if (_shutdown || _manualResetActive != 0 || _onlineHoeingGeneration > batch.Generation
                || _onlineHoeingGeneration == batch.Generation && _onlineHoeingRunId is { } other && other != original.RunId)
                return null;
            _onlineHoeingGeneration = batch.Generation;
            _onlineHoeingRunId = original.RunId;
            _stateContinuationRuns[original.RunId] = batch.Generation;
            if (batch.Policy?.Policy != TaskConflictPolicy.Resume || handoff.Phase is "preparedGap" or "gap"
                || _drives.ContainsKey(original.WorkflowId) || _reservedWorkflows.Contains(original.WorkflowId))
                return original.RunId;
            if (!_runs.UpdateMergingIf(original.RunId, latest =>
            {
                if (latest.StopRequested || latest.OnlineHoeingHandoff is not { } held
                    || held.Generation != batch.Generation || held.Ticket != ticket || latest.WireRunId != original.WireRunId)
                    return false;
                latest.State = held.Phase is "observing" or "terminal"
                    ? WorkflowRunState.Running : WorkflowRunState.OnlineHoeingParking;
                return true;
            }, out var retained) || retained is null) return null;
            _runs.RetainPriorityProgress(retained);
            _reservedWorkflows.Add(original.WorkflowId);
        }
        try
        {
            var runner = CreateRunner(_clientAccessor());
            var accepted = LaunchDrive(original.WorkflowId, runner,
                cts => runner.ResumeOnlineContinuationAsync(original.RunId, cts.Token),
                "已接回原计划的联机恢复观察", original.RunId);
            return accepted.Ok ? original.RunId : null;
        }
        catch
        {
            lock (_gate) _reservedWorkflows.Remove(original.WorkflowId);
            throw;
        }
    }

    internal bool ObserveManagedCompletionRetry(OnlineHoeingBatch batch, string runId)
    {
        if (batch.PlanResumeContinuation?.GetValueOrDefault("idempotencyKey") is not string originalKey) return true;
        var run = _runs.Load(runId);
        if (run is null) return false;
        if (run.StopRequested || IsExecutionResetReleased(run)) { batch.Cancel(); return false; }
        var handoff = run.OnlineHoeingHandoff?.ResumeKey == originalKey ? run.OnlineHoeingHandoff
            : run.OnlineHoeingHistory.SingleOrDefault(h => h.ResumeKey == originalKey && h.Generation == batch.Generation);
        if (handoff is not { PriorityRecovery: true, ResumeAttempt: not null }) return true;
        var submissions = run.SubmissionHistory.Concat(run.CurrentSubmission is { } current ? [current] : [])
            .Where(s => s.NodeId == handoff.NodeId && s.Occurrence == handoff.Occurrence
                && s.LoopIteration == handoff.LoopIteration && s.Attempt >= handoff.ResumeAttempt
                && s.Intent == SubmitIntentState.Accepted && Guid.TryParse(s.JobId, out _)).ToArray();
        var latest = submissions.OrderByDescending(s => s.Attempt).FirstOrDefault();
        if (latest is null) return false;
        if (batch.PolicyExecution is { } known && !string.Equals(known.JobId, latest.JobId, StringComparison.OrdinalIgnoreCase))
        {
            static bool SameJob(string? a, string? b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;
            if (!SameJob(known.JobId, latest.JobId))
            {
                var old = submissions.SingleOrDefault(s => SameJob(s.JobId, known.JobId));
                if (old is not { ObservedTerminal: "failed", ExecutionExitConfirmed: true }) return false;
                batch.PolicyTerminalResult ??= new BgiTaskQueueStatus
                { TaskHandle = known.JobId, Epoch = known.Epoch, IdempotencyKey = old.Key, Status = "failed", ExecutionExitConfirmed = true };
                var parts = latest.Epoch?.Split(':');
                if (parts is not { Length: 2 } || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks)) return false;
                batch.ManagedRunId ??= runId;
                if (!batch.AdoptManagedCompletionRetry(runId, latest.Key, new(latest.JobId!, new() { ProcessId = pid, StartTicksUtc = ticks }, true)))
                    return false;
                batch.PersistRecovery?.Invoke();
            }
        }
        // A failed original resume remains this same arrangement until its original Runner
        // has taken and completed a later attempt of the same node occurrence.
        return latest.ExecutionExitConfirmed && latest.ObservedTerminal == "succeeded";
    }

    internal string? OnlineHoeingManagedRun(int generation)
    {
        lock (_gate) return _onlineHoeingGeneration == generation ? _onlineHoeingRunId : null;
    }

    internal bool CanOpenRetainedPrioritySend(int generation, string runId)
    {
        if (Volatile.Read(ref _manualResetActive) != 0 || !_runs.HasPriorityProgress(runId)) return false;
        lock (_gate)
        {
            if (_onlineHoeingGeneration != generation || _onlineHoeingRunId != runId) return false;
            var original = _runs.Load(runId);
            if (original is null || original.StopRequested) return false;
            return original.OnlineHoeingHandoff is { StateContinuation: true, PriorityRecovery: true,
                       Phase: "resumeAccepted" or "observing" or "terminal" } handoff && handoff.Generation == generation
                || original.OnlineHoeingHistory.Any(h => h.StateContinuation && h.PriorityRecovery
                    && h.Generation == generation && h.Phase is "terminal" or "released");
        }
    }

    internal async Task OpenOnlineHoeingSendAsync(string? skipAutoRunId = null, bool priorityRecovery = false)
    {
        if (Volatile.Read(ref _manualResetActive) != 0) return;
        await _onlineSendGate.OpenAsync().ConfigureAwait(false);
        try { await WakeOnlineHoeingWaitersAsync(skipAutoRunId).ConfigureAwait(false); }
        catch (Exception ex) when (priorityRecovery && OnlineRecordTemporarilyUnavailable(ex))
        {
            Interlocked.Exchange(ref _priorityWaiterWakePending, 1);
            _log?.Invoke("[联机优先] 实际派发已恢复，旧等待计划记录稍后自动补查：" + ex.Message);
        }
    }

    // Called by the existing owner heartbeat. It never opens a gate closed by a newer batch.
    internal void RetryPriorityWaiterWake()
    {
        lock (_gate)
        {
            if (_shutdown || Volatile.Read(ref _priorityWaiterWakePending) == 0 || _onlineSendGate.IsClosed
                || Volatile.Read(ref _manualResetActive) != 0 || _priorityWaiterWakeTask is { IsCompleted: false }) return;
            _priorityWaiterWakeTask = Task.Run(async () =>
            {
                try { await WakeOnlineHoeingWaitersAsync(null).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) { }
                catch (Exception ex) { _log?.Invoke("[联机优先] 旧等待计划仍待自动补查：" + ex.Message); }
            });
        }
    }

    private async Task WakeOnlineHoeingWaitersAsync(string? skipAutoRunId)
    {
        await _onlineWaiterWakeGate.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try { await WakeOnlineHoeingWaitersCoreAsync(skipAutoRunId).ConfigureAwait(false); }
        finally { _onlineWaiterWakeGate.Release(); }
    }

    private static bool IsPendingOnlineWaiter(WorkflowRunRecord run)
        => !run.StopRequested && run.OnlineHoeingGateParked && run.OnlineHoeingHandoff is null
            && !IsExecutionResetReleased(run) && !RunStore.HasUnresolvedExternalFact(run)
            && run.State is WorkflowRunState.LocalWaitParking or WorkflowRunState.Interrupted;

    private async Task WakeOnlineHoeingWaitersCoreAsync(string? skipAutoRunId)
    {
        if (_onlineSendGate.IsClosed || Volatile.Read(ref _manualResetActive) != 0 || _shutdownCts.IsCancellationRequested) return;
        var pending = false;
        foreach (var parked in _runs.List().Where(r => IsPendingOnlineWaiter(r) && r.RunId != skipAutoRunId))
        {
            if (_onlineSendGate.IsClosed || Volatile.Read(ref _manualResetActive) != 0 || _shutdownCts.IsCancellationRequested) return;
            for (var i = 0; i < 20 && IsDriving(parked.WorkflowId); i++)
                await Task.Delay(100, _shutdownCts.Token).ConfigureAwait(false);
            if (_onlineSendGate.IsClosed || Volatile.Read(ref _manualResetActive) != 0 || _shutdownCts.IsCancellationRequested) return;
            if (IsDriving(parked.WorkflowId)) { pending = true; continue; }
            var current = _runs.Load(parked.RunId);
            if (current is null || !IsPendingOnlineWaiter(current)) continue;
            // The original Runner consumes this marker in the same write that changes it to Running.
            // A refusal or failed publication therefore cannot lose the only automatic wake request.
            var resumed = await ResumeRunAsync(parked.RunId).ConfigureAwait(false);
            if (!resumed.Ok) _log?.Invoke("[任务中心] 锄地后等待计划未能自动接续：" + resumed.Message);
            var remaining = _runs.Load(parked.RunId);
            pending |= remaining is not null && IsPendingOnlineWaiter(remaining);
        }
        Interlocked.Exchange(ref _priorityWaiterWakePending, pending ? 1 : 0);
        NotifyStateChanged();
    }

    /// <summary>
    /// 用户确认“全局状态重置”后的人工放弃：保留原运行和未知事实，
    /// 但终止本轮联机接管责任，禁止旧计划在重置后自动续跑。
    /// 只有执行环境已经确认复位时才重新打开发送闸门。
    /// </summary>
    internal async Task<(int Abandoned, bool SendGateOpened)> ForceAbandonOnlineHoeingForManualResetAsync(
        string reason, bool openSendGate)
    {
        // 全局复位也必须先经过任务中心现有的所有者获取/恢复屏障。
        // 重置按钮可能早于第一次 Start/Resume 被点击；直接写 RunStore 会被所有者护栏拒绝，
        // 并把可预期的阻断冒泡成未处理 UI 异常。取得失败时保守保持发送闸门关闭。
        try
        {
            await EnsureRecoveredAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("[任务中心] 全局复位未取得运行目录所有者能力，保留记录并关闭发送闸门：" + ex.Message);
            return (0, false);
        }

        // 先给所有当前有驱动/等待状态的任务登记 Stop；BGI 随后由全局复位受控重建，
        // 这样普通单机/一条龙的宿主驱动也不会在复位后继续发下一项。
        var activeRunIds = _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList()
            .Where(r => ActiveStates.Contains(r.State))
            .Select(r => r.RunId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var runId in activeRunIds)
        {
            try
            {
                var stop = await RequestRunActionAsync(runId, WorkflowRunAction.Stop)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (!stop.Ok)
                    _log?.Invoke("[任务中心] 全局复位 Stop 未确认：" + runId + " " + stop.Message);
            }
            catch (Exception ex)
            {
                _log?.Invoke("[任务中心] 全局复位登记 Stop 未完成（将由 BGI 重建兜底）："
                    + runId + " " + ex.Message);
            }
        }

        var abandoned = 0;
        foreach (var run in _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList())
        {
            if (run.OnlineHoeingHandoff is null && !run.OnlineHoeingGateParked)
                continue;

            if (_runs.UpdateMergingIf(run.RunId, latest =>
                {
                    if (latest.OnlineHoeingHandoff is { } handoff)
                    {
                        handoff.Phase = "manual_abandoned";
                        latest.OnlineHoeingHistory.Add(handoff.Archived());
                        latest.OnlineHoeingHandoff = null;
                    }

                    latest.OnlineHoeingGateParked = false;
                    latest.StopRequested = true;
                    if (!latest.IsTerminal)
                        latest.State = WorkflowRunState.Unknown;
                    latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                        + "用户执行全局状态重置，旧联机接管已人工放弃；原始历史与未知结果保留，禁止自动续跑。"
                        + (string.IsNullOrWhiteSpace(reason) ? "" : "原因：" + reason);
                    return true;
                }, out _))
            {
                abandoned++;
            }
        }

        lock (_gate)
        {
            _onlineHoeingRunId = null;
            _onlineHoeingGeneration = 0;
            foreach (var signal in _onlineResumeSignals.Values)
                signal.TrySetResult(new OnlineResumeSignal(null, "用户执行全局状态重置，旧计划禁止自动恢复"));
            _onlineResumeSignals.Clear();
        }

        var sendGateOpened = false;
        if (openSendGate)
        {
            var driversSettled = false;
            for (var i = 0; i < 20; i++)
            {
                lock (_gate) driversSettled = _drives.Count == 0;
                if (driversSettled) break;
                await Task.Delay(250).ConfigureAwait(false);
            }

            // 只看进程内驱动数量会漏掉未驱动但仍处于活动态的耐久运行。
            // 这些运行必须至少已落盘停止意图，否则不能宣称任务中心复位完成。
            var unstoppedRuns = _runs.List().Where(run => !IsExecutionResetReleased(run)).ToList()
                .Where(r => ActiveStates.Contains(r.State) && !r.StopRequested)
                .Select(r => r.RunId).ToArray();
            if (driversSettled && unstoppedRuns.Length == 0)
            {
                await _onlineSendGate.OpenAsync().ConfigureAwait(false);
                sendGateOpened = true;
                Interlocked.Exchange(ref _manualResetActive, 0);
            }
            else
                _log?.Invoke("[任务中心] 全局复位后仍有运行驱动或未登记停止的活动运行，发送闸门保持关闭"
                    + (unstoppedRuns.Length == 0 ? "" : "：" + string.Join(",", unstoppedRuns)));
        }
        NotifyStateChanged();
        return (abandoned, sendGateOpened);
    }

    internal async Task<bool> FinishOnlineGapAfterReleaseAsync(string runId, string ticket)
    {
        if (Volatile.Read(ref _manualResetActive) != 0) return false;
        var existing = _runs.Load(runId);
        if (existing is null || existing.StopRequested) return false;
        var ticketHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
        bool SameRelease(OnlineHoeingHandoffRecord h) => h.Phase == "released"
            && (h.Ticket == ticket || h.TicketHash == ticketHash);
        var released = existing.OnlineHoeingHistory.LastOrDefault(SameRelease);
        if (existing.OnlineHoeingHandoff is not null || released is null)
        {
            if (!_runs.UpdateMergingIf(runId, latest =>
                {
                    if (latest.StopRequested || latest.IsTerminal || RunStore.HasUnresolvedExternalFact(latest)
                        || latest.OnlineHoeingHandoff is not { Phase: "gap" } handoff
                        || handoff.Ticket != ticket) return false;
                    handoff.Phase = "released";
                    latest.OnlineHoeingHistory.Add(handoff.Archived());
                    latest.OnlineHoeingHandoff = null;
                    return true;
                }, out var fresh) || fresh is null) return false;
            released = fresh.OnlineHoeingHistory.Last(SameRelease);
        }
        // The release publication may have succeeded before a later List/Load failed.
        // Resume the remaining steps from the same archived ticket, without another release.
        await OpenOnlineHoeingSendAsync(skipAutoRunId: runId).ConfigureAwait(false);
        if (Volatile.Read(ref _manualResetActive) != 0) return false;
        var afterOpen = _runs.Load(runId);
        if (afterOpen is null || afterOpen.StopRequested) return false;
        if (afterOpen.IsTerminal || IsDriving(afterOpen.WorkflowId)) return true;
        if (afterOpen.State == WorkflowRunState.LocalWaitParking)
        {
            var resumed = await ResumeRunAsync(runId).ConfigureAwait(false);
            if (!resumed.Ok && !released.StateContinuation)
                MarkReleasedGapUnknown(runId, resumed.Message);
            return resumed.Ok;
        }
        var continued = afterOpen.State is WorkflowRunState.Running or WorkflowRunState.Waiting;
        if (!continued && !released.StateContinuation)
            MarkReleasedGapUnknown(runId, "计划状态未进入可接续执行");
        return continued;
    }

    private void MarkReleasedGapUnknown(string runId, string reason)
    {
        _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.IsTerminal || latest.StopRequested || latest.OnlineHoeingHistory.LastOrDefault()?.Phase != "released")
                return false;
            latest.State = WorkflowRunState.Unknown;
            latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                + "联机锄地后原计划空隙接续未确认：" + reason;
            return true;
        }, out _);
    }

    internal async Task<bool> SettleFailedOnlineHoeingAsync(int generation, string runId,
        bool physicalCleanupConfirmed, string reason)
    {
        bool Owns(WorkflowRunRecord? run) => run?.RunId == runId
            && run.OnlineHoeingHandoff is { } handoff && handoff.Generation == generation;
        lock (_gate)
            if (_onlineHoeingGeneration != generation || _onlineHoeingRunId != runId) return false;
        var original = _runs.Load(runId);
        if (!Owns(original))
        {
            // Finalization may have succeeded while admission writeback was unavailable.
            // Retry only that same stopped history; never create another handoff or run.
            if (!physicalCleanupConfirmed || original?.RunId != runId
                || original.OnlineHoeingHandoff is not null || original.State != WorkflowRunState.Cancelled
                || !original.StopRequested || original.OnlineHoeingHistory.LastOrDefault() is not { Phase: "stopped" } history
                || history.Generation != generation || !TerminalReleaseEvidence.RunSettled(original)) return false;
            return await FinishFailedOnlineAdmissionAsync(generation, runId, reason).ConfigureAwait(false);
        }
        if (!physicalCleanupConfirmed)
        {
            FailPlanResume(runId, reason + "；本批作业与接管票据的物理清理未确认，保留原计划责任", generation);
            return false;
        }

        var handoff = original!.OnlineHoeingHandoff!;
        var sameOriginalStillRunning = false;
        if (handoff is { Phase: "prepared", Ticket: null, ResumeJobId: null, OriginalJobId: not null }
            && original is { StopRequested: false, OnlineHoeingGateParked: false, State: WorkflowRunState.Running }
            && original.CurrentSubmission is { ObservedTerminal: null } submission
            && submission.JobId == handoff.OriginalJobId && submission.Key == handoff.OriginalSubmissionKey
            && _clientAccessor() is { State: BgiExternalLinkState.Ready } client
            && WorkflowStopAuthority.Epoch(client.ServerEpoch) == handoff.Epoch)
        {
            // A locally unchanged record alone cannot prove that suspend did not happen:
            // the original runner may not yet have persisted its cancellation observation.
            try
            {
                using var queryBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var (status, job) = await client.QueryJobStatusAsync(handoff.OriginalJobId, queryBudget.Token)
                    .ConfigureAwait(false);
                sameOriginalStillRunning = status == "running" && job is { State: "running", ExecutionExitConfirmed: false }
                    && WorkflowStopAuthority.Epoch(job.Epoch) == handoff.Epoch
                    && WorkflowStopAuthority.Epoch(client.ServerEpoch) == handoff.Epoch
                    && Guid.TryParse(job.JobId, out var observedJob)
                    && Guid.TryParse(handoff.OriginalJobId, out var expectedJob) && observedJob == expectedJob
                    && job.IdempotencyKey == handoff.OriginalSubmissionKey
                    && Guid.TryParse(job.WorkflowRunId, out var observedRun)
                    && Guid.TryParse(original.WireRunId, out var expectedRun) && observedRun == expectedRun
                    && job.NodeId == handoff.NodeId
                    && job.Occurrence == handoff.Occurrence && job.Iteration == handoff.LoopIteration
                    && job.Attempt == handoff.Attempt;
            }
            catch { /* An unknown original state retains the handoff. */ }
        }

        var withdrewPreparation = false;
        lock (_gate)
        {
            if (_onlineHoeingGeneration != generation || _onlineHoeingRunId != runId) return false;
            withdrewPreparation = _runs.UpdateMergingIf(runId, latest =>
            {
                if (!Owns(latest) || latest.StopRequested || latest.IsTerminal
                    || latest.OnlineHoeingHandoff is not { Ticket: null, ResumeJobId: null } current
                    || current.Phase != handoff.Phase || current.Epoch != handoff.Epoch
                    || current.OriginalJobId != handoff.OriginalJobId
                    || current.OriginalSubmissionKey != handoff.OriginalSubmissionKey
                    || latest.CurrentSubmission?.Key != original.CurrentSubmission?.Key
                    || latest.CurrentSubmission?.JobId != original.CurrentSubmission?.JobId
                    || latest.CurrentSubmission?.ObservedTerminal != original.CurrentSubmission?.ObservedTerminal
                    || latest.Cursor is not { } cursor || cursor.NodeId != current.NodeId
                    || cursor.Occurrence != current.Occurrence || cursor.LoopIteration != current.LoopIteration
                    || cursor.Attempt != current.Attempt) return false;
                var noSendGap = current.Phase == "preparedGap" && current.OriginalJobId is null
                    && latest.State == WorkflowRunState.LocalWaitParking && latest.OnlineHoeingGateParked
                    && !_drives.ContainsKey(latest.WorkflowId);
                if (noSendGap)
                {
                    var facts = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(latest))!;
                    facts.OnlineHoeingHandoff = null;
                    if (RunStore.HasUnresolvedTerminalResponsibility(facts)) return false;
                }
                else if (!sameOriginalStillRunning || current.Phase != "prepared"
                    || latest.State != WorkflowRunState.Running || latest.OnlineHoeingGateParked) return false;
                latest.OnlineHoeingHandoff = null;
                latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                    + $"联机批次 {generation} 未接管原执行，仅撤销准备登记（原提交 {current.OriginalSubmissionKey ?? "无"}）：{reason}";
                return true;
            }, out _);
        }
        if (withdrewPreparation)
        {
            ClearFailedOnlineHandoffOwner(generation, runId, reason);
            NotifyStateChanged();
            return CanOpenOnlineSendAfterBatch;
        }

        var beforeStop = _runs.Load(runId);
        if (!Owns(beforeStop)) return false;
        // Never stop an original execution merely because preparation could not be undone.
        // Only a durable takeover/yield can authorize stopping this owned plan.
        if (beforeStop!.OnlineHoeingHandoff is not { ResumeJobId: null } toStop
            || toStop.Phase is not ("suspended" or "parked" or "gap")
                && !(beforeStop.CurrentSubmission is { ObservedTerminal: "cancelled", ExecutionExitConfirmed: true } yielded
                    && yielded.Key == toStop.OriginalSubmissionKey && yielded.JobId == toStop.OriginalJobId))
            return false;
        try
        {
            var stop = await RequestRunActionAsync(runId, WorkflowRunAction.Stop)
                .WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            for (var i = 0; i < 150; i++)
            {
                var stopped = _runs.Load(runId);
                if (!Owns(stopped) || stopped!.OnlineHoeingHandoff!.ResumeJobId is not null) return false;
                if (stopped.State != WorkflowRunState.Cancelled && !IsDriving(stopped.WorkflowId))
                {
                    // An exited driver cannot consume its queued Stop. The original cancellation
                    // and exit must already be durable; no live root or unknown result is guessed.
                    lock (_gate)
                    {
                        _runs.UpdateMergingIf(runId, latest =>
                        {
                            if (!Owns(latest) || latest.IsTerminal || !latest.StopRequested
                                || latest.OnlineHoeingGateParked || _drives.ContainsKey(latest.WorkflowId)
                                || _reservedWorkflows.Contains(latest.WorkflowId)
                                || latest.OnlineHoeingHandoff is not { ResumeJobId: null, Ticket: not null } current
                                || latest.CurrentSubmission is not { ObservedTerminal: "cancelled", ExecutionExitConfirmed: true } exited
                                || exited.Key != current.OriginalSubmissionKey || exited.JobId != current.OriginalJobId
                                || exited.Epoch != current.Epoch || exited.WireRunId != latest.WireRunId
                                || exited.NodeId != current.NodeId || exited.Occurrence != current.Occurrence
                                || exited.LoopIteration != current.LoopIteration || exited.Attempt != current.Attempt) return false;
                            var candidate = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(latest))!;
                            candidate.StopRequested = true;
                            candidate.State = WorkflowRunState.Cancelled;
                            candidate.OnlineHoeingHandoff!.Phase = "stopped";
                            candidate.OnlineHoeingHistory.Add(candidate.OnlineHoeingHandoff.Archived());
                            candidate.OnlineHoeingHandoff = null;
                            if (RunStore.HasUnresolvedTerminalResponsibility(candidate)
                                || !TerminalReleaseEvidence.ValidOnlineHoeingHandoff(candidate, candidate.OnlineHoeingHistory[^1])
                                || !TerminalReleaseEvidence.RunSettled(candidate)) return false;
                            latest.State = WorkflowRunState.Cancelled;
                            latest.Note = (latest.Note is null ? "" : latest.Note + " | ")
                                + "联机批次失败/取消，原作业取消及退出均已确认；结束原计划且不创建恢复作业：" + reason;
                            return true;
                        }, out stopped);
                    }
                    if (!Owns(stopped)) return false;
                }
                if (stopped.State == WorkflowRunState.Cancelled && stopped.StopRequested)
                {
                    // A missing ticket still cannot manufacture an auditable stopped handoff.
                    if (stopped.OnlineHoeingHandoff.Ticket is null
                        || !FinalizeStoppedOnlineHandoff(runId, generation)) return false;
                    NotifyStateChanged();
                    return await FinishFailedOnlineAdmissionAsync(generation, runId, reason).ConfigureAwait(false);
                }
                if (!stop.Ok && !IsDriving(stopped.WorkflowId)) return false;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("[任务中心] 联机失败后的原计划停止尚未确认：" + runId + " " + ex.Message);
        }
        return false;
    }

    internal async Task<bool> SettleCancelledOnlinePolicyAsync(int generation, string runId)
    {
        bool Matches(WorkflowRunRecord? run) => run?.RunId == runId
            && (run.OnlineHoeingHandoff?.Generation == generation
                || run.OnlineHoeingHandoff is null && run.OnlineHoeingHistory.LastOrDefault()?.Generation == generation);
        var run = _runs.Load(runId);
        if (!Matches(run)) return false;
        if (run!.OnlineHoeingHandoff is null && run.IsTerminal && TerminalReleaseEvidence.RunSettled(run))
            return await FinishFailedOnlineAdmissionAsync(generation, runId, "本批停止后的计划责任已结清").ConfigureAwait(false);
        try { await RequestRunActionAsync(runId, WorkflowRunAction.Stop).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false); }
        catch { return false; }
        for (var i = 0; i < 150 && IsDriving(run.WorkflowId); i++) await Task.Delay(100).ConfigureAwait(false);
        run = _runs.Load(runId);
        if (!Matches(run) || IsDriving(run!.WorkflowId)) return false;
        if (run.OnlineHoeingHandoff is null)
            return run.IsTerminal && TerminalReleaseEvidence.RunSettled(run)
                && (await FinishFailedOnlineAdmissionAsync(generation, runId, "本批停止后的计划责任已结清").ConfigureAwait(false));
        if (run.OnlineHoeingHandoff.ResumeJobId is null)
            return await SettleFailedOnlineHoeingAsync(generation, runId, true, "本批停止，原计划不再续跑").ConfigureAwait(false);

        var h = run.OnlineHoeingHandoff;
        var ext = _clientAccessor();
        if (ext is not { State: BgiExternalLinkState.Ready }
            || WorkflowStopAuthority.Epoch(ext.ServerEpoch) != h.Epoch) return false;
        BgiJobInfo? observed;
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (_, observed) = await ext.QueryJobStatusAsync(h.ResumeJobId, budget.Token).ConfigureAwait(false);
        }
        catch { return false; }
        static bool SameGuid(string? a, string? b) => Guid.TryParse(a, out var left) && Guid.TryParse(b, out var right) && left == right;
        if (observed is not { ExecutionExitConfirmed: true } || !BgiJobTerminalPolling.IsTerminal(observed.State)
            || !SameGuid(observed.JobId, h.ResumeJobId) || observed.IdempotencyKey != h.ResumeKey
            || !SameGuid(observed.WorkflowRunId, run.WireRunId) || observed.NodeId != h.NodeId
            || observed.Occurrence != h.Occurrence || observed.Iteration != h.LoopIteration || observed.Attempt != h.ResumeAttempt
            || WorkflowStopAuthority.Epoch(observed.Epoch) != h.Epoch
            || WorkflowStopAuthority.Epoch(ext.ServerEpoch) != h.Epoch) return false;
        var finalized = false;
        lock (_gate)
        {
            finalized = _runs.UpdateMergingIf(runId, latest =>
            {
                if (!Matches(latest) || !latest.IsTerminal && !latest.StopRequested || _drives.ContainsKey(latest.WorkflowId)
                    || _reservedWorkflows.Contains(latest.WorkflowId)
                    || latest.OnlineHoeingHandoff is not { } current || current.Generation != generation
                    || current.ResumeJobId != h.ResumeJobId || current.ResumeKey != h.ResumeKey
                    || current.ResumeAttempt != h.ResumeAttempt || current.Epoch != h.Epoch
                    || string.IsNullOrEmpty(current.ResumeFingerprint) || string.IsNullOrEmpty(current.ResumeExpiresAtUtc)) return false;
                var candidate = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(latest))!;
                var takeover = candidate.OnlineHoeingHandoff!;
                var originals = TerminalReleaseEvidence.Submissions(candidate).Where(s => s.Key == takeover.OriginalSubmissionKey
                    && s.JobId == takeover.OriginalJobId && s.Epoch == takeover.Epoch && s.WireRunId == candidate.WireRunId
                    && s.NodeId == takeover.NodeId && s.Occurrence == takeover.Occurrence && s.LoopIteration == takeover.LoopIteration
                    && s.Attempt == takeover.Attempt && s.ObservedTerminal == "cancelled" && s.ExecutionExitConfirmed).ToArray();
                if (originals.Length != 1 || takeover.ResumeAttempt != takeover.Attempt + 1) return false;
                var old = originals[0];
                var oldOutcomes = candidate.NodeOutcomes.Where(o => o.SubmissionKey == old.Key).ToArray();
                int sequence;
                if (oldOutcomes.Length == 1) sequence = oldOutcomes[0].SequenceIndex;
                else if (oldOutcomes.Length == 0)
                {
                    try
                    {
                        var snapshot = _workflows.LoadSnapshot(candidate.WorkflowId);
                        if (snapshot.Revision != candidate.WorkflowRevision
                            || !new WorkflowPlan(snapshot.Document).TryLocate(takeover.NodeId, takeover.Occurrence,
                                takeover.LoopIteration, out var location)) return false;
                        sequence = location.SequenceIndex;
                    }
                    catch { return false; }
                    candidate.NodeOutcomes.Add(new WorkflowNodeOutcome
                    {
                        NodeId = old.NodeId, SequenceIndex = sequence, Occurrence = old.Occurrence, LoopIteration = old.LoopIteration,
                        Attempt = old.Attempt, Result = "cancelled", RawTerminal = "cancelled", Reason = "online_hoeing_preempted",
                        SubmissionKey = old.Key, AcceptedSendIdentity = old.AcceptedSendIdentity, RetryNextAttempt = takeover.ResumeAttempt
                    });
                }
                else return false;
                if (candidate.CurrentSubmission?.Key == old.Key)
                {
                    candidate.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(old))!);
                    candidate.CurrentSubmission = new WorkflowSubmission
                    {
                        Key = takeover.ResumeKey!, JobId = takeover.ResumeJobId, NodeId = takeover.NodeId,
                        Occurrence = takeover.Occurrence, LoopIteration = takeover.LoopIteration, Attempt = takeover.ResumeAttempt!.Value,
                        Intent = SubmitIntentState.Accepted, SendAttempted = true, Epoch = takeover.Epoch, WireRunId = candidate.WireRunId,
                        Fingerprint = takeover.ResumeFingerprint, ExpiresAtUtc = takeover.ResumeExpiresAtUtc
                    };
                }
                if (candidate.CurrentSubmission is not { } resumed || resumed.Key != takeover.ResumeKey
                    || !SameGuid(resumed.JobId, takeover.ResumeJobId) || resumed.Epoch != takeover.Epoch
                    || resumed.NodeId != takeover.NodeId || resumed.Occurrence != takeover.Occurrence
                    || resumed.LoopIteration != takeover.LoopIteration || resumed.Attempt != takeover.ResumeAttempt) return false;
                if (resumed.ObservedTerminal is not null && resumed.ObservedTerminal != observed.State
                    || latest.State == WorkflowRunState.Succeeded && observed.State != "succeeded") return false;
                resumed.ObservedTerminal = observed.State;
                resumed.ExecutionExitConfirmed = true;
                resumed.ExecutionExitDisposition = observed.ExecutionExitDisposition;
                resumed.EffectState = observed.State;
                if (!candidate.NodeOutcomes.Any(o => o.SubmissionKey == resumed.Key))
                    candidate.NodeOutcomes.Add(new WorkflowNodeOutcome
                    {
                        NodeId = resumed.NodeId, SequenceIndex = sequence, Occurrence = resumed.Occurrence,
                        LoopIteration = resumed.LoopIteration, Attempt = resumed.Attempt, Result = observed.State!, RawTerminal = observed.State,
                        SubmissionKey = resumed.Key, Reason = "联机批次停止时，精确恢复作业真实终态及退出已确认"
                    });
                takeover.ResumeTerminal = observed.State;
                takeover.Phase = "terminal";
                candidate.OnlineHoeingHistory.Add(takeover.Archived());
                candidate.OnlineHoeingHandoff = null;
                if (!candidate.IsTerminal) candidate.State = WorkflowRunState.Cancelled;
                if (!TerminalReleaseEvidence.ValidOnlineHoeingHandoff(candidate, candidate.OnlineHoeingHistory[^1])
                    || !TerminalReleaseEvidence.RunSettled(candidate)) return false;
                latest.SubmissionHistory = candidate.SubmissionHistory;
                latest.CurrentSubmission = candidate.CurrentSubmission;
                latest.NodeOutcomes = candidate.NodeOutcomes;
                latest.OnlineHoeingHistory = candidate.OnlineHoeingHistory;
                latest.OnlineHoeingHandoff = null;
                latest.State = candidate.State;
                return true;
            }, out _);
        }
        if (!finalized) return false;
        NotifyStateChanged();
        return await FinishFailedOnlineAdmissionAsync(generation, runId, "本批停止后的恢复作业及计划责任已结清").ConfigureAwait(false);
    }

    private async Task<bool> FinishFailedOnlineAdmissionAsync(int generation, string runId, string reason)
    {
        var result = await ReconcileAdmissionTerminalForExplicitStopAsync(runId,
            "联机批次取消/失败后的原计划及受理登记已结清").ConfigureAwait(false);
        if (!result.Ok)
        {
            _log?.Invoke("[任务中心] 原计划已停止，受理责任仍待核对：" + runId + " " + result.Message);
            return false;
        }
        ClearFailedOnlineHandoffOwner(generation, runId, reason);
        return CanOpenOnlineSendAfterBatch;
    }

    private void ClearFailedOnlineHandoffOwner(int generation, string runId, string reason)
    {
        lock (_gate)
        {
            if (_onlineHoeingGeneration != generation || _onlineHoeingRunId != runId) return;
            _onlineHoeingRunId = null;
            if (_onlineResumeSignals.TryRemove(runId, out var signal))
                signal.TrySetResult(new OnlineResumeSignal(null, reason));
        }
    }

    internal bool FinalizeStoppedOnlineHandoff(string runId, int? expectedGeneration = null)
        => _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.State != WorkflowRunState.Cancelled || !latest.StopRequested
                || latest.OnlineHoeingHandoff is not { ResumeJobId: null } handoff
                || expectedGeneration is { } generation && handoff.Generation != generation) return false;
            var externalFacts = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(latest))!;
            externalFacts.OnlineHoeingHandoff = null;
            if (RunStore.HasUnresolvedTerminalResponsibility(externalFacts)) return false;
            handoff.Phase = "stopped";
            latest.OnlineHoeingHistory.Add(handoff.Archived());
            latest.OnlineHoeingHandoff = null;
            return true;
        }, out _);

    internal async Task<IReadOnlyDictionary<string, object?>?> PreparePlanResumeAsync(string runId, string ticket,
        CancellationToken ct = default)
    {
        // A successful prepare can outlive a lost caller receipt. Reconstruct its immutable
        // continuation instead of asking the same handoff to become parked a second time.
        var prior = _runs.Load(runId);
        if (Volatile.Read(ref _manualResetActive) == 0 && prior is { StopRequested: false }
            && prior.State == WorkflowRunState.OnlineHoeingParking
            && prior.OnlineHoeingHandoff is { Phase: "resumeRequested", ResumeKey: not null,
                ResumeAttempt: not null, OriginalJobId: not null } requested
            && requested.Ticket == ticket
            && prior.CurrentSubmission is { ObservedTerminal: "cancelled", ExecutionExitConfirmed: true })
        {
            ct.ThrowIfCancellationRequested();
            var originalEpoch = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
            if (originalEpoch is not null && requested.Epoch == $"{originalEpoch.ProcessId}:{originalEpoch.StartTicksUtc}")
                return new Dictionary<string, object?>
                {
                    ["workflowRunId"] = prior.WireRunId, ["nodeId"] = requested.NodeId,
                    ["occurrence"] = requested.Occurrence, ["iteration"] = requested.LoopIteration,
                    ["originalJobId"] = requested.OriginalJobId, ["resumeAttempt"] = requested.ResumeAttempt.Value,
                    ["idempotencyKey"] = requested.ResumeKey
                };
            return null;
        }
        for (var i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _manualResetActive) != 0) return null;
            var ready = _runs.Load(runId);
            if (ready?.State == WorkflowRunState.OnlineHoeingParking
                && ready.OnlineHoeingHandoff is { Phase: "parked", Ticket: { } savedTicket }
                && savedTicket == ticket) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        var run = _runs.Load(runId);
        if (Volatile.Read(ref _manualResetActive) != 0) return null;
        var handoff = run?.OnlineHoeingHandoff;
        if (run is null || handoff is not { Phase: "parked", OriginalJobId: not null }
            || handoff.Ticket != ticket || run.StopRequested
            || handoff.CheckpointPhase == "preExecution"
            || run.CurrentSubmission is not { ObservedTerminal: "cancelled", ExecutionExitConfirmed: true })
            return null;
        try
        {
            if (_admissionWired)
            {
                _runs.VerifyBoundOwner();
                if (TryGetAdmissionScopeForResume(run.RunId, run.WorkflowId) is null)
                    return null;
                var originalHandoff = _admissionStore?.Read().File?.Handoff;
                if (run.CurrentSubmission is not { } originalSubmission
                    || ResolveOriginalNodeSendIdentity(run, originalSubmission, originalHandoff) is null)
                    return null;
            }
            var epoch = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
            if (epoch is null || handoff.Epoch != $"{epoch.ProcessId}:{epoch.StartTicksUtc}")
                return null;
        }
        catch { return null; }
        try
        {
            var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
            if (snapshot.Revision != run.WorkflowRevision) return null;
            var plan = new WorkflowPlan(snapshot.Document);
            plan.ValidatePathCursor(run);
            if (!plan.TryLocate(handoff.NodeId, handoff.Occurrence, handoff.LoopIteration, out _))
                return null;
        }
        catch { return null; }
        var nextAttempt = handoff.Attempt + 1;
        var key = RunStore.DeriveSubmissionKey(run.RunId, handoff.NodeId,
            handoff.Occurrence, handoff.LoopIteration, nextAttempt);
        var continuation = new Dictionary<string, object?>
        {
            ["workflowRunId"] = run.WireRunId,
            ["nodeId"] = handoff.NodeId,
            ["occurrence"] = handoff.Occurrence,
            ["iteration"] = handoff.LoopIteration,
            ["originalJobId"] = handoff.OriginalJobId,
            ["resumeAttempt"] = nextAttempt,
            ["idempotencyKey"] = key,
        };
        var payload = JsonSerializer.Serialize(continuation);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..24].ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");
        if (!_runs.UpdateMergingIf(runId, latest =>
            {
                if (latest.StopRequested || latest.State != WorkflowRunState.OnlineHoeingParking
                    || latest.OnlineHoeingHandoff is not { Phase: "parked" } current
                    || current.Ticket != ticket || current.OriginalJobId != handoff.OriginalJobId) return false;
                current.ResumeKey = key;
                current.ResumeAttempt = nextAttempt;
                current.ResumeFingerprint = fingerprint;
                current.ResumeExpiresAtUtc = expires;
                current.Phase = "resumeRequested";
                return true;
            }, out _)) return null;
        return continuation;
    }

    internal bool AcceptPlanResumeJob(string runId, string ticket, string jobId)
    {
        if (Volatile.Read(ref _manualResetActive) != 0) return false;
        if (!Guid.TryParse(jobId, out var parsedJob)) return false;
        var existing = _runs.Load(runId);
        if (existing is { StopRequested: false, OnlineHoeingHandoff:
                { Phase: "resumeAccepted" or "observing" or "terminal" } duplicate
            }
            && duplicate.Ticket == ticket
            && Guid.TryParse(duplicate.ResumeJobId, out var alreadyAccepted)
            && alreadyAccepted == parsedJob) return true;
        var stopPending = false;
        if (!_runs.UpdateMergingIf(runId, latest =>
            {
                if (latest.OnlineHoeingHandoff is not { Phase: "resumeRequested" } handoff
                    || handoff.Ticket != ticket || handoff.ResumeKey is null) return false;
                handoff.ResumeJobId = parsedJob.ToString("D");
                stopPending = latest.StopRequested;
                handoff.Phase = stopPending ? "stopPending" : "resumeAccepted";
                return true;
            }, out _)) return false;
        if (stopPending) return false;
        _onlineResumeSignals.GetOrAdd(runId,
            _ => new TaskCompletionSource<OnlineResumeSignal>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(new OnlineResumeSignal(parsedJob.ToString("D"), null));
        return true;
    }

    internal void FailPlanResume(string runId, string reason, int? expectedGeneration = null)
    {
        var marked = _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.OnlineHoeingHandoff is not { } handoff || latest.IsTerminal
                || expectedGeneration is { } generation && handoff.Generation != generation) return false;
            handoff.Phase = "unknown";
            latest.State = WorkflowRunState.Unknown;
            latest.Note = (latest.Note is null ? "" : latest.Note + " | ") + reason;
            return true;
        }, out _);
        if (expectedGeneration.HasValue && !marked) return;
        if (marked)
        {
            DriveEntry? drive;
            lock (_gate) _drives.TryGetValue(_runs.Load(runId)?.WorkflowId ?? "", out drive);
            if (drive?.Runner.HasActiveControl(runId) == true)
                drive.Runner.RequestAction(runId, WorkflowRunAction.Stop);
        }
        // 未进入 AwaitOnlineHoeingContinuationAsync 时也可能失败（例如停止终态超时）。
        // 用普通结果传递失败，避免创建无人观察的 faulted Task，随后由 GC 在任意时刻弹全局错误框。
        _onlineResumeSignals.GetOrAdd(runId,
            _ => new TaskCompletionSource<OnlineResumeSignal>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(new OnlineResumeSignal(null, reason));
        NotifyStateChanged();
    }

    internal async Task<BoundaryTerminalResult?> AwaitOnlineHoeingContinuationAsync(
        BgiWorkflowExecutionBoundary boundary, WorkflowRunRecord run, WorkflowSubmission original,
        BoundaryTerminalResult originalTerminal, CancellationToken ct)
    {
        if (_stateContinuationRuns.ContainsKey(run.RunId) || run.OnlineHoeingHandoff?.StateContinuation == true)
            return await AwaitStateContinuationResumeAsync(boundary, run, original, originalTerminal, ct).ConfigureAwait(false);
        // 接管登记在 Runner 等待 BGI 终态期间由宿主落盘；Runner 的旧内存副本不含该标记。
        var latestBeforePark = _runs.Load(run.RunId);
        var handoff = latestBeforePark?.OnlineHoeingHandoff;
        if (handoff?.StateContinuation == true)
            return await AwaitStateContinuationResumeAsync(boundary, run, original, originalTerminal, ct).ConfigureAwait(false);
        if (originalTerminal.ErrorCode != "preempted" || !originalTerminal.ExecutionExitConfirmed
            || handoff is null || latestBeforePark?.StopRequested == true
            || handoff.OriginalJobId != original.JobId || handoff.OriginalSubmissionKey != original.Key
            || handoff.NodeId != original.NodeId || handoff.Occurrence != original.Occurrence
            || handoff.LoopIteration != original.LoopIteration || handoff.Attempt != original.Attempt)
            return null;
        if (!_runs.UpdateMergingIf(run.RunId, latest =>
            {
                if (latest.StopRequested || latest.OnlineHoeingHandoff is not { } current
                    || current.Generation != handoff.Generation || current.Phase is not ("prepared" or "suspended")
                    || latest.CurrentSubmission?.Key != original.Key) return false;
                latest.CurrentSubmission.ObservedTerminal = "cancelled";
                latest.CurrentSubmission.ExecutionExitConfirmed = true;
                latest.CurrentSubmission.ExecutionExitDisposition = originalTerminal.ExecutionExitDisposition;
                latest.CurrentSubmission.EffectState = "cancelled";
                latest.State = WorkflowRunState.OnlineHoeingParking;
                current.Phase = "parked";
                return true;
            }, out _)) return BoundaryTerminalResult.UncertainWith("原计划让位身份未能耐久停驻");
        NotifyStateChanged();
        OnlineResumeSignal signal;
        try
        {
            signal = await _onlineResumeSignals.GetOrAdd(run.RunId,
                _ => new TaskCompletionSource<OnlineResumeSignal>(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return BoundaryTerminalResult.UncertainWith("恢复未确认：" + ex.Message); }
        if (signal.FailureReason is { } failure)
            return BoundaryTerminalResult.UncertainWith("恢复未确认：" + failure);
        if (string.IsNullOrWhiteSpace(signal.JobId))
            return BoundaryTerminalResult.UncertainWith("恢复作业身份缺失");
        var resumedJobId = signal.JobId;

        var parked = _runs.Load(run.RunId);
        var accepted = parked?.OnlineHoeingHandoff;
        if (parked is null || accepted is not { Phase: "resumeAccepted", ResumeKey: not null,
                ResumeAttempt: not null, ResumeFingerprint: not null, ResumeExpiresAtUtc: not null }
            || accepted.ResumeJobId != resumedJobId || parked.StopRequested)
            return BoundaryTerminalResult.UncertainWith("恢复作业受理身份与停驻运行不符");

        int sequenceIndex;
        try
        {
            var snapshot = _workflows.LoadSnapshot(parked.WorkflowId);
            if (snapshot.Revision != parked.WorkflowRevision
                || !new WorkflowPlan(snapshot.Document).TryLocate(accepted.NodeId,
                    accepted.Occurrence, accepted.LoopIteration, out var located))
                return BoundaryTerminalResult.UncertainWith("锄地期间计划修订或节点路径发生变化");
            sequenceIndex = located.SequenceIndex;
        }
        catch { return BoundaryTerminalResult.UncertainWith("恢复节点定义不可读取"); }

        if (!_runs.UpdateMergingIf(run.RunId, latest =>
            {
                if (latest.StopRequested || latest.State != WorkflowRunState.OnlineHoeingParking
                    || latest.OnlineHoeingHandoff is not { Phase: "resumeAccepted" } current
                    || current.ResumeJobId != resumedJobId
                    || latest.CurrentSubmission is not { ObservedTerminal: "cancelled" } old
                    || latest.Cursor is not { } cursor || cursor.Attempt != current.Attempt) return false;
                latest.NodeOutcomes.Add(new WorkflowNodeOutcome
                {
                    NodeId = current.NodeId,
                    Occurrence = current.Occurrence,
                    LoopIteration = current.LoopIteration,
                    SequenceIndex = sequenceIndex,
                    Attempt = old.Attempt,
                    Result = "cancelled",
                    RawTerminal = "cancelled",
                    Reason = "online_hoeing_preempted",
                    SubmissionKey = old.Key,
                    AcceptedSendIdentity = old.AcceptedSendIdentity,
                    RetryNextAttempt = current.ResumeAttempt,
                });
                latest.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(old))!);
                cursor.Attempt = current.ResumeAttempt!.Value;
                latest.CurrentSubmission = new WorkflowSubmission
                {
                    Key = current.ResumeKey!, NodeId = current.NodeId,
                    Occurrence = current.Occurrence, LoopIteration = current.LoopIteration,
                    Attempt = current.ResumeAttempt.Value, Intent = SubmitIntentState.Accepted,
                    JobId = resumedJobId, SendAttempted = true,
                    Epoch = current.Epoch, WireRunId = latest.WireRunId,
                    Fingerprint = current.ResumeFingerprint,
                    ExpiresAtUtc = current.ResumeExpiresAtUtc,
                    RecordedAt = DateTimeOffset.UtcNow,
                };
                current.Phase = "observing";
                latest.State = WorkflowRunState.Running;
                return true;
            }, out var observing) || observing is null)
            return BoundaryTerminalResult.UncertainWith("恢复作业未能绑定原计划节点");

        var resume = observing.OnlineHoeingHandoff!;
        var terminal = await boundary.AwaitOnlineResumeJobAsync(observing, resume, resumedJobId,
            resume.ResumeKey!, resume.ResumeAttempt!.Value, ct).ConfigureAwait(false);
        if (!terminal.Uncertain)
        {
            if (!_runs.UpdateMergingIf(run.RunId, latest =>
            {
                if (latest.OnlineHoeingHandoff is not { Phase: "observing" } current
                    || current.ResumeJobId != resumedJobId) return false;
                current.ResumeTerminal = terminal.Terminal;
                current.Phase = "terminal";
                return true;
            }, out _))
                return BoundaryTerminalResult.UncertainWith("恢复作业终态已观察但关联落盘未确认");
        }
        return terminal;
    }
    private static bool OnlineRecordTemporarilyUnavailable(Exception exception)
        => exception is System.IO.IOException or UnauthorizedAccessException or JsonException;

    private async Task<BoundaryTerminalResult?> AwaitStateContinuationResumeAsync(
        BgiWorkflowExecutionBoundary boundary, WorkflowRunRecord run, WorkflowSubmission original,
        BoundaryTerminalResult originalTerminal, CancellationToken ct)
    {
        if (originalTerminal.ErrorCode != "preempted" || !originalTerminal.ExecutionExitConfirmed) return null;
        static bool SameJob(string? a, string? b) => Guid.TryParse(a, out var left)
            && Guid.TryParse(b, out var right) && left == right;
        BoundaryTerminalResult? observedTerminal = null;
        string? observingJob = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var latest = _runs.Load(run.RunId);
                if (latest is null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
                if (latest.StopRequested || Volatile.Read(ref _manualResetActive) != 0)
                    return BoundaryTerminalResult.UncertainWith("原计划已被明确停止或重置");
                var handoff = latest.OnlineHoeingHandoff;
                if (handoff is null || !handoff.StateContinuation
                    || !SameJob(handoff.OriginalJobId, original.JobId) || handoff.OriginalSubmissionKey != original.Key
                    || handoff.NodeId != original.NodeId || handoff.Occurrence != original.Occurrence
                    || handoff.LoopIteration != original.LoopIteration || handoff.Attempt != original.Attempt
                    || latest.WireRunId != run.WireRunId)
                    return BoundaryTerminalResult.UncertainWith("原计划接续身份已改变，未接回其他任务");
                RunStore.RebaseOnto(run, latest);
                if (handoff.Phase is "prepared" or "suspended")
                {
                    _runs.UpdateMergingIf(run.RunId, current =>
                    {
                        if (current.StopRequested || current.OnlineHoeingHandoff is not { } active
                            || active.Generation != handoff.Generation || active.Phase is not ("prepared" or "suspended")
                            || current.CurrentSubmission?.Key != original.Key) return false;
                        current.CurrentSubmission.ObservedTerminal = "cancelled";
                        current.CurrentSubmission.ExecutionExitConfirmed = true;
                        current.CurrentSubmission.ExecutionExitDisposition = originalTerminal.ExecutionExitDisposition;
                        current.CurrentSubmission.EffectState = "cancelled";
                        current.State = WorkflowRunState.OnlineHoeingParking;
                        active.Phase = "parked";
                        return true;
                    }, out _);
                    continue;
                }
                if (handoff.Phase is "parked" or "resumeRequested")
                { await Task.Delay(100, ct).ConfigureAwait(false); continue; }
                if (handoff is not { Phase: "resumeAccepted" or "observing" or "terminal", ResumeKey: not null,
                    ResumeAttempt: not null, ResumeJobId: not null, ResumeFingerprint: not null, ResumeExpiresAtUtc: not null })
                    return BoundaryTerminalResult.UncertainWith("原计划恢复身份尚未取得有效受理事实");
                if (observingJob != null && !SameJob(observingJob, handoff.ResumeJobId))
                    return BoundaryTerminalResult.UncertainWith("原恢复任务编号发生变化，未接回另一任务");
                observingJob = handoff.ResumeJobId;
                if (handoff.Phase == "resumeAccepted")
                {
                    var snapshot = _workflows.LoadSnapshot(latest.WorkflowId);
                    if (snapshot.Revision != latest.WorkflowRevision
                        || !new WorkflowPlan(snapshot.Document).TryLocate(handoff.NodeId, handoff.Occurrence,
                            handoff.LoopIteration, out var located))
                        return BoundaryTerminalResult.UncertainWith("锄地期间计划修订或节点路径发生变化");
                    _runs.UpdateMergingIf(run.RunId, current =>
                    {
                        if (current.StopRequested || current.State != WorkflowRunState.OnlineHoeingParking
                            || current.OnlineHoeingHandoff is not { Phase: "resumeAccepted" } active
                            || !SameJob(active.ResumeJobId, observingJob) || active.ResumeKey != handoff.ResumeKey
                            || current.CurrentSubmission is not { ObservedTerminal: "cancelled" } old
                            || old.Key != original.Key || current.Cursor is not { } cursor || cursor.Attempt != active.Attempt)
                            return false;
                        current.NodeOutcomes.Add(new WorkflowNodeOutcome
                        {
                            NodeId = active.NodeId, Occurrence = active.Occurrence, LoopIteration = active.LoopIteration,
                            SequenceIndex = located.SequenceIndex, Attempt = old.Attempt, Result = "cancelled",
                            RawTerminal = "cancelled", Reason = "online_hoeing_preempted", SubmissionKey = old.Key,
                            AcceptedSendIdentity = old.AcceptedSendIdentity, RetryNextAttempt = active.ResumeAttempt,
                        });
                        current.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(old))!);
                        cursor.Attempt = active.ResumeAttempt!.Value;
                        current.CurrentSubmission = new WorkflowSubmission
                        {
                            Key = active.ResumeKey!, NodeId = active.NodeId, Occurrence = active.Occurrence,
                            LoopIteration = active.LoopIteration, Attempt = active.ResumeAttempt.Value,
                            Intent = SubmitIntentState.Accepted, JobId = observingJob, SendAttempted = true,
                            Epoch = active.Epoch, WireRunId = current.WireRunId, Fingerprint = active.ResumeFingerprint,
                            ExpiresAtUtc = active.ResumeExpiresAtUtc, RecordedAt = DateTimeOffset.UtcNow,
                        };
                        active.Phase = "observing";
                        current.State = WorkflowRunState.Running;
                        return true;
                    }, out _);
                    continue;
                }
                if (latest.CurrentSubmission is not { } resumed || resumed.Key != handoff.ResumeKey
                    || !SameJob(resumed.JobId, observingJob) || resumed.Attempt != handoff.ResumeAttempt)
                    return BoundaryTerminalResult.UncertainWith("原恢复任务与计划当前提交身份不符");
                if (handoff.Phase == "terminal" && handoff.ResumeTerminal is { } savedTerminal
                    && resumed.ObservedTerminal == savedTerminal && resumed.ExecutionExitConfirmed)
                {
                    RunStore.RebaseOnto(run, latest);
                    return observedTerminal ?? BoundaryTerminalResult.Observed(savedTerminal,
                        exitConfirmed: true, exitDisposition: resumed.ExecutionExitDisposition);
                }
                observedTerminal ??= await boundary.AwaitOnlineResumeJobAsync(latest, handoff, observingJob,
                    handoff.ResumeKey, handoff.ResumeAttempt.Value, ct).ConfigureAwait(false);
                if (observedTerminal.Uncertain) return observedTerminal;
                if (_runs.UpdateMergingIf(run.RunId, current =>
                {
                    if (current.StopRequested || current.OnlineHoeingHandoff is not { Phase: "observing" } active
                        || !SameJob(active.ResumeJobId, observingJob) || active.ResumeKey != handoff.ResumeKey
                        || current.CurrentSubmission?.Key != handoff.ResumeKey) return false;
                    active.ResumeTerminal = observedTerminal.Terminal;
                    active.Phase = "terminal";
                    current.CurrentSubmission.ObservedTerminal = observedTerminal.Terminal;
                    current.CurrentSubmission.ExecutionExitConfirmed = observedTerminal.ExecutionExitConfirmed;
                    current.CurrentSubmission.ExecutionExitDisposition = observedTerminal.ExecutionExitDisposition;
                    current.CurrentSubmission.EffectState = observedTerminal.Terminal;
                    return true;
                }, out var finished) && finished != null)
                {
                    RunStore.RebaseOnto(run, finished);
                    return observedTerminal;
                }
            }
            catch (Exception exception) when (OnlineRecordTemporarilyUnavailable(exception) && !ct.IsCancellationRequested)
            { /* 同一 Runner 保留原恢复任务，文件可写后只补关联，不重发恢复。 */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

}
