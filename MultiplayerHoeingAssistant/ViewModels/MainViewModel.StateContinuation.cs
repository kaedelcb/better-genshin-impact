using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    internal sealed class CompletionContinuationPorts
    {
        internal required Action RequireAuthority { get; init; }
        internal required Func<CancellationToken, Task<bool>> Prepare { get; init; }
        internal required Func<CancellationToken, Task<CommandResult>> Start { get; init; }
        internal required Func<CancellationToken, Task<BgiTaskQueueStatus?>> Query { get; init; }
        internal required Func<CancellationToken, Task<bool>> Reconcile { get; init; }
        internal required Func<BgiTaskQueueStatus, CancellationToken, Task<bool>> Acknowledge { get; init; }
        internal Func<CancellationToken, Task> Delay { get; init; } = ct => Task.Delay(1000, ct);
        internal Action<string>? Waiting { get; init; }
        internal bool RetainedResult { get; init; }
        internal Func<string?>? ExpectedRequestKey { get; init; }
        internal bool RegisteredJob { get; init; } = true;
    }

    private sealed class CompletionPolicyRejectedException(string message) : InvalidOperationException(message);

    // The same worker is used by the actual UI path and deterministic transport fixtures.
    internal static async Task RunCompletionContinuationAsync(OnlineHoeingBatch batch,
        CompletionContinuationPorts io, CancellationToken ct)
    {
        static bool SameJob(string? a, string? b) => Guid.TryParse(a, out var left)
            && Guid.TryParse(b, out var right) && left == right;
        string? ExpectedKey() => io.ExpectedRequestKey?.Invoke()
            ?? (io.RetainedResult ? batch.PolicyRequestKey : null);
        var noExecutionNeeded = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            io.RequireAuthority();
            try
            {
                var recordsReady = false;
                if (batch.PolicyExecution is not null || batch.PolicyTicketReleased)
                {
                    try { recordsReady = await io.Reconcile(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not ControlContextRevokedException
                        && !ct.IsCancellationRequested)
                    { io.Waiting?.Invoke("完成动作继续执行，原计划记录待补：" + ex.Message); }
                }

                if (batch.PolicyTerminalResult is { } terminal)
                {
                    if (recordsReady) batch.MarkLocalExecutionReleased();
                    // Save the actual result before acknowledging. An acknowledgement failure never restarts it.
                    if (!batch.PolicyResultAcknowledged)
                    {
                        batch.PolicyResultAcknowledged = ExpectedKey() is null
                            || await io.Acknowledge(terminal, ct).ConfigureAwait(false);
                        if (!batch.PolicyResultAcknowledged)
                            io.Waiting?.Invoke("完成动作已经结束，结果确认待补");
                    }
                    if (recordsReady && batch.PolicyResultAcknowledged) return;
                }
                else if (batch.PolicyExecution is { } known)
                {
                    var observed = await io.Query(ct).ConfigureAwait(false);
                    if (observed is not null && SameJob(observed.TaskHandle, known.JobId)
                        && CoordinatedBatchLoop.SameEpoch(observed.Epoch, known.Epoch)
                        && (ExpectedKey() is not { } expected || observed.IdempotencyKey == expected)
                        && observed.ExecutionExitConfirmed
                        && observed.Status is "completed" or "failed" or "queueCancelled")
                    {
                        batch.PolicyTerminalResult = observed;
                        batch.PolicySendUnknown = false;
                        if (observed.Status != "completed" || observed.Cancelled)
                            batch.CompletionActionWarning = "绑定完成动作"
                                + (observed.Cancelled || observed.Status == "queueCancelled" ? "已取消" : "执行失败")
                                + "：" + (observed.ErrorMessage ?? observed.ErrorCode ?? observed.Status);
                        continue;
                    }
                    io.Waiting?.Invoke("正在跟踪同一个绑定完成动作");
                }
                else if (noExecutionNeeded)
                {
                    if (recordsReady) return;
                }
                else
                {
                    if (ExpectedKey() is { } requestKey && batch.PolicyStartAttempted)
                    {
                        var original = await io.Query(ct).ConfigureAwait(false);
                        if (original is null) throw new IOException("原完成动作的队列状态暂未确认");
                        if (original.Status != "not_found")
                        {
                            if (string.IsNullOrEmpty(original.TaskHandle)
                                || original.IdempotencyKey != requestKey
                                || !CoordinatedBatchLoop.SameEpoch(original.Epoch, batch.ExecutionEpoch))
                                throw new IOException("原完成动作的编号或进程尚未确认");
                            batch.RecordPolicyExecution(new HoeingPolicyExecution(original.TaskHandle,
                                original.Epoch!, true));
                            batch.PolicySendUnknown = false;
                            continue;
                        }
                    }
                    if (!await io.Prepare(ct).ConfigureAwait(false))
                        io.Waiting?.Invoke("等待原 BGI 具备执行绑定完成动作的条件");
                    else
                    {
                        ct.ThrowIfCancellationRequested();
                        io.RequireAuthority();
                        var result = await io.Start(ct).ConfigureAwait(false);
                        // Retain a late accepted receipt before checking stop, so exact cancellation can find it.
                        if (result.JobId is { } job && result.TargetProcessId is { } pid
                            && long.TryParse(result.TargetStartTicksUtc, out var ticks))
                        {
                            var epoch = new BgiEpoch { ProcessId = pid, StartTicksUtc = ticks };
                            if (!CoordinatedBatchLoop.SameEpoch(epoch, batch.ExecutionEpoch))
                                throw new ControlContextRevokedException("完成动作回执来自其他 BGI，禁止重放", ct);
                            batch.RecordPolicyExecution(new HoeingPolicyExecution(job, epoch, io.RegisteredJob));
                            batch.PolicySendUnknown = false;
                        }
                        ct.ThrowIfCancellationRequested();
                        io.RequireAuthority();
                        if (result.ErrorCode is "manual_stop_cooldown" or "stale_epoch" or "stale_ticket"
                            or "stale_context" or "already_revoked" or "reset_superseded" or "start_cancelled"
                            or "manual_stop_fence" or "completion_not_authorized" or "takeover_conflict")
                            throw new ControlContextRevokedException("绑定完成动作的原执行权限已撤销：" + result.Message, ct);
                        if (batch.PolicyExecution is null)
                        {
                            if (result.Status == "success" && (ExpectedKey() is null || result.ErrorCode == "no_context"))
                                noExecutionNeeded = true;
                            else if (result.ErrorCode is "specified_task_missing" or "invalid_request"
                                or "idempotency_conflict" or "configuration_revision_mismatch" or "config_revision_mismatch")
                                throw new CompletionPolicyRejectedException(result.Message);
                            else
                            {
                                batch.PolicySendUnknown = batch.PolicyStartAttempted;
                                io.Waiting?.Invoke("绑定完成动作尚未确认，保留原请求：" + result.Message);
                            }
                        }
                    }
                }
            }
            catch (BgiStateContinuationStoppedException)
            {
                batch.Cancel();
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (ex is not ControlContextRevokedException
                && ex is not CompletionPolicyRejectedException && !ct.IsCancellationRequested)
            { io.Waiting?.Invoke("绑定完成动作待接续：" + ex.Message); }
            await io.Delay(ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyStateContinuationPolicyTeardownAsync(OnlineHoeingBatch batch,
        string description, string? ticket)
    {
        var executor = _commandExecutor ?? throw new IOException("本批完成动作执行器暂不可用");
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        var runId = host?.OnlineHoeingManagedRun(batch.Generation);
        var policy = batch.Policy ?? throw new InvalidOperationException("本批完成策略未冻结");
        var epoch = batch.ExecutionEpoch ?? throw new IOException("原 BGI 进程尚未确认");
        var ct = batch.Cts.Token;
        var gapResume = false;
        var stoppedPlan = runId is null;
        var associatedResume = runId is null;
        var completed = false;
        string? lastWaiting = null;

        string? CompletionRequestKey()
        {
            if (policy.Policy == TaskConflictPolicy.RunSpecified) return batch.PolicyRequestKey;
            if (policy.Policy != TaskConflictPolicy.Resume || gapResume) return null;
            return batch.PlanResumeContinuation?.TryGetValue("idempotencyKey", out var key) == true
                ? key as string : ticket is not null ? "resume/" + ticket : null;
        }

        void RequireAuthority()
        {
            ct.ThrowIfCancellationRequested();
            RequireCoordinatedControlContext(batch, ct, terminalReport: true);
            lock (_batchGate)
                if (!ReferenceEquals(_commandExecutor, executor) || !ReferenceEquals(_activeBatch, batch)
                    || batch.Generation <= _resetFenceGeneration || _localResetGate.CurrentCount == 0
                    || batch.ExplicitStopRequested || !batch.LocalSucceeded)
                    throw new ControlContextRevokedException("本批绑定完成动作已被停止或替换", ct);
            if (_externalClient?.ServerEpoch is { } current && !CoordinatedBatchLoop.SameEpoch(current, epoch))
                throw new ControlContextRevokedException("原 BGI 已变化，禁止向新进程重放完成动作", ct);
        }

        BgiExternalClient External()
        {
            RequireAuthority();
            return _externalClient is { State: BgiExternalLinkState.Ready } ext
                && CoordinatedBatchLoop.SameEpoch(ext.ServerEpoch, epoch) ? ext
                : throw new IOException("等待连接原 BGI");
        }

        void Waiting(string message)
        {
            if (lastWaiting == message) return;
            lastWaiting = message;
            AddLog("[绑定完成动作] " + message);
            UpdateHoeingBatchStatus(batch, "completionPending", message);
        }

        async Task<bool> Reconcile(CancellationToken token)
        {
            RequireAuthority();
            if (host is null) return true;
            if (runId is not null && policy.Policy != TaskConflictPolicy.Resume && !stoppedPlan)
                stoppedPlan = await host.SettleFailedOnlineHoeingAsync(batch.Generation, runId,
                    batch.PolicyTicketReleased, "锄地结束，按绑定策略结束原计划而不恢复").ConfigureAwait(false);
            if (runId is not null && policy.Policy == TaskConflictPolicy.Resume && !associatedResume)
            {
                if (gapResume)
                    associatedResume = await host.FinishOnlineGapAfterReleaseAsync(runId, ticket!).ConfigureAwait(false);
                else if (batch.PolicyExecution is { } accepted)
                {
                    var saved = host.Runs.Load(runId);
                    var resumeKey = CompletionRequestKey();
                    static bool SameResume(string? observed, string expected) => Guid.TryParse(observed, out var value)
                        && Guid.TryParse(expected, out var job) && value == job;
                    associatedResume = saved?.OnlineHoeingHandoff is { } linked
                            && SameResume(linked.ResumeJobId, accepted.JobId)
                        || saved?.CurrentSubmission is { } current && current.Key == resumeKey && SameResume(current.JobId, accepted.JobId)
                        || saved?.SubmissionHistory.Any(history => history.Key == resumeKey && SameResume(history.JobId, accepted.JobId)) == true;
                    if (!associatedResume) associatedResume = host.AcceptPlanResumeJob(runId, ticket!, accepted.JobId);
                }
            }
            if (!(policy.Policy == TaskConflictPolicy.Resume ? associatedResume : stoppedPlan)) return false;
            if (runId is not null && policy.Policy == TaskConflictPolicy.Resume && !gapResume
                && batch.PolicyTerminalResult is not null)
            {
                // The plan runner is a second consumer of this same terminal. Do not unpin its
                // BGI result until that runner has saved it and can advance the original plan.
                var saved = host.Runs.Load(runId);
                var resumeKey = CompletionRequestKey();
                var handedOff = saved?.OnlineHoeingHandoff;
                var planSavedTerminal = handedOff is { Phase: "terminal", ResumeTerminal: not null }
                    || saved?.CurrentSubmission is { ExecutionExitConfirmed: true } current
                        && current.Key == resumeKey && current.ObservedTerminal is "completed" or "succeeded" or "failed" or "cancelled" or "queueCancelled" or "rejected"
                    || saved?.NodeOutcomes.Any(outcome => outcome.SubmissionKey == resumeKey) == true;
                if (!planSavedTerminal) return false;
            }
            if (!host.CanOpenOnlineSendAfterBatch) return false;
            // Keep the original plan's send gate closed until the successor is actually accepted.
            if (policy.Policy != TaskConflictPolicy.RunSpecified || batch.PolicyExecution is not null)
                await host.OpenOnlineHoeingSendAsync().ConfigureAwait(false);
            return true;
        }

        async Task<bool> Prepare(CancellationToken token)
        {
            External();
            // A lost resume reply may already have started the original job. The same ticket
            // retrieves that receipt; requiring an empty slot here would deadlock on our own job.
            if (policy.Policy == TaskConflictPolicy.Resume && batch.PolicyStartAttempted)
                return true;
            var boundary = await executor.CaptureExecutionBoundarySnapshotAsync().WaitAsync(token).ConfigureAwait(false);
            if (!boundary.Known) return false;
            if (boundary.ProcessId != epoch.ProcessId || boundary.StartTicksUtc != epoch.StartTicksUtc)
                throw new ControlContextRevokedException("完成动作的原 BGI 身份已变化", token);
            if (boundary.SlotOccupied) return false;
            RequireAuthority();
            if (!batch.PolicyTicketReleased && executor.TakeoverTicketSnapshot != ticket)
                throw new ControlContextRevokedException("原接管票据已撤销，禁止启动绑定完成动作", token);
            if (policy.Policy == TaskConflictPolicy.Resume && runId is not null)
            {
                if (batch.SuspendedRawData is { } raw)
                    host!.ConfirmOnlineHoeingSuspend(batch.Generation, ticket, raw);
                var handoff = host!.Runs.Load(runId)?.OnlineHoeingHandoff;
                if (handoff is null || handoff.Ticket != ticket) return false;
                gapResume = handoff.Phase == "gap";
                if (!gapResume && batch.PlanResumeContinuation is null)
                {
                    batch.PlanResumeContinuation = await host.PreparePlanResumeAsync(runId, ticket!, token).ConfigureAwait(false);
                    if (batch.PlanResumeContinuation is null) return false;
                }
            }
            if (policy.Policy != TaskConflictPolicy.Resume || gapResume)
            {
                if (!batch.PolicyTicketReleased)
                {
                    var released = await executor.ExecuteResumeAsync(cancel: true, expectedTicket: ticket,
                        cancellationToken: token).ConfigureAwait(false);
                    if (released.ErrorCode is "already_revoked" or "stale_ticket" or "stale_context" or "stale_epoch")
                        throw new ControlContextRevokedException("原接管已撤销：" + released.Message, token);
                    if (released.Status != "success") return false;
                    batch.PolicyTicketReleased = true;
                }
                try { await Reconcile(token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ControlContextRevokedException && !token.IsCancellationRequested)
                { Waiting("原计划停止记录待补，绑定完成动作继续：" + ex.Message); }
            }
            return true;
        }

        async Task<CommandResult> Start(CancellationToken token)
        {
            RequireAuthority();
            if (policy.Policy == TaskConflictPolicy.RunSpecified)
                return await executor.StartRetainedCompletionActionAsync(batch, token).ConfigureAwait(false);
            if (policy.Policy == TaskConflictPolicy.Stop || gapResume)
                return new CommandResult { Status = "success", Message = "原上下文已释放" };
            batch.PolicyStartAttempted = true;
            var result = runId is not null
                ? await executor.ExecutePlanResumeAsync(ticket!, batch.PlanResumeContinuation!, AddLog, token).ConfigureAwait(false)
                : await executor.ExecuteResumeAsync(expectedTicket: ticket, cancellationToken: token).ConfigureAwait(false);
            if (result.Status == "success") batch.PolicyTicketReleased = true;
            return result;
        }

        async Task<BgiTaskQueueStatus?> Query(CancellationToken token)
        {
            var ext = External();
            if (CompletionRequestKey() is { } key)
            {
                var original = await ext.QueryTaskQueueStatusByKeyAsync(key, token).ConfigureAwait(false);
                if (original is null || original.Status != "not_found" || batch.PolicyExecution is null)
                    return original;
            }
            if (batch.PolicyExecution is not { } execution) return null;
            var queued = await ext.QueryTaskQueueStatusAsync(execution.JobId, token).ConfigureAwait(false);
            if (queued is not null && queued.Status != "not_found") return queued;
            var (_, job) = await ext.QueryJobStatusAsync(execution.JobId, token).ConfigureAwait(false);
            return job is null ? null : new BgiTaskQueueStatus
            {
                TaskHandle = job.JobId, Epoch = job.Epoch, IdempotencyKey = job.IdempotencyKey,
                ExecutionExitConfirmed = job.ExecutionExitConfirmed, Cancelled = job.WasCancelled || job.State == "cancelled",
                Status = job.State switch { "succeeded" => "completed", "failed" or "rejected" => "failed",
                    "cancelled" => "queueCancelled", _ => job.State },
                ErrorCode = job.ErrorCode, ErrorMessage = job.ErrorMessage,
            };
        }

        batch.ConfigurePolicyCancellation(executor.CancelHoeingPolicyExecutionAsync);
        batch.ConfigurePolicyCleanup(async () =>
        {
            await FinishCancelledCoordinatedPolicyAsync(batch, executor, host, runId).ConfigureAwait(false);
            batch.LocalCleanupConfirmed = true;
            return true;
        });
        try
        {
            await RunCompletionContinuationAsync(batch, new CompletionContinuationPorts
            {
                RequireAuthority = RequireAuthority, Prepare = Prepare, Start = Start, Query = Query,
                Reconcile = Reconcile, Waiting = Waiting,
                ExpectedRequestKey = CompletionRequestKey,
                RegisteredJob = true,
                Acknowledge = (result, token) => External().AcknowledgeTaskResultAsync(result.IdempotencyKey!,
                    result.TaskHandle!, result.Epoch!, token),
            }, ct).ConfigureAwait(false);
            completed = true;
            AddLog("[绑定完成动作] " + description + "后的绑定动作已收口"
                + (batch.CompletionActionWarning is { } warning ? "：" + warning : ""));
        }
        catch (ControlContextRevokedException)
        {
            completed = true;
            throw;
        }
        finally
        {
            if (completed || ct.IsCancellationRequested) batch.MarkPolicyPhaseFinished();
        }
    }
}
