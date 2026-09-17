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
}
