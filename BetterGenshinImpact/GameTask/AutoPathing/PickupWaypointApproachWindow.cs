using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>保留当前路线段的一次拾取观察；观察可在上一途经点开启，始终归属目标点。</summary>
internal sealed class PickupWaypointApproachWindow : IDisposable
{
    private const double StartDistance = 3;

    private int _ownerIndex = -1;
    private int? _resumeIndex;
    private PickupWaypointProgress? _progress;
    private bool _disposed;

    internal static int? SelectObservationIndex(IReadOnlyList<Waypoint> segment, int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= segment.Count)
            return null;

        var current = segment[currentIndex];
        if (IsPickupTarget(current))
            return currentIndex;

        // 只从没有其他动作的普通移动点提前观察紧接的目标，避免串点计数或跨传送。
        if (currentIndex + 1 >= segment.Count || current.PickupTargetCount.HasValue
            || !string.IsNullOrEmpty(current.Action) || !string.IsNullOrWhiteSpace(current.SyncPointId)
            || (current.Type != WaypointType.Path.Code && current.Type != WaypointType.Target.Code))
            return null;

        return IsPickupTarget(segment[currentIndex + 1]) ? currentIndex + 1 : null;
    }

    private static bool IsPickupTarget(Waypoint waypoint)
        => waypoint.Type == WaypointType.Target.Code && waypoint.Action == ActionEnum.CombatScript.Code
           && waypoint.PickupTargetCount is { Count: > 0 };

    internal static bool IsWithinStartDistance(Waypoint target, Point2f position, out double distance)
    {
        distance = double.PositiveInfinity;
        if ((position.X == 0 && position.Y == 0) || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return false;

        distance = Navigation.GetDistance(target, position);
        return double.IsFinite(distance) && distance <= StartDistance;
    }

    internal void EnterPoint(int currentIndex)
    {
        if (!HasObservationFor(currentIndex))
            Clear();
    }

    internal bool HasObservationFor(int waypointIndex) => _progress != null && _ownerIndex == waypointIndex;

    internal void Start(int waypointIndex, PickupWaypointProgress progress, int? resumeIndex)
    {
        if (_disposed || _progress != null)
        {
            if (!ReferenceEquals(_progress, progress))
                progress.Dispose();
            return;
        }

        _ownerIndex = waypointIndex;
        _resumeIndex = resumeIndex;
        _progress = progress;
    }

    internal PickupWaypointProgress? GetForPoint(int waypointIndex, out int? resumeIndex)
    {
        if (!HasObservationFor(waypointIndex))
        {
            resumeIndex = null;
            return null;
        }

        resumeIndex = _resumeIndex;
        return _progress;
    }

    private void Clear()
    {
        _progress?.Dispose();
        _progress = null;
        _ownerIndex = -1;
        _resumeIndex = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Clear();
    }
}
