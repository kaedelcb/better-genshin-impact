using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class ManualStartupTests
{
    [Theory]
    [InlineData("--manual-start")]
    [InlineData("--MANUAL-START")]
    public void ExplicitManualLaunchSuppressesAutomaticStartup(string argument)
    {
        var intent = StartupLaunchIntent.FromArguments(new[] { argument });
        Assert.True(intent.ManualStart);
        Assert.False(intent.AllowsAutomaticStartup);
    }

    [Fact]
    public void ExistingNoAutoLaunchSwitchKeepsItsSeparateMeaning()
    {
        var intent = StartupLaunchIntent.FromArguments(new[] { "--no-auto-launch" });
        Assert.False(intent.ManualStart);
        Assert.True(intent.AllowsAutomaticStartup);
        Assert.False(StartupLaunchIntent.FromArguments(new[] { "--no-auto-launch", "--manual-start" }).AllowsAutomaticStartup);
    }

    [Fact]
    public void OrdinaryLaunchAndAnUnrelatedArgumentRetainExistingStartupBehavior()
    {
        Assert.True(StartupLaunchIntent.FromArguments(Array.Empty<string>()).AllowsAutomaticStartup);
        Assert.True(StartupLaunchIntent.FromArguments(new[] { "--manual-start-later" }).AllowsAutomaticStartup);
        Assert.True(StartupLaunchIntent.FromArguments(new[] { "--minimized" }).AllowsAutomaticStartup);
    }
}
