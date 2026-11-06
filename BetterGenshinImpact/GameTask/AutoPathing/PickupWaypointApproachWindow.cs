using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>保留当前路线段的一次拾取观察，覆盖接近、节点动作和获准的首个补捡路段。</summary>
internal sealed class PickupWaypointApproachWindow : IDisposable
{
    internal const double StartDistance = 5;

    private int _ownerIndex = -1;
    private int? _resumeIndex;
    private int? _departureResumeIndex;
    private PickupWaypointProgress? _progress;
    private bool _disposed;
    private int? _moveBeforeConfirmationIndex;

    internal static int? SelectObservationIndex(IReadOnlyList<Waypoint> segment, int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= segment.Count)
            return null;

        var current = segment[currentIndex];
        if (IsPickupTarget(current))
            return currentIndex;

        // 当前带参数的点优先；其它节点可预备下一点，传送后的观察由新段接管。
        if (currentIndex + 1 >= segment.Count || current.Type == WaypointType.Teleport.Code)
            return null;

        return IsPickupTarget(segment[currentIndex + 1]) ? currentIndex + 1 : null;
    }

    internal static bool IsPickupTarget(Waypoint waypoint)
        => waypoint.PickupTargetCount is { Count: > 0 };

    internal static bool IsWithinStartDistance(Waypoint target, Point2f position, out double distance)
    {
        distance = double.PositiveInfinity;
        if ((position.X == 0 && position.Y == 0) || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return false;

        distance = Navigation.GetDistance(target, position);
        return double.IsFinite(distance) && distance <= StartDistance;
    }

    internal bool EnterPoint(int currentIndex)
    {
        var moveBeforeConfirmation = _moveBeforeConfirmationIndex == currentIndex;
        _moveBeforeConfirmationIndex = null; // 只属于实际下一点，无移动或改走其它点也不顺延。
        if (!HasObservationFor(currentIndex) && !IsDepartureFor(currentIndex))
            Clear();
        return moveBeforeConfirmation;
    }

    internal bool BeginDeparture(int ownerIndex, int resumeIndex)
    {
        if (!HasObservationFor(ownerIndex) || _resumeIndex is not { } plannedResume
            || resumeIndex <= ownerIndex + 1 || resumeIndex > plannedResume || !_progress!.BeginDeparture())
            return false;

        _departureResumeIndex = resumeIndex;
        return true;
    }

    internal bool IsDepartureFor(int waypointIndex) =>
        _departureResumeIndex.HasValue && waypointIndex == _ownerIndex + 1;

    internal Action<Point2f>? WithDepartureObserver(IReadOnlyList<Waypoint> segment, int currentIndex,
        Func<Waypoint, bool> canObserve, Action<Point2f>? observeApproach)
    {
        if (!IsDepartureFor(currentIndex))
            return observeApproach;

        var owner = segment[currentIndex - 1];
        // 下一点的归属边界与能否提前开启它的观察独立：补捡点有动作时也不能串计。
        var nextPickup = currentIndex + 1 < segment.Count && IsPickupTarget(segment[currentIndex + 1])
            && SharesPositionSpace(owner, segment[currentIndex + 1]) ? segment[currentIndex + 1] : null;

        return position =>
        {
            var approachingNextPickup = nextPickup != null && IsWithinStartDistance(nextPickup, position, out _);
            ObserveDeparturePosition(currentIndex, owner,
                !approachingNextPickup && canObserve(owner) ? position : default);
            if (!IsDepartureFor(currentIndex))
                observeApproach?.Invoke(position);
        };
    }

    internal static bool SharesPositionSpace(Waypoint origin, Waypoint next)
        => origin is not WaypointForTrack from || next is not WaypointForTrack to
           || (from.MapName == to.MapName && from.MapMatchMethod == to.MapMatchMethod);

    internal void ObserveDeparturePosition(int waypointIndex, Waypoint owner, Point2f position)
    {
        if (!IsDepartureFor(waypointIndex))
            return;

        if (IsWithinStartDistance(owner, position, out _) && _progress!.ConfirmDeparturePosition())
            return;

        // 关闭与计数使用同一把锁，先前已达标则保留结果给移动后的安全切换点。
        _progress!.Dispose();
        if (!_progress.ReachedToken.IsCancellationRequested)
            Clear();
    }

    internal int? FinishDeparture(int waypointIndex)
    {
        if (!IsDepartureFor(waypointIndex))
            return null;

        _progress!.Dispose(); // 先封存，再读结果；此后到达的帧不能改变路线。
        var resume = _progress.ReachedToken.IsCancellationRequested ? _departureResumeIndex : null;
        if (resume.HasValue)
        {
            _progress.RecordReachedCompletion();
            _moveBeforeConfirmationIndex = resume;
        }
        Clear();
        return resume;
    }

    internal bool ActivateForPoint(int waypointIndex)
        => HasObservationFor(waypointIndex) && _progress!.ActivateCounting();

    internal int? CompletePoint(IReadOnlyList<Waypoint> segment, int waypointIndex)
    {
        if (!HasObservationFor(waypointIndex))
            return null;

        // 策略可能已经收尾并Dispose；只有正常达标完成记录才可加速下一点，报错不算。
        if (_progress!.TargetCompleted)
            _moveBeforeConfirmationIndex = _resumeIndex ?? waypointIndex + 1;
        if (_progress.IsDisposed)
            return null;

        if (_progress.ReachedToken.IsCancellationRequested)
        {
            _progress.RecordReachedCompletion();
            _moveBeforeConfirmationIndex = _resumeIndex ?? waypointIndex + 1;
            _progress.Dispose();
            _progress.LogPointCompleted(departing: false);
            return _resumeIndex;
        }

        var departureResume = PickupWaypointSkipDecisions.GetDepartureResumeIndex(segment, waypointIndex, _resumeIndex);
        if (departureResume is { } resume && BeginDeparture(waypointIndex, resume))
        {
            _progress.LogPointCompleted(departing: true);
            return null;
        }

        _progress.Dispose();
        _progress.LogPointCompleted(departing: false);
        return null;
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

    internal PickupWaypointProgress? GetForArrival(int waypointIndex)
        => (HasObservationFor(waypointIndex) || IsDepartureFor(waypointIndex)) && !_progress!.IsDisposed
            ? _progress : null;

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
        _departureResumeIndex = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _moveBeforeConfirmationIndex = null;
        Clear();
    }
}

