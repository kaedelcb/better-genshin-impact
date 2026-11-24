using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private bool SupportsHostWorld(GatewayHandlerContext ctx) => ctx.IsV3
        && _capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HostProvidesWorldCapability) == true;

    public HostWorldSnapshot HostWorldOperation(GatewayHandlerContext ctx, string? roomCode, string sessionId,
        string? action = null, List<string>? routeNames = null, bool allowPartial = false, int partyCount = 0,
        string? manifestDigest = null, string? routeName = null, double durationSeconds = 0)
    {
        if (!SupportsHostWorld(ctx)) throw new InvalidOperationException("host_world_capability_required");
        if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("host_world_session_required");
        if (action is not (null or "start" or "publish" or "finish" or "routeDone"))
            throw new InvalidOperationException("host_world_action_invalid");
        if (action == "routeDone" && (string.IsNullOrWhiteSpace(routeName) || routeName.Length > 512
            || !double.IsFinite(durationSeconds) || durationSeconds <= 0))
            throw new InvalidOperationException("host_world_route_done_invalid");
        if (action == "publish" && (routeNames == null || routeNames.Count > 16384
            || routeNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 512)
            || routeNames.Distinct(StringComparer.Ordinal).Count() != routeNames.Count
            || (routeNames.Count > 0
                ? manifestDigest is not { Length: 64 } || !manifestDigest.All(Uri.IsHexDigit)
                : manifestDigest != null)))
            throw new InvalidOperationException("host_world_routes_invalid");

        var room = _roomManager.GetRoom(roomCode);
        if (room?.StandbyNegotiation is { } state && room.SessionId == sessionId)
        {
            lock (room)
            {
                if (_roomManager.CanUseHostWorldRoomLocked(room, ctx.ConnectionId)
                    && room.Players.SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId && p.NegotiatesStandby) is { } caller)
                {
                    CheckHostWorldActor(state.Snapshot, caller.PlayerUid, action);
                    switch (action)
                    {
                        case "start":
                            if (state.Snapshot.Phase == "waiting") StartHostWorldIfReadyLocked(room, state, allowPartial, partyCount);
                            break;
                        case "publish":
                            if (!state.IsTerminal)
                            {
                                if (room.HostConfig is { MultiWorldEnabled: true } && !room.IsStarted)
                                    throw new InvalidOperationException("host_world_start_required");
                                RequireRoomRoutePackage(room);
                            }
                            state.Publish(routeNames!, manifestDigest);
                            if (state.Snapshot.RouteNames is { } manifest)
                            {
                                room.HostRouteListUploaded = true;
                                room.HostRouteList = [.. manifest];
                            }
                            break;
                        case "finish":
                            state.Finish(caller.PlayerUid);
                            break;
                        case "routeDone":
                            state.RecordRouteDone(routeName!, Math.Min(durationSeconds, 6 * 60 * 60), manifestDigest, DateTime.UtcNow);
                            break;
                    }
                    return state.Snapshot;
                }
            }
        }

        var (retained, uid) = _roomManager.ReadRetainedHostWorld(roomCode, sessionId, ctx.ConnectionId);
        if (retained == null || uid == null) throw new InvalidOperationException("host_world_session_mismatch");
        CheckHostWorldActor(retained, uid, action);
        if (action == "publish" && retained.RouteNames is { } previous
            && (!previous.SequenceEqual(routeNames!, StringComparer.Ordinal) || retained.ManifestDigest != manifestDigest))
            throw new InvalidOperationException("host_world_routes_frozen");
        if (action == "routeDone")
        {
            HostWorldExecution.RequirePublishedRoute(retained, routeName!, manifestDigest);
            if (retained.RouteCompletions?.Any(receipt => receipt.RouteName == routeName) != true)
                throw new InvalidOperationException("host_world_not_running");
        }
        return retained;
    }

    private static void CheckHostWorldActor(HostWorldSnapshot state, string uid, string? action)
    {
        if ((action is "start" or "publish") && uid != state.HostUid)
            throw new InvalidOperationException("host_world_host_required");
        if ((action is "finish" or "routeDone") && !state.ExecutorUids.Contains(uid, StringComparer.Ordinal))
            throw new InvalidOperationException("host_world_executor_required");
    }

    private void StartHostWorldIfReadyLocked(Room room, HostWorldExecution state, bool allowPartial, int partyCount)
    {
        var standby = room.Players.Where(p => p.StandbyOnly).Select(p => p.PlayerUid).ToArray();
        var required = partyCount == 0 && !allowPartial ? room.ExpectedPlayerCount : partyCount;
        if (required < (standby.Length > 0 ? 2 : 1) || required > room.ExpectedPlayerCount
            || !allowPartial && required != room.ExpectedPlayerCount
            || room.HostConfig == null || !room.HostReady || room.Players.Count != required
            || (room.WorldPlanParticipantUids != null && !_roomManager.GetHoeingStartupReadyLocked(room).Ready)
            || (standby.Length > 0 || !allowPartial) && _roomManager.GetLiveJoinedHostWorldCountLocked(room) != required)
            return;
        if (standby.Length > 0 && room.Players.Any(p => !p.NegotiatesStandby
            || _capabilityLookup?.Supports(p.ConnectionId, GatewayProtocol.HostProvidesWorldCapability) != true))
            throw new InvalidOperationException("player_standby_peer_unsupported");
        RequireRoomRoutePackage(room);
        state.Start(room.Players.Where(p => !p.StandbyOnly).Select(p => p.PlayerUid), standby);
        if (state.Snapshot.Phase == "aborted" || room.HostConfig.MultiWorldEnabled) return;
        room.IsStarted = true;
        room.StartedAtUtc = DateTime.UtcNow;
        ObservePhase(room, room.Code, "room.hostWorldUpdate.start");
    }
}
