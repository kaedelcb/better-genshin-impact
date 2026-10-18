using MultiplayerHoeingAssistant.Services;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>只有服务端可以授予接管、推进业务项或分配有限恢复次数。</summary>
    private Task<bool> RunCoordinatedBatchAsync(OnlineHoeingBatch batch,
        Func<CancellationToken, Task> prepare, Func<CancellationToken, Task> takeover)
    {
        CommandExecutor? owner = null;
        BgiExternalClient External() => _externalClient is { State: BgiExternalLinkState.Ready } ext
            ? ext : throw new IOException("BGI 外部接口尚未就绪");
        async Task<bool?> ConfirmLocalTaskExit(string jobId, BgiEpoch epoch, string requestKey,
            bool cancelled, CancellationToken ct)
        {
            var ext = External();
            if (!CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch))
                throw new InvalidOperationException("普通任务终态核对前 BGI 纪元已变化");
            var (_, job) = await ext.QueryJobStatusAsync(jobId, ct);
            if (!ReferenceEquals(ext, _externalClient) || ext.State != BgiExternalLinkState.Ready
                || !CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch))
                throw new InvalidOperationException("普通任务终态核对期间 BGI 身份或通道已变化");
            return job is null ? null : CoordinatedBatchLoop.ConfirmsLocalTaskExit(job, jobId, epoch, requestKey, cancelled);
        }
        var io = new CoordinatedBatchRuntime
        {
            Prepare = prepare,
            Takeover = async ct =>
            {
                owner = _commandExecutor ?? throw new InvalidOperationException("本机接管执行器不可用");
                await takeover(ct);
                if (!ReferenceEquals(owner, _commandExecutor)) throw new InvalidOperationException("接管执行器已被替换");
            },
            Exchange = (stage, result, reason, index, attempt, ct) =>
                (_signalRClient ?? throw new IOException("控制房间未连接")).UpdateCoordinatedBatchAsync(
                    batch.Generation, batch.Token, batch.BatchId, string.Join(",", batch.Bindings.Select(b => b.Type)),
                    index, attempt, result, ct, stage, reason),
            ReadJobs = ct => External().QueryJobListAsync(ct),
            ReadTerminal = (jobId, ct) => External().QueryTaskQueueStatusAsync(jobId, ct),
            CancelOwned = async (jobId, ct) => { await External().CancelOwnedTaskAsync(jobId, ct); },
            Submit = (item, epoch, ct) =>
            {
                var ext = _externalClient is { State: BgiExternalLinkState.Ready } ready ? ready
                    : throw new BgiNotSentException(BgiNotSentException.ChannelNotReady, "BGI 通道不可用，启动未发送");
                if (_signalRClient?.SupportsLocalTaskFailure != true)
                    throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "服务端缺少普通任务失败隔离能力，启动未发送");
                if (!ext.HasCapability("hoeing.batchOutcome.v1") || !ext.HasCapability("hoeing.coordinatedStart.v1")
                    || !ext.HasCapability("hoeing.finishEvidence.v1"))
                    throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "BGI 缺少统一联机启动/结果能力，启动未发送");
                if (string.IsNullOrEmpty(batch.TakeoverTicket) || owner == null
                    || !ReferenceEquals(owner, _commandExecutor) || owner.TakeoverTicketSnapshot != batch.TakeoverTicket
                    || ext.TakeoverTicket != batch.TakeoverTicket)
                    throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "本批接管票据已变化，启动未发送");
                if (!batch.ConfigRevisions.TryGetValue(OnlineHoeingBatch.ConfigurationKey(item.Name, item.IsOneDragon), out var revision))
                    throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "本批任务配置修订未冻结，启动未发送");
                return ext.SubmitTaskStartAsync(item.IsOneDragon ? null : item.Name,
                    item.IsOneDragon ? item.Name : null, 0, batch.Generation,
                    string.Join(",", batch.Bindings.Where(b => b.Type == "group").Select(b => b.Name)), ct,
                    preempt: false, idempotencyKey: item.RequestKey,
                    expectedConfigRevision: revision,
                    bgiEpoch: new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                    coordinatedHoeing: true, hoeingBatchId: batch.BatchId,
                    hoeingParticipantUids: batch.Start!.ParticipantUids);
            },
            ReleaseTicket = async (ticket, epoch, ct) =>
            {
                if (owner == null || !ReferenceEquals(owner, _commandExecutor)
                    || owner.TakeoverTicketSnapshot != ticket) return false;
                var current = await External().QueryJobListAsync(ct);
                if (!CoordinatedBatchLoop.SameEpoch(epoch, current?.Epoch)) return false;
                var release = await owner.ExecuteResumeAsync(cancel: true, expectedTicket: ticket).WaitAsync(ct);
                return release.Status == "success" || release.ErrorCode is "stale_ticket" or "stale_context"
                    && await owner.ConfirmTakeoverRevokedAsync(epoch, batch.TakenOver, ct);
            },
            RequireFinishEvidence = true,
            RequireLocalTaskFailureIsolation = true,
            ConfirmLocalTasksFailure = (jobId, epoch, requestKey, ct) =>
                ConfirmLocalTaskExit(jobId, epoch, requestKey, false, ct),
            ConfirmLocalTasksCancellation = (jobId, epoch, requestKey, ct) =>
                ConfirmLocalTaskExit(jobId, epoch, requestKey, true, ct),
            FinalizeLocal = (epoch, ct) => FinalizeLocalCoordinatedBatchAsync(batch,
                owner ?? throw new InvalidOperationException("本批收尾执行器不可用"), epoch, ct),
            OriginalProcessAlive = CommandExecutor.ProbeOriginalBgiProcess,
            AbandonExitedOwnership = (ticket, epoch, ct) => owner is null
                ? Task.FromResult(false) : AbandonExitedCoordinatedBatchOwnershipAsync(batch, owner, ticket, epoch, ct),
            Status = (phase, message) =>
            {
                if (batch.LocalExecutionReleased && phase is "running" or "stopping" or "completed")
                    UpdateHoeingBatchStatus(batch, "localCompleted", "本端已完成，可手动执行；等待队友及批次清理确认");
                else UpdateHoeingBatchStatus(batch, phase, message);
            },
            Log = AddLog,
        };
        return new CoordinatedBatchLoop(batch, io).RunAsync();
    }
}

