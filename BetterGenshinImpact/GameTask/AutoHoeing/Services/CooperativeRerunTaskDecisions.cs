using System;
using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

public static class CooperativeRerunTaskDecisions
{
    public static bool IsEnabled(bool multiplayer, string? keywords)
        => multiplayer && RouteRetryModeDecisions.ParseKeywords(keywords).Count > 0;

    /// <summary>
    /// 是否真正启用新的协同重跑。
    /// 兼容门控（确认规则：不支持的组合不得半启用，但也不得因此破坏正常锄地）：
    /// 房间内任一参与者未宣告 hoeing.rerun.v1 时，本功能整体不启用，本端退回旧的轮末重跑路径
    /// 并明确告警——而不是让整轮锄地失败。
    /// </summary>
    public static bool IsEnabled(bool multiplayer, string? keywords, bool serverSupportsCapability)
        => IsEnabled(multiplayer, keywords) && serverSupportsCapability;

    public static void ThrowIfTerminalFailure(RerunRouteOutcome outcome)
    {
        if (outcome == RerunRouteOutcome.Failed)
            throw new InvalidOperationException("共同重跑路线失败");
        if (outcome == RerunRouteOutcome.Cancelled)
            throw new OperationCanceledException("共同重跑路线被取消");
    }

    public static bool ShouldStatue(RerunStage stage, int replayCount)
        => replayCount > 0 && stage == RerunStage.Completed;

    /// <summary>
    /// 神像门禁只有收到明确的全员到达才允许进入重跑；失败和超时都必须安全取消本次重跑。
    /// </summary>
    public static bool AllowsStatueRerun(
        BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.SyncBarrierWaitResult result)
        => result == BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.SyncBarrierWaitResult.AllArrived;

    /// <summary>本地传送失败时是否还有门禁重试机会。</summary>
    public static bool ShouldRetryStatueTeleport(int attempt, int maxAttempts)
        => attempt < maxAttempts;

    /// <summary>
    /// 固定本轮门禁人数：优先采用房间名册快照，名册暂不可用时才回退配置人数。
    /// </summary>
    public static int ResolveExpectedPlayerCount(int rosterCount, int configuredCount)
        => Math.Clamp(rosterCount > 0 ? rosterCount : configuredCount, 1, 4);
}
