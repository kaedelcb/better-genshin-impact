#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Pull 跳转判定测试（route-anchor · 阶段 4）。
///
/// 这些判定是"跳错路线"的唯一防线：漏执行路线**不会补跑**，而重复执行会污染 CD/统计，
/// 所以任何不确定都必须判为"不跳并回报失败"。
/// </summary>
public class RouteAnchorJumpDecisionsTests
{
    // 场景：本轮计划 20 条路线，从 0 开始；当前正在执行第 5 条。

    [Theory]
    [InlineData(6)]     // 正常向前一跳
    [InlineData(10)]    // 大部队已走远，服务端要求直接跳到更后面
    [InlineData(19)]    // 最后一条
    public void CanJump_ForwardWithinPlan_True(int target)
        => Assert.True(RouteAnchorJumpDecisions.CanJump(5, 0, target, 20));

    [Theory]
    [InlineData(5)]     // 目标等于当前：无需跳（也就不该走跳转分支）
    [InlineData(3)]     // 向后跳：会重复执行已完成路线 → 非法
    [InlineData(-1)]    // 非法索引（也覆盖服务端轮末哨兵误入此路径）
    [InlineData(20)]    // 等于 routeCount：越界
    [InlineData(99)]    // 远超范围
    public void CanJump_NotStrictlyForwardOrOutOfRange_False(int target)
        => Assert.False(RouteAnchorJumpDecisions.CanJump(5, 0, target, 20));

    [Fact]
    public void CanJump_RespectsStartIndex()
    {
        // 调试起始索引 startIndex=3：目标 2 在起点之前 → 非法；目标 3 等于当前 → 非法；目标 5 合法
        Assert.False(RouteAnchorJumpDecisions.CanJump(3, 3, 2, 20));
        Assert.False(RouteAnchorJumpDecisions.CanJump(3, 3, 3, 20));
        Assert.True(RouteAnchorJumpDecisions.CanJump(3, 3, 5, 20));
    }

    [Fact]
    public void CanJump_DegenerateInputs_False()
    {
        Assert.False(RouteAnchorJumpDecisions.CanJump(0, 0, 1, 0));       // 空计划
        Assert.False(RouteAnchorJumpDecisions.CanJump(0, -1, 1, 5));      // 非法 startIndex
    }

    // =========================================================================
    // 循环变量换算：写 target-1（continue 后自增恰好落到 target）
    // =========================================================================

    [Theory]
    [InlineData(1, 0)]
    [InlineData(6, 5)]
    [InlineData(19, 18)]
    public void ResolveLoopIndexForJump_IsTargetMinusOne(int target, int expected)
        => Assert.Equal(expected, RouteAnchorJumpDecisions.ResolveLoopIndexForJump(target));

    [Fact]
    public void ResolveLoopIndexForJump_ChainsWithForLoopIncrement_ToLandExactlyOnTarget()
    {
        // 契约验证：把 for 循环的自增语义也纳入断言，防止接线时写成 target 而漏执行目标路线
        for (var target = 1; target <= 5; target++)
        {
            var routeIndex = RouteAnchorJumpDecisions.ResolveLoopIndexForJump(target);
            routeIndex++;   // for 的 routeIndex++
            Assert.Equal(target, routeIndex);
        }
    }

    // =========================================================================
    // 失败语义与中断判定
    // =========================================================================

    [Fact]
    public void ShouldReportPullFailure_IsInverseOfCanJump()
    {
        Assert.False(RouteAnchorJumpDecisions.ShouldReportPullFailure(true));
        Assert.True(RouteAnchorJumpDecisions.ShouldReportPullFailure(false));
    }

    [Fact]
    public void ShouldAbortCurrentRoute_OnlyWhenTargetIsAhead()
    {
        Assert.True(RouteAnchorJumpDecisions.ShouldAbortCurrentRoute(5, 6));
        Assert.True(RouteAnchorJumpDecisions.ShouldAbortCurrentRoute(5, 10));
        Assert.False(RouteAnchorJumpDecisions.ShouldAbortCurrentRoute(5, 5));
        Assert.False(RouteAnchorJumpDecisions.ShouldAbortCurrentRoute(5, 4));
    }
}
