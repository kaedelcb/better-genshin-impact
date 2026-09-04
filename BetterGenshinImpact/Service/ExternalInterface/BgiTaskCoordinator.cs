using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// [切片7] BGI 侧任务协调器（进程级单例）：ext.task.start 的队列式编排
/// （Actor Mailbox 语义——忙时不拒收、串行派发；背压=有界队列满返回 queue_full，不阻塞调用方）。
/// 只服务协商了 capability task.queue 的 ext 通道；v2 task.start / 手动 UI / 触发器路径零改动（货冻结）。
/// 红线：等锁只读轮询 TaskSemaphore.CurrentCount（绝不 WaitAsync 抢占）；
/// 执行段走 InstanceRequestHandler.ExecuteTaskStartCoreAsync（内部 Dispatcher.InvokeAsync，与 v2 同一事实源）；
/// 事件发布只读 + fire-and-forget（ExternalInterfaceEventHub.Publish，零订阅者零扇出）；
/// pump 为 fire-and-forget 后台任务，所有异常就地观测落日志。
/// </summary>
internal sealed class BgiTaskCoordinator : IDisposable
{
    /// <summary>有界队列容量（Reactive Streams 背压：满则 queue_full，不静默丢弃、不阻塞）。</summary>
    public const int QueueCapacity = 8;

    private static readonly TimeSpan DefaultSlotPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultSlotWaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>进程级单例（懒创建：单机不连助手时连对象都不建，零感知）。</summary>
    private static readonly Lazy<BgiTaskCoordinator> SharedInstance = new(() => new BgiTaskCoordinator());

    public static BgiTaskCoordinator Instance => SharedInstance.Value;

    /// <summary>单例是否已创建（App.OnExit 据此决定是否 Dispose，不为关机而创建实例）。</summary>
    public static bool IsCreated => SharedInstance.IsValueCreated;

    public enum SubmitStatus
    {
        /// <summary>已入队，立即返回 taskHandle。</summary>
        Queued,

        /// <summary>同 generation+name 命中在队/在跑项，采用既有 taskHandle（不发 task.queued）。</summary>
        Adopted,

        /// <summary>同 generation+name 最近一次已执行完成（沿用 _lastExecutedTask 语义）。</summary>
        AlreadyExecuted,

        /// <summary>队列已满（背压，调用方显式处理）。</summary>
        QueueFull,

        /// <summary>协调器已销毁（进程退出中）。</summary>
        Unavailable,
    }

    public enum CancelOutcome
    {
        /// <summary>在队项：已移除并取消（task.queueCancelled 已发布）。</summary>
        CancelledQueued,

        /// <summary>在跑且句柄匹配：调用方应走等价 task.stop 的全局取消。</summary>
        StopRequestedRunning,

        /// <summary>句柄不存在（不在队也不在跑）。</summary>
        NotFound,
    }

    /// <summary>一次任务提交。Executor = 执行段委托（内部自行 Dispatcher.InvokeAsync），返回 true = 执行中被取消。</summary>
    public sealed record TaskSubmission(
        int Generation,
        string? GroupName,
        string? ConfigName,
        int StartFromIndex,
        Func<CancellationToken, Task<bool>> Executor)
    {
        /// <summary>幂等去重名（groupName ?? configName），与 v2 taskName 语义一致。</summary>
        public string? Name => GroupName ?? ConfigName;
    }

    public readonly record struct SubmitResult(SubmitStatus Status, Guid TaskHandle, int QueuePosition);

