using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>A transport transition is retryable; a server identity rejection is not.</summary>
internal sealed class ControlChannelUnavailableException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>Serializes room ownership changes and batch calls, without owning local stop/cleanup.</summary>
internal sealed class ControlSubmissionDeferredException(string message, Exception? inner = null)
    : IOException(message, inner);

internal sealed class ControlContextRevokedException(string message, CancellationToken token)
    : OperationCanceledException(message, token);

internal sealed class CoordinatedControlAuthority
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _state = new();
    private object? _owner;
    private string _context = "";
    private long _generation;
    private bool _ready;
    private CancellationTokenSource _revocation = new();

    internal sealed record Grant(object Owner, string Context, long Generation, CancellationToken Revoked);

    private sealed class Scope(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _transition.WaitAsync(ct).ConfigureAwait(false);
        return new Scope(_transition);
    }

    internal void Revoke(object? expectedOwner = null)
    {
        CancellationTokenSource revoked;
        lock (_state)
        {
            if (expectedOwner is not null && !ReferenceEquals(expectedOwner, _owner)) return;
            _ready = false;
            _generation++;
            revoked = _revocation;
            _revocation = new();
        }
        // Token callbacks must never run under the state lock or acquire the transition gate.
        try { revoked.Cancel(); }
        catch (AggregateException) { /* All cancellation callbacks ran; the grant remains revoked. */ }
    }

    internal void Publish(object owner, string context)
    {
        Revoke();
        lock (_state)
        {
            _owner = owner;
            _context = context;
            _ready = true;
        }
    }

    internal Grant Capture(object owner, string context)
    {
        lock (_state)
        {
            if (!_ready || !ReferenceEquals(owner, _owner) || context != _context)
                throw new ControlChannelUnavailableException("控制房间业务身份正在恢复");
            return new(owner, context, _generation, _revocation.Token);
        }
    }

    internal async Task<T> SubmitAsync<T>(Grant? grant, Action requireAdmission,
        Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        IDisposable gate;
        try { gate = await EnterAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        { throw new ControlSubmissionDeferredException("提交门等待已取消，未发送任务", ex); }
        using (gate)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                requireAdmission();
                if (grant is null) throw new ControlChannelUnavailableException("尚无原批次运行授权");
                Verify(grant);
            }
            catch (Exception ex) when (ex is not ControlContextRevokedException
                && ex is ControlChannelUnavailableException or TimeoutException or OperationCanceledException)
            { throw new ControlSubmissionDeferredException("提交前原授权已撤销，未发送任务", ex); }
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, grant!.Revoked);
            try { call.Token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException ex)
            { throw new ControlSubmissionDeferredException("发送调用前授权已撤销，未发送任务", ex); }
            // Crossing this line is the one send boundary. No later exception is definitely-not-sent.
            return await send(call.Token).ConfigureAwait(false);
        }
    }

    internal void Verify(Grant grant)
    {
        lock (_state)
            if (!_ready || !ReferenceEquals(grant.Owner, _owner) || grant.Context != _context
                || grant.Generation != _generation || grant.Revoked.IsCancellationRequested)
                throw new ControlChannelUnavailableException("控制连接代次已变化，保留原批次等待核对");
    }
}
