#nullable enable

using BetterGenshinImpact.GameTask.AutoTrackPath;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// FastDragAreaSwitchSkipDecisions 的 PBT 属性测试。
/// **Validates: bugfix.md BC-1 / BC-3 (design.md 组件1)**
/// </summary>
public class FastDragAreaSwitchSkipDecisionsTest
{
    /// <summary>属性 1（主规格）：ShouldSkip == (retryTimes == 0 && hasPrior)。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_EqualsFirstAttemptAndPrior(int retryTimes, bool hasPrior)
    {
        var actual = FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, hasPrior);
        return (actual == (retryTimes == 0 && hasPrior)).ToProperty();
    }

    /// <summary>属性 2（Preservation / BC-3）：重试轮恒不跳过。
    /// 蕴含用布尔代数 !A || B 表达（FsCheck C# 无 ==&gt; 运算符，仓库测试惯例见 SyncPointRouteSkipAlignment 系列 / TeleportCdCompensationDecisionsTest）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_RetryNeverSkips(NonNegativeInt retryTimes, bool hasPrior)
    {
        var actual = FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes.Get, hasPrior);
        return (retryTimes.Get <= 0 || !actual).ToProperty();
    }

    /// <summary>属性 3（Preservation）：无先验恒不跳过（保持现状切区）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_NoPriorNeverSkips(int retryTimes)
    {
        var actual = FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, false);
        return (!actual).ToProperty();
    }

    /// <summary>属性 4（显式边界）：首次尝试 + 无先验 → 不跳过。</summary>
    [Property]
    public Property ShouldSkip_FirstAttemptWithoutPrior_False()
    {
        return (!FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(0, false)).ToProperty();
    }

    /// <summary>属性 5（显式边界）：重试 + 有先验 → 不跳过（CC2 保底）。</summary>
    [Property]
    public Property ShouldSkip_RetryWithPrior_False()
    {
        return (!FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(1, true)).ToProperty();
    }
}
