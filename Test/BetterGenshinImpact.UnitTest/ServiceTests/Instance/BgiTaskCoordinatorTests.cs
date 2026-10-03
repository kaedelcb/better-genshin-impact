using System.Collections.Concurrent;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [切片7] BgiTaskCoordinator 队列语义单测：入队 / 去重(adopted / already_executed) /
/// 取消 / 派发顺序 / 背压 queue_full / 等槽超时 task_busy。
/// 协调器的槽位判定、事件发布、等锁节奏全部构造注入，测试不触碰 TaskSemaphore/Dispatcher。
/// [Collection]：本类经协调器写 JobRegistry.Instance 进程级单例，与 TaskTakeoverIncidentTests、
/// BgiTaskCoordinatorTerminalSplitCharacterizationTests、CoordinatedTaskQueueTests 同挂该非并行集合，
/// 四个单例写者互不重叠（ev2 第 1 轮重要项 3 串行化闭合；第 3 轮重要项 2 v2 扫描补入
/// CoordinatedTaskQueueTests 后由「三个」更正为「四个」，第 9 轮重要项 3 同步本头注）。
/// </summary>
[Collection("TaskTakeoverIncident")]
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
                (_, __) =>
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

    [Theory]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Group)]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Prerequisite)]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Terminal)]
    public void DeliveryExit_RealRootRejectsOldStopVersion_OnlyAfterCoordinatorCleanupConfirmsNeverStarted(
        BetterGenshinImpact.Service.Execution.JobKind kind)
    {
        using var h = new Harness(slotFree: true);
        var previousVersion = BetterGenshinImpact.Service.Execution.ExecutionScope.StopVersionNow;
        BetterGenshinImpact.Service.Execution.ExecutionScope.StopActive(manual: true);
        var bodyCalls = 0;
        var result = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(0, null, null, 0, (handle, _) =>
        {
            using var root = BetterGenshinImpact.Service.Execution.ExecutionScope.Start(new(
                kind, "stale-stop", BetterGenshinImpact.Service.Execution.JobSource.Ext,
                JobId: handle, ExpectedStopVersion: previousVersion));
            Interlocked.Increment(ref bodyCalls);
            return Task.FromResult(false);
        })
        {
            RegistryKind = kind, RegistryName = "stale-stop", ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"), PayloadFingerprint = "stale-stop-test",
            Identity = new(Guid.NewGuid(), "n-1", 0, Occurrence: 0, Attempt: 1),
        });
        Assert.True(Harness.WaitFor(() => BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(result.TaskHandle)?.ExitConfirmed == true));
        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(result.TaskHandle)!;
        Assert.Equal(0, bodyCalls);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Rejected, job.State);
        Assert.Equal("never_started", job.ExitDisposition);
        Assert.True(job.ExecutorEntered);
        Assert.True(job.ExecutorCleanupCompleted);
        Assert.Null(job.DeliveredExecutionInstanceId);
        Assert.False(BetterGenshinImpact.Service.Execution.ExecutionScope.HasActive);
    }

    [Fact]
    public void DeliveryExit_OrdinaryCancellationExceptionIsNotNeverStartedProof()
    {
        using var h = new Harness(slotFree: true);
        var result = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(0, null, null, 0,
            (_, _) => throw new OperationCanceledException("no typed admission proof"))
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "unconfirmed", ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"), PayloadFingerprint = "oce-test",
        });
        Assert.True(Harness.WaitFor(() => BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(result.TaskHandle)?.ExecutorCleanupCompleted == true));
        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(result.TaskHandle)!;
        Assert.False(job.ExitConfirmed);
        Assert.Null(job.ExitDisposition);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.ResultUnknown, job.State);
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
    public void TaskStartQueueUnavailable_ReturnsFailureInsteadOfV2Fallback()
    {
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { groupName = "测试组" });
        var unavailable = new BgiTaskCoordinator.SubmitResult(
            BgiTaskCoordinator.SubmitStatus.Unavailable, Guid.Empty, 0);

        var response = ExternalInterfaceCommandPlane.MapTaskStartQueueResult(request, unavailable, generation: 3);

        Assert.False(response.Success);
        Assert.Equal("queue_unavailable", response.ErrorCode);
        Assert.Contains("未切换到 v2", response.ErrorMessage);
    }

    [Fact]
    public void Submit_AfterDispose_IsUnavailableWithoutExecutionOrEvents()
    {
        using var h = new Harness(slotFree: true);
        h.Coordinator.Dispose();
        var executed = 0;
        var result = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            1,
            "关停后任务",
            null,
            0,
            (_, _) =>
            {
                Interlocked.Increment(ref executed);
                return Task.FromResult(false);
            }));

        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Unavailable, result.Status);
        Assert.Equal(Guid.Empty, result.TaskHandle);
        Assert.Equal(0, executed);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task Dispose_CancelsCurrentExecutorBeforeReturning()
    {
        using var h = new Harness(slotFree: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, "关停时在跑任务", null, 0, async (_, token) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                }

                return true;
            }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Coordinator.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

    // ===== [终态可拉取 2026-09-09] QueryItemStatus：事件推送之外的生命周期拉取安全网 =====

    [Fact]
    public void QueryItemStatus_Lifecycle_PendingThenCompleted()
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Submit("组A", generation: 0);

        // 在队 → pending
        Assert.Equal("pending", h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status);

        // 槽位释放 → 执行完 → completed（终态在事件发布之外可独立拉取）
        h.SlotFree = true;
        Assert.True(Harness.WaitFor(() =>
            h.Events.Any(e => e.Name == ExternalInterfaceEventNames.TaskCompleted)));
        var terminal = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("completed", terminal.Status);
        Assert.False(terminal.Cancelled);
    }

    [Fact]
    public void QueryItemStatus_CancelledWhileQueued_ReturnsQueueCancelled()
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Submit("组A", generation: 0);
        h.Coordinator.CancelByHandle(submitted.TaskHandle);

        Assert.Equal("queueCancelled", h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status);
    }

    [Fact]
    public void QueryItemStatus_SlotWaitTimeout_ReturnsFailedBusy()
    {
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromMilliseconds(300));
        var submitted = h.Submit("组A", generation: 0);

        Assert.True(Harness.WaitFor(() =>
            h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "failed"));
        Assert.Equal("task_busy", h.Coordinator.QueryItemStatus(submitted.TaskHandle).ErrorCode);
    }

    [Fact]
    public void QueryItemStatus_UnknownHandle_ReturnsNotFound()
    {
        using var h = new Harness(slotFree: true);
        Assert.Equal("not_found", h.Coordinator.QueryItemStatus(Guid.NewGuid()).Status);
    }

    [Fact]
    public void QueryItemStatus_CompletedWithCancellation_CancelledFlagPreserved()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, "组A", null, 0, (_, __) => Task.FromResult(true))); // 执行中被取消（F11 语义）

        Assert.True(Harness.WaitFor(() =>
            h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "completed"));
        Assert.True(h.Coordinator.QueryItemStatus(submitted.TaskHandle).Cancelled);
    }

    // ===== [A2.4] JobRegistry 整编：taskHandle==jobId 别名 + 终态同源进注册表 =====
    // 单测中 Executor 是测试 lambda（漏斗 TaskRunner 不参与），协调器是唯一写入者；
    // 生产路径的"漏斗先写、协调器 TryMark 幂等无操作"由 TryMarkTerminal 的已终态返回 false 保证。

    [Fact]
    public void JobRegistry_Submit_RegistersQueuedJobWithTaskHandleAlias()
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Submit("组A", generation: 7);

        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle);
        Assert.NotNull(job);
        Assert.Equal(submitted.TaskHandle, job.JobId); // taskHandle==jobId 同一 Guid 别名
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Queued, job.State);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobSource.Ext, job.Source);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobKind.Group, job.Kind);
        Assert.Equal(7, job.Generation);
        Assert.Equal("组A", job.Name);
    }

    [Theory]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Prerequisite, "prerequisite.account")]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Terminal, "terminal.completionAction")]
    public void JobRegistry_Submit_UsesExplicitOperationKindAndName(
        BetterGenshinImpact.Service.Execution.JobKind kind, string name)
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (_, _) => Task.FromResult(false))
        {
            RegistryKind = kind,
            RegistryName = name,
        });

        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle);
        Assert.NotNull(job);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Queued, job.State);
        Assert.Equal(kind, job.Kind);
        Assert.Equal(name, job.Name);
    }

    [Theory]
    [InlineData(BetterGenshinImpact.Service.Execution.JobState.Rejected, BetterGenshinImpact.Service.Execution.JobErrorCodes.TaskBusy)]
    [InlineData(BetterGenshinImpact.Service.Execution.JobState.Failed, BetterGenshinImpact.Service.Execution.JobErrorCodes.AccountMismatch)]
    public void Prerequisite_DefiniteFailure_IsNotReportedCompleted(
        BetterGenshinImpact.Service.Execution.JobState outcome, string code)
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkTerminal(
                    handle, outcome, code, "确定失败", false);
                return Task.FromResult(false);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "failed"));
        Assert.Equal(code, h.Coordinator.QueryItemStatus(submitted.TaskHandle).ErrorCode);
        Assert.Contains(h.Events, e => e.Name == ExternalInterfaceEventNames.TaskFailed && e.ErrorCode == code);
        Assert.DoesNotContain(h.Events, e => e.Name == ExternalInterfaceEventNames.TaskCompleted);
    }

    [Fact]
    public void Prerequisite_UnknownResult_RemainsNonterminal()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkUnknown(
                    handle, BetterGenshinImpact.Service.Execution.JobErrorCodes.PrerequisiteOutcomeUnknown,
                    "切号结果不明");
                return Task.FromResult(false);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "result_unknown"));
        Assert.DoesNotContain(h.Events, e => e.Name is ExternalInterfaceEventNames.TaskCompleted or ExternalInterfaceEventNames.TaskFailed);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.ResultUnknown,
            BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle)?.State);
        var request = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.JobStatus,
            new { jobId = submitted.TaskHandle.ToString("N") });
        Assert.True(ExternalInterfaceQueryPlane.TryDispatch(null!, null!, request, out var response));
        Assert.Equal("result_unknown", response.Data?["status"]?.ToString());
        Assert.Equal("result_unknown", response.Data?["job"]?["state"]?.ToString());
    }

    [Fact]
    public void Prerequisite_VerifiedSuccess_ReportsCompleted()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkTerminal(
                    handle, BetterGenshinImpact.Service.Execution.JobState.Succeeded,
                    errorMessage: "switched_and_verified");
                return Task.FromResult(false);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "completed"));
        Assert.Contains(h.Events, e => e.Name == ExternalInterfaceEventNames.TaskCompleted);
    }

    [Fact]
    public void Terminal_PrecommitMarker_DoesNotProveCompletion()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkTerminal(
                    handle, BetterGenshinImpact.Service.Execution.JobState.Succeeded,
                    errorMessage: "executed_pre_commit:shutdown");
                return Task.FromResult(false);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Terminal,
            RegistryName = "terminal.completionAction",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "result_unknown"));
        Assert.DoesNotContain(h.Events, e => e.Name == ExternalInterfaceEventNames.TaskCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prerequisite_MissingResultOrEscapedException_RemainsUnknown(bool throws)
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (_, _) => throws
                ? Task.FromException<bool>(new InvalidOperationException("结果未写回"))
                : Task.FromResult(false))
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "result_unknown"));
        Assert.DoesNotContain(h.Events, e => e.Name is ExternalInterfaceEventNames.TaskCompleted or ExternalInterfaceEventNames.TaskFailed);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.ResultUnknown,
            BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle)?.State);
    }

    [Fact]
    public void Prerequisite_ExecutionCancellation_DoesNotClaimCompleted()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, null, null, 0, (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkTerminal(handle,
                    BetterGenshinImpact.Service.Execution.JobState.Cancelled,
                    BetterGenshinImpact.Service.Execution.JobErrorCodes.CancelledUser, wasCancelled: true);
                return Task.FromResult(true);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });

        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "result_unknown"));
        Assert.DoesNotContain(h.Events, e => e.Name == ExternalInterfaceEventNames.TaskCompleted);
    }

    [Fact]
    public void Submit_SameKeyDifferentPayloadWhileQueued_RejectsWithoutReplacingOriginal()
    {
        using var h = new Harness(slotFree: false);
        var key = Guid.NewGuid().ToString("N");
        BgiTaskCoordinator.TaskSubmission Request(string group, string fingerprint) =>
            new(0, group, null, 0, (_, _) => Task.FromResult(false))
            {
                IdempotencyKey = key,
                PayloadFingerprint = fingerprint,
            };

        var first = h.Coordinator.Submit(Request("组A", "payload-a"));
        var same = h.Coordinator.Submit(Request("组A", "payload-a"));
        var collision = h.Coordinator.Submit(Request("组B", "payload-b"));
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, first.Status);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Adopted, same.Status);
        Assert.Equal(first.TaskHandle, same.TaskHandle);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, collision.Status);
        Assert.Equal(Guid.Empty, collision.TaskHandle);
        Assert.Equal("pending", h.Coordinator.QueryItemStatus(first.TaskHandle).Status);
        Assert.Equal(1, h.Coordinator.QueueDepth);
    }

    [Fact]
    public void Submit_UnknownOccupancyInterleave_SameKeyAdoptsDifferentKeyConflicts_NoDuplicateHandle()
    {
        // R5 批次 8 交错夹具（BGI 侧）：槽位被占（未决占用）时——
        // ①同键同载荷 ⇒ Adopted（沿用既有句柄，不新增执行身份、不重复发 task.queued）；
        // ②同键异载荷 ⇒ IdempotencyConflict（不得静默替换原作业，句柄为空）；
        // ③同键缺载荷指纹 ⇒ InvalidSubmission（不得注册）。
        using var h = new Harness(slotFree: false);
        var key = Guid.NewGuid().ToString("N");
        BgiTaskCoordinator.TaskSubmission Request(string group, string? fingerprint) =>
            new(0, group, null, 0, (_, _) => Task.FromResult(false))
            {
                IdempotencyKey = key,
                PayloadFingerprint = fingerprint,
            };

        var first = h.Coordinator.Submit(Request("组A", "payload-a"));
        var adopted = h.Coordinator.Submit(Request("组A", "payload-a"));
        var conflict = h.Coordinator.Submit(Request("组A", "payload-b"));
        var invalid = h.Coordinator.Submit(Request("组A", null));

        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, first.Status);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Adopted, adopted.Status);
        Assert.Equal(first.TaskHandle, adopted.TaskHandle);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal(Guid.Empty, conflict.TaskHandle);
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.InvalidSubmission, invalid.Status);
        Assert.Equal(Guid.Empty, invalid.TaskHandle);

        // 只有一次入队/一个句柄：冲突与非法提交都没有产生第二条执行身份
        Assert.Equal(1, h.Coordinator.QueueDepth);
        Assert.Equal(1, h.Events.Count(e => e.Name == ExternalInterfaceEventNames.TaskQueued));
        Assert.Equal("pending", h.Coordinator.QueryItemStatus(first.TaskHandle).Status);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Submit_KeyWithoutFingerprint_FailsBeforeQueueRegistration(string? fingerprint)
    {
        using var h = new Harness(slotFree: false);
        var executions = 0;
        var result = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, "组A", null, 0, (_, _) =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(false);
            })
        {
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = fingerprint,
        });
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.InvalidSubmission, result.Status);
        Assert.Equal(Guid.Empty, result.TaskHandle);
        Assert.Equal(0, h.Coordinator.QueueDepth);
        Assert.Empty(h.Events);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Submit_SameKeyDifferentPayloadWhileRunning_RejectsAndKeepsOriginal()
    {
        using var h = new Harness(slotFree: true);
        var key = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BgiTaskCoordinator.TaskSubmission Request(string fingerprint) =>
            new(0, "组A", null, 0, async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return false;
            })
            {
                IdempotencyKey = key,
                PayloadFingerprint = fingerprint,
            };

        var first = h.Coordinator.Submit(Request("payload-a"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.Adopted,
                h.Coordinator.Submit(Request("payload-a")).Status);
            var collision = h.Coordinator.Submit(Request("payload-b"));
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, collision.Status);
            Assert.Equal(Guid.Empty, collision.TaskHandle);
            Assert.Equal(first.TaskHandle, h.Coordinator.CurrentTaskHandle);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(first.TaskHandle).Status == "completed"));
    }

    [Fact]
    public void TaskStartFingerprint_CanonicalizesPropertyOrderAndIncludesPayload()
    {
        var a = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { groupName = "组A", generation = 3, idempotencyKey = "k1" });
        var same = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { idempotencyKey = "k2", generation = 3, groupName = "组A" });
        var changed = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { groupName = "组B", generation = 3, idempotencyKey = "k1" });
        var changedStart = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.TaskStart,
            new { groupName = "组A", generation = 3, startFromIndex = 2, idempotencyKey = "k1" });
        var changedOperation = InstanceIpcEnvelope.Request(ExternalInterfaceOperations.PrerequisiteAccount,
            new { groupName = "组A", generation = 3, idempotencyKey = "k1" });
        Assert.Equal(BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(a),
            BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(same));
        Assert.NotEqual(BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(a),
            BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(changed));
        Assert.NotEqual(BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(a),
            BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(changedStart));
        Assert.NotEqual(BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(a),
            BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(changedOperation));

        var response = ExternalInterfaceCommandPlane.MapTaskStartQueueResult(a,
            new BgiTaskCoordinator.SubmitResult(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, Guid.Empty, 0), 3);
        Assert.False(response.Success);
        Assert.Equal("idempotency_conflict", response.ErrorCode);
        var invalid = ExternalInterfaceCommandPlane.MapTaskStartQueueResult(a,
            new BgiTaskCoordinator.SubmitResult(BgiTaskCoordinator.SubmitStatus.InvalidSubmission, Guid.Empty, 0), 3);
        Assert.False(invalid.Success);
        Assert.Equal("invalid_request", invalid.ErrorCode);
    }

    [Theory]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Prerequisite)]
    [InlineData(BetterGenshinImpact.Service.Execution.JobKind.Terminal)]
    public void Projected_UnknownSameKey_AdoptsOriginalWithoutExecutingAgain(
        BetterGenshinImpact.Service.Execution.JobKind kind)
    {
        using var h = new Harness(slotFree: true);
        var key = Guid.NewGuid().ToString("N");
        var executions = 0;
        BgiTaskCoordinator.TaskSubmission Create(string fingerprint = "test-payload") => new(0, null, null, 0, (handle, _) =>
        {
            Interlocked.Increment(ref executions);
            BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkUnknown(handle,
                BetterGenshinImpact.Service.Execution.JobErrorCodes.PrerequisiteOutcomeUnknown, "未确认");
            return Task.FromResult(false);
        })
        {
            RegistryKind = kind,
            RegistryName = kind == BetterGenshinImpact.Service.Execution.JobKind.Terminal
                ? "terminal.completionAction" : "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = key,
            PayloadFingerprint = fingerprint,
        };

        var first = h.Coordinator.Submit(Create());
        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(first.TaskHandle).Status == "result_unknown"));
        var second = h.Coordinator.Submit(Create());
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.Adopted, second.Status);
        Assert.Equal(first.TaskHandle, second.TaskHandle);
        var collision = h.Coordinator.Submit(Create("different-payload"));
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, collision.Status);
        Assert.Equal(Guid.Empty, collision.TaskHandle);
        var crossEntryCollision = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(
            0, "其他配置组", null, 0, (_, _) => Task.FromResult(false))
        {
            IdempotencyKey = key,
            PayloadFingerprint = "ordinary-task-payload",
        });
        Assert.Equal(BgiTaskCoordinator.SubmitStatus.IdempotencyConflict, crossEntryCollision.Status);
        Assert.Equal(Guid.Empty, crossEntryCollision.TaskHandle);
        Assert.Equal("result_unknown", h.Coordinator.QueryItemStatus(first.TaskHandle).Status);
        Assert.Equal(1, executions);
    }

    [Fact]
    public void Prerequisite_UnknownObservation_SurvivesTerminalHistoryEviction()
    {
        using var h = new Harness(slotFree: true);
        var unknown = h.Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(0, null, null, 0,
            (handle, _) =>
            {
                BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TryMarkUnknown(handle,
                    BetterGenshinImpact.Service.Execution.JobErrorCodes.PrerequisiteOutcomeUnknown, "未确认");
                return Task.FromResult(false);
            })
        {
            RegistryKind = BetterGenshinImpact.Service.Execution.JobKind.Prerequisite,
            RegistryName = "prerequisite.account",
            ProjectRegistryOutcome = true,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            PayloadFingerprint = "test-payload",
        });
        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(unknown.TaskHandle).Status == "result_unknown"));
        for (var i = 0; i < 33; i++)
        {
            var ordinary = h.Submit("完成历史" + i, generation: 0);
            Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(ordinary.TaskHandle).Status == "completed"));
        }
        Assert.Equal("result_unknown", h.Coordinator.QueryItemStatus(unknown.TaskHandle).Status);
    }

    [Fact]
    public void JobRegistry_QueueCancelled_MarkedTerminalInRegistry()
    {
        using var h = new Harness(slotFree: false);
        var submitted = h.Submit("组A", generation: 0);
        h.Coordinator.CancelByHandle(submitted.TaskHandle);

        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle);
        Assert.NotNull(job);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Cancelled, job.State);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobErrorCodes.CancelledUser, job.ErrorCode);
    }

    [Fact]
    public void JobRegistry_CompletedExecution_MarkedSucceededInRegistry()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.Submit("组A", generation: 0);

        Assert.True(Harness.WaitFor(() =>
            h.Events.Any(e => e.Name == ExternalInterfaceEventNames.TaskCompleted)));
        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle);
        Assert.NotNull(job);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Succeeded, job.State);
        Assert.NotNull(job.StartedAtUtc); // 派发点 Running 推进已落地
        Assert.NotNull(job.FinishedAtUtc);
    }

    [Fact]
    public void JobRegistry_SlotWaitTimeout_MarkedFailedBusyInRegistry()
    {
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromMilliseconds(300));
        var submitted = h.Submit("组A", generation: 0);

        Assert.True(Harness.WaitFor(() =>
            h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "failed"));
        var job = BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(submitted.TaskHandle);
        Assert.NotNull(job);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobState.Failed, job.State);
        Assert.Equal(BetterGenshinImpact.Service.Execution.JobErrorCodes.TaskBusy, job.ErrorCode);
    }
}
