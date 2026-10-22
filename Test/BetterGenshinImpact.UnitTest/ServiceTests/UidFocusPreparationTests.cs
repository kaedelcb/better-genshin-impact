using BetterGenshinImpact.Service.OneDragon;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class UidFocusPreparationTests
{
    [Fact]
    public async Task AlreadyFocused_DoesNotActivateOrWait()
    {
        var activations = 0;
        var waits = 0;
        var result = await UidFocusPreparation.WaitAsync(
            () => activations++, () => true, CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; });

        Assert.True(result.Focused);
        Assert.Equal(0, activations);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task FocusArrivesAfterStartupDelay_ContinuesOnlyAfterObservedFocus()
    {
        var activations = 0;
        var waits = 0;
        var result = await UidFocusPreparation.WaitAsync(
            () => activations++, () => waits >= 6, CancellationToken.None,
            (duration, _) =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(250), duration);
                waits++;
                return Task.CompletedTask;
            });

        Assert.True(result.Focused);
        Assert.Equal(6, waits);
        Assert.Equal(2, activations);
        Assert.Equal(activations, result.ActivationAttempts);
    }

    [Fact]
    public async Task OtherWindowKeepsFocus_StopsAtBoundWithoutClaimingSuccess()
    {
        var activations = 0;
        var waits = 0;
        var result = await UidFocusPreparation.WaitAsync(
            () => activations++, () => false, CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; });

        Assert.False(result.Focused);
        Assert.Equal(40, waits);
        Assert.Equal(10, activations);
        Assert.Equal(activations, result.ActivationAttempts);
    }

    [Fact]
    public async Task FocusArrivesAtLastObservation_IsAcceptedWithinBound()
    {
        var waits = 0;
        var result = await UidFocusPreparation.WaitAsync(
            () => { }, () => waits == 4, CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; }, pollTimes: 4);

        Assert.True(result.Focused);
        Assert.Equal(4, waits);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotInspectOrActivateWindow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var actions = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UidFocusPreparation.WaitAsync(
            () => actions++, () => { actions++; return false; }, cancellation.Token));

        Assert.Equal(0, actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledWhileWaiting_DoesNotInspectOrActivateAgain(bool delayObservesCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var activations = 0;
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UidFocusPreparation.WaitAsync(
            () => activations++, () => { reads++; return false; }, cancellation.Token,
            (_, token) =>
            {
                cancellation.Cancel();
                if (delayObservesCancellation) token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }));

        Assert.Equal(1, activations);
        Assert.Equal(1, reads);
    }
}
