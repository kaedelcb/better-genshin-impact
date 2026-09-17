namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

public static class CoordinatedHoeingOutcome
{
    public static bool IsIncomplete(bool executionFailed, bool expCapStop, bool completedNormally,
        bool sessionTerminated, bool setupIncomplete, bool multiWorldExpected, int unexecutedCount)
        => executionFailed || (!expCapStop && (setupIncomplete
            || (multiWorldExpected && !completedNormally)
            || (!completedNormally && (sessionTerminated || unexecutedCount > 0))));
}
