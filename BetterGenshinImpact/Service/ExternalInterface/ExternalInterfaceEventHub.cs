using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// ext.event 事件中心（进程级单例）。
/// 职责：订阅表（按会话隔离）、事件扇出（复用 InstanceConnection.WriteJsonAsync 写锁串行化）、
/// stateRevision 版本号、近因环形缓冲（断线续传补发，切片4）、
/// 观察器生命周期（第一个订阅者启动、最后一个退订停止——A1 单机零感知）。
/// 推送 fire-and-forget：绝不在任务/管道线程上挂起等待 I/O（§3.8-6）。
/// </summary>
internal sealed class ExternalInterfaceEventHub
{
    /// <summary>近因事件环形缓冲容量（《通信方案》§4.6 有界事件日志在模块一通道的对应实现）。</summary>
    public const int RecentEventCapacity = 500;

    private static readonly ExternalInterfaceEventHub SharedInstance = new();

    /// <summary>
    /// 进程级单例。App.xaml.cs 的 DI 注册（挂载点②）以工厂方式返回本实例，
    /// 保证 DI 注入与模块内静态访问拿到同一个对象。
    /// </summary>
    public static ExternalInterfaceEventHub Instance => SharedInstance;

    private readonly ConcurrentDictionary<Guid, EventSubscriber> _subscribers = new();
    private readonly ExternalInterfaceEventObserver _observer = new();
    private readonly ILogger _logger;
    private long _stateRevision;

    /// <summary>
    /// 近因事件环形缓冲（有界 500 条）：断线重连恢复订阅时按 lastKnownRevision 补发缺失事件。
    /// 只在 Publish 时入队（观察器仅在存在订阅者时运行，suspend/resumed 挂载点写入量可忽略），
    /// 独立小锁只做入队/出队/快照拷贝，不与扇出写路径交叉，不引入新锁竞争。
    /// </summary>
    private readonly object _recentLock = new();
    private readonly Queue<BufferedEvent> _recentEvents = new();

    private ExternalInterfaceEventHub()
    {
        try
        {
            _logger = App.GetService<ILogger<ExternalInterfaceEventHub>>() ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;
        }
        catch
        {
            _logger = NullLogger.Instance;
        }
    }

    /// <summary>当前状态版本号：每条事件 +1，ext.task.status 快照同源携带。</summary>
    public long CurrentRevision => Interlocked.Read(ref _stateRevision);

    public void Subscribe(
        Guid sessionId,
        InstanceConnection connection,
        IReadOnlyList<string> events)
    {
        var subscriber = _subscribers.GetOrAdd(
            sessionId,
            _ => new EventSubscriber(sessionId, connection));
        subscriber.AddEvents(events);
        _observer.Start();
        _logger.LogDebug(
            "ext.event 订阅：session={SessionId} events=[{Events}]（当前订阅会话数 {Count}）",
            sessionId,
            string.Join(",", events.Count == 0 ? ExternalInterfaceEventNames.All : events),
            _subscribers.Count);
    }

    /// <summary>退订。events 为空表示退订全部（会话级注销）。</summary>
    public void Unsubscribe(Guid sessionId, IReadOnlyList<string> events)
    {
        if (events.Count == 0)
        {
            RemoveSession(sessionId);
            return;
        }

        if (_subscribers.TryGetValue(sessionId, out var subscriber)
            && !subscriber.RemoveEvents(events))
        {
            RemoveSession(sessionId);
        }
    }

    /// <summary>连接断开/写失败时注销会话订阅，防止向死连接累积推送（A5/A9）。</summary>
    public void RemoveSession(Guid sessionId)
    {
        if (_subscribers.TryRemove(sessionId, out _))
        {
            _logger.LogDebug("ext.event 订阅已注销：session={SessionId}", sessionId);
        }

        if (_subscribers.IsEmpty)
        {
            _observer.Stop();
        }
    }

    /// <summary>发布事件：revision 单调 +1，入近因缓冲，扇出到所有匹配订阅者。</summary>
    public void Publish(string eventName, object? payload)
    {
        var revision = Interlocked.Increment(ref _stateRevision);
        var eventData = ExternalInterfaceProtocol.BuildEventData(eventName, revision, payload);
        var envelope = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.EventPush, eventData);

        lock (_recentLock)
        {
            _recentEvents.Enqueue(new BufferedEvent(revision, eventName, eventData));
            while (_recentEvents.Count > RecentEventCapacity)
            {
                _recentEvents.Dequeue();
            }
        }

