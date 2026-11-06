using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RegisterSetOneTests
{
    private static RegisterBankSnapshot Snapshot(int? one = null) => new()
    {
        Available = true,
        Entries = Enumerable.Range(1, RegisterBank.Count)
            .Select(n => new RegisterEntry { Number = n, Value = n == one ? 1 : 0 }).ToList()
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnknownReplyDoesNotInventAOneOrClaimSuccess(bool unknown)
    {
        var calls = 0;
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), setOne: _ =>
        {
            calls++;
            return unknown
                ? Task.FromResult(new RegisterBankSnapshot { Error = "执行端状态未知" })
                : Task.FromException<RegisterBankSnapshot>(new InvalidOperationException("请求结果未确认"));
        });
        bank.Apply(Snapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.SetOneAsync(4));
        Assert.Equal(1, calls);
        Assert.False(bank.IsBusy);
        Assert.All(bank.Cells, cell => Assert.Equal(unknown ? "?" : "0", cell.ValueText));
        Assert.Contains("归一未确认", bank.SetOneStatus);
        Assert.DoesNotContain("已归一", bank.SetOneStatus);
        Assert.Equal(!unknown, bank.CanSetOne);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(11)]
    public async Task InvalidNumberIsRejectedBeforeDispatch(int number)
    {
        var calls = 0;
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), setOne: _ =>
        { calls++; return Task.FromResult(Snapshot()); });
        bank.Apply(Snapshot());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bank.SetOneAsync(number));
        Assert.Equal(0, calls);
        Assert.False(bank.IsBusy);
        Assert.All(bank.Cells, cell => Assert.Equal("0", cell.ValueText));
    }

    [Fact]
    public async Task MissingCapabilityOrUnavailableStateIsRejectedBeforeDispatch()
    {
        var unsupported = new RegisterBankViewModel(_ => throw new InvalidOperationException());
        unsupported.Apply(Snapshot());
        Assert.False(unsupported.CanSetOne);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unsupported.SetOneAsync(1));
        var calls = 0;
        var disconnected = new RegisterBankViewModel(_ => throw new InvalidOperationException(), setOne: _ =>
        { calls++; return Task.FromResult(Snapshot(1)); });
        Assert.False(disconnected.CanSetOne);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disconnected.SetOneAsync(1));
        disconnected.Apply(Snapshot());
        Assert.True(disconnected.CanSetOne);
        disconnected.Apply(null);
        Assert.False(disconnected.CanSetOne);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disconnected.SetOneAsync(1));
        Assert.Equal(0, calls);
        Assert.False(disconnected.IsBusy);
        Assert.All(disconnected.Cells, cell => Assert.Equal("?", cell.ValueText));
    }

    [Fact]
    public async Task InFlightSetOneBlocksSetResetAndSaveUntilEndpointAcknowledges()
    {
        var pending = new TaskCompletionSource<RegisterBankSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setCalls = new List<int>(); var resetCalls = 0; var saveCalls = 0;
        var bank = new RegisterBankViewModel(_ =>
        { saveCalls++; return Task.FromResult(Snapshot()); }, _ =>
        { resetCalls++; return Task.FromResult(Snapshot()); }, number =>
        { setCalls.Add(number); return pending.Task; });
        bank.Apply(Snapshot());
        var first = bank.SetOneAsync(4);
        Assert.True(bank.IsBusy);
        Assert.False(bank.CanSetOne); Assert.False(bank.CanReset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.SetOneAsync(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.ResetAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bank.SaveAsync(new RegisterConfigurationChange { Number = 1 }));
        Assert.Equal(new[] { 4 }, setCalls);
        Assert.Equal(0, resetCalls); Assert.Equal(0, saveCalls);
        Assert.All(bank.Cells, cell => Assert.Equal("0", cell.ValueText));
        pending.SetResult(Snapshot(4));
        await first;
        Assert.False(bank.IsBusy);
        Assert.True(bank.CanSetOne); Assert.True(bank.CanReset);
        Assert.Equal("1", bank.Cells[3].ValueText);
        Assert.All(bank.Cells.Where(cell => cell.Entry.Number != 4), cell => Assert.Equal("0", cell.ValueText));
        Assert.Equal("R4 已归一。", bank.SetOneStatus);
    }
}
