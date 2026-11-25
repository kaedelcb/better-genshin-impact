using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private readonly object _batchGate = new();
    private readonly Dictionary<string, CoordinatedBatchState> _controlBatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoordinatedBatchResetResult> _processedResetResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _resetCleanupExecutors = new(StringComparer.Ordinal);

    private static int _nextBatchGeneration = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly Dictionary<string, CoordinatedBatchState> _batchRequests = new(StringComparer.Ordinal);

    private bool ReleaseOfflineCompletionWaits(string group, CoordinatedBatchState state)
    {
        if (state.Phase != "completed") return false;
        var revision = state.Revision;
        var changed = false;
        foreach (var uid in state.ParticipantUids)
            changed |= _roomManager.ReleaseOfflineCompletionWait(group, uid, state.BatchId,
                () => state.ReleaseOfflineCompletionWait(uid));
        return changed || state.Revision != revision;
    }

    public CoordinatedBatchSnapshot? CurrentCoordinatedBatch(GatewayHandlerContext ctx, string? batchId = null)
    {
        lock (_batchGate)
        {
            var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            if (!_controlBatches.TryGetValue(group, out var state)) return null;
            if (!string.IsNullOrEmpty(batchId) && state.BatchId != batchId)
                throw new InvalidOperationException("batch_not_found");
            return state.Snapshot();
        }
    }

    public async Task<CoordinatedBatchSnapshot> BeginManualCoordinatedBatchAsync(GatewayHandlerContext ctx,
        string requestId, IReadOnlyList<string> targetUids, DateTimeOffset? expiresAtUtc,
        bool useCompletionActions = false, string quickCommandKey = "",
        IReadOnlyDictionary<string, CoordinatedCompletionAction>? confirmedCompletionActions = null)
    {
        if (quickCommandKey is not ("" or "一键传奇" or "一键次数盾" or "一键精英" or "一键小怪" or "一键自定义"))
            throw new InvalidOperationException("batch_quick_command_key");
        if (_capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
            throw new InvalidOperationException("hoeing_start_capability_required");
        if (useCompletionActions && _capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.CompletionChoiceCapability) != true)
            throw new InvalidOperationException("hoeing_completion_choice_capability_required");
        if (quickCommandKey.Length > 0
            && _capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.QuickCommandCompletionCapability) != true)
            throw new InvalidOperationException("hoeing_quick_command_completion_capability_required");
        string group;
        CoordinatedBatchState batch;
        bool created;
        lock (_batchGate)
        {
            group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            var senderUid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId)
                ?? _roomManager.GetRemoteUidByConnectionId(group, ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_identity_required");
            _roomManager.ExpireOnlineIntents(group, _roomManager.ControlUtcNow);
            batch = BeginCoordinatedBatch(group, "manual", senderUid + "\0" + requestId,
                requestId, targetUids, null, expiresAtUtc, out created, useCompletionActions, quickCommandKey,
                confirmedCompletionActions);
            if (created) _roomManager.DisarmOnlineEvents(group, batch.ParticipantUids);
        }
        if (created) await PublishBatchStartAsync(group, batch);
        lock (_batchGate) return batch.Snapshot();
    }

    // Cancelling an accepted Begin is not a physical reset. Only the original sender's
    // exact request may be revoked; registered executors still owe their real cleanup.
    public async Task<CoordinatedBatchSnapshot> CancelCoordinatedBeginAsync(GatewayHandlerContext ctx,
        string requestId, string batchId, int generation)
    {
        string group;
        CoordinatedBatchState state;
        CoordinatedBatchSnapshot snapshot;
        lock (_batchGate)
        {
            group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            var senderUid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId)
                ?? _roomManager.GetRemoteUidByConnectionId(group, ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_identity_required");
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128
                || !_batchRequests.TryGetValue(group + "\0manual\0" + senderUid + "\0" + requestId, out state!)
                || state.BatchId != batchId || state.Generation != generation)
                throw new InvalidOperationException("batch_request_identity_mismatch");
            // A replay is answered from its original request, never from a successor.
            if (!_controlBatches.TryGetValue(group, out var current) || !ReferenceEquals(current, state)
                || state.IsTerminal) return state.Snapshot();
            state.CancelBegin();
            snapshot = state.Snapshot();
            _roomManager.DisarmOnlineEvents(group, state.ParticipantUids);
            _roomManager.MarkExhausted(group);
        }
        await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchState);
        await BroadcastControlRoomPlayersAsync(group, forceFull: true);
        return snapshot;
    }

    // 只负责受理。拒绝新请求不得修改旧批次，包括尚未完成清理的终态批次。
    private CoordinatedBatchState BeginCoordinatedBatch(string group, string source, string requestKey,
        string requestId, IReadOnlyList<string> uids, IReadOnlyDictionary<string, int>? intentGenerations,
        DateTimeOffset? expiresAtUtc, out bool created, bool useCompletionActions = false, string quickCommandKey = "",
        IReadOnlyDictionary<string, CoordinatedCompletionAction>? confirmedCompletionActions = null)
    {
        created = false;
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
            throw new InvalidOperationException("batch_request_id");
        if (uids.Count is < 1 or > 4 || uids.Any(string.IsNullOrWhiteSpace)
            || uids.Distinct(StringComparer.Ordinal).Count() != uids.Count)
            throw new InvalidOperationException("batch_roster");
        var key = group + "\0" + source + "\0" + requestKey;
        if (_batchRequests.TryGetValue(key, out var repeated))
        {
            if (!repeated.ParticipantUids.Order(StringComparer.Ordinal).SequenceEqual(uids.Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("batch_request_identity_mismatch");
            var prior = repeated.Snapshot();
            if (prior.UseCompletionActions != useCompletionActions || prior.QuickCommandKey != quickCommandKey)
                throw new InvalidOperationException("batch_request_identity_mismatch");
            if (source == "manual" && useCompletionActions)
                RequireConfirmedRoomSwitchActions(uids, prior.ConfirmedCompletionActions, confirmedCompletionActions);
            return repeated;
        }
        if (expiresAtUtc is { } expiry && expiry <= _roomManager.ControlUtcNow)
            throw new InvalidOperationException("request_expired");
        if (_controlBatches.Any(pair => pair.Key != group && !pair.Value.IsTerminal && uids.Any(pair.Value.HasParticipant)))
            throw new InvalidOperationException("participant_batch_active_in_another_room");
        if (_controlBatches.TryGetValue(group, out var old))
        {
            if (!old.IsTerminal) throw new InvalidOperationException("previous_batch_active");
        }
        if (!_controlBatches.ContainsKey(group) && _controlBatches.Count >= 4096)
            throw new InvalidOperationException("batch_capacity");
        if (_batchRequests.Count >= 16384) throw new InvalidOperationException("batch_request_capacity");
        // Only hoeing admission depends on the official route catalog; unrelated quick commands keep working.
        if (quickCommandKey.Length == 0) _routePackagePolicy?.RequireConfigured();
        var players = _roomManager.GetCoordinatedStartPlayers(group);
        var selected = new List<Models.ControlRoomPlayer>();
        foreach (var uid in uids)
        {
            var matches = players.Where(p => p.PlayerUid == uid && p.Online).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("batch_participant_unavailable_or_ambiguous:" + uid);
            var player = matches[0];
            // Control connections belong to the assistant, whose hello version is not the BGI version.
            if (quickCommandKey.Length == 0) _routePackagePolicy?.RequireVersion(player.BgiVersion);
            if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
                throw new InvalidOperationException("hoeing_start_capability_required:" + uid);
            if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.LocalFinishCapability) != true)
                throw new InvalidOperationException("hoeing_local_finish_capability_required:" + uid);
            if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.LocalTaskFailureCapability) != true)
                throw new InvalidOperationException("hoeing_local_task_failure_capability_required:" + uid);
            if (quickCommandKey.Length > 0)
            {
                if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.QuickCommandCompletionCapability) != true)
                    throw new InvalidOperationException("hoeing_quick_command_completion_capability_required:" + uid);
            }
            else if (player.OnlineHoeingGroupNames.Count == 0)
                throw new InvalidOperationException("batch_binding_missing:" + uid);
            if (useCompletionActions)
            {
                if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.CompletionChoiceCapability) != true)
                    throw new InvalidOperationException("hoeing_completion_choice_capability_required:" + uid);
            }
            selected.Add(player);
        }
        Dictionary<string, IReadOnlyList<CoordinatedBatchBinding>> bindings;
        if (quickCommandKey.Length > 0)
        {
            // 快捷指令允许每人各自绑定配置组或一条龙；只冻结这次所选指令，不回落到联机锄地绑定。
            bindings = selected.ToDictionary(p => p.PlayerUid,
                p => (IReadOnlyList<CoordinatedBatchBinding>)[ParseQuickCommandBinding(p, quickCommandKey)],
                StringComparer.Ordinal);
        }
        else
        {
            var bindingIssue = RoomManager.ValidateCoordinatedBindingLayout(selected);
            if (bindingIssue is not null) throw new InvalidOperationException(bindingIssue);
            bindings = selected.ToDictionary(p => p.PlayerUid,
                p => (IReadOnlyList<CoordinatedBatchBinding>)p.OnlineHoeingGroupNames.Select((name, i) =>
                    new CoordinatedBatchBinding(name, p.OnlineHoeingGroupTypes.Count == 0 ? "group" : p.OnlineHoeingGroupTypes[i])).ToArray(),
                StringComparer.Ordinal);
        }
        // 不从确认请求替执行端设置动作。以受理时的成员状态为准，冻结有效动作的摘要。
        // 自动上线沿用绑定动作，即使 UseCompletionActions 为 false；手动未勾选才不使用。
        var actions = new Dictionary<string, CoordinatedCompletionAction>(StringComparer.Ordinal);
        if (source != "manual" || useCompletionActions)
        {
            foreach (var player in selected)
            {
                var action = player.HoeingCompletionAction;
                if ((useCompletionActions || action?.Policy == "switchRoom") && action?.IsValid() != true)
                    throw new InvalidOperationException("hoeing_completion_action_invalid:" + player.PlayerUid);
                if (action != null) actions[player.PlayerUid] = action;
            }
            if (source == "manual")
                RequireConfirmedRoomSwitchActions(uids, actions, confirmedCompletionActions);
        }
        var instances = selected.ToDictionary(p => p.PlayerUid, p => p.ClientInstanceId, StringComparer.Ordinal);
        var generation = Interlocked.Increment(ref _nextBatchGeneration);
        if (generation <= 0) throw new InvalidOperationException("batch_generation_exhausted");
        var batch = new CoordinatedBatchState(generation, uids, DateTime.UtcNow, source, requestId,
            bindings, intentGenerations, instances, useCompletionActions, quickCommandKey, stateContinuation: true,
            priorityRecovery: selected.All(p => _capabilityLookup?.Supports(p.ConnectionId,
                GatewayProtocol.HoeingPriorityRecoveryCapability) == true), confirmedCompletionActions: actions);
        _controlBatches[group] = batch;
        _batchRequests[key] = batch;
        created = true;
        return batch;
    }

    private static void RequireConfirmedRoomSwitchActions(IEnumerable<string> uids,
        IReadOnlyDictionary<string, CoordinatedCompletionAction> actual,
        IReadOnlyDictionary<string, CoordinatedCompletionAction>? confirmed)
    {
        foreach (var uid in uids)
            if (!CoordinatedCompletionAction.MatchesRoomSwitchConfirmation(
                    confirmed?.GetValueOrDefault(uid), actual.GetValueOrDefault(uid)))
                throw new InvalidOperationException("hoeing_completion_action_changed:" + uid);
    }

    private static CoordinatedBatchBinding ParseQuickCommandBinding(Models.ControlRoomPlayer player, string key)
    {
        var value = player.QuickCommands.GetValueOrDefault(key) ?? "";
        var type = value.StartsWith("GROUP:", StringComparison.Ordinal) ? "group"
            : value.StartsWith("ONEDRAGON:", StringComparison.Ordinal) ? "onedragon" : "";
        var name = type == "group" ? value["GROUP:".Length..]
            : type == "onedragon" ? value["ONEDRAGON:".Length..] : "";
        if (type.Length == 0 || string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 1024)
            throw new InvalidOperationException("batch_quick_binding_invalid:" + player.PlayerUid);
        return new CoordinatedBatchBinding(name, type);
    }

    private async Task PublishBatchStartAsync(string group, CoordinatedBatchState state)
    {
        CoordinatedBatchSnapshot snapshot;
        lock (_batchGate) snapshot = state.Snapshot();
        ObserveRoomLog(context => context.RegisterBatch(group, snapshot.BatchId, snapshot.ParticipantUids));
        await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchStart);
    }

    private async Task ReplayPendingBatchStartAsync(string group, string connectionId)
    {
        if (_capabilityLookup?.Supports(connectionId, GatewayProtocol.HoeingStartCapability) != true) return;
        CoordinatedBatchSnapshot? snapshot;
        lock (_batchGate)
        {
            var player = _roomManager.GetControlRoomPlayers(group)
                .SingleOrDefault(p => p.ConnectionId == connectionId && p.Online);
            snapshot = player is not null && _controlBatches.TryGetValue(group, out var state)
                ? state.TryReplayRegistration(player.PlayerUid, player.ClientInstanceId, DateTime.UtcNow) : null;
        }

        // 状态续接保留原批次。仍未登记的执行端可以借状态上报补收一次开始通知。
        if (snapshot is not null)
            await _broadcaster.SendEventOnlyToConnectionAsync(connectionId,
                GatewayProtocol.Events.ControlBatchStart, snapshot, group[5..]);
    }

    private async Task PublishBatchSnapshotAsync(string group, CoordinatedBatchState state,
        CoordinatedBatchSnapshot snapshot, string eventName)
    {
        ObserveHoeingBatch(group, snapshot);
        foreach (var uid in snapshot.ParticipantUids)
        {
            var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.PlayerUid == uid && p.Online
                && state.MatchesExecutor(uid, p.ClientInstanceId));
            if (player is not null)
                await _broadcaster.SendEventOnlyToConnectionAsync(player.ConnectionId,
                    eventName, snapshot, group[5..]);
        }
    }

    private void ValidateGameBatch(string? batchId, IReadOnlyList<string> expected, string hostUid)
    {
        // 控制与游戏协调器允许不同部署。授权在助手/BGI外部开始合同完成，
        // 此处只冻结本游戏房间身份，不能要求控制批次存在于本进程。
        if (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 128 || expected.Count is < 1 or > 4
            || expected.Any(string.IsNullOrWhiteSpace) || expected.Distinct(StringComparer.Ordinal).Count() != expected.Count
            || !expected.Contains(hostUid, StringComparer.Ordinal))
            throw new InvalidOperationException("hoeing_start_identity_mismatch");
    }

    public async Task<CoordinatedBatchSnapshot> UpdateCoordinatedBatchAsync(GatewayHandlerContext ctx, int generation,
        string token, string batchId, string layout, int index, int attempt, string result,
        string stage = "", string reason = "")
    {
        if (_capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
            throw new InvalidOperationException("hoeing_start_capability_required");
        if (result == "local_tasks_failed"
            && _capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.LocalTaskFailureCapability) != true)
            throw new InvalidOperationException("hoeing_local_task_failure_capability_required");
        string group;
        CoordinatedBatchState state;
        CoordinatedBatchSnapshot snapshot;
        bool notifyTerminal;
        bool completionWaitsChanged;
        lock (_batchGate)
        {
            group = _roomManager.GetControlRoomGroup(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_identity_required");
            if (!_controlBatches.TryGetValue(group, out state!) || state.Generation != generation || state.BatchId != batchId)
                throw new InvalidOperationException("batch_not_found");
            if (!state.MatchesExecutor(player.PlayerUid, player.ClientInstanceId))
                throw new InvalidOperationException("batch_executor_instance_mismatch");
            var previousPhase = state.Phase;
            snapshot = state.Apply(player.PlayerUid, token, batchId, layout, index, attempt, result, DateTime.UtcNow, stage, reason);
            // 兼容先离线、后由最后一个队友完成共同锄地的顺序。
            completionWaitsChanged = ReleaseOfflineCompletionWaits(group, state);
            if (completionWaitsChanged) snapshot = state.Snapshot();
            notifyTerminal = previousPhase != snapshot.Phase && snapshot.Phase is ("completed" or "aborted");
        }
        ObserveHoeingBatch(group, snapshot);
        if (completionWaitsChanged) await BroadcastControlRoomPlayersAsync(group, forceFull: true);
        if (notifyTerminal) await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchState);
        return snapshot;
    }
    public async Task<CoordinatedBatchSnapshot> ConfirmCoordinatedResetCleanedAsync(
        GatewayHandlerContext ctx, string resetId, string batchId)
    {
        string group;
        CoordinatedBatchState state;
        CoordinatedBatchSnapshot snapshot;
        lock (_batchGate)
        {
            group = _roomManager.GetControlRoomGroup(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_identity_required");
            if (!_processedResetResults.TryGetValue(group + "\0" + resetId, out var reset)
                || !reset.Found || reset.Reason == "stale_reset" || reset.BatchId != batchId
                || !reset.TargetUids.Contains(player.PlayerUid, StringComparer.Ordinal)
                || !_controlBatches.TryGetValue(group, out state!) || state.BatchId != batchId
                || state.Generation != reset.Generation || !state.MatchesResetCleanup(player.PlayerUid, resetId)
                || !(state.MatchesExecutor(player.PlayerUid, player.ClientInstanceId)
                    || _resetCleanupExecutors.TryGetValue(group + "\0" + resetId, out var executors)
                        && executors.TryGetValue(player.PlayerUid, out var instance)
                        && instance == player.ClientInstanceId))
                throw new InvalidOperationException("batch_reset_cleanup_not_authorized");
            snapshot = state.ConfirmResetCleaned(player.PlayerUid, resetId);
        }
        await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchState);
        return snapshot;
    }

    /// <summary>
    /// 手动“重置状态”的服务端第一步：以 resetId 幂等地终止当前控制房间批次，
    /// 让所有参与者收到同一个失效边界。它不等待客户端回执，也不把 SignalR 在线
    /// 当成执行成功；目标助手随后仍需执行本地 takeover/epoch 清理。
    /// </summary>
    public async Task<CoordinatedBatchResetResult> ResetCoordinatedBatchAsync(
        GatewayHandlerContext ctx, string resetId, IReadOnlyList<string> requestedTargets,
        int? expectedGeneration = null, string? expectedBatchId = null)
    {
        if (string.IsNullOrWhiteSpace(resetId) || resetId.Length > 128)
            throw new InvalidOperationException("reset_id");

        var requested = requestedTargets?.Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        CoordinatedBatchSnapshot? snapshot = null;
        CoordinatedBatchState? resetBatch = null;
        IReadOnlyList<string> participants = [];
        var wasActive = false;
        var stale = false;
        string group;
        CoordinatedBatchResetResult result;
        IReadOnlyList<string> targetUids;
        lock (_batchGate)
        {
            group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
                ?? throw new InvalidOperationException("batch_control_room_required");
            var resetKey = group + "\0" + resetId;
            if (_processedResetResults.TryGetValue(resetKey, out var processed))
                return processed;
            _controlBatches.TryGetValue(group, out var state);
            targetUids = requested.Contains("*", StringComparer.Ordinal)
                ? (state is not null
                    ? state.ParticipantUids
                    : _roomManager.GetControlRoomPlayers(group).Select(p => p.PlayerUid)
                        .Where(static uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).ToArray())
                : requested;
            // Bind the physical barrier to executors that can actually receive this
            // reset. An offline target is not silently considered physically reset.
            var cleanupExecutors = _roomManager.GetControlRoomPlayers(group)
                .Where(p => p.Online && targetUids.Contains(p.PlayerUid, StringComparer.Ordinal))
                .GroupBy(p => p.PlayerUid, StringComparer.Ordinal)
                .Where(players => players.Count() == 1)
                .ToDictionary(players => players.Key, players => players.First().ClientInstanceId, StringComparer.Ordinal);
            // Resetting an unrelated executor (or an empty selection) must not abort,
            // disarm or acquire cleanup ownership over the room's current batch.
            if (state is not null && state.ParticipantUids.Intersect(targetUids, StringComparer.Ordinal).Any())
            {
                resetBatch = state;
                participants = state.ParticipantUids;
                snapshot = state.Snapshot();
                stale = string.IsNullOrWhiteSpace(expectedBatchId)
                    || expectedGeneration.HasValue && expectedGeneration.Value != state.Generation
                    || !string.Equals(expectedBatchId, state.BatchId, StringComparison.Ordinal);
                if (!stale)
                {
                    wasActive = !state.IsTerminal;
                    state.BeginResetCleanup(resetId, targetUids, cleanupExecutors.Keys);
                    snapshot = state.Snapshot();
                }
            }

            // A replaced assistant may not continue an old batch. An explicit new reset
            // can authorize its unique, currently connected executor to attest physical
            // cleanup, without changing the old batch's execution identity or tokens.
            // Even an independent reset needs a trusted fence for a local orphan batch.
            // Later Begin allocations use the same monotonic source and remain newer.
            var resetGeneration = snapshot?.Generation ?? Interlocked.Increment(ref _nextBatchGeneration);
            if (resetGeneration <= 0) throw new InvalidOperationException("batch_generation_exhausted");
            result = new CoordinatedBatchResetResult
            {
                ResetId = resetId,
                Found = snapshot != null,
                BatchId = snapshot?.BatchId ?? "",
                Generation = resetGeneration,
                Phase = snapshot?.Phase ?? "none",
                Reason = stale ? "stale_reset" : snapshot?.Reason ?? "no_active_batch",
                TargetUids = targetUids,
                MissingRegistrationUids = snapshot?.MissingRegistrationUids ?? []
            };
            _processedResetResults[resetKey] = result;
            if (!stale) _resetCleanupExecutors[resetKey] = cleanupExecutors;
            if (_processedResetResults.Count > 1024)
            {
                var expiredKey = _processedResetResults.Keys.First();
                _processedResetResults.Remove(expiredKey);
                _resetCleanupExecutors.Remove(expiredKey);
            }
            if (stale) return result;

            // 与批次核对共用锁，不能在核对旧批次后清掉并发新一轮的就绪边沿。
            // 共享批次被任一成员重置后，整轮参与者均解除服务端就绪边沿。
            var disarmUids = wasActive && participants.Count > 0 ? participants : targetUids;
            if (disarmUids.Count > 0)
                _roomManager.DisarmOnlineEvents(group, disarmUids);
            if (wasActive) _roomManager.MarkExhausted(group);
        }

        if (resetBatch is not null && snapshot is not null)
            await PublishBatchSnapshotAsync(group, resetBatch, snapshot, GatewayProtocol.Events.ControlBatchState);
        await BroadcastControlRoomPlayersAsync(group, forceFull: true);

        _logger.LogInformation(
            "联机批次手动重置：group={Group}, resetId={ResetId}, found={Found}, active={Active}, targets={Targets}, missing={Missing}",
            group, resetId, result.Found, wasActive, string.Join(",", result.TargetUids),
            string.Join(",", result.MissingRegistrationUids));
        return result;
    }
}
