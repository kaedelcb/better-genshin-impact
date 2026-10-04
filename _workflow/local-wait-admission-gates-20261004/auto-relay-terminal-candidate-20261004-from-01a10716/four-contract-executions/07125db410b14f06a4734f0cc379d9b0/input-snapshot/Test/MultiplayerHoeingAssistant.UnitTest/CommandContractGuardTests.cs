using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class CommandContractGuardTests
{
    [Theory]
    [InlineData("start_group")]
    [InlineData("start_oneclick")]
    [InlineData("start_bgi")]
    [InlineData("close_game")]
    [InlineData("set_task_enabled")]
    public async Task ExpiredRemoteCommandDoesNotTouchProcessOrTransport(string operation)
    {
        // Intentionally no monitor/client: any accidental descent into product actions fails.
        var executor = new CommandExecutor(null!, "unused", () => throw new Exception("transport must not be touched"));
        var result = await executor.ExecuteAsync(new RemoteCommand {
            Cmd = operation, ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Equal("failed", result.Status);
        Assert.Equal("request_expired", result.ErrorCode);
    }

    [Fact]
    public async Task RevisionBoundRequestCannotFallBackToLegacyTarget()
    {
        var executor = new CommandExecutor(null!, "unused");
        var result = await executor.ExecuteAsync(new RemoteCommand {
            Cmd = "start_group", Params = new() { ["groupName"] = "fixture", ["expectedConfigRevision"] = "rev" } });
        Assert.Equal("failed", result.Status);
        Assert.Equal("capability_required", result.ErrorCode);
    }
}
