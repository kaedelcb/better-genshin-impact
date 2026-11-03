using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal static class PickupWaypointSkipDecisions
{
    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount)
        => GetResumeIndex(segment, currentIndex, skipCount, out _);

    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount, out string? reason)
    {
        // 路线按传送点分段。必须在当前段留下一个可直接前往的续接点。
        if (currentIndex < 0 || currentIndex >= segment.Count || skipCount <= 0)
        {
            reason = "当前点或跳点数量无效";
            return null;
        }

        var remaining = segment.Count - currentIndex - 1;
        if (skipCount >= remaining)
        {
            reason = $"当前传送段只剩 {remaining} 个点，跳过 {skipCount} 个点后无法在本段保留续接点";
            return null;
        }

        var resumeIndex = currentIndex + skipCount + 1;
        if (!IsMovementPoint(segment[resumeIndex]))
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

    private static bool IsMovementPoint(Waypoint point)
        => point.Type == WaypointType.Path.Code || point.Type == WaypointType.Target.Code;

    private static bool IsPickupDetourAction(string? action)
        => string.IsNullOrEmpty(action) || action == ActionEnum.CombatScript.Code
            || action == ActionEnum.PickAround.Code || action == ActionEnum.PickUpCollect.Code;
}
