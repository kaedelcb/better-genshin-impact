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

    [Fact]
    public void ServerOnlineExpiryDisarmsMatchingArmedGenerationAndPreservesScheduleEligibility()
    {
        var logs = new List<string>();
        var lifecycle = new OnlineIntentLifecycle(logs.Add);
        lifecycle.NoteScheduledOnlineConfigured();
        var gen = lifecycle.RaiseLocalIntent("command");
        lifecycle.NoteReportDelivered(gen);
        lifecycle.NoteReportPending(gen);

        Assert.False(lifecycle.OnServerOnlineExpired(0));
        Assert.False(lifecycle.OnServerOnlineExpired(gen - 1));
        Assert.False(lifecycle.OnServerOnlineExpired(gen + 1));
        Assert.Equal(OnlineIntentState.Armed, lifecycle.State);

        Assert.True(lifecycle.OnServerOnlineExpired(gen));
        Assert.Equal(OnlineIntentState.Idle, lifecycle.State);
        Assert.False(lifecycle.IsOnlineReady);
        Assert.Equal("none", lifecycle.OnlineMode);
        Assert.Null(lifecycle.ArmedOnlineGeneration);
        Assert.Equal(0, lifecycle.PendingOnlineReportGen);
        Assert.False(lifecycle.TryGetReReportGen(out _));
        Assert.False(lifecycle.ManuallyClearedOnline);
        Assert.Contains(logs, msg => msg.Contains("服务端上线意图超时过期", StringComparison.Ordinal));
        Assert.False(lifecycle.OnServerOnlineExpired(gen));
    }

    [Fact]
    public void OrchestrationRevocationCannotCancelAnotherGenerationOrAnAcceptedBatch()
    {
        var lifecycle = new OnlineIntentLifecycle(_ => { });
        var original = lifecycle.RaiseLocalIntent("command");
        var newer = lifecycle.RaiseLocalIntent("command");
        Assert.False(lifecycle.RevokeArmedIntent(original, "旧编排动作已撤销"));
        Assert.Equal(newer, lifecycle.ArmedOnlineGeneration);
        Assert.True(lifecycle.TryBeginCoordinatedBatch("newer", 71, manual: false, intentGeneration: newer));
        Assert.False(lifecycle.RevokeArmedIntent(newer, "编排上线已撤销"));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
    }

    [Fact]
    public void NumericGenerationReuseInAnotherRoomDoesNotAdoptOrRevokeOldOrchestration()
    {
        var lifecycle = new OnlineIntentLifecycle(_ => { });
        // Mailbox uses the assistant's local counter; a later native BGI signal can use the same number.
        var generation = lifecycle.RaiseLocalIntent("command", "a-to-b", "context-b");
        Assert.Equal("a-to-b", lifecycle.CurrentOrchestrationOrigin?.IntentId);
        Assert.False(lifecycle.TryBeginCoordinatedBatch("wrong", 20, false, generation));
        lifecycle.ChangeAssistantRoom(false);
        Assert.Null(lifecycle.CurrentOrchestrationOrigin);
        Assert.Equal(OnlineEdgeResult.Accepted, lifecycle.ApplyEdge(generation, DateTime.UtcNow, out var ordinary));
        Assert.Equal(generation, ordinary);
        Assert.Null(lifecycle.CurrentOrchestrationOrigin);
        Assert.False(lifecycle.RevokeArmedIntent(generation, "旧乙的迟到撤回", "a-to-b"));
        Assert.True(lifecycle.TryBeginCoordinatedBatch("room-c", 21, false, ordinary));
    }

    [Fact]
    public void ServerOnlineExpiryDoesNotInterruptExecutingBatch()
    {
        var lifecycle = new OnlineIntentLifecycle(_ => { });
        var gen = lifecycle.RaiseLocalIntent("command");
        Assert.True(lifecycle.TryBeginCoordinatedBatch("batch-1", 1, manual: false, intentGeneration: gen));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);

        Assert.False(lifecycle.OnServerOnlineExpired(gen));
        Assert.Equal(OnlineIntentState.Executing, lifecycle.State);
    }
}
