using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class WorkflowTimeWindowClockTests
{
    [Fact]
    public void RemoteClock_PreservesExecutorZone_AndCrossesWindowBoundaryBetweenPolls()
    {
        var clock = new WorkflowTimeWindowClock();
        var executor = new DateTimeOffset(2026, 10, 25, 8, 59, 59, TimeSpan.FromHours(8));
        var monitor = new DateTimeOffset(2026, 10, 24, 18, 0, 0, TimeSpan.FromHours(-7));
        const long received = 100;
        clock.Synchronize(executor, received);
        var range = new WorkflowTimeWindow { Start = "08:00", End = "09:00" };
        Assert.True(clock.HasRemoteTime);
        Assert.True(WorkflowTimeWindowPolicy.Allows(range, clock.Current(monitor, received)));
        // A monitor clock adjustment cannot freeze or move the executor's projected time.
        var advanced = clock.Current(monitor.AddHours(-2), received + Stopwatch.Frequency);
        Assert.Equal(executor.AddSeconds(1), advanced);
        Assert.Equal(TimeSpan.FromHours(8), advanced.Offset);
        Assert.False(WorkflowTimeWindowPolicy.Allows(range, advanced));
    }

    [Fact]
    public void RemoteClock_NewPollCorrectsProjection_AndOldEndpointFallsBackToLocalTime()
    {
        var clock = new WorkflowTimeWindowClock();
        var local = DateTimeOffset.Now;
        Assert.Equal(local, clock.Current(local, 100));
        var executor = new DateTimeOffset(2026, 10, 25, 23, 59, 59, TimeSpan.FromHours(9));
        clock.Synchronize(executor, 100);
        Assert.Equal(executor.AddSeconds(2), clock.Current(local, 100 + 2 * Stopwatch.Frequency));
        var corrected = executor.AddMinutes(5);
        clock.Synchronize(corrected, 200);
        Assert.Equal(corrected, clock.Current(local, 200));
        var legacy = JsonSerializer.Deserialize<RemoteTaskListSnapshot>("{}")!;
        Assert.Null(legacy.ServerTime);
        clock.Synchronize(legacy.ServerTime, 300);
        Assert.False(clock.HasRemoteTime);
        Assert.Equal(local, clock.Current(local, 400));
    }

    [Fact]
    public void SnapshotClock_RoundTripsExecutorOffset()
    {
        var source = new RemoteTaskListSnapshot
        {
            ServerTime = new DateTimeOffset(2026, 10, 25, 22, 15, 0, TimeSpan.FromHours(5.5)),
        };
        var copy = JsonSerializer.Deserialize<RemoteTaskListSnapshot>(JsonSerializer.Serialize(source))!;
        Assert.Equal(source.ServerTime, copy.ServerTime);
        Assert.Equal(source.ServerTime.Value.Offset, copy.ServerTime!.Value.Offset);
    }
}
