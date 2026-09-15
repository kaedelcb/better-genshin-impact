using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public class CooperativeExecutionTests
{
    [Fact]
    public async Task FightStopDoesNotCancelRouteOrNextFight()
    {
        using var route = new CancellationTokenSource();
        var skip = false;
        using (var fight = new CooperativeFightScope("s0:f0", () => skip, route.Token))
        {
            skip = true;
            fight.CheckStop();
            Assert.True(fight.IsInterrupted);
            Assert.True(fight.Token.IsCancellationRequested);
            Assert.False(route.IsCancellationRequested);
            await fight.StopMonitoringAsync();
        }
        using var next = new CooperativeFightScope("s0:f1", () => false, route.Token);
        Assert.False(next.IsInterrupted);
        Assert.False(next.Token.IsCancellationRequested);
        await next.StopMonitoringAsync();
    }

    [Theory]
    [InlineData(true, false, false, false, RerunRouteOutcome.Completed)]
    [InlineData(true, true, false, false, RerunRouteOutcome.Incomplete)]
    [InlineData(false, false, false, false, RerunRouteOutcome.Incomplete)]
    [InlineData(true, false, true, false, RerunRouteOutcome.Failed)]
    [InlineData(true, false, false, true, RerunRouteOutcome.Cancelled)]
    // 取消/失败优先级高于"完整"。
    [InlineData(true, false, true, true, RerunRouteOutcome.Cancelled)]
    [InlineData(false, false, true, false, RerunRouteOutcome.Failed)]
    public void OutcomeNeverHidesIncompleteOrCancellation(bool completed, bool incomplete,
        bool failed, bool cancelled, RerunRouteOutcome expected)
        => Assert.Equal(expected, CooperativeExecutionDecisions.Outcome(completed, incomplete, failed, cancelled));

    /// <summary>
    /// 该纯函数是 RouteExecutionEngine 计算协作终态的**唯一生产入口**（引擎已改为调用它），
    /// 因此这些用例覆盖真实结果分类，而不是无人调用的影子实现。
    /// 被请求跳整线时，即使跑完末节点也不得算"完整"。
    /// </summary>
    [Theory]
    [InlineData(true, false, false, false, true, RerunRouteOutcome.Incomplete)]
    [InlineData(true, false, false, false, false, RerunRouteOutcome.Completed)]
    public void SkipRouteRequestedDowngradesCompleted(bool completed, bool incomplete, bool failed,
        bool cancelled, bool skipRouteRequested, RerunRouteOutcome expected)
        => Assert.Equal(expected, CooperativeExecutionDecisions.Outcome(
            completed, incomplete, failed, cancelled, skipRouteRequested));

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, true, false)]
    public void ReplayDoesNotSupplyNegativeExperienceEvidence(bool replay, bool interrupted, bool hasExp, bool expected)
        => Assert.Equal(expected, CooperativeExecutionDecisions.ShouldReportExperience(replay, interrupted, hasExp));
}
