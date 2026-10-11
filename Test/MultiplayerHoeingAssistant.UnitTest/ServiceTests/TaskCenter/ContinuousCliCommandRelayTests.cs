using Mistletoe.Shared;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class ContinuousCliCommandRelayTests
{
    private static readonly object EnvGate = new();

    [Theory]
    [InlineData("夜间计划")]
    [InlineData(null)]
    public void PublishThenConsume_UsesSameExplicitIsolatedRoot(string? planName)
    {
        lock (EnvGate)
        {
            var previous = Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT");
            var root = Path.Combine(Path.GetTempPath(), "continuous-cli-relay-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("NEXUSBGI_DATA_ROOT", root);
                Assert.True(ContinuousCliCommandRelay.Publish(planName, out var error), error);
                Assert.True(ContinuousCliCommandRelay.TryConsume(out var command));
                Assert.NotNull(command);
                Assert.Equal(planName, command!.PlanName);
                Assert.Equal(Environment.ProcessId, command.SourceProcessId);
                Assert.False(ContinuousCliCommandRelay.TryConsume(out _));
            }
            finally
            {
                Environment.SetEnvironmentVariable("NEXUSBGI_DATA_ROOT", previous);
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch { }
            }
        }
    }
}
