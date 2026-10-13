using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class OnlineIntentLifecycleResetTests
{
    [Fact]
    public void ResetKeepsAFutureScheduleEligibleButDoesNotUndoAnEarlierManualClear()
    {
        var lifecycle = new OnlineIntentLifecycle(_ => { });

        lifecycle.NoteScheduledOnlineConfigured();
        lifecycle.OnResetCleared();
        Assert.False(lifecycle.ManuallyClearedOnline);

        lifecycle.OnManualCleared();
        lifecycle.OnResetCleared();
        Assert.True(lifecycle.ManuallyClearedOnline);

        lifecycle.NoteScheduledOnlineConfigured();
        Assert.False(lifecycle.ManuallyClearedOnline);
    }

    [Fact]
    public void LateAllReadyAfterResetCannotStartUntilFreshIntentIsArmed()
    {
        var lifecycle = new OnlineIntentLifecycle(_ => { });
        lifecycle.RaiseLocalIntent("command");
        lifecycle.OnResetCleared();

        Assert.False(lifecycle.TryBeginAllReady(99));

        var generation = lifecycle.RaiseLocalIntent("command");
        Assert.True(lifecycle.TryBeginAllReady(generation));
    }
}
