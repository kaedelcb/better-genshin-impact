using System.Diagnostics;
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
    public void DistanceUsesFreshCoordinatesAndIncludesFiveButNotBeyond()
    {
        var target = new Waypoint { X = 10, Y = 20 };
        Assert.True(PickupWaypointApproachWindow.IsWithinStartDistance(target, new Point2f(15, 20), out var distance));
        Assert.Equal(5, distance);
        Assert.False(PickupWaypointApproachWindow.IsWithinStartDistance(target, new Point2f(15.01f, 20), out _));
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

    [Fact]
    public void DepartureCountsOnlyNewRowsAfterFreshInRangePositionAndKeepsAReachedResult()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window);
        var owner = new Waypoint { X = 10, Y = 20 };

        Assert.True(window.BeginDeparture(0, 3));
        window.EnterPoint(1);
        Assert.True(window.IsDepartureFor(1));

        // 新位置还没确认；这两帧不能补齐目标。
        SampleRows(progress, 4);
        Assert.Equal(2, progress.Count);

        var preConfirmationFrame = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 100;
        window.ObserveDeparturePosition(1, owner, new Point2f(15, 20));
        progress.ObserveFrame(Rows(4), default, preConfirmationFrame, 50);
        progress.ObserveFrame(Rows(4), default, preConfirmationFrame + 1, 50);
        Assert.Equal(2, progress.Count);
        SampleRows(progress, 2);
        Assert.Equal(2, progress.Count);
        SampleRows(progress, 4);
        Assert.Equal(4, progress.Count);

        window.ObserveDeparturePosition(1, owner, new Point2f(15.01f, 20));
        Assert.Equal(3, window.FinishDeparture(1));
        Assert.Null(window.FinishDeparture(1));
        Assert.Null(PickupWaypointProgress.Capture());
    }

    [Theory]
    [InlineData(15.01f, 20f)]
    [InlineData(0f, 0f)]
    public void LeavingRangeOrLosingPositionSealsAnUnfinishedDeparture(float x, float y)
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window);
        var owner = new Waypoint { X = 10, Y = 20 };

        Assert.True(window.BeginDeparture(0, 3));
        window.EnterPoint(1);
        window.ObserveDeparturePosition(1, owner, new Point2f(14, 20));
        // 首帧看见新增但尚未稳定。第二帧晚到时已失去对 A 的位置确认。
        progress.ObserveFrame(Rows(4), default, Stopwatch.GetTimestamp(), 50);
        window.ObserveDeparturePosition(1, owner, new Point2f(x, y));
        window.ObserveDeparturePosition(1, owner, new Point2f(14, 20));
        SampleRows(progress, 4);

        Assert.Equal(2, progress.Count);
        Assert.Null(window.FinishDeparture(1));
        Assert.Null(PickupWaypointProgress.Capture());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("combat_script")]
    [InlineData("pick_around")]
    public void NeighboringPickupClosesDepartureEvenWhenFallbackActionBlocksItsEarlyObserver(string? fallbackAction)
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window);
        var next = PickupPoint();
        next.X = 18;
        next.Y = 20;
        Waypoint[] segment = [new() { X = 10, Y = 20 }, new() { Action = fallbackAction }, next];
        Assert.True(window.BeginDeparture(0, 2));
        window.EnterPoint(1);
        var approachCalls = 0;
        Action<Point2f>? nextObserver = PickupWaypointApproachWindow.SelectObservationIndex(segment, 1) == 2
            ? _ => approachCalls++ : null;
        var observe = window.WithDepartureObserver(segment, 1, _ => true, nextObserver)!;

        observe(new Point2f(11, 20));
        Assert.True(window.IsDepartureFor(1));
        // 同时进入 A、C 的范围；此时 C 的提示不能把 A 从 2/4 补到 4/4。
        observe(new Point2f(14, 20));
        SampleRows(progress, 4);

        Assert.Equal(2, progress.Count);
        Assert.Null(window.FinishDeparture(1));
        Assert.Equal(fallbackAction == null ? 1 : 0, approachCalls);
    }

    [Fact]
    public void FinishingBeforeTheFallbackActionRejectsLateFramesAndReleasesTheNextObserver()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window);
        var owner = new Waypoint { X = 10, Y = 20 };
        Assert.True(window.BeginDeparture(0, 3));
        window.EnterPoint(1);
        window.ObserveDeparturePosition(1, owner, new Point2f(14, 20));

        Assert.Null(window.FinishDeparture(1));

        using var next = PickupWaypointProgress.TryBegin(1);
        Assert.NotNull(next);
        SampleRows(progress, 4);
        Assert.Equal(2, progress.Count);
        Assert.Equal(0, next.Count);
        Assert.Null(window.FinishDeparture(1));
    }

    [Fact]
    public void ACountOnlyNodeCannotCarryItsObservationIntoTheNextPoint()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window, plannedResume: null);

        Assert.False(window.BeginDeparture(0, 3));
        window.EnterPoint(1);

        Assert.False(window.IsDepartureFor(1));
        Assert.Null(PickupWaypointProgress.Capture());
    }

    [Fact]
    public void DepartureCannotSkipAnIndependentPickupPoint()
    {
        Waypoint[] nextIsIndependent = [PickupPoint(), PickupPoint(), new(), new()];
        Assert.Null(PickupWaypointSkipDecisions.GetDepartureResumeIndex(nextIsIndependent, 0, 3));

        Waypoint[] laterIsIndependent = [PickupPoint(), new(), PickupPoint(), new()];
        Assert.Equal(2, PickupWaypointSkipDecisions.GetDepartureResumeIndex(laterIsIndependent, 0, 3));
    }

    [Fact]
    public void DepartureKeepsTheEndOfRouteResumeOnlyForAnAlreadyValidFinalSegment()
    {
        Waypoint[] segment = [PickupPoint(), new(), new()];
        var finalResume = PickupWaypointSkipDecisions.GetResumeIndex(segment, 0, 2, isLastSegment: true, out _);
        var nonfinalResume = PickupWaypointSkipDecisions.GetResumeIndex(segment, 0, 2, isLastSegment: false, out _);

        Assert.Equal(segment.Length, PickupWaypointSkipDecisions.GetDepartureResumeIndex(segment, 0, finalResume));
        Assert.Null(PickupWaypointSkipDecisions.GetDepartureResumeIndex(segment, 0, nonfinalResume));
    }

    private static PickupWaypointProgress StartWithTwoRows(PickupWaypointApproachWindow window, int? plannedResume = 3)
    {
        var progress = PickupWaypointProgress.TryBegin(4)!;
        window.EnterPoint(0);
        window.Start(0, progress, plannedResume);
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        for (var i = 0; i < 2; i++) progress.Observe(Rows(2));
        Assert.Equal(2, progress.Count);
        return progress;
    }

    private static void SampleRows(PickupWaypointProgress progress, int count)
    {
        var observation = Rows(count);
        for (var i = 0; i < 2; i++)
            progress.ObserveFrame(observation, default, Stopwatch.GetTimestamp(), 50);
    }

    private static PickupRowObservation Rows(int count)
    {
        var rows = new PickupVisualRow[count];
        for (var index = 0; index < rows.Length; index++)
            rows[index] = new PickupVisualRow(563 + 48 * index, 0x789CE33256DBE2AC);
        return new PickupRowObservation(true, rows);
    }

    private static Waypoint PickupPoint() => new()
    {
        Type = "target", Action = "combat_script", PickupTargetCount = new PickupTarget(1, 0),
    };
}
