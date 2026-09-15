using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;

/// <summary>Owns only this fight's cancellation; remote skip never cancels route recovery.</summary>
public sealed class CooperativeFightScope : IDisposable
{
    private readonly Func<bool> _shouldStop;
    private readonly CancellationTokenSource _fight;
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly Task _monitor;
    private int _interrupted;
    public string PointId { get; }
    public CancellationToken Token => _fight.Token;
    public bool IsInterrupted => Volatile.Read(ref _interrupted) != 0;

    public CooperativeFightScope(string pointId, Func<bool> shouldStop, CancellationToken routeToken)
    {
        PointId = pointId;
        _shouldStop = shouldStop;
        _fight = CancellationTokenSource.CreateLinkedTokenSource(routeToken);
        _monitor = MonitorAsync();
    }

    public bool CheckStop()
    {
        if (_shouldStop() && Interlocked.Exchange(ref _interrupted, 1) == 0)
            _fight.Cancel();
        return IsInterrupted;
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (!_monitorStop.IsCancellationRequested && !_fight.IsCancellationRequested)
            {
                if (CheckStop()) break;
                await Task.Delay(50, _monitorStop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 监视任务只负责"该不该停战"：判据读取（快照/标记）一旦异常，绝不能让它变成
            // 上层 finally 里的清理异常（那会把一次跳过升级成线路失败）。按"不停战"降级。
        }
    }

    public async Task StopMonitoringAsync()
    {
        _monitorStop.Cancel();
        try { await _monitor.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 同上：清理路径不得因监视任务异常而失败 */ }
    }

    public void Dispose()
    {
        _monitorStop.Cancel();
        try { _monitor.GetAwaiter().GetResult(); }
        catch (Exception) { /* 同上：Dispose 不得抛出 */ }
        _monitorStop.Dispose();
        _fight.Dispose();
    }
}

public static class CooperativeExecutionDecisions
{
    /// <summary>
    /// 协作线路的终态判定（生产唯一入口，RouteExecutionEngine 调用）。
    /// 优先级：取消 &gt; 失败 &gt; 完整（且无本地不完整标记、未被请求跳线）&gt; 不完整。
    /// 单机/无协作上下文由调用方短路为 None，不经过本函数。
    /// </summary>
    public static RerunRouteOutcome Outcome(bool completed, bool incomplete, bool failed, bool cancelled,
        bool skipRouteRequested = false)
        => cancelled ? RerunRouteOutcome.Cancelled
            : failed ? RerunRouteOutcome.Failed
            : completed && !incomplete && !skipRouteRequested ? RerunRouteOutcome.Completed
            : RerunRouteOutcome.Incomplete;

    public static bool ShouldReportExperience(bool replay, bool interrupted, bool hasExp)
        => !interrupted && (!replay || hasExp);
}
