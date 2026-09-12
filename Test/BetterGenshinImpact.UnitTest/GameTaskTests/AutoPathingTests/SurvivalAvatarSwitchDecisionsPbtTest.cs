#nullable enable

using BetterGenshinImpact.GameTask.AutoPathing;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// SurvivalAvatarSwitchDecisions.PlanSwitchTarget 决策纯函数 PBT（A7 重整：红血生存切人）。
/// **Validates: 用户规格（2026-09-13 拍板）——红血 → 有行走位切行走位；无行走位切下一位（不能是万叶）；无路可切 → 0（走神像兜底）**
/// 输入域含非法组合（current/kazuha/walker ∈ [0,6]、N ∈ [1,6]），覆盖防御分支。
/// P1 为 oracle 全等价（规格直译参考实现），P6 为旧硬编码语义的等价性守护。
/// </summary>
public class SurvivalAvatarSwitchDecisionsPbtTest
{
    private static Gen<int> G(int lo, int hi) => Gen.Choose(lo, hi);

    /// <summary>P1（主规格，oracle 全等价）：行走位优先 / 轮换避万叶 / 无路可切，与规格直译参考实现全等价。</summary>
    [Property(MaxTest = 2000)]
    public Property PlanSwitchTarget_MatchesSpecOracle()
    {
        // 本仓库 FsCheck 版本 Prop.ForAll 最多 3 个 Arb → 4 输入用嵌套 ForAll（输入空间不变）
        return Prop.ForAll(
            Arb.From(G(1, 6)), Arb.From(G(1, 6)),
            (n, current) =>
                Prop.ForAll(
                    Arb.From(G(0, 6)), Arb.From(G(0, 6)),
                    (kazuha, walker) =>
                        (SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, n, kazuha, walker)
                         == Oracle(current, n, kazuha, walker)).ToProperty()));
    }

    /// <summary>P2：目标为有效序号时永不等于万叶位（万叶永远不是切人目标）。</summary>
    [Property(MaxTest = 1000)]
    public Property Target_NeverKazuha()
    {
        return Prop.ForAll(
            Arb.From(G(1, 6)), Arb.From(G(1, 6)),
            (n, current) =>
                Prop.ForAll(
                    Arb.From(G(0, 6)), Arb.From(G(0, 6)),
                    (kazuha, walker) =>
                    {
                        var t = SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, n, kazuha, walker);
                        return (t == 0 || !(kazuha >= 1 && kazuha <= n) || t != kazuha).ToProperty();
                    }));
    }

    /// <summary>P3（Q2 守护）：目标为有效序号时永不等于当前红血角色自己（防 no-op 自切）。</summary>
    [Property(MaxTest = 1000)]
    public Property Target_NeverSelf()
    {
        return Prop.ForAll(
            Arb.From(G(1, 6)), Arb.From(G(1, 6)),
            (n, current) =>
                Prop.ForAll(
                    Arb.From(G(0, 6)), Arb.From(G(0, 6)),
                    (kazuha, walker) =>
                    {
                        var t = SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, n, kazuha, walker);
                        return (t == 0 || t != current).ToProperty();
                    }));
    }

    /// <summary>P4（Q1 优先级）：行走位合法且 != 自己且 != 万叶 → 恒切行走位。</summary>
    [Property(MaxTest = 1000)]
    public Property WalkerPriority_WhenValidAndNotSelfAndNotKazuha()
    {
        return Prop.ForAll(
            Arb.From(G(1, 6)), Arb.From(G(1, 6)),
            (n, current) =>
                Prop.ForAll(
                    Arb.From(G(0, 6)), Arb.From(G(0, 6)),
                    (kazuha, walker) =>
                    {
                        var t = SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, n, kazuha, walker);
                        var currentValid = current >= 1 && current <= n;
                        var walkerUsable = walker >= 1 && walker <= n && walker != current
                                           && !(kazuha >= 1 && kazuha <= n && kazuha == walker);
                        return (!currentValid || !walkerUsable || t == walker).ToProperty();
                    }));
    }

    /// <summary>P5（Q4 守护）：两人队 [万叶, X] 的 X 红血 → 无路可切 → 0（修复原实现的 no-op 自切空转）。</summary>
    [Property(MaxTest = 200)]
    public Property TwoManTeam_WithKazuha_ReturnsNoPath()
    {
        return Prop.ForAll(Arb.From(G(1, 2)), current =>
        {
            var kazuha = current == 1 ? 2 : 1;
            return (SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, 2, kazuha, 0) == 0).ToProperty();
        });
    }

    /// <summary>P6（旧语义等价守护）：无万叶、无行走位、N==4 → 与原硬编码 (current % 4) + 1 逐值等价。</summary>
    [Property(MaxTest = 500)]
    public Property LegacyEquivalence_NoKazuhaNoWalker_N4()
    {
        return Prop.ForAll(Arb.From(G(1, 4)), current =>
        {
            var expected = (current % 4) + 1;
            return (SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, 4, 0, 0) == expected).ToProperty();
        });
    }

    /// <summary>P7（防御分支）：当前起点越界或 N &lt; 1 → 恒 0，绝不抛异常、绝不产出越界序号。</summary>
    [Property(MaxTest = 500)]
    public Property InvalidInputs_AlwaysZero()
    {
        return Prop.ForAll(
            Arb.From(G(-2, 8)), Arb.From(G(0, 6)),
            (current, n) =>
                Prop.ForAll(
                    Arb.From(G(-2, 8)), Arb.From(G(-2, 8)),
                    (kazuha, walker) =>
                    {
                        if (n >= 1 && current >= 1 && current <= n) return true.ToProperty();
                        return (SurvivalAvatarSwitchDecisions.PlanSwitchTarget(current, n, kazuha, walker) == 0).ToProperty();
                    }));
    }

    /// <summary>参考实现：用户规格的直译（行走位优先 → 轮换避万叶且避自己 → 无路可切 0）。</summary>
    private static int Oracle(int current, int n, int kazuha, int walker)
    {
        if (n < 1) return 0;
        if (current < 1 || current > n) return 0;
        var hasKazuha = kazuha >= 1 && kazuha <= n;
        if (walker >= 1 && walker <= n && walker != current && !(hasKazuha && walker == kazuha)) return walker;

        var candidate = current;
        for (var i = 0; i < n; i++)
        {
            candidate = (candidate % n) + 1;
            if (candidate != current && !(hasKazuha && candidate == kazuha)) return candidate;
        }

        return 0;
    }
}
