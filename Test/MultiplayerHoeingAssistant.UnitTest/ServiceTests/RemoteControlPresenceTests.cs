using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RemoteControlPresenceTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task MultipleOwnersPrefixClearAndOldEndKeepOtherLeasesVisible()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var view = new Indicator();
        using var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        Assert.True(await presence.ActivateAsync("remote:one:", "old", Lease, false));
        Assert.True(await presence.ActivateAsync("browser:two:", "two", Lease, false));
        await presence.ClearOwnerPrefixAsync("remote:");
        Assert.False(presence.IsActive("remote:one:", "old"));
        Assert.True(presence.IsActive("browser:two:", "two"));
        Assert.True(view.Visible);
        Assert.True(await presence.ActivateAsync("remote:one:", "new", Lease, false));
        await presence.DeactivateAsync("remote:one:", "old");
        Assert.True(presence.IsActive("remote:one:", "new"));
        await presence.ClearAsync();
        Assert.False(view.Visible);
        Assert.False(await presence.ActivateAsync("browser:two:", "two", Lease, true));
    }

    [Fact]
    public async Task EndBeforeStartAndEndWhileQueuedCannotResurrect()
    {
        using var ui = new UiThread();
        var view = new Indicator();
        using var presence = new RemoteControlPresence(ui.Dispatcher, new Clock(), () => view);
        await presence.DeactivateAsync("a", "early");
        Assert.False(await presence.ActivateAsync("a", "early", Lease, false));
        using (ui.Block())
        {
            var pending = presence.ActivateAsync("a", "late", Lease, false);
            var end = presence.DeactivateAsync("a", "late");
            Assert.False(presence.IsActive("a", "late"));
            ui.Release();
            Assert.False(await pending);
            await end;
        }
        Assert.False(view.Visible);
        Assert.Equal(0, view.Shows);
        Assert.False(await presence.ActivateAsync("a", "late", Lease, false));
    }

    [Fact]
    public async Task ExpiryAndRenewalUseBoundedMonotonicTimeAndCloseAutomatically()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var view = new Indicator();
        using var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        Assert.True(await presence.ActivateAsync("a", "one", TimeSpan.FromMinutes(2), false));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(await presence.ActivateAsync("a", "one", Lease, true));
        clock.Advance(TimeSpan.FromSeconds(12));
        await ui.Flush();
        Assert.False(presence.IsActive("a", "one"));
        Assert.False(view.Visible);
        Assert.False(await presence.ActivateAsync("a", "one", Lease, true));
        Assert.True(await presence.ActivateAsync("a", "short", TimeSpan.FromSeconds(2), false));
        clock.Advance(TimeSpan.FromSeconds(2));
        await ui.Flush();
        Assert.False(view.Visible);
    }

    [Fact]
    public async Task QueuedCancellationExpiryAndClearDoNotShow()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var view = new Indicator();
        using var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        using var cancellation = new CancellationTokenSource();
        using (ui.Block())
        {
            var cancelled = presence.ActivateAsync("a", "cancel", Lease, false, cancellation.Token);
            var expired = presence.ActivateAsync("a", "expire", TimeSpan.FromSeconds(1), false);
            var cleared = presence.ActivateAsync("b", "clear", Lease, false);
            cancellation.Cancel();
            clock.Advance(TimeSpan.FromSeconds(1));
            var clear = presence.ClearOwnerPrefixAsync("b");
            ui.Release();
            Assert.False(await cancelled);
            Assert.False(await expired);
            Assert.False(await cleared);
            await clear;
        }
        Assert.Equal(0, view.Shows);
        Assert.False(view.Visible);
    }

    [Fact]
    public async Task FailedShowAndFactoryNeverConfirmAndDisposeCloses()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var failed = new Indicator { FailShow = true };
        using (var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => failed))
        {
            Assert.False(await presence.ActivateAsync("a", "fail", Lease, false));
            Assert.False(presence.IsActive("a", "fail"));
            Assert.Equal(1, failed.Closes);
        }
        using (var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => throw new InvalidOperationException()))
            Assert.False(await presence.ActivateAsync("a", "factory", Lease, false));
        var view = new Indicator();
        var disposable = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        Assert.True(await disposable.ActivateAsync("a", "live", Lease, false));
        disposable.Dispose();
        await ui.Flush();
        Assert.False(view.Visible);
        Assert.False(disposable.IsActive("a", "live"));
    }

    [Fact]
    public async Task EndOrExpiryDuringShowCannotConfirm()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var view = new Indicator { OnShow = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        using var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        var pending = presence.ActivateAsync("a", "one", Lease, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var end = presence.DeactivateAsync("a", "one");
        clock.Advance(Lease);
        release.Set();
        Assert.False(await pending);
        await end;
        Assert.False(view.Visible);
        Assert.False(presence.IsActive("a", "one"));
    }

    [Fact]
    public async Task ExpiryDuringShowCannotConfirm()
    {
        using var ui = new UiThread();
        var clock = new Clock();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var view = new Indicator { OnShow = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        using var presence = new RemoteControlPresence(ui.Dispatcher, clock, () => view);
        var pending = presence.ActivateAsync("a", "one", Lease, false);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        clock.Advance(Lease);
        release.Set();
        Assert.False(await pending);
        await ui.Flush();
        Assert.False(view.Visible);
        Assert.False(await presence.ActivateAsync("a", "one", Lease, true));
    }

    [Fact]
    public async Task DisposeWhileQueuedPreventsLateDisplay()
    {
        using var ui = new UiThread();
        var view = new Indicator();
        var presence = new RemoteControlPresence(ui.Dispatcher, new Clock(), () => view);
        using (ui.Block())
        {
            var pending = presence.ActivateAsync("a", "one", Lease, false);
            presence.Dispose();
            ui.Release();
            Assert.False(await pending);
        }
        await ui.Flush();
        Assert.Equal(0, view.Shows);
        Assert.False(view.Visible);
    }

    [Fact]
    public async Task UnexpectedIndicatorCloseRevokesEveryLease()
    {
        using var ui = new UiThread();
        var view = new Indicator();
        using var presence = new RemoteControlPresence(ui.Dispatcher, new Clock(), () => view);
        Assert.True(await presence.ActivateAsync("a", "one", Lease, false));
        Assert.True(await presence.ActivateAsync("b", "two", Lease, false));
        await ui.Dispatcher.InvokeAsync(view.Close).Task;
        Assert.False(presence.IsActive("a", "one"));
        Assert.False(presence.IsActive("b", "two"));
        Assert.False(await presence.ActivateAsync("a", "one", Lease, true));
    }

    private sealed class Indicator : IRemoteControlIndicator
    {
        public event EventHandler? Closed;
        public bool IsVisible => Visible;
        public bool Visible { get; private set; }
        public int Shows { get; private set; }
        public int Closes { get; private set; }
        public bool FailShow { get; init; }
        public Action? OnShow { get; init; }
        public void Show() { Shows++; OnShow?.Invoke(); if (FailShow) throw new InvalidOperationException(); Visible = true; }
        public void Close() { Closes++; Visible = false; Closed?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref _ticks, amount.Ticks);
            Timer[] timers;
            lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _release = new();
        public Dispatcher Dispatcher { get; }
        public UiThread()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => { ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher); System.Windows.Threading.Dispatcher.Run(); }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        public IDisposable Block()
        {
            _release.Reset();
            using var entered = new ManualResetEventSlim();
            Dispatcher.BeginInvoke(new Action(() => { entered.Set(); _release.Wait(TimeSpan.FromSeconds(10)); }));
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("UI dispatcher did not block");
            return new ReleaseHandle(this);
        }
        public void Release() => _release.Set();
        public Task Flush() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public void Dispose()
        {
            Release();
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
            _release.Dispose();
        }
        private sealed class ReleaseHandle(UiThread owner) : IDisposable { public void Dispose() => owner.Release(); }
    }
}
