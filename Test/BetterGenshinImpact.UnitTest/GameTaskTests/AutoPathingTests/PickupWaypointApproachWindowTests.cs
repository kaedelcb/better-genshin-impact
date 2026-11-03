using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupWaypointApproachWindowTests
{
    [Fact]
    public void PlainPointCanObserveOnlyTheNextTargetAndOwnPickupTakesPrecedence()
    {
        Waypoint[] segment = [new(), PickupPoint(), PickupPoint()];

        Assert.Equal(1, PickupWaypointApproachWindow.SelectObservationIndex(segment, 0));
        Assert.Equal(1, PickupWaypointApproachWindow.SelectObservationIndex(segment, 1));
        Assert.Null(PickupWaypointApproachWindow.SelectObservationIndex([new(), new(), PickupPoint()], 0));
    }

    [Theory]
    [InlineData("teleport", null, null, false)]
    [InlineData("orientation", null, null, false)]
    [InlineData("target", "pick_around", null, false)]
    [InlineData("path", null, "sync_1", false)]
    [InlineData("path", null, null, true)]
    public void CompetingOrMarkedPointCannotStartObservationForNextPoint(
        string type, string? action, string? syncId, bool marked)
    {
        Waypoint[] segment = [new() { Type = type, Action = action, SyncPointId = syncId,
            PickupTargetCount = marked ? new PickupTarget(1, 0) : null }, PickupPoint()];

        Assert.Null(PickupWaypointApproachWindow.SelectObservationIndex(segment, 0));
    }

    [Fact]
    public void DistanceUsesFreshCoordinatesAndIncludesThreeButNotBeyond()
    {
        var target = new Waypoint { X = 10, Y = 20 };
        Assert.True(PickupWaypointApproachWindow.IsWithinStartDistance(target, new Point2f(13, 20), out var distance));
        Assert.Equal(3, distance);
        Assert.False(PickupWaypointApproachWindow.IsWithinStartDistance(target, new Point2f(13.01f, 20), out _));
        Assert.False(PickupWaypointApproachWindow.IsWithinStartDistance(new Waypoint(), default, out _));
        Assert.False(PickupWaypointApproachWindow.IsWithinStartDistance(target, new Point2f(float.NaN, 20), out _));
    }

    [Fact]
    public void EnteringObservedTargetKeepsRowsAlreadyObtainedWhileApproaching()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(2)!;
        window.EnterPoint(0);
        window.Start(1, progress, 3);
        for (var i = 0; i < 3; i++) progress.Observe(new PickupRowObservation(true, []));
        for (var i = 0; i < 2; i++)
            progress.Observe(new PickupRowObservation(true, [new PickupVisualRow(563, 0x789CE33256DBE2AC)]));

        Assert.Null(window.GetForPoint(0, out var otherResume));
        Assert.Null(otherResume);
        window.EnterPoint(1);
        Assert.True(window.HasObservationFor(1));
        var adopted = window.GetForPoint(1, out var resume);
        Assert.Same(progress, adopted);
        Assert.Equal(1, adopted!.Count);
        Assert.Equal(3, resume);
    }

    [Fact]
    public void SkippingObservedPointReleasesItsObserver()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(2)!;
        window.EnterPoint(0);
        window.Start(1, progress, null);
        Assert.Same(progress, PickupWaypointProgress.Capture());

        window.EnterPoint(2);

        Assert.False(window.HasObservationFor(1));
        Assert.Null(PickupWaypointProgress.Capture());
    }

    private static Waypoint PickupPoint() => new()
    {
        Type = "target", Action = "combat_script", PickupTargetCount = new PickupTarget(1, 0),
    };
}
