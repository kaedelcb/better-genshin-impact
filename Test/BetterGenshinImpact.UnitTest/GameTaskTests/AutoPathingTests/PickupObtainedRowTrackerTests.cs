using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public sealed class PickupObtainedRowTrackerTests
{
    private const ulong A = 0x2222222222222222;
    private const ulong B = 0xcccccccccccccccc;
    private const ulong C = 0x5555555555555555;

    [Fact]
    public void OldRowsAreBaselinedAndTwoNewIdenticalRowsStayDistinct()
    {
        var tracker = new PickupObtainedRowTracker();
        Assert.False(tracker.Observe(Frame((553, A))).BaselineReady);
        Assert.Equal(new PickupRowUpdate(0, false, true), tracker.Observe(Frame((554, A))));

        var threeRows = Frame((553, A), (601, A), (649, A));
        Assert.Equal(0, tracker.Observe(threeRows).AddedRows);
        Assert.Equal(2, tracker.Observe(threeRows).AddedRows);
        Assert.Equal(0, tracker.Observe(threeRows).AddedRows);
    }

    [Fact]
    public void ConfirmedDisappearanceAllowsTheSameRowToCountAgain()
    {
        var tracker = WithEmptyBaseline();
        var oneRow = Frame((553, A));
        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
        var total = tracker.Observe(oneRow).AddedRows;

        Assert.False(tracker.Observe(Frame()).Cleared);
        Assert.False(tracker.Observe(Frame()).Cleared);
        Assert.Equal(new PickupRowUpdate(0, true, true), tracker.Observe(Frame()));
        Assert.False(tracker.Observe(Frame()).Cleared);

        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
        total += tracker.Observe(oneRow).AddedRows;
        Assert.Equal(2, total);
    }

    [Fact]
    public void TemporaryMissingOrSpuriousRowsAreNotCounted()
    {
        var tracker = WithEmptyBaseline();
        var oneRow = Frame((553, A));
        tracker.Observe(oneRow);
        Assert.Equal(1, tracker.Observe(oneRow).AddedRows);
        Assert.Equal(0, tracker.Observe(Frame((553, A), (601, B))).AddedRows);
        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
        Assert.False(tracker.Observe(Frame()).Cleared);
        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
    }

    [Fact]
    public void ScrollingKeepsOldRowsAndOnlyCountsANewRowAtTheEdge()
    {
        var tracker = WithEmptyBaseline();
        var initial = Frame((553, A), (601, B));
        tracker.Observe(initial);
        Assert.Equal(2, tracker.Observe(initial).AddedRows);
        Assert.Equal(0, tracker.Observe(Frame((528, A ^ 1), (577, B ^ 2))).AddedRows);
        Assert.Equal(0, tracker.Observe(Frame((553, B), (601, C))).AddedRows);
        Assert.Equal(1, tracker.Observe(Frame((554, B), (602, C))).AddedRows);
    }

    [Fact]
    public void InterruptedFramesRebaselineWithoutGuessingWhatWasPickedUp()
    {
        var tracker = WithEmptyBaseline();
        var oneRow = Frame((553, A));
        tracker.Observe(oneRow);
        Assert.Equal(1, tracker.Observe(oneRow).AddedRows);
        Assert.False(tracker.Observe(Frame()).Cleared);
        Assert.False(tracker.Observe(Frame()).Cleared);
        Assert.False(tracker.Observe(PickupRowObservation.Interrupted).BaselineReady);
        Assert.False(tracker.Observe(Frame()).Cleared);

        var twoRows = Frame((553, A), (601, B));
        Assert.False(tracker.Observe(twoRows).BaselineReady);
        Assert.Equal(new PickupRowUpdate(0, false, true), tracker.Observe(twoRows));
    }

    [Fact]
    public void ABriefFadeDoesNotPreventCountingTheFirstNewRow()
    {
        var tracker = WithEmptyBaseline();
        Assert.True(tracker.Observe(default).BaselineReady);
        Assert.True(tracker.Observe(default).BaselineReady);
        var oneRow = Frame((553, A));
        Assert.Equal(0, tracker.Observe(oneRow).AddedRows);
        Assert.Equal(1, tracker.Observe(oneRow).AddedRows);

        Assert.True(tracker.Observe(default).BaselineReady);
        Assert.True(tracker.Observe(default).BaselineReady);
        Assert.False(tracker.Observe(default).BaselineReady);
        var twoRows = Frame((553, A), (601, B));
        Assert.Equal(0, tracker.Observe(twoRows).AddedRows);
        Assert.Equal(new PickupRowUpdate(0, false, true), tracker.Observe(twoRows));
    }

    [Fact]
    public void EmptyBaselineSurvivesTitleFadeAndCountsAllThreeRows()
    {
        var tracker = WithEmptyBaseline();
        for (var frame = 0; frame < 4; frame++)
            Assert.Equal(new PickupRowUpdate(0, false, true), tracker.Observe(default));

        var oneRow = Frame((553, A));
        var twoRows = Frame((553, A), (601, A));
        var threeRows = Frame((553, A), (601, A), (649, A));
        foreach (var list in new[] { oneRow, twoRows, threeRows })
        {
            Assert.Equal(0, tracker.Observe(list).AddedRows);
            Assert.Equal(1, tracker.Observe(list).AddedRows);
            Assert.Equal(0, tracker.Observe(list).AddedRows);
        }
    }

    [Fact]
    public void InterruptedEmptyBaselineDoesNotCountRowsThatAppearDuringTheGap()
    {
        var tracker = WithEmptyBaseline();
        Assert.False(tracker.Observe(PickupRowObservation.Interrupted).BaselineReady);
        Assert.False(tracker.Observe(default).BaselineReady);
        var oneRow = Frame((553, A));
        Assert.False(tracker.Observe(oneRow).BaselineReady);
        Assert.Equal(new PickupRowUpdate(0, false, true), tracker.Observe(oneRow));
    }

    [Fact]
    public void InPlaceUnrelatedOrReorderedListsDoNotProveNewRows()
    {
        var tracker = WithEmptyBaseline();
        var initial = Frame((553, A), (601, B));
        tracker.Observe(initial);
        Assert.Equal(2, tracker.Observe(initial).AddedRows);

        var swapped = Frame((553, B), (601, A));
        tracker.Observe(swapped);
        Assert.Equal(0, tracker.Observe(swapped).AddedRows);
        var unrelated = Frame((553, C));
        tracker.Observe(unrelated);
        tracker.Observe(unrelated);
        Assert.Equal(0, tracker.Observe(unrelated).AddedRows);
    }

    [Fact]
    public void SingleRowDropDoesNotRetireAnExistingRow()
    {
        var tracker = WithEmptyBaseline();
        var twoRows = Frame((553, A), (601, A));
        tracker.Observe(twoRows);
        Assert.Equal(2, tracker.Observe(twoRows).AddedRows);
        var shortList = Frame((553, A));
        tracker.Observe(shortList);
        tracker.Observe(shortList);
        Assert.Equal(0, tracker.Observe(twoRows).AddedRows);
        Assert.Equal(0, tracker.Observe(twoRows).AddedRows);
    }

    private static PickupRowObservation Frame(params (double Y, ulong Signature)[] rows)
        => new(true, rows.Select(row => new PickupVisualRow(row.Y, row.Signature)).ToArray());

    private static PickupObtainedRowTracker WithEmptyBaseline()
    {
        var tracker = new PickupObtainedRowTracker();
        Assert.False(tracker.Observe(Frame()).BaselineReady);
        Assert.False(tracker.Observe(Frame()).BaselineReady);
        Assert.True(tracker.Observe(Frame()).BaselineReady);
        return tracker;
    }
}
