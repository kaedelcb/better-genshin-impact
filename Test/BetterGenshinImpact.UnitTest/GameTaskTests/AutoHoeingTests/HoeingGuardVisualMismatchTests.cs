#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// HoeingGuardDecisions.ShouldTriggerVisualMismatchExit 的 PBT 属性测试。
/// **Validates: requirements.md BC-2（视觉失明检测）/ design.md §5**
/// </summary>
public class HoeingGuardVisualMismatchTests
{
    // P-1 门控关：单机/守护关闭恒不触发（单机零感知）
    [Property(MaxTest = 200)]
    public bool ShouldTriggerVisualMismatchExit_GuardOff_Never(
        bool suppressed, int visualCount, int coordinatorCount, double continuousSeconds, double windowSeconds)
    {
        return !HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            false, suppressed, visualCount, coordinatorCount, continuousSeconds, windowSeconds);
    }

    // P-2 抑制窗口：恒不触发
    [Property(MaxTest = 200)]
    public bool ShouldTriggerVisualMismatchExit_Suppressed_Never(
        bool guardOn, int visualCount, int coordinatorCount, double continuousSeconds)
    {
        return !HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            guardOn, true, visualCount, coordinatorCount, continuousSeconds, 30.0);
    }

    // P-3 视觉<=0（识别不到）：恒不触发
    [Property(MaxTest = 200)]
    public bool ShouldTriggerVisualMismatchExit_VisualZero_Never(
        bool guardOn, bool suppressed, int coordinatorCount, double continuousSeconds)
    {
        return !HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            guardOn, suppressed, 0, coordinatorCount, continuousSeconds, 30.0);
    }

    // P-4 视觉>=协调器（无失明）：即使持续满窗口也不触发
    [Property(MaxTest = 200)]
    public bool ShouldTriggerVisualMismatchExit_NoMismatch_Never(
        bool guardOn, bool suppressed, int coordinatorCount, double continuousSeconds)
    {
        var visualCount = System.Math.Max(1, coordinatorCount); // 视觉 >= 协调器
        return !HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            guardOn, suppressed, visualCount, coordinatorCount, continuousSeconds, 30.0);
    }

    // P-5 视觉<协调器但未满窗口：不触发
    [Fact]
    public void ShouldTriggerVisualMismatchExit_BelowCoordinatorButNotEnoughTime_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 3, 4, 29.0, 30.0));
        Assert.False(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 1, 4, 5.9, 30.0));
        Assert.False(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 4, 4, 1000.0, 30.0)); // 相等：无失明
    }

    // P-6 视觉<协调器且满窗口：触发
    [Fact]
    public void ShouldTriggerVisualMismatchExit_BelowCoordinatorAndEnoughTime_IsTrue()
    {
        Assert.True(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 3, 4, 30.0, 30.0));
        Assert.True(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 2, 4, 45.0, 30.0));
        Assert.True(HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
            true, false, 1, 3, 100.0, 30.0));
    }
}