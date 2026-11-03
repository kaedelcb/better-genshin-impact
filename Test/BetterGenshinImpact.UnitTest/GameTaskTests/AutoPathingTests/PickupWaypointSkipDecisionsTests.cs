using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public sealed class PickupWaypointSkipDecisionsTests
{
    [Fact]
    public void ThreeDetoursCountEveryJsonPointAndResumeAtTheFourth()
    {
        Waypoint[] segment =
        [
            new() { Type = "teleport" },
            new() { Type = "target", Action = "combat_script" }, // A
            new() { Type = "path" },
            new() { Type = "target", Action = "pick_up_collect" },
            new() { Type = "path" },
            new() { Type = "target", Action = "fight" }, // E is still executed normally
        ];

        Assert.Equal(5, PickupWaypointSkipDecisions.GetResumeIndex(segment, currentIndex: 1, skipCount: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)] // Reaches the end of the segment, with no resume point.
    [InlineData(int.MaxValue)]
    public void UnusableRangeDoesNotJump(int skipCount)
    {
        Waypoint[] segment = [new(), new(), new(), new()];
        Assert.Null(PickupWaypointSkipDecisions.GetResumeIndex(segment, currentIndex: 0, skipCount));
    }

    [Theory]
    [InlineData("teleport", null, null)]
    [InlineData("actionOnly", null, null)]
    [InlineData("orientation", null, null)]
    [InlineData("target", "force_tp", null)]
    [InlineData("target", "fight", null)]
    [InlineData("target", "combat_script", "meeting")]
    public void RequiredPointInsideRangeKeepsTheWholeRoute(string type, string? action, string? syncPoint)
    {
        Waypoint[] segment = [new(), new() { Type = type, Action = action, SyncPointId = syncPoint }, new()];
        Assert.Null(PickupWaypointSkipDecisions.GetResumeIndex(segment, currentIndex: 0, skipCount: 1));
    }

    [Fact]
    public void CannotResumeAtATeleportOrSkipACompleteTeleportSegment()
    {
        Waypoint[] segment = [new(), new(), new() { Type = "teleport" }, new()];
        Assert.Null(PickupWaypointSkipDecisions.GetResumeIndex(segment, currentIndex: 0, skipCount: 1));
        Assert.Null(PickupWaypointSkipDecisions.GetResumeIndex(segment, currentIndex: 0, skipCount: 2));
    }
}
