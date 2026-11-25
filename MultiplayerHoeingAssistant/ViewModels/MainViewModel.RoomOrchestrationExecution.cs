using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private const string RoomOrchestrationIntentPrefix = "room-orch-";
    private static readonly TimeSpan RoomOrchestrationSignalLifetime = TimeSpan.FromMinutes(2);
    private static readonly string RoomOrchestrationExecutionPath = Path.Combine(AssistantDataDirectory.Root,
        "room-orchestration", AssistantInstanceScope.CurrentSuffix + ".execution.json");

    // Execution identity is separate from the display cache. A saved record can deny replay and
    // identify an already accepted job, but loading it never schedules a new switch or task start.
    private sealed record RoomOrchestrationExecutionRecord(RoomOrchestrationRoomKey Source, string BatchId,
        int Generation, string IntentId, TaskConflictPolicySettings Policy, BgiEpoch Epoch, long StopVersion,
        long IntentTimestamp, long ReleaseBoundaryUtc, string SourceContext, string SourceIdentity)
    {
        public string SwitchState { get; init; } = "waiting";
        public string PostState { get; init; } = Policy.RoomSwitch?.RunAfterSwitch == true ? "pending" : "none";
        public string Message { get; init; } = "本机清理已确认，等待原房间收尾";
        public string? TargetIdentity { get; init; }
        public long CommittedAtUtc { get; init; }
        public bool SubmitAttempted { get; init; }
        public string? JobId { get; init; }
        public string? RunId { get; init; }
        public string? WireRunId { get; init; }
        public bool OnlineRevoked { get; init; }
        public int OnlineGeneration { get; init; }
        public long OnlineTriggeredAtUtc { get; init; }
        public string? OnlineJobId { get; init; }
        public string? OnlineSubmissionKey { get; init; }
        public string? OnlineWorkflowRunId { get; init; }
        public bool OnlineConfirmed { get; init; }
        public bool ResultAcknowledged { get; init; }
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public long Revision { get; init; }
        public RoomSwitchCompletion Target => Policy.RoomSwitch!;
    }

    private sealed record RoomOrchestrationCutoff(RoomOrchestrationRoomKey Target, BgiEpoch Epoch, long AtUtc, int Generation);
    private sealed record RoomOrchestrationExecutionFile(RoomOrchestrationCutoff? Cutoff, RoomOrchestrationExecutionRecord[] Records,
        RoomOrchestrationCutoff? PreviousCutoff = null);
    private sealed class RoomOrchestrationIntent(string id, CommandExecutor owner)
    {
        internal string Id { get; } = id;
        internal CommandExecutor Owner { get; } = owner;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal string CancellationReason { get; set; } = "原编排已撤销";
    }
    private readonly object _roomOrchestrationExecutionGate = new();
    private readonly Dictionary<string, RoomOrchestrationExecutionRecord> _roomOrchestrationExecutions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _roomOrchestrationSubmitGate = new(1, 1);
    private readonly SemaphoreSlim _roomOrchestrationOnlineGate = new(1, 1);
    private RoomOrchestrationIntent? _pendingRoomOrchestration;
    private RoomOrchestrationCutoff? _roomOrchestrationCutoff;
    private RoomOrchestrationCutoff? _previousRoomOrchestrationCutoff;
    private bool _roomOrchestrationExecutionLoaded;
    private bool _roomOrchestrationExecutionUnreadable;
    private long _roomOrchestrationUnreadableAtUtc;

    private RoomOrchestrationCutoff? CurrentRoomOrchestrationCutoffLocked()
    {
        var key = GetRoomOrchestrationKey(_config?.ActiveRoomProfile);
        if (key is null) return null;
        if (_roomOrchestrationCutoff?.Target == key) return _roomOrchestrationCutoff;
        return _previousRoomOrchestrationCutoff?.Target == key ? _previousRoomOrchestrationCutoff : null;
    }

    private void LoadRoomOrchestrationExecution()
    {
        RoomOrchestrationExecutionRecord[] interrupted;
        lock (_roomOrchestrationExecutionGate)
        {
            if (_roomOrchestrationExecutionLoaded) return;
            _roomOrchestrationExecutionLoaded = true;
            try
            {
                if (!File.Exists(RoomOrchestrationExecutionPath)) return;
                var saved = JsonSerializer.Deserialize<RoomOrchestrationExecutionFile>(File.ReadAllText(RoomOrchestrationExecutionPath))
                    ?? throw new InvalidDataException("编排接续记录为空");
                _roomOrchestrationCutoff = saved.Cutoff;
                _previousRoomOrchestrationCutoff = saved.PreviousCutoff;
                foreach (var record in saved.Records ?? [])
                {
                    if (record.Source is null || record.Policy?.RoomSwitch is not { IsValid: true }
                        || record.Epoch is not { ProcessId: > 0, StartTicksUtc: > 0 } || record.StopVersion < 0
                        || string.IsNullOrWhiteSpace(record.IntentId) || !record.IntentId.StartsWith(RoomOrchestrationIntentPrefix, StringComparison.Ordinal))
                        throw new InvalidDataException("编排接续记录包含无法确认的身份");
                    // Uncommitted operations are interrupted, even if the assistant crashed between
                    // the room's ready reply and the local profile commit. Existing room-switch
                    // recovery owns that transaction, not this worker.
                    var switchState = record.SwitchState is "waiting" or "switching" ? "unknown" : record.SwitchState;
                    var postState = record.PostState is "pending" or "queued" or "running" ? "unknown" : record.PostState;
                    _roomOrchestrationExecutions.Add(record.IntentId, record with
                    {
                        SwitchState = switchState, PostState = postState,
                        OnlineRevoked = record.OnlineRevoked || switchState != "switched",
                        Message = switchState == "unknown" || postState == "unknown" ? "重启后原接续状态待核对；不会重发" : record.Message
                    });
                }
                if (CurrentRoomOrchestrationCutoffLocked() is { } cutoff)
                    Volatile.Write(ref _roomSwitchCommittedAtUtc, cutoff.AtUtc);
                interrupted = _roomOrchestrationExecutions.Values.ToArray();
            }
            catch (Exception ex)
            {
                _roomOrchestrationExecutionUnreadable = true;
                _roomOrchestrationUnreadableAtUtc = DateTime.UtcNow.Ticks;
                _roomOrchestrationExecutions.Clear();
                System.Diagnostics.Trace.WriteLine("[房间编排] 原接续记录无法读取，禁用新的自动切房与其来源信号：" + ex.Message);
                return;
            }
        }
        foreach (var latest in interrupted.GroupBy(record => record.Source).Select(group => group.OrderByDescending(record => record.UpdatedAt).First()))
            PublishRoomOrchestrationTransition(latest, begin: true);
        foreach (var record in interrupted)
        {
            if (record.SwitchState == "switched" && record.SubmitAttempted
                && (record.PostState == "unknown" || !record.ResultAcknowledged && record.Policy.SpecifiedTaskType != "workflow"))
                ObserveOnlineTask(Task.Run(() => ObserveRoomOrchestrationPostAsync(record.IntentId)), "重启后核对编排后继原任务");
        }
    }

    private bool SaveRoomOrchestrationExecutionLocked()
    {
        if (_roomOrchestrationExecutionUnreadable) return false;
        try
        {
            // Keep latest records for display, signal ownership and crash deduplication; never a work queue.
            foreach (var key in _roomOrchestrationExecutions.Values.OrderByDescending(x => x.UpdatedAt).Skip(20)
                .Where(x => x.IntentId != _pendingRoomOrchestration?.Id && (x.OnlineRevoked || x.UpdatedAt < DateTimeOffset.UtcNow.AddMinutes(-2)))
                .Select(x => x.IntentId).ToArray()) _roomOrchestrationExecutions.Remove(key);
            if (LocalPeerSyncService.WriteFileAtomic(RoomOrchestrationExecutionPath,
                JsonSerializer.Serialize(new RoomOrchestrationExecutionFile(_roomOrchestrationCutoff, _roomOrchestrationExecutions.Values.ToArray(),
                    _previousRoomOrchestrationCutoff)))) return true;
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("[房间编排] 接续记录保存失败：" + ex.Message); }
        // In-memory revocation still takes effect, but no new automatic action can be authorized.
        _roomOrchestrationExecutionUnreadable = true;
        return false;
    }

    private RoomOrchestrationExecutionRecord? ReadRoomOrchestrationExecution(string id)
    {
        lock (_roomOrchestrationExecutionGate) return _roomOrchestrationExecutions.GetValueOrDefault(id);
    }

    private void PublishRoomOrchestrationTransition(RoomOrchestrationExecutionRecord record, bool begin = false)
        => RecordRoomOrchestrationTransition(new(record.Source, record.IntentId, record.Target,
            record.SwitchState, record.PostState, record.Message, record.UpdatedAt, record.JobId, record.RunId,
            record.OnlineConfirmed, record.Policy.SpecifiedTaskType, record.Policy.SpecifiedTaskName, record.BatchId, record.Generation, record.Revision), begin);

    private bool UpdateRoomOrchestrationExecution(string id, Func<RoomOrchestrationExecutionRecord, RoomOrchestrationExecutionRecord> change)
    {
        RoomOrchestrationExecutionRecord record;
        bool saved;
        lock (_roomOrchestrationExecutionGate)
        {
            if (!_roomOrchestrationExecutions.TryGetValue(id, out var old)) return false;
            record = change(old);
            if (record == old) return !_roomOrchestrationExecutionUnreadable;
            _roomOrchestrationExecutions[id] = record = record with { UpdatedAt = DateTimeOffset.UtcNow, Revision = old.Revision + 1 };
            saved = SaveRoomOrchestrationExecutionLocked();
        }
        PublishRoomOrchestrationTransition(record);
        return saved;
    }

    // Called under _batchGate, before MarkSettled; no calls to the room or BGI are made here.
    private RoomOrchestrationIntent? TryReserveRoomOrchestration(OnlineHoeingBatch batch)
    {
        if (batch.Policy?.RoomSwitch is not { } target || _config is not { } config
            || GetRoomOrchestrationKey(config.ActiveRoomProfile) is not { } source || source.PlayerUid != batch.SelfUid
            || _commandExecutor is not { } owner || batch.ControlContext != CurrentControlContext()
            || GetAssistantRoomIdentity() is not { } identity || IsStandaloneMode
            || !IsExecutorMode || _disposing || !batch.TryClaimRoomCompletion()) return null;
        var intentId = RoomOrchestrationIntentPrefix + batch.BatchId;
        var record = new RoomOrchestrationExecutionRecord(source, batch.BatchId, batch.Generation, intentId,
            batch.Policy, batch.ExecutionEpoch!, batch.StateContinuationStopVersion!.Value, batch.CompletionIntentTimestamp,
            batch.RoomReleaseBoundaryUtc, batch.ControlContext, identity);
        if (CompletionPolicyFields.ValidateRoomSwitch(config, config.ActiveRoomProfileId, batch.Policy) is { } invalid)
        {
            PublishRoomOrchestrationTransition(record with { SwitchState = "failed", PostState = target.RunAfterSwitch ? "cancelled" : "none", Message = invalid }, begin: true);
            return null;
        }
        // A peer can resend an old cleanup after a crash between durable reservation and MarkSettled.
        // The old intent's existence is a replay fence, never permission to start it again.
        var intent = new RoomOrchestrationIntent(intentId, owner);
        intent.Lifetime.CancelAfter(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        lock (_roomOrchestrationExecutionGate)
        {
            if (_roomOrchestrationExecutionUnreadable || _roomOrchestrationExecutions.ContainsKey(intentId)
                || _pendingRoomOrchestration is not null) return null;
            _pendingRoomOrchestration = intent;
            _roomOrchestrationExecutions.Add(intentId, record);
            if (!SaveRoomOrchestrationExecutionLocked())
            {
                _pendingRoomOrchestration = null;
                _roomOrchestrationExecutions[intentId] = record = record with
                { SwitchState = "failed", PostState = target.RunAfterSwitch ? "cancelled" : "none", Message = "编排接续身份未能保存，未切房" };
                intent = null;
            }
        }
        PublishRoomOrchestrationTransition(record, begin: true);
        return intent;
    }

    private void NoteRoomOrchestrationNewIntent(string reason)
        => CancelRoomOrchestrationPending(reason, revokeOnline: false);

    private bool CancelRoomOrchestrationPending(string reason, bool revokeOnline = false, bool revokeBatch = true)
    {
        if (revokeBatch)
        {
            lock (_batchGate)
                if (_activeBatch is { LocalCompletionReady: true } batch) batch.RevokeRoomCompletion();
        }
        Task? cancellation = null;
        lock (_roomOrchestrationExecutionGate)
        {
            if (_pendingRoomOrchestration is { } pending)
            {
                pending.CancellationReason = reason;
                // Publish cancellation while the same pending identity is still under the lock.
                // CancelAsync schedules callbacks instead of running them under this lock.
                cancellation = pending.Lifetime.CancelAsync();
            }
        }
        if (cancellation is not null) ObserveOnlineTask(cancellation, "撤销尚未提交的编排接续");
        if (revokeOnline) RevokeRoomOrchestrationOnline(reason);
        return cancellation is not null;
    }

    private static bool IsRoomOrchestrationTarget(RoomOrchestrationExecutionRecord record, AssistConfig? config, string roomCode)
        => config is not null && config.PlayerUid == record.Source.PlayerUid
            && config.ActiveRoomProfileId == record.Target.TargetProfileId && roomCode == record.Target.TargetRoomCode
            && RoomServerAddress.Same(config.ServerUrl, record.Target.TargetServerUrl) && record.Target.MatchesTarget(config);

    private bool IsRoomOrchestrationTargetCurrent(RoomOrchestrationExecutionRecord record)
        => !_disposing && IsExecutorMode && !IsStandaloneMode && GetAssistantRoomIdentity() is not null
            && IsRoomOrchestrationTarget(record, _config, RoomCode);

    private void RequireRoomOrchestrationIntent(RoomOrchestrationIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        intent.Lifetime.Token.ThrowIfCancellationRequested();
        lock (_roomOrchestrationExecutionGate)
            if (_roomOrchestrationExecutionUnreadable || !ReferenceEquals(_pendingRoomOrchestration, intent))
                throw new OperationCanceledException("原编排接续身份已撤销或未能保存", ct);
        if (_disposing || !IsExecutorMode || IsStandaloneMode || !ReferenceEquals(intent.Owner, _commandExecutor)
            || Volatile.Read(ref _ordinaryStopInProgress) != 0 || _localResetGate.CurrentCount == 0)
            throw new OperationCanceledException("本机执行环境已改变、停止或重置", ct);
    }

    private async Task VerifyRoomOrchestrationStartAsync(RoomOrchestrationIntent intent, bool afterSwitch, CancellationToken ct)
    {
        RequireRoomOrchestrationIntent(intent, ct);
        var record = ReadRoomOrchestrationExecution(intent.Id) ?? throw new OperationCanceledException("原编排记录已丢失", ct);
        var config = _config;
        if (config is null || config.PlayerUid != record.Source.PlayerUid || !record.Target.MatchesSource(config)
            || !record.Target.MatchesTarget(config)
            || (afterSwitch ? !IsRoomOrchestrationTargetCurrent(record) || record.TargetIdentity != GetAssistantRoomIdentity()
                : config.ActiveRoomProfileId != record.Source.ProfileId || !RoomServerAddress.Same(config.ServerUrl, record.Source.ServerUrl)
                  || RoomCode != record.Source.RoomCode || record.SourceContext != CurrentControlContext()
                  || record.Generation <= ReadCoordinatedResetFence()
                  || !_changingAssistantRoom && record.SourceIdentity != GetAssistantRoomIdentity()))
            throw new OperationCanceledException("已冻结的源/目标房间或控制身份已改变", ct);
        if (AssistantRoomHasBatch() || _intentLifecycle.IsOnlineReady)
            throw new OperationCanceledException("已有更新的联机批次或上线意图，未继续编排", ct);
        var ext = _externalClient;
        if (ext is not { State: BgiExternalLinkState.Ready } || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch))
            throw new OperationCanceledException("原 BGI 进程身份未确认，未继续编排", ct);
        var stop = await ext.ReadStateContinuationStopVersionAsync(record.Epoch, ct).ConfigureAwait(false);
        if (stop != record.StopVersion || !ReferenceEquals(ext, _externalClient))
            throw new OperationCanceledException("原 BGI 停止权限已撤销或不能核对", ct);
        var boundary = await intent.Owner.CaptureExecutionBoundarySnapshotAsync().WaitAsync(ct).ConfigureAwait(false);
        var jobs = await ext.QueryJobListAsync(ct).ConfigureAwait(false);
        // A new job which has already completed is still newer work, not an idle authorization.
        if (!boundary.Known || boundary.SlotOccupied || boundary.Active is not null || boundary.AutoHoeingRunning
            || boundary.ProcessId != record.Epoch.ProcessId || boundary.StartTicksUtc != record.Epoch.StartTicksUtc
            || jobs is null || !CoordinatedBatchLoop.SameEpoch(record.Epoch, jobs.Epoch)
            || jobs.Jobs.Any(job => job.IdempotencyKey != CommandExecutor.RoomOrchestrationTaskKey(record.IntentId)
                && (!BgiJobTerminalPolling.IsTerminal(job.State) || job.EnqueuedAtUtc?.ToUniversalTime().Ticks >= record.ReleaseBoundaryUtc)))
            throw new OperationCanceledException("原收尾后已有新的本机任务，或本机空闲未确认", ct);
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        if (host is not null && host.ListActiveRuns().Any(run => run.CreatedAt.UtcDateTime.Ticks >= record.ReleaseBoundaryUtc
            && !(Services.TaskCenterHost.IsRoomOrchestrationWorkflowRun(run) && run.Handoffs.Any(h => h.IntentKey == record.IntentId))))
            throw new OperationCanceledException("原批结束后已有新的计划安排，未启动旧后继", ct);
        RequireRoomOrchestrationIntent(intent, ct);
        if (afterSwitch)
        {
            var client = _signalRClient;
            if (client is not { IsControlRoomReady: true }) throw new OperationCanceledException("目标房间就绪未确认", ct);
            var team = await client.GetCurrentCoordinatedBatchAsync(ct).ConfigureAwait(false);
            if (!ReferenceEquals(client, _signalRClient) || !client.IsControlRoomReady || !IsRoomOrchestrationTargetCurrent(record)
                || record.TargetIdentity != GetAssistantRoomIdentity() || AssistantRoomHasBatch() || _intentLifecycle.IsOnlineReady
                || team is { CleanupComplete: false } && team.ParticipantUids.Contains(record.Source.PlayerUid, StringComparer.Ordinal))
                throw new OperationCanceledException("目标房间已有或正在受理联机任务，未启动旧后继", ct);
        }
        RequireRoomOrchestrationIntent(intent, ct);
    }

    private async Task ExecuteRoomOrchestrationAsync(RoomOrchestrationIntent intent)
    {
        var ct = intent.Lifetime.Token;
        var observe = false;
        try
        {
            var record = ReadRoomOrchestrationExecution(intent.Id)!;
            await VerifyRoomOrchestrationStartAsync(intent, afterSwitch: false, ct);
            var result = await SwitchAssistantRoomCoreAsync(record.Target.TargetProfileId,
                token => VerifyRoomOrchestrationStartAsync(intent, afterSwitch: false, token),
                record.SourceIdentity, ct, waitForOnlineRound: true, orchestration: intent);
            if (!result.Success)
            {
                UpdateRoomOrchestrationExecution(intent.Id, old => old with
                {
                    SwitchState = result.Unknown ? "unknown" : ct.IsCancellationRequested ? "cancelled" : "failed",
                    PostState = old.Target.RunAfterSwitch ? "cancelled" : "none", OnlineRevoked = true,
                    Message = ct.IsCancellationRequested && !result.Unknown ? intent.CancellationReason : result.Message
                });
                return;
            }
            if (!record.Target.RunAfterSwitch) return;
            // Only the short handoff (not the successor's lifetime) is mutually exclusive with
            // newly arriving coordinated work. The new room may keep/stop an accepted task normally.
            await _roomOrchestrationSubmitGate.WaitAsync(ct);
            try
            {
                using var submitBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                submitBudget.CancelAfter(TimeSpan.FromSeconds(45));
                var token = submitBudget.Token;
                await VerifyRoomOrchestrationStartAsync(intent, afterSwitch: true, token);
                var committed = ReadRoomOrchestrationExecution(intent.Id)!;
                if (committed.SwitchState != "switched" || committed.TargetIdentity != result.Identity)
                    throw new OperationCanceledException("目标房间提交结果未确认，未启动后继", token);
                if (!UpdateRoomOrchestrationExecution(intent.Id, old => old with { SubmitAttempted = true, Message = "目标房间已就绪，正在申请后继任务" }))
                    throw new OperationCanceledException("启动身份未保存，未发送后继", token);
                observe = true;
                var launch = new RoomOrchestrationLaunch(intent.Id, committed.Policy.PostSwitchTask(), committed.Epoch,
                    committed.StopVersion, committed.IntentTimestamp, committed.TargetIdentity!);
                Task Verify(CancellationToken check) => VerifyRoomOrchestrationStartAsync(intent, afterSwitch: true, check);
                var reply = launch.Policy.SpecifiedTaskType == "workflow"
                    ? await TaskCenterHost.StartRoomOrchestrationWorkflowAsync(launch, Verify, token)
                    : await intent.Owner.StartRoomOrchestrationTaskAsync(launch, Verify, token);
                if (reply.JobId is { Length: > 0 } job && reply.TargetProcessId == committed.Epoch.ProcessId
                    && long.TryParse(reply.TargetStartTicksUtc, out var ticks) && ticks == committed.Epoch.StartTicksUtc)
                {
                    UpdateRoomOrchestrationExecution(intent.Id, old => old with
                    {
                        JobId = launch.Policy.SpecifiedTaskType == "workflow" ? null : job,
                        RunId = launch.Policy.SpecifiedTaskType == "workflow" ? job : null,
                        PostState = reply.Status == "success" ? "queued" : "unknown", Message = reply.Message
                    });
                }
                else
                {
                    // The launcher distinguishes definite no-send/rejection from an unconfirmed
                    // response; uncertain sends are observed only by the frozen key, never retried.
                    var unknown = reply.ErrorCode is null or "result_unknown" or "state_unavailable"
                        || reply.ResponsibilityState == ResponsibilityState.Pending;
                    var cancelled = reply.ErrorCode is "manual_stop_fence" or "manual_stop_cooldown" or "start_cancelled" or "completion_not_authorized";
                    UpdateRoomOrchestrationExecution(intent.Id, old => old with
                    {
                        PostState = unknown ? "unknown" : cancelled ? "cancelled" : "failed", Message = reply.Message,
                        OnlineRevoked = old.OnlineRevoked || !unknown
                    });
                    observe = unknown;
                }
            }
            finally { _roomOrchestrationSubmitGate.Release(); }
        }
        catch (Exception ex)
        {
            var stopped = ct.IsCancellationRequested || ex is OperationCanceledException;
            UpdateRoomOrchestrationExecution(intent.Id, old => old.SwitchState == "switched"
                ? old with { PostState = old.SubmitAttempted ? "unknown" : stopped ? "cancelled" : "failed",
                    OnlineRevoked = old.OnlineRevoked || !old.SubmitAttempted, Message = ct.IsCancellationRequested ? intent.CancellationReason : ex.Message }
                : old with { SwitchState = stopped ? "cancelled" : "failed", PostState = old.Target.RunAfterSwitch ? "cancelled" : "none",
                    OnlineRevoked = true, Message = ct.IsCancellationRequested ? intent.CancellationReason : ex.Message });
            observe = ReadRoomOrchestrationExecution(intent.Id)?.SubmitAttempted == true;
        }
        finally
        {
            lock (_roomOrchestrationExecutionGate)
                if (ReferenceEquals(_pendingRoomOrchestration, intent)) _pendingRoomOrchestration = null;
            if (observe) ObserveOnlineTask(ObserveRoomOrchestrationPostAsync(intent.Id), "核对独立编排后继");
        }
    }

    // Save the epoch/time high-water BEFORE writing the newly active room profile. If the
    // assistant dies immediately after that profile commit, BGI's old online snapshot is rejected.
    private void PrepareRoomOrchestrationCutover(RoomOrchestrationIntent intent, string targetIdentity)
    {
        var record = ReadRoomOrchestrationExecution(intent.Id) ?? throw new IOException("原编排切换记录不可读");
        if (!RoomServerAddress.TryNormalize(record.Target.TargetServerUrl, out var server)) throw new IOException("目标服务器无效");
        var now = DateTime.UtcNow.Ticks;
        lock (_roomOrchestrationExecutionGate)
        {
            // The existing room remains authoritative until the target profile is committed.
            // Keep its old cutoff if target ready succeeded but the local save later fails.
            _previousRoomOrchestrationCutoff = CurrentRoomOrchestrationCutoffLocked();
            _roomOrchestrationCutoff = new(new(record.Target.TargetProfileId, server, record.Target.TargetRoomCode,
                record.Source.PlayerUid), record.Epoch, now, _intentLifecycle.CurrentOnlineGeneration ?? 0);
        }
        if (!UpdateRoomOrchestrationExecution(intent.Id, old => old with
            { TargetIdentity = targetIdentity, CommittedAtUtc = now, SwitchState = "switching", Message = "目标房间业务确认后正在提交本机档案" }))
            throw new IOException("切房信号截止未能保存，未提交本机档案");
    }

    private void ConfirmRoomOrchestrationCutover(RoomOrchestrationIntent intent)
    {
        if (!UpdateRoomOrchestrationExecution(intent.Id, old => old with
            { SwitchState = "switched", PostState = old.Target.RunAfterSwitch ? "pending" : "none",
                Message = old.Target.RunAfterSwitch ? "目标房间已就绪，等待后继启动" : "目标房间已就绪，待命（未自动上线）" }))
        {
            intent.CancellationReason = "房间已切换，独立后继记录未保存，未启动";
            ObserveOnlineTask(intent.Lifetime.CancelAsync(), "取消未保存的编排后继");
        }
    }

    // Used before ApplyEdge for BOTH the event and snapshot paths. The ordinary in-session cut-off
    // is still applied by the caller. An old persisted BGI generation may not create a new intent.
    private bool SuppressPersistedRoomOrchestrationSignal(int gen, DateTime? at, BgiEpoch? epoch)
    {
        lock (_roomOrchestrationExecutionGate)
        {
            // Damaged orchestration evidence cannot authorize an old pre-start snapshot in an unknown room;
            // freshly produced ordinary online signals remain available after initialization.
            if (_roomOrchestrationUnreadableAtUtc > 0 && (!at.HasValue || at.Value.ToUniversalTime().Ticks <= _roomOrchestrationUnreadableAtUtc))
                return true;
            return CurrentRoomOrchestrationCutoffLocked() is { } cutoff
                && (!at.HasValue || at.Value.ToUniversalTime().Ticks <= cutoff.AtUtc
                    || CoordinatedBatchLoop.SameEpoch(epoch, cutoff.Epoch) && gen <= cutoff.Generation);
        }
    }

    private static bool SameRoomOrchestrationJob(string? expected, string? actual)
        => Guid.TryParse(expected, out var a) && Guid.TryParse(actual, out var b) && a == b;

    private async Task<BgiTaskQueueStatus?> ReadRoomOrchestrationPostAsync(RoomOrchestrationExecutionRecord record, CancellationToken ct)
    {
        if (record.Policy.SpecifiedTaskType == "workflow")
        {
            TaskCenterHost? host;
            lock (_taskCenterHostGate) host = _taskCenterHost;
            return host is null ? null : await host.QueryRoomOrchestrationWorkflowAsync(record.RunId ?? "", record.IntentId, ct).ConfigureAwait(false);
        }
        var ext = _externalClient;
        if (ext is not { State: BgiExternalLinkState.Ready } || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch)) return null;
        var status = await ext.QueryTaskQueueStatusByKeyAsync(CommandExecutor.RoomOrchestrationTaskKey(record.IntentId), ct).ConfigureAwait(false);
        return ReferenceEquals(ext, _externalClient) && CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch) ? status : null;
    }

    /// <summary>A queued direct successor is not an active BGI root yet. Do not let it start late
    /// after the next coordinated batch has chosen its takeover target. Cancel only its exact retained job.</summary>
    private async Task<bool> ResolveQueuedRoomOrchestrationBeforeBatchAsync(CancellationToken ct)
    {
        RoomOrchestrationExecutionRecord[] candidates;
        lock (_roomOrchestrationExecutionGate)
            candidates = _roomOrchestrationExecutions.Values.Where(record => record.SubmitAttempted
                && record.SwitchState == "switched" && record.Policy.SpecifiedTaskType != "workflow"
                && record.PostState is not ("completed" or "failed" or "cancelled")
                && IsRoomOrchestrationTarget(record, _config, RoomCode)).ToArray();
        foreach (var record in candidates)
        {
            if (_externalClient is not { State: BgiExternalLinkState.Ready } ext
                || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch))
            {
                // A confirmed exited process cannot own a queued task in the replacement BGI.
                // Its historical result remains unknown; never treat exit as task success or replay it.
                if (CommandExecutor.ProbeOriginalBgiProcess(record.Epoch) != false) return false;
                UpdateRoomOrchestrationExecution(record.IntentId, old => old with
                { PostState = "unknown", OnlineRevoked = true, Message = "原后继执行进程已退出，结果未确认；不会重发" });
                RevokeRoomOrchestrationOnline("原后继执行进程已退出", onlyId: record.IntentId);
                continue;
            }
            var key = CommandExecutor.RoomOrchestrationTaskKey(record.IntentId);
            var status = await ext.QueryTaskQueueStatusByKeyAsync(key, ct);
            if (status is null || !ReferenceEquals(ext, _externalClient) || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch)) return false;
            if (status.Status == "not_found" && (record.JobId is null
                || ReadRoomOrchestrationExecution(record.IntentId) is { PostState: "completed" or "failed" or "cancelled" })) continue;
            if (status.IdempotencyKey != key || !CoordinatedBatchLoop.SameEpoch(record.Epoch, status.Epoch)
                || status.TaskHandle is not { Length: > 0 } handle || record.JobId is { } accepted && !SameRoomOrchestrationJob(accepted, handle)) return false;
            if (status.Status is "running" or "completing") continue; // Already active: normal takeover owns it.
            if (status.ExecutionExitConfirmed && status.Status is "completed" or "failed" or "queueCancelled") continue;
            if (status.Status is not ("pending" or "queued")) return false;
            if (!(await ext.CancelOwnedTaskAsync(handle, ct)).Success) return false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(ext, _externalClient) || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch)) return false;
                var ended = await ext.QueryTaskQueueStatusByKeyAsync(key, ct);
                if (ended?.Status == "not_found" && ReadRoomOrchestrationExecution(record.IntentId) is { PostState: "completed" or "failed" or "cancelled" } terminal
                    && SameRoomOrchestrationJob(handle, terminal.JobId)) break;
                if (ended is { ExecutionExitConfirmed: true } && ended.IdempotencyKey == key
                    && SameRoomOrchestrationJob(handle, ended.TaskHandle) && CoordinatedBatchLoop.SameEpoch(record.Epoch, ended.Epoch)
                    && ended.Status is "completed" or "failed" or "queueCancelled") break;
                await Task.Delay(200, ct);
            }
        }
        return true;
    }

    private async Task ObserveRoomOrchestrationPostAsync(string id)
    {
        DateTimeOffset? unavailableSince = null;
        while (!_disposing && ReadRoomOrchestrationExecution(id) is { } record && record.SubmitAttempted)
        {
            using var call = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var workflow = record.Policy.SpecifiedTaskType == "workflow";
                var expected = workflow ? record.IntentId : CommandExecutor.RoomOrchestrationTaskKey(record.IntentId);
                var status = await ReadRoomOrchestrationPostAsync(record, call.Token);
                var exact = status is { TaskHandle.Length: > 0 } && status.IdempotencyKey == expected
                    && CoordinatedBatchLoop.SameEpoch(record.Epoch, status.Epoch)
                    && (workflow ? record.RunId is null || record.RunId == status.TaskHandle
                        : record.JobId is null || SameRoomOrchestrationJob(record.JobId, status.TaskHandle));
                if (exact)
                {
                    var terminal = status!.ExecutionExitConfirmed && status.Status is "completed" or "failed" or "queueCancelled";
                    var postState = terminal ? status.Cancelled || status.Status == "queueCancelled" ? "cancelled"
                        : status.Status == "completed" ? "completed" : "failed"
                        : status.Status is "running" or "completing" ? "running" : status.Status is "pending" or "queued" ? "queued"
                        : status.Status == "waiting" ? "pending" : "unknown";
                    var detail = postState switch
                    {
                        "completed" => "目标房间后继任务正常完成",
                        "cancelled" => "目标房间后继任务已取消",
                        "failed" => "目标房间后继任务失败：" + (status.ErrorMessage ?? status.ErrorCode),
                        "running" => "目标房间后继任务执行中",
                        "queued" => "目标房间后继任务已受理，等待执行",
                        "pending" => "目标房间后继任务等待中",
                        _ => "原后继任务状态待核对"
                    };
                    var saved = UpdateRoomOrchestrationExecution(id, old => old with
                    {
                        JobId = workflow ? null : status.TaskHandle, RunId = workflow ? status.TaskHandle : null,
                        WireRunId = workflow ? status.WorkflowRunId : null,
                        PostState = postState, Message = detail,
                        OnlineRevoked = old.OnlineRevoked || postState is "failed" or "cancelled" || status.StopRequested,
                        ResultAcknowledged = old.ResultAcknowledged || workflow && terminal
                    });
                    if (postState is "failed" or "cancelled" || status.StopRequested) RevokeRoomOrchestrationOnline(detail, onlyId: id);
                    if (terminal && (workflow || record.ResultAcknowledged)) return;
                    var reconciled = workflow || await TaskCenterHost.ReconcileRoomOrchestrationTaskAsync(id, record.Epoch, status, call.Token);
                    if (terminal && saved && reconciled && status.Epoch is { } epoch && _externalClient is { State: BgiExternalLinkState.Ready } ext
                        && CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch)
                        && await ext.AcknowledgeTaskResultAsync(expected, status.TaskHandle!, epoch, call.Token))
                    {
                        UpdateRoomOrchestrationExecution(id, old => old with { ResultAcknowledged = true });
                        return;
                    }
                    unavailableSince = null;
                }
                else if (record.PostState is not ("completed" or "failed" or "cancelled"))
                {
                    UpdateRoomOrchestrationExecution(id, old => old with { PostState = "unknown", Message = "原后继任务身份或状态暂未确认；未重复提交" });
                    unavailableSince ??= DateTimeOffset.UtcNow;
                }
                if (record.OnlineGeneration != 0 && !record.OnlineRevoked
                    && _externalClient is { State: BgiExternalLinkState.Ready } peer && CoordinatedBatchLoop.SameEpoch(peer.ServerEpoch, record.Epoch)
                    && await peer.ReadStateContinuationStopVersionAsync(record.Epoch, call.Token) is { } stop && stop != record.StopVersion)
                    RevokeRoomOrchestrationOnline("后继任务停止授权已撤销", onlyId: id);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or JsonException)
            {
                unavailableSince ??= DateTimeOffset.UtcNow;
                if (record.PostState is not ("completed" or "failed" or "cancelled"))
                    UpdateRoomOrchestrationExecution(id, old => old with { PostState = "unknown", Message = "原后继任务状态暂不可读；未重新提交" });
            }
            if (unavailableSince is { } since && DateTimeOffset.UtcNow - since > TimeSpan.FromMinutes(2)) return;
            if (record.PostState is "completed" or "failed" or "cancelled" && record.ResultAcknowledged) return;
            await Task.Delay(1500).ConfigureAwait(false);
        }
    }

    // Find a successor by the actual BGI submission or workflow identity, including the race
    // where its first online marker arrives before the launcher's receipt reaches this assistant.
    private bool TryResolveRoomOrchestrationSignal(string? submissionKey, string? workflowRunId, out string? intentId)
    {
        intentId = null;
        if (submissionKey?.StartsWith("room-orchestration/", StringComparison.Ordinal) == true)
        {
            intentId = submissionKey["room-orchestration/".Length..];
            return true;
        }
        if (string.IsNullOrWhiteSpace(workflowRunId)) return false;
        lock (_roomOrchestrationExecutionGate)
        {
            if (_roomOrchestrationExecutions.Values.FirstOrDefault(record => SameRoomOrchestrationJob(record.WireRunId, workflowRunId)) is { } known)
            { intentId = known.IntentId; return true; }
        }
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        var run = host?.Runs.List().FirstOrDefault(item => SameRoomOrchestrationJob(item.WireRunId, workflowRunId));
        if (run is not null && Services.TaskCenterHost.IsRoomOrchestrationWorkflowRun(run))
        {
            intentId = run.Handoffs.Select(h => h.IntentKey).FirstOrDefault(key => key.StartsWith(RoomOrchestrationIntentPrefix, StringComparison.Ordinal));
            return true; // Known compiled successor without its local record must not fall through.
        }
        return host?.IsRoomOrchestrationWorkflowSignal(workflowRunId) == true;
    }

    private async Task<bool> VerifyRoomOrchestrationMailboxSignalAsync(string id, string? workflowRunId, string? jobId,
        string? submissionKey, long stopVersion, BgiEpoch? epoch, string kind, string? targetName, CancellationToken ct)
    {
        var record = ReadRoomOrchestrationExecution(id);
        if (record is null || _roomOrchestrationExecutionUnreadable || jobId is not { Length: > 0 }
            || submissionKey is not { Length: > 0 } || !CoordinatedBatchLoop.SameEpoch(epoch, record.Epoch)
            || stopVersion != record.StopVersion || !_config!.RoomProfiles.Any(profile => profile.Id == record.Target.TargetProfileId
                && (kind == "online" || string.Equals(profile.Name, targetName, StringComparison.OrdinalIgnoreCase)))) return false;
        return await VerifyRoomOrchestrationSignalIdentityAsync(record, workflowRunId, jobId, submissionKey, ct);
    }

    private async Task<bool> ReportRoomOrchestrationMailboxOnlineAsync(string id, string? workflowRunId, string jobId,
        string key, long stopVersion, BgiEpoch? epoch, DateTime receivedAt, CancellationToken ct)
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null) return false;
        await _roomOrchestrationOnlineGate.WaitAsync(ct);
        try
        {
            var record = ReadRoomOrchestrationExecution(id);
            if (record is null || !ValidRoomOrchestrationSignalTime(record, receivedAt.Ticks) || AssistantRoomHasBatch()
                || !await VerifyRoomOrchestrationMailboxSignalAsync(id, workflowRunId, jobId, key, stopVersion, epoch, "online", null, ct)) return false;
            var gen = _intentLifecycle.RaiseLocalIntent("command", id, CurrentControlContext());
            _workflowOnlineOrigin = null;
            if (!BindRoomOrchestrationOnline(id, gen, receivedAt.Ticks, workflowRunId, jobId, key)) return false;
            _intentLifecycle.NoteReportPending(gen);
            return await ReportOnlineEventTrackedAsync(gen, id);
        }
        finally { _roomOrchestrationOnlineGate.Release(); }
    }

    private bool HandleRoomOrchestrationOnlineSignal(int gen, DateTime? triggeredAtUtc, string? workflowRunId,
        string? jobId, string? submissionKey, long? stopVersion, BgiEpoch? epoch)
    {
        if (!TryResolveRoomOrchestrationSignal(submissionKey, workflowRunId, out var id)) return false;
        if (id is not null && gen > 0 && triggeredAtUtc is { } at && jobId is { Length: > 0 }
            && submissionKey is { Length: > 0 } && stopVersion is { } version && epoch is not null)
            ObserveOnlineTask(ProcessRoomOrchestrationOnlineSignalAsync(id, gen, at.ToUniversalTime().Ticks,
                workflowRunId, jobId, submissionKey, version, epoch), "核对编排后继上线信号");
        return true; // Never fall through to the ordinary online path with partial successor metadata.
    }

    private async Task ProcessRoomOrchestrationOnlineSignalAsync(string id, int gen, long atUtc, string? workflowRunId,
        string jobId, string submissionKey, long stopVersion, BgiEpoch epoch)
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null) return;
        await _roomOrchestrationOnlineGate.WaitAsync();
        try
        {
            var record = ReadRoomOrchestrationExecution(id);
            if (record is null || gen <= record.OnlineGeneration || _roomOrchestrationExecutionUnreadable
                || stopVersion != record.StopVersion || !CoordinatedBatchLoop.SameEpoch(epoch, record.Epoch)
                || !ValidRoomOrchestrationSignalTime(record, atUtc)) return;
            using var call = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await VerifyRoomOrchestrationSignalIdentityAsync(record, workflowRunId, jobId, submissionKey, call.Token)) return;
            if (ReadRoomOrchestrationExecution(id) is not { OnlineRevoked: false }) return;
            var edge = _intentLifecycle.ApplyEdge(gen, new DateTime(atUtc, DateTimeKind.Utc), out var reportGen, id, CurrentControlContext());
            if (edge != OnlineEdgeResult.Accepted) return;
            if (!BindRoomOrchestrationOnline(id, reportGen, atUtc, workflowRunId, jobId, submissionKey)) return;
            _workflowOnlineOrigin = null;
            _intentLifecycle.NoteReportPending(reportGen);
            await ReportOnlineEventTrackedAsync(reportGen, id);
        }
        finally { _roomOrchestrationOnlineGate.Release(); }
    }

    private static bool ValidRoomOrchestrationSignalTime(RoomOrchestrationExecutionRecord record, long atUtc)
        => record.CommittedAtUtc > 0 && atUtc > record.CommittedAtUtc && atUtc <= DateTime.UtcNow.Ticks
            && DateTime.UtcNow.Ticks - atUtc < RoomOrchestrationSignalLifetime.Ticks;

    private bool BindRoomOrchestrationOnline(string id, int generation, long atUtc, string? workflowRunId, string jobId, string submissionKey)
    {
        var saved = UpdateRoomOrchestrationExecution(id, old => old.OnlineRevoked || generation <= old.OnlineGeneration ? old : old with
        {
            OnlineGeneration = generation, OnlineTriggeredAtUtc = atUtc, OnlineJobId = jobId,
            OnlineSubmissionKey = submissionKey, OnlineWorkflowRunId = workflowRunId, OnlineConfirmed = false,
            WireRunId = workflowRunId ?? old.WireRunId
        });
        if (saved && ReadRoomOrchestrationExecution(id) is { OnlineRevoked: false } current && current.OnlineGeneration == generation) return true;
        _intentLifecycle.RevokeArmedIntent(generation, "编排上线身份未保存或已撤销", id);
        return false;
    }

    private async Task<bool> VerifyRoomOrchestrationSignalIdentityAsync(RoomOrchestrationExecutionRecord record, string? workflowRunId,
        string jobId, string submissionKey, CancellationToken ct)
    {
        if (record.OnlineRevoked || record.SwitchState != "switched" || !record.SubmitAttempted || !IsRoomOrchestrationTargetCurrent(record)
            || record.PostState is "failed" or "cancelled" || _ordinaryStopInProgress != 0 || _localResetGate.CurrentCount == 0) return false;
        if (_externalClient is not { State: BgiExternalLinkState.Ready } ext || !CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch))
        {
            if (CommandExecutor.ProbeOriginalBgiProcess(record.Epoch) == false)
                RevokeRoomOrchestrationOnline("原后继进程已退出，编排上线授权撤销", onlyId: record.IntentId);
            return false;
        }
        var stop = await ext.ReadStateContinuationStopVersionAsync(record.Epoch, ct);
        if (stop is null) return false;
        if (stop != record.StopVersion)
        {
            RevokeRoomOrchestrationOnline("原后继停止授权已撤销", onlyId: record.IntentId);
            return false;
        }
        if (record.Policy.SpecifiedTaskType == "workflow")
        {
            var status = await TaskCenterHost.QueryRoomOrchestrationWorkflowAsync(record.RunId ?? "", record.IntentId, ct);
            if (status is not { TaskHandle.Length: > 0, StopRequested: false } || status.IdempotencyKey != record.IntentId
                || !SameRoomOrchestrationJob(status.WorkflowRunId, workflowRunId) || status.RoomIdentity != record.TargetIdentity
                || status.StopVersion != record.StopVersion
                || !CoordinatedBatchLoop.SameEpoch(record.Epoch, status.Epoch)
                || status.Cancelled || status.Status is "failed" or "queueCancelled") return false;
            var run = TaskCenterHost.Runs.Load(status.TaskHandle);
            if (run is null || run.StopRequested) return false;
            bool MatchesSubmission(WorkflowSubmission? submission) => submission is not null
                && submission.Key == submissionKey && SameRoomOrchestrationJob(submission.JobId, jobId)
                && submission.Epoch == WorkflowStopAuthority.Epoch(record.Epoch);
            if (!MatchesSubmission(run.CurrentSubmission) && !run.SubmissionHistory.Any(MatchesSubmission)) return false;
        }
        else
        {
            if (workflowRunId is not null || submissionKey != CommandExecutor.RoomOrchestrationTaskKey(record.IntentId)) return false;
            if (record.JobId is { } accepted && !SameRoomOrchestrationJob(accepted, jobId)) return false;
            if (record.PostState != "completed" || record.JobId is null)
            {
                var status = await ext.QueryTaskQueueStatusByKeyAsync(submissionKey, ct);
                if (status is null || status.IdempotencyKey != submissionKey || !SameRoomOrchestrationJob(status.TaskHandle, jobId)
                    || !CoordinatedBatchLoop.SameEpoch(record.Epoch, status.Epoch) || status.Cancelled
                    || status.Status is not ("pending" or "running" or "completed")) return false;
            }
        }
        return IsRoomOrchestrationTargetCurrent(record) && ReferenceEquals(ext, _externalClient)
            && CoordinatedBatchLoop.SameEpoch(record.Epoch, ext.ServerEpoch)
            && ReadRoomOrchestrationExecution(record.IntentId) is { OnlineRevoked: false, PostState: not ("failed" or "cancelled") };
    }

    private OnlineIntentLifecycle.OrchestrationOrigin? GetRoomOrchestrationOnlineOrigin(int generation)
    {
        var origin = _intentLifecycle.CurrentOrchestrationOrigin;
        return origin is not null && origin.Generation == generation ? origin : null;
    }

    private async Task RetractRoomOrchestrationOnlineReportAsync(int generation, SignalRClient originalClient, string intentId)
    {
        var record = ReadRoomOrchestrationExecution(intentId);
        if (record is null || record.OnlineGeneration != generation || !record.OnlineRevoked || !ReferenceEquals(originalClient, _signalRClient)
            || !IsRoomOrchestrationTarget(record, _config, RoomCode)
            || _intentLifecycle.State == OnlineIntentState.Executing
            || _intentLifecycle.ArmedOnlineGeneration is not null && GetRoomOrchestrationOnlineOrigin(generation)?.IntentId != intentId) return;
        _intentLifecycle.RevokeArmedIntent(generation, "旧编排上线的迟到回执已撤销", intentId);
        await originalClient.ReportOnlineEventAsync(generation, false);
    }

    private bool IsRoomOrchestrationOnlineGeneration(int generation) => GetRoomOrchestrationOnlineOrigin(generation) is not null;

    private async Task<bool> VerifyRoomOrchestrationOnlineReportAsync(int generation, string? expectedIntentId = null)
    {
        var origin = GetRoomOrchestrationOnlineOrigin(generation);
        if (origin is null) return expectedIntentId is null;
        if (expectedIntentId is not null && origin.IntentId != expectedIntentId) return false;
        if (origin.ControlContext != CurrentControlContext())
        {
            RevokeRoomOrchestrationOnline("原编排上线控制身份已变化", onlyId: origin.IntentId);
            return false;
        }
        var record = ReadRoomOrchestrationExecution(origin.IntentId);
        if (record is null || record.OnlineGeneration != generation) return false;
        if (record.OnlineRevoked || record.PostState is "failed" or "cancelled"
            || !record.OnlineConfirmed && !ValidRoomOrchestrationSignalTime(record, record.OnlineTriggeredAtUtc))
        {
            RevokeRoomOrchestrationOnline("上线信号原授权已撤销或过期", onlyId: record.IntentId);
            return false;
        }
        try
        {
            using var call = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return !_roomOrchestrationExecutionUnreadable && record.OnlineJobId is { } job && record.OnlineSubmissionKey is { } key
                && await VerifyRoomOrchestrationSignalIdentityAsync(record, record.OnlineWorkflowRunId, job, key, call.Token)
                && GetRoomOrchestrationOnlineOrigin(generation) == origin;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or JsonException)
        { return false; }
    }

    private void NoteRoomOrchestrationOnlineReported(int generation, string? expectedIntentId = null)
    {
        var origin = GetRoomOrchestrationOnlineOrigin(generation);
        if (origin is not null && (expectedIntentId is null || origin.IntentId == expectedIntentId))
            UpdateRoomOrchestrationExecution(origin.IntentId, old => old.OnlineRevoked || old.OnlineGeneration != generation
                ? old : old with { OnlineConfirmed = true });
    }

    private void RevokeRoomOrchestrationOnline(string reason, string? onlyId = null, RoomOrchestrationRoomKey? departed = null)
    {
        RoomOrchestrationExecutionRecord[] records;
        lock (_roomOrchestrationExecutionGate)
            records = _roomOrchestrationExecutions.Values.Where(record => (onlyId is null || record.IntentId == onlyId)
                && (departed is null || record.Target.TargetProfileId == departed.ProfileId
                    && RoomServerAddress.Same(record.Target.TargetServerUrl, departed.ServerUrl)
                    && record.Target.TargetRoomCode == departed.RoomCode && record.Source.PlayerUid == departed.PlayerUid)).ToArray();
        foreach (var record in records)
        {
            UpdateRoomOrchestrationExecution(record.IntentId, old => old with { OnlineRevoked = true });
            // Only the exact still-armed generation is ours to withdraw. A new independent online
            // intent or an admitted batch remains on its original lifecycle.
            if (record.OnlineGeneration > 0 && IsRoomOrchestrationTarget(record, _config, RoomCode)
                && GetRoomOrchestrationOnlineOrigin(record.OnlineGeneration)?.IntentId == record.IntentId
                && _intentLifecycle.RevokeArmedIntent(record.OnlineGeneration, reason, record.IntentId) && _signalRClient is { } client)
                ObserveOnlineTask(client.ReportOnlineEventAsync(record.OnlineGeneration, false), "撤回被撤销的编排上线");
        }
    }
}
