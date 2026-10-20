using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CommandExecutorTakeoverResetTests
{
    [Theory]
    [InlineData(true, "held-ticket", true)]
    [InlineData(false, "held-ticket", false)]
    [InlineData(true, "different-ticket", false)]
    public void InheritReceiptRequiresOriginalTicketAndConfirmedExit(bool exited, string ticket, bool accepted)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            receiptOnly = true, takeoverTicket = ticket, quiesceConfirmed = exited,
            expectedExecutionInstanceId = Guid.NewGuid().ToString("N"), expectedStateRevision = 2
        });
        Assert.Equal(accepted, CommandExecutor.IsQuiescentTakeoverReceipt(json, "held-ticket"));
        Assert.False(CommandExecutor.IsQuiescentTakeoverReceipt("{}", "held-ticket"));
        Assert.False(CommandExecutor.IsQuiescentTakeoverReceipt("{", "held-ticket"));
    }

    [Fact]
    public void EpochChangeIsTheOnlyKnownCrossProcessInvalidation()
    {
        var held = new BgiEpoch { ProcessId = 10, StartTicksUtc = 100 };
        var same = new BgiEpoch { ProcessId = 10, StartTicksUtc = 100 };
        var restarted = new BgiEpoch { ProcessId = 11, StartTicksUtc = 200 };

        Assert.False(CommandExecutor.IsBgiEpochChanged(held, same));
        Assert.True(CommandExecutor.IsBgiEpochChanged(held, restarted));
        Assert.False(CommandExecutor.IsBgiEpochChanged(held, null));
        Assert.False(CommandExecutor.IsBgiEpochChanged(null, restarted));
    }

    [Fact]
    public async Task ResetDropsTicketWhenTheBgiProcessIsConfirmedGone()
    {
        var executor = new CommandExecutor(null!, "unused")
        {
            BgiRunningProbeOverride = () => false
        };
        executor.SeedTakeoverStateForTest("old-ticket", new BgiEpoch { ProcessId = 10, StartTicksUtc = 100 });

        var result = await executor.ResetTakeoverStateAsync();

        Assert.Equal("success", result.Status);
        Assert.Equal("epoch_reset", result.ErrorCode);
        Assert.Null(executor.TakeoverTicketSnapshot);
    }
}
