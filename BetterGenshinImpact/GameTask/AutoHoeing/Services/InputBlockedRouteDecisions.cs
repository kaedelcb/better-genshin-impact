using System;
using System.Linq;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 模拟键鼠被系统阻塞（失焦/锁屏/UAC/安全桌面）时的协同重跑决策。
///
/// 输入阻塞是本地可恢复故障，不应直接升级为全队中止；但把线路降级为 Incomplete
/// 必须同时保证它仍能进入轮末重跑计划，否则会静默少打一条线。
/// 连续多次阻塞仍无法恢复时回到原有 Failed 语义，避免输入环境长期损坏时整轮静默丢线路。
/// </summary>
public static class InputBlockedRouteDecisions
{
    /// <summary>连续降级达到该次数后回到 Failed。</summary>
    public const int MaxConsecutiveDowngrades = 2;

    /// <summary>
    /// 是否可把本次输入阻塞降级为 Incomplete。
    /// 正常轮必须同时满足"线路可重跑 + 已有规范战斗点"，否则线路不会被轮末计划补跑，
    /// 降级会变成静默少打；这种情况宁可回到 Failed 让整队重开。
    /// 重跑轮的计划已冻结，Incomplete 是协议内终态，不需要重跑标记。
    /// </summary>
    public static bool ShouldDowngrade(
        int consecutiveDowngrades, bool isReplay, bool eligible, string? canonicalFightPointId)
        => consecutiveDowngrades < MaxConsecutiveDowngrades
           && (isReplay || (eligible && !string.IsNullOrWhiteSpace(canonicalFightPointId)));

    /// <summary>
    /// 只有正常轮（非重跑轮）才需要补报轮末重跑标记：重跑轮计划已冻结，
    /// Incomplete 本身就是协议内终态。线路必须可重跑，且已有可上报的规范战斗点。
    /// </summary>
    public static bool ShouldReportRoundEndRerun(
        bool isReplay, bool eligible, string? canonicalFightPointId)
        => !isReplay && eligible && !string.IsNullOrWhiteSpace(canonicalFightPointId);

    /// <summary>降级时累加连续计数；未降级时归零。</summary>
    public static int NextConsecutiveDowngradeCount(bool downgraded, int current)
        => downgraded ? current + 1 : 0;

    /// <summary>识别直接抛出、AggregateException 包裹或内层包裹的输入阻塞异常。</summary>
    public static bool IsInputBlocked(Exception? exception)
    {
        if (exception is WindowsInputBlockedException) return true;
        if (exception is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Any(IsInputBlocked);
        return exception?.InnerException != null && IsInputBlocked(exception.InnerException);
    }

    /// <summary>取出真正的输入阻塞原因，避免把 AggregateException 的笼统消息写进线路失败原因。</summary>
    public static string InputBlockedReason(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var inner = aggregate.Flatten().InnerExceptions.FirstOrDefault(IsInputBlocked);
            if (inner != null) return inner.Message;
        }

        return exception.Message;
    }
}
