using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedControlAuthorityTests
{
    [Fact]
    public async Task ReplacementOwnsTheJoinWindowAndOldGrantCannotSendAfterAdoption()
    {
        var authority = new CoordinatedControlAuthority();
        var old = new object();
        var replacement = new object();
        authority.Publish(old, "room-a");
        var grant = authority.Capture(old, "room-a");
        var transition = await authority.EnterAsync(CancellationToken.None);
        authority.Revoke(old); // Before the new join can take the old server identity.
        Assert.True(grant.Revoked.IsCancellationRequested);
        var waitingUpdate = authority.EnterAsync(CancellationToken.None);
        Assert.False(waitingUpdate.IsCompleted);
        authority.Publish(replacement, "room-a");
        transition.Dispose();
        using (await waitingUpdate.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Throws<ControlChannelUnavailableException>(() => authority.Verify(grant));
            Assert.Throws<ControlChannelUnavailableException>(() => authority.Capture(old, "room-a"));
            authority.Verify(authority.Capture(replacement, "room-a"));
        }
        var sends = 0;
        await Assert.ThrowsAsync<ControlSubmissionDeferredException>(() => authority.SubmitAsync(grant,
            () => { }, _ => Task.FromResult(++sends), CancellationToken.None));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task StopCancelsGateWaitWithoutWaitingForTheJoinOrDisposingItsOwner()
    {
        var authority = new CoordinatedControlAuthority();
        using var join = await authority.EnterAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var waiting = authority.EnterAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task RevocationAfterJobReadIsDefinitelyNotSentButRevocationAfterSendRemainsUnknown()
    {
        var authority = new CoordinatedControlAuthority();
        var owner = new object();
        authority.Publish(owner, "room-a");
        var beforeRead = authority.Capture(owner, "room-a");
        authority.Revoke(owner); // Simulate a reconnect during the awaited ReadJobs call.
        var sends = 0;
        await Assert.ThrowsAsync<ControlSubmissionDeferredException>(() => authority.SubmitAsync(beforeRead,
            () => { }, _ => Task.FromResult(++sends), CancellationToken.None));
        Assert.Equal(0, sends);

        authority.Publish(owner, "room-a");
        var current = authority.Capture(owner, "room-a");
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitting = authority.SubmitAsync(current, () => { }, async ct =>
        {
            Interlocked.Increment(ref sends);
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }, CancellationToken.None);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        authority.Revoke(owner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitting);
        Assert.Equal(1, sends); // The caller must retain Submitted and reconcile its original request key.
    }

    [Fact]
    public async Task CancellationAfterAdmissionButBeforeInvokingSendIsDefinitelyNotSent()
    {
        var authority = new CoordinatedControlAuthority();
        var owner = new object();
        authority.Publish(owner, "room-a");
        var grant = authority.Capture(owner, "room-a");
        using var cancellation = new CancellationTokenSource();
        var sends = 0;
        await Assert.ThrowsAsync<ControlSubmissionDeferredException>(() => authority.SubmitAsync(grant,
            () => cancellation.Cancel(), _ => Task.FromResult(++sends), cancellation.Token));
        // Admission and Verify succeeded. The final linked-token check caught the cancellation.
        authority.Verify(grant);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task FailedAdoptionCannotMakeAnOldConnectedOwnerHealthyAgain()
    {
        var authority = new CoordinatedControlAuthority();
        var old = new object();
        authority.Publish(old, "room-a");
        using (await authority.EnterAsync(CancellationToken.None)) authority.Revoke(old);
        Assert.Throws<ControlChannelUnavailableException>(() => authority.Capture(old, "room-a"));
        Assert.Throws<ControlChannelUnavailableException>(() => authority.Capture(old, "room-b"));
        // Only a fresh, completed hello/join publication can grant authority again.
        var successor = new object();
        authority.Publish(successor, "room-a");
        Assert.Throws<ControlChannelUnavailableException>(() => authority.Capture(old, "room-a"));
    }
}
