using System.Runtime.CompilerServices;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed record FrozenRoutePackage(string Id);
    private readonly ConditionalWeakTable<Room, FrozenRoutePackage> _frozenRoutePackages = new();

    private void RequireRoomRoutePackage(Room room, bool requirePin = true, string? proposedId = null)
    {
        if (_routePackagePolicy is null) return; // Pure business fixtures are not the production composition root.
        lock (room)
        {
            foreach (var player in room.Players)
                _routePackagePolicy.RequireConnectionVersion(player.ConnectionId, player.ReportedVersion);
            var id = proposedId ?? room.HostConfig?.RoutePackageId;
            if (string.IsNullOrEmpty(id) && !requirePin && !_frozenRoutePackages.TryGetValue(room, out _)) return;
            _routePackagePolicy.RequirePackage(id);
            // A world-plan target already carries its immutable predecessor config before any host upload.
            var existing = _frozenRoutePackages.TryGetValue(room, out var frozen) ? frozen.Id : room.HostConfig?.RoutePackageId;
            if (!string.IsNullOrEmpty(existing) && !string.Equals(existing, id, StringComparison.Ordinal))
                throw new InvalidOperationException("hoeing_route_package_frozen");
            if (frozen is null) _frozenRoutePackages.Add(room, new FrozenRoutePackage(id!));
        }
    }
}
