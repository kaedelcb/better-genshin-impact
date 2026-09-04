using System.Collections.Concurrent;
using BetterGenshinImpact.Service.ExternalInterface;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [切片7] BgiTaskCoordinator 队列语义单测：入队 / 去重(adopted / already_executed) /
/// 取消 / 派发顺序 / 背压 queue_full / 等槽超时 task_busy。
/// 协调器的槽位判定、事件发布、等锁节奏全部构造注入，测试不触碰 TaskSemaphore/Dispatcher。
/// </summary>
public class BgiTaskCoordinatorTests
{
    /// <summary>事件记录：(事件名, taskHandle 字符串, errorCode, cancelled)。</summary>
    private sealed record EventRecord(string Name, string? TaskHandle, string? ErrorCode, bool Cancelled);

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<EventRecord> _events = new();

        /// <summary>槽位是否空闲（测试可控；false 时协调器项停在等锁/在队）。</summary>
        public bool SlotFree;

        public BgiTaskCoordinator Coordinator { get; }

        /// <summary>执行委托被调用的顺序（按 submission name 记录）。</summary>
        public ConcurrentQueue<string?> ExecutedOrder { get; } = new();

        public Harness(bool slotFree, TimeSpan? slotWaitTimeout = null)
        {
            SlotFree = slotFree;
            Coordinator = new BgiTaskCoordinator(
                isSlotFree: () => SlotFree,
                publish: CaptureEvent,
                logger: NullLogger.Instance,
                slotPollInterval: TimeSpan.FromMilliseconds(10),
                slotWaitTimeout: slotWaitTimeout ?? TimeSpan.FromSeconds(5));
        }

        private void CaptureEvent(string name, object? payload)
        {
            string? handle = null;
            string? errorCode = null;
            var cancelled = false;
            if (payload != null)
            {
                var type = payload.GetType();
                handle = type.GetProperty("taskHandle")?.GetValue(payload)?.ToString();
                errorCode = type.GetProperty("errorCode")?.GetValue(payload)?.ToString();
                cancelled = type.GetProperty("cancelled")?.GetValue(payload) is true;
            }

            _events.Enqueue(new EventRecord(name, handle, errorCode, cancelled));
        }

        public List<EventRecord> Events => _events.ToList();

