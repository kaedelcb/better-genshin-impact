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
            if (!_closed) return new SendLease(_mutex);
            var opened = _opened.Task;
            _mutex.Release();
            await opened.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally { _mutex.Release(); }
    }

    public async Task OpenAsync()
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_closed) return;
            _closed = false;
            _opened.TrySetResult();
        }
        finally { _mutex.Release(); }
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
