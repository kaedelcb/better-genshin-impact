using System.Collections.Concurrent;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [ev2 2026-09-25] RecordTerminal 两侧分裂的确定性表征＋观测边界固化（R5.3 §24.83-84）。
///
/// 表征对象：BgiTaskCoordinator.RecordTerminal（BgiTaskCoordinator.cs:259 起）——
/// 先写队列 _terminals（RecordQueueObservation），再尝试写统一注册表（TryRegistryTerminal），
/// 两步不原子；普通 Task&lt;bool&gt; 路径的 Executor 只表达取消（true=取消、false=未取消），
/// 生产执行体（漏斗/RunOpAsync）可在返回 false 前先写 job 终态（确定失败/拒绝）。
/// 先终态者赢 ⇒ 协调器随后的 Succeeded/Cancelled 写入被拒 ⇒
/// <b>队列报 completed、注册表保留执行体终态——两侧分裂</b>（§24.84「执行体先记失败／拒绝，
/// 协调器随后按 false 记队列 completed，再尝试记 job 成功但被『先终态者赢』拒绝」）。
///
/// <b>观测边界冻结（本文件的存在意义）</b>：以下夹具钉住的是 §24.83-84 登记的<b>现存</b>可观测行为，
/// 包括分裂面本身——这不是对分裂的背书，而是让任何改变（含「顺手修复」I8 族）都必须：
/// ①先过 §24.84 候选合同批的会诊（改语义按 §17.4-A 复会诊并再生声明面清单）；
/// ②由该合同批同步更新本文件。禁止在无会诊的情况下让本文件从红变绿或从绿变红。
///
/// <b>同步纪律</b>：RecordTerminal 的写入顺序＝队列先、注册表后；各终态路径的事件发布在两步写入
/// 之后同步执行（Publish 为注入委托、同步入队）。故所有夹具以<b>终态事件</b>为唯一等待锚点——
/// 事件可见 ⇒ 两步写入均已完成 ⇒ 其后的队列/注册表读数无窗口竞态。
///
/// <b>边界局限（如实）</b>：两步写入之间的瞬态窗口（队列已写、注册表未写）无生产接缝、
/// 不可确定性钉住——尝试记录：RecordQueueObservation 与 TryRegistryTerminal 是同一私有方法内
/// 的相邻同步调用，无拦截点（类 sealed、方法 private）；本文件只钉最终态两侧的可观测面。
///
/// <b>夹具编号对照（FIX-n → 测试方法）</b>：
/// FIX-1＝Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome（Theory 两例）；
/// FIX-2＝Split_NormalPath_ExecutorPreWritesRejected_ReturnsTrue_QueueCompleted_JobRejectedNotCancelled；
/// FIX-3＝SameSource_NormalPath_NoPreWrittenTerminal_QueueCompleted_JobSucceeded；
/// FIX-4＝SameSource_NormalPath_ExecutorPreWritesSucceeded_QueueCompleted_JobSucceeded；
/// FIX-5＝SameSource_NormalPath_ExecutorCancelled_QueueCompletedCancelled_JobCancelledWithUserReason；
/// FIX-6＝Fallback_ExecutorThrows_QueueFailedTaskStartFailed_JobFailedSameCode；
/// FIX-7＝Fallback_QueueCancelWhilePending_QueueQueueCancelled_JobCancelledUser；
/// FIX-8＝Fallback_SlotWaitTimeout_QueueFailedTaskBusy_JobFailedTaskBusy；
/// FIX-9＝Fallback_QueueFull_RejectionLivesOnlyInRegistry_NoQueueObservation。
///
/// <b>反向突变验证（R5，日志 _ev2/ev2_mutation_log.md；与本日志/_apply_mutation.py 三方一致）</b>：
/// M1（执行完成路径队列状态改写）守护 FIX-1/2/3/4/5 的队列侧 completed 断言；
/// M2（RecordTerminal 注册表兜底写入拆除）守护 FIX-3/5/6/7/8 的注册表侧断言；
/// M3（completed+cancelled 的注册表映射丢弃取消区分）守护 FIX-5；
/// M4（queueCancelled 的注册表映射改写为成功）守护 FIX-7；
/// M5（failed 的注册表映射丢弃真实错误码）守护 FIX-8；
/// M6（JobRegistry.TryMarkTerminal 摘除已终态拒绝守卫＝方向反转）守护 FIX-1×2/FIX-2 的注册表侧断言；
/// M7（Submit 队列满路径注册表写入拆除）守护 FIX-9。
/// 全部真实执行：红 → 还原（git diff 0 差异）→ 复绿。（第 8 轮建议 4：头注补 M6/M7，
/// 纯注释变更，不改测试行为。）
/// </summary>
[Collection("TaskTakeoverIncident")]
public class BgiTaskCoordinatorTerminalSplitCharacterizationTests
{
    /// <summary>事件记录：(事件名, taskHandle 字符串, errorCode, cancelled)。</summary>
    private sealed record EventRecord(string Name, string? TaskHandle, string? ErrorCode, bool Cancelled);

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentQueue<EventRecord> _events = new();

