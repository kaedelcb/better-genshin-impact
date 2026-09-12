#nullable enable

using BetterGenshinImpact.GameTask.Common;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonTests;

/// <summary>
/// FocusRecoveryDecisions 决策纯函数的 PBT 属性测试（T1 批量收口 E2）。
/// **Validates: focus-recovery-no-budget-limit spec / bugfix.md §4 EB-1**
/// 固化决策契约（三输入全空间，无预算/无节流路径）：
///   Cfg=Off（RestoreFocusOnLost=false）或前台已是原神 → Skip；
///   最小化 → TryRestoreIconic；可见但非前台 → TryFocus。
/// </summary>
public class FocusRecoveryDecisionsPbtTest
{
    private static Gen<int> BitGen() => Gen.Choose(0, 1);

    /// <summary>P1（主规格，三 bool 全空间）：路由表全等价。</summary>
    [Property(MaxTest = 500)]
    public Property Decide_RoutesByState()
    {
        return Prop.ForAll(Arb.From(BitGen()), Arb.From(BitGen()), Arb.From(BitGen()),
            (restoreOnLost, fgGenshin, minimized) =>
            {
                var state = new FocusRecoveryState(restoreOnLost == 1, fgGenshin == 1, minimized == 1);
                var expected = !state.RestoreFocusOnLost || state.ForegroundIsGenshin
                    ? FocusRecoveryDecision.Skip
                    : state.GameWindowMinimized
                        ? FocusRecoveryDecision.TryRestoreIconic
                        : FocusRecoveryDecision.TryFocus;
                return (FocusRecoveryDecisions.Decide(state) == expected).ToProperty();
            });
    }

    /// <summary>P2（Preservation）：Cfg=Off 恒 Skip（无论前台/最小化）——严格模式抛异常由调用方处理。</summary>
    [Property(MaxTest = 200)]
    public Property Decide_ReturnsSkip_WhenRestoreDisabled()
    {
        return Prop.ForAll(Arb.From(BitGen()), Arb.From(BitGen()), (fgGenshin, minimized) =>
            (FocusRecoveryDecisions.Decide(new FocusRecoveryState(false, fgGenshin == 1, minimized == 1))
                == FocusRecoveryDecision.Skip).ToProperty());
    }

    /// <summary>P3（Preservation）：前台已是原神恒 Skip（无论 Cfg 与最小化）。</summary>
    [Property(MaxTest = 200)]
    public Property Decide_ReturnsSkip_WhenForegroundIsGenshin()
    {
        return Prop.ForAll(Arb.From(BitGen()), Arb.From(BitGen()), (restoreOnLost, minimized) =>
            (FocusRecoveryDecisions.Decide(new FocusRecoveryState(restoreOnLost == 1, true, minimized == 1))
                == FocusRecoveryDecision.Skip).ToProperty());
    }

    /// <summary>P4：Cfg=On + 非前台 + 最小化 → TryRestoreIconic（ShowWindow SW_RESTORE 路由）。</summary>
    [Property(MaxTest = 200)]
    public Property Decide_ReturnsTryRestoreIconic_WhenMinimized()
    {
        return (FocusRecoveryDecisions.Decide(new FocusRecoveryState(true, false, true))
            == FocusRecoveryDecision.TryRestoreIconic).ToProperty();
    }

    /// <summary>P5：Cfg=On + 非前台 + 可见 → TryFocus（FocusWindow 路由）。</summary>
    [Property(MaxTest = 200)]
    public Property Decide_ReturnsTryFocus_WhenVisibleNotForeground()
    {
        return (FocusRecoveryDecisions.Decide(new FocusRecoveryState(true, false, false))
            == FocusRecoveryDecision.TryFocus).ToProperty();
    }
}
