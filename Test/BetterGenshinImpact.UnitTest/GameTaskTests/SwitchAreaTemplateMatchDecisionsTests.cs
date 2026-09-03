using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

/// <summary>
/// SwitchAreaTemplateMatchDecisions 纯函数单测（switch-area-template-match spec / Task 1）。
/// 守护模板匹配"命中/未命中/阈值边界/坐标中心"判定逻辑。
/// </summary>
public class SwitchAreaTemplateMatchDecisionsTests
{
    [Fact]
    public void IsHit_BelowThreshold_False()
    {
        Assert.False(SwitchAreaTemplateMatchDecisions.IsHit(0.79));
    }

    [Fact]
    public void IsHit_AtThreshold_True()
    {
        Assert.True(SwitchAreaTemplateMatchDecisions.IsHit(0.8));
    }

    [Fact]
    public void GetClickPoint_Center()
    {
        var (x, y) = SwitchAreaTemplateMatchDecisions.GetClickPoint(10, 20, 40, 30);
        Assert.Equal(30, x);
        Assert.Equal(35, y);
    }

    [Fact]
    public void ShouldUse_MissingTemplate_False()
    {
        Assert.False(SwitchAreaTemplateMatchDecisions.ShouldUseTemplateMatch(false, 0.95));
    }

    [Fact]
    public void ShouldUse_BelowThreshold_False()
    {
        Assert.False(SwitchAreaTemplateMatchDecisions.ShouldUseTemplateMatch(true, 0.5));
    }

    [Fact]
    public void ShouldUse_Hit_True()
    {
        Assert.True(SwitchAreaTemplateMatchDecisions.ShouldUseTemplateMatch(true, 0.9));
    }
}
