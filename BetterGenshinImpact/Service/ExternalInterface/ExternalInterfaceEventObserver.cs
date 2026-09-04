using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoOnline;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// 事件源轮询观察器（过渡实现，§3.6 桥接表）：
/// 只读 TaskSemaphore.CurrentCount 做 0↔1 边沿检测（与 task.start 等锁同款姿势，绝不 WaitAsync）、
/// lock(AutoHoeingProgress.Sync) 读锄地进度（不新增写）、NotifyOnlineTask 代序号边沿。
/// 只在存在事件订阅者时运行（ExternalInterfaceEventHub 控制生命周期），
/// 单机（无订阅者）零后台负载（A1）。远期可换引擎内真事件钩子，接口不变。
/// </summary>
internal sealed class ExternalInterfaceEventObserver
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HoeingPushThrottle = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cts;

    // 边沿检测基线。仅观察器线程访问，无需加锁；
    // 读取内部单例时保持与 HandleTaskStatus 相同的防御姿势（try/catch、IsDisposed 在前）。
    private bool _lastRunning;
    private string _lastProjectKey = string.Empty;
    private string? _lastRouteDisplay;
    private int _lastOnlineGeneration;
    private DateTime _lastHoeingPushUtc = DateTime.MinValue;

    public ExternalInterfaceEventObserver()
    {
        try
        {
            _logger = App.GetService<ILogger<ExternalInterfaceEventObserver>>() ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;
        }
        catch
        {
            _logger = NullLogger.Instance;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _cts is not null;
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // 启动时同步基线：不补发"订阅之前"已发生的上线代序号（与助手轮询边沿语义一致）
            try
            {
                _lastOnlineGeneration = NotifyOnlineTask.CurrentGeneration;
                _lastRunning = TaskControl.TaskSemaphore.CurrentCount == 0;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "ext.event 观察器启动基线读取失败，按默认值继续");
            }

            _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception exception)
                {
                    // 单轮异常绝不杀死观察器（内部单例可能处于半初始化态等瞬态故障）
                    _logger.LogDebug(exception, "ext.event 观察器单轮检测失败");
                }

                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（最后一个订阅者退订）
        }
    }

    private void Tick()
    {
        var hub = ExternalInterfaceEventHub.Instance;
        var now = DateTime.UtcNow;

        // 1. 任务运行边沿：CurrentCount 1→0 = task.started，0→1 = task.stopped
        var running = TaskControl.TaskSemaphore.CurrentCount == 0;
        if (running != _lastRunning)
        {
            _lastRunning = running;
            if (running)
            {
                var (taskName, groupName) = ReadCurrentTask();
                hub.Publish(
                    ExternalInterfaceEventNames.TaskStarted,
                    new { taskName, groupName });
                _lastProjectKey = string.Empty;
            }
            else
            {
                var wasCancelled = ReadWasCancelled();
                hub.Publish(
                    ExternalInterfaceEventNames.TaskStopped,
                    new { wasCancelled });
            }
        }

        // 2. 任务进度：当前脚本项目信息变化时推送
        if (running)
        {
            var (projectName, groupName, projectKey) = ReadCurrentProject();
            if (!string.IsNullOrEmpty(projectKey) && projectKey != _lastProjectKey)
            {
                _lastProjectKey = projectKey;
                hub.Publish(
                    ExternalInterfaceEventNames.TaskProgress,
                    new { groupName, projectName });
            }
        }

        // 3. 锄地进度：currentRouteDisplay 变化且 1s 节流
        var routeDisplay = ReadCurrentRouteDisplay();
        if (!string.Equals(routeDisplay, _lastRouteDisplay, StringComparison.Ordinal)
            && (now - _lastHoeingPushUtc) >= HoeingPushThrottle)
        {
            _lastRouteDisplay = routeDisplay;
            if (!string.IsNullOrEmpty(routeDisplay))
            {
                _lastHoeingPushUtc = now;
                hub.Publish(
                    ExternalInterfaceEventNames.HoeingProgress,
                    new { currentRouteDisplay = routeDisplay });
            }
        }

        // 4. 联机锄地上线：generation 边沿 + 30s 近因窗口（与 HandleTaskStatus 口径一致）
        var generation = NotifyOnlineTask.CurrentGeneration;
        if (generation > _lastOnlineGeneration
            && NotifyOnlineTask.LastTriggeredAt != DateTime.MinValue
            && (now - NotifyOnlineTask.LastTriggeredAt).TotalSeconds
                < ExternalInterfaceProtocol.OnlineRecentWindowSeconds)
        {
            _lastOnlineGeneration = generation;
            hub.Publish(
                ExternalInterfaceEventNames.OnlineTriggered,
                new { generation, triggeredAt = NotifyOnlineTask.LastTriggeredAt });
        }
    }

    private static (string? taskName, string? groupName) ReadCurrentTask()
    {
        try
        {
            var ctx = BetterGenshinImpact.GameTask.RunnerContext.Instance;
            if (ctx?.taskProgress != null)
            {
                return (
                    ctx.taskProgress.CurrentScriptGroupProjectInfo?.Name,
                    ctx.taskProgress.CurrentScriptGroupName);
            }

            return (BetterGenshinImpact.GameTask.TaskContext.Instance()?.CurrentScriptProject?.Name, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? projectName, string? groupName, string projectKey) ReadCurrentProject()
    {
        try
        {
            var ctx = BetterGenshinImpact.GameTask.RunnerContext.Instance;
            var info = ctx?.taskProgress?.CurrentScriptGroupProjectInfo;
            if (info is null)
            {
                return (null, null, string.Empty);
            }

            var groupName = ctx!.taskProgress!.CurrentScriptGroupName;
            return (info.Name, groupName, $"{groupName}|{info.Index}|{info.Name}");
        }
        catch
        {
            return (null, null, string.Empty);
        }
    }

    private static string? ReadCurrentRouteDisplay()
    {
        try
        {
            lock (AutoHoeingProgress.Sync)
            {
                if (!AutoHoeingProgress.IsRunning || AutoHoeingProgress.TotalRoutes <= 0)
                {
                    return null;
                }

                return $"第{AutoHoeingProgress.CurrentRouteIndex}/{AutoHoeingProgress.TotalRoutes}条线路: {AutoHoeingProgress.RouteFileName}";
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadWasCancelled()
    {
        try
        {
            var cancellationContext = BetterGenshinImpact.Core.Script.CancellationContext.Instance;
            return !cancellationContext.IsDisposed && cancellationContext.WasCancelled;
        }
        catch
        {
            return false;
        }
    }
}
