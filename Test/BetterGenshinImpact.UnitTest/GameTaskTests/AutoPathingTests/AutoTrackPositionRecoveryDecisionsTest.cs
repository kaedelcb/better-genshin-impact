#nullable enable

using BetterGenshinImpact.GameTask.AutoTrackPath;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// AutoTrackPositionRecoveryDecisions 的分级补救决策 PBT 属性测试。
/// **Validates: bugfix.md BC-1 / BC-3, design.md 组件1 / 组件3**
///
/// 覆盖列：
///   - PBT-1 Decide(times=1) 恒为 BlindWalk（第 1 级盲走，现状保留）
///   - PBT-2 Decide(times=2) 按缩放分叉：缩放&lt;稳定档→ZoomInThenRecog；≥稳定档→SwitchAreaThenRecog
///   - PBT-3 Decide(times=3) 恒为 SwitchAreaThenRecog（第 3 级切地区）
///   - PBT-4 Decide(times≥4) 恒为 ThrowRetry（兜底重传）
///   - Preservation：IsRecoveryApplicable 独立地图=false / 提瓦特=true；Decide(times=1) 盲走
/// </summary>
public class AutoTrackPositionRecoveryDecisionsTest
{
    /// <summary>PBT-1：第 1 级总走盲走（与缩放无关）。</summary>
    [Property(MaxTest = 1000)]
    public Property Decide_Times1_AlwaysBlindWalk(double zoom)
    {
        var action = AutoTrackPositionRecoveryDecisions.Decide(1, zoom);
        return (action == CenterRecoveryAction.BlindWalk).ToProperty();
    }

    /// <summary>PBT-2：第 2 级按缩放分叉——缩放 &lt; 稳定档拉大再识别，≥ 稳定档直接切地区。</summary>
    [Property(MaxTest = 1000)]
    public Property Decide_Times2_ZoomBelowStablePullsIn_OtherwiseSwitchArea(bool below)
    {
        double zoom = below
            ? AutoTrackPositionRecoveryDecisions.RecoverStableZoom - 0.1
            : AutoTrackPositionRecoveryDecisions.RecoverStableZoom + 0.1;
        var expected = below
            ? CenterRecoveryAction.ZoomInThenRecog
            : CenterRecoveryAction.SwitchAreaThenRecog;
        return (AutoTrackPositionRecoveryDecisions.Decide(2, zoom) == expected).ToProperty();
    }

    /// <summary>PBT-3：第 3 级总走切地区（与缩放无关）。</summary>
    [Property(MaxTest = 1000)]
    public Property Decide_Times3_AlwaysSwitchArea(double zoom)
    {
        var action = AutoTrackPositionRecoveryDecisions.Decide(3, zoom);
        return (action == CenterRecoveryAction.SwitchAreaThenRecog).ToProperty();
    }

    /// <summary>PBT-4：兜底（times≥4）总抛重传（与缩放无关）。</summary>
    [Property(MaxTest = 1000)]
    public Property Decide_TimesGte4_AlwaysThrowRetry(int times, double zoom)
    {
        var t = times + 4; // 强制 ≥4
        var action = AutoTrackPositionRecoveryDecisions.Decide(t, zoom);
        return (action == CenterRecoveryAction.ThrowRetry).ToProperty();
    }

    /// <summary>Preservation：独立地图不启用分级补救（BC-3 / CC5）。</summary>
    [Property(MaxTest = 100)]
    public Property IsRecoveryApplicable_IndependentMap_False(bool anything)
    {
        return (AutoTrackPositionRecoveryDecisions.IsRecoveryApplicable(false) == false).ToProperty();
    }

    /// <summary>Preservation：提瓦特启用分级补救。</summary>
    [Property(MaxTest = 100)]
    public Property IsRecoveryApplicable_Teyvat_True(bool anything)
    {
        return (AutoTrackPositionRecoveryDecisions.IsRecoveryApplicable(true) == true).ToProperty();
    }

    /// <summary>Preservation：第 1 级盲走（CC2，与既有行为一致）。</summary>
    [Property(MaxTest = 100)]
    public Property Decide_Times1_BlindWalk_IsCurrentBehavior(double zoom)
    {
        return (AutoTrackPositionRecoveryDecisions.Decide(1, zoom) == CenterRecoveryAction.BlindWalk).ToProperty();
    }
}
