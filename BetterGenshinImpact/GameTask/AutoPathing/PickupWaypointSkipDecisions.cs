using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal static class PickupWaypointSkipDecisions
{
    internal static int? GetResumeIndex(IReadOnlyList<Waypoint> segment, int currentIndex, int skipCount)
    {
        // 路线按传送点分段。必须在当前段留下一个可直接前往的续接点。
        if (currentIndex < 0 || currentIndex >= segment.Count || skipCount <= 0
            || skipCount > segment.Count - currentIndex - 2)
        {
            return null;
        }

        var resumeIndex = currentIndex + skipCount + 1;
        if (!IsMovementPoint(segment[resumeIndex]))
            return null;

        for (var index = currentIndex + 1; index < resumeIndex; index++)
        {
            var point = segment[index];
            if (!IsMovementPoint(point) || !string.IsNullOrWhiteSpace(point.SyncPointId)
                || !IsPickupDetourAction(point.Action))
            {
                return null;
            }
        }

        return resumeIndex;
    }

    private static bool IsMovementPoint(Waypoint point)
        => point.Type == WaypointType.Path.Code || point.Type == WaypointType.Target.Code;

    private static bool IsPickupDetourAction(string? action)
        => string.IsNullOrEmpty(action) || action == ActionEnum.CombatScript.Code
            || action == ActionEnum.PickAround.Code || action == ActionEnum.PickUpCollect.Code;
}
