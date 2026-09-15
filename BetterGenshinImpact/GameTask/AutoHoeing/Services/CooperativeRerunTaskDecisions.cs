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
}
