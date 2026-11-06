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
    [InlineData("path", null)]
    [InlineData("path", "combat_script")]
    [InlineData("target", "fight")]
    [InlineData("target", "pick_around")]
    [InlineData("orientation", "log_output")]
    [InlineData("actionOnly", "nahida_collect")]
    [InlineData("teleport", null)]
    public void EveryValidMarkedNodeIsObservedWithoutTypeOrActionWhitelist(string type, string? action)
    {
        var point = new Waypoint { Type = type, Action = action, PickupTargetCount = new PickupTarget(1, 0) };
        Assert.True(PickupWaypointApproachWindow.IsPickupTarget(point));
        Assert.Equal(0, PickupWaypointApproachWindow.SelectObservationIndex([point, PickupPoint()], 0));
        var executor = new PathExecutor(CancellationToken.None)
        {
            PartyConfig = new BetterGenshinImpact.Core.Config.PathingPartyConfig { EnablePickupTargetCount = true },
        };
        Assert.True(executor.CanUseWaypointPickup(point));
        executor.PartyConfig.EnablePickupTargetCount = false;
        Assert.False(executor.CanUseWaypointPickup(point));
    }

    [Theory]
    [InlineData("path", "pick_around", null)]
    [InlineData("target", "combat_script", null)]
    [InlineData("orientation", null, null)]
    [InlineData("actionOnly", null, "sync_1")]
    public void PreviousActionsAndSyncMarksDoNotDisablePreparingTheNextMarkedNode(string type, string? action, string? syncId)
    {
        Waypoint[] segment = [new() { Type = type, Action = action, SyncPointId = syncId }, PickupPoint()];
        Assert.Equal(1, PickupWaypointApproachWindow.SelectObservationIndex(segment, 0));
        Assert.Null(PickupWaypointApproachWindow.SelectObservationIndex([new() { Type = "teleport" }, PickupPoint()], 0));
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
    public void NeighboringPickupClosesDepartureWithAnyFallbackAction(string? fallbackAction)
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
        Assert.Equal(1, approachCalls);
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

    [Fact]
    public void NormalPathArrivalActivatesPreparedCountingWithoutAnExtraMoveOrWait()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(2, counting: false)!;
        var owner = new Waypoint { Type = "path", X = 10, Y = 20, PickupTargetCount = new PickupTarget(2, 0) };
        window.Start(0, progress, null);
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        Assert.Equal(0, progress.Count);

        Assert.False(PickupWaypointApproachWindow.IsWithinStartDistance(owner, new Point2f(15.5f, 20), out _));
        Assert.True(window.ActivateForPoint(0));
        SampleRows(progress, 2);
        Assert.Equal(2, progress.Count);
        Assert.Null(window.CompletePoint([owner], 0));
        Assert.True(progress.IsDisposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pick_around")]
    [InlineData("fight")]
    [InlineData("log_output")]
    public void MarkedNodeCanCompleteAndSkipWithoutACombatScript(string? action)
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        Waypoint[] segment =
        [
            new() { Type = "path", Action = action, PickupTargetCount = new PickupTarget(1, 1) },
            new(),
            new()
        ];
        window.Start(0, progress, 2);
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        SampleRows(progress, 1);
        Assert.Equal(2, window.CompletePoint(segment, 0));
        Assert.True(progress.IsDisposed);
    }

    [Fact]
    public void ARejectedSkipPlanDoesNotDisableTheCurrentNodesCount()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        Waypoint[] segment =
        [
            new() { Type = "path", PickupTargetCount = new PickupTarget(1, 2) },
            new() { Type = "teleport" }
        ];
        var resume = PickupWaypointSkipDecisions.GetResumeIndex(segment, 0, 2);
        Assert.Null(resume);
        window.Start(0, progress, resume);
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        SampleRows(progress, 1);
        Assert.True(progress.ReachedToken.IsCancellationRequested);
        Assert.Null(window.CompletePoint(segment, 0));
    }

    [Fact]
    public void RetiredFailedActionsCannotTurnTheirCountIntoASkipAtTheNodeBoundary()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = StartWithTwoRows(window);
        progress.Dispose();
        Assert.Null(window.CompletePoint([new(), new(), new(), new()], 0));
    }

    [Fact]
    public void AFrameCapturedBeforeCountingActivationCannotIncrementTheNode()
    {
        using var progress = PickupWaypointProgress.TryBegin(2, counting: false)!;
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        var oldFrame = Stopwatch.GetTimestamp() - 1;
        Assert.True(progress.ActivateCounting());
        progress.ObserveFrame(Rows(2), default, oldFrame, 50);
        progress.ObserveFrame(Rows(2), default, oldFrame, 50);
        Assert.Equal(0, progress.Count);
        SampleRows(progress, 2);
        Assert.Equal(2, progress.Count);
    }

    [Fact]
    public void ReachedCountCannotSwallowAnUnrelatedOrRouteCancellation()
    {
        using var progress = PickupWaypointProgress.TryBegin(1)!;
        for (var i = 0; i < 3; i++) progress.Observe(Rows(0));
        SampleRows(progress, 1);
        using var operation = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        using var route = new CancellationTokenSource();
        Assert.True(progress.IsOwnCancellation(new OperationCanceledException(operation.Token), operation.Token, route.Token));
        Assert.True(progress.IsOwnCancellation(new OperationCanceledException(progress.ReachedToken), operation.Token, route.Token));
        Assert.False(progress.IsOwnCancellation(new OperationCanceledException(other.Token), operation.Token, route.Token));
        route.Cancel();
        Assert.False(progress.IsOwnCancellation(new OperationCanceledException(operation.Token), operation.Token, route.Token));
    }

    [Fact]
    public void ArrivalWaitOnlyUsesItsOwnNodeOrItsDeclaredPickupDetour()
    {
        using var window = new PickupWaypointApproachWindow();
        using var progress = PickupWaypointProgress.TryBegin(2)!;
        window.Start(1, progress, 4);

        // 下一点的预备观察不能提前结束当前普通点的停顿。
        Assert.Null(window.GetForArrival(0));
        Assert.Same(progress, window.GetForArrival(1));
        Assert.Null(window.GetForArrival(2));

        Assert.True(window.BeginDeparture(1, 4));
        window.EnterPoint(2);
        Assert.Same(progress, window.GetForArrival(2));
        Assert.Null(window.GetForArrival(3));

        progress.Dispose();
        Assert.Null(window.GetForArrival(2));
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
