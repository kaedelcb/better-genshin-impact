using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.OneDragon;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class UidGameEntryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlreadyInGame_ContinuesOriginalReturnToMainUiWithoutWaiting(bool atMainUi)
    {
        var screen = atMainUi ? UidGameEntry.Screen.MainUi : UidGameEntry.Screen.OtherUi;
        var reads = 0;
        var waits = 0;
        var error = await UidGameEntry.EnterIfNeededAsync(
            () => { reads++; return screen; }, CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; });

        Assert.Null(error);
        Assert.Equal(1, reads);
        Assert.Equal(0, waits);
    }

    [Fact]
    public async Task EntryButtonMissed_RetriesAndWaitsThroughLoadingUntilMainUi()
    {
        var screens = new Queue<UidGameEntry.Screen>([
            UidGameEntry.Screen.EntryHandled,
            UidGameEntry.Screen.EntryHandled,
            UidGameEntry.Screen.OtherUi,
            UidGameEntry.Screen.OtherUi,
            UidGameEntry.Screen.MainUi,
        ]);
        var steps = new List<string>();
        var error = await UidGameEntry.EnterIfNeededAsync(
            () =>
            {
                var screen = screens.Dequeue();
                steps.Add(screen.ToString());
                return screen;
            }, CancellationToken.None,
            (duration, _) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(1), duration);
                steps.Add("wait");
                return Task.CompletedTask;
            }, retryTimes: 10);

        Assert.Null(error);
        Assert.Empty(screens);
        Assert.Equal([
            "EntryHandled", "wait", "EntryHandled", "wait", "OtherUi",
            "wait", "OtherUi", "wait", "MainUi",
        ], steps);
    }

    [Fact]
    public async Task EntryThenReward_ContinuesWaitingUntilMainUi()
    {
        var screens = new Queue<UidGameEntry.Screen>([
            UidGameEntry.Screen.EntryHandled,
            UidGameEntry.Screen.OtherUi,
            UidGameEntry.Screen.EntryHandled,
            UidGameEntry.Screen.EntryHandled,
            UidGameEntry.Screen.MainUi,
        ]);
        var error = await UidGameEntry.EnterIfNeededAsync(
            () => screens.Dequeue(), CancellationToken.None,
            (_, _) => Task.CompletedTask, retryTimes: 10);

        Assert.Null(error);
        Assert.Empty(screens);
    }

    [Fact]
    public async Task EntryButtonDisappeared_UnknownLoadingTimesOutWithoutClaimingSuccess()
    {
        var reads = 0;
        var error = await UidGameEntry.EnterIfNeededAsync(
            () => ++reads == 1 ? UidGameEntry.Screen.EntryHandled : UidGameEntry.Screen.OtherUi,
            CancellationToken.None, (_, _) => Task.CompletedTask, retryTimes: 3);

        Assert.NotNull(error);
        Assert.Contains("超时", error);
        Assert.True(reads > 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusLost_StopsWithoutAnotherScreenAction(bool afterEntry)
    {
        var reads = 0;
        var waits = 0;
        var error = await UidGameEntry.EnterIfNeededAsync(
            () =>
            {
                reads++;
                return afterEntry && reads == 1
                    ? UidGameEntry.Screen.EntryHandled
                    : UidGameEntry.Screen.FocusLost;
            },
            CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; }, retryTimes: 10);

        Assert.NotNull(error);
        Assert.Contains("焦点", error);
        Assert.Equal(afterEntry ? 2 : 1, reads);
        Assert.Equal(afterEntry ? 1 : 0, waits);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotReadOrHandleScreen()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UidGameEntry.EnterIfNeededAsync(
            () => { reads++; return UidGameEntry.Screen.EntryHandled; }, cancellation.Token,
            (_, _) => Task.CompletedTask));

        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledWhileWaiting_DoesNotHandleAnotherScreen(bool delayObservesCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UidGameEntry.EnterIfNeededAsync(
            () => { reads++; return UidGameEntry.Screen.EntryHandled; }, cancellation.Token,
            (_, token) =>
            {
                cancellation.Cancel();
                if (delayObservesCancellation) token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }));

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task EntryPreparationTimedOut_AccountSequenceNeverSwitchesAccount()
    {
        var verifies = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            async token =>
            {
                verifies++;
                var reads = 0;
                var reason = await UidGameEntry.EnterIfNeededAsync(
                    () => ++reads == 1 ? UidGameEntry.Screen.EntryHandled : UidGameEntry.Screen.OtherUi,
                    token, (_, _) => Task.CompletedTask, retryTimes: 2);
                return new OneDragonAccountCapability.UidVerifyResult(
                    OneDragonAccountCapability.UidVerifyStatus.ReadFailed, reason);
            },
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Contains("超时", error.Message);
        Assert.Equal(3, verifies);
        Assert.Equal(0, switches);
    }
}
