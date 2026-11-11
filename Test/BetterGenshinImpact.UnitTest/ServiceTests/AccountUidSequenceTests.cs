using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.OneDragon;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class AccountUidSequenceTests
{
    [Fact]
    public async Task MatchingCurrentUid_DoesNotSwitchOrVerifyAgain()
    {
        var reads = 0;
        var switches = 0;
        var switched = await AccountUidSequence.EnsureAsync(
            _ => { reads++; return Task.FromResult(Verify(OneDragonAccountCapability.UidVerifyStatus.Matched)); },
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None);

        Assert.False(switched);
        Assert.Equal(1, reads);
        Assert.Equal(0, switches);
    }

    [Fact]
    public async Task Mismatch_SwitchesAfterLegacyRetries_ThenStopsAtFirstMatch()
    {
        var steps = new List<string>();
        var switched = await AccountUidSequence.EnsureAsync(
            _ =>
            {
                steps.Add("verify");
                return Task.FromResult(Verify(steps.Count == 5
                    ? OneDragonAccountCapability.UidVerifyStatus.Matched
                    : OneDragonAccountCapability.UidVerifyStatus.Mismatch));
            },
            (n, _) => { steps.Add("switch-" + n); return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(switched);
        Assert.Equal(["verify", "verify", "verify", "switch-1", "verify"], steps);
    }

    [Fact]
    public async Task StillMismatched_StopsAfterTwoSwitches()
    {
        var reads = 0;
        var switches = 0;
        await Assert.ThrowsAsync<AccountMismatchException>(() =>
            AccountUidSequence.EnsureAsync(
                _ => { reads++; return Task.FromResult(Verify(OneDragonAccountCapability.UidVerifyStatus.Mismatch)); },
                (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(9, reads);
        Assert.Equal(2, switches);
    }

    [Fact]
    public async Task UnconfirmedSwitch_StopsWithoutAnotherAttempt()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() =>
            AccountUidSequence.EnsureAsync(
                _ => { reads++; return Task.FromResult(Verify(OneDragonAccountCapability.UidVerifyStatus.Mismatch)); },
                (_, _) => { switches++; return Task.FromResult(false); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Equal(3, reads);
        Assert.Equal(1, switches);
    }

    [Fact]
    public async Task UnreadableUid_NeverSwitchesAccount()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() =>
            AccountUidSequence.EnsureAsync(
                _ =>
                {
                    reads++;
                    return Task.FromResult(new OneDragonAccountCapability.UidVerifyResult(
                        OneDragonAccountCapability.UidVerifyStatus.ReadFailed, "游戏窗口未获得焦点"));
                },
                (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Equal(5, reads);
        Assert.Equal(0, switches);
    }

    [Fact]
    public async Task EveryPreparationStoppedBeforeInput_ReportsConfirmedNotStartedWithoutSwitching()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ =>
            {
                reads++;
                return Task.FromResult(Verify(OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted));
            },
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.AccountPreparationNotStarted, error.Code);
        Assert.Equal(3, reads);
        Assert.Equal(0, switches);
    }

    [Theory]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.Mismatch)]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.ReadFailed)]
    public async Task EarlierVerificationMayHaveOperatedGame_LaterFocusFailureRemainsUnknown(
        OneDragonAccountCapability.UidVerifyStatus firstStatus)
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 1 ? firstStatus :
                OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted)),
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Equal(firstStatus == OneDragonAccountCapability.UidVerifyStatus.ReadFailed ? 5 : 3, reads);
        Assert.Equal(0, switches);
    }

    [Fact]
    public async Task ReadFailureBetweenPreparationFailures_DoesNotForgetPossibleGameInput()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 2 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed :
                OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted)),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Equal(5, reads);
    }

    [Fact]
    public async Task FocusFailureAfterSwitch_RemainsUnknownWithoutAnotherSwitch()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads <= 3 ? OneDragonAccountCapability.UidVerifyStatus.Mismatch :
                OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted)),
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Equal(6, reads);
        Assert.Equal(1, switches);
    }

    [Fact]
    public async Task FocusRecoversOnNextVerification_ContinuesWithoutSwitching()
    {
        var reads = 0;
        var switched = await AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted :
                OneDragonAccountCapability.UidVerifyStatus.Matched)),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None);

        Assert.False(switched);
        Assert.Equal(2, reads);
    }

    // 常规五次核验后只允许补完末尾的连续不符证据，最多七次；
    // 读取/准备失败必须中断证据，不能跨失败累计三次不符来切号。
    [Fact]
    public async Task ReadFailureThenThreeMismatches_SwitchesAndReverifies()
    {
        var reads = 0;
        var steps = new List<string>();
        var switched = await AccountUidSequence.EnsureAsync(
            _ =>
            {
                steps.Add("verify");
                var status = ++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                    : reads == 5 ? OneDragonAccountCapability.UidVerifyStatus.Matched
                    : OneDragonAccountCapability.UidVerifyStatus.Mismatch;
                return Task.FromResult(Verify(status));
            },
            (n, _) => { steps.Add("switch-" + n); return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(switched);
        Assert.Equal(["verify", "verify", "verify", "verify", "switch-1", "verify"], steps);
    }

    [Fact]
    public async Task TwoMismatchesReadFailureTwoMismatches_DoesNotCombineStaleEvidence()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(new OneDragonAccountCapability.UidVerifyResult(
                ++reads is 3 or 6 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                    : OneDragonAccountCapability.UidVerifyStatus.Mismatch,
                reads == 3 ? "first-read-failure" : "last-mismatch")),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(6, reads);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Contains("first-read-failure", error.Message);
    }

    [Fact]
    public async Task ReadFailureThenPreparationFailure_ResetsMismatchStreak()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads switch
            {
                1 => OneDragonAccountCapability.UidVerifyStatus.ReadFailed,
                3 or 6 => OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted,
                _ => OneDragonAccountCapability.UidVerifyStatus.Mismatch,
            })),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(6, reads);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
    }

    [Fact]
    public async Task ReadFailureThenMatch_DoesNotConsumeRemainingBudgetOrSwitch()
    {
        var reads = 0;
        var switched = await AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : OneDragonAccountCapability.UidVerifyStatus.Matched)),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None);

        Assert.False(switched);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task InvalidExpectedAfterReadFailure_StopsImmediately()
    {
        var reads = 0;
        await Assert.ThrowsAsync<AccountMismatchException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : OneDragonAccountCapability.UidVerifyStatus.InvalidExpected)),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CancellationDuringExtendedBudget_StopsBeforeNextReadOrSwitch()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountUidSequence.EnsureAsync(
            _ =>
            {
                if (++reads == 4) cts.Cancel();
                return Task.FromResult(Verify(OneDragonAccountCapability.UidVerifyStatus.ReadFailed));
            },
            (_, _) => throw new InvalidOperationException("不应切号"), cts.Token));

        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task ReadFailureAfterSwitch_ExtendsOnlyCurrentRoundAndDoesNotSwitchAgain()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads <= 3 ? OneDragonAccountCapability.UidVerifyStatus.Mismatch
                : OneDragonAccountCapability.UidVerifyStatus.ReadFailed)),
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(8, reads);
        Assert.Equal(1, switches);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
    }

    [Fact]
    public async Task RepeatedReadFailures_PreservesFirstFailureReason()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(new OneDragonAccountCapability.UidVerifyResult(
                OneDragonAccountCapability.UidVerifyStatus.ReadFailed, ++reads == 1 ? "first-failure" : "later-failure")),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(5, reads);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Contains("first-failure", error.Message);
    }

    [Fact]
    public async Task TwoReadFailuresThenThreeMismatches_UsesFifthReadThenSwitches()
    {
        var reads = 0;
        var switches = 0;
        var switched = await AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads <= 2 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : reads == 6 ? OneDragonAccountCapability.UidVerifyStatus.Matched
                : OneDragonAccountCapability.UidVerifyStatus.Mismatch)),
            (_, _) => { Assert.Equal(5, reads); switches++; return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(switched);
        Assert.Equal(6, reads);
        Assert.Equal(1, switches);
    }

    [Fact]
    public async Task ExtendedRoundThenPreparationFailures_ResetBudgetAfterSwitch()
    {
        var reads = 0;
        var switches = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : reads <= 4 ? OneDragonAccountCapability.UidVerifyStatus.Mismatch
                : OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted)),
            (_, _) => { switches++; return Task.FromResult(true); }, CancellationToken.None));

        Assert.Equal(7, reads);
        Assert.Equal(1, switches);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
    }

    [Theory]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.Matched)]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.Mismatch)]
    public async Task CancelledVerificationResult_CannotSucceedOrTriggerSwitch(
        OneDragonAccountCapability.UidVerifyStatus lastStatus)
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AccountUidSequence.EnsureAsync(
            _ =>
            {
                if (++reads == 3) cts.Cancel();
                return Task.FromResult(Verify(reads == 3 ? lastStatus
                    : OneDragonAccountCapability.UidVerifyStatus.Mismatch));
            },
            (_, _) => throw new InvalidOperationException("不应切号"), cts.Token));

        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ReadFailureReason_IsNotHiddenByEarlierPreparationFailure()
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(new OneDragonAccountCapability.UidVerifyResult(
                ++reads == 1 ? OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted
                    : reads is 3 or 6 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                    : OneDragonAccountCapability.UidVerifyStatus.Mismatch,
                reads == 1 ? "preparation" : reads == 3 ? "read-failure" : "mismatch")),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(6, reads);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
        Assert.Contains("read-failure", error.Message);
    }

    // 用户日志：四次未读到 UID，第五次才读到不符。必须补齐连续证据后切号并复验，不能直接失败。
    [Theory]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.Mismatch, 8, 1)]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.Matched, 6, 0)]
    public async Task FourReadFailuresThenMismatch_CompletesConfirmation(
        OneDragonAccountCapability.UidVerifyStatus nextStatus, int expectedReads, int expectedSwitches)
    {
        var reads = 0;
        var switches = 0;
        var switched = await AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads <= 4 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : reads == 5 ? OneDragonAccountCapability.UidVerifyStatus.Mismatch
                : switches > 0 ? OneDragonAccountCapability.UidVerifyStatus.Matched : nextStatus)),
            (_, _) => { Assert.Equal(7, reads); switches++; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(expectedReads, reads);
        Assert.Equal(expectedSwitches, switches);
        Assert.Equal(expectedSwitches > 0, switched);
    }

    [Theory]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.ReadFailed)]
    [InlineData(OneDragonAccountCapability.UidVerifyStatus.PreparationNotStarted)]
    public async Task TailConfirmationInterrupted_StopsWithoutExtendingAgain(
        OneDragonAccountCapability.UidVerifyStatus interrupted)
    {
        var reads = 0;
        var error = await Assert.ThrowsAsync<PrerequisiteFailedException>(() => AccountUidSequence.EnsureAsync(
            _ => Task.FromResult(Verify(++reads <= 4 ? OneDragonAccountCapability.UidVerifyStatus.ReadFailed
                : reads == 5 ? OneDragonAccountCapability.UidVerifyStatus.Mismatch : interrupted)),
            (_, _) => throw new InvalidOperationException("不应切号"), CancellationToken.None));

        Assert.Equal(6, reads);
        Assert.Equal(JobErrorCodes.PrerequisiteOutcomeUnknown, error.Code);
    }

    private static OneDragonAccountCapability.UidVerifyResult Verify(OneDragonAccountCapability.UidVerifyStatus status)
        => new(status, null);
}