        /// <summary>提交一个任务；执行委托记录调用顺序并立即完成（未取消）。</summary>
        public BgiTaskCoordinator.SubmitResult Submit(string? groupName, int generation)
            => Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
                generation,
                groupName,
                null,
                0,
                _ =>
                {
                    ExecutedOrder.Enqueue(groupName);
                    return Task.FromResult(false);
                }));

        /// <summary>轮询等待条件成立（超时断言失败由调用方处理返回值）。</summary>
        public static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return condition();
        }

        public void Dispose() => Coordinator.Dispose();
    }

    [Fact]
    public void Submit_EnqueuesImmediately_AndPublishesLifecycleInOrder()
    {
        using var h = new Harness(slotFree: true);
        var result = h.Submit("组A", generation: 1);

        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, result.Status);
        Assert.NotEqual(Guid.Empty, result.TaskHandle);
        Assert.Equal(1, result.QueuePosition);

        Assert.True(Harness.WaitFor(() => h.ExecutedOrder.Count == 1));
        Assert.True(Harness.WaitFor(() => h.Events.Any(e => e.Name == ExternalInterfaceEventNames.TaskCompleted)));

        var names = h.Events.Select(e => e.Name).ToList();
        var queuedIdx = names.IndexOf(ExternalInterfaceEventNames.TaskQueued);
        var startedIdx = names.IndexOf(ExternalInterfaceEventNames.TaskStarted);
        var completedIdx = names.IndexOf(ExternalInterfaceEventNames.TaskCompleted);
        Assert.True(queuedIdx >= 0 && startedIdx > queuedIdx && completedIdx > startedIdx,
            $"事件顺序应为 queued→started→completed，实际: {string.Join(",", names)}");

        // 全部事件带同一句柄
        var handle = result.TaskHandle.ToString("N");
        Assert.All(h.Events, e => Assert.Equal(handle, e.TaskHandle));
        Assert.False(h.Events.First(e => e.Name == ExternalInterfaceEventNames.TaskCompleted).Cancelled);
    }

    [Fact]
    public void Submit_Dedup_SameGenerationAndNameInQueue_ReturnsAdopted()
    {
        using var h = new Harness(slotFree: false); // 槽位占用，项停在队中
        var first = h.Submit("组A", generation: 5);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, first.Status);

        var dup = h.Submit("组A", generation: 5);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Adopted, dup.Status);
        Assert.Equal(first.TaskHandle, dup.TaskHandle); // 采用既有句柄

        // 同 generation 不同配置组允许并存（OnAllReady 依次执行多个配置组场景）
        var other = h.Submit("组B", generation: 5);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, other.Status);

        // adopted 不重复发 task.queued
        Assert.Equal(2, h.Events.Count(e => e.Name == ExternalInterfaceEventNames.TaskQueued));
        Assert.Equal(2, h.Coordinator.QueueDepth);
    }

    [Fact]
    public void Submit_Dedup_SameGenerationAndNameCompleted_ReturnsAlreadyExecuted()
    {
        using var h = new Harness(slotFree: true);
        var first = h.Submit("组A", generation: 7);
        Assert.True(Harness.WaitFor(() => h.ExecutedOrder.Count == 1));
        Assert.True(Harness.WaitFor(() =>
            h.Events.Any(e => e.Name == ExternalInterfaceEventNames.TaskCompleted)));

        var dup = h.Submit("组A", generation: 7);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.AlreadyExecuted, dup.Status);
        Assert.Equal(1, h.ExecutedOrder.Count); // 不重复执行
    }

    [Fact]
    public void CancelByHandle_QueuedItem_NotExecuted_AndPublishesQueueCancelledOnce()
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Submit("组A", generation: 0);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, submitted.Status);

        var outcome = h.Coordinator.CancelByHandle(submitted.TaskHandle);
        Assert.Equal(BgiTaskCoordinator.CancelOutcome.CancelledQueued, outcome);
        Assert.Equal(0, h.Coordinator.QueueDepth);

        // 未知句柄 / 重复取消 → NotFound
        Assert.Equal(BgiTaskCoordinator.CancelOutcome.NotFound, h.Coordinator.CancelByHandle(submitted.TaskHandle));
        Assert.Equal(BgiTaskCoordinator.CancelOutcome.NotFound, h.Coordinator.CancelByHandle(Guid.NewGuid()));

        // 槽位释放后该项也不得执行
        h.SlotFree = true;
        Thread.Sleep(200); // 给 pump 留出 drain 时间
        Assert.Empty(h.ExecutedOrder);

        // task.queueCancelled 恰好一次（取消路径与 pump 跳过路径不双发）
        Assert.Equal(1, h.Events.Count(e => e.Name == ExternalInterfaceEventNames.TaskQueueCancelled));
    }

    [Fact]
    public void Pump_DispatchesInFifoOrder_AfterSlotFreed()
    {
        using var h = new Harness(slotFree: false);
        var a = h.Submit("组A", generation: 0);
        var b = h.Submit("组B", generation: 0);
        Assert.Equal(1, a.QueuePosition);
        Assert.Equal(2, b.QueuePosition);

        h.SlotFree = true;
        Assert.True(Harness.WaitFor(() => h.ExecutedOrder.Count == 2));

        Assert.Equal(new string?[] { "组A", "组B" }, h.ExecutedOrder.ToArray());

        // started/completed 严格交替：A 完成后才派发 B（串行派发）
        var lifecycle = h.Events
            .Where(e => e.Name is ExternalInterfaceEventNames.TaskStarted or ExternalInterfaceEventNames.TaskCompleted)
            .Select(e => (e.Name, e.TaskHandle))
            .ToList();
        Assert.Equal(
            [
                (ExternalInterfaceEventNames.TaskStarted, a.TaskHandle.ToString("N")),
                (ExternalInterfaceEventNames.TaskCompleted, a.TaskHandle.ToString("N")),
                (ExternalInterfaceEventNames.TaskStarted, b.TaskHandle.ToString("N")),
                (ExternalInterfaceEventNames.TaskCompleted, b.TaskHandle.ToString("N")),
            ],
            lifecycle);
    }

    [Fact]
    public void Submit_WhenQueueFull_ReturnsQueueFull_WithoutBlocking()
    {
        using var h = new Harness(slotFree: false);
        for (var i = 0; i < BgiTaskCoordinator.QueueCapacity; i++)
        {
            var r = h.Submit($"组{i}", generation: 0);
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, r.Status);
        }

        var overflow = h.Submit("组溢出", generation: 0);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.QueueFull, overflow.Status);
        Assert.Equal(BgiTaskCoordinator.QueueCapacity, h.Coordinator.QueueDepth);
    }

    [Fact]
    public void Pump_SlotWaitTimeout_PublishesTaskFailedBusy()
    {
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromMilliseconds(300));
        var submitted = h.Submit("组A", generation: 0);

        Assert.True(Harness.WaitFor(() =>
            h.Events.Any(e => e.Name == ExternalInterfaceEventNames.TaskFailed)));
        var failed = h.Events.First(e => e.Name == ExternalInterfaceEventNames.TaskFailed);
        Assert.Equal("task_busy", failed.ErrorCode);
        Assert.Equal(submitted.TaskHandle.ToString("N"), failed.TaskHandle);
        Assert.Empty(h.ExecutedOrder); // 等锁超时不得执行
    }

    [Fact]
    public void ClearQueue_CancelsAllQueuedItems_WithEvents()
    {
        using var h = new Harness(slotFree: false);
        h.Submit("组A", generation: 0);
        h.Submit("组B", generation: 0);
        h.Submit("组C", generation: 0);
        Assert.Equal(3, h.Coordinator.QueueDepth);

        var cleared = h.Coordinator.ClearQueue();
        Assert.Equal(3, cleared);
        Assert.Equal(0, h.Coordinator.QueueDepth);
        Assert.Equal(3, h.Events.Count(e => e.Name == ExternalInterfaceEventNames.TaskQueueCancelled));

        h.SlotFree = true;
        Thread.Sleep(200);
        Assert.Empty(h.ExecutedOrder);
    }
}
