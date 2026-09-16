#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 路线边界"是否需要提交/收口"的判定测试（route-anchor）。
///
/// 这段判定原先只存在于 AutoHoeingTask 的循环里、没有任何测试覆盖，
/// 而第十一轮那个"第二轮门控静默失效"的缺陷正出在这里（守卫恒为真 → 整轮不提交边界且无日志）。
/// 现在把它钉成契约：**凡是"无需提交"的情形都必须显式可验证**，不允许靠副作用成立。
/// </summary>
public class RouteAnchorBoundaryDecisionsTests
{
    // 场景：本轮 20 条路线，startIndex=0。

    [Fact]
    public void ShouldSubmitBoundary_NormalProgression_SubmitsEachBoundaryOnce()
    {
        var lastSubmitted = -1;

        // 第 1 条路线（currentRouteIndex=0）：没有上一条边界 → 不提交
        Assert.False(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(0, 0, 20, lastSubmitted));

        // 第 2 条路线（=1）：为边界 0 提交
        Assert.True(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(1, 0, 20, lastSubmitted));
        lastSubmitted = 0;

        // 同一边界不重复提交
        Assert.False(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(1, 0, 20, lastSubmitted));

        // 继续推进：边界 1 可提交
        Assert.True(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(2, 0, 20, lastSubmitted));
    }

    [Fact]
    public void ShouldSubmitBoundary_SecondRoundAfterReset_WorksAgain()
    {
        // 跨轮复位后的关键场景（第十一轮缺陷的正例）：
        // 上一轮已提交到边界 19；新一轮把 lastSubmittedBoundary 复位为 -1 后，边界 0 必须能再次提交。
        Assert.False(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(1, 0, 20, lastSubmittedBoundary: 19));  // 未复位 → 静默跳过
        Assert.True(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(1, 0, 20, lastSubmittedBoundary: -1));   // 已复位 → 正常提交
    }

    [Fact]
    public void ShouldSubmitBoundary_WithStartIndex_RespectsOffset()
    {
        // 调试起始索引 startIndex=3：currentRouteIndex=3 是首条 → 不提交；=4 → 提交边界 3
        Assert.False(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(3, 3, 20, -1));
        Assert.True(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(4, 3, 20, -1));
    }

    [Theory]
    [InlineData(0, 0, 0)]      // 空计划
    [InlineData(20, 0, 20)]    // 越界（等于 routeCount）
    [InlineData(25, 0, 20)]    // 越界
    [InlineData(0, 5, 20)]     // 起点之前
    public void ShouldSubmitBoundary_DegenerateInputs_False(int current, int startIndex, int routeCount)
        => Assert.False(RouteAnchorBoundaryDecisions.ShouldSubmitBoundary(current, startIndex, routeCount, -1));

    // =========================================================================
    // 轮末收口
    // =========================================================================

    [Fact]
    public void ShouldFinishRound_RequiresExecutedRouteAndNotTerminated()
    {
        Assert.True(RouteAnchorBoundaryDecisions.ShouldFinishRound(19, 0, 20, lastSubmittedBoundary: 18, sessionTerminated: false));
        Assert.False(RouteAnchorBoundaryDecisions.ShouldFinishRound(19, 0, 20, lastSubmittedBoundary: 18, sessionTerminated: true));  // 已停止
        Assert.False(RouteAnchorBoundaryDecisions.ShouldFinishRound(19, 0, 20, lastSubmittedBoundary: 19, sessionTerminated: false)); // 该边界已提交
        Assert.False(RouteAnchorBoundaryDecisions.ShouldFinishRound(-1, 0, 20, -1, false));                                            // 未执行任何路线
        Assert.False(RouteAnchorBoundaryDecisions.ShouldFinishRound(20, 0, 20, -1, false));                                            // 越界
    }

    // =========================================================================
    // 跳转后的计数不变量
    // =========================================================================

    [Theory]
    [InlineData(13, 0, 13)]
    [InlineData(13, 3, 10)]
    [InlineData(5, 5, 0)]
    public void ResolveCountAfterJump_KeepsCurrentRouteIndexInvariant(int target, int startIndex, int expected)
    {
        var count = RouteAnchorBoundaryDecisions.ResolveCountAfterJump(target, startIndex);

        Assert.Equal(expected, count);
        Assert.Equal(target, startIndex + count);   // 不变量：currentRouteIndex = startIndex + count
    }
}
