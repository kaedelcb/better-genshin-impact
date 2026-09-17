using MultiplayerHoeingAssistant.Services;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedBatchOutcomeTests
{
    [Theory]
    [InlineData("running", false, null, false, "")]
    [InlineData("pending", false, null, true, "")]
    [InlineData("completed", false, null, false, "succeeded")]
    [InlineData("completed", false, null, true, "succeeded")]
    [InlineData("completed", true, null, false, "cancelled")]
    [InlineData("completed", true, null, true, "stopped")]
    [InlineData("queueCancelled", false, null, false, "cancelled")]
    [InlineData("queueCancelled", false, null, true, "stopped")]
    [InlineData("failed", false, "hoeing_incomplete", false, "failed")]
    [InlineData("failed", false, "hoeing_incomplete", true, "failed")]
    [InlineData("failed", false, "task_cancelled", true, "stopped")]
    [InlineData("failed", false, "task_cancelled", false, "unknown")]
    [InlineData("failed", false, "task_start_failed", true, "unknown")]
    [InlineData("not_found", false, null, true, "unknown")]
    [InlineData("future_status", false, null, false, "unknown")]
    public void ClassifiesOnlyConfirmedTerminals(string status, bool cancelled, string? error, bool stopping, string expected)
        => Assert.Equal(expected, CoordinatedBatchOutcome.Classify(new BgiTaskQueueStatus
        { Status = status, Cancelled = cancelled, ErrorCode = error }, stopping));

    [Fact]
    public void MissingResponseIsUnknown() => Assert.Equal("unknown", CoordinatedBatchOutcome.Classify(null, true));
}
