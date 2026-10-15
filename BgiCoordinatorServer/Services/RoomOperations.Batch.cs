using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private readonly object _batchGate = new();
    private readonly Dictionary<string, CoordinatedBatchState> _controlBatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoordinatedBatchResetResult> _processedResetResults = new(StringComparer.Ordinal);

    private static int _nextBatchGeneration = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private readonly Dictionary<string, CoordinatedBatchState> _batchRequests = new(StringComparer.Ordinal);

    public CoordinatedBatchSnapshot? CurrentCoordinatedBatch(GatewayHandlerContext ctx, string? batchId = null)
    {
        var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_control_room_required");
        lock (_batchGate)
        {
            if (!_controlBatches.TryGetValue(group, out var state)) return null;
            if (!string.IsNullOrEmpty(batchId) && state.BatchId != batchId)
                throw new InvalidOperationException("batch_not_found");
            return state.Snapshot();
        }
    }

    public async Task<CoordinatedBatchSnapshot> BeginManualCoordinatedBatchAsync(GatewayHandlerContext ctx,
        string requestId, IReadOnlyList<string> targetUids, DateTimeOffset? expiresAtUtc)
    {
        var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_control_room_required");
        if (_capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
            throw new InvalidOperationException("hoeing_start_capability_required");
        var senderUid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId)
            ?? _roomManager.GetRemoteUidByConnectionId(group, ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_identity_required");
        CoordinatedBatchState batch;
        bool created;
        lock (_batchGate)
        {
            batch = BeginCoordinatedBatch(group, "manual", senderUid + ":" + requestId,
                requestId, targetUids, null, expiresAtUtc, out created);
            if (created) _roomManager.DisarmOnlineEvents(group, batch.ParticipantUids);
        }
        if (created) await PublishBatchStartAsync(group, batch);
        lock (_batchGate) return batch.Snapshot();
    }

    // 只负责受理。拒绝新请求不得修改旧批次，包括尚未完成清理的终态批次。
    private CoordinatedBatchState BeginCoordinatedBatch(string group, string source, string requestKey,
        string requestId, IReadOnlyList<string> uids, IReadOnlyDictionary<string, int>? intentGenerations,
        DateTimeOffset? expiresAtUtc, out bool created)
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
            return repeated;
        }
        if (expiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("request_expired");
        if (_controlBatches.TryGetValue(group, out var old))
        {
            if (!old.IsTerminal) throw new InvalidOperationException("previous_batch_active");
        }
        if (!_controlBatches.ContainsKey(group) && _controlBatches.Count >= 4096)
            throw new InvalidOperationException("batch_capacity");
        if (_batchRequests.Count >= 16384) throw new InvalidOperationException("batch_request_capacity");
        var players = _roomManager.GetCoordinatedStartPlayers(group);
        var selected = new List<Models.ControlRoomPlayer>();
        foreach (var uid in uids)
        {
            var matches = players.Where(p => p.PlayerUid == uid && p.Online).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("batch_participant_unavailable_or_ambiguous:" + uid);
            var player = matches[0];
            if (_capabilityLookup?.Supports(player.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
                throw new InvalidOperationException("hoeing_start_capability_required:" + uid);
            if (player.OnlineHoeingGroupNames.Count == 0)
                throw new InvalidOperationException("batch_binding_missing:" + uid);
            selected.Add(player);
        }
        var bindingIssue = RoomManager.ValidateCoordinatedBindingLayout(selected);
        if (bindingIssue is not null) throw new InvalidOperationException(bindingIssue);
        var bindings = selected.ToDictionary(p => p.PlayerUid,
            p => (IReadOnlyList<CoordinatedBatchBinding>)p.OnlineHoeingGroupNames.Select((name, i) =>
                new CoordinatedBatchBinding(name, p.OnlineHoeingGroupTypes.Count == 0 ? "group" : p.OnlineHoeingGroupTypes[i])).ToArray(),
            StringComparer.Ordinal);
        var instances = selected.ToDictionary(p => p.PlayerUid, p => p.ClientInstanceId, StringComparer.Ordinal);
        var generation = Interlocked.Increment(ref _nextBatchGeneration);
        if (generation <= 0) throw new InvalidOperationException("batch_generation_exhausted");
        var batch = new CoordinatedBatchState(generation, uids, DateTime.UtcNow, source, requestId,
            bindings, intentGenerations, instances);
        _controlBatches[group] = batch;
        _batchRequests[key] = batch;
        created = true;
        return batch;
    }

    private async Task PublishBatchStartAsync(string group, CoordinatedBatchState state)
    {
        CoordinatedBatchSnapshot snapshot;
        lock (_batchGate) snapshot = state.Snapshot();
        await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchStart);
    }

    private async Task PublishBatchSnapshotAsync(string group, CoordinatedBatchState state,
        CoordinatedBatchSnapshot snapshot, string eventName)
    {
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
        var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
        if (group == null) throw new InvalidOperationException("batch_control_room_required");
        if (_capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HoeingStartCapability) != true)
            throw new InvalidOperationException("hoeing_start_capability_required");
        var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_identity_required");
        CoordinatedBatchState state;
        CoordinatedBatchSnapshot snapshot;
        bool notifyAbort;
        lock (_batchGate)
        {
            if (!_controlBatches.TryGetValue(group, out state!) || state.Generation != generation || state.BatchId != batchId)
                throw new InvalidOperationException("batch_not_found");
            if (!state.MatchesExecutor(player.PlayerUid, player.ClientInstanceId))
                throw new InvalidOperationException("batch_executor_instance_mismatch");
            var previousPhase = state.Phase;
            snapshot = state.Apply(player.PlayerUid, token, batchId, layout, index, attempt, result, DateTime.UtcNow, stage, reason);
            notifyAbort = previousPhase != "aborted" && snapshot.Phase == "aborted";
        }
        if (notifyAbort) await PublishBatchSnapshotAsync(group, state, snapshot, GatewayProtocol.Events.ControlBatchState);
        return snapshot;
    }
    public async Task<CoordinatedBatchSnapshot> ConfirmCoordinatedResetCleanedAsync(
        GatewayHandlerContext ctx, string resetId, string batchId)
    {
        var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_control_room_required");
        var player = _roomManager.GetControlRoomPlayers(group).SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId)
            ?? throw new InvalidOperationException("batch_identity_required");
        CoordinatedBatchState state;
        CoordinatedBatchSnapshot snapshot;
        lock (_batchGate)
        {
            if (!_processedResetResults.TryGetValue(group + "\0" + resetId, out var reset)
                || !reset.Found || reset.Reason == "stale_reset" || reset.BatchId != batchId
                || !reset.TargetUids.Contains(player.PlayerUid, StringComparer.Ordinal)
                || !_controlBatches.TryGetValue(group, out state!) || state.BatchId != batchId
                || !state.MatchesExecutor(player.PlayerUid, player.ClientInstanceId))
                throw new InvalidOperationException("batch_reset_cleanup_not_authorized");
            snapshot = state.ConfirmResetCleaned(player.PlayerUid);
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
        var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId);
        if (string.IsNullOrWhiteSpace(group))
            throw new InvalidOperationException("batch_control_room_required");
        if (string.IsNullOrWhiteSpace(resetId) || resetId.Length > 128)
            throw new InvalidOperationException("reset_id");

        var requested = requestedTargets?.Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        CoordinatedBatchSnapshot? snapshot = null;
        CoordinatedBatchState? resetBatch = null;
        IReadOnlyList<string> participants = [];
        var wasActive = false;
        var stale = false;
        var resetKey = group + "\0" + resetId;
        lock (_batchGate)
        {
            if (_processedResetResults.TryGetValue(resetKey, out var processed))
                return processed;
            if (_controlBatches.TryGetValue(group, out var state))
            {
                resetBatch = state;
                participants = state.ParticipantUids;
                snapshot = state.Snapshot();
                stale = string.IsNullOrWhiteSpace(expectedBatchId)
                    || expectedGeneration.HasValue && expectedGeneration.Value != state.Generation
                    || !string.IsNullOrWhiteSpace(expectedBatchId)
                       && !string.Equals(expectedBatchId, state.BatchId, StringComparison.Ordinal);
                if (!stale)
                {
                    wasActive = !state.IsTerminal;
                    if (wasActive)
                        state.Abort("manual_reset");
                    snapshot = state.Snapshot();
                }
            }
        }

        var targetUids = requested.Contains("*", StringComparer.Ordinal)
            ? (participants.Count > 0
                ? participants
                : _roomManager.GetControlRoomPlayers(group).Select(p => p.PlayerUid)
                    .Where(static uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).ToArray())
            : requested;

        var result = new CoordinatedBatchResetResult
        {
            ResetId = resetId,
            Found = snapshot != null,
            BatchId = snapshot?.BatchId ?? "",
            Generation = snapshot?.Generation ?? 0,
            Phase = snapshot?.Phase ?? "none",
            Reason = stale ? "stale_reset" : snapshot?.Reason ?? "no_active_batch",
            TargetUids = targetUids,
            MissingRegistrationUids = snapshot?.MissingRegistrationUids ?? []
        };
        lock (_batchGate)
        {
            if (_processedResetResults.TryGetValue(resetKey, out var processed))
                return processed;
            _processedResetResults[resetKey] = result;
            if (_processedResetResults.Count > 1024)
                _processedResetResults.Remove(_processedResetResults.Keys.First());
        }

        if (stale)
            return result;

        // 共享批次一旦被任一成员重置，整轮参与者都必须解除服务端就绪边沿；
        // 只清点击者会把其他成员的旧 generation 留在服务端，下一次上报可能幻影开新轮。
        var disarmUids = wasActive && participants.Count > 0 ? participants : targetUids;
        if (disarmUids.Count > 0)
            _roomManager.DisarmOnlineEvents(group, disarmUids);
        if (wasActive)
        {
            _roomManager.MarkExhausted(group);
            if (resetBatch is not null && snapshot is not null)
                await PublishBatchSnapshotAsync(group, resetBatch, snapshot, GatewayProtocol.Events.ControlBatchState);
        }
        await BroadcastControlRoomPlayersAsync(group, forceFull: true);

        _logger.LogInformation(
            "联机批次手动重置：group={Group}, resetId={ResetId}, found={Found}, active={Active}, targets={Targets}, missing={Missing}",
            group, resetId, result.Found, wasActive, string.Join(",", result.TargetUids),
            string.Join(",", result.MissingRegistrationUids));
        return result;
    }
}
