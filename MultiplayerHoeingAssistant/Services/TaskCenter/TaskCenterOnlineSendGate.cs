using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 联机接管期间冻结任务中心的所有外部发送。取得发送租约的调用持有锁直到端口返回，
/// 因此 CloseAsync 返回时不存在尚在发送窗口中的任务中心请求。
/// </summary>
internal sealed class TaskCenterOnlineSendGate
{
    private SemaphoreSlim _mutex = new(1, 1);
    private readonly object _stateGate = new();
    private TaskCompletionSource _opened = Completed();
    private volatile bool _closed;
    private TaskCompletionSource _invalidated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ReleaseInvalidatedWaiterAsync(Task pending, SemaphoreSlim mutex)
    {
        try { await pending.ConfigureAwait(false); mutex.Release(); }
        catch (OperationCanceledException) { }
    }

    /// <summary>只在调用方已确认旧 BGI 物理复位后换代；旧租约仅能释放旧信号量。</summary>
    internal void ResetAfterPhysicalBgiReset()
    {
        lock (_stateGate)
        {
            _invalidated.TrySetResult();
            _invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mutex = new SemaphoreSlim(1, 1);
            _closed = false;
            _opened.TrySetResult();
        }
    }

    public bool IsClosed => _closed;

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct)
    {
        SemaphoreSlim mutex;
        Task invalidated;
        lock (_stateGate) { mutex = _mutex; invalidated = _invalidated.Task; }
        while (true)
        {
            TaskCenterDriveAuthority.VerifyCurrent();
            // The previous sender may never return. Invalidation wakes waiters without
            // disposing a semaphore that its old lease must still be able to release.
            var pending = mutex.WaitAsync(ct);
            if (await Task.WhenAny(pending, invalidated).ConfigureAwait(false) == invalidated)
            {
                _ = ReleaseInvalidatedWaiterAsync(pending, mutex);
                throw new RunRecordConflictException("原发送代已因物理复位失效。");
            }
            await pending.ConfigureAwait(false);
            Task opened;
            lock (_stateGate)
            {
                if (!ReferenceEquals(mutex, _mutex))
                {
                    mutex.Release();
                    throw new RunRecordConflictException("原发送代已因物理复位失效。");
                }
                try { TaskCenterDriveAuthority.VerifyCurrent(); }
                catch { mutex.Release(); throw; }
                if (!_closed) return new SendLease(mutex);
                opened = _opened.Task;
            }
            mutex.Release();
            await Task.WhenAny(opened, invalidated).WaitAsync(ct).ConfigureAwait(false);
            if (invalidated.IsCompleted) throw new RunRecordConflictException("原发送代已因物理复位失效。");
        }
    }

    public void CloseForPriority()
    {
        using var drivePermission = TaskCenterDriveAuthority.EnterPublication();
        lock (_stateGate)
        {
            if (_closed) return;
            _closed = true;
            _opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        SemaphoreSlim mutex;
        Task invalidated;
        lock (_stateGate) { mutex = _mutex; invalidated = _invalidated.Task; }
        var pending = mutex.WaitAsync(ct);
        if (await Task.WhenAny(pending, invalidated).ConfigureAwait(false) == invalidated)
        {
            _ = ReleaseInvalidatedWaiterAsync(pending, mutex);
            throw new RunRecordConflictException("原关闭请求已因物理复位失效。");
        }
        await pending.ConfigureAwait(false);
        try
        {
            using var drivePermission = TaskCenterDriveAuthority.EnterPublication();
            lock (_stateGate)
            {
                if (!ReferenceEquals(mutex, _mutex)) throw new RunRecordConflictException("原关闭请求已因物理复位失效。");
                TaskCenterDriveAuthority.VerifyCurrent();
                CloseForPriority();
            }
        }
        finally { mutex.Release(); }
    }

    public Task OpenAsync()
    {
        using var drivePermission = TaskCenterDriveAuthority.EnterPublication();
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
