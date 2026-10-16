using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Helpers;
using System.IO;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private void ObserveOnlineTask(Task task, string operation)
    {
        _ = task.ContinueWith(completed =>
        {
            try
            {
                var error = completed.Exception;
                RuntimeLog.WriteLine($"[联机任务异常] {operation}: {error}");
                AddLog($"[联机任务异常] {operation}未确认：{error?.GetBaseException().Message}");
            }
            catch { /* 最后一道观察边界不得再次产生无人观察的任务异常。 */ }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ConfirmAllReadyAndStartAsync(int generation)
    {
        await OnAllReadyConfirmedInternal(generation);
    }

    /// <summary>[P1b] 策略收尾恰好一次守卫：已完成收尾的最高批次 generation（单调不减，代序号比对）。
    /// 新一轮 AllReady 不重置——重置会让旧轮次的 10s 恢复定时器在新一轮里重跑收尾。</summary>
    private int _teardownDoneGeneration;
    private readonly HashSet<string> _settledHoeingBatchIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _coordinatedTeardownTasks = new(StringComparer.Ordinal);
    private string? _lastHoeingBatchId;
    private string? _lastHoeingStartPhase;
    private string? _lastHoeingStartMessage;

    /// <summary>互斥锁：防止两轮 AllReady 并发执行 OnAllReadyConfirmedInternal（patterns §31）。</summary>
    private int _isAllReadyProcessing;

    /// <summary>
    /// [P1b] 策略收尾恰好一次守卫：批次正常末尾 / 批次内 F11 取消分支 / 10s 恢复定时器三个入口互斥，
    /// 同一轮上线锄地（按批次 generation 代序号比对）只执行一次——恢复定时器由 autoHoeingRunning
    /// 边沿触发，与批次末尾收尾可能同时命中，RunSpecified 策略双执行会重复启动指定任务（实机事故）。
    /// 守卫单调不减、新一轮 AllReady 不重置（重置会让旧轮次迟到的恢复定时器在新一轮里重跑收尾）；
    /// generation ≤ 0 的异常批次不进门，保持原行为直接执行。
    /// </summary>
    private async Task ApplyPolicyTeardownOnceAsync(OnlineHoeingBatch batch, string executedDesc, bool userCancelled, string? expectedTicket = null)
    {
        // Per-item falling edges must not consume the whole-batch once guard.
        if (batch.Start != null && !batch.LocalSucceeded && !batch.CoordinatedSucceeded && !userCancelled) return;
        if (batch.Start == null)
        {
            await ApplyPolicyTeardownCoreAsync(batch, executedDesc, userCancelled, expectedTicket);
            return;
        }
        lock (_batchGate) { if (!ReferenceEquals(_activeBatch, batch)) return; }
        TaskCompletionSource completion;
        var leader = false;
        lock (_teardownGate)
        {
            if (!_coordinatedTeardownTasks.TryGetValue(batch.BatchId, out completion!))
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _coordinatedTeardownTasks[batch.BatchId] = completion;
                leader = true;
                if (_coordinatedTeardownTasks.Count > 256)
                {
                    var retired = _coordinatedTeardownTasks.FirstOrDefault(p => p.Key != batch.BatchId && p.Value.Task.IsCompleted);
                    if (retired.Key != null) _coordinatedTeardownTasks.Remove(retired.Key);
                }
            }
        }
        if (leader)
        {
            try
            {
                await ApplyPolicyTeardownCoreAsync(batch, executedDesc, userCancelled, expectedTicket ?? batch.TakeoverTicket);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        await completion.Task; // 其他收尾触发者必须等同一操作真正完成，不能仅见“已进入”就认定完成。
    }

    private async Task ApplyPolicyTeardownCoreAsync(OnlineHoeingBatch batch, string executedDesc, bool userCancelled, string? expectedTicket = null)
    {
        if (batch.Start is not null)
        {
            await ApplyCoordinatedPolicyTeardownAsync(batch, executedDesc, expectedTicket);
            return;
        }
        // Online batches are finalized only by the all-member authority, never by a per-task falling edge.
        if (!batch.CoordinatedSucceeded && !userCancelled) return;
        lock (_teardownGate)
        {
            if (batch.Start is null)
            {
                if (batch.Generation > 0 && _teardownDoneGeneration >= batch.Generation) return;
                if (batch.Generation > _teardownDoneGeneration) _teardownDoneGeneration = batch.Generation;
            }
        }
        if (_commandExecutor != null)
        {
            TaskCenterHost? host;
            lock (_taskCenterHostGate) host = _taskCenterHost;
            var managedRunId = host?.OnlineHoeingManagedRun(batch.Generation);
            var policy = batch.Policy ?? SnapshotOnlineHoeingPolicy();
            async Task ApplyPolicyAsync()
            {
                var result = await _commandExecutor.ApplyPolicyTeardownAsync(policy, executedDesc,
                    userCancelled, AddLog, expectedTicket,
                    specifiedStartAdmission: batch.Start != null && host != null ? host.AdmitExternalStartAsync : null,
                    beforeSpecifiedStart: batch.Start == null ? null : async () =>
                    {
                        if (host?.CanOpenOnlineSendAfterBatch != true)
                            throw new InvalidOperationException("原计划接管责任未结清，未启动完成后指定任务");
                        await host.OpenOnlineHoeingSendAsync();
                    });
                if (result.Status != "success")
                    throw new InvalidOperationException("锄地收尾未完成：" + result.Message);
            }
            if (host is not null && managedRunId is not null)
            {
                var planAlreadyStopped = host.Runs.Load(managedRunId)?.StopRequested == true;
                var originalTicket = host.Runs.Load(managedRunId)?.OnlineHoeingHandoff?.Ticket;
                if (userCancelled || planAlreadyStopped || policy.Policy != TaskConflictPolicy.Resume)
                {
                    if (!planAlreadyStopped)
                    {
                        var stop = await host.RequestRunActionAsync(managedRunId, WorkflowRunAction.Stop);
                        if (!stop.Ok)
                        {
                            host.FailPlanResume(managedRunId, "计划表停止登记失败：" + stop.Message);
                            throw new InvalidOperationException("计划表停止未确认：" + stop.Message);
                        }
                    }
                    var stopped = false;
                    for (var check = 0; check < 50; check++)
                    {
                        var state = host.Runs.Load(managedRunId);
                        if (state?.State == WorkflowRunState.Cancelled && state.StopRequested
                            && state.OnlineHoeingHandoff?.ResumeJobId is null)
                        {
                            stopped = true;
                            break;
                        }
                        await Task.Delay(100);
                    }
                    if (!stopped)
                    {
                        host.FailPlanResume(managedRunId, "计划表停止终态未确认，不启动完成后指定任务");
                        throw new InvalidOperationException("计划表停止终态未确认，保留恢复现场待核对");
                    }
                    if (!host.FinalizeStoppedOnlineHandoff(managedRunId))
                    {
                        throw new InvalidOperationException("已停止但接管责任未能结清，保留恢复现场待核对");
                    }
                    if (planAlreadyStopped && !userCancelled && policy.Policy == TaskConflictPolicy.Resume)
                    {
                        var cleared = await _commandExecutor.ExecuteResumeAsync(cancel: true,
                            expectedTicket: expectedTicket ?? originalTicket);
                        if (cleared.Status != "success")
                            throw new InvalidOperationException("手动停止后的接管票据未确认释放：" + cleared.Message);
                    }
                    else
                        await ApplyPolicyAsync();
                }
                else
                {
                    var ticket = host.Runs.Load(managedRunId)?.OnlineHoeingHandoff?.Ticket;
                    if (string.IsNullOrWhiteSpace(ticket))
                    {
                        host.FailPlanResume(managedRunId, "计划恢复缺少已确认的联机接管票据");
                    }
                    else if (host.Runs.Load(managedRunId)?.OnlineHoeingHandoff?.Phase == "gap")
                    {
                        var release = await _commandExecutor.ExecuteResumeAsync(cancel: true, expectedTicket: ticket);
                        if (release.Status != "success"
                            || !await host.FinishOnlineGapAfterReleaseAsync(managedRunId, ticket))
                            host.FailPlanResume(managedRunId, "任务间隙的执行权释放或计划后继恢复未确认");
                        else AddLog("[任务中心] 锄地前处于任务间隙，已按原计划位置继续后续任务");
                    }
                    else
                    {
                        var continuation = await host.PreparePlanResumeAsync(managedRunId, ticket);
                        if (continuation is null)
                            host.FailPlanResume(managedRunId, "原节点让位终态未确认，不能启动计划续跑作业");
                        else
                        {
                            var resumed = await _commandExecutor.ExecutePlanResumeAsync(ticket, continuation, AddLog);
                            if (resumed.Status != "success" || string.IsNullOrWhiteSpace(resumed.JobId)
                                || !host.AcceptPlanResumeJob(managedRunId, ticket, resumed.JobId))
                            {
                                if (resumed.Status == "success" && resumed.JobId is { } unexpectedJob
                                    && _externalClient is { } ext)
                                {
                                    try
                                    {
                                        using var cancelBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                        await ext.CancelOwnedTaskAsync(unexpectedJob, cancelBudget.Token);
                                    }
                                    catch (Exception ex) { AddLog("[任务中心] 迟到恢复作业取消未确认：" + ex.Message); }
                                }
                                host.FailPlanResume(managedRunId, "BGI 恢复作业身份或受理回执未确认：" + resumed.Message);
                            }
                            else AddLog("[任务中心] 已受理原节点恢复；确认其完成后才会继续计划表后续任务");
                        }
                    }
                }
            }
            else
                await ApplyPolicyAsync();
            if (batch.Start != null && host?.CanOpenOnlineSendAfterBatch == false)
                throw new InvalidOperationException("原计划接管或恢复责任仍未确认");
            // 记录实际执行时间：10s 恢复定时器的无批次键直接路径据此跳过 30s 内的迟到边沿
            lock (_teardownGate) { _lastTeardownUtc = DateTime.UtcNow; }
        }
    }

    private async Task ApplyCoordinatedPolicyTeardownAsync(OnlineHoeingBatch batch, string description, string? ticket)
    {
        var executor = _commandExecutor ?? throw new InvalidOperationException("本批收尾执行器不可用");
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        var runId = host?.OnlineHoeingManagedRun(batch.Generation);
        var token = batch.Cts.Token;
        var policy = batch.Policy ?? SnapshotOnlineHoeingPolicy();
        batch.ConfigurePolicyCancellation(executor.CancelHoeingPolicyExecutionAsync);
        batch.ConfigurePolicyCleanup(async () =>
        {
            await FinishCancelledCoordinatedPolicyAsync(batch, executor, host, runId);
            batch.LocalCleanupConfirmed = true;
            if (batch.RunTask is { } running)
                ObserveOnlineTask(running.ContinueWith(_ => RetryCoordinatedCleanupAsync(batch),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap(),
                    "停止后的批次清理回执");
            return true;
        });
        async Task Accepted(CommandResult result, bool registered)
        {
            if (result.JobId is null || result.TargetProcessId is not { } pid
                || !long.TryParse(result.TargetStartTicksUtc, out var ticks))
            {
                batch.PolicySendUnknown = true;
                return;
            }
            CommandExecutor.ExecutionIdentity? root = null;
            if (!registered)
            {
                var current = await executor.CaptureExecutionIdentityAsync();
                if (current is { } identity && identity.ProcessId == pid && identity.StartTicksUtc == ticks
                    && Guid.TryParse(result.JobId, out var job) && identity.JobId == job) root = identity;
            }
            batch.RecordPolicyExecution(new HoeingPolicyExecution(result.JobId,
                new BgiEpoch { ProcessId = pid, StartTicksUtc = ticks }, registered, root));
        }
        async Task OpenForSpecified()
        {
            token.ThrowIfCancellationRequested();
            if (host?.CanOpenOnlineSendAfterBatch != true)
                throw new InvalidOperationException("原计划接管责任未结清，未启动完成后指定任务");
            await host.OpenOnlineHoeingSendAsync();
            token.ThrowIfCancellationRequested();
        }
        try
        {
        CommandResult? result = null;
        if (runId is null)
        {
            result = await executor.ApplyPolicyTeardownAsync(policy, description, batch.IsCancellationRequested,
                AddLog, ticket, host is null ? null : host.AdmitExternalStartAsync,
                host is null ? null : OpenForSpecified, token, Accepted);
            if (result.Status == "success") batch.PolicyTicketReleased = true;
        }
        else if (batch.IsCancellationRequested || policy.Policy != TaskConflictPolicy.Resume)
        {
            var release = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: ticket);
            if (release.Status != "success") throw new InvalidOperationException("原上下文释放未确认：" + release.Message);
            batch.PolicyTicketReleased = true;
            if (!await host!.SettleFailedOnlineHoeingAsync(batch.Generation, runId, true, "本批结束后停止原计划"))
                throw new InvalidOperationException("原计划停止责任尚未结清");
            if (!batch.IsCancellationRequested && policy.Policy == TaskConflictPolicy.RunSpecified)
                result = await executor.StartSpecifiedAfterReleasedAsync(policy, host.AdmitExternalStartAsync,
                    OpenForSpecified, token, Accepted);
        }
        else
        {
            var handoff = host!.Runs.Load(runId)?.OnlineHoeingHandoff;
            if (handoff?.Ticket is not { } planTicket)
                throw new InvalidOperationException("原计划恢复票据缺失");
            if (handoff.Phase == "gap")
            {
                var release = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: planTicket);
                if (release.Status != "success") throw new InvalidOperationException("原计划空隙执行权释放未确认");
                batch.PolicyTicketReleased = true;
                token.ThrowIfCancellationRequested();
                if (!await host.FinishOnlineGapAfterReleaseAsync(runId, planTicket))
                    throw new InvalidOperationException("原计划空隙接续未确认");
            }
            else
            {
                var continuation = await host.PreparePlanResumeAsync(runId, planTicket, token);
                token.ThrowIfCancellationRequested();
                if (continuation is null) throw new InvalidOperationException("原计划让位终态未确认");
                result = await executor.ExecutePlanResumeAsync(planTicket, continuation, AddLog, token);
                if (result.Status == "success" && result.JobId is not null)
                {
                    batch.PolicyTicketReleased = true;
                    await Accepted(result, true);
                    if (!batch.IsCancellationRequested && !host.AcceptPlanResumeJob(runId, planTicket, result.JobId))
                        throw new InvalidOperationException("恢复作业已受理但原计划关联未确认");
                }
            }
        }
        if (result is { Status: "failed", JobId: null, ErrorCode: null or "result_unknown" }
            && batch.PolicyExecution is null) batch.PolicySendUnknown = true;
        if (batch.IsCancellationRequested)
            return;
        if (result is { Status: not "success" }) throw new InvalidOperationException("本批收尾未完成：" + result.Message);
        if (host?.CanOpenOnlineSendAfterBatch == false) throw new InvalidOperationException("原计划接管责任尚未确认");
        }
        finally { batch.MarkPolicyPhaseFinished(); }
    }

    private async Task FinishCancelledCoordinatedPolicyAsync(OnlineHoeingBatch batch, CommandExecutor executor,
        TaskCenterHost? host, string? runId)
    {
        var physical = await batch.WaitPolicyCancellationAsync() && !batch.PolicySendUnknown;
        if (executor.TakeoverTicketSnapshot is { } held)
        {
            if (held != batch.TakeoverTicket) physical = false;
            else
            {
                var release = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: held);
                batch.PolicyTicketReleased = release.Status == "success";
                physical &= batch.PolicyTicketReleased;
            }
        }
        else physical &= batch.PolicyTicketReleased || batch.CleanupConfirmed || !batch.TakenOver;
        if (runId is not null)
        {
            if (physical)
                physical = await host!.SettleCancelledOnlinePolicyAsync(batch.Generation, runId);
            else host!.FailPlanResume(runId, "本批停止后的后继任务或票据退出未确认", batch.Generation);
        }
        if (!physical) throw new InvalidOperationException("本批停止后的精确作业或接管责任尚未清理");
        batch.Outcome = "cancelled";
        batch.CleanupConfirmed = true;
    }

    private Task OnAllReadyConfirmedInternal(int generation)
    {
        NotifyBatchLoud("联机锄地未启动", "服务端仍使用旧开锄协议，请更新配套服务端；当前任务保持执行。");
        return Task.CompletedTask;
    }

    private sealed class HoeingStartPreparation
    {
        public CommandExecutor? Executor;
        public TaskCenterHost? Host;
        public bool SendGateClosed;
        public OnlineHoeingPreparation? Plan;
    }

    internal TaskConflictPolicySettings SnapshotBatchCompletionPolicy(CoordinatedBatchSnapshot start)
        => start.Source == "manual" && !start.UseCompletionActions
            ? new TaskConflictPolicySettings() : SnapshotOnlineHoeingPolicy();

    private async Task OnCoordinatedBatchStartAsync(CoordinatedBatchSnapshot start)
    {
        var uid = _config?.PlayerUid;
        if (string.IsNullOrWhiteSpace(uid) || !start.ParticipantUids.Contains(uid, StringComparer.Ordinal)) return;
        if (!IsExecutorMode || start.Protocol != "hoeing.start.v2") return;
        OnlineHoeingBatch? batch = null;
        string? rejected = null;
        var recoveryPending = false;
        lock (_batchGate)
        {
            if (start.Generation <= _resetFenceGeneration
                || _settledHoeingBatchIds.Contains(start.BatchId) || start.CleanupComplete) return;
            if (_activeBatch is { IsAlive: true } active)
            {
                if (active.BatchId == start.BatchId) return;
                rejected = "本机已有联机批次正在准备、执行或清理，未中断现有任务";
            }
            else if (_localResetGate.CurrentCount == 0)
                rejected = "本机状态重置尚未完成，未开始另一批次";
            else if (_ordinaryStopInProgress > 0)
                rejected = "本机停止请求尚未完成，未开始另一批次";
            else if (start.Phase != "registering" || start.Members.Any(m => m.Uid == uid && m.Stage != "pending"))
            {
                // 重启后没有原成员token，不能把旧作业冒认为新执行；显式重置按resetId结清。
                recoveryPending = true;
            }
            else
            {
                try
                {
                    var candidate = new OnlineHoeingBatch(start, uid,
                        SnapshotBatchCompletionPolicy(start));
                    batch = candidate;
                    _activeBatch = batch; // 从这里起Preparing也是busy，而不是等待RunTask出现。
                }
                catch (Exception ex) { rejected = ex.Message; }
            }
        }
        if (recoveryPending)
        {
            NotifyBatchLoud("联机批次待核对", "发现旧批次仍未清理，保留当前任务；请使用状态重置完成同批次核对。");
            return;
        }
        if (rejected != null)
        {
            await RejectCoordinatedStartAsync(start, uid, rejected);
            return;
        }
        if (batch == null) return;
        var expectedIntent = start.IntentGenerations.TryGetValue(uid, out var ownIntent) ? (int?)ownIntent : null;
        if (batch.IsCancellationRequested || !_intentLifecycle.TryBeginCoordinatedBatch(start.BatchId,
                start.Generation, start.Source == "manual", expectedIntent))
        {
            await RejectCoordinatedStartAsync(start, uid, "本机上线意图已改变或已取消，本次开始未接受");
            batch.MarkSettled();
            lock (_batchGate) { if (ReferenceEquals(_activeBatch, batch)) _activeBatch = null; }
            return;
        }
        UpdateHoeingBatchStatus(batch, "preparing", "正在准备联机锄地");
        batch.RunTask = RunAcceptedHoeingBatchAsync(batch);
        ObserveOnlineTask(batch.RunTask, "统一联机批次");
    }

    private void OnCoordinatedBatchState(CoordinatedBatchSnapshot state)
    {
        OnlineHoeingBatch? batch;
        lock (_batchGate) batch = _activeBatch;
        if (batch?.BatchId != state.BatchId) return;
        if (batch.RunTask?.IsCompleted == true && _localResetGate.CurrentCount > 0)
        {
            ObserveOnlineTask(RetryCoordinatedCleanupAsync(batch), "联机清理回执补报");
            return;
        }
        if (state.Phase == "aborted" && batch.PreserveLocalSuccess(state.Reason))
        {
            batch.TeamFailureReason = state.Reason;
            UpdateHoeingBatchStatus(batch, batch.LocalExecutionReleased ? "localCompleted" : "cleaning",
                batch.LocalExecutionReleased ? "本端已完成；队友异常不影响本机当前任务" : "本端已完成，正在结清本机收尾；队友异常不撤销本端结果");
            return;
        }
        if (state.Phase == "aborted")
        {
            batch.FailureReason = state.Reason;
            batch.Outcome = state.Reason.Contains("cancel", StringComparison.Ordinal) || state.Reason is "manual_reset" or "user_stop_all"
                ? "cancelled" : state.Reason.Contains("unknown", StringComparison.Ordinal) ? "unknown" : "failed";
            UpdateHoeingBatchStatus(batch, "stopping", "全队批次已中止：" + state.Reason);
            batch.Cancel(userInitiated: false);
        }
    }

    private async Task RejectCoordinatedStartAsync(CoordinatedBatchSnapshot start, string uid, string reason)
    {
        AddLog("[联机开始] 未接受：" + reason);
        if (_signalRClient == null || !start.Bindings.TryGetValue(uid, out var items)) return;
        var token = Guid.NewGuid().ToString("N");
        var layout = string.Join(",", items.Select(i => i.Type));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var state = await _signalRClient.UpdateCoordinatedBatchAsync(start.Generation, token, start.BatchId,
                layout, 0, 0, "preparation_failed", timeout.Token, "registered", reason);
            if (state.Phase == "aborted")
                await _signalRClient.UpdateCoordinatedBatchAsync(start.Generation, token, start.BatchId,
                    layout, state.Index, state.Attempt, "", timeout.Token, "cleaned");
        }
        catch (Exception ex) { AddLog("[联机开始] 拒绝回执未送达：" + ex.Message); }
    }

    private void UpdateHoeingBatchStatus(OnlineHoeingBatch batch, string phase, string message)
    {
        if (batch.Phase == phase && batch.Message == message) return;
        batch.Phase = phase;
        batch.Message = message;
        AddLog($"[联机开始] {batch.BatchId} {message}");
        ObserveOnlineTask(ReportStatusAsync(), "联机批次状态上报");
    }

    private async Task RunAcceptedHoeingBatchAsync(OnlineHoeingBatch batch)
    {
        var preparation = new HoeingStartPreparation();
        try
        {
            await RunCoordinatedBatchAsync(batch,
                ct => PrepareCoordinatedStartAsync(batch, preparation, ct),
                ct => TakeOverForCoordinatedStartAsync(batch, preparation, ct));
            if (batch.LocalSucceeded && !batch.IsCancellationRequested)
            {
                batch.Outcome = "succeeded";
                batch.CleanupConfirmed |= batch.LocalExecutionReleased;
                batch.Message = batch.CompletionActionWarning is { } warning
                    ? "本端联机锄地已完成；" + warning
                    : batch.LocalExecutionReleased
                    ? batch.CoordinatedSucceeded ? "联机锄地已完整完成" : "本端联机锄地已完成，队友结果单独记录"
                    : "本端联机锄地已完成，本机收尾仍待确认：" + batch.FailureReason;
            }
            else
            {
                if (preparation.Plan?.ManagedRunId is { } failedRun)
                    batch.CleanupConfirmed = await preparation.Host!.SettleFailedOnlineHoeingAsync(
                        batch.Generation, failedRun, batch.CleanupConfirmed,
                        "联机批次未完整完成：" + batch.FailureReason);
                batch.Message = batch.Outcome switch
                {
                    "cancelled" => "联机锄地已取消，正在确认清理",
                    "failed" => "联机锄地失败：" + batch.FailureReason,
                    _ => "联机锄地结果未确认：" + batch.FailureReason,
                };
            }
        }
        catch (Exception ex)
        {
            batch.CleanupConfirmed = batch.LocalExecutionReleased;
            batch.Outcome = batch.IsCancellationRequested ? "cancelled" : batch.LocalSucceeded ? "succeeded" : "failed";
            batch.FailureReason ??= ex.Message;
            if (batch.IsCancellationRequested && batch.HasPolicyCleanup)
                batch.CleanupConfirmed = await batch.WaitCancelledPolicyCleanupAsync();
            else if (!batch.LocalSucceeded && preparation.Plan?.ManagedRunId is { } failedRun)
                preparation.Host!.FailPlanResume(failedRun, "全队锄地未完整完成：" + ex.Message);
            batch.Message = batch.LocalSucceeded && !batch.IsCancellationRequested
                ? "本端锄地已完成，收尾仍待确认：" + ex.Message
                : batch.IsCancellationRequested ? "联机锄地已中止：" + ex.Message : "联机锄地失败：" + ex.Message;
            AddLog("[联机开始] " + batch.Message);
        }
        finally
        {
            if (batch.IsCancellationRequested && batch.HasPolicyCleanup)
                batch.CleanupConfirmed = await batch.WaitCancelledPolicyCleanupAsync();
            if (!batch.LocalExecutionReleased && preparation.SendGateClosed && preparation.Host?.CanOpenOnlineSendAfterBatch != true)
                batch.CleanupConfirmed = false;
            if (!batch.LocalExecutionReleased && preparation.SendGateClosed && preparation.Host?.CanOpenOnlineSendAfterBatch == true)
            {
                try { await preparation.Host.OpenOnlineHoeingSendAsync(); }
                catch (Exception ex) { batch.CleanupConfirmed = false; AddLog("[联机开始] 原计划发送闸门尚未恢复：" + ex.Message); }
            }
            batch.LocalCleanupConfirmed |= batch.CleanupConfirmed;
            if (batch.LocalCleanupConfirmed && _localResetGate.CurrentCount > 0)
                await RetryCoordinatedCleanupAsync(batch);
            else UpdateHoeingBatchStatus(batch, "cleanupPending", batch.LocalSucceeded
                ? "本端锄地已完成，旧收尾待核对；不会重跑锄地或完成动作"
                : "联机批次清理尚未确认，请核对状态；不会启动另一批次");
            ObserveOnlineTask(ReportStatusAsync(), "联机批次终态上报");
        }
    }

    // A waiting peer or an undelivered cleaned ACK is not ownership of this endpoint's execution slot.
    internal bool HasLocalCoordinatedExecutionResponsibility
    {
        get
        {
            lock (_batchGate) return _activeBatch is
                { IsAlive: true, LocalExecutionReleased: false, LocalCleanupConfirmed: false };
        }
    }

    private bool HasReleasedLocalCoordinatedBatch
    {
        get
        {
            lock (_batchGate) return _activeBatch is { IsAlive: true } batch
                && (batch.LocalExecutionReleased || batch.LocalCleanupConfirmed);
        }
    }

    private async Task<bool> AbandonExitedCoordinatedBatchOwnershipAsync(OnlineHoeingBatch batch,
        CommandExecutor executor, string? ticket, BgiEpoch epoch, CancellationToken ct)
    {
        if (!await executor.AbandonExitedCoordinatedOwnershipAsync(ticket, epoch, ct)) return false;
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        if (host is not null)
        {
            await host.RecoverOnlineHandoffsBeforeStartAsync();
            if (!host.CanOpenOnlineSendAfterBatch) return false;
            await host.OpenOnlineHoeingSendAsync();
        }
        batch.PolicyTicketReleased = true;
        return true;
    }

    private async Task FinalizeLocalCoordinatedBatchAsync(OnlineHoeingBatch batch, CommandExecutor executor,
        BgiEpoch epoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_batchGate)
            if (!ReferenceEquals(_activeBatch, batch) || !ReferenceEquals(_commandExecutor, executor)
                || batch.Generation <= _resetFenceGeneration || _localResetGate.CurrentCount == 0)
                throw new OperationCanceledException("本批收尾权限已被本机停止或重置撤销");
        if (!batch.LocalSucceeded) throw new InvalidOperationException("本机整个最后配置组尚未确认成功");
        if (CommandExecutor.ProbeOriginalBgiProcess(epoch) == false)
        {
            if (!await AbandonExitedCoordinatedBatchOwnershipAsync(batch, executor, batch.TakeoverTicket, epoch, ct))
                throw new InvalidOperationException("原 BGI 已退出，旧接管责任仍待确认");
            if (batch.Policy?.Policy is TaskConflictPolicy.Resume or TaskConflictPolicy.RunSpecified)
                batch.CompletionActionWarning = "原 BGI 已退出，未执行绑定完成动作；未向新进程重放";
        }
        else
        {
            var boundary = await executor.CaptureExecutionBoundarySnapshotAsync();
            if (!boundary.Known || boundary.ProcessId != epoch.ProcessId || boundary.StartTicksUtc != epoch.StartTicksUtc)
                throw new IOException("本机收尾的原 BGI 身份暂未确认；未向后继进程重放完成动作");
            if (boundary.SlotOccupied)
                throw new InvalidOperationException("本机已有新的执行，旧批次只补清理，不抢占当前任务");
            ct.ThrowIfCancellationRequested();
            await ApplyPolicyTeardownOnceAsync(batch, "联机锄地", false, batch.TakeoverTicket);
            ct.ThrowIfCancellationRequested();
            TaskCenterHost? host;
            lock (_taskCenterHostGate) host = _taskCenterHost;
            if (host is not null)
            {
                if (!host.CanOpenOnlineSendAfterBatch)
                    throw new InvalidOperationException("本机原计划接管责任仍待结清");
                await host.OpenOnlineHoeingSendAsync();
            }
        }
        ct.ThrowIfCancellationRequested();
        batch.CleanupConfirmed = true;
        batch.LocalCleanupConfirmed = true;
        batch.MarkLocalExecutionReleased();
        UpdateHoeingBatchStatus(batch, "localCompleted", batch.CompletionActionWarning is { } warning
            ? "本端锄地已完成并释放，可手动执行；" + warning
            : "本端任务和收尾已完成，可手动执行；等待队友结束");
    }

    // Recovery may release an old responsibility, but never reruns Resume/RunSpecified policy.
    private async Task<bool> ReconcileLocalCoordinatedCleanupAsync(OnlineHoeingBatch batch, CancellationToken ct)
    {
        if (batch.LocalExecutionReleased || batch.LocalCleanupConfirmed) return true;
        if (batch.RunTask?.IsCompleted != true || batch.LocalFinalizationTask?.IsCompleted == false) return false;
        var executor = _commandExecutor;
        var epoch = batch.ExecutionEpoch;
        if (executor is null || epoch is null) return false;
        var oldProcessExited = CommandExecutor.ProbeOriginalBgiProcess(epoch) == false;
        if (oldProcessExited)
        {
            if (!await AbandonExitedCoordinatedBatchOwnershipAsync(batch, executor, batch.TakeoverTicket, epoch, ct)) return false;
            if (batch.Policy?.Policy is TaskConflictPolicy.Resume or TaskConflictPolicy.RunSpecified)
                batch.CompletionActionWarning ??= "原 BGI 已退出，绑定完成动作结果未确认；未向新进程重放";
        }
        else
        {
            if (!batch.LocalSucceeded || batch.PolicySendUnknown) return false;
            if (batch.PolicyExecution is { } policyExecution)
            {
                // A known policy successor is observed by its own identity; cleanup never stops it.
                if (!policyExecution.RegisteredJob || _externalClient is not { State: BgiExternalLinkState.Ready } ext
                    || !CoordinatedBatchLoop.SameEpoch(policyExecution.Epoch, ext.ServerEpoch)) return false;
                var (_, job) = await ext.QueryJobStatusAsync(policyExecution.JobId, ct);
                if (job is null || !Guid.TryParse(job.JobId, out var observedJob)
                    || !Guid.TryParse(policyExecution.JobId, out var expectedJob) || observedJob != expectedJob
                    || !CoordinatedBatchLoop.SameEpoch(job.Epoch, policyExecution.Epoch) || !job.ExecutionExitConfirmed
                    || !BgiJobTerminalPolling.IsTerminal(job.State)) return false;
            }
            if (batch.TakeoverTicket is { } oldTicket && executor.TakeoverTicketSnapshot == oldTicket)
            {
                var release = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: batch.TakeoverTicket).WaitAsync(ct);
                if (release.Status != "success") return false;
            }
            else if (!batch.PolicyTicketReleased
                && !await executor.ConfirmTakeoverRevokedAsync(epoch, true, ct)) return false;
            batch.PolicyTicketReleased = true;
            TaskCenterHost? host;
            lock (_taskCenterHostGate) host = _taskCenterHost;
            if (host is not null)
            {
                var runId = host.OnlineHoeingManagedRun(batch.Generation);
                if (runId is not null && !host.CanOpenOnlineSendAfterBatch
                    && !await host.SettleFailedOnlineHoeingAsync(batch.Generation, runId, true,
                        "本端已完成，完成动作未确认；仅释放旧责任，不重发完成动作")) return false;
                if (!host.CanOpenOnlineSendAfterBatch) return false;
                await host.OpenOnlineHoeingSendAsync();
            }
        }
        batch.CleanupConfirmed = true;
        batch.LocalCleanupConfirmed = true;
        if (batch.LocalSucceeded) batch.MarkLocalExecutionReleased();
        return true;
    }

    private readonly SemaphoreSlim _coordinatedCleanupAckGate = new(1, 1);

    private async Task RetryCoordinatedCleanupAsync(OnlineHoeingBatch batch)
    {
        if (_localResetGate.CurrentCount == 0) return;
        await _coordinatedCleanupAckGate.WaitAsync();
        try
        {
            if (_localResetGate.CurrentCount == 0) return;
            lock (_batchGate) { if (!ReferenceEquals(_activeBatch, batch)) return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (batch.IsCancellationRequested && batch.HasPolicyCleanup
                && !await batch.WaitCancelledPolicyCleanupAsync().WaitAsync(timeout.Token)) return;
            if (!await ReconcileLocalCoordinatedCleanupAsync(batch, timeout.Token)) return;
            var authority = _signalRClient;
            if (authority is not { IsConnected: true, SupportsCoordinatedStart: true })
                throw new IOException("控制房间暂不可用，保留旧批次清理回执");
            var state = await authority.GetCurrentCoordinatedBatchAsync(timeout.Token);
            if (!ReferenceEquals(authority, _signalRClient) || !authority.IsConnected || !authority.SupportsCoordinatedStart)
                throw new IOException("控制房间查询期间已断线或更换，保留旧批次清理回执");
            if (state?.BatchId == batch.BatchId)
            {
                if (state.Phase is not ("completed" or "aborted")) return;
                await authority.UpdateCoordinatedBatchAsync(batch.Generation, batch.Token, batch.BatchId,
                    string.Join(",", batch.Bindings.Select(i => i.Type)), state.Index, state.Attempt, "", timeout.Token, "cleaned");
            }
            // 同批清理被确认，或权威服务器明确旧批次已不存在；都不操作后继任务。
            batch.CleanupConfirmed = true;
            CompleteCoordinatedCleanup(batch);
        }
        catch (Exception ex)
        {
            if (batch.LocalExecutionReleased || batch.LocalCleanupConfirmed)
                UpdateHoeingBatchStatus(batch, "localCompleted", "本端已结束，可手动执行；服务端清理回执待补报");
            AddLog("[联机开始] 清理回执仍待确认：" + ex.Message);
        }
        finally { _coordinatedCleanupAckGate.Release(); }
    }

    private void CompleteCoordinatedCleanup(OnlineHoeingBatch batch)
    {
        lock (_batchGate)
        {
            if (!ReferenceEquals(_activeBatch, batch)
                || batch.IsCancellationRequested && batch.HasPolicyCleanup && !batch.CancelledPolicyCleanupConfirmed) return;
        }
        _intentLifecycle.FinishCoordinatedBatch(batch.BatchId, batch.Message);
        lock (_batchGate)
        {
            if (!ReferenceEquals(_activeBatch, batch)
                || batch.IsCancellationRequested && batch.HasPolicyCleanup && !batch.CancelledPolicyCleanupConfirmed) return;
            batch.MarkSettled();
            _lastHoeingBatchId = batch.BatchId;
            _lastHoeingStartPhase = batch.Outcome == "succeeded" ? "completed" : batch.Outcome;
            _lastHoeingStartMessage = batch.Outcome switch
            {
                "succeeded" => batch.CompletionActionWarning is { } warning
                    ? "本端联机锄地已完成；" + warning
                    : batch.LocalFinalizationTask?.IsFaulted == true
                    ? "本端联机锄地已完成；完成动作未确认，未重发：" + batch.FailureReason
                    : batch.CoordinatedSucceeded ? "联机锄地已完整完成" : "本端联机锄地已完成",
                "cancelled" => "联机锄地已取消，清理已确认",
                "failed" => batch.Message,
                _ => "联机锄地执行结果未确认，已完成本端清理",
            };
            _settledHoeingBatchIds.Add(batch.BatchId);
            if (_settledHoeingBatchIds.Count > 256) _settledHoeingBatchIds.Remove(_settledHoeingBatchIds.First());
            _activeBatch = null;
        }
        AddLog("[联机开始] " + batch.Message + "；本端清理已确认");
        ObserveOnlineTask(ReportStatusAsync(), "联机清理完成状态");
    }

    /// <summary>单机锄地可按原流程让位；实际联机和无法确认模式的锄地均保留。</summary>
    internal static void EnsureHoeingAllowsCoordinatedStart(CommandExecutor.ExecutionBoundarySnapshot boundary)
    {
        if (boundary.AutoHoeingMultiplayer == true)
            throw new InvalidOperationException("BGI 已在执行联机锄地，普通开始不会打断它");
        if (boundary.AutoHoeingRunning && boundary.AutoHoeingMultiplayer is null)
            throw new InvalidOperationException("BGI 正在锄地，但单机/联机模式未确认，保留当前任务");
    }

    private async Task PrepareCoordinatedStartAsync(OnlineHoeingBatch batch, HoeingStartPreparation context, CancellationToken ct)
    {
        if (_signalRClient?.SupportsLocalFinish != true)
            throw new InvalidOperationException("服务端缺少本端独立收尾能力，未中断当前任务");
        var names = batch.Bindings.Select(i => i.Name).ToArray();
        var types = batch.Bindings.Select(i => i.Type).ToArray();
        var local = OnlineHoeingBindingValidator.Validate(_config?.OnlineHoeingGroupNames, _config?.OnlineHoeingGroupTypes);
        if (!local.IsValid || !local.Names.SequenceEqual(names) || !local.Types.SequenceEqual(types))
            throw new InvalidOperationException("绑定已改变或不合法，请重新开始以使用最新绑定");
        var ready = await EnsureTaskCenterExecutionReadyAsync(ct);
        if (ready != null) throw new InvalidOperationException(ready);
        ct.ThrowIfCancellationRequested();
        var ext = GetBoundTaskCenterClient() ?? throw new InvalidOperationException("BGI 通道身份未确认");
        foreach (var capability in new[] { "task.queue", "hoeing.batchOutcome.v1", "hoeing.coordinatedStart.v1", "execution.targetedSuspend.v1", "config.applied", "hoeing.finishEvidence.v1" })
            if (!ext.HasCapability(capability)) throw new InvalidOperationException("BGI 缺少统一开锄能力 " + capability + "，未中断原任务");
        var cfg = await ext.SendCommandAsync("ext.config.list", null, TimeSpan.FromSeconds(10), ct);
        if (!cfg.Success || string.IsNullOrWhiteSpace(cfg.Data)) throw new InvalidOperationException("无法读取 BGI 任务资源");
        var available = ParseConfigListData(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cfg.Data));
        var validation = OnlineHoeingBindingValidator.Validate(names, types, available.ConfigGroups, available.OneClickConfigs, true);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join("；", validation.Errors));
        foreach (var binding in batch.Bindings)
        {
            ct.ThrowIfCancellationRequested();
            var dragon = binding.Type == "onedragon";
            var described = await ext.SendCommandAsync("ext.config.describe",
                new { groupName = dragon ? null : binding.Name, configName = dragon ? binding.Name : null },
                TimeSpan.FromSeconds(10), ct);
            if (!described.Success || string.IsNullOrWhiteSpace(described.Data))
                throw new InvalidOperationException("无法冻结任务配置：" + binding.Name + "；" + described.ErrorMessage);
            var document = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(described.Data);
            if (!CommandExecutor.TryParseEpoch(described.Data, out var pid, out var ticks)
                || pid != ext.ServerEpoch?.ProcessId || ticks != ext.ServerEpoch.StartTicksUtc
                || !document.TryGetProperty("configRevision", out var revision)
                || revision.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new InvalidOperationException("配置与 BGI 身份未确认：" + binding.Name);
            if (!document.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != System.Text.Json.JsonValueKind.Array
                || !tasks.EnumerateArray().Any(task => task.TryGetProperty("enabled", out var enabled)
                    && enabled.ValueKind == System.Text.Json.JsonValueKind.True))
                throw new InvalidOperationException("没有启用的任务：" + binding.Name);
            batch.FreezeConfigurationRevision(binding.Name, dragon, revision.GetString()!);
        }
        context.Executor = _commandExecutor ?? throw new InvalidOperationException("本机执行器未配置");
        context.Host = IsExecutorMode ? TaskCenterHost : null;
        if (context.Host != null) await context.Host.RecoverOnlineHandoffsBeforeStartAsync();
        ct.ThrowIfCancellationRequested();
        var boundary = await context.Executor.CaptureExecutionBoundarySnapshotAsync();
        if (!boundary.Known || boundary.SlotOccupied && boundary.Active is null)
            throw new InvalidOperationException("BGI 执行身份未确认，保留原任务");
        EnsureHoeingAllowsCoordinatedStart(boundary);
        if (context.Executor.TakeoverTicketSnapshot != null)
            throw new InvalidOperationException("原接管尚未结清，保留当前执行");
    }

    private async Task TakeOverForCoordinatedStartAsync(OnlineHoeingBatch batch, HoeingStartPreparation context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executor = context.Executor ?? throw new InvalidOperationException("执行器未准备");
        if (!ReferenceEquals(executor, _commandExecutor)) throw new InvalidOperationException("本机执行器已改变，未接管");
        if (context.Host != null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            await context.Host.CloseOnlineHoeingSendAsync(budget.Token);
            context.SendGateClosed = true;
        }
        ct.ThrowIfCancellationRequested();
        var boundary = await executor.CaptureExecutionBoundarySnapshotAsync();
        if (!boundary.Known)
            throw new InvalidOperationException("BGI 状态未知，未中断现有任务");
        EnsureHoeingAllowsCoordinatedStart(boundary);
        if (context.Host != null)
        {
            context.Plan = context.Host.PrepareOnlineHoeing(batch.Generation, boundary);
            if (!context.Plan.Ready) throw new InvalidOperationException(context.Plan.Reason ?? "原计划不能接管");
        }
        ct.ThrowIfCancellationRequested();
        CommandResult result;
        try { result = await executor.ExecuteSuspendAsync("联机批次 " + batch.BatchId, boundary); }
        finally { batch.TakeoverTicket = executor.TakeoverTicketSnapshot; }
        if (result.Status != "success") throw new InvalidOperationException(result.Message ?? "原任务接管未完成");
        ct.ThrowIfCancellationRequested();
        if (context.Plan?.ManagedRunId is not null
            && !context.Host!.ConfirmOnlineHoeingSuspend(batch.Generation, batch.TakeoverTicket, result.RawData))
            throw new InvalidOperationException("原计划与挂起回执身份不一致");
        if (!await executor.WaitTaskSlotSettledAsync("[联机开始]", AddLog, boundary.Active))
            throw new InvalidOperationException("原执行根退出尚未确认");
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>[A6] 抢占链响亮告警收口（ADR-2026-09-16）：用户可见日志 + 托盘气泡。
    /// 批次侧任一段最终失败（settle 中止 / 重试预算耗尽 / F11 冷却跳过）都必须用户可见，
    /// 不允许"每组一行执行失败然后无人知晓"。</summary>
    private void NotifyBatchLoud(string title, string message)
    {
        AddLog($"[告警] {message}");
        try
        {
            (System.Windows.Application.Current as App)?.ShowTrayBalloon(title, message);
        }
        catch
        {
            // 托盘不可用时静默（日志已保底）
        }
    }
}
