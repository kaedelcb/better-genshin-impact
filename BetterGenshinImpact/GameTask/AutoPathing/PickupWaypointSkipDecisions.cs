using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing;

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
            if (point.PickupTargetCount.HasValue || (owner is WaypointForTrack origin && point is WaypointForTrack next
                    && (origin.MapName != next.MapName || origin.MapMatchMethod != next.MapMatchMethod)))
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
