using System.Text.Json;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class MainViewModelTaskStatusParsingTests
{
    [Theory]
    [InlineData("{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900},\"stateRevision\":3}", true, true)]
    [InlineData("{\"running\":false,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900},\"stateRevision\":3}", false, true)]
    [InlineData("{}", false, false)]
    [InlineData("{\"running\":\"false\",\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900}}", false, false)]
    [InlineData("{\"running\":false}", false, false)]
    public void ParseTaskStatusData_SeparatesIdleFromUnavailable(
        string json, bool expectedRunning, bool expectedAvailable)
    {
        using var document = JsonDocument.Parse(json);

        var result = MainViewModel.ParseTaskStatusData(document.RootElement);

        Assert.Equal(expectedRunning, result.BgiRunning);
        Assert.Equal(expectedAvailable, result.TaskStatusAvailable);
        if (expectedAvailable)
        {
            Assert.Equal("9:900", result.TaskStatusBgiEpoch);
            Assert.Equal(3L, result.StateRevision);
        }
    }
}
