using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

public static class RouteRerunDecisions
{
    /// <summary>线路需重跑标记判定："只重跑一次"——既不在 markSet（避免重复标记）也不在 doneSet（避免重复重跑）。</summary>
    public static bool ShouldMarkRerun(int routeIndex, HashSet<int> markSet, HashSet<int> doneSet)
        => !markSet.Contains(routeIndex) && !doneSet.Contains(routeIndex);
}