        /// <summary>槽位是否空闲（测试可控；false 时协调器项停在等锁/在队）。</summary>
        public bool SlotFree;

        public BgiTaskCoordinator Coordinator { get; }

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

        /// <summary>提交 Generation>0 的普通 Task&lt;bool&gt; 路径任务（v2 形状；不投影注册表结果）。</summary>
        public BgiTaskCoordinator.SubmitResult SubmitNormal(
            string name, Func<Guid, CancellationToken, Task<bool>> executor, int generation = 5)
            => Coordinator.Submit(new BgiTaskCoordinator.TaskSubmission(generation, name, null, 0, executor));

        /// <summary>轮询等待条件成立（超时由调用方对返回值断言）。</summary>
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

    /// <summary>等待指定句柄的指定终态事件出现（事件是 RecordTerminal 两步写入之后的同步发布点）。
    /// 返回值与等待谓词同过滤（含 errorCode），避免同句柄同名多事件时读到非预期记录。</summary>
    private static EventRecord WaitForTerminalEvent(
        Harness h, string eventName, Guid handle, string? errorCode = null)
    {
        Assert.True(Harness.WaitFor(() => h.Events.Any(e =>
            e.Name == eventName
            && e.TaskHandle == handle.ToString("N")
            && (errorCode == null || e.ErrorCode == errorCode))),
            $"未等到终态事件 {eventName}（handle={handle:N}）");
        return h.Events.First(e =>
            e.Name == eventName
            && e.TaskHandle == handle.ToString("N")
            && (errorCode == null || e.ErrorCode == errorCode));
    }

    // ===== 分裂面（§24.84 登记的核心事实：队列 completed 与注册表执行体终态并存）=====

    [Theory]
    [InlineData(JobState.Failed, JobErrorCodes.PrerequisiteFailed)]
    [InlineData(JobState.Rejected, JobErrorCodes.TaskBusy)]
    public void Split_NormalPath_ExecutorPreWritesFailure_QueueReportsCompleted_JobKeepsExecutorOutcome(
        JobState outcome, string code)
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("分裂表征组", (handle, _) =>
        {
            // 前置自证：派发点已推进 Running（非终态），执行体的先终态写入必须成功——
            // 若协调器时序改变导致此处失败，说明前置条件破坏，夹具响亮失败而非静默失真。
            Assert.True(JobRegistry.Instance.TryMarkTerminal(handle, outcome, code, "执行体先记终态", false),
                "执行体先终态写入失败：派发点未先把作业推进到非终态");
            // 漏斗/RunOpAsync 语义（§24.84）：确定失败/拒绝写 job 后返回 false（false 只表达未取消）
            return Task.FromResult(false);
        });

