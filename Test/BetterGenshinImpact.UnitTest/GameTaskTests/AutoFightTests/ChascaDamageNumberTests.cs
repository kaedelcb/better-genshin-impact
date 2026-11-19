using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class ChascaDamageNumberTests
{
    [Theory]
    [InlineData(1.0, 1190, 258, 32, 30)]
    [InlineData(2.0 / 3, 794, 172, 20, 19)]
    public void SelectDamageNumber_ExcludesBulletSlotAndKeepsDamage(
        double scale, int slotX, int slotY, int slotWidth, int slotHeight)
    {
        var slot = new Region(slotX, slotY, slotWidth, slotHeight) { Text = "987654" };
        var damage = new Region(500, 280, 12, 9) { Text = "12345" };

        var result = AvatarCombatSpecialization.SelectDamageNumber([slot, damage], scale, excludeChascaBulletSlots: true);

        Assert.Equal((506, 284, "12345"), result);
        Assert.Equal("987654", AvatarCombatSpecialization.SelectDamageNumber([slot], scale, excludeChascaBulletSlots: false)?.text);
    }

    [Fact]
    public void IsFlying_ReadsFlightIndicatorIn720pFrame()
    {
        using var frame = new ImageRegion(new Mat(720, 1280, MatType.CV_8UC3, Scalar.All(0)), 0, 0);
        Assert.False(AvatarCombatSpecialization.IsFlying(frame));
        frame.SrcMat.Set(685, 1056, new Vec3b(255, 255, 255));
        Assert.True(AvatarCombatSpecialization.IsFlying(frame));
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
        Assert.Null(AvatarCombatSpecialization.SelectDamageNumber([slot], 1.0, excludeChascaBulletSlots: true));
    }
}
