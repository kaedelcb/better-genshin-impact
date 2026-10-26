using System.Reflection;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class RerunTeleportProtectionTests
{
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorldStateMonitor Monitor() => new(new CoordinatorClient(), "test-player");
    private static void Set(WorldStateMonitor monitor, string name, object value)
        => typeof(WorldStateMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, value);
    private static T Get<T>(WorldStateMonitor monitor, string name)
        => (T)typeof(WorldStateMonitor).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor)!;

    [Fact]
    public async Task OpenMapThroughRecovery_IsProtected_AndOldObservationCannotExitAfterwards()
    {
        await using var monitor = Monitor();
        var oldObservation = monitor.VisualObservationVersion;
        var actions = 0;
        await monitor.RunWithTeleportSuppressionAsync(async ct =>
        {
            Assert.True(Get<bool>(monitor, "_isTeleportSuppressed"));
            Assert.False(await monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, () =>
            { actions++; return Task.CompletedTask; }));
            monitor.RefreshTeleportSuppression(); // 长传送重试仍使用既有刷新入口。
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Assert.True(Get<bool>(monitor, "_isTeleportSuppressed"));
        }, CancellationToken.None);
        Assert.False(Get<bool>(monitor, "_isTeleportSuppressed"));
        Assert.False(await monitor.TryRunVisualActionAsync(oldObservation, () =>
        { actions++; return Task.CompletedTask; }));
        Assert.True(await monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, () =>
        { actions++; return Task.CompletedTask; }));
        Assert.Equal(1, actions);
    }

    [Fact]
    public async Task InFlightReturnToMainUi_FinishesBeforeMapOperationStarts()
    {
        await using var monitor = Monitor();
        var recovering = Signal(); var release = Signal(); var mapStarted = false;
        var recovery = monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, async () =>
        { recovering.SetResult(true); await release.Task; });
        await recovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var teleport = monitor.RunWithTeleportSuppressionAsync(ct =>
        { mapStarted = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(teleport.IsCompleted);
        Assert.False(mapStarted);
        release.SetResult(true);
        await Task.WhenAll(recovery, teleport).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(mapStarted);
    }

    [Fact]
    public async Task CancelWhileWaitingForRecovery_DoesNotOpenMapOrLeaveProtection()
    {
        await using var monitor = Monitor();
        var release = Signal(); var entered = Signal(); var mapStarted = false;
        var recovery = monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, async () =>
        { entered.SetResult(true); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var teleport = monitor.RunWithTeleportSuppressionAsync(ct =>
        { mapStarted = true; return Task.CompletedTask; }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teleport);
        Assert.False(mapStarted);
        Assert.False(Get<bool>(monitor, "_isTeleportSuppressed"));
        release.SetResult(true);
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedTeleport_ReleasesProtection_AndDoesNotBecomeSuccess()
    {
        await using var monitor = Monitor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.RunWithTeleportSuppressionAsync(
            ct => throw new InvalidOperationException("teleport failed"), CancellationToken.None));
        Assert.False(Get<bool>(monitor, "_isTeleportSuppressed"));
        Assert.True(await monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, () => Task.CompletedTask));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserOrMonitorStop_CancelsTeleport_AndReleasesProtection(bool monitorStop)
    {
        await using var monitor = Monitor();
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var teleport = monitor.RunWithTeleportSuppressionAsync(async ct =>
        { entered.SetResult(true); await Task.Delay(Timeout.Infinite, ct); }, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (monitorStop) monitor.Stop(); else stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teleport.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Get<bool>(monitor, "_isTeleportSuppressed"));
    }

    [Fact]
    public async Task ProtectionClearsVisualHistory_ButPreservesHeartbeatFailures()
    {
        await using var monitor = Monitor();
        Set(monitor, "_connectedButNotInGame", 6);
        Set(monitor, "_visualMismatchInProgress", true);
        Set(monitor, "_consecutiveHeartbeatFailures", 2);
        var heartbeatStart = DateTime.UtcNow.AddSeconds(-20);
        Set(monitor, "_firstHeartbeatFailureTime", heartbeatStart);
        await monitor.RunWithTeleportSuppressionAsync(ct =>
        {
            Assert.Equal(0, Get<int>(monitor, "_connectedButNotInGame"));
            Assert.False(Get<bool>(monitor, "_visualMismatchInProgress"));
            Assert.Equal(2, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(2, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        Assert.Equal(heartbeatStart, Get<DateTime>(monitor, "_firstHeartbeatFailureTime"));
    }

    [Fact]
    public async Task RealHeartbeatFailure_StillConfirmsExitDuringTeleport()
    {
        await using var monitor = Monitor();
        var exited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.OnExitConfirmed += (_, reason) => { exited.TrySetResult(reason); return Task.CompletedTask; };
        var entered = Signal();
        var teleport = monitor.RunWithTeleportSuppressionAsync(async ct =>
        { entered.SetResult(true); await Task.Delay(Timeout.Infinite, ct); }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Set(monitor, "_consecutiveHeartbeatFailures", 2);
        Set(monitor, "_firstHeartbeatFailureTime", DateTime.UtcNow.AddSeconds(-31));
        monitor.NotifyHeartbeatFailure();
        Assert.Contains("心跳", await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => teleport.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExistingRouteProtection_StillBlocksVisualActions()
    {
        await using var monitor = Monitor();
        monitor.BeginTeleportSuppression();
        var observation = monitor.VisualObservationVersion;
        Assert.False(await monitor.TryRunVisualActionAsync(observation, () => Task.CompletedTask));
        monitor.EndTeleportSuppression();
        Assert.False(await monitor.TryRunVisualActionAsync(observation, () => Task.CompletedTask));
        Assert.True(await monitor.TryRunVisualActionAsync(monitor.VisualObservationVersion, () => Task.CompletedTask));
    }
}
