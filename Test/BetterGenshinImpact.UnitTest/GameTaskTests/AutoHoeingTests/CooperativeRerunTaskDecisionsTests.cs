using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.Shared.CooperativeRerun;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class CooperativeRerunTaskDecisionsTests
{
    [Theory]
    [InlineData(false, "retry", false)]
    [InlineData(true, "", false)]
    [InlineData(true, " , ， ", false)]
    [InlineData(true, "retry", true)]
    public void IsEnabled_UsesMultiplayerAndNonEmptyKeywords(bool multiplayer, string keywords, bool expected)
        => Assert.Equal(expected, CooperativeRerunTaskDecisions.IsEnabled(multiplayer, keywords));

    /// <summary>
    /// 兼容门控：配置了关键词但房间未宣告 hoeing.rerun.v1 时必须**不启用**新协同重跑
    /// （退回旧轮末重跑路径），而不是半启用或让整轮锄地失败。
    /// </summary>
    [Theory]
    [InlineData(true, "retry", true, true)]
    [InlineData(true, "retry", false, false)]
    [InlineData(false, "retry", true, false)]
    [InlineData(true, "", true, false)]
    public void IsEnabled_RequiresCapabilityWhenKeywordsConfigured(
        bool multiplayer, string keywords, bool serverSupportsCapability, bool expected)
        => Assert.Equal(expected, CooperativeRerunTaskDecisions.IsEnabled(
            multiplayer, keywords, serverSupportsCapability));

    [Theory]
    [InlineData(RerunStage.Completed, 1, true)]
    [InlineData(RerunStage.Completed, 0, false)]
    [InlineData(RerunStage.Finishing, 1, false)]
    [InlineData(RerunStage.Aborted, 3, false)]
    public void ShouldStatue_RequiresConfirmedCompletionAndReplay(RerunStage stage, int count, bool expected)
        => Assert.Equal(expected, CooperativeRerunTaskDecisions.ShouldStatue(stage, count));
}
