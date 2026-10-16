using System.Diagnostics;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedExitedOwnershipTests
{
    private static BgiEpoch DeadEpoch => new() { ProcessId = Environment.ProcessId,
        StartTicksUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks - 1 };

    [Fact]
    public async Task ProvenOldProcessExitClearsOnlyItsExactTicket()
    {
        var executor = new CommandExecutor(null!, "unused");
        var old = DeadEpoch;
        executor.SeedTakeoverStateForTest("old", old);
        Assert.True(await executor.AbandonExitedCoordinatedOwnershipAsync("old", old, default));
        Assert.Null(executor.TakeoverTicketSnapshot);
    }

    [Fact]
    public async Task LateOldCleanupDoesNotClearTheSuccessorTicket()
    {
        var executor = new CommandExecutor(null!, "unused");
        executor.SeedTakeoverStateForTest("new", new BgiEpoch { ProcessId = Environment.ProcessId,
            StartTicksUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks });
        Assert.True(await executor.AbandonExitedCoordinatedOwnershipAsync("old", DeadEpoch, default));
        Assert.Equal("new", executor.TakeoverTicketSnapshot);
    }

    [Fact]
    public async Task SameTicketWithDifferentStoredEpochIsNotDischarged()
    {
        var executor = new CommandExecutor(null!, "unused");
        executor.SeedTakeoverStateForTest("same", new BgiEpoch { ProcessId = 1, StartTicksUtc = 1 });
        Assert.False(await executor.AbandonExitedCoordinatedOwnershipAsync("same", DeadEpoch, default));
        Assert.Equal("same", executor.TakeoverTicketSnapshot);
    }

    [Fact]
    public async Task LivingOrUnknownOriginalProcessIsNeverAssumedGone()
    {
        var executor = new CommandExecutor(null!, "unused");
        var living = new BgiEpoch { ProcessId = Environment.ProcessId,
            StartTicksUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks };
        executor.SeedTakeoverStateForTest("held", living);
        Assert.True(CommandExecutor.ProbeOriginalBgiProcess(living));
        Assert.False(await executor.AbandonExitedCoordinatedOwnershipAsync("held", living, default));
        Assert.Null(CommandExecutor.ProbeOriginalBgiProcess(new BgiEpoch()));
        Assert.Equal("held", executor.TakeoverTicketSnapshot);
    }
}