/// <summary>生产循环的窄 IO 边界；定向测试可以驱动真实状态推进，不实例化 WPF 或操作用户 BGI。</summary>
internal sealed class CoordinatedBatchRuntime
{
    internal required Func<CancellationToken, Task> Prepare { get; init; }
    internal required Func<CancellationToken, Task> Takeover { get; init; }
    internal required Func<string, string, string, int, int, CancellationToken, Task<CoordinatedBatchSnapshot>> Exchange { get; init; }
    internal required Func<CancellationToken, Task<BgiJobListSnapshot?>> ReadJobs { get; init; }
    internal required Func<string, CancellationToken, Task<BgiTaskQueueStatus?>> ReadTerminal { get; init; }
    internal required Func<string, CancellationToken, Task> CancelOwned { get; init; }
    internal required Func<BatchExpectedItem, BgiEpoch, CancellationToken, Task<BgiTaskSubmitResult>> Submit { get; init; }
    internal required Func<string, BgiEpoch, CancellationToken, Task<bool>> ReleaseTicket { get; init; }
    // Finalization owns only this endpoint. Its task may run while the loop keeps the team lease alive.
    internal Func<BgiEpoch, CancellationToken, Task>? FinalizeLocal { get; init; }
    internal Func<BgiEpoch, bool?>? OriginalProcessAlive { get; init; }
    internal Func<string?, BgiEpoch, CancellationToken, Task<bool>>? AbandonExitedOwnership { get; init; }
    internal Action<string, string> Status { get; init; } = (_, _) => { };
    internal Action<string> Log { get; init; } = _ => { };
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan StageTimeout { get; init; } = TimeSpan.FromSeconds(150);
    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(40);
    internal TimeSpan SubmissionReconcileTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan ObservationReconcileTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan AuthorityContactTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal bool RequireFinishEvidence { get; init; }
    internal bool RequireLocalTaskFailureIsolation { get; init; }
    internal Func<string, BgiEpoch, string, CancellationToken, Task<bool?>>? ConfirmLocalTasksFailure { get; init; }
    internal Func<string, BgiEpoch, string, CancellationToken, Task<bool?>>? ConfirmLocalTasksCancellation { get; init; }
}

