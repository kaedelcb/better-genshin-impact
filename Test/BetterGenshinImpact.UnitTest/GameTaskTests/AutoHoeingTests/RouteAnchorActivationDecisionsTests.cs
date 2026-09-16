#nullable enable

using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 路线边界锚点激活/放行判定测试（route-anchor）。
///
/// 重点：所有判定都必须是"安全默认"——任一条件不满足即不激活；非 Released 一律不放行。
/// 这些判定是"忘记接线就静默半启用"和"拿不到放行还继续跑"这两类事故的唯一防线。
/// </summary>
public class RouteAnchorActivationDecisionsTests
{
    // =========================================================================
    // 激活判定：四个条件缺一不可
    // =========================================================================

    [Fact]
    public void ShouldActivate_AllConditionsMet_True()
        => Assert.True(RouteAnchorActivationDecisions.ShouldActivate(true, true, true, true));

    [Theory]
    [InlineData(false, true, true, true)]    // 配置关闭
    [InlineData(true, false, true, true)]    // 单机
    [InlineData(true, true, false, true)]    // 不在房间/未连接
    [InlineData(true, true, true, false)]    // 服务端不支持
    [InlineData(false, false, false, false)]
    public void ShouldActivate_AnyConditionMissing_False(
        bool config, bool multiplayer, bool inRoom, bool serverCapability)
        => Assert.False(RouteAnchorActivationDecisions.ShouldActivate(config, multiplayer, inRoom, serverCapability));

    // =========================================================================
    // 放行判定：只有 Released 允许进入下一条路线
    // =========================================================================

    [Fact]
    public void AllowsNextRoute_OnlyReleased()
    {
        Assert.True(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Released));

        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Stopped));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Failed));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Cancelled));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Disabled));
    }

    // =========================================================================
    // 计划标识：只由顺序与文件名决定（各成员必须一致）
    // =========================================================================

    [Fact]
    public void BuildPlanId_StableForSameOrder_DiffersForDifferentOrderOrContent()
    {
        var a = RouteAnchorActivationDecisions.BuildPlanId(new List<string> { "r1.json", "r2.json", "r3.json" });
        var b = RouteAnchorActivationDecisions.BuildPlanId(new List<string> { "r1.json", "r2.json", "r3.json" });
        var reordered = RouteAnchorActivationDecisions.BuildPlanId(new List<string> { "r2.json", "r1.json", "r3.json" });
        var different = RouteAnchorActivationDecisions.BuildPlanId(new List<string> { "r1.json", "r2.json", "r4.json" });

        Assert.Equal(a, b);
        Assert.StartsWith("plan-3-", a);
        Assert.NotEqual(a, reordered);
        Assert.NotEqual(a, different);
    }

    [Fact]
    public void BuildPlanId_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal("", RouteAnchorActivationDecisions.BuildPlanId(new List<string>()));
        Assert.Equal("", RouteAnchorActivationDecisions.BuildPlanId(null!));
    }

    // =========================================================================
    // 结果类型推导：只看跳过计数是否变化（避免在每个跳过出口插桩）
    // =========================================================================

    [Fact]
    public void ResolveOutcome_SkippedOnlyWhenCounterChanged()
    {
        Assert.Equal(BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.OutcomeCompleted,
            RouteAnchorActivationDecisions.ResolveOutcome(3, 3));
        Assert.Equal(BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.OutcomeSkipped,
            RouteAnchorActivationDecisions.ResolveOutcome(3, 4));
    }
}
