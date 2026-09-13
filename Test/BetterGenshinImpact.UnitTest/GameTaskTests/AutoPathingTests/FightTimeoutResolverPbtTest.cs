#nullable enable

using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// B5 收口测试：FightTimeoutResolver.ResolveWaypointTimeout（waypoint 级战斗超时解析）。
/// 以"直译 oracle"守护零行为变化：Action 为 fight 且 ActionParams 可解析整数 → 返回该秒数，否则 null
/// （与 AutoFightHandler 原内联块的判定条件/int.TryParse 语义逐字等价）。
/// 输入域：action 域（fight/其他动作/null）× 任意 ActionParams 字符串全空间 + 显式边界样本。
/// </summary>
public class FightTimeoutResolverPbtTest
{
    /// <summary>oracle 用字面量，与 SUT 引用的 ActionEnum.Fight.Code（="fight"）相互独立。</summary>
    private const string FightCode = "fight";

    /// <summary>action 域编码（仿 ZeroCoordGuardPbtTest 整数域习惯）：0=null，1=fight，2=move，3=combat_script。</summary>
    private static string? ActionOf(int kind) => kind switch
    {
        0 => null,
        1 => FightCode,
        2 => "move",
        _ => "combat_script",
    };

    /// <summary>规格直译 oracle：AutoFightHandler 原内联实现（块 A/块 D）的语义等价式。</summary>
    private static int? Oracle(string? action, string? actionParams)
    {
        if (action != FightCode || string.IsNullOrEmpty(actionParams))
        {
            return null;
        }

        return int.TryParse(actionParams, out var number) ? number : null;
    }

    /// <summary>P1（主规格，全域真值表）：action 域 {null, fight, move, combat_script} × 任意参数串，结果与 oracle 全等价。</summary>
    [Property(MaxTest = 500)]
    public Property Resolve_MatchesSpecOracle(int actionKind, string actionParams)
    {
        var action = ActionOf(actionKind);
        var expected = Oracle(action, actionParams);
        return (FightTimeoutResolver.ResolveWaypointTimeout(action, actionParams) == expected).ToProperty();
    }

    /// <summary>P2（防御分支）：非 fight 的 action（含 null）恒不注入，无论 ActionParams 是什么。</summary>
    [Property(MaxTest = 200)]
    public Property Resolve_NonFightActionAlwaysNull(int actionKind, string actionParams)
    {
        var action = ActionOf(actionKind == 1 ? 2 : actionKind);
        return (FightTimeoutResolver.ResolveWaypointTimeout(action, actionParams) == null).ToProperty();
    }

    /// <summary>P3（防御分支）：即使 action==fight，ActionParams 为 null 或空串也恒不注入（边界含空串）。</summary>
    [Property(MaxTest = 100)]
    public Property Resolve_NullOrEmptyParamsAlwaysNull(int paramsKind)
    {
        var actionParams = paramsKind == 0 ? null : string.Empty;
        return (FightTimeoutResolver.ResolveWaypointTimeout(FightCode, actionParams) == null).ToProperty();
    }

    /// <summary>P4（往返）：任意 int（含负数/0/int.MinValue/int.MaxValue）注入后必须原值返回——现状语义负数也照注入。</summary>
    [Property(MaxTest = 500)]
    public Property Resolve_ParsedIntRoundTrips(int value)
    {
        return (FightTimeoutResolver.ResolveWaypointTimeout(FightCode, value.ToString()) == value).ToProperty();
    }

    /// <summary>P5（边界样本）：明确非整数的 ActionParams 恒不注入。
    /// 注意：" 5 "（前导/尾随空白）在 int.TryParse 现状语义下可解析，属 P1 oracle 覆盖范围，不列为本样本。</summary>
    [Property(MaxTest = 100)]
    public Property Resolve_NonNumericSamplesAlwaysNull(int sampleKind)
    {
        string[] samples = { "abc", "12abc", "1.5", "30,40", "12 34", "＋5" };
        var actionParams = samples[System.Math.Abs(sampleKind) % samples.Length];
        return (FightTimeoutResolver.ResolveWaypointTimeout(FightCode, actionParams) == null).ToProperty();
    }
}