internal sealed class CoordinatedBatchLoop(OnlineHoeingBatch batch, CoordinatedBatchRuntime io)
{
    private BatchExpectedItem? _item;
    private BgiEpoch? _epoch;
    private int _index;
    private int _attempt;
    private long _revision = -1;
    private string _stage = "registered";
    private string _result = "";
    private string _reason = "";
    private bool _complete;
    private DateTime _submittedAt;
    private Task? _prepare;
    private Task? _takeover;
    private CancellationTokenSource? _stageCancellation;
    private DateTime _stageStarted;
    private bool _cancelSent;
    private int _completedNative;
    private DateTime? _observationUnavailableSince;

    internal static bool SameEpoch(BgiEpoch? expected, BgiEpoch? actual) => expected != null && actual != null
        && expected.ProcessId == actual.ProcessId && expected.StartTicksUtc == actual.StartTicksUtc;

    internal static bool ConfirmsLocalTaskExit(BgiJobInfo job, string jobId, BgiEpoch epoch,
        string requestKey, bool cancelled) => Guid.TryParse(jobId, out var expectedJob)
        && Guid.TryParse(job.JobId, out var observedJob) && expectedJob == observedJob
        && SameEpoch(epoch, job.Epoch) && job.IdempotencyKey == requestKey
        && job.State == (cancelled ? "cancelled" : "failed") && job.WasCancelled == cancelled
        && job.ExecutionExitConfirmed;

    internal static string RequestKey(string batchId, string uid, int index, int attempt) => "hoeing.v2/"
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{batchId}\n{uid}\n{index}\n{attempt}"))).ToLowerInvariant();

    internal static bool KnownRejection(string? code) => code is "queue_full" or "queue_unavailable"
        or "capability_required" or "invalid_request" or "stale_epoch" or "request_expired"
        or "unsupported_operation" or "task_busy" or "manual_stop_cooldown";

