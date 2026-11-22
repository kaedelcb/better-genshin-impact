using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ChascaDamageNumberTests
{
    [Fact]
    public void SelectDamageNumber_ExcludesBulletSlotAndKeepsDamage()
    {
        var slot = new Region(1190, 258, 32, 30) { Text = "987654" };
        var damage = new Region(500, 280, 12, 9) { Text = "12345" };

        var result = AvatarCombatSpecialization.SelectDamageNumber([slot, damage], excludeChascaBulletSlots: true);

        Assert.Equal((506, 284, "12345"), result);
        Assert.Equal("987654", AvatarCombatSpecialization.SelectDamageNumber([slot], excludeChascaBulletSlots: false)?.text);
    }

    [Fact]
    public void LegendaryLoss_ResetsOnDamageAndExitsAfterTwoUnconfirmedFrames()
    {
        var lostFrames = 0;
        Assert.False(AvatarCombatSpecialization.ShouldExitAfterChascaLegendaryLost(ref lostFrames, true, false, 1, 0.75));
        Assert.False(AvatarCombatSpecialization.ShouldExitAfterChascaLegendaryLost(ref lostFrames, true, true, 1, 0.75));
        Assert.False(AvatarCombatSpecialization.ShouldExitAfterChascaLegendaryLost(ref lostFrames, true, false, 1, 0.75));
        Assert.True(AvatarCombatSpecialization.ShouldExitAfterChascaLegendaryLost(ref lostFrames, true, false, 1, 0.75));
    }

    [Fact]
    public void SelectDamageNumber_ReturnsNoTarget_WhenOnlyBulletSlotsAreVisible()
    {
        var slot = new Region(1238, 340, 22, 15) { Text = "9876" };
        Assert.Null(AvatarCombatSpecialization.SelectDamageNumber([slot], excludeChascaBulletSlots: true));
    }
}
