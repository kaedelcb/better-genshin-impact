using System.Text.Json;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _onlineResumeSignals = new();

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
        var before = _runs.List();
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
        foreach (var run in _runs.List())
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
        foreach (var run in _runs.List())
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
        var remaining = _runs.List().Count(r => r.OnlineHoeingHandoff is not null);
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
                return _runs.List().Any(r => BlocksCurrentOnlineBatch(r, epoch));
            }
            catch { return true; } // 运行台账不可读不等于没有待恢复责任。
        }
    }

    internal bool CanOpenOnlineSendAfterBatch
    {
        get
        {
            try
            {
                var current = _onlineEpochProvider?.Invoke() ?? _clientAccessor()?.ServerEpoch;
                var epoch = WorkflowStopAuthority.Epoch(current);
                return _runs.List().All(r => !BlocksCurrentOnlineBatch(r, epoch)
                    || r.OnlineHoeingHandoff?.Phase is "resumeAccepted" or "observing" or "terminal");
            }
            catch { return false; }
        }
    }

    /// <summary>先封住全部任务中心发送，再读取 BGI 的同纪元执行根。</summary>
    internal Task CloseOnlineHoeingSendAsync(CancellationToken ct = default)
        => _onlineSendGate.CloseAsync(ct);

    internal OnlineHoeingPreparation PrepareOnlineHoeing(int generation,
        CommandExecutor.ExecutionBoundarySnapshot snapshot)
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
        var currentRuns = _runs.List();
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
        lock (_gate) { _onlineHoeingRunId = target.RunId; _onlineHoeingGeneration = generation; }
        _onlineResumeSignals.TryAdd(target.RunId,
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        NotifyStateChanged();
        return new OnlineHoeingPreparation(true, target.RunId, null);
    }

    internal bool ConfirmOnlineHoeingSuspend(int generation, string? ticket, string? rawData)
    {
        string? runId;
        lock (_gate) { runId = _onlineHoeingGeneration == generation ? _onlineHoeingRunId : null; }
        if (runId is null) return true;
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(rawData)) return false;
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(rawData); }
        catch (JsonException) { return false; }
        if (data.ValueKind != JsonValueKind.Object) return false;
        var run = _runs.Load(runId);
        var handoff = run?.OnlineHoeingHandoff;
        if (run is null || handoff is null || handoff.Generation != generation) return false;
        static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        static int? Int(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number)
                ? number : null;
        var status = String(data, "status");
        var matched = handoff.Phase == "preparedGap"
            ? status == "no_task"
            : status == "suspended"
              && Guid.TryParse(String(data, "workflowRunId"), out var wire)
              && Guid.TryParse(run.WireRunId, out var expectedWire) && wire == expectedWire
              && String(data, "nodeId") == handoff.NodeId
              && Guid.TryParse(String(data, "originalJobId"), out var original)
              && Guid.TryParse(handoff.OriginalJobId, out var expectedOriginal) && original == expectedOriginal
              && Int(data, "occurrence") == handoff.Occurrence
              && Int(data, "iteration") == handoff.LoopIteration
              && Int(data, "attempt") == handoff.Attempt;
        if (!matched) return false;
        return _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.OnlineHoeingHandoff is not { } current || current.Generation != generation
                || current.Phase != handoff.Phase || latest.StopRequested) return false;
            current.Ticket = ticket;
            current.Phase = status == "no_task" ? "gap"
                : current.Phase == "parked" ? "parked" : "suspended";
            current.CheckpointPhase = String(data, "planCheckpointPhase");
            current.NextTaskIndex = Int(data, "planNextTaskIndex");
            return true;
        }, out _);
    }

    internal string? OnlineHoeingManagedRun(int generation)
    {
        lock (_gate) return _onlineHoeingGeneration == generation ? _onlineHoeingRunId : null;
    }

    internal async Task OpenOnlineHoeingSendAsync(string? skipAutoRunId = null)
    {
        await _onlineSendGate.OpenAsync().ConfigureAwait(false);
        foreach (var parked in _runs.List().Where(r => r.OnlineHoeingGateParked
                     && r.OnlineHoeingHandoff is null && r.State == WorkflowRunState.LocalWaitParking
                     && r.RunId != skipAutoRunId))
        {
            for (var i = 0; i < 20 && IsDriving(parked.WorkflowId); i++)
                await Task.Delay(100).ConfigureAwait(false);
            if (IsDriving(parked.WorkflowId)) continue;
            if (_runs.UpdateMergingIf(parked.RunId, latest =>
                {
                    if (latest.State != WorkflowRunState.LocalWaitParking || !latest.OnlineHoeingGateParked
                        || latest.OnlineHoeingHandoff is not null || latest.StopRequested) return false;
                    latest.OnlineHoeingGateParked = false;
                    return true;
                }, out _))
            {
                var resumed = await ResumeRunAsync(parked.RunId).ConfigureAwait(false);
                if (!resumed.Ok) _log?.Invoke("[任务中心] 锄地后等待计划未能自动接续：" + resumed.Message);
            }
        }
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
        var activeRunIds = _runs.List()
            .Where(r => ActiveStates.Contains(r.State))
            .Select(r => r.RunId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var runId in activeRunIds)
        {
            try
            {
                await RequestRunActionAsync(runId, WorkflowRunAction.Stop)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke("[任务中心] 全局复位登记 Stop 未完成（将由 BGI 重建兜底）："
                    + runId + " " + ex.Message);
            }
        }

        var abandoned = 0;
        foreach (var run in _runs.List())
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
                signal.TrySetCanceled();
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

            if (driversSettled)
            {
                await _onlineSendGate.OpenAsync().ConfigureAwait(false);
                sendGateOpened = true;
            }
            else
                _log?.Invoke("[任务中心] 全局复位后仍有运行驱动未退出，发送闸门保持关闭");
        }
        NotifyStateChanged();
        return (abandoned, sendGateOpened);
    }

    internal async Task<bool> FinishOnlineGapAfterReleaseAsync(string runId, string ticket)
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
        await OpenOnlineHoeingSendAsync(skipAutoRunId: runId).ConfigureAwait(false);
        var afterOpen = _runs.Load(runId);
        if (afterOpen is null || afterOpen.StopRequested) return false;
        if (!IsDriving(afterOpen.WorkflowId) && afterOpen.State == WorkflowRunState.LocalWaitParking)
        {
            var resumed = await ResumeRunAsync(runId).ConfigureAwait(false);
            if (!resumed.Ok)
            {
                MarkReleasedGapUnknown(runId, resumed.Message);
                return false;
            }
            return true;
        }
        var continued = IsDriving(afterOpen.WorkflowId) || afterOpen.State is WorkflowRunState.Running
            or WorkflowRunState.Waiting or WorkflowRunState.LocalWaitParking or WorkflowRunState.Succeeded;
        if (!continued) MarkReleasedGapUnknown(runId, "计划状态未进入可接续执行");
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

    internal bool FinalizeStoppedOnlineHandoff(string runId)
        => _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.State != WorkflowRunState.Cancelled || !latest.StopRequested
                || latest.OnlineHoeingHandoff is not { ResumeJobId: null } handoff) return false;
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
        for (var i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ready = _runs.Load(runId);
            if (ready?.State == WorkflowRunState.OnlineHoeingParking
                && ready.OnlineHoeingHandoff is { Phase: "parked", Ticket: { } savedTicket }
                && savedTicket == ticket) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        var run = _runs.Load(runId);
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
            _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(parsedJob.ToString("D"));
        return true;
    }

    internal void FailPlanResume(string runId, string reason)
    {
        var marked = _runs.UpdateMergingIf(runId, latest =>
        {
            if (latest.OnlineHoeingHandoff is not { } handoff || latest.IsTerminal) return false;
            handoff.Phase = "unknown";
            latest.State = WorkflowRunState.Unknown;
            latest.Note = (latest.Note is null ? "" : latest.Note + " | ") + reason;
            return true;
        }, out _);
        if (marked)
        {
            DriveEntry? drive;
            lock (_gate) _drives.TryGetValue(_runs.Load(runId)?.WorkflowId ?? "", out drive);
            if (drive?.Runner.HasActiveControl(runId) == true)
                drive.Runner.RequestAction(runId, WorkflowRunAction.Stop);
        }
        _onlineResumeSignals.GetOrAdd(runId,
            _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetException(new InvalidOperationException(reason));
        NotifyStateChanged();
    }

    internal async Task<BoundaryTerminalResult?> AwaitOnlineHoeingContinuationAsync(
        BgiWorkflowExecutionBoundary boundary, WorkflowRunRecord run, WorkflowSubmission original,
        BoundaryTerminalResult originalTerminal, CancellationToken ct)
    {
        // 接管登记在 Runner 等待 BGI 终态期间由宿主落盘；Runner 的旧内存副本不含该标记。
        var latestBeforePark = _runs.Load(run.RunId);
        var handoff = latestBeforePark?.OnlineHoeingHandoff;
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
        string resumedJobId;
        try
        {
            resumedJobId = await _onlineResumeSignals.GetOrAdd(run.RunId,
                _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return BoundaryTerminalResult.UncertainWith("恢复未确认：" + ex.Message); }

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
}
