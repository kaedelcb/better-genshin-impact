using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class SpecialAdjacentTpPointDecisionsTests
{
    private static readonly List<SpecialAdjacentTpPoint> List = new()
    {
        new() { X = 2594.43, Y = 9384.52 },
        new() { X = 3375, Y = 2701 },
    };

    [Fact]
    public void Force_TargetNotNearTpJsonPoint_HitsByRouteCoordinate()
    {
        // 最近真实传送点在几千单位外，路线坐标是罐子点
        var (hit, zoom) = SpecialAdjacentTpPointDecisions.IsSpecialAdjacentPointForTp(
            List, 5397, 10425, 2593.16, 9385.24, true, 50.0, 1.5);
        Assert.True(hit);
        Assert.Equal(1.5, zoom);
    }

    [Fact]
    public void NotForce_SameInput_DoesNotHit()
    {
        var (hit, _) = SpecialAdjacentTpPointDecisions.IsSpecialAdjacentPointForTp(
            List, 5397, 10425, 2593.16, 9385.24, false, 50.0, 1.5);
        Assert.False(hit);
    }

    [Fact]
    public void Force_TargetNearTpJsonPoint_NoSecondCheck()
    {
        // 路线坐标在最近真实点 50 以内：不做第二次判定，结果与旧判定一致（未命中）
        var (hit, _) = SpecialAdjacentTpPointDecisions.IsSpecialAdjacentPointForTp(
            List, 2660, 9384, 2640, 9384, true, 50.0, 1.5);
        Assert.False(hit);
    }

    [Fact]
    public void BaseHit_BehavesAsBefore()
    {
        var (hit, _) = SpecialAdjacentTpPointDecisions.IsSpecialAdjacentPointForTp(
            List, 3375, 2701, 9999, 9999, true, 50.0, 1.5);
        Assert.True(hit);
    }
}