        var completed = WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskCompleted, submitted.TaskHandle);
        Assert.False(completed.Cancelled);

        // 队列侧观察面（ext.task.queueStatus 唯一事实源）：false ⇒ completed，无错误码
        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("completed", queue.Status);
        Assert.False(queue.Cancelled);
        Assert.Null(queue.ErrorCode);

        // 注册表侧观察面：先终态者赢 ⇒ 执行体的 Failed/Rejected 保留，协调器的 Succeeded 写入被拒
        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(outcome, snapshot.State);
        Assert.Equal(code, snapshot.ErrorCode);

        // 分裂成立的两个可观测面同时为真（本夹具钉住的 §24.84 现状；修复属 §24.84 候选合同批）
    }

    [Fact]
    public void Split_NormalPath_ExecutorPreWritesRejected_ReturnsTrue_QueueCompleted_JobRejectedNotCancelled()
    {
        // 分裂的取消变体：执行体先记 Rejected（非取消语义）后返回 true（被取消）——
        // 队列按 completed+cancelled 登记，注册表保留 Rejected：两侧对「发生了什么」给出不同答案。
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("分裂取消变体组", (handle, _) =>
        {
            Assert.True(JobRegistry.Instance.TryMarkTerminal(
                handle, JobState.Rejected, JobErrorCodes.TaskBusy, "执行体先记拒绝", false));
            return Task.FromResult(true);
        });

        var completed = WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskCompleted, submitted.TaskHandle);
        Assert.True(completed.Cancelled);

        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("completed", queue.Status);
        Assert.True(queue.Cancelled);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Rejected, snapshot.State);
        Assert.Equal(JobErrorCodes.TaskBusy, snapshot.ErrorCode);
        Assert.False(snapshot.WasCancelled); // 注册表的取消标记属于先写者（执行体），协调器写入被拒
    }

    // ===== 同源面（无先写终态或同向先写时，两侧终态一致——与分裂面互为对照）=====

    [Fact]
    public void SameSource_NormalPath_NoPreWrittenTerminal_QueueCompleted_JobSucceeded()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("同源无先写组", (_, _) => Task.FromResult(false));

        WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskCompleted, submitted.TaskHandle);

        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("completed", queue.Status);
        Assert.False(queue.Cancelled);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Succeeded, snapshot.State);
        Assert.Null(snapshot.ErrorCode);
    }

    [Fact]
    public void SameSource_NormalPath_ExecutorPreWritesSucceeded_QueueCompleted_JobSucceeded()
    {
        // 先写终态与协调器写入同向（Succeeded）时不分裂：先终态者赢，但两侧终态一致。
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("同源同向先写组", (handle, _) =>
        {
            Assert.True(JobRegistry.Instance.TryMarkTerminal(
                handle, JobState.Succeeded, errorMessage: "switched_and_verified"));
            return Task.FromResult(false);
        });

        WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskCompleted, submitted.TaskHandle);

        Assert.Equal("completed", h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status);
        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Succeeded, snapshot.State);
    }

    [Fact]
    public void SameSource_NormalPath_ExecutorCancelled_QueueCompletedCancelled_JobCancelledWithUserReason()
    {
        // cancelled=true 的映射契约：队列 completed+Cancelled=true；注册表 Cancelled(cancelled_user)。
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("同源取消组", (_, _) => Task.FromResult(true));

        var completed = WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskCompleted, submitted.TaskHandle);
        Assert.True(completed.Cancelled);

        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("completed", queue.Status);
        Assert.True(queue.Cancelled);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Cancelled, snapshot.State);
        Assert.Equal(JobErrorCodes.CancelledUser, snapshot.ErrorCode);
        Assert.True(snapshot.WasCancelled);
    }

    // ===== 兜底面（漏斗不可达路径由 RecordTerminal 兜底注册表写入——A2.4 注释声明的合同）=====

    [Fact]
    public void Fallback_ExecutorThrows_QueueFailedTaskStartFailed_JobFailedSameCode()
    {
        using var h = new Harness(slotFree: true);
        var submitted = h.SubmitNormal("异常兜底组", (_, _) =>
            Task.FromException<bool>(new InvalidOperationException("执行段炸了")));

        var failed = WaitForTerminalEvent(
            h, ExternalInterfaceEventNames.TaskFailed, submitted.TaskHandle, "task_start_failed");

        Assert.Equal("task_start_failed", failed.ErrorCode);
        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("failed", queue.Status);
        Assert.Equal("task_start_failed", queue.ErrorCode);
        Assert.Equal("执行段炸了", queue.Message);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Equal(JobErrorCodes.TaskStartFailed, snapshot.ErrorCode);
    }

    [Fact]
    public void Fallback_QueueCancelWhilePending_QueueQueueCancelled_JobCancelledUser()
    {
        // slotWaitTimeout 拉长到 30s（与 FIX-9 同构）：本用例语义上不需要短超时，
        // 依赖「WaitFor(pending) → CancelByHandle」在 pump 等槽窗口内完成——若继承 5s 缺省，
        // 测试线程被冻结 >5s 时 pump 先按 task_busy 超时记终态，CancelByHandle 转 NotFound，
        // 一次环境性冻结会伪装成取消路径断言失败（第 3 轮重要项 3 处置）。
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromSeconds(30));
        var submitted = h.SubmitNormal("排队取消组", (_, _) => Task.FromResult(false), generation: 0);
        Assert.True(Harness.WaitFor(() => h.Coordinator.QueryItemStatus(submitted.TaskHandle).Status == "pending"));

        Assert.Equal(BgiTaskCoordinator.CancelOutcome.CancelledQueued, h.Coordinator.CancelByHandle(submitted.TaskHandle));

        WaitForTerminalEvent(h, ExternalInterfaceEventNames.TaskQueueCancelled, submitted.TaskHandle);

        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("queueCancelled", queue.Status);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Cancelled, snapshot.State);
        Assert.Equal(JobErrorCodes.CancelledUser, snapshot.ErrorCode);
    }

    [Fact]
    public void Fallback_SlotWaitTimeout_QueueFailedTaskBusy_JobFailedTaskBusy()
    {
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromMilliseconds(200));
        var submitted = h.SubmitNormal("等槽超时组", (_, _) => Task.FromResult(false), generation: 0);

        var failed = WaitForTerminalEvent(
            h, ExternalInterfaceEventNames.TaskFailed, submitted.TaskHandle, JobErrorCodes.TaskBusy);
        Assert.Equal(JobErrorCodes.TaskBusy, failed.ErrorCode);

        var queue = h.Coordinator.QueryItemStatus(submitted.TaskHandle);
        Assert.Equal("failed", queue.Status);
        Assert.Equal(JobErrorCodes.TaskBusy, queue.ErrorCode);

        var snapshot = JobRegistry.Instance.QueryOutcomeSnapshot(submitted.TaskHandle);
        Assert.NotNull(snapshot);
        Assert.Equal(JobState.Failed, snapshot.State);
        Assert.Equal(JobErrorCodes.TaskBusy, snapshot.ErrorCode);
    }

    [Fact]
    public void Fallback_QueueFull_RejectionLivesOnlyInRegistry_NoQueueObservation()
    {
        // 队列满路径不产生任何队列观察面条目（句柄未返回给调用方，RecordTerminal 未被调用）：
        // Rejected(queue_full) 只存在于注册表——观测边界现状，§24.83「排队」行登记的事实之一。
        // slotWaitTimeout 拉长到 30s：占位项在用例期间绝不因等槽超时移出 _pending
        // （若占位项中途超时被移出，第 9 个提交会变成 Queued 而非 QueueFull）。
        using var h = new Harness(slotFree: false, slotWaitTimeout: TimeSpan.FromSeconds(30));
        try
        {
            var uniqueName = $"队列满第九个-{Guid.NewGuid():N}";
            for (var i = 0; i < BgiTaskCoordinator.QueueCapacity; i++)
            {
                var r = h.SubmitNormal($"占位组{i}", (_, _) => Task.FromResult(false), generation: 0);
                Assert.Equal(BgiTaskCoordinator.SubmitStatus.Queued, r.Status);
            }

            var eventsBefore = h.Events.Count;
            var rejected = h.SubmitNormal(uniqueName, (_, _) => Task.FromResult(false), generation: 0);
            Assert.Equal(BgiTaskCoordinator.SubmitStatus.QueueFull, rejected.Status);
            Assert.Equal(Guid.Empty, rejected.TaskHandle);

            // 队列侧：句柄未返回 ⇒ 无可查询对象；也不发任何事件（仅日志留痕）
            Assert.Equal(eventsBefore, h.Events.Count);

            // 注册表侧：Rejected(queue_full) 唯一留痕
            Assert.True(Harness.WaitFor(() =>
                JobRegistry.Instance.Snapshot().Any(j => j.Name == uniqueName && j.IsTerminal)));
            var job = JobRegistry.Instance.Snapshot().Single(j => j.Name == uniqueName);
            Assert.Equal(JobState.Rejected, job.State);
            Assert.Equal(JobErrorCodes.QueueFull, job.ErrorCode);
        }
        finally
        {
            // 反向污染收敛：清掉占位在队项（Queued 非终态作业不会被注册表淘汰、会跨用例滞留单例）；
            // 清队使其转为 Cancelled 终态进入可淘汰 FIFO。finally 保证断言中途失败也必须清理，
            // 不把非终态泄漏叠加在原始失败之上。
            h.Coordinator.ClearQueue();
        }
    }
}
