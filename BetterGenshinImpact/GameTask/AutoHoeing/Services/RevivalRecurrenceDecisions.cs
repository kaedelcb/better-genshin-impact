using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 反复复苏升级动作枚举。
/// </summary>
public enum RevivalEscalationAction
{
    /// <summary>未触发升级，沿用现有"走回战斗点"流程。</summary>
    Continue = 0,

    /// <summary>跳本段（与同步点后异常对齐）+ 神像回血。</summary>
    SkipSegment = 1,

    /// <summary>跳整路线 + 神像回血（防死循环）。</summary>
    SkipRoute = 2,

}

/// <summary>
/// 反复复苏双层兜底纯函数决策层（PBT 友好，无副作用）。
/// 详见 .kiro/specs/multi-revival-rapid-recurrence-fallback/design.md §6 Correctness Properties。
/// </summary>
public static class RevivalRecurrenceDecisions
{
    /// <summary>
    /// 统计 [now - windowSeconds, now] 闭区间内的复苏次数。
    /// </summary>
    public static int CountInWindow(IReadOnlyList<DateTime> timestamps, DateTime now, int windowSeconds)
    {
        if (timestamps == null || timestamps.Count == 0 || windowSeconds <= 0) return 0;
        var threshold = now - TimeSpan.FromSeconds(windowSeconds);
        int n = 0;
        for (int i = 0; i < timestamps.Count; i++)
        {
            var t = timestamps[i];
            if (t >= threshold && t <= now) n++;
        }
        return n;
    }

    /// <summary>
    /// 决策函数：根据复苏时间戳序列与配置阈值返回升级动作。
    /// 优先级：SkipRoute > SkipSegment > Continue。
    /// 防御性：任意非正参数返回 Continue（避免误升级）。
    /// </summary>
    public static RevivalEscalationAction Decide(
        IReadOnlyList<DateTime> timestamps,
        DateTime now,
        int windowSeconds,
        int rapidThreshold,
        int routeCap)
    {
        if (timestamps == null) return RevivalEscalationAction.Continue;
        if (windowSeconds <= 0 || rapidThreshold <= 0 || routeCap <= 0)
            return RevivalEscalationAction.Continue;

        // 优先级 1：路线累计达上限 → SkipRoute（即便同时也满足 rapid，cap 优先）
        if (timestamps.Count >= routeCap) return RevivalEscalationAction.SkipRoute;

        // 优先级 2：窗口内累计达 rapidThreshold → SkipSegment
        if (CountInWindow(timestamps, now, windowSeconds) >= rapidThreshold)
            return RevivalEscalationAction.SkipSegment;

        return RevivalEscalationAction.Continue;
    }
}