    private sealed class PendingTask
    {
        public required Guid TaskHandle { get; init; }
        public required TaskSubmission Submission { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public DateTime EnqueuedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>终态事件（queueCancelled/failed）只发一次：取消路径与 pump 谁先观察到取消谁发。</summary>
        private int _terminalEventPublished;

        /// <summary>CTS 只 Dispose 一次（出队/取消/退出路径都可能触发）。</summary>
        private int _ctsDisposed;

        public bool TryMarkTerminalEventPublished()
            => Interlocked.CompareExchange(ref _terminalEventPublished, 1, 0) == 0;

        public void DisposeCtsOnce()
        {
            if (Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
            {
                Cts.Dispose();
            }
        }
    }

    private readonly Channel<PendingTask> _channel = Channel.CreateBounded<PendingTask>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>在队项登记（句柄路由取消 + queueDepth 的权威来源）。项开始执行时移出。</summary>
    private readonly ConcurrentDictionary<Guid, PendingTask> _pending = new();

    private readonly Func<bool> _isSlotFree;
    private readonly Action<string, object?> _publish;
    private readonly ILogger _logger;
    private readonly TimeSpan _slotPollInterval;
    private readonly TimeSpan _slotWaitTimeout;
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>提交/取消/派发转换的串行化锁（粒度极小，绝不跨 await 持有）。</summary>
    private readonly object _submitLock = new();
    private readonly object _pumpLock = new();

    private PendingTask? _current;
    private Task? _pumpTask;
    private bool _disposed;

    /// <summary>最近一次执行完成的 task.start 代序号+名（从 InstanceRequestHandler._lastExecutedTask 迁入，单一事实源）。</summary>
    private (int Generation, string? Name) _lastExecutedTask = (0, null);

    /// <summary>
    /// 测试友好构造：槽位判定/事件发布/等锁节奏全部可注入。
    /// 生产默认：只读轮询 TaskControl.TaskSemaphore.CurrentCount（绝不 WaitAsync），事件走 EventHub。
    /// </summary>
    internal BgiTaskCoordinator(
        Func<bool>? isSlotFree = null,
        Action<string, object?>? publish = null,
        ILogger? logger = null,
        TimeSpan? slotPollInterval = null,
        TimeSpan? slotWaitTimeout = null)
    {
        _isSlotFree = isSlotFree ?? (static () => GameTask.Common.TaskControl.TaskSemaphore.CurrentCount != 0);
        _publish = publish ?? ((name, payload) => ExternalInterfaceEventHub.Instance.Publish(name, payload));
        _slotPollInterval = slotPollInterval ?? DefaultSlotPollInterval;
        _slotWaitTimeout = slotWaitTimeout ?? DefaultSlotWaitTimeout;
        if (logger is not null)
        {
            _logger = logger;
        }
        else
        {
            try
            {
                _logger = App.GetService<ILogger<BgiTaskCoordinator>>() ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;
            }
            catch
            {
                _logger = NullLogger.Instance;
            }
        }
    }

    /// <summary>在队任务数（task.status.queueDepth）。</summary>
    public int QueueDepth => _pending.Count;

    /// <summary>在跑任务的句柄（协调器派发时登记；手动任务为 null）。</summary>
    public Guid? CurrentTaskHandle
    {
        get
        {
            lock (_submitLock)
            {
                return _current?.TaskHandle;
            }
        }
    }

    /// <summary>最近一次执行完成的 task.start 代序号+名（v2 handler 幂等检查改查这里，行为等价）。</summary>
    public (int Generation, string? Name) LastExecutedTask
    {
        get
        {
            lock (_submitLock)
            {
                return _lastExecutedTask;
            }
        }
    }

    /// <summary>登记最近一次执行（v2 handler 在"无损拒绝检查之后"调用，切片1修复语义不得回退；pump 在派发点调用）。</summary>
    public void RegisterExecuted(int generation, string? name)
    {
        lock (_submitLock)
        {
            _lastExecutedTask = (generation, name);
        }
    }

    /// <summary>
    /// 入队：幂等去重（在队/在跑 → adopted；最近已执行 → already_executed）→ TryWrite 背压 →
    /// 发 task.queued → 立即返回。全程不阻塞调用方。
    /// </summary>
    public SubmitResult Submit(TaskSubmission submission)
    {
        PendingTask? adopted = null;
        var name = submission.Name;

        lock (_submitLock)
        {
            if (_disposed)
            {
                return new SubmitResult(SubmitStatus.Unavailable, Guid.Empty, 0);
            }

            if (submission.Generation > 0)
            {
                if (_current is { } current
                    && current.Submission.Generation == submission.Generation
                    && current.Submission.Name == name)
                {
                    adopted = current;
                }
                else
                {
                    adopted = _pending.Values.FirstOrDefault(
                        p => p.Submission.Generation == submission.Generation && p.Submission.Name == name);
                }

                if (adopted is not null)
                {
                    _logger.LogInformation(
                        "[task.queue] generation={Gen} name={Name} 在队/在跑，采用既有 taskHandle={Handle}",
                        submission.Generation, name, adopted.TaskHandle);
                    return new SubmitResult(SubmitStatus.Adopted, adopted.TaskHandle, 0);
                }

                if (submission.Generation == _lastExecutedTask.Generation
                    && name == _lastExecutedTask.Name)
                {
                    _logger.LogInformation(
                        "[task.queue] generation={Gen} name={Name} 已执行过，跳过重复执行",
                        submission.Generation, name);
                    return new SubmitResult(SubmitStatus.AlreadyExecuted, Guid.Empty, 0);
                }
            }

            var item = new PendingTask
            {
                TaskHandle = Guid.NewGuid(),
                Submission = submission,
                Cts = new CancellationTokenSource(),
            };

            // 背压语义门 = 在队登记数（pump 出队后等槽位的项仍算在队，只信 Channel 占用会把
            // "等槽位中的项"误判为已消化——实测姿势见 BgiTaskCoordinatorTests.Backpressure）。
            if (_pending.Count >= QueueCapacity)
            {
                item.DisposeCtsOnce();
                _logger.LogWarning("[task.queue] 队列已满（容量 {Capacity}），拒绝入队 name={Name}", QueueCapacity, name);
                return new SubmitResult(SubmitStatus.QueueFull, Guid.Empty, 0);
            }

            if (!_channel.Writer.TryWrite(item))
            {
                // 兜底：Channel 物理满（与登记数不同步的极端竞态），同样不阻塞
                item.DisposeCtsOnce();
                _logger.LogWarning("[task.queue] 队列已满（容量 {Capacity}），拒绝入队 name={Name}", QueueCapacity, name);
                return new SubmitResult(SubmitStatus.QueueFull, Guid.Empty, 0);
            }

            _pending[item.TaskHandle] = item;
            EnsurePumpStartedNoLock();
            var position = _pending.Count;

            _logger.LogInformation(
                "[task.queue] 入队 taskHandle={Handle} name={Name} generation={Gen} queuePosition={Pos}",
                item.TaskHandle, name, submission.Generation, position);

            // 锁外发布会引入乱序风险极小（事件仅通知语义），但保持锁内发布使 queuePosition 与事件顺序一致；
            // Publish 本身只读 + fire-and-forget，不在锁内做 I/O 等待，无死锁风险。
            PublishSafe(ExternalInterfaceEventNames.TaskQueued, new
            {
                taskHandle = item.TaskHandle.ToString("N"),
                groupName = submission.GroupName,
                configName = submission.ConfigName,
                generation = submission.Generation,
                queuePosition = position,
            });

            return new SubmitResult(SubmitStatus.Queued, item.TaskHandle, position);
        }
    }

    /// <summary>按句柄取消：在队 → 移除+事件；在跑且匹配 → 由调用方走等价 task.stop；否则 task_not_found。</summary>
    public CancelOutcome CancelByHandle(Guid taskHandle)
    {
        PendingTask? queued = null;
        lock (_submitLock)
        {
            if (_pending.TryGetValue(taskHandle, out queued))
            {
                _pending.TryRemove(taskHandle, out _);
                queued.Cts.Cancel();
            }
            else if (_current is { } current && current.TaskHandle == taskHandle)
            {
                return CancelOutcome.StopRequestedRunning;
            }
            else
            {
                return CancelOutcome.NotFound;
            }
        }

        if (queued.TryMarkTerminalEventPublished())
        {
            PublishSafe(ExternalInterfaceEventNames.TaskQueueCancelled, new { taskHandle = taskHandle.ToString("N") });
        }

        queued.DisposeCtsOnce();
        _logger.LogInformation("[task.queue] 在队项已取消 taskHandle={Handle}", taskHandle);
        return CancelOutcome.CancelledQueued;
    }

    /// <summary>清空在队项（ext.task.stop clearQueue=true）：逐项取消 + task.queueCancelled。返回清退数量。</summary>
    public int ClearQueue()
    {
        List<PendingTask> items;
        lock (_submitLock)
        {
            items = _pending.Values.ToList();
            _pending.Clear();
            foreach (var item in items)
            {
                item.Cts.Cancel();
            }
        }

        foreach (var item in items)
        {
            if (item.TryMarkTerminalEventPublished())
            {
                PublishSafe(ExternalInterfaceEventNames.TaskQueueCancelled, new { taskHandle = item.TaskHandle.ToString("N") });
            }

            item.DisposeCtsOnce();
        }

        if (items.Count > 0)
        {
            _logger.LogInformation("[task.queue] 清空在队项 {Count} 个", items.Count);
        }

        return items.Count;
    }

    // ===== pump（后台线程，串行派发）=====

    private void EnsurePumpStartedNoLock()
    {
        lock (_pumpLock)
        {
            if (_pumpTask is not null)
            {
                return;
            }

            // fire-and-forget：PumpLoopAsync 内部全量 try/catch 落日志，绝不逃逸未观测异常
            _pumpTask = Task.Run(PumpLoopAsync, CancellationToken.None);
        }
    }

    private async Task PumpLoopAsync()
    {
        var pumpToken = _disposeCts.Token;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(pumpToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    await DispatchItemAsync(item, pumpToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose 停止，正常退出
        }
        catch (Exception exception)
        {
            // pump 意外死亡（理论不可达：执行段有逐项 try/catch；此处为最后防线）。
            // 必须置空 _pumpTask，否则 EnsurePumpStartedNoLock 永不重启、队列静默瘫痪。
            // 不原地重启（防崩溃循环），下一次 Submit 时自动拉起并 drain 残留通道项。
            _logger.LogError(exception, "[task.queue] pump 循环异常退出，等待下一次提交时自动重启");
            lock (_pumpLock)
            {
                _pumpTask = null;
            }
        }
    }

    private async Task DispatchItemAsync(PendingTask item, CancellationToken pumpToken)
    {
        // 取消路径已处理的项：静默跳过（queueCancelled 由取消路径发布，CTS 已 Dispose）
        if (item.Cts.IsCancellationRequested)
        {
            if (item.TryMarkTerminalEventPublished())
            {
                PublishSafe(ExternalInterfaceEventNames.TaskQueueCancelled, new { taskHandle = item.TaskHandle.ToString("N") });
            }

            item.DisposeCtsOnce();
            return;
        }

        // 1. 等槽位空（只读轮询 CurrentCount，绝不 WaitAsync 抢占；15s 兜底防旧任务卡死）
        if (!await WaitSlotFreeAsync(item, pumpToken).ConfigureAwait(false))
        {
            if (item.Cts.IsCancellationRequested)
            {
                if (item.TryMarkTerminalEventPublished())
                {
                    PublishSafe(ExternalInterfaceEventNames.TaskQueueCancelled, new { taskHandle = item.TaskHandle.ToString("N") });
                }
            }
            else if (item.TryMarkTerminalEventPublished())
            {
                PublishSafe(ExternalInterfaceEventNames.TaskFailed, new
                {
                    taskHandle = item.TaskHandle.ToString("N"),
                    errorCode = "task_busy",
                    message = "等待任务槽位释放超时（15s），旧任务可能卡死",
                });
            }

            item.DisposeCtsOnce();
            return;
        }

        // 2. 进入执行转换（与取消路径在 _submitLock 下串行，消除"取消与执行"竞态窗口）
        lock (_submitLock)
        {
            if (item.Cts.IsCancellationRequested)
            {
                // 下一行 return 前由锁外统一处理终态
            }
            else
            {
                _pending.TryRemove(item.TaskHandle, out _);
                _current = item;
            }
        }

        if (_current != item)
        {
            if (item.TryMarkTerminalEventPublished())
            {
                PublishSafe(ExternalInterfaceEventNames.TaskQueueCancelled, new { taskHandle = item.TaskHandle.ToString("N") });
            }

            item.DisposeCtsOnce();
            return;
        }

        var startedAt = DateTime.UtcNow;
        try
        {
            // 3. 派发点幂等登记（对应 v2 "登记在拒绝检查之后、执行段之前"的位置）
            if (item.Submission.Generation > 0)
            {
                RegisterExecuted(item.Submission.Generation, item.Submission.Name);
            }

            PublishSafe(ExternalInterfaceEventNames.TaskStarted, new
            {
                taskHandle = item.TaskHandle.ToString("N"),
                groupName = item.Submission.GroupName,
                configName = item.Submission.ConfigName,
            });

            var cancelled = await item.Submission.Executor(item.Cts.Token).ConfigureAwait(false);
            var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            PublishSafe(ExternalInterfaceEventNames.TaskCompleted, new
            {
                taskHandle = item.TaskHandle.ToString("N"),
                groupName = item.Submission.GroupName,
                cancelled,
                durationMs,
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[task.queue] 任务执行失败 taskHandle={Handle}", item.TaskHandle);
            if (item.TryMarkTerminalEventPublished())
            {
                PublishSafe(ExternalInterfaceEventNames.TaskFailed, new
                {
                    taskHandle = item.TaskHandle.ToString("N"),
                    errorCode = "task_start_failed",
                    message = exception.GetBaseException().Message,
                });
            }
        }
        finally
        {
            lock (_submitLock)
            {
                if (ReferenceEquals(_current, item))
                {
                    _current = null;
                }
            }

            item.DisposeCtsOnce();
        }
    }

    /// <summary>等槽位空：只读轮询 + 项级取消可打断 + 兜底超时。返回 false = 超时或被取消。</summary>
    private async Task<bool> WaitSlotFreeAsync(PendingTask item, CancellationToken pumpToken)
    {
        var deadline = DateTime.UtcNow + _slotWaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_isSlotFree())
            {
                return true;
            }

            if (item.Cts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(_slotPollInterval, pumpToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false; // 协调器 Dispose
            }
        }

        return _isSlotFree();
    }

    /// <summary>事件发布只读 + fire-and-forget；发布异常绝不泄漏进 pump/调用方（红线：异常观测落日志）。</summary>
    private void PublishSafe(string eventName, object? payload)
    {
        try
        {
            _publish(eventName, payload);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "[task.queue] 事件发布失败 {Event}", eventName);
        }
    }

    /// <summary>进程退出前调用（App.OnExit）：停 pump、在队项 CTS 全部 Cancel+Dispose（防句柄泄漏）。</summary>
    public void Dispose()
    {
        lock (_submitLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _channel.Writer.TryComplete();
        _disposeCts.Cancel();

        List<PendingTask> remaining;
        lock (_submitLock)
        {
            remaining = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var item in remaining)
        {
            try
            {
                item.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 并发取消路径已处理
            }

            item.DisposeCtsOnce();
        }

        try
        {
            _pumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 关机路径吞异常
        }

        _disposeCts.Dispose();
    }
}
