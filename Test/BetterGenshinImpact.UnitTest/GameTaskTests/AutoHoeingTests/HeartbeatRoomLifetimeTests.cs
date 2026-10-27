#nullable enable
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>2026-10-27：旧房间关闭后心跳取消被累计30秒，误停下一世界准入。</summary>
public sealed class HeartbeatRoomLifetimeTests
{
    private static CoordinatorClient Client() => new() { _testIsConnectedOverride = true };
    private static GatewayEnvelope Response(GatewayEnvelope request) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(new { success = true })
    };
    private static void AcceptRoom(CoordinatorClient client)
        => client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, token) =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Response(request)); };
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void ExpireFailureWindow(WorldStateMonitor monitor)
    {
        Set(monitor, "_consecutiveHeartbeatFailures", 2);
        Set(monitor, "_firstHeartbeatFailureTime", DateTime.UtcNow.AddSeconds(-35));
    }

    [Theory]
    [InlineData("close")]
    [InlineData("leave")]
    [InlineData("broadcast")]
    public async Task EndedRoomDoesNotSendOrCountHeartbeatsAndNextWorldCanJoin(string end)
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        ExpireFailureWindow(monitor);
        if (end == "close") await client.CloseRoomAsync();
        else if (end == "leave") await client.LeaveRoomAsync();
        else client.DispatchEvt(new()
        {
            Type = GatewayProtocol.MessageTypes.Event,
            Name = GatewayProtocol.Events.RoomClosed, RoomCode = "old",
            Payload = GatewayEnvelope.ToPayload(new { reason = "normal-world-switch" })
        });
        Assert.Equal(0, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        var sends = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, token) =>
        { sends++; token.ThrowIfCancellationRequested(); return Task.FromResult(Response(request)); };
        for (var tick = 0; tick < 8; tick++) await client.SendHeartbeatAsync();
        Assert.Equal(0, sends);
        Assert.Equal(0, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        Assert.True(await client.JoinRoomAsync("next", "name", "uid"));
        await client.SendHeartbeatAsync();
        Assert.True(client.IsInRoom);
        Assert.Equal(2, sends); // 新房间加入和新房间心跳。
    }

    [Fact]
    public async Task NormalWorldSwitchCanWaitPastThirtySecondsWithoutStopping()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        monitor.OnExitConfirmed += (_, _) => { stop.Cancel(); return Task.CompletedTask; };
        monitor.BeginRoundSwitch(180);
        await client.CloseRoomAsync();
        // 实际跨过生产30秒窗口；不启动截图线程，也不操作游戏。
        for (var tick = 0; tick < 8; tick++)
        {
            if (tick > 0) await Task.Delay(TimeSpan.FromSeconds(5));
            await client.SendHeartbeatAsync();
        }
        Assert.False(stop.IsCancellationRequested);
        Assert.True(await client.JoinRoomAsync("next", "name", "uid", stop.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOldHeartbeatCannotChangeNewRoomFailures(bool oldSuccess)
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHeartbeat && request.RoomCode == "old")
            {
                entered.SetResult(true);
                await release.Task;
                if (!oldSuccess) throw new InvalidOperationException("late old transport failure");
            }
            if (request.Name == GatewayProtocol.Names.SessionHeartbeat && request.RoomCode == "next")
                throw new InvalidOperationException("current transport failure");
            return Response(request);
        };
        var old = client.SendHeartbeatAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.CloseRoomAsync();
        Assert.True(await client.JoinRoomAsync("next", "name", "uid"));
        await client.SendHeartbeatAsync();
        Assert.Equal(1, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        release.SetResult(true);
        await old.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        Assert.True(client.IsInRoom);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentRoomTransportFailureAndRpcTimeoutStillStop(bool timeout)
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        var exited = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.OnExitConfirmed += (_, reason) => { exited.TrySetResult(reason); return Task.CompletedTask; };
        monitor.BeginRoundSwitch(180); // 不能简单以轮换为由屏蔽真实故障。
        client._testAuxiliaryBudget = TimeSpan.FromMilliseconds(30);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, _, token) =>
        {
            if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("network failure");
        };
        await client.SendHeartbeatAsync();
        Assert.Equal(1, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        Assert.False(exited.Task.IsCompleted);
        ExpireFailureWindow(monitor);
        await client.SendHeartbeatAsync();
        Assert.Contains("心跳", await exited.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        AcceptRoom(client);
        Assert.False(await client.JoinRoomAsync("forbidden", "name", "uid"));
    }

    [Fact]
    public async Task CurrentSuccessClearsFailures()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        ExpireFailureWindow(monitor);
        await client.SendHeartbeatAsync();
        Assert.Equal(0, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
    }

    [Fact]
    public async Task ManualStopCancelsInFlightHeartbeatAndCannotRejoin()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        await using var monitor = new WorldStateMonitor(client, "uid");
        client.WorldStateMonitor = monitor;
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        { entered.SetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(request); };
        var heartbeat = client.SendHeartbeatAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await heartbeat.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Get<int>(monitor, "_consecutiveHeartbeatFailures"));
        AcceptRoom(client);
        Assert.False(await client.JoinRoomAsync("forbidden", "name", "uid"));
    }

    [Fact]
    public async Task DelayedExitForClosedOrReplacedRoomCannotRevokeNextRoom()
    {
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("old", "name", "uid"));
        var epoch = client.CurrentRoomEpoch!.Value;
        await client.CloseRoomAsync();
        Assert.False(client.TryConfirmHeartbeatExit(epoch));
        Assert.True(await client.JoinRoomAsync("next", "name", "uid"));
        Assert.False(client.TryConfirmHeartbeatExit(epoch));
        await client.WaitForBusinessReadyAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorldExitWithMissingOrClearedCoordinatorStillCancels(bool clearOnCancel)
    {
        var task = new AutoHoeingTask();
        using var stop = new CancellationTokenSource();
        Set(task, "_linkedStopCts", stop);
        await using var client = Client();
        AcceptRoom(client);
        Assert.True(await client.JoinRoomAsync("room", "name", "uid"));
        await using var coordinator = new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig());
        if (clearOnCancel) Set(task, "_multiplayerCoordinator", coordinator);
        using var registration = stop.Token.Register(() => Set(task, "_multiplayerCoordinator", null!));
        await task.HandleWorldExitConfirmedAsync(false, "test-real-failure");
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal("test-real-failure", Get<string>(task, "_stopReason"));
        Assert.True(Get<bool>(task, "_sessionTerminated"));
        if (clearOnCancel) Assert.True(coordinator.IsExitTriggered);
    }
}