    private async Task<CoordinatedBatchSnapshot> ExchangeAsync(CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(10));
        var snapshot = await io.Exchange(_stage, _result, _reason, _index, _attempt, bound.Token);
        var start = batch.Start!;
        if (snapshot.Protocol != "hoeing.start.v2" || snapshot.BatchId != batch.BatchId
            || snapshot.Generation != batch.Generation || snapshot.Revision < _revision
            || snapshot.Index < _index || snapshot.Index > batch.Bindings.Count
            || snapshot.Attempt is < 0 or > 1
            || snapshot.ParticipantUids.Count != start.ParticipantUids.Count
            || !new HashSet<string>(start.ParticipantUids, StringComparer.Ordinal).SetEquals(snapshot.ParticipantUids)
            || !snapshot.Bindings.TryGetValue(batch.SelfUid, out var bindings) || !batch.Bindings.SequenceEqual(bindings))
            throw new InvalidOperationException("批次身份、冻结绑定或版本不匹配，禁止推进");
        if (_revision != snapshot.Revision)
            io.Log($"[全队批次] {batch.BatchId} revision={snapshot.Revision} item={snapshot.Index} attempt={snapshot.Attempt} phase={snapshot.Phase} reason={snapshot.Reason}");
        _revision = snapshot.Revision;
        return snapshot;
    }

    private void StartStage(Func<CancellationToken, Task> action)
    {
        _stageCancellation?.Dispose();
        _stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(batch.Cts.Token);
        _stageCancellation.CancelAfter(io.StageTimeout);
        _stageStarted = DateTime.UtcNow;
        var token = _stageCancellation.Token;
        var task = Task.Run(() => action(token), CancellationToken.None);
        if (_stage == "registered") _prepare = task;
        else _takeover = task;
    }

    private async Task<bool> ObserveStageAsync(Task task, string failure, CancellationToken ct)
    {
        if (!task.IsCompleted && DateTime.UtcNow - _stageStarted < io.StageTimeout) return false;
        try
        {
            if (!task.IsCompleted) throw new TimeoutException($"本机阶段执行超过{io.StageTimeout.TotalSeconds:0.###}秒，已停止本轮");
            await task;
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception ex) when (!batch.IsCancellationRequested)
        {
            _result = failure;
            _reason = LimitReason(ex.Message);
            throw;
        }
    }

    internal async Task<bool> RunAsync()
    {
        if (batch.Start == null || batch.Bindings.Count == 0 || string.IsNullOrWhiteSpace(batch.SelfUid))
            throw new InvalidOperationException("统一开始缺少冻结批次、成员或绑定");
        var ct = batch.Cts.Token;
        var lastContact = DateTime.UtcNow;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (batch.LocalSucceeded && batch.LocalFinalizationTask is not null)
                    await ObserveLocalFinalizationAsync();
                CoordinatedBatchSnapshot team;
                try { team = await ExchangeAsync(ct); lastContact = DateTime.UtcNow; }
                catch (Exception ex) when (!batch.IsCancellationRequested
                    && ex is IOException or TimeoutException or OperationCanceledException
                    && DateTime.UtcNow - lastContact < io.AuthorityContactTimeout)
                {
                    await Task.Delay(io.PollInterval, ct);
                    continue; // 没有权威回应时只能续等，不能启动或推进。
                }
                if (_result == "local_tasks_failed" && batch.ObserveLocalTasksFailureAccepted(team))
                {
                    batch.Outcome = "failed";
                    io.Status("cleaning", "联机锄地已完成，本机普通任务失败；正在释放本机执行权，队友继续当前项");
                    return false; // Cleanup only releases this exact job/ticket. No success policy is run.
                }
                if (team.Phase == "aborted" && team.Reason.StartsWith("local_tasks_failed:", StringComparison.Ordinal)
                    && batch.CurrentBindingSucceeded && !batch.LocalSucceeded && !batch.IsCancellationRequested)
                {
                    batch.PendingBindingsSkipped = true;
                    batch.Outcome = "partial_completed";
                    batch.TeamFailureReason = team.Reason;
                    batch.Message = "当前绑定已完成；因成员普通任务失败，后续绑定未启动";
                    io.Status("cleaning", batch.Message);
                    return false;
                }
                if (team.Phase == "aborted" && batch.PreserveLocalSuccess(team.Reason))
                {
                    batch.TeamFailureReason = team.Reason;
                    if (!await ObserveLocalFinalizationAsync())
                    {
                        await Task.Delay(io.PollInterval, ct);
                        continue;
                    }
                    _complete = true;
                    batch.Outcome = "succeeded";
                    io.Status("completed", "本端已完成；队友异常不撤销本端结果或后继任务");
                    return true;
                }
                if (team.Phase == "aborted")
                {
                    batch.Outcome = team.Reason.Contains("cancel", StringComparison.Ordinal) || team.Reason == "manual_reset"
                        ? "cancelled" : team.Reason.Contains("unknown", StringComparison.Ordinal) ? "unknown" : "failed";
                    if (_result is not ("local_failed" or "local_tasks_failed")) _result = batch.Outcome;
                    _reason = LimitReason(team.Reason);
                    throw new InvalidOperationException("全队批次中止: " + team.Reason);
                }
                if (team.Phase == "completed")
                {
                    if (_item?.State != BatchItemState.TerminalConfirmed || _result != "succeeded"
                        || team.Index != batch.Bindings.Count || !batch.TakenOver)
                        throw new InvalidOperationException("服务端完成与本端任务终态不一致");
                    batch.CoordinatedSucceeded = true;
                    if (!await ObserveLocalFinalizationAsync())
                    {
                        await Task.Delay(io.PollInterval, ct);
                        continue;
                    }
                    _complete = true;
                    batch.Outcome = "succeeded";
                    io.Status("completed", "全队任务完成，正在按本批策略收尾");
                    return true; // 正常结束的票据归外层冻结策略，不在这里释放。
                }
                if (team.Phase == "registering")
                    io.Status("registering", "等待全队登记");
                else if (team.Phase == "preparing")
                {
                    io.Status("preparing", "正在准备本机，持续向全队报告状态");
                    if (_prepare == null) StartStage(io.Prepare);
                    if (!batch.Prepared && await ObserveStageAsync(_prepare!, "preparation_failed", ct))
                    {
                        var jobs = await ReadJobsForObservationAsync(ct);
                        if (jobs is null)
                        {
                            await Task.Delay(io.PollInterval, ct);
                            continue;
                        }
                        _observationUnavailableSince = null;
                        _epoch = jobs.Epoch!;
                        batch.ExecutionEpoch = _epoch;
                        batch.Prepared = true;
                        _stage = "prepared";
                    }
                }
                else if (team.Phase == "takeover")
                {
                    if (!batch.Prepared) throw new InvalidOperationException("本端准备未完成，不执行接管");
                    io.Status("takeover", "全队准备完成，正在准确接管原任务");
                    if (_takeover == null) StartStage(io.Takeover);
                    if (!batch.TakenOver && await ObserveStageAsync(_takeover!, "takeover_failed", ct))
                    {
                        var jobs = await ReadJobsForObservationAsync(ct);
                        if (jobs is null)
                        {
                            await Task.Delay(io.PollInterval, ct);
                            continue;
                        }
                        if (!SameEpoch(_epoch, jobs.Epoch)) throw new InvalidOperationException("接管期间 BGI 纪元已改变");
                        _observationUnavailableSince = null;
                        batch.TakenOver = true;
                        _stage = "takeoverConfirmed";
                    }
                }
                else if (team.Phase is "running" or "stopping")
                {
                    if (!batch.TakenOver) throw new InvalidOperationException("本端接管未确认，禁止业务项起步");
                    await RunItemAsync(team, ct);
                    if (batch.LocalSucceeded) await ObserveLocalFinalizationAsync();
                }
                else throw new InvalidOperationException("未知联机批次阶段: " + team.Phase);
                await Task.Delay(io.PollInterval, ct);
            }
        }
        catch (Exception ex)
        {
            if (batch.LocalSucceeded && !batch.IsCancellationRequested)
            {
                batch.Outcome = "succeeded";
                batch.FailureReason ??= "本端任务已完成，收尾待确认：" + ex.Message;
                io.Log($"[全队批次] {batch.BatchId} 本端成功结果保留，收尾待确认: {ex.Message}");
                io.Status("cleanupPending", batch.FailureReason);
                return false;
            }
            if (batch.IsCancellationRequested) _result = batch.Outcome is "failed" or "cancelled" ? batch.Outcome : "unknown";
            else if (_result is not ("preparation_failed" or "takeover_failed" or "unknown" or "failed" or "local_failed" or "local_tasks_failed" or "cancelled"))
                _result = !batch.Prepared ? "preparation_failed" : !batch.TakenOver ? "takeover_failed" : "unknown";
            batch.Outcome = _result is "failed" or "local_failed" or "local_tasks_failed" or "preparation_failed" or "takeover_failed" ? "failed" : _result;
            _reason = LimitReason(_reason.Length == 0 ? ex.Message : _reason);
            batch.FailureReason ??= _reason;
            io.Log($"[全队批次] {batch.BatchId} {_result}: {_reason}");
            io.Status("aborted", "本批已中止，正在确认本批任务与接管责任清理");
            return false;
        }
        finally
        {
            if (!_complete) await CleanupAsync();
            if (_prepare?.IsCompleted != false && _takeover?.IsCompleted != false) _stageCancellation?.Dispose();
        }
    }

    private async Task<bool> ObserveLocalFinalizationAsync()
    {
        if (!batch.LocalSucceeded || io.FinalizeLocal is null) return true;
        var epoch = batch.LocalSuccessEpoch ?? throw new InvalidOperationException("本端成功缺少原 BGI 身份");
        var finalize = batch.BeginLocalFinalization(async () =>
        {
            await io.FinalizeLocal(epoch, batch.Cts.Token);
            batch.Cts.Token.ThrowIfCancellationRequested();
            batch.MarkLocalExecutionReleased(); // The callback may finish after team contact was lost.
        });
        if (!finalize.IsCompleted) return false;
        await finalize;
        batch.Cts.Token.ThrowIfCancellationRequested();
        batch.MarkLocalExecutionReleased();
        return true;
    }

    private async Task RunItemAsync(CoordinatedBatchSnapshot team, CancellationToken ct)
    {
        if (team.Index != _index || team.Attempt != _attempt)
        {
            if (batch.LocalSucceeded)
                throw new InvalidOperationException("本端最后一项已完成并进入收尾，不重放旧批次");
            var nextItem = team.Index == _index + 1 && team.Attempt == 0 && _result == "succeeded";
            var recovery = team.Index == _index && team.Attempt == _attempt + 1 && _result is "failed" or "stopped" or "succeeded";
            if (_item?.State != BatchItemState.TerminalConfirmed || (!nextItem && !recovery))
                throw new InvalidOperationException("旧项未确认退出或恢复推进不合法");
            if (team.Index == _index && _completedNative > 0)
                throw new InvalidOperationException("本项已有联机锄地完成证据，不整组重跑");
            _index = team.Index; _attempt = team.Attempt;
            _item = null; _result = ""; _reason = ""; _cancelSent = false; _completedNative = 0;
            batch.MarkCurrentBindingStarted();
        }
        if (_index >= batch.Bindings.Count) throw new InvalidOperationException("运行项越过冻结绑定范围");
        var binding = batch.Bindings[_index];
        _item ??= new BatchExpectedItem(binding.Name, binding.Type == "onedragon")
            { RequestKey = RequestKey(batch.BatchId, batch.SelfUid, _index, _attempt) };
        if (_item.State == BatchItemState.TerminalConfirmed && _result.Length > 0)
            return; // Already observed terminal evidence survives a later disconnect or BGI restart.
        io.Status(team.Phase, $"第 {_index + 1}/{batch.Bindings.Count} 项：{binding.Name}（恢复 {_attempt}/1）");
        using var rpc = CancellationTokenSource.CreateLinkedTokenSource(ct);
        rpc.CancelAfter(TimeSpan.FromSeconds(10));
        var jobs = await ReadJobsForObservationAsync(rpc.Token);
        if (jobs is null) return;
        if (!SameEpoch(_epoch, jobs.Epoch)) throw new InvalidOperationException("BGI 纪元已变化，不向后继进程重放");
        AttachOriginalJob(jobs);
        if (_item.State == BatchItemState.Submitted && _item.JobId == null
            && DateTime.UtcNow - _submittedAt > io.SubmissionReconcileTimeout)
            throw new TimeoutException("启动回执未知且按原请求键未找到作业，禁止重发");
        if (team.Phase == "stopping" && _result.Length == 0)
        {
            if (_item.State == BatchItemState.PendingSubmit)
            { _item.State = BatchItemState.TerminalConfirmed; _result = "stopped"; }
            else if (_item.JobId != null && !_cancelSent)
            { await io.CancelOwned(_item.JobId, rpc.Token); _cancelSent = true; }
        }
        if (_item.State == BatchItemState.Submitted && _item.JobId != null && _result.Length == 0)
        {
            var terminal = await ReadObservationAsync(token => io.ReadTerminal(_item.JobId, token), rpc.Token);
            if (terminal?.Status is null or "" or "not_found")
            {
                ObservationUnavailable("本批作业终态暂不可读");
                return;
            }
            if (io.RequireFinishEvidence && (terminal.Status is "completed" or "failed" or "queueCancelled")
                && terminal.HoeingEvidence?.MatchesTerminal(terminal) != true)
            {
                ObservationUnavailable("本批整组终态与联机执行证据未完整对齐");
                return;
            }
            if (terminal.HoeingEvidence is { IsValid: true } evidence)
                _completedNative = Math.Max(_completedNative, evidence.Completed);
            var localTasksCancellation = !batch.IsCancellationRequested && !_cancelSent && team.Phase == "running"
                && terminal.HoeingEvidence?.IsLocalTasksCancellation(terminal) == true;
            var localTasksFailure = io.RequireLocalTaskFailureIsolation
                && (terminal.HoeingEvidence?.IsLocalTasksFailure(terminal) == true || localTasksCancellation);
            if (localTasksFailure)
            {
                bool? identityConfirmed = null;
                var confirm = localTasksCancellation ? io.ConfirmLocalTasksCancellation : io.ConfirmLocalTasksFailure;
                if (confirm is not null)
                {
                    try { identityConfirmed = await confirm(_item.JobId, _epoch!, _item.RequestKey, rpc.Token); }
                    catch (Exception ex) when (!batch.IsCancellationRequested
                        && ex is IOException or TimeoutException or OperationCanceledException) { }
                }
                if (identityConfirmed != true)
                {
                    ObservationUnavailable("本机普通任务失败的原作业身份尚未确认");
                    return;
                }
            }
            ct.ThrowIfCancellationRequested();
            _observationUnavailableSince = null;
            _result = localTasksFailure ? "local_tasks_failed" : CoordinatedBatchOutcome.Classify(terminal, team.Phase == "stopping");
            if (!localTasksFailure && _completedNative > 0 && (terminal?.Status == "failed" || _result == "stopped"))
                _result = "local_failed"; // The group failed after real hoeing work; restarting it would repeat that work.
            if (!string.IsNullOrWhiteSpace(terminal?.ErrorCode))
                _reason = LimitReason(terminal.ErrorCode + ": " + terminal.ErrorMessage);
            if (terminal?.Status is "completed" or "failed" or "queueCancelled")
                _item.State = BatchItemState.TerminalConfirmed;
            if (_result == "local_failed")
                _reason = LimitReason($"联机锄地已完成 {_completedNative} 次，本机配置组后续失败，禁止整组重跑；" + _reason);
            if (_result == "local_tasks_failed")
            {
                var outcome = localTasksCancellation ? "已完成锄地后的本机任务已取消或切换" : "本机普通任务失败";
                _reason = LimitReason($"{outcome}；本项全部 {_completedNative} 次联机锄地已完成，不重跑且不执行成功完成动作；" + _reason);
                batch.RecordLocalTasksFailure(_epoch!, _reason, _index, _attempt, _item.JobId!, _item.RequestKey);
            }
            if (_result == "unknown") throw new InvalidOperationException("本批任务终态未知");
            if (_item.State == BatchItemState.TerminalConfirmed && _result == "succeeded")
            {
                batch.MarkCurrentBindingSucceeded();
                if (_index == batch.Bindings.Count - 1) batch.MarkLocalSuccess(_epoch!);
            }
        }
        if (team.Phase != "running" || _result.Length != 0 || _item.State != BatchItemState.PendingSubmit) return;
        _observationUnavailableSince = null;
        _item.State = BatchItemState.Submitted; // 发送前保留唯一请求身份；任何不确定都不得生成第二次提交。
        _submittedAt = DateTime.UtcNow;
        BgiTaskSubmitResult submitted;
        try { submitted = await io.Submit(_item, _epoch!, rpc.Token); }
        catch (BgiNotSentException ex) when (BgiNotSentException.IsKnownEvidenceCode(ex.EvidenceCode))
        { _item.State = BatchItemState.TerminalConfirmed; _result = "failed"; _reason = LimitReason(ex.Message); return; }
        catch (Exception ex) when (!batch.IsCancellationRequested && ex is IOException or TimeoutException or OperationCanceledException)
        { io.Log("[全队批次] 启动回执未确认，将按原请求键补查: " + ex.Message); return; }
        if (!submitted.Success && KnownRejection(submitted.ErrorCode))
        {
            _item.State = BatchItemState.TerminalConfirmed;
            _result = submitted.ErrorCode == "manual_stop_cooldown" ? "cancelled" : "failed";
            _reason = LimitReason(submitted.ErrorCode + ": " + submitted.ErrorMessage);
            return;
        }
        if (!submitted.Success || submitted.Status is not ("queued" or "adopted" or "already_executed")
            || string.IsNullOrWhiteSpace(submitted.TaskHandle))
        { io.Log("[全队批次] 提交结果未知，保留原请求身份补查: " + submitted.ErrorCode); return; }
        _item.JobId = submitted.TaskHandle;
        io.Log($"[全队批次] {batch.BatchId} item={_index} attempt={_attempt} job={_item.JobId} {binding.Name}");
    }

    private async Task<T?> ReadObservationAsync<T>(Func<CancellationToken, Task<T?>> read, CancellationToken ct)
        where T : class
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await CoordinatedBatchReads.ReadAsync(read, budget.Token); }
        catch (Exception ex) when (!batch.IsCancellationRequested
            && ex is IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
        { return null; }
    }

    private async Task<BgiJobListSnapshot?> ReadJobsForObservationAsync(CancellationToken ct)
    {
        var jobs = await ReadObservationAsync(io.ReadJobs, ct);
        if (jobs?.Epoch is not { ProcessId: > 0, StartTicksUtc: > 0 })
        {
            ObservationUnavailable("BGI 作业状态暂不可读");
            return null;
        }
        return jobs;
    }

    private void ObservationUnavailable(string message)
    {
        var now = DateTime.UtcNow;
        _observationUnavailableSince ??= now;
        if (now - _observationUnavailableSince >= io.ObservationReconcileTimeout)
            throw new TimeoutException(message + "，有界补查后仍未确认；未重发任务");
        io.Status("reconciling", message + "，正在按原任务身份补查；未重发任务");
    }

    private void AttachOriginalJob(BgiJobListSnapshot jobs)
    {
        if (_item == null || _item.State != BatchItemState.Submitted) return;
        var matches = jobs.Jobs.Where(j => j.IdempotencyKey == _item.RequestKey).ToList();
        if (matches.Count > 1 || (matches.Count == 1 && _item.JobId != null && matches[0].JobId != _item.JobId))
            throw new InvalidOperationException("原请求键对应的作业身份冲突");
        if (matches.Count == 1) _item.JobId ??= matches[0].JobId;
    }

    private async Task<bool> SettleOwnedJobAsync(CancellationToken ct)
    {
        if (_item == null || _item.State is BatchItemState.PendingSubmit or BatchItemState.TerminalConfirmed) return true;
        while (!ct.IsCancellationRequested)
        {
            if (OriginalProcessExited()) return true;
            var jobs = await ReadObservationAsync(io.ReadJobs, ct);
            if (jobs?.Epoch is null)
            {
                await Task.Delay(io.PollInterval, ct);
                continue;
            }
            if (!SameEpoch(_epoch, jobs.Epoch)) return OriginalProcessExited();
            AttachOriginalJob(jobs);
            if (_item.JobId == null) return false; // 查无作业不是未发送；不能释放本批责任。
            var terminal = await io.ReadTerminal(_item.JobId, ct);
            if (terminal?.Status is "completed" or "failed" or "queueCancelled")
            { _item.State = BatchItemState.TerminalConfirmed; return true; }
            if (jobs!.Jobs.All(j => j.JobId != _item.JobId || j.IdempotencyKey != _item.RequestKey)) return false;
            if (!_cancelSent) { await io.CancelOwned(_item.JobId, ct); _cancelSent = true; }
            await Task.Delay(io.PollInterval, ct);
        }
        return false;
    }

    private bool OriginalProcessExited() => _epoch is not null && io.OriginalProcessAlive?.Invoke(_epoch) == false;

    private async Task CleanupAsync()
    {
        if (batch.LocalExecutionReleased)
        {
            batch.CleanupConfirmed = true;
            return; // This loop no longer owns BGI; delayed team failures cannot reclaim a successor.
        }
        _stageCancellation?.Cancel();
        using var cleanup = new CancellationTokenSource(io.CleanupTimeout);
        try { await ExchangeAsync(cleanup.Token); }
        catch (Exception ex) { io.Log("[全队批次] 中止上报未确认: " + ex.Message); }
        var stagesDone = true;
        foreach (var task in new[] { _prepare, _takeover, batch.LocalFinalizationTask }.Where(t => t != null))
        {
            try { await task!.WaitAsync(cleanup.Token); }
            catch (Exception ex)
            {
                if (!task!.IsCompleted) stagesDone = false;
                io.Log("[全队批次] 本机准备/接管退出: " + ex.Message);
            }
        }
        var jobDone = false;
        try { jobDone = await SettleOwnedJobAsync(cleanup.Token); }
        catch (Exception ex) { io.Log("[全队批次] 本批作业退出未确认: " + ex.Message); }
        var ticketDone = string.IsNullOrEmpty(batch.TakeoverTicket) && !batch.TakenOver;
        if (stagesDone && jobDone && !string.IsNullOrEmpty(batch.TakeoverTicket) && _epoch != null)
        {
            try
            {
                ticketDone = OriginalProcessExited()
                    ? io.AbandonExitedOwnership is not null
                        && await io.AbandonExitedOwnership(batch.TakeoverTicket, _epoch, cleanup.Token)
                    : await io.ReleaseTicket(batch.TakeoverTicket, _epoch, cleanup.Token);
            }
            catch (Exception ex) { io.Log("[全队批次] 本批票据释放未确认: " + ex.Message); }
        }
        batch.CleanupConfirmed = stagesDone && jobDone && ticketDone;
        io.Status(batch.CleanupConfirmed ? "aborted" : "cleanupPending",
            batch.CleanupConfirmed ? "本批已中止并清理" : "本批清理尚未确认，保留执行责任");
        // cleaned ACK 由外层在策略及整个调用栈收口后发送，本循环不抢先宣称已清理。
    }

    private static string LimitReason(string reason) => reason.Length <= 512 ? reason : reason[..512];
}
