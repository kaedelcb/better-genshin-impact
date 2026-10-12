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
        Assert.Equal(3, reads);
        Assert.Equal(0, switches);
    }

    private static OneDragonAccountCapability.UidVerifyResult Verify(OneDragonAccountCapability.UidVerifyStatus status)
        => new(status, null);
}
