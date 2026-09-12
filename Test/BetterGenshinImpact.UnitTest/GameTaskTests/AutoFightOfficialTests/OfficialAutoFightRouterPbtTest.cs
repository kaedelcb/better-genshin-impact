using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFightOfficial;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightOfficialTests;

/// <summary>
/// OfficialAutoFightRouter.UseOfficial 路由决策真值表 PBT（B4 收口）。
/// **Validates: official-autofight-parallel-engine spec §4.2**
/// 路由契约三维度：
///   1) 联机锄地（isMultiplayerHoeing=true）→ 恒走茶包版（false），无视开关（R3.3）
///   2) 非联机 + 配置非空 → 返回 UseOfficialAutoFight 开关值
///   3) 非联机 + 配置为空 → false（安全回退茶包版）
/// </summary>
public class OfficialAutoFightRouterPbtTest
{
    private static AutoFightConfig MakeConfig(bool useOfficial)
        => new AutoFightConfig { UseOfficialAutoFight = useOfficial };

    /// <summary>P1（R3.3 主守护）：联机锄地恒走茶包版——开关任意、配置任意（含 null）恒 false。</summary>
    [Property(MaxTest = 300)]
    public Property UseOfficial_MultiplayerHoeing_AlwaysFalse(bool switchOn, bool configIsNull)
    {
        AutoFightConfig? config = configIsNull ? null : MakeConfig(switchOn);
        return (!OfficialAutoFightRouter.UseOfficial(config, true)).ToProperty();
    }

    /// <summary>P2：非联机 + 配置非空 → 恒等于开关值。</summary>
    [Property(MaxTest = 300)]
    public Property UseOfficial_NotMultiplayer_EqualsSwitch(bool switchOn)
    {
        return (OfficialAutoFightRouter.UseOfficial(MakeConfig(switchOn), false) == switchOn).ToProperty();
    }

    /// <summary>P3（安全回退）：非联机 + 配置为空 → false（不抛异常，回退茶包版）。</summary>
    [Fact]
    public void UseOfficial_NullConfig_SafeFallbackToTeapot()
    {
        Assert.False(OfficialAutoFightRouter.UseOfficial(null, false));
    }

    /// <summary>P4 全维度真值表：联机 × 开关（非空配置）。</summary>
    [Property(MaxTest = 200)]
    public Property UseOfficial_FullTruthTable(bool isMultiplayer, bool switchOn)
    {
        var expected = !isMultiplayer && switchOn;
        return (OfficialAutoFightRouter.UseOfficial(MakeConfig(switchOn), isMultiplayer) == expected).ToProperty();
    }

    /// <summary>P5 默认值守护：UseOfficialAutoFight 默认 false——不开开关则全走茶包版（存量行为不变）。</summary>
    [Fact]
    public void AutoFightConfig_UseOfficialAutoFight_DefaultFalse()
    {
        Assert.False(new AutoFightConfig().UseOfficialAutoFight);
    }
}
