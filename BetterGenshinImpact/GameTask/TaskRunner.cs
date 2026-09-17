using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

using BetterGenshinImpact.View;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Helpers;
using Wpf.Ui.Violeta.Controls;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.ViewModel;
using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 用于以独立任务的方式执行任意方法
/// </summary>
public class TaskRunner
{
    private readonly ILogger<TaskRunner> _logger = App.GetLogger<TaskRunner>();

    // private readonly DispatcherTimerOperationEnum _timerOperation = DispatcherTimerOperationEnum.None;

    public TaskRunner()
    {
    }

    // public TaskRunner(DispatcherTimerOperationEnum timerOperation)
    // {
    //     _timerOperation = timerOperation;
    // }
    
    /// <summary>
    /// 加锁并独立运行任务
    /// </summary>
    /// <param name="action"></param>
    /// <param name="resetCancellationContext">任务开始时是否重建 CancellationContext。</param>
    /// <param name="clearCancellationContextOnLockFailure">获取信号量锁失败时是否清理 CancellationContext。</param>
    /// <param name="job">[A2] 作业描述符：非空时登记进 JobRegistry 并随执行推进状态；null = 不登记（旧行为）。</param>
    /// <returns>[A1.1] 显式执行结果：RejectedSlotBusy = 槽位被占本次未执行（旧行为仅 ERR 日志静默返回）。</returns>
    public async Task<TaskRunResult> RunCurrentAsync(Func<Task> action, bool resetCancellationContext = true, bool clearCancellationContextOnLockFailure = false, string? soloTaskName = null, JobDescriptor? job = null)
    {
        if (job?.ParentJobId is { } parent && ExecutionScope.Current?.Descriptor.JobId != parent)
            return TaskRunResult.RejectedSlotBusy;
        if (job == null && ExecutionScope.Current?.Descriptor.Kind == JobKind.OneDragon)
            job = new JobDescriptor(JobKind.Solo, soloTaskName ?? "一条龙前置或收尾",
                JobSource.OneDragonInternal, ParentJobId: ExecutionScope.Current.Descriptor.JobId);
        // [A2 统一注册表] 登记点在抢锁之前：连"被拒"也是注册表里的一条事实（Rejected 终态），不再静默丢失。
        // 观察性故障不影响执行：登记/推进全部容错留痕。
        // [A2.4] job.JobId 非空 = 认领协调器入队时已建的 Queued 作业（taskHandle==jobId 别名）；
        // 认领失败（注册表登记被容错跳过等）退化新建，日志留痕。
        BgiJob? registeredJob = null;
        if (job != null)
        {
            try
            {
                if (job.JobId is { } adoptId)
                {
                    registeredJob = JobRegistry.Instance.Query(adoptId);
                    if (registeredJob == null)
                    {
                        _logger.LogWarning("[JobRegistry] 认领作业不存在（退化新建）: jobId={JobId} name={Name}", adoptId, job.Name);
                        registeredJob = JobRegistry.Instance.Submit(job.Kind, job.Name, job.Source, job.Generation, job.IdempotencyKey, job.ParentJobId, job.JobId, job.ExecutionIdentity).Job;
                    }
                }
                else
                {
                    var submission = JobRegistry.Instance.Submit(job.Kind, job.Name, job.Source, job.Generation, job.IdempotencyKey, job.ParentJobId, identity: job.ExecutionIdentity);
                    if (submission.Adopted) return TaskRunResult.RejectedSlotBusy;
                    registeredJob = submission.Job;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JobRegistry] 作业登记失败（不影响执行）: {Name}", job.Name);
            }
        }

        // A duplicate may refer to somebody else's running/terminal attempt. Never execute
        // it again or overwrite its outcome with this caller's admission failure.
        if (registeredJob != null && (registeredJob.IsTerminal ||
            registeredJob.State != JobState.Queued && ExecutionScope.Current?.Descriptor.JobId != registeredJob.JobId))
            return TaskRunResult.RejectedSlotBusy;
        ExecutionScope? ownedScope = null;
        try
        {
            if (ExecutionScope.Current == null)
                ownedScope = ExecutionScope.Start(job ?? new JobDescriptor(JobKind.Solo, soloTaskName ?? "未命名", JobSource.Ui));
        }
        catch (InvalidOperationException ex)
        {
            if (registeredJob != null)
                TryRegistryTerminal(registeredJob.JobId, JobState.Rejected, JobErrorCodes.TaskBusy, ex.Message, false);
            return TaskRunResult.RejectedSlotBusy;
        }
        using var rootLifetime = ownedScope;
        var scope = ExecutionScope.Current!;
        // 加锁
        var hasLock = await TaskSemaphore.WaitAsync(0);
        if (!hasLock)
        {
            _logger.LogError("任务启动失败：当前存在正在运行中的独立任务，请不要重复执行任务！");
            if (registeredJob != null)
            {
                TryRegistryTerminal(registeredJob.JobId, JobState.Rejected, JobErrorCodes.TaskBusy, "任务槽位被占用，未执行", false);
            }
            if (clearCancellationContextOnLockFailure)
            {
                CancellationContext.Instance.Clear();
            }
            return TaskRunResult.RejectedSlotBusy;
        }
        // The logical root owns admission across all of its leaf tasks.
        if (!scope.IsCurrentOwner)
        {
            TaskSemaphore.Release();
            var stopped = scope.Result == TaskRunResult.Preempted ? TaskRunResult.Preempted : TaskRunResult.Cancelled;
            if (registeredJob != null)
                TryRegistryTerminal(registeredJob.JobId, JobState.Cancelled,
                    stopped == TaskRunResult.Preempted ? JobErrorCodes.Preempted : JobErrorCodes.CancelledUser, null, true);
            return stopped;
        }
        if (registeredJob != null)
        {
            TryRegistryTransition(registeredJob.JobId, JobState.Running);
        }
        // 独立任务身份：拿锁成功后立即写入，保证 ext 观察器 200ms 边沿（task.started）与
        // task.status 查询都能读到任务名；finally 中随任务结束清空。
        // 配置组/一条龙等路径不传 soloTaskName（默认 null），行为不变。
        RunnerContext.Instance.SoloTaskName = soloTaskName;
        // [ext事件直挂] task.started 逐次必达：观察器 200ms 边沿会漏掉连续条目的快速切换，
        // 由引擎直接发布（理由详见 ExternalInterfaceEventHub.PublishTaskStarted 注释）
        BetterGenshinImpact.Service.ExternalInterface.ExternalInterfaceEventHub.Instance.PublishTaskStarted(soloTaskName);
        var faulted = false; // [A2] catch(Exception) 命中标记，供注册表终态判定
        var failuresBefore = scope.FailureCount;
        var interrupted = false;
        var wasCancelled = false;
        try
        {
            _logger.LogInformation("→ {Text}", string.IsNullOrEmpty(soloTaskName) ? "任务启动！" : soloTaskName + "，任务启动！");
            
            // 初始化
            Init();
            if (resetCancellationContext)
            {
                CancellationContext.Instance.Set();
            }
            RunnerContext.Instance.Clear();
            await action();
        }
        catch (NormalEndException e)
        {
            Notify.Event(NotificationEvent.TaskCancel).Success("任务手动取消，或正常结束");
            _logger.LogInformation("任务中断:{Msg}", e.Message);
            if (RunnerContext.Instance.IsContinuousRunGroup)
            {
                // 连续执行时，抛出异常，终止执行
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            Notify.Event(NotificationEvent.TaskCancel).Success("任务被手动取消");
            _logger.LogInformation("任务中断:{Msg}", "任务被取消");
            if (RunnerContext.Instance.IsContinuousRunGroup)
            {
                // 连续执行时，抛出异常，终止执行
                throw;
            }
        }
        catch (Exception e)
        {
            faulted = true;
            scope.Observe(TaskRunResult.Failed);
            Notify.Event(NotificationEvent.TaskError).Error("任务执行异常", e);
            _logger.LogError(e.Message);
            _logger.LogDebug(e.StackTrace);
        }
        finally
        {
            // 任务是否被取消需在 CancellationContext.Clear() 之前捕获（Clear 后 IsDisposed=true 不可读）
            try
            {
                var cancellationContext = CancellationContext.Instance;
                wasCancelled = !cancellationContext.IsDisposed && cancellationContext.WasCancelled;
            }
            catch
            {
                // 读取失败按未取消处理，不影响收尾
            }

            faulted |= scope.FailureCount > failuresBefore;
            wasCancelled |= interrupted || !scope.IsCurrentOwner;
            if (wasCancelled && scope.Result != TaskRunResult.Preempted) scope.Observe(TaskRunResult.Cancelled);
            try { End(); }
            catch (Exception ex)
            {
                faulted = true;
                scope.Observe(TaskRunResult.Failed);
                _logger.LogError(ex, "任务清理失败，仍释放本任务槽位");
            }
            _logger.LogInformation("→ {Text}", string.IsNullOrEmpty(soloTaskName) ? "任务结束" : soloTaskName + "，任务结束");

            // [传送标记] 任务结束 = 位置上下文结束：清空快速传送"上次成功传送地图"标记，
            // 下一次任务首传走切区（保守），避免跨任务陈旧标记误跳过（teleport-fastdrag-skip-last-successful-map spec）。
            TpTaskFastDrag.ResetLastSuccessfulTeleportMap();

            CancellationContext.Instance.Clear();
            // 独立任务身份随任务结束清空（Clear() 不动此字段，避免 :72 启动处 Clear() 误清刚写入的名字）
            RunnerContext.Instance.SoloTaskName = null;
            RunnerContext.Instance.Clear();

            // [A2 统一注册表] 终态登记先于槽位释放/事件发布（总计划 §6.3：终态登记先于终态事件）。
            // 判定依据 wasCancelled/faulted 两个事实源；NormalEndException 的"手动取消或正常结束"
            // 固有歧义不在此消解（与 task.stopped(wasCancelled) 的既有口径一致）。
            if (registeredJob != null)
            {
                TryRegistryTerminal(registeredJob.JobId,
                    faulted ? JobState.Failed : wasCancelled ? JobState.Cancelled : JobState.Succeeded,
                    faulted ? JobErrorCodes.TaskStartFailed : wasCancelled ? (scope.Result == TaskRunResult.Preempted ? JobErrorCodes.Preempted : scope.StopReason) : null,
                    null, wasCancelled);
            }

            // 释放锁
            if (hasLock)
            {
                TaskSemaphore.Release();
                // [切片7·唯一挂载点] 槽位释放全局信号：只读 fire-and-forget，零订阅者零扇出，单机零感知。
                // 协调器/助手 settle 判定统一靠它；手动任务结束同样触发（spec §4.3 有意设计）。
                BetterGenshinImpact.Service.ExternalInterface.ExternalInterfaceEventHub.Instance.PublishTaskSlotReleased();
                // [ext事件直挂] task.stopped 逐次必达（与 task.started 同款理由，见 PublishTaskStarted 注释）
                BetterGenshinImpact.Service.ExternalInterface.ExternalInterfaceEventHub.Instance.PublishTaskStopped(wasCancelled);
            }
        }

        return faulted ? TaskRunResult.Failed : wasCancelled ? scope.Result : TaskRunResult.Ran;
    }

    public void FireAndForget(Func<Task> action)
    {
        Task.Run(() => RunCurrentAsync(action));
    }

    public async Task<TaskRunResult> RunThreadAsync(Func<Task> action, string? soloTaskName = null, JobDescriptor? job = null)
    {
        return await Task.Run(() => RunCurrentAsync(action, soloTaskName: soloTaskName, job: job));
    }

    public async Task RunSoloTaskAsync(ISoloTask soloTask, JobSource source = JobSource.Ui)
    {
        ExecutionScope? owned = null;
        try
        {
            if (ExecutionScope.Current == null)
                owned = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, soloTask.Name, source));
        }
        catch (InvalidOperationException ex) { _logger.LogWarning(ex, "独立任务未获执行权: {Name}", soloTask.Name); return; }
        using var root = owned;
        ExecutionScope.Current!.SetCheckpoint(new SuspendContextCapture.Snapshot(
            "solo", null, 0, null, soloTask.Name, 0, null, null,
            soloTask.Name == BetterGenshinImpact.GameTask.AutoOnline.NotifyOnlineTask.TaskName));
        // 启动等待之前先进行取消操作的初始化，便于在任务开始前终止任务.
        CancellationContext.Instance.Set();

