using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private readonly object _batchGate = new();
    private readonly Dictionary<string, CoordinatedBatchState> _controlBatches = new(StringComparer.Ordinal);

    private void BeginCoordinatedBatch(string group, int generation, IReadOnlyList<string> uids)
    {
        lock (_batchGate)
        {
            if (_controlBatches.TryGetValue(group, out var old))
            {
                if (old.Generation == generation) return;
                // Do not launch a new batch while a previous execution may still hold the game slot.
                if (!old.IsTerminal) { old.Abort("superseded"); return; }
            }
            if (!_controlBatches.ContainsKey(group) && _controlBatches.Count >= 4096)
                throw new InvalidOperationException("batch_capacity");
            _controlBatches[group] = new CoordinatedBatchState(generation, uids, DateTime.UtcNow);
        }
    }

    public CoordinatedBatchSnapshot UpdateCoordinatedBatch(GatewayHandlerContext ctx, int generation,
        string token, string batchId, string layout, int index, int attempt, string result)
    {
        var group = _roomManager.GetControlRoomGroup(ctx.ConnectionId);
        if (group == null) throw new InvalidOperationException("batch_control_room_required");
        var uid = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId);
        if (string.IsNullOrEmpty(uid)) throw new InvalidOperationException("batch_identity_required");
        lock (_batchGate)
        {
            // Never reconstruct after server restart or from an arbitrary client request.
            if (!_controlBatches.TryGetValue(group, out var state) || state.Generation != generation)
                throw new InvalidOperationException("batch_not_found");
            return state.Apply(uid, token, batchId, layout, index, attempt, result, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 手动“重置状态”的服务端第一步：以 resetId 幂等地终止当前控制房间批次，
    /// 让所有参与者收到同一个失效边界。它不等待客户端回执，也不把 SignalR 在线
    /// 当成执行成功；目标助手随后仍需执行本地 takeover/epoch 清理。
    /// </summary>
    public async Task<CoordinatedBatchResetResult> ResetCoordinatedBatchAsync(
        GatewayHandlerContext ctx, string resetId, IReadOnlyList<string> requestedTargets)
    {
        var group = _roomManager.GetControlRoomGroupOrRemote(ctx.ConnectionId);
        if (string.IsNullOrWhiteSpace(group))
            throw new InvalidOperationException("batch_control_room_required");
        if (string.IsNullOrWhiteSpace(resetId) || resetId.Length > 128)
            throw new InvalidOperationException("reset_id");

        var requested = requestedTargets?.Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        CoordinatedBatchSnapshot? snapshot = null;
        IReadOnlyList<string> participants = [];
        var wasActive = false;
        lock (_batchGate)
        {
            if (_controlBatches.TryGetValue(group, out var state))
            {
                participants = state.ParticipantUids;
                wasActive = !state.IsTerminal;
                if (wasActive)
                    state.Abort("manual_reset");
                snapshot = state.Snapshot();
            }
        }

        var targetUids = requested.Contains("*", StringComparer.Ordinal)
            ? (participants.Count > 0
                ? participants
                : _roomManager.GetControlRoomPlayers(group).Select(p => p.PlayerUid)
                    .Where(static uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).ToArray())
            : requested;

        // 共享批次一旦被任一成员重置，整轮参与者都必须解除服务端就绪边沿；
        // 只清点击者会把其他成员的旧 generation 留在服务端，下一次上报可能幻影开新轮。
        var disarmUids = wasActive && participants.Count > 0 ? participants : targetUids;
        if (disarmUids.Count > 0)
            _roomManager.DisarmOnlineEvents(group, disarmUids);
        if (wasActive)
        {
            _roomManager.MarkExhausted(group);
            var generation = snapshot?.Generation ?? 0;
            await _broadcaster.BroadcastGroupAsync(group, "AllReadyAbort",
                new { generation, targetUids = participants, reason = "manual_reset", resetId }, generation);
        }
        await BroadcastControlRoomPlayersAsync(group, forceFull: true);

        var result = new CoordinatedBatchResetResult
        {
            ResetId = resetId,
            Found = snapshot != null,
            BatchId = snapshot?.BatchId ?? "",
            Generation = snapshot?.Generation ?? 0,
            Phase = snapshot?.Phase ?? "none",
            Reason = snapshot?.Reason ?? "no_active_batch",
            TargetUids = targetUids,
            MissingRegistrationUids = snapshot?.MissingRegistrationUids ?? []
        };
        _logger.LogInformation(
            "联机批次手动重置：group={Group}, resetId={ResetId}, found={Found}, active={Active}, targets={Targets}, missing={Missing}",
            group, resetId, result.Found, wasActive, string.Join(",", result.TargetUids),
            string.Join(",", result.MissingRegistrationUids));
        return result;
    }
}
