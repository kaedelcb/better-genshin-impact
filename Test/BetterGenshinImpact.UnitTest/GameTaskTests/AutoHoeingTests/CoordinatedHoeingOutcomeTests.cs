using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public class CoordinatedHoeingOutcomeTests
{
    [Theory]
    [InlineData(false, false, true, true, false, true, 4, false)] // Normal close after every world completed.
    [InlineData(false, false, false, false, false, true, 0, true)] // No remaining known routes is not all worlds done.
    [InlineData(false, false, false, false, true, false, 0, true)] // Party creation failed before world loop.
    [InlineData(false, false, false, true, false, false, 0, true)]
    [InlineData(false, false, false, false, false, false, 0, false)] // Valid single-world empty round.
    [InlineData(false, false, false, false, false, false, 1, true)]
    [InlineData(false, true, false, true, false, true, 9, false)] // Intentional exp cap is not recovery.
    [InlineData(true, true, true, true, false, true, 0, true)] // Actual exception must remain a failure.
    public void CompletionRequiresWorldEvidence(bool failed, bool cap, bool complete, bool terminated,
        bool setup, bool multi, int unexecuted, bool expected)
        => Assert.Equal(expected, CoordinatedHoeingOutcome.IsIncomplete(failed, cap, complete, terminated, setup, multi, unexecuted));
}