        // 没启动的时候先启动
        bool waitForMainUi = soloTask.Name != "自动七圣召唤" && !soloTask.Name.Contains("自动音游") &&
                             !soloTask.Name.Contains("幽境危战");
        await ScriptService.StartGameTask(waitForMainUi);
        if (CancellationContext.Instance.IsCancellationRequested)
        {
            _logger.LogInformation("独立任务在启动阶段被取消: {Name}", soloTask.Name);
            CancellationContext.Instance.Clear();
            return;
        }
        
        await Task.Run(() => RunCurrentAsync(
            async () => await soloTask.Start(CancellationContext.Instance.Cts.Token),
            resetCancellationContext: false,
            clearCancellationContextOnLockFailure: true,
            soloTaskName: soloTask.Name,
            job: new JobDescriptor(JobKind.Solo, soloTask.Name, source)));
    }

    /// <summary>[A2] 注册表状态推进的容错留痕包装：观察性故障绝不影响任务执行（纪律 §3）。</summary>
    private void TryRegistryTransition(Guid jobId, JobState state)
    {
        try
        {
            if (state == JobState.Running)
            {
                JobRegistry.Instance.TryMarkRunning(jobId);
            }
            else
            {
                JobRegistry.Instance.TryMarkCancelling(jobId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JobRegistry] 作业状态推进失败（不影响执行）: {JobId} → {State}", jobId, state);
        }
    }

    /// <summary>[A2] 注册表终态登记的容错留痕包装。</summary>
    private void TryRegistryTerminal(Guid jobId, JobState terminal, string? errorCode, string? errorMessage, bool wasCancelled)
    {
        try
        {
            JobRegistry.Instance.TryMarkTerminal(jobId, terminal, errorCode, errorMessage, wasCancelled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JobRegistry] 作业终态登记失败（不影响执行）: {JobId} → {State}", jobId, terminal);
        }
    }

    public void Init()
    {
        if (!TaskContext.Instance().IsInitialized)
        {
            UIDispatcherHelper.Invoke(() => { Toast.Warning("请先在启动页，启动截图器再使用本功能"); });
            throw new InvalidOperationException("请先在启动页，启动截图器再使用本功能");
        }

        // [输入状态安全] 任务启动前释放所有残留按键。
        // 助手触发命令时，游戏角色可能正在赶路/鼓舞（W/Shift/空格等长按），按键可能残留按下状态。
        // 若不在新任务开始前释放，残留按键会导致新任务角色持续移动或行为异常。
        // 与 End() 结尾的 ReleaseAllKey() 对称，保证任务以干净的输入状态进入。
        Simulation.ReleaseAllKey();

        // 清空实时任务触发器
        TaskTriggerDispatcher.Instance().ClearTriggers();
        
        // 隐藏地图遮罩
        UIDispatcherHelper.Invoke(() =>
        {
            if (MaskWindow.InstanceNullable() != null)
            {
                if (MaskWindow.Instance().DataContext is MaskWindowViewModel vm)
                {
                    vm.IsInBigMapUi = false;
                }
            }
        });
        VisionContext.Instance().DrawContent.ClearAll(); 
        
        // 激活原神窗口
        var maskWindow = MaskWindow.Instance();
        SystemControl.ActivateWindow();
        maskWindow.Invoke(maskWindow.Show);
    }

    public void End()
    {
        if (!TaskContext.Instance().IsInitialized)
        {
            return;
        }

        Simulation.ReleaseAllKey();

        // 还原实时任务触发器
        TaskTriggerDispatcher.Instance().ClearTriggers();
        TaskTriggerDispatcher.Instance().SetTriggers(GameTaskManager.LoadInitialTriggers());

        VisionContext.Instance().DrawContent.ClearAll();
        HtmlMaskWindow.CloseAll();
    }

}
