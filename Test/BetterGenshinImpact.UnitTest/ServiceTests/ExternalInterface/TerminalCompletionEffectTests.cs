using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;

namespace BetterGenshinImpact.UnitTest.ServiceTests.ExternalInterface;

public class TerminalCompletionEffectTests
{
    [Theory]
    [InlineData("closeGame", "confirmed:closeGame", true)]
    [InlineData("closeGame", "executed:closeGame", false)]
    [InlineData("closeGame", null, false)]
    [InlineData("closeSoftware", "confirmed:closeGame", false)]
    [InlineData("closeGameAndSoftware", "confirmed:closeGame", false)]
    [InlineData("shutdown", "confirmed:closeGame", false)]
    public void DeliveryTerminal_OnlyMatchingIndependentGameEffectCanSucceed(string action, string? detail, bool confirmed)
    {
        var registry = new JobRegistry(startHeartbeatTimer: false);
        var job = registry.Submit(JobKind.Terminal, action, JobSource.Ext).Job;
        registry.TryMarkRunning(job.JobId);
        TerminalCompletionEffect.Publish(registry, job.JobId, action, detail);
        Assert.Equal(confirmed ? JobState.Succeeded : JobState.ResultUnknown, job.State);
        Assert.False(job.ExitConfirmed); // Effect confirmation never substitutes for root/executor cleanup.
    }

    [Fact]
    public void DeliveryTerminal_IncompleteEnumerationCannotConfirmGameExit()
    {
        Assert.Null(TerminalCompletionEffect.ProbeGameExited(() => throw new UnauthorizedAccessException()));
        Assert.False(TerminalCompletionEffect.ProbeGameExited(() => true));
        Assert.True(TerminalCompletionEffect.ProbeGameExited(() => false));
    }
}