        foreach (var (sessionId, subscriber) in _subscribers)
        {
            if (!subscriber.IsSubscribedTo(eventName))
            {
                continue;
            }

            // 连接已死 → 注销并跳过（探针"连上立刻断"等场景不留残留）
            if (subscriber.Connection.Completion.IsCompleted)
            {
                RemoveSession(sessionId);
                continue;
            }

            _ = PushToSubscriberAsync(subscriber, envelope);
        }
    }

    /// <summary>task.suspended / task.resumed 由 HandleTaskSuspend/HandleTaskResume 成功路径末尾调用（挂载点③）。</summary>
    public void PublishTaskSuspended(string taskType, string? groupName, int taskIndex)
        => Publish(ExternalInterfaceEventNames.TaskSuspended, new { taskType, groupName, taskIndex });

    public void PublishTaskResumed(string taskType, string? groupName)
        => Publish(ExternalInterfaceEventNames.TaskResumed, new { taskType, groupName });

    /// <summary>
    /// [切片7] 槽位释放全局信号：唯一挂载点 = TaskRunner.RunCurrentAsync 的 TaskSemaphore.Release() 之后一行。
    /// 无 handle（手动任务结束也发，助手 settle 判定统一靠它）。
    /// 挂载在任务引擎 finally 路径上，发布异常绝不外抛影响引擎收尾。
    /// </summary>
    public void PublishTaskSlotReleased()
    {
        try
        {
            Publish(ExternalInterfaceEventNames.TaskSlotReleased, new { });
        }
        catch
        {
            // 事件发布失败不影响任务引擎 finally（只读 fire-and-forget）
        }
    }

    /// <summary>
    /// 断线续传：取 lastKnownRevision 之后的缓冲事件（按订阅兴趣过滤）。
    /// 返回 resyncRequired=true 表示缺口超出缓冲（或缓冲为空但确有缺失/版本号回退），
    /// 客户端应主动拉 ext.task.status 快照校准（LSP 文档同步模型，§4.6 模块一对应实现）。
    /// 调用时序约定：注册订阅者之前调用并推送，缺口窗口由客户端跳号检测+快照兜底。
    /// </summary>
    public (List<JObject> ReplayEvents, bool ResyncRequired) GetReplayEvents(
        long lastKnownRevision,
        Func<string, bool> isInterested)
    {
        lock (_recentLock)
        {
            var current = CurrentRevision;
            if (lastKnownRevision < 0 || lastKnownRevision > current)
            {
                // 版本号回退（BGI 重启后 revision 归零）或非法值：无法续传，走快照校准
                return ([], true);
            }

            if (lastKnownRevision == current)
            {
                return ([], false); // 无缺失
            }

            if (_recentEvents.Count == 0)
            {
                // 有缺失但缓冲为空（如无订阅者期间发布的事件未入队前的边界），走快照校准
                return ([], true);
            }

            var oldest = _recentEvents.Peek().Revision;
            if (oldest > lastKnownRevision + 1)
            {
                // 缺口最老部分已出缓冲，补发无法保证完整 → 快照校准
                return ([], true);
            }

            var replay = new List<JObject>();
            foreach (var buffered in _recentEvents)
            {
                if (buffered.Revision > lastKnownRevision && isInterested(buffered.Name))
                {
                    replay.Add(buffered.Data);
                }
            }

            return (replay, false);
        }
    }

    private async Task PushToSubscriberAsync(
        EventSubscriber subscriber,
        InstanceIpcEnvelope envelope)
    {
        try
        {
            await subscriber.Connection
                .WriteJsonAsync(envelope, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                          or ObjectDisposedException
                                          or OperationCanceledException
                                          or InvalidOperationException)
        {
            _logger.LogDebug(exception, "ext.event 推送失败，注销订阅会话 {SessionId}", subscriber.SessionId);
            RemoveSession(subscriber.SessionId);
        }
    }

    /// <summary>近因缓冲条目：revision + 事件名（订阅过滤用）+ 完整事件帧 data（补发直接复用）。</summary>
    private sealed record BufferedEvent(long Revision, string Name, JObject Data);

    private sealed class EventSubscriber
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _events = new(StringComparer.Ordinal);
        private bool _subscribeAll;

        public EventSubscriber(Guid sessionId, InstanceConnection connection)
        {
            SessionId = sessionId;
            Connection = connection;
        }

        public Guid SessionId { get; }

        public InstanceConnection Connection { get; }

        /// <summary>空列表 = 订阅全部已知事件。</summary>
        public void AddEvents(IReadOnlyList<string> events)
        {
            lock (_lock)
            {
                if (events.Count == 0)
                {
                    _subscribeAll = true;
                    _events.Clear();
                    return;
                }

                foreach (var name in events)
                {
                    _events.Add(name);
                }
            }
        }

        /// <summary>返回 false 表示该订阅者已无任何兴趣，调用方应注销会话。</summary>
        public bool RemoveEvents(IReadOnlyList<string> events)
        {
            lock (_lock)
            {
                if (events.Count == 0)
                {
                    _subscribeAll = false;
                    _events.Clear();
                    return false;
                }

                if (_subscribeAll)
                {
                    // 从"全部"退订若干：退化为"全部减去退订项"
                    _subscribeAll = false;
                    _events.Clear();
                    foreach (var name in ExternalInterfaceEventNames.All)
                    {
                        _events.Add(name);
                    }
                }

                foreach (var name in events)
                {
                    _events.Remove(name);
                }

                return _subscribeAll || _events.Count > 0;
            }
        }

        public bool IsSubscribedTo(string eventName)
        {
            lock (_lock)
            {
                return _subscribeAll || _events.Contains(eventName);
            }
        }
    }
}
