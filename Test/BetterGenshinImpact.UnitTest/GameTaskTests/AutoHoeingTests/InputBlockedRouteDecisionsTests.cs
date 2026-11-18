using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Fischless.WindowsInput;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 输入被系统阻塞时的协同重跑降级决策。
/// 覆盖：连续降级上限、正常轮补报轮末重跑的条件、异常识别与原因提取。
///
/// 验证约定：输入阻塞只有在“线路仍能进入轮末补跑”或“已在重跑轮”时才降级为 Incomplete，
/// 且连续降级有上限；否则必须保留原 Failed 语义。结果改变交付决定：这些用例失败时不得提交本次降级修复。
/// 现有证据不足：真实 SendInput 阻塞/焦点恢复时序和服务器计划冻结无法在单测中复现，
/// 由 CooperativeRerunServerContractTests 与 2026-11-18 实机日志补充。
/// 结束判据：本类全部通过，协同重跑相关回归集无失败。
/// </summary>
public sealed class InputBlockedRouteDecisionsTests
{
    /// <summary>
    /// 降级为 Incomplete 的前提：未达到连续上限，且正常轮能保证轮末补跑（可重跑 + 有规范战斗点）。
    /// 正常轮不满足补跑条件时必须回到 Failed，避免整条线被静默跳过；重跑轮计划已冻结，不需要补跑标记。
    /// </summary>
    [Theory]
    [InlineData(0, false, true, "s:0:fight:0", true)]
    [InlineData(1, false, true, "s:0:fight:0", true)]
    [InlineData(2, false, true, "s:0:fight:0", false)]
    [InlineData(3, false, true, "s:0:fight:0", false)]
    [InlineData(0, false, false, "s:0:fight:0", false)]
    [InlineData(0, false, true, null, false)]
    [InlineData(0, false, true, "", false)]
    [InlineData(0, false, true, "  ", false)]
    [InlineData(0, true, false, null, true)]
    [InlineData(0, true, true, null, true)]
    [InlineData(2, true, true, "s:0:fight:0", false)]
    public void ShouldDowngrade_RequiresBudgetAndPreservableRoute(
        int consecutiveDowngrades, bool isReplay, bool eligible,
        string? canonicalFightPointId, bool expected)
        => Assert.Equal(expected, InputBlockedRouteDecisions.ShouldDowngrade(
            consecutiveDowngrades, isReplay, eligible, canonicalFightPointId));

    /// <summary>
    /// 只有正常轮、线路可重跑、且已进入过规范战斗点时才补报轮末重跑；
    /// 重跑轮计划已冻结，不该再产生新的计划标记。
    /// </summary>
    [Theory]
    [InlineData(false, true, "s:0:fight:0", true)]
    [InlineData(false, true, null, false)]
    [InlineData(false, true, "", false)]
    [InlineData(false, true, "  ", false)]
    [InlineData(false, false, "s:0:fight:0", false)]
    [InlineData(true, true, "s:0:fight:0", false)]
    public void ShouldReportRoundEndRerun_RequiresNormalEligibleCanonicalPoint(
        bool isReplay, bool eligible, string? canonicalFightPointId, bool expected)
        => Assert.Equal(expected, InputBlockedRouteDecisions.ShouldReportRoundEndRerun(
            isReplay, eligible, canonicalFightPointId));

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(false, 2, 0)]
    [InlineData(false, 0, 0)]
    public void NextConsecutiveDowngradeCount_IncrementsOnlyOnDowngrade(
        bool downgraded, int current, int expected)
        => Assert.Equal(expected, InputBlockedRouteDecisions.NextConsecutiveDowngradeCount(downgraded, current));

    /// <summary>
    /// 生产路径可能收到直接异常、Task.WhenAll 的 AggregateException，或其它异常的内层包裹；
    /// 三种都必须识别，避免把可恢复输入故障当成普通线路失败。
    /// </summary>
    [Fact]
    public void IsInputBlocked_RecognizesDirectAggregateAndNested()
    {
        var direct = new WindowsInputBlockedException("blocked");
        Assert.True(InputBlockedRouteDecisions.IsInputBlocked(direct));
        Assert.True(InputBlockedRouteDecisions.IsInputBlocked(new AggregateException(direct)));
        Assert.True(InputBlockedRouteDecisions.IsInputBlocked(
            new InvalidOperationException("outer", direct)));
        Assert.False(InputBlockedRouteDecisions.IsInputBlocked(new InvalidOperationException("other")));
        Assert.False(InputBlockedRouteDecisions.IsInputBlocked(null));
    }

    [Fact]
    public void InputBlockedReason_PrefersInnerInputMessage()
    {
        var direct = new WindowsInputBlockedException("模拟键鼠消息发送失败");
        Assert.Equal("模拟键鼠消息发送失败", InputBlockedRouteDecisions.InputBlockedReason(direct));

        var aggregate = new AggregateException(new InvalidOperationException("other"), direct);
        Assert.Equal("模拟键鼠消息发送失败", InputBlockedRouteDecisions.InputBlockedReason(aggregate));

        Assert.Equal("other",
            InputBlockedRouteDecisions.InputBlockedReason(new InvalidOperationException("other")));
    }
}
