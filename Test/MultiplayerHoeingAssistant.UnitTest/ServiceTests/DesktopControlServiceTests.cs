using System.Collections.Concurrent;
using System.Threading.Channels;
using BgiCoordinatorServer.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// 所有画面和输入均为 fake。覆盖授权、帧与代次、不排队及 ACK/执行回执的交付边界。
public class DesktopControlServiceTests
{
    [Fact]
    public async Task PermissionDefaultsOffAndSnapshotCannotEnableIt()
    {
        using var f = new DesktopControlFixture();
        var copy = f.Settings.Current;
        copy.AllowDesktopControl = true;
        Assert.False(f.Settings.Current.AllowDesktopControl);
        var result = await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline);
        Assert.Equal("denied", result.Status);
        Assert.Equal(0, f.Sender.Calls);
        f.Allow();
        Assert.True(new DodocoSettingsService(f.Root).Current.AllowDesktopControl);
        Assert.Equal("executed", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        f.Settings.Update(x => x.AllowDesktopControl = false);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Theory]
    [InlineData("left", -0.01, 0.5, "")]
    [InlineData("right", 0.5, 1.01, "")]
    [InlineData("double", double.NaN, 0.5, "")]
    [InlineData("key", 0, 0, "Ctrl+R")]
    [InlineData("wheel", 0, 0, "")]
    public async Task InvalidCommandsNeverReachSender(string action, double x, double y, string key)
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var command = f.Command() with { Action = action, X = x, Y = y, Key = key };
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(command, f.Deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task DuplicateRequestAndSameFrameWithNewRequestNeverExecuteTwice()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var command = f.Command();
        Assert.Equal("executed", (await f.Control.ExecuteLocalAsync(command, f.Deadline)).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(command with { FrameId = f.Capture().FrameId }, f.Deadline)).Status);
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(command with { RequestId = Guid.NewGuid().ToString("N") }, f.Deadline)).Status);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task CancellationTombstoneRejectsInputArrivingLater()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var command = f.Command();
        f.Control.CancelLocal(command.RequestId);
        Assert.Equal("cancelled", (await f.Control.ExecuteLocalAsync(command, f.Deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    // 取消去重表不能在助手长期运行后永久耗尽；旧请求仍因帧过期而不能重放。
    [Fact]
    public async Task FinishedClaimsExpireWithoutRestart_AndOldFramesStayRejected()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var old = f.Command();
        f.Control.CancelLocal(old.RequestId);
        for (var i = 1; i < 8192; i++) f.Control.CancelLocal(Guid.NewGuid().ToString("N"));
        Assert.Equal("busy", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(old, f.Deadline)).Status);
        Assert.Equal("executed", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task GateRejectsConcurrentInputImmediatelyWithoutQueueing()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Sender.BeforeSend = (_, _) => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var first = f.Control.ExecuteLocalAsync(f.Command(), f.Deadline);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var second = f.Control.ExecuteLocalAsync(f.Command(), f.Deadline);
            Assert.True(second.IsCompleted);
            Assert.Equal("busy", (await second).Status);
        }
        finally { release.Set(); }
        Assert.Equal("executed", (await first).Status);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Fact]
    public async Task PermissionRevokedDuringGeometryValidationPreventsFirstInput()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.GeometryProbe = () => f.Settings.Update(x => x.AllowDesktopControl = false);
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task MonotonicDeadlineCannotBeExtendedByWallClockRollback()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var deadline = f.Deadline;
        f.GeometryProbe = () => { f.Clock.Advance(TimeSpan.FromSeconds(3)); f.Clock.WallOffset = TimeSpan.FromHours(-1); };
        Assert.Equal("expired", (await f.Control.ExecuteLocalAsync(f.Command(), deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task ExpiredCommandAndChangedLayoutAreRejected()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        Assert.Equal("expired", (await f.Control.ExecuteLocalAsync(f.Command(), f.Clock.GetUtcNow())).Status);
        var command = f.Command();
        f.Geometry = f.Geometry with { LayoutId = "changed-scale-or-display" };
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(command, f.Deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task ExpiredCaptureAndPermissionRevokedAtSenderBoundaryNeverSend()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var stale = f.Command();
        f.Clock.Advance(DesktopControlProtocol.FrameLifetime);
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(stale, f.Deadline)).Status);
        f.Sender.BeforeSend = (_, _) => f.Settings.Update(x => x.AllowDesktopControl = false);
        Assert.Equal("denied", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    // 真正进入目标事件→执行服务，不能只证明查看端按钮在两机钟差下可开启。
    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public async Task RemoteInputUsesCaptureRelativeBudget_WhenTargetWallClockDiffers(int offsetSeconds)
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Clock.WallOffset = TimeSpan.FromSeconds(offsetSeconds);
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        var command = f.RemoteCommand();
        var serverDeadline = f.Clock.GetUtcNow() - f.Clock.WallOffset + TimeSpan.FromSeconds(3);
        f.Client.Request(command, serverDeadline);
        Assert.Equal("executed", (await f.Client.NextReport()).Status);
        Assert.Equal(1, f.Sender.Calls);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public async Task DelayedRemotePacketExpires_EvenWithAnOffsetWallClock(int offsetSeconds)
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Clock.WallOffset = TimeSpan.FromSeconds(offsetSeconds);
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        var command = f.RemoteCommand();
        var serverDeadline = f.Clock.GetUtcNow() - f.Clock.WallOffset + TimeSpan.FromSeconds(3);
        f.Clock.Advance(TimeSpan.FromSeconds(4)); // 帧仍未到15秒，只有指令本身过期。
        f.Client.Request(command, serverDeadline);
        Assert.Equal("expired", (await f.Client.NextReport()).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Theory]
    [InlineData(2399, "executed")]
    [InlineData(2400, "expired")]
    public async Task RelativeBudgetIncludesUploadTransferAndExecutionDelay(int executionDelayMs, string expected)
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        var command = f.RemoteCommand();
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); // 捕获到服务器收到图片。
        f.Clock.Advance(TimeSpan.FromSeconds(2)); // 服务器收到图片到发控制指令。
        var serverDeadline = f.Deadline;
        f.Clock.Advance(TimeSpan.FromMilliseconds(400)); // 指令传输。
        f.Sender.BeforeSend = (_, _) => f.Clock.Advance(TimeSpan.FromMilliseconds(executionDelayMs));
        f.Client.Request(command, serverDeadline, frameAgeMilliseconds: 2000);
        Assert.Equal(expected, (await f.Client.NextReport()).Status);
        Assert.Equal(expected == "executed" ? 1 : 0, f.Sender.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3001)]
    public async Task InvalidRemoteLifetimeNeverExecutes(double lifetime)
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        f.Client.Request(f.RemoteCommand(), f.Deadline, lifetimeMilliseconds: lifetime);
        Assert.Equal("denied", (await f.Client.NextReport()).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task LocalAndRemoteRequestersShareTargetTwoSecondLimit()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        Assert.Equal("executed", (await f.Control.ExecuteLocalAsync(f.Command(), f.Deadline)).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Client.Request(f.RemoteCommand(), f.Deadline);
        Assert.Equal("busy", (await f.Client.NextReport()).Status);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Client.Request(f.RemoteCommand(), f.Deadline);
        Assert.Equal("executed", (await f.Client.NextReport()).Status);
        Assert.Equal(2, f.Sender.Calls);
    }

    [Fact]
    public async Task RemoteCancellationBeforeRequestedEventAlsoLeavesTombstone()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        var command = f.RemoteCommand();
        f.Client.Cancel(command.RequestId);
        f.Client.Request(command, f.Deadline);
        Assert.Equal("cancelled", (await f.Client.NextReport()).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task RemoteFrameFromPreviousConnectionEpochIsRejected()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        f.Client = new FakeDesktopControlClient(); f.Control.EnsureHooked();
        var command = f.RemoteCommand();
        f.Client.Epoch++;
        f.Control.EnsureHooked();
        f.Client.Request(command, f.Deadline);
        Assert.Equal("denied", (await f.Client.NextReport()).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task AcceptedAckDoesNotCompleteViewerUntilMatchingResult()
    {
        using var f = new DesktopControlFixture();
        f.Client = new FakeDesktopControlClient();
        var command = f.RemoteCommand();
        var waiting = f.Control.RequestAsync(command);
        Assert.Single(f.Client.Sent);
        Assert.False(waiting.IsCompleted);
        f.Client.Result(new() { RequestId = command.RequestId, TargetUid = "someone-else", Status = "executed" });
        Assert.False(waiting.IsCompleted);
        f.Client.Result(new() { RequestId = command.RequestId, TargetUid = command.TargetUid, Status = "executed" });
        Assert.Equal("executed", (await waiting).Status);
        Assert.Empty(f.Client.CancelledSessions);
        Assert.Equal("denied", (await f.Control.RequestAsync(command)).Status);
        Assert.Single(f.Client.Sent);
    }

    [Fact]
    public async Task ViewerTimesOutAsUnknownAndCancelsWithoutRetry()
    {
        using var f = new DesktopControlFixture(); f.Client = new FakeDesktopControlClient();
        var waiting = f.Control.RequestAsync(f.RemoteCommand());
        Assert.False(waiting.IsCompleted);
        f.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("unknown", (await waiting).Status);
        Assert.Single(f.Client.Sent);
        Assert.Single(f.Client.CancelledSessions);
    }

    [Fact]
    public async Task DisconnectImmediatelyClosesPendingViewerAndIgnoresLateResult()
    {
        using var f = new DesktopControlFixture(); f.Client = new FakeDesktopControlClient();
        var command = f.RemoteCommand();
        var waiting = f.Control.RequestAsync(command);
        f.Client.Ready = false;
        f.Client.RetireForReplacement();
        Assert.Equal("unknown", (await waiting).Status);
        f.Client.Result(new() { RequestId = command.RequestId, TargetUid = command.TargetUid, Status = "executed" });
        Assert.Single(f.Client.Sent);
    }

    [Fact]
    public async Task ReplacementAndRoomEpochClosePendingAndRejectOldEvents()
    {
        using var f = new DesktopControlFixture(); f.Client = new FakeDesktopControlClient();
        var old = f.Client;
        var command = f.RemoteCommand();
        var waiting = f.Control.RequestAsync(command);
        f.Client = new FakeDesktopControlClient { Room = "room-b" };
        f.Control.EnsureHooked();
        Assert.Equal("unknown", (await waiting).Status);
        old.Result(new() { RequestId = command.RequestId, TargetUid = command.TargetUid, Status = "executed" });
        var secondCommand = f.RemoteCommand();
        var next = f.Control.RequestAsync(secondCommand);
        f.Client.Result(new() { RequestId = secondCommand.RequestId, TargetUid = secondCommand.TargetUid, Status = "executed" }, "room-a");
        Assert.False(next.IsCompleted);
        f.Client.Result(new() { RequestId = secondCommand.RequestId, TargetUid = secondCommand.TargetUid, Status = "executed" });
        Assert.Equal("executed", (await next).Status);
    }
}

public class ScreenshotControlFrameTests
{
    [Fact]
    public void CaptureStillAddsHistoryAndRegistersItsFrameId()
    {
        using var f = new DesktopControlFixture();
        for (var i = 0; i < ScreenshotService.HistoryCapacity + 1; i++)
        {
            var frame = f.Capture();
            Assert.True(Guid.TryParseExact(frame.FrameId, "N", out _));
            Assert.True(f.Screenshots.TryGetControlFrame(frame.FrameId, out var geometry));
            Assert.Same(frame.Geometry, geometry);
        }
        Assert.Equal(ScreenshotService.HistoryCapacity, f.Screenshots.History.Count);
    }

    [Fact]
    public void RemoteAliasKeepsOriginalCaptureAgeAndCannotBeRevivedWithNewCapture()
    {
        using var f = new DesktopControlFixture();
        var frame = f.Capture();
        var alias = MemberScreenshotRelayService.CreateRequestId(Guid.NewGuid().ToString("N"));
        f.Clock.Advance(TimeSpan.FromSeconds(14));
        f.Screenshots.RememberControlFrame(alias, frame.Geometry!);
        Assert.True(f.Screenshots.TryGetControlFrame(alias, out _));
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(f.Screenshots.TryGetControlFrame(alias, out _));
        f.Screenshots.RememberControlFrame(alias, f.Capture().Geometry!);
        Assert.False(f.Screenshots.TryGetControlFrame(alias, out _));
    }

    [Fact]
    public async Task AllAliasesOfOneActualCaptureShareOneConsumption()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        var frame = f.Capture();
        var alias = MemberScreenshotRelayService.CreateRequestId(Guid.NewGuid().ToString("N"));
        f.Screenshots.RememberControlFrame(alias, frame.Geometry!);
        Assert.True(f.Screenshots.TryGetControlFrame(alias, out _));
        Assert.Equal("executed", (await f.Control.ExecuteLocalAsync(f.Command(frame), f.Deadline)).Status);
        Assert.False(f.Screenshots.TryGetControlFrame(alias, out _));
    }

    [Fact]
    public void EqualButUncapturedGeometryCannotRegisterAlias()
    {
        using var f = new DesktopControlFixture();
        var frame = f.Capture();
        var alias = MemberScreenshotRelayService.CreateRequestId(Guid.NewGuid().ToString("N"));
        f.Screenshots.RememberControlFrame(alias, frame.Geometry! with { });
        Assert.False(f.Screenshots.TryGetControlFrame(alias, out _));
    }
}

internal sealed class DesktopControlFixture : IDisposable
{
    public string Root { get; }
    public DesktopControlClock Clock { get; } = new();
    public DodocoSettingsService Settings { get; }
    public ScreenshotService Screenshots { get; }
    public DesktopControlService Control { get; }
    public FakeDesktopSender Sender { get; } = new();
    public FakeDesktopControlClient? Client;
    public Action? GeometryProbe;
    public DesktopCaptureGeometry Geometry = new() { Left = 0, Top = 0, Width = 3840, Height = 2160, DisplayId = "primary", LayoutId = "layout-150-percent" };
    public DateTimeOffset Deadline => Clock.GetUtcNow() + DesktopControlProtocol.RequestLifetime;
    public DesktopControlFixture()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "测试必须使用隔离 NEXUSBGI_DATA_ROOT");
        Root = Path.Combine(AssistantDataDirectory.Root, "desktop-control-" + Guid.NewGuid().ToString("N"));
        Settings = new DodocoSettingsService(Root);
        Screenshots = new ScreenshotService(() => 1280, Clock, () => { GeometryProbe?.Invoke(); return Geometry; },
            (_, _) => new ScreenFrame { JpegBytes = [1, 2, 3], Width = 1280, Height = 720, Geometry = Geometry });
        Control = new DesktopControlService(Screenshots, Settings, () => Client, Sender, Clock);
    }
    public void Allow() => Settings.Update(x => x.AllowDesktopControl = true);
    public ScreenFrame Capture() => Screenshots.Capture()!;
    public DesktopControlCommand Command(ScreenFrame? frame = null) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), TargetUid = "local", FrameId = (frame ?? Capture()).FrameId,
        Action = "key", Key = "Win+D"
    };
    public DesktopControlCommand RemoteCommand()
    {
        var frame = Capture();
        var id = MemberScreenshotRelayService.CreateRequestId(Guid.NewGuid().ToString("N"));
        Screenshots.RememberControlFrame(id, frame.Geometry!, Client!.DesktopControlContext);
        return Command(frame) with { TargetUid = Client.DesktopControlUid, FrameId = id };
    }
    public void Dispose()
    {
        Control.Dispose();
        // 本夹具只写自身新建的设置目录，失败日志由外层受控测试命令保留。
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

internal sealed class FakeDesktopSender : DesktopInputSender
{
    public int Calls;
    public Action<CancellationToken, Action?>? BeforeSend;
    public override void Send(DesktopControlCommand command, DesktopCaptureGeometry geometry, CancellationToken cancellationToken, Action? beforeInput = null)
    {
        BeforeSend?.Invoke(cancellationToken, beforeInput);
        cancellationToken.ThrowIfCancellationRequested();
        beforeInput?.Invoke();
        Interlocked.Increment(ref Calls);
    }
}

internal sealed class FakeDesktopControlClient : SignalRClient
{
    public bool Ready = true;
    public long Epoch = 1;
    public string Room = "room-a";
    public string Uid = "target-uid";
    public readonly ConcurrentQueue<DesktopControlCommand> Sent = new();
    public readonly ConcurrentQueue<string> CancelledSessions = new();
    private readonly Channel<DesktopControlResult> _reports = Channel.CreateUnbounded<DesktopControlResult>();
    internal override bool DesktopControlReady => Ready;
    internal override long DesktopControlEpoch => Epoch;
    internal override string DesktopControlRoomCode => Room;
    internal override string DesktopControlUid => Uid;
    public override Task RequestDesktopControlAsync(DesktopControlCommand command, CancellationToken cancellationToken = default)
    { Sent.Enqueue(command); return Task.CompletedTask; }
    public override Task ReportDesktopControlAsync(DesktopControlResult result, CancellationToken cancellationToken = default)
    { _reports.Writer.TryWrite(result); return Task.CompletedTask; }
    public override Task CancelDesktopControlAsync(string sessionId, string? targetUid = null, CancellationToken cancellationToken = default)
    { CancelledSessions.Enqueue(sessionId); return Task.CompletedTask; }
    public Task<DesktopControlResult> NextReport() => _reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    public void Request(DesktopControlCommand command, DateTimeOffset deadline,
        double frameAgeMilliseconds = 0, double lifetimeMilliseconds = 3000)
        => Event("desktop.control.requested", new DesktopControlRequest { Command = command, RoomCode = Room,
            RequesterConnectionId = "viewer", ExpiresAtUtc = deadline,
            FrameAgeMilliseconds = frameAgeMilliseconds, LifetimeMilliseconds = lifetimeMilliseconds });
    public void Result(DesktopControlResult result, string? room = null) => Event("desktop.control.result", result, room);
    public void Cancel(string id) => Event("desktop.control.cancelled", new { requestId = id });
    private void Event(string name, object data, string? room = null)
        => DispatchDesktopControlEvent(new GatewayEnvelope { Type = "event", Name = name, RoomCode = room ?? Room, Payload = GatewayEnvelope.ToPayload(data) });
}

internal sealed class DesktopControlClock : TimeProvider
{
    private long _ticks;
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow;
    private readonly List<ClockTimer> _timers = [];
    public TimeSpan WallOffset;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => _origin + TimeSpan.FromTicks(GetTimestamp()) + WallOffset;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        Interlocked.Add(ref _ticks, elapsed.Ticks);
        ClockTimer[] timers;
        lock (_timers) timers = _timers.ToArray();
        foreach (var timer in timers) timer.Fire();
    }
    private sealed class ClockTimer(DesktopControlClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _due = long.MaxValue;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                return true;
            }
        }
        public void Fire()
        {
            lock (this)
            {
                if (_disposed || clock.GetTimestamp() < _due) return;
                _due = long.MaxValue;
            }
            callback(state);
        }
        public void Dispose() { lock (this) _disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
