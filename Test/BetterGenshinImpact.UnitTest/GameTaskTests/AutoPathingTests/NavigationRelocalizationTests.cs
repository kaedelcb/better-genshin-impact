using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoPathing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class NavigationRelocalizationTests
{
    private static readonly Point2f TeleportAnchor = new(13203.16f, 28105.44f);
    private static readonly Point2f GlobalSearch = new(-1, -1);
    private static readonly Point2f FarFalseMatch = new(23860.08f, 16158.41f);

    [Fact]
    public void LocalMatchUsesOriginalAnchorWithoutSearchingGlobally()
    {
        var expected = new Point2f(TeleportAnchor.X - 10, TeleportAnchor.Y);
        var calls = new List<Point2f>();

        var position = NavigationInstance.MatchRecoveryPosition(TeleportAnchor, anchor =>
        {
            calls.Add(anchor);
            return expected;
        }, _ => true, 50, out _);

        Assert.Equal(expected, position);
        Assert.Equal(new[] { TeleportAnchor }, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrUnusableLocalMatchFallsBackToGlobal(bool missingLocalMatch)
    {
        var calls = new List<Point2f>();
        var validGlobal = new Point2f(TeleportAnchor.X + 20, TeleportAnchor.Y);
        var unusableLocal = new Point2f(TeleportAnchor.X + 300, TeleportAnchor.Y);

        var position = NavigationInstance.MatchRecoveryPosition(TeleportAnchor, anchor =>
        {
            calls.Add(anchor);
            if (anchor == GlobalSearch) return validGlobal;
            return missingLocalMatch ? default : unusableLocal;
        }, candidate => candidate != unusableLocal, 50, out _);

        Assert.Equal(validGlobal, position);
        Assert.Equal(new[] { TeleportAnchor, GlobalSearch }, calls);
    }

    [Fact]
    public void FarGlobalMatchIsRejectedAndNextAttemptStillStartsAtOriginalAnchor()
    {
        var calls = new List<Point2f>();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var position = NavigationInstance.MatchRecoveryPosition(TeleportAnchor, anchor =>
            {
                calls.Add(anchor);
                return anchor == GlobalSearch ? FarFalseMatch : default;
            }, _ => true, 50, out _);

            Assert.Equal(default, position);
        }

        Assert.Equal(new[] { TeleportAnchor, GlobalSearch, TeleportAnchor, GlobalSearch }, calls);
    }
}
