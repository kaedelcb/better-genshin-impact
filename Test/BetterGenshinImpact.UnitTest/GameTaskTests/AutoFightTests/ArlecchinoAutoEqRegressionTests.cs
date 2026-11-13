using BetterGenshinImpact.GameTask.AutoFight;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

/// <summary>
/// 回归本次用户反馈：短 attack 段之间必须延续红血确认，收契退避不得阻止保命 Q；
/// 连续契条应识别为有契，散点不能冒充契条。旧日志不含像素，故用内存图像和时间序列验证。
/// 这些用例通过才接受本次修复；不把它们当作游戏内按键或现场识图效果的证明。
/// </summary>
public class ArlecchinoAutoEqRegressionTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 9, 51, 55, DateTimeKind.Utc);

    [Fact]
    public void RedBloodAcrossShortAttacks_PreemptsCollectionEvenWithBondAndNoSkillCd()
    {
        var state = new ArlecchinoAutoEqState();
        state.Observe(redBlood: true, hasBond: true, Start);
        Assert.False(state.ShouldTryBurst(Start, 0, 5, allowEmptyBond: true));

        // 下一段 attack(0.01) 仍使用同一角色状态，而非重新从零累计。
        var nextAttack = Start.AddMilliseconds(120);
        state.Observe(redBlood: true, hasBond: true, nextAttack);
        Assert.True(state.ShouldTryBurst(nextAttack, 0, 5, allowEmptyBond: true));
        Assert.False(state.CanCollect(nextAttack));
    }

    [Fact]
    public void FailedCollectionBackoff_DoesNotBlockRedBloodBurstDuringRecovery()
    {
        var state = new ArlecchinoAutoEqState();
        state.CollectionFinished(Start);
        state.Observe(redBlood: true, hasBond: false, Start);
        var nextFrame = Start.AddMilliseconds(150);
        state.Observe(redBlood: true, hasBond: false, nextFrame);
        Assert.False(state.CanCollect(nextFrame));
        Assert.True(state.ShouldTryBurst(nextFrame, 0, 220, allowEmptyBond: false));
    }

    [Fact]
    public void SingleFrameMissingBond_DoesNotStartCollection()
    {
        var state = new ArlecchinoAutoEqState();
        state.Observe(redBlood: false, hasBond: false, Start);
        Assert.False(state.CanCollect(Start));
        state.Observe(redBlood: false, hasBond: true, Start.AddMilliseconds(150));
        state.Observe(redBlood: false, hasBond: false, Start.AddMilliseconds(300));
        Assert.False(state.CanCollect(Start.AddMilliseconds(300)));
    }

    [Fact]
    public void EmptyBondConfirmed_RespectsCdThresholdAndDoesNotBurstDuringCollection()
    {
        var state = new ArlecchinoAutoEqState();
        state.Observe(redBlood: false, hasBond: false, Start);
        var nextFrame = Start.AddMilliseconds(120);
        state.Observe(redBlood: false, hasBond: false, nextFrame);
        Assert.True(state.CanCollect(nextFrame));
        Assert.False(state.ShouldTryBurst(nextFrame, 5, 5, allowEmptyBond: true));
        Assert.True(state.ShouldTryBurst(nextFrame, 5.1, 5, allowEmptyBond: true));
        Assert.False(state.ShouldTryBurst(nextFrame, 30, 5, allowEmptyBond: false));
    }

    [Fact]
    public void CollectionFinished_PreventsImmediateRepeatedHeavyAttacks()
    {
        var state = new ArlecchinoAutoEqState();
        state.CollectionFinished(Start);
        state.Observe(redBlood: false, hasBond: false, Start);
        state.Observe(redBlood: false, hasBond: false, Start.AddMilliseconds(120));
        Assert.False(state.CanCollect(Start.AddMilliseconds(120)));
        state.Observe(redBlood: false, hasBond: false, Start.AddMilliseconds(1900));
        Assert.False(state.CanCollect(Start.AddMilliseconds(1900)));
        state.Observe(redBlood: false, hasBond: false, Start.AddSeconds(2));
        Assert.True(state.CanCollect(Start.AddSeconds(2)));
    }

    [Fact]
    public void RedBloodClearsOrObservationExpires_RequiresFreshConfirmation()
    {
        var state = new ArlecchinoAutoEqState();
        state.Observe(redBlood: true, hasBond: true, Start);
        state.Observe(redBlood: false, hasBond: true, Start.AddMilliseconds(120));
        state.Observe(redBlood: true, hasBond: true, Start.AddMilliseconds(240));
        Assert.False(state.ShouldTryBurst(Start.AddMilliseconds(240), 0, 5, true));
        state.Observe(redBlood: true, hasBond: true, Start.AddSeconds(5));
        Assert.False(state.ShouldTryBurst(Start.AddSeconds(5), 0, 5, true));
        state.Observe(redBlood: true, hasBond: true, Start.AddMilliseconds(5120));
        Assert.True(state.ShouldTryBurst(Start.AddMilliseconds(5120), 0, 5, true));
    }

    [Fact]
    public void UnconfirmedBurstAttempt_IsThrottledWithoutLosingRedBlood()
    {
        var state = new ArlecchinoAutoEqState();
        state.Observe(true, true, Start);
        var confirmed = Start.AddMilliseconds(120);
        state.Observe(true, true, confirmed);
        state.BurstAttempted(confirmed);
        Assert.False(state.ShouldTryBurst(confirmed.AddMilliseconds(100), 0, 5, true));
        Assert.True(state.ShouldTryBurst(confirmed.AddMilliseconds(500), 0, 5, true));
    }

    [Theory]
    [InlineData(180, 6)]
    [InlineData(20, 1)]
    public void ContinuousBondBand_IsRecognized(int width, int height)
    {
        using var image = EmptyScreen();
        Cv2.Rectangle(image, new Rect(850, 1005, width, height), new Scalar(140, 144, 255), -1);
        Assert.True(ArlecchinoBondDetector.HasBond(image, 0));
    }

    [Fact]
    public void TwoIsolatedMatchingPixels_AreNotBond()
    {
        using var image = EmptyScreen();
        image.Set(1005, 850, new Vec3b(140, 144, 255));
        image.Set(1010, 950, new Vec3b(140, 144, 255));
        Assert.False(ArlecchinoBondDetector.HasBond(image, 0));
    }

    [Fact]
    public void VerticalNoiseOrNormalRedBlood_AreNotBond()
    {
        using var image = EmptyScreen();
        Cv2.Rectangle(image, new Rect(850, 1000, 1, 20), new Scalar(140, 144, 255), -1);
        Cv2.Rectangle(image, new Rect(870, 1000, 100, 10), new Scalar(90, 90, 255), -1);
        Assert.False(ArlecchinoBondDetector.HasBond(image, 0));
    }

    [Fact]
    public void QiKong_PreservesSamplingDirection()
    {
        using var image = EmptyScreen();
        Cv2.Rectangle(image, new Rect(1000, 1005, 40, 6), new Scalar(140, 144, 255), -1);
        Assert.True(ArlecchinoBondDetector.HasBond(image, 0));
        Assert.False(ArlecchinoBondDetector.HasBond(image, 100));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(219)]
    [InlineData(220)]
    [InlineData(221)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void QiKongBoundary_HasValidCropAndFiltersSinglePixel(int qiKong)
    {
        using var image = EmptyScreen();
        image.Set(1005, 840, new Vec3b(140, 144, 255));
        Assert.False(ArlecchinoBondDetector.HasBond(image, qiKong));
        Cv2.Rectangle(image, new Rect(840, 1005, 20, 6), new Scalar(140, 144, 255), -1);
        Assert.True(ArlecchinoBondDetector.HasBond(image, qiKong));
    }

    [Fact]
    public void EmptyScreen_HasNoBond()
    {
        using var image = EmptyScreen();
        Assert.False(ArlecchinoBondDetector.HasBond(image, 0));
    }

    private static Mat EmptyScreen() => new(1080, 1920, MatType.CV_8UC3, Scalar.All(0));
}
