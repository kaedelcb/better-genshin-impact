using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private bool SupportsHostWorld(GatewayHandlerContext ctx) => ctx.IsV3
        && _capabilityLookup?.Supports(ctx.ConnectionId, GatewayProtocol.HostProvidesWorldCapability) == true;

    public HostWorldSnapshot HostWorldOperation(GatewayHandlerContext ctx, string? roomCode, string sessionId,
        string? action = null, List<string>? routeNames = null, bool allowPartial = false, int partyCount = 0,
        string? manifestDigest = null)
    {
        if (!SupportsHostWorld(ctx)) throw new InvalidOperationException("host_world_capability_required");
        if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(sessionId))
            throw new InvalidOperationException("host_world_session_required");
        if (action is not (null or "start" or "publish" or "finish"))
            throw new InvalidOperationException("host_world_action_invalid");
        if (action == "publish" && (routeNames == null || routeNames.Count > 16384
            || routeNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 512)
            || routeNames.Distinct(StringComparer.Ordinal).Count() != routeNames.Count
            || (routeNames.Count > 0
                ? manifestDigest is not { Length: 64 } || !manifestDigest.All(Uri.IsHexDigit)
                : manifestDigest != null)))
            throw new InvalidOperationException("host_world_routes_invalid");

        var room = _roomManager.GetRoom(roomCode);
        if (room?.HostWorld is { } state && room.SessionId == sessionId)
        {
            lock (room)
            {
                if (_roomManager.CanUseHostWorldRoomLocked(room, ctx.ConnectionId)
                    && room.Players.SingleOrDefault(p => p.ConnectionId == ctx.ConnectionId) is { } caller)
                {
                    CheckHostWorldActor(state.Snapshot, caller.PlayerUid, action);
                    switch (action)
                    {
                        case "start":
                            if (state.Snapshot.Phase == "waiting") StartHostWorldIfReadyLocked(room, state, allowPartial, partyCount);
                            break;
                        case "publish":
                            if (!state.IsTerminal) RequireRoomRoutePackage(room);
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
        return retained;
    }

    private static void CheckHostWorldActor(HostWorldSnapshot state, string uid, string? action)
    {
        if ((action is "start" or "publish") && uid != state.HostUid)
            throw new InvalidOperationException("host_world_host_required");
        if (action == "finish" && !state.ExecutorUids.Contains(uid, StringComparer.Ordinal))
            throw new InvalidOperationException("host_world_executor_required");
    }

    private void StartHostWorldIfReadyLocked(Room room, HostWorldExecution state, bool allowPartial, int partyCount)
    {
        var required = partyCount == 0 && !allowPartial ? room.ExpectedPlayerCount : partyCount;
        if (required < 2 || required > room.ExpectedPlayerCount || !allowPartial && required != room.ExpectedPlayerCount
            || room.HostConfig == null || !room.HostReady || room.Players.Count != required
            || _roomManager.GetLiveJoinedHostWorldCountLocked(room) != required
            || room.Players.Any(p => _capabilityLookup?.Supports(p.ConnectionId, GatewayProtocol.HostProvidesWorldCapability) != true))
            return;
        RequireRoomRoutePackage(room);
        state.Start(room.RouteExecutors.Select(p => p.PlayerUid));
        room.IsStarted = true;
        room.StartedAtUtc = DateTime.UtcNow;
        ObservePhase(room, room.Code, "room.hostWorldUpdate.start");
    }
}
