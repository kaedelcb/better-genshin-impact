#nullable restore

using System;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 路线边界"是否需要提交/收口"的纯判定（route-anchor）。
///
/// 为什么要单独抽出来：这段守卫原先直接写在 <c>AutoHoeingTask</c> 的路线循环里，
/// 是**唯一没有测试覆盖的接线分支**——而第十一轮那个"第二轮门控静默失效"的缺陷正出在这里
/// （跨轮未复位 `lastSubmittedBoundary` → 守卫恒为真 → 整轮不提交边界且无任何日志）。
/// 抽成纯函数后，这个分支可以被直接撒输入验证。
/// </summary>
public static class RouteAnchorBoundaryDecisions
{
    /// <summary>
    /// 进入第 <paramref name="currentRouteIndex"/> 条路线前，是否需要为"上一条边界"走收口流程。
    ///
    /// 返回 false（即"无需提交"）的三种情况，都必须显式成立、不得靠副作用：
    ///   1. 首条路线：其上没有边界，轮次起点由既有 route_sync_done 覆盖；
    ///   2. 该边界已提交过（<paramref name="lastSubmittedBoundary"/> 记录）——幂等，避免重复提交；
    ///   3. 越界（currentRouteIndex 不在 [startIndex, routeCount) 内）——异常输入不得推进状态机。
    /// </summary>
    public static bool ShouldSubmitBoundary(
        int currentRouteIndex, int startIndex, int routeCount, int lastSubmittedBoundary)
    {
        if (routeCount <= 0) return false;
        if (currentRouteIndex <= startIndex) return false;          // 首条路线 / 起点之前
        if (currentRouteIndex >= routeCount) return false;          // 越界
        var completedBoundary = currentRouteIndex - 1;
        return completedBoundary > lastSubmittedBoundary;            // 同一边界不重复提交
    }

    /// <summary>轮末是否需要收口：本轮确实执行过路线、尚未因停止而退出、且该边界未提交过。</summary>
    public static bool ShouldFinishRound(
        int lastExecutedRouteIndex, int startIndex, int routeCount, int lastSubmittedBoundary, bool sessionTerminated)
    {
        if (sessionTerminated) return false;
        if (routeCount <= 0) return false;
        if (lastExecutedRouteIndex < startIndex) return false;
        if (lastExecutedRouteIndex >= routeCount) return false;
        return lastExecutedRouteIndex > lastSubmittedBoundary;
    }

    /// <summary>
    /// 跳转后应写入循环计数器的值，保持 <c>currentRouteIndex = startIndex + count</c> 不变量
    /// （见方案附录 A.1：只改 routeIndex 会让日志/进度/守护计数与实际执行的路线错位）。
    /// </summary>
    public static int ResolveCountAfterJump(int targetRouteIndex, int startIndex) => targetRouteIndex - startIndex;
}
