using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class AutoStartupRegistrationTests
{
    [Theory]
    [InlineData(true, " --minimized --no-auto-launch")]
    [InlineData(false, " --no-auto-launch")]
    public void BuildCommand_UsesAbsolutePathAndExpectedArguments(bool minimized, string expectedArguments)
    {
        var relativePath = Path.Combine("bin", "MultiplayerHoeingAssistant.exe");

        var command = AutoStartupRegistration.BuildCommand(relativePath, minimized);

        Assert.Equal($"\"{Path.GetFullPath(relativePath)}\"{expectedArguments}", command);
    }

    [Fact]
    public void HistoricalValueNames_ContainsOnlyTheKnownLegacyAssistantValue()
    {
        Assert.Equal([AutoStartupRegistration.LegacyValueName], AutoStartupRegistration.HistoricalValueNames);
    }
}
