using System.Windows.Threading;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>UI adapter; implementations are called only on the supplied dispatcher.</summary>
public interface IRemoteControlIndicator
{
    event EventHandler? Closed;
    bool IsVisible { get; }
    void Show();
    void Close();
}

/// <summary>Independent monotonic leases for each controller, with one shared indicator.</summary>
public sealed class RemoteControlPresence : IRemoteControlPresence
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromMinutes(3);
    private const int MaximumTombstones = 4096;
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly Func<IRemoteControlIndicator> _factory;
    private readonly Dictionary<(string Owner, string Id), Lease> _leases = new();
    private readonly Dictionary<(string Owner, string Id), long> _tombstones = new();
    private readonly ITimer _timer;
    private IRemoteControlIndicator? _window;
    private long? _overflowFence;
    private bool _disposed;

    private sealed class Lease
    {
        public long Started;
        public TimeSpan Lifetime;
        public bool Confirmed;
    }

    public RemoteControlPresence(Dispatcher dispatcher, TimeProvider? timeProvider = null,
        Func<IRemoteControlIndicator>? windowFactory = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = timeProvider ?? TimeProvider.System;
        _factory = windowFactory ?? (() => new RemoteControlIndicatorWindow());
        _timer = _clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public Task<bool> ActivateAsync(string owner, string controlId, TimeSpan lifetime, bool renewOnly,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(controlId) || lifetime <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            return Task.FromResult(false);
        var key = (owner, controlId);
        Lease lease;
        long requestedAt;
        var boundedLifetime = lifetime > MaximumLifetime ? MaximumLifetime : lifetime;
        lock (_gate)
        {
            requestedAt = _clock.GetTimestamp();
            Prune(requestedAt);
            if (_disposed || _tombstones.ContainsKey(key)) return Task.FromResult(false);
            if (_leases.TryGetValue(key, out var existing))
            {
                if (renewOnly && !existing.Confirmed) return Task.FromResult(false);
                lease = existing;
            }
            else
            {
                if (renewOnly || _overflowFence.HasValue) return Task.FromResult(false);
                lease = new Lease { Started = requestedAt, Lifetime = boundedLifetime };
                _leases.Add(key, lease);
            }
        }
        return ConfirmOnDispatcherAsync(key, lease, requestedAt, boundedLifetime, cancellationToken);
    }

    private async Task<bool> ConfirmOnDispatcherAsync((string Owner, string Id) key, Lease lease,
        long requestedAt, TimeSpan lifetime, CancellationToken token)
    {
        try
        {
            return await _dispatcher.InvokeAsync(() =>
            {
                lock (_gate)
                {
                    var now = _clock.GetTimestamp();
                    Prune(now);
                    if (!CanConfirm(key, lease, requestedAt, lifetime, token, now))
                    {
                        ReconcileWindow();
                        return false;
                    }
                }

                // Never hold the state lock over window creation or Show: end/clear must
                // record their tombstones immediately even if the UI is busy.
                IRemoteControlIndicator? candidate = null;
                try
                {
                    if (_window == null)
                    {
                        candidate = _factory();
                        candidate.Closed += IndicatorClosed;
                        candidate.Show();
                        if (!candidate.IsVisible) throw new InvalidOperationException("远程控制提示窗口未能显示。");
                    }
                }
                catch
                {
                    if (candidate != null) candidate.Closed -= IndicatorClosed;
                    try { candidate?.Close(); } catch { }
                    lock (_gate)
                    {
                        if (_leases.TryGetValue(key, out var current) && ReferenceEquals(current, lease))
                            Remove(key, _clock.GetTimestamp());
                        ReconcileWindow();
                    }
                    return false;
                }

                lock (_gate)
                {
                    var now = _clock.GetTimestamp();
                    Prune(now);
                    if (candidate != null) _window = candidate;
                    if (!CanConfirm(key, lease, requestedAt, lifetime, token, now))
                    {
                        ReconcileWindow();
                        return false;
                    }
                    // A late queued renewal must not overwrite a newer one.
                    if (_clock.GetElapsedTime(lease.Started, requestedAt) >= TimeSpan.Zero)
                    {
                        lease.Started = requestedAt;
                        lease.Lifetime = lifetime;
                    }
                    lease.Confirmed = true;
                    return true;
                }
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_leases.TryGetValue(key, out var current) && ReferenceEquals(current, lease)) Remove(key, _clock.GetTimestamp());
            }
            _ = QueueReconcile();
            return false;
        }
    }

    private bool CanConfirm((string Owner, string Id) key, Lease lease, long requestedAt,
        TimeSpan lifetime, CancellationToken token, long now)
    {
        _leases.TryGetValue(key, out var current);
        if (!_disposed && !token.IsCancellationRequested && ReferenceEquals(current, lease) &&
            _clock.GetElapsedTime(requestedAt, now) < lifetime) return true;
        if (ReferenceEquals(current, lease) && !lease.Confirmed) Remove(key, now);
        return false;
    }

    public Task DeactivateAsync(string owner, string controlId)
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            if (!_disposed) Remove((owner, controlId), now);
        }
        return QueueReconcile();
    }

    public Task ClearOwnerPrefixAsync(string ownerPrefix)
    {
        ArgumentNullException.ThrowIfNull(ownerPrefix);
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            foreach (var key in _leases.Keys.Where(k => k.Owner.StartsWith(ownerPrefix, StringComparison.Ordinal)).ToArray()) Remove(key, now);
        }
        return QueueReconcile();
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            Prune(now);
            foreach (var key in _leases.Keys.ToArray()) Remove(key, now);
        }
        return QueueReconcile();
    }

    public bool IsActive(string owner, string controlId)
    {
        bool active;
        lock (_gate)
        {
            Prune(_clock.GetTimestamp());
            active = !_disposed && _leases.TryGetValue((owner, controlId), out var lease) && lease.Confirmed;
        }
        if (!active) _ = QueueReconcile();
        return active;
    }

    private void Remove((string Owner, string Id) key, long now)
    {
        _leases.Remove(key);
        if (!_tombstones.ContainsKey(key) && _tombstones.Count >= MaximumTombstones)
        {
            // Do not evict a live tombstone while allowing a stale start to succeed.
            _overflowFence = now;
            return;
        }
        _tombstones[key] = now;
    }

    private void Prune(long now)
    {
        foreach (var key in _tombstones.Where(p => _clock.GetElapsedTime(p.Value, now) >= TombstoneLifetime).Select(p => p.Key).ToArray()) _tombstones.Remove(key);
        if (_overflowFence is long fence && _clock.GetElapsedTime(fence, now) >= TombstoneLifetime) _overflowFence = null;
        foreach (var key in _leases.Where(p => _clock.GetElapsedTime(p.Value.Started, now) >= p.Value.Lifetime).Select(p => p.Key).ToArray()) Remove(key, now);
    }

    private void Sweep()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Prune(_clock.GetTimestamp());
        }
        _ = QueueReconcile();
    }

    private Task QueueReconcile()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return Task.CompletedTask;
        try
        {
            return _dispatcher.InvokeAsync(() => { lock (_gate) { Prune(_clock.GetTimestamp()); ReconcileWindow(); } }).Task;
        }
        catch (InvalidOperationException) { return Task.CompletedTask; }
    }

    private void IndicatorClosed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_window is not { } window || !ReferenceEquals(window, sender)) return;
            window.Closed -= IndicatorClosed;
            _window = null;
            var now = _clock.GetTimestamp();
            foreach (var key in _leases.Keys.ToArray()) Remove(key, now);
        }
    }

    // Called under the state lock on the UI dispatcher.
    private void ReconcileWindow()
    {
        if (_window != null && (_disposed || !_leases.Values.Any(l => l.Confirmed)))
        {
            var window = _window;
            _window = null;
            window.Closed -= IndicatorClosed;
            try { window.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _leases.Clear();
            _tombstones.Clear();
        }
        _timer.Dispose();
        _ = QueueReconcile();
    }
}
