using System.Collections.Concurrent;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private readonly ConcurrentDictionary<string, WorldPlanState> _worldPlans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _worldPlansBySource = new(StringComparer.Ordinal);

    private void PruneWorldPlans()
    {
        var now = _worldPlanTime.GetUtcNow().UtcDateTime;
        foreach (var pair in _worldPlans)
            if (now > pair.Value.ExpiresAtUtc.AddMinutes(2) && _worldPlans.TryRemove(pair.Key, out var removed))
                _worldPlansBySource.TryRemove(new KeyValuePair<string, string>(removed.InitialRoomSessionId, removed.Id));
    }

    public Task<WorldPlanAdmission> BeginWorldPlanAsync(GatewayHandlerContext ctx, bool forceLegacy = false,
        bool freezeCurrentParty = false)
    {
        PruneWorldPlans();
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || room.HostConnectionId != ctx.ConnectionId)
            throw new InvalidOperationException("world_plan_host_required");
        lock (room)
        {
            if (freezeCurrentParty && (!forceLegacy || room.HoeingBatchId != null))
                throw new InvalidOperationException("world_plan_manual_party_invalid");
            if (_worldPlansBySource.TryGetValue(room.SessionId, out var existing) && _worldPlans.TryGetValue(existing, out var current))
                return Task.FromResult(current.Acquire(room.Players.Single(p => p.ConnectionId == ctx.ConnectionId).PlayerUid, _worldPlanTime.GetUtcNow().UtcDateTime));
            if (room.IsStarted || room.HostConfig == null || !room.HostConfig.MultiWorldEnabled)
                throw new InvalidOperationException("world_plan_config_invalid");
            if (room.Players.Count != room.ExpectedPlayerCount && !(forceLegacy && room.HoeingBatchId == null))
                throw new InvalidOperationException("world_plan_roster_incomplete");
            if (room.HoeingBatchId != null && (!room.ExpectedParticipantUids.Order(StringComparer.Ordinal)
                .SequenceEqual(room.Players.Select(p => p.PlayerUid).Order(StringComparer.Ordinal)) || forceLegacy))
                throw new InvalidOperationException("world_plan_roster_incomplete");
            var capable = !forceLegacy && room.Players.All(p => !string.IsNullOrWhiteSpace(p.PlayerUid)
                && _capabilityLookup?.Supports(p.ConnectionId, WorldPlanState.Capability) == true)
                && room.Players.Select(p => p.PlayerUid).Distinct(StringComparer.Ordinal).Count() == room.Players.Count;
            var currentPartyFrozen = false;
            if (freezeCurrentParty)
            {
                room.ExpectedPlayerCount = room.Players.Count;
                var currentUids = room.Players.Select(p => p.PlayerUid).ToArray();
                currentPartyFrozen = currentUids.All(uid => !string.IsNullOrWhiteSpace(uid))
                    && currentUids.Distinct(StringComparer.Ordinal).Count() == currentUids.Length;
                if (currentPartyFrozen) room.WorldPlanParticipantUids = currentUids;
            }
            var state = new WorldPlanState(room, capable, _worldPlanTime.GetUtcNow().UtcDateTime, currentPartyFrozen);
            if (capable)
            {
                room.WorldPlanParticipantUids = state.Participants.AsReadOnly();
                room.WorldPlanId = state.Id;
            }
            else room.UseLegacyWorldPlanOrder = true;
            _worldPlans[state.Id] = state;
            _worldPlansBySource[room.SessionId] = state.Id;
            return Task.FromResult(state.Acquire(state.InitialHostUid, _worldPlanTime.GetUtcNow().UtcDateTime));
        }
    }

    public Task<WorldPlanAdmission?> AcquireWorldPlanAsync(GatewayHandlerContext ctx)
    {
        PruneWorldPlans();
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null) return Task.FromResult<WorldPlanAdmission?>(null);
        lock (room)
        {
            WorldPlanState? plan = null;
            if (_worldPlansBySource.TryGetValue(room.SessionId, out var id)) _worldPlans.TryGetValue(id, out plan);
            if (plan == null)
            {
                // A supported member must not wait for begin from an older host which never sends that command.
                if (room.HostConfig is not { MultiWorldEnabled: true }
                    || _capabilityLookup?.Supports(room.HostConnectionId, WorldPlanState.Capability) == true)
                    return Task.FromResult<WorldPlanAdmission?>(null);
                plan = new WorldPlanState(room, false, _worldPlanTime.GetUtcNow().UtcDateTime);
                room.UseLegacyWorldPlanOrder = true;
                _worldPlans[plan.Id] = plan;
                _worldPlansBySource[room.SessionId] = plan.Id;
            }
            var player = room.Players.SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId)
                ?? throw new InvalidOperationException("world_plan_not_participant");
            return Task.FromResult<WorldPlanAdmission?>(plan.Acquire(player.PlayerUid, _worldPlanTime.GetUtcNow().UtcDateTime));
        }
    }

    private WorldPlanState RequireWorldPlan(string planId, string ticket)
    {
        if (!_worldPlans.TryGetValue(planId, out var state)) throw new InvalidOperationException("world_plan_not_found");
        state.Authenticate(ticket);
        return state;
    }

    public Task<WorldPlanSnapshot> GetWorldPlanAsync(string planId, string ticket)
        => Task.FromResult(RequireWorldPlan(planId, ticket).Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime));

    public Task<WorldPlanSnapshot> ReportWorldPlanAsync(string planId, string ticket, bool known, int count)
        => Task.FromResult(RequireWorldPlan(planId, ticket).Report(ticket, known, count, _worldPlanTime.GetUtcNow().UtcDateTime));

    public Task<WorldPlanSnapshot> AckWorldPlanAsync(string planId, string ticket)
    {
        var plan = RequireWorldPlan(planId, ticket);
        var snapshot = plan.Ack(ticket, _worldPlanTime.GetUtcNow().UtcDateTime);
        if (snapshot.Stage == "Legacy")
        {
            var source = _roomManager.GetRoom(plan.InitialRoomCode);
            if (source != null)
                lock (source)
                    if (source.SessionId == plan.InitialRoomSessionId && source.WorldPlanId == plan.Id)
                    {
                        source.WorldPlanParticipantUids = null;
                        source.WorldPlanId = null;
                        source.UseLegacyWorldPlanOrder = true;
                    }
        }
        return Task.FromResult(snapshot);
    }

    public async Task<WorldPlanSnapshot> AbortWorldPlanAsync(string planId, string ticket)
    {
        var plan = RequireWorldPlan(planId, ticket);
        await plan.TransferGate.WaitAsync();
        try
        {
            var snapshot = plan.Abort(ticket, _worldPlanTime.GetUtcNow().UtcDateTime);
            // A peer may already hold the C room code. Revoke that exact room as well as the plan;
            // a delayed old abort cannot close a later D room or a reused room code.
            if (snapshot.TargetRoomCode is { } code && snapshot.TargetRoomSessionId is { } session
                && _roomManager.EndRoomIfMatches(code, session))
            {
                _phaseObserver.Mark(code, RoomPhase.Ended, "worldPlan.abort");
                _phaseObserver.Forget(code);
                await _broadcaster.BroadcastGroupAsync(code, "RoomClosed",
                    new { reason = "本次开队预判已中止" }, "本次开队预判已中止");
            }
            return snapshot;
        }
        finally { plan.TransferGate.Release(); }
    }

    /// <summary>发出游戏进世界申请前再次核对准入，绝不根据已缓存的房码继续一个已撤销计划。</summary>
    public async Task<WorldPlanSnapshot> ValidateWorldPlanTargetAsync(GatewayHandlerContext ctx, string planId, string ticket)
    {
        var plan = RequireWorldPlan(planId, ticket);
        await plan.TransferGate.WaitAsync();
        try
        {
            var uid = plan.Authenticate(ticket);
            var snapshot = plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            var (room, code) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
            if (snapshot.Stage != "Committed" || room == null || code != snapshot.TargetRoomCode
                || room.SessionId != snapshot.TargetRoomSessionId)
                throw new InvalidOperationException("world_plan_target_invalid");
            lock (room)
            {
                var current = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
                var hostUid = snapshot.RemainingHostUids.FirstOrDefault();
                if (!ReferenceEquals(current.Room, room) || !ReferenceEquals(_roomManager.GetRoom(code), room)
                    || !room.Players.Any(p => p.ConnectionId == ctx.ConnectionId && p.PlayerUid == uid)
                    || string.IsNullOrEmpty(hostUid)
                    || room.Players.SingleOrDefault(p => p.ConnectionId == room.HostConnectionId)?.PlayerUid != hostUid)
                    throw new InvalidOperationException("world_plan_target_identity");
            }
            return snapshot;
        }
        finally { plan.TransferGate.Release(); }
    }

    /// <summary>建房与登记在同一窄门内；回执丢失后重发不会建立第二个 C 房。</summary>
    public async Task<WorldPlanSnapshot> CreateWorldPlanTargetAsync(GatewayHandlerContext ctx, string planId, string ticket)
    {
        var plan = RequireWorldPlan(planId, ticket);
        await plan.TransferGate.WaitAsync();
        try
        {
            var uid = plan.RequireTarget(ticket, _worldPlanTime.GetUtcNow().UtcDateTime);
            if (_capabilityLookup?.Supports(ctx.ConnectionId, WorldPlanState.Capability) != true)
                throw new InvalidOperationException("world_plan_capability_required");
            var player = plan.Players.Single(p => p.PlayerUid == uid);
            var snapshot = plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            if (uid != plan.InitialHostUid && !snapshot.SourceReleased)
                throw new InvalidOperationException("world_plan_source_not_released");
            if (snapshot.TargetRoomCode != null)
            {
                var target = _roomManager.GetRoom(snapshot.TargetRoomCode);
                if (target == null || target.SessionId != snapshot.TargetRoomSessionId)
                    throw new InvalidOperationException("world_plan_target_ended");
                if (target.HostConnectionId != ctx.ConnectionId)
                {
                    var joined = await JoinRoomAsync(ctx, target.Code, player.PlayerName, uid, player.ReportedVersion, plan.Config.HoeingBatchId);
                    if (!joined.Success) throw new InvalidOperationException(joined.Error ?? "world_plan_rejoin_failed");
                }
                // Group subscription may have failed after allocation; repeating it is safe.
                await LeaveAllGroupsAsync(ctx, target.Code);
                await _broadcaster.AddToGroupAsync(ctx, target.Code);
                TrackGroup(ctx, target.Code);
                await _broadcaster.BroadcastGroupAsync(target.Code, "PlayerListUpdated", new { players = target.Players }, target.Players);
                return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            }
            if (uid == plan.InitialHostUid)
            {
                var initial = _roomManager.GetRoom(plan.InitialRoomCode);
                if (initial == null || initial.SessionId != plan.InitialRoomSessionId)
                    throw new InvalidOperationException("world_plan_source_ended");
                plan.SetTarget(initial.Code, initial.SessionId);
                return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            }
            // Allocate and register before the first transport await, including a failed/lost acknowledgement.
            // The caller's authority comes from its private plan ticket; ordinary room/versions checks remain in JoinRoom.
            var code = _roomManager.CreateRoom(ctx.ConnectionId, player.PlayerName,
                plan.Players.Select(p => p.PlayerName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList(),
                uid, plan.Participants.Count, player.ReportedVersion, plan.Config.HoeingBatchId,
                plan.Config.HoeingBatchId == null ? null : plan.Participants,
                plan.Participants, plan.Id, GatewayProtocol.RoomProtocols.V3, snapshot.HostConfig);
            var room = _roomManager.GetRoom(code)!;
            plan.SetTarget(code, room.SessionId);
            await LeaveAllGroupsAsync(ctx);
            await _broadcaster.AddToGroupAsync(ctx, code);
            TrackGroup(ctx, code);
            ObservePhase(room, code, "worldPlan.createTarget");
            await _broadcaster.BroadcastGroupAsync(code, "PlayerListUpdated", new { players = room.Players }, room.Players);
            return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
        }
        finally { plan.TransferGate.Release(); }
    }

    /// <summary>只退出冻结的源房；迟到的重发绝不能关闭或退出已经加入的 C 房。</summary>
    public async Task<WorldPlanSnapshot> LeaveWorldPlanSourceAsync(GatewayHandlerContext ctx, string planId, string ticket)
    {
        var plan = RequireWorldPlan(planId, ticket);
        await plan.TransferGate.WaitAsync();
        try
        {
            if (plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime).SourceReleased) return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            if (!plan.RegisterSourceReady(ticket, _worldPlanTime.GetUtcNow().UtcDateTime)) return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
            // Delete this source instance only; mappings already belonging to a successor are conditional removals.
            if (!_roomManager.EndRoomIfMatches(plan.InitialRoomCode, plan.InitialRoomSessionId))
            {
                plan.Abort(ticket, _worldPlanTime.GetUtcNow().UtcDateTime);
                throw new InvalidOperationException("world_plan_source_ended");
            }
            plan.ReleaseSource();
            await _broadcaster.BroadcastGroupAsync(plan.InitialRoomCode, "RoomClosed",
                new { reason = "开队预判完成，转入目标世界" }, "开队预判完成，转入目标世界");
            return plan.Snapshot(_worldPlanTime.GetUtcNow().UtcDateTime);
        }
        finally { plan.TransferGate.Release(); }
    }
}
