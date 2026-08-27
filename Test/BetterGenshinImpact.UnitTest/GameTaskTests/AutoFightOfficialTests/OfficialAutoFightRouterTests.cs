#nullable enable

using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFightOfficial;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightOfficialTests;

/// <summary>
/// Feature: official-autofight-parallel-engine 路由决策属性测试（design §8.1）。
/// 覆盖 OfficialAutoFightRouter.UseOfficial：联机锄地恒 false、非联机等于开关值、null 不崩。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// </summary>
public class OfficialAutoFightRouterTests
{
    // ========== R3.3：联机锄地恒返回 false（无视开关） ==========
    [Property(MaxTest = 100)]
    public bool Multiplayer_AlwaysTeapot(bool useOfficial)
    {
        var cfg = new AutoFightConfig { UseOfficialAutoFight = useOfficial };
        return OfficialAutoFightRouter.UseOfficial(cfg, isMultiplayerHoeing: true) == false;
    }

    // ========== R3.1/R3.2：非联机时返回值 == 开关值 ==========
    [Property(MaxTest = 100)]
    public bool NonMultiplayer_EqualsSwitch(bool useOfficial)
    {
        var cfg = new AutoFightConfig { UseOfficialAutoFight = useOfficial };
        return OfficialAutoFightRouter.UseOfficial(cfg, isMultiplayerHoeing: false) == useOfficial;
    }

    // ========== 配置为 null 时安全回退茶包版，不抛异常 ==========
    [Property(MaxTest = 50)]
    public bool NullConfig_FallsBackToTeapot(bool isMultiplayerHoeing)
    {
        return OfficialAutoFightRouter.UseOfficial(null, isMultiplayerHoeing) == false;
    }

    // ========== 冒烟：默认配置（开关默认 false）非联机 → 茶包版 ==========
    [Fact]
    public void DefaultConfig_NonMultiplayer_IsTeapot()
    {
        var cfg = new AutoFightConfig();
        Assert.False(OfficialAutoFightRouter.UseOfficial(cfg, false));
    }
}
