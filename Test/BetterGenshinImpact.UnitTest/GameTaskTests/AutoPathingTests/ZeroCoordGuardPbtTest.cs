#nullable enable

using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.Core.Config;
using OpenCvSharp;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// A12 收口测试：ZeroCoordGuard.ResolveOrientationPosition 零坐标防呆全域真值表 +
/// MiniMapMatchTuningValidator 节流校验 + 配置默认值守护。
/// **Validates: 零坐标防呆 Requirement 4.2/4.3/4.4（历史 spec 编号，来源已归档）**
/// 输入域：guardEnabled × 当前帧是否(0,0) × 上一帧是否(0,0) 全空间 + 任意坐标值。
/// </summary>
public class ZeroCoordGuardPbtTest
{
    private static Gen<int> G(int lo, int hi) => Gen.Choose(lo, hi);

    /// <summary>恒非零坐标构造：|x|+1 保证非零（避开 int 加法溢出/负号陷阱）。</summary>
    private static Point2f NonZero(int x, int y) => new Point2f(Math.Abs(x) + 1f, Math.Abs(y) + 1f);

    /// <summary>P1（主规格，三标志位全域真值表）：结果与规格直译 oracle 全等价。
    /// 非零坐标值不影响分支（逻辑只看是否 (0,0)），用固定代表值 (2,2)/(10,10) 区分 current/prev。</summary>
    [Property(MaxTest = 500)]
    public Property Resolve_MatchesSpecOracle(int guardOn, int curZeroFlag, int prevZeroFlag)
    {
        var curZero = curZeroFlag == 1;
        var prevZero = prevZeroFlag == 1;
        var current = curZero ? new Point2f(0f, 0f) : new Point2f(2f, 2f);
        var prev = prevZero ? new Point2f(0f, 0f) : new Point2f(10f, 10f);
        var guard = guardOn == 1;

        var (pos, skip) = ZeroCoordGuard.ResolveOrientationPosition(current, prev, guard);

        var expectedSkip = guard && curZero && prevZero;
        var expectedPos = !guard ? current
            : !curZero ? current
            : !prevZero ? prev
            : current;

        return ((skip == expectedSkip) && pos.X == expectedPos.X && pos.Y == expectedPos.Y).ToProperty();
    }

    /// <summary>P2（Q1 守护）：开关关闭 → 恒 (current, false)，即使当前帧是 (0,0)（保持现状行为）。</summary>
    [Fact]
    public void Resolve_GuardOff_PassthroughEvenZero()
    {
        var current = new Point2f(0f, 0f);
        var prev = new Point2f(3f, 4f);
        var (pos, skip) = ZeroCoordGuard.ResolveOrientationPosition(current, prev, guardEnabled: false);
        Assert.False(skip);
        Assert.Equal(current.X, pos.X);
        Assert.Equal(current.Y, pos.Y);
    }

    /// <summary>P3（4.2）：开启 + 当前 (0,0) + 上一帧有效 → 用上一帧，不跳过。</summary>
    [Fact]
    public void Resolve_GuardOn_CurrentZero_UsesPrev()
    {
        var current = new Point2f(0f, 0f);
        var prev = new Point2f(3f, 4f);
        var (pos, skip) = ZeroCoordGuard.ResolveOrientationPosition(current, prev, guardEnabled: true);
        Assert.False(skip);
        Assert.Equal(prev.X, pos.X);
        Assert.Equal(prev.Y, pos.Y);
    }

    /// <summary>P4（4.4）：开启 + 当前 (0,0) + 上一帧也无效 → skip=true（调用方跳过本帧朝向更新）。</summary>
    [Fact]
    public void Resolve_GuardOn_BothZero_Skips()
    {
        var (pos, skip) = ZeroCoordGuard.ResolveOrientationPosition(new Point2f(0f, 0f), new Point2f(0f, 0f), guardEnabled: true);
        Assert.True(skip);
    }

    /// <summary>P5：开启 + 当前帧非 (0,0) → 恒用当前帧（与上一帧无关，含上一帧 (0,0)）。</summary>
    [Property(MaxTest = 500)]
    public Property Resolve_GuardOn_CurrentNonZero_AlwaysCurrent(int prevX, int prevY)
    {
        var current = new Point2f(3.5f, 7.2f);
        var prev = prevX == 0 && prevY == 0 ? new Point2f(0f, 0f) : NonZero(prevX, prevY);
        var (pos, skip) = ZeroCoordGuard.ResolveOrientationPosition(current, prev, guardEnabled: true);
        return (!skip && pos.X == current.X && pos.Y == current.Y).ToProperty();
    }

    // ---------- MiniMapMatchTuningValidator / 默认值（A12 配置回退链） ----------

    /// <summary>P6：ValidateDumpThrottleMs 负值 → 回落默认 2000 并标记 fellBack。</summary>
    [Property(MaxTest = 300)]
    public Property ValidateDumpThrottleMs_Negative_FallsBackToDefault(int candidate)
    {
        if (candidate >= 0) return true.ToProperty();
        var (value, fellBack) = MiniMapMatchTuningValidator.ValidateDumpThrottleMs(candidate);
        return (value == MiniMapMatchTuningConfig.DefaultDumpThrottleMs && fellBack).ToProperty();
    }

    /// <summary>P7：ValidateDumpThrottleMs 非负 → 原值直通、不标记。</summary>
    [Property(MaxTest = 300)]
    public Property ValidateDumpThrottleMs_NonNegative_Passthrough(int candidate)
    {
        if (candidate < 0) return true.ToProperty();
        var (value, fellBack) = MiniMapMatchTuningValidator.ValidateDumpThrottleMs(candidate);
        return (value == candidate && !fellBack).ToProperty();
    }

    /// <summary>P8：默认值守护（诊断设施三开关的回落值——实际默认全关，保守语义）。</summary>
    [Fact]
    public void TuningConfig_Defaults_Pinned()
    {
        Assert.False(MiniMapMatchTuningConfig.DefaultDiagnosticsEnabled);
        Assert.False(MiniMapMatchTuningConfig.DefaultDumpFailedFrame);
        Assert.Equal(2000, MiniMapMatchTuningConfig.DefaultDumpThrottleMs);
    }
}
