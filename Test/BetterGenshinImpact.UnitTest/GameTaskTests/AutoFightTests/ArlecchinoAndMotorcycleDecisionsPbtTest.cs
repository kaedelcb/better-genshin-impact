using BetterGenshinImpact.GameTask.AutoFight;
using FsCheck;
using FsCheck.Xunit;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

/// <summary>
/// B6/B12 决策纯函数的 PBT/边界测试：
///   - ArlecchinoAutoEqDecisions.ShouldReleaseQByCd（严格大于语义）
///   - ArlecchinoBurstGateDecisions.ShouldGate（开关 × 角色名真值表）
///   - AvatarCombatSpecialization.IsMotorcycleColor（色差阈值边界，玛薇卡摩托状态）
/// </summary>
public class ArlecchinoAndMotorcycleDecisionsPbtTest
{
    private static Gen<int> G(int lo, int hi) => Gen.Choose(lo, hi);

    // ---------- ShouldReleaseQByCd（B6） ----------

    /// <summary>P1：任意 (cd, threshold) 下语义恒等价于 cd &gt; threshold（严格大于）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldReleaseQByCd_StrictlyGreater(double cd, int threshold)
    {
        return (ArlecchinoAutoEqDecisions.ShouldReleaseQByCd(cd, threshold) == (cd > threshold)).ToProperty();
    }

    /// <summary>P2 边界：cd == threshold → false（不放 Q）；cd == threshold + 0.5 → true。</summary>
    [Property(MaxTest = 500)]
    public Property ShouldReleaseQByCd_BoundaryEqualsThresholdIsFalse(int threshold)
    {
        return (!ArlecchinoAutoEqDecisions.ShouldReleaseQByCd(threshold, threshold)
                && ArlecchinoAutoEqDecisions.ShouldReleaseQByCd(threshold + 0.5, threshold)).ToProperty();
    }

    // ---------- ShouldGate（B6） ----------

    /// <summary>P3 真值表：当且仅当 开关开启 且 角色名为阿蕾奇诺 时门控生效。</summary>
    [Property(MaxTest = 500)]
    public Property ShouldGate_TruthTable(bool gateEnabled, bool isArlecchino)
    {
        var name = isArlecchino ? "阿蕾奇诺" : "钟离";
        var expected = gateEnabled && isArlecchino;
        return (ArlecchinoBurstGateDecisions.ShouldGate(gateEnabled, name) == expected).ToProperty();
    }

    /// <summary>P4：null 角色名恒不门控（守卫 null 路径）。</summary>
    [Fact]
    public void ShouldGate_NullName_NeverGates()
    {
        Assert.False(ArlecchinoBurstGateDecisions.ShouldGate(true, null));
        Assert.False(ArlecchinoBurstGateDecisions.ShouldGate(true, ""));
        Assert.True(ArlecchinoBurstGateDecisions.ShouldGate(true, "阿蕾奇诺"));
        Assert.False(ArlecchinoBurstGateDecisions.ShouldGate(false, "阿蕾奇诺"));
    }

    // ---------- IsMotorcycleColor（B12 玛薇卡） ----------

    /// <summary>P5：相同颜色 → 恒为摩托状态（色差 0 &lt; 阈值 15）。</summary>
    [Property(MaxTest = 500)]
    public Property IsMotorcycleColor_IdenticalColorsAlwaysTrue(byte b, byte g, byte r)
    {
        var p = new Vec3b(b, g, r);
        return AvatarCombatSpecialization.IsMotorcycleColor(p, p).ToProperty();
    }

    /// <summary>P6 色差边界：单通道差 10 → 摩托（10 &lt; 15）；差 20 → 非摩托（20 ≥ 15）。</summary>
    [Fact]
    public void IsMotorcycleColor_ThresholdBoundary()
    {
        var black = new Vec3b(0, 0, 0);
        Assert.True(AvatarCombatSpecialization.IsMotorcycleColor(black, new Vec3b(10, 0, 0)));
        Assert.False(AvatarCombatSpecialization.IsMotorcycleColor(black, new Vec3b(20, 0, 0)));
    }

    /// <summary>P7：多通道小差累计超阈——(8,8,8) 欧氏色差 ≈ 13.86 &lt; 15 → 摩托；(10,10,10) ≈ 17.3 ≥ 15 → 非摩托。</summary>
    [Fact]
    public void IsMotorcycleColor_EuclideanAccumulation()
    {
        var black = new Vec3b(0, 0, 0);
        Assert.True(AvatarCombatSpecialization.IsMotorcycleColor(black, new Vec3b(8, 8, 8)));
        Assert.False(AvatarCombatSpecialization.IsMotorcycleColor(black, new Vec3b(10, 10, 10)));
    }
}
