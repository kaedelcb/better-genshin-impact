using BetterGenshinImpact.Core.Config;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.Core.Config;

public class MaskWindowConfigTests
{
    // ========== Property 1: ComputeEffectiveSize 公式正确性 ==========
    // 收敛到公版 8f554e21 架构：以 ComputeEffectiveSize(baseSize, scale, scaleTo1080pRatio, displayDpiScale, scalingEnabled) 替代旧的 ComputeEffectiveFontSize。
    // 启用缩放时：= Max(Round(baseSize × Clamp(scale) × Max(scaleTo1080pRatio,0.1) / Max(displayDpiScale,0.1), AwayFromZero), 1.0)。

    [Property(MaxTest = 100)]
    public Property ComputeEffectiveSize_Formula_Correctness(double baseSize, double scale, double scaleTo1080pRatio, double displayDpiScale)
    {
        // 过滤无效值（公式的有限性由被测函数内部保护，这里只验证正常域）
        if (baseSize <= 0 || double.IsNaN(baseSize) || double.IsInfinity(baseSize)
            || scaleTo1080pRatio <= 0 || double.IsNaN(scaleTo1080pRatio) || double.IsInfinity(scaleTo1080pRatio)
            || displayDpiScale <= 0 || double.IsNaN(displayDpiScale) || double.IsInfinity(displayDpiScale))
        {
            return true.ToProperty();
        }

        var resolutionScale = Math.Max(scaleTo1080pRatio, 0.1);
        var dpi = Math.Max(displayDpiScale, 0.1);
        var expected = Math.Max(
            Math.Round(baseSize * MaskWindowConfig.ComputeClampedScale(scale) * resolutionScale / dpi, MidpointRounding.AwayFromZero),
            1.0);
        var actual = MaskWindowConfig.ComputeEffectiveSize(baseSize, scale, scaleTo1080pRatio, displayDpiScale, true);
        return (actual == expected).ToProperty();
    }

    // ========== Property 2: 禁用缩放时直接返回基础尺寸 ==========
    // 公版新增 OverlayScalingEnabled 开关：scalingEnabled=false 时忽略缩放因子，直接返回有效基础尺寸。

    [Property(MaxTest = 100)]
    public Property ComputeEffectiveSize_Disabled_ReturnsBaseSize(double baseSize, double scale, double scaleTo1080pRatio, double displayDpiScale)
    {
        if (baseSize <= 0 || double.IsNaN(baseSize) || double.IsInfinity(baseSize))
        {
            return true.ToProperty();
        }

        var actual = MaskWindowConfig.ComputeEffectiveSize(baseSize, scale, scaleTo1080pRatio, displayDpiScale, false);
        return (actual == baseSize).ToProperty();
    }

    // ========== Property 3: ComputeClampedScale 范围约束 ==========

    [Property(MaxTest = 100)]
    public Property ComputeClampedScale_Range_Constraints(double value)
    {
        var result = MaskWindowConfig.ComputeClampedScale(value);
        return (result >= MaskWindowConfig.MinLogFontScale && result <= MaskWindowConfig.MaxLogFontScale).ToProperty();
    }

    // ========== Property 4: NaN 回退 ==========

    [Fact]
    public void ComputeClampedScale_NaN_ReturnsOne()
    {
        var result = MaskWindowConfig.ComputeClampedScale(double.NaN);
        Assert.Equal(1.0, result);
    }

    // ========== Property 5: 启用缩放时结果有下限 1.0 ==========
    // 公版启用缩放路径末尾有 Math.Max(result, 1.0)，保证渲染字号不会缩到 <1 像素。
    // 注意：禁用缩放路径直接返回 safeBaseSize（可能 <1），故本性质仅约束 scalingEnabled=true。

    [Property(MaxTest = 100)]
    public Property ComputeEffectiveSize_Enabled_Result_AtLeastOne(double baseSize, double scale, double scaleTo1080pRatio, double displayDpiScale)
    {
        var actual = MaskWindowConfig.ComputeEffectiveSize(baseSize, scale, scaleTo1080pRatio, displayDpiScale, true);
        return (actual >= 1.0).ToProperty();
    }
}
