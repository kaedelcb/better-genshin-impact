using System.Runtime.CompilerServices;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed record FrozenRoutePackage(string Id, bool UseLocalRoutes);
    private readonly ConditionalWeakTable<Room, FrozenRoutePackage> _frozenRoutePackages = new();

    private void RequireRoomRoutePackage(Room room, RoomConfig? proposed = null)
    {
        if (_routePackagePolicy is null) return; // Pure business fixtures are not the production composition root.
        lock (room)
        {
            foreach (var player in room.Players)
                _routePackagePolicy.RequireConnectionVersion(player.ConnectionId, player.ReportedVersion);
            var config = proposed ?? room.HostConfig;
            var next = new FrozenRoutePackage(config?.RoutePackageId ?? "", config?.UseLocalRouteFallback == true);
            _routePackagePolicy.RequirePackage(next.Id, next.UseLocalRoutes);
            // A world-plan target already carries its immutable predecessor config before any host upload.
            var hasFrozen = _frozenRoutePackages.TryGetValue(room, out var frozen);
            var existing = hasFrozen ? frozen : room.HostConfig is { } inherited
                ? new FrozenRoutePackage(inherited.RoutePackageId ?? "", inherited.UseLocalRouteFallback) : null;
            if (existing != null && existing != next)
                throw new InvalidOperationException("hoeing_route_package_frozen");
            if (!hasFrozen) _frozenRoutePackages.Add(room, next);
        }
    }
}