internal static class PickupWaypointSkipDecisions
{
    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount)
        => GetResumeIndex(segment, currentIndex, skipCount, out _);

    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount, out string? reason)
        => GetResumeIndex(segment, currentIndex, skipCount, isLastSegment: false, out reason);

    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount,
        bool isLastSegment, out string? reason)
    {
        // 路线按传送点分段。仅最后一段可恰好跳完，直接结束整条路线。
        if (currentIndex < 0 || currentIndex >= segment.Count || skipCount <= 0)
        {
            reason = "当前点或跳点数量无效";
            return null;
        }

        var remaining = segment.Count - currentIndex - 1;
        if (skipCount > remaining || (skipCount == remaining && !isLastSegment))
        {
            reason = $"当前传送段只剩 {remaining} 个点，跳过 {skipCount} 个点后无法在本段保留续接点";
            return null;
        }

        var resumeIndex = currentIndex + skipCount + 1;
        if (resumeIndex < segment.Count && !IsMovementPoint(segment[resumeIndex]))
        {
            reason = $"跳过后的续接点（后第 {skipCount + 1} 点）不是可走到的点位，type={segment[resumeIndex].Type}";
            return null;
        }

        for (var index = currentIndex + 1; index < resumeIndex; index++)
        {
            var point = segment[index];
            if (!IsMovementPoint(point) || !string.IsNullOrWhiteSpace(point.SyncPointId)
                || !IsPickupDetourAction(point.Action))
            {
                var sync = string.IsNullOrWhiteSpace(point.SyncPointId) ? "" : "，是同步点";
                reason = $"后第 {index - currentIndex} 点不可跳过，type={point.Type}，action={point.Action ?? "(空)"}{sync}";
                return null;
            }
        }

        reason = null;
        return resumeIndex;
    }

    internal static int? GetDepartureResumeIndex(IReadOnlyList<Waypoint> segment, int ownerIndex, int? plannedResume)
    {
        if (ownerIndex < 0 || ownerIndex >= segment.Count || plannedResume is not { } resume
            || resume <= ownerIndex + 1 || resume > segment.Count)
            return null;

        var owner = segment[ownerIndex];
        for (var index = ownerIndex + 1; index < resume; index++)
        {
            var point = segment[index];
            if (point.PickupTargetCount.HasValue || !PickupWaypointApproachWindow.SharesPositionSpace(owner, point))
                return index == ownerIndex + 1 ? null : index;
        }

        return resume;
    }

    private static bool IsMovementPoint(Waypoint point)
        => point.Type == WaypointType.Path.Code || point.Type == WaypointType.Target.Code;

    private static bool IsPickupDetourAction(string? action)
        => string.IsNullOrEmpty(action) || action == ActionEnum.CombatScript.Code
            || action == ActionEnum.PickAround.Code || action == ActionEnum.PickUpCollect.Code;
}
