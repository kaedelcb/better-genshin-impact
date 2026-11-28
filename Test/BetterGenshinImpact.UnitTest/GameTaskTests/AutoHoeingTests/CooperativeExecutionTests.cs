using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task FightCancellationLeavesRouteAvailableForRevival(bool legacyCancellation)
    {
        using var route = new CancellationTokenSource();
        var stopFight = false;
        using var fight = new CooperativeFightScope("s0:f0", () => stopFight, route.Token);

        var interrupted = await AutoFightHandler.RunFightAsync(fight, route.Token, async token =>
        {
            stopFight = true;
            fight.CheckStop();
            try
            {
                await Task.Delay(Timeout.Infinite, token).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (legacyCancellation)
            {
                throw new NormalEndException("取消自动任务");
            }
        });

        Assert.True(interrupted);
        Assert.False(route.IsCancellationRequested);
    }

    [Fact]
    public async Task RouteStopStillPropagatesWhenFightWasInterrupted()
    {
        using var route = new CancellationTokenSource();
        var stopFight = false;
        using var fight = new CooperativeFightScope("s0:f0", () => stopFight, route.Token);

        await Assert.ThrowsAsync<NormalEndException>(() => AutoFightHandler.RunFightAsync(fight, route.Token, _ =>
        {
            stopFight = true;
            fight.CheckStop();
            route.Cancel();
            return Task.FromException(new NormalEndException("取消自动任务"));
        }));
    }

    [Fact]
    public async Task RevivalHandoffWaitsForAnInFlightObservation()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var release = new ManualResetEventSlim();
        var detector = new AnomalyDetector();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var markRegistered = false;
        var observation = Task.Run(() => detector.SettleRevivalAsync(false, () =>
        {
            observing.SetResult();
            release.Wait(bound.Token);
            markRegistered = true;
        }, bound.Token), bound.Token);

        try
        {
            await observing.Task.WaitAsync(bound.Token);
            var handoff = detector.SettleRevivalAsync(false, () => Assert.True(markRegistered), bound.Token);
            Assert.False(handoff.IsCompleted);
            release.Set();
            await handoff;
        }
        finally
        {
            release.Set();
            await observation;
        }
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
