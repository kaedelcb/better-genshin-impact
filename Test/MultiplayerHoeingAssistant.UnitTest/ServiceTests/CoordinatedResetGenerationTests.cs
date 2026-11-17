using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedResetGenerationTests
{
    [Theory]
    [InlineData("local-old", 11, "server-new", 12, 11, false)]
    [InlineData("local-new", 13, "server-old", 12, 13, true)]
    [InlineData("same", 12, "same", 12, 12, false)]
    [InlineData("same", 12, "same", 11, 12, true)]
    [InlineData("local", 12, "different", 12, 12, true)]
    [InlineData("local", 12, "different", 0, 12, true)]
    [InlineData("same", 12, "same", 0, 12, false)]
    [InlineData("local-old", 11, "", 12, 11, false)]
    [InlineData(null, 0, "server-old", 12, 13, true)]
    [InlineData(null, 0, "server-current", 13, 13, false)]
    public void ResetDistinguishesLaggingBatchFromSuccessor(string? localId, int localGeneration,
        string resetId, int resetGeneration, int lastAccepted, bool stale)
        => Assert.Equal(stale, MainViewModel.ResetTargetsSuccessor(localId, localGeneration,
            resetId, resetGeneration, lastAccepted));
}
