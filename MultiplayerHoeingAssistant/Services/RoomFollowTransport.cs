using System.Diagnostics;
using System.Threading.Channels;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>每份授权一条固定频道；既不连接当前房间，也不主动向无订阅需求的中继发送元数据。</summary>
public sealed class RoomFollowPublisher : IAsyncDisposable
{
    private readonly RoomFollowGrant _grant;
    private readonly bool _bypassProxy;
    private readonly RoomFollowWorker _worker = new();
    private readonly RoomFollowOutbox _outbox = new();

    public RoomFollowPublisher(RoomFollowGrant grant, bool bypassProxy)
    {
        ArgumentNullException.ThrowIfNull(grant);
        grant.Validate();
        _grant = grant;
        _bypassProxy = bypassProxy;
    }

    /// <summary>显式启动后台监听/绑定；幂等，返回不表示已连接。失败仅重连，不查询房间状态。</summary>
    public Task StartAsync() => _worker.StartAsync(token => RoomFollowWorker.RetryAsync(
        ct => _grant.Invitation.Local
            ? RoomFollowLocalTransport.PublishAsync(_grant, _outbox, ct)
            : RoomFollowRemoteTransport.PublishAsync(_grant, _bypassProxy, _outbox, ct), token));

    public void Update(RoomFollowSnapshot? snapshot)
    {
        _worker.ThrowIfDisposed();
        try { _outbox.Update(RoomFollowSnapshotCodec.Encode(snapshot, _grant.Invitation)); }
        catch (ArgumentException)
        {
            _outbox.Update(null); // 无效更新不能让旧快照继续作为已确认状态。
            throw;
        }
    }

    public ValueTask DisposeAsync() => _worker.DisposeAsync();
}

/// <summary>仅接收持有邀请的指定执行端元数据；不读取房间密码，也不参与房间鉴权。</summary>
public sealed class RoomFollowSubscriber : IAsyncDisposable
{
    private readonly RoomFollowInvitation _invitation;
    private readonly bool _bypassProxy;
    private readonly RoomFollowWorker _worker = new();
    private string? _lastDelivered;
    private bool _delivered;

    public RoomFollowSubscriber(RoomFollowInvitation invitation, bool bypassProxy)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        invitation.Validate();
        _invitation = invitation;
        _bypassProxy = bypassProxy;
    }

    /// <summary>后台线程按序提供最新完整快照；null 表示未连接、撤回、断线或无效状态。UI 调度由调用方负责。</summary>
    public event Action<RoomFollowSnapshot?>? Changed;

    /// <summary>显式启动后台任务；不等待尚未在线的执行端。DisposeAsync 返回后不会再回调。</summary>
    public Task StartAsync() => _worker.StartAsync(async token =>
    {
        Deliver(null);
        await RoomFollowWorker.RetryAsync(async ct =>
        {
            try
            {
                if (_invitation.Local) await RoomFollowLocalTransport.SubscribeAsync(_invitation, Deliver, ct);
                else await RoomFollowRemoteTransport.SubscribeAsync(_invitation, _bypassProxy, Deliver, ct);
            }
            finally { Deliver(null); }
        }, token);
    });

    private void Deliver(string? state)
    {
        if (_worker.IsDisposed && state is not null) return;
        if (!RoomFollowSnapshotCodec.TryDecode(state, _invitation, out var snapshot)) state = null;
        if (_delivered && string.Equals(_lastDelivered, state, StringComparison.Ordinal)) return;
        _delivered = true;
        _lastDelivered = state;
        if (Changed is not { } handlers) return;
        foreach (Action<RoomFollowSnapshot?> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch { /* 消费者异常不能破坏后续传输，也不记录其私有状态。 */ }
        }
    }

    public ValueTask DisposeAsync() => _worker.DisposeAsync();
}

// 只保存最新全量视图和一个唤醒信号，快速变化合并而非重放旧状态。
internal sealed class RoomFollowOutbox
{
    private readonly object _gate = new();
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private string? _state;
    private long _revision;

    internal void Update(string? state)
    {
        lock (_gate)
        {
            if (string.Equals(_state, state, StringComparison.Ordinal)) return;
            _state = state;
            _revision++;
        }
        Wake();
    }

    internal (long Revision, string? State) Capture()
    {
        lock (_gate) return (_revision, _state);
    }

    internal void Wake() => _signal.Writer.TryWrite(0);
    internal async ValueTask WaitAsync(CancellationToken token) { _ = await _signal.Reader.ReadAsync(token); }
}

internal sealed class RoomFollowFencedException : Exception { }

/// <summary>单一生命周期任务。前一连接已结束并释放才退避重连；停止后不再自动恢复。</summary>
internal sealed class RoomFollowWorker : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task, _stopping;
    private volatile bool _disposed;

    internal bool IsDisposed => _disposed;
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal Task StartAsync(Func<CancellationToken, Task> run)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _task ??= Task.Run(() => run(_lifetime.Token));
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _stopping ??= Task.Run(async () =>
            {
                try
                {
                    await _lifetime.CancelAsync();
                    if (_task is not null) await _task;
                }
                finally { _lifetime.Dispose(); }
            });
            return new ValueTask(_stopping);
        }
    }

    internal static async Task RetryAsync(Func<CancellationToken, Task> attempt, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try { await attempt(token); }
            catch (RoomFollowFencedException) { return; } // 旧发布端不能以自动重连抢回已替换的频道。
            catch (Exception) { /* 绑定/传输失败等同离线；不回显能力、连接地址或正文。 */ }
            if (token.IsCancellationRequested) break;
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30)) failures = 0;
            var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5)));
            failures = Math.Min(failures + 1, 5);
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
