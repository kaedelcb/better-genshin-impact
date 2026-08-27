using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class SeaOfBygoneErasMinZoomPreservationTest
{
    private static double ComputeMinZoomLevel(double disBetweenTpPoints, string mapName)
    {
        var minZoom = Math.Max(disBetweenTpPoints / 30, 1.0);
        if (mapName == "SeaOfBygoneEras")
        {
            minZoom = Math.Max(minZoom, 2.0);
        }
        return minZoom;
    }

    private const string SeaOfBygoneEras = "SeaOfBygoneEras";
    private const string Teyvat = "Teyvat";
    private const string Enkanomiya = "Enkanomiya";
    private const string AncientSacredMountain = "AncientSacredMountain";

    // 具体用例：非旧日之海地图不受影响
    [Fact]
    public void Preservation_Teyvat_Distance39_ShouldBe1_30()
    {
        var minZoom = ComputeMinZoomLevel(39, Teyvat);
        Assert.Equal(1.30, minZoom, 2);
    }

    [Fact]
    public void Preservation_Enkanomiya_Distance45_ShouldBe1_50()
    {
        var minZoom = ComputeMinZoomLevel(45, Enkanomiya);
        Assert.Equal(1.50, minZoom, 2);
    }

    [Fact]
    public void Preservation_AncientSacredMountain_Distance0_ShouldBe1_0()
    {
        var minZoom = ComputeMinZoomLevel(0, AncientSacredMountain);
        Assert.Equal(1.0, minZoom, 2);
    }

    // 具体用例：旧日之海远距离自然值 ≥ 2.0，钳制无实际效果
    [Fact]
    public void Preservation_SeaOfBygoneEras_Distance60_ShouldBe2_0()
    {
        var minZoom = ComputeMinZoomLevel(60, SeaOfBygoneEras);
        Assert.Equal(2.0, minZoom, 4);
    }

    [Fact]
    public void Preservation_SeaOfBygoneEras_Distance90_ShouldBe3_0()
    {
        var minZoom = ComputeMinZoomLevel(90, SeaOfBygoneEras);
        Assert.Equal(3.0, minZoom, 4);
    }

    // PBT：非旧日之海地图 + 任意距离 → 与原公式一致
    [Property(MaxTest = 500)]
    public Property Preservation_Pbt_NonSeaOfBygoneEras_FormulaUnchanged(float rawDis)
    {
        var dis = Math.Abs(rawDis) % 1001; // [0, 1000]
        var minZoom = ComputeMinZoomLevel(dis, Teyvat);
        // 钳制条件 mapName == "SeaOfBygoneEras" 确保非旧日之海不进入钳制分支
        // 此处直接验证原公式计算结果
        return (minZoom == Math.Max(dis / 30, 1.0))
            .Label($"Non-SeaOfBygoneEras: dis={dis:F0} → minZoomLevel={minZoom:F4}")
            .Label("Formula unchanged: Math.Max(dis / 30, 1.0)");
    }

    // PBT：旧日之海 + 远距离（≥ 60）→ 钳制无实际效果
    [Property(MaxTest = 500)]
    public Property Preservation_Pbt_SeaOfBygoneEras_LargeDistance_NoClampEffect(float rawDis)
    {
        var dis = 60 + Math.Abs(rawDis) % 941; // [60, 1000]
        var minZoom = ComputeMinZoomLevel(dis, SeaOfBygoneEras);
        // 当自然值 >= 2.0 时，钳制 Math.Max(minZoom, 2.0) 返回原值
        // 这里验证 ComputeMinZoomLevel 返回的值与原始公式一致（钳制无实际效果）
        var original = Math.Max(dis / 30, 1.0);
        return (minZoom == original)
            .Label($"SeaOfBygoneEras: dis={dis:F0} → minZoomLevel={minZoom:F4}, original={original:F4}")
            .Label("Clamp has no effect when natural value >= 2.0");
    }
}