using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 联机接管期间冻结任务中心的所有外部发送。取得发送租约的调用持有锁直到端口返回，
/// 因此 CloseAsync 返回时不存在尚在发送窗口中的任务中心请求。
/// </summary>
internal sealed class TaskCenterOnlineSendGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _stateGate = new();
    private TaskCompletionSource _opened = Completed();
    private volatile bool _closed;

    public bool IsClosed => _closed;

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            await _mutex.WaitAsync(ct).ConfigureAwait(false);
            Task opened;
            lock (_stateGate)
            {
                if (!_closed) return new SendLease(_mutex);
                opened = _opened.Task;
            }
            _mutex.Release();
            await opened.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void CloseForPriority()
    {
        lock (_stateGate)
        {
            if (_closed) return;
            _closed = true;
            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try { CloseForPriority(); }
        finally { _mutex.Release(); }
    }

    public Task OpenAsync()
    {
        lock (_stateGate)
        {
            _closed = false;
            _opened.TrySetResult();
        }
        return Task.CompletedTask;
    }

    private sealed class SendLease(SemaphoreSlim mutex) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            mutex.Release();
            return ValueTask.CompletedTask;
        }
    }
}
