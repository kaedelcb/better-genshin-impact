using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

internal sealed record RoundOrderStartupSnapshot(string SessionId, string HostUid, string[] ParticipantUids, int RoundCount);

// Read and mutate under the source room lock. Keep one terminal result for every waiting RPC.
internal sealed class RoundOrderStartup(RoundOrderStartupSnapshot snapshot, DateTimeOffset deadline)
{
    internal RoundOrderStartupSnapshot Snapshot { get; } = snapshot;
    internal DateTimeOffset Deadline { get; } = deadline;
    internal Dictionary<string, string> Confirmations { get; } = new(StringComparer.Ordinal);
    internal TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed partial class RoomOperations
{
    private RoundOrderStartup CreateRoundOrderStartupLocked(Room room, List<string> order, string hostUid, int roundCount)
    {
        var roster = room.Players.Select(p => p.PlayerUid).ToArray();
        var expected = room.WorldPlanParticipantUids ?? room.ExpectedParticipantUids;
        if (roster.Length == 0 || roster.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(hostUid)
            || roster.Distinct(StringComparer.Ordinal).Count() != roster.Length
            || room.Players.Any(p => !room.WorldJoinedSet.Contains(p.ConnectionId))
            || !order.ToHashSet(StringComparer.Ordinal).SetEquals(roster)
            || (expected.Count > 0 && !roster.ToHashSet(StringComparer.Ordinal).SetEquals(expected)))
            throw new InvalidOperationException("round_order_roster_invalid");
        return new(new(room.SessionId, hostUid, roster, Math.Min(roundCount, order.Count)),
            _worldPlanTime.GetUtcNow().AddSeconds(30));
    }

    internal (List<string> Order, RoundOrderStartupSnapshot? Startup) GetRoundOrderSnapshot(
        GatewayHandlerContext ctx, string? roomCode)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || (roomCode != null && room.Code != roomCode)) return ([], null);
        lock (room) return ([.. room.RoundHostOrder], room.RoundOrderStartup?.Snapshot);
    }

    internal async Task<bool> ConfirmRoundOrderAsync(GatewayHandlerContext ctx, string? roomCode,
        string sessionId, IReadOnlyList<string> order, int roundCount)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || room.Code != roomCode) return false;
        RoundOrderStartup gate;
        TimeSpan remaining;
        lock (room)
        {
            if (room.RoundOrderStartup is not { } startup || startup.Snapshot.SessionId != sessionId
                || !ReferenceEquals(_roomManager.GetRoomByConnectionId(ctx.ConnectionId).Room, room)) return false;
            gate = startup;
            var caller = room.Players.SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (caller == null || !gate.Snapshot.ParticipantUids.Contains(caller.PlayerUid, StringComparer.Ordinal))
                return false;
            if (gate.Snapshot.RoundCount != roundCount || roundCount <= 0
                || !room.RoundHostOrder.SequenceEqual(order, StringComparer.Ordinal)) return false;

            remaining = gate.Deadline - _worldPlanTime.GetUtcNow();
            if (remaining <= TimeSpan.Zero || room.CoordinatedAbortBroadcasted)
                gate.Result.TrySetResult(false);
            if (!gate.Result.Task.IsCompleted)
            {
                gate.Confirmations[caller.PlayerUid] = ctx.ConnectionId;
                // Count each frozen UID once, and do not accept a replaced connection's receipt.
                if (gate.Snapshot.ParticipantUids.All(uid => gate.Confirmations.TryGetValue(uid, out var connectionId)
                    && !room.GracePendingMembers.ContainsKey(connectionId)
                    && ReferenceEquals(_roomManager.GetRoomByConnectionId(connectionId).Room, room)
                    && room.Players.Any(p => p.PlayerUid == uid && p.ConnectionId == connectionId)))
                    gate.Result.TrySetResult(true);
            }
        }
        if (gate.Result.Task.IsCompleted) return await gate.Result.Task;
        try { return await gate.Result.Task.WaitAsync(remaining, _worldPlanTime); }
        catch (TimeoutException)
        {
            lock (room) gate.Result.TrySetResult(false);
            return await gate.Result.Task;
        }
    }
}
