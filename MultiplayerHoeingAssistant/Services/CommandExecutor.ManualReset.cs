using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    private readonly SemaphoreSlim _manualResetGate = new(1, 1);
    private readonly object _resetStateGate = new();
    private int _resetVersion;
    private int _resetInProgress;

    internal Func<Task<bool>>? ResetProcessOverrideForTest { get; set; }
    internal Func<int, Task<IpcResponse?>>? ResetStatusQueryOverrideForTest { get; set; }
    internal TimeSpan? ResetReadyTimeoutForTest { get; set; }

    private bool IsManualResetActive => Volatile.Read(ref _resetInProgress) != 0;
    private bool WasResetSuperseded(int version)
        => IsManualResetActive || version != Volatile.Read(ref _resetVersion)
           || _commandStopVersion.Value is { } stopVersion && stopVersion != Volatile.Read(ref _allStopVersion);

    private static CommandResult ResetInProgress() => new()
    {
        ErrorCode = "reset_in_progress", Message = "正在重置执行环境，完成后可重新启动联机任务"
    };

    private static CommandResult ResetSuperseded() => new()
    {
        Status = "cancelled", ErrorCode = "reset_superseded",
        Message = "旧接管或恢复已被状态重置撤销，未向新执行环境发送旧操作"
    };

    private async Task<CommandResult> ForceResetExecutionCoreAsync(Action<string>? log)
    {
        // 人工重置独立于普通接管/恢复串行锁；旧操作卡住也不能挡住物理复位。
        if (!await _manualResetGate.WaitAsync(0).ConfigureAwait(false)) return ResetInProgress();
        try
        {
            int stopVersion;
            BgiEpoch? oldEpoch;
            lock (_resetStateGate)
            {
                Interlocked.Exchange(ref _resetInProgress, 1);
                Interlocked.Increment(ref _resetVersion);
                stopVersion = Interlocked.Increment(ref _allStopVersion);
                oldEpoch = _takeoverEpoch ?? _externalClientProvider?.Invoke()?.ServerEpoch;
                Interlocked.Exchange(ref _resumeRetryInFlight, 0);
            }

            if (_monitor is null && ResetProcessOverrideForTest is null)
                return new() { ErrorCode = "monitor_unavailable", Message = "BGI 进程监控器不可用，未执行全局重置" };

            _monitor?.HoldAutomaticRestartForUserStop();
            log?.Invoke("[状态重置] 正在撤销旧启动与恢复，重建本会话 BGI 执行环境");
            var restarted = await (ResetProcessOverrideForTest?.Invoke()
                ?? _monitor.RestartBgiControlledAsync(null, "状态重置全局复位")).ConfigureAwait(false);
            if (!restarted)
                return new() { ErrorCode = "bgi_reset_failed", Message = "BGI 未能完成受控重建，未确认旧执行已退出" };

            // 受控重建已经确认旧进程退出。只在这个物理边界之后废弃旧票据。
            lock (_resetStateGate)
            {
                _takeoverTicket = null;
                _takeoverEpoch = null;
                if (_externalClientProvider?.Invoke() is { } ext) ext.TakeoverTicket = null;
            }

            // 直接等可信任务快照；不再先串行等待 config.list，再追加一轮空闲轮询。
            var timeout = ResetReadyTimeoutForTest ?? TimeSpan.FromSeconds(30);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (stopVersion != Volatile.Read(ref _allStopVersion))
                    return new() { Status = "cancelled", ErrorCode = "cancelled_user", Message = "重置期间收到新的全停止，未恢复启动状态" };
                var remaining = timeout - clock.Elapsed;
                var connectTimeout = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 1000);
                try
                {
                    var response = await (ResetStatusQueryOverrideForTest?.Invoke(connectTimeout)
                        ?? SendIpcPreferredAsync("task.status", null, connectTimeout))
                        .WaitAsync(remaining).ConfigureAwait(false);
                    if (stopVersion != Volatile.Read(ref _allStopVersion))
                        return new() { Status = "cancelled", ErrorCode = "cancelled_user", Message = "重置期间收到新的全停止，未恢复启动状态" };
                    if (response is { Success: true } && IsManualResetReady(response.Data, oldEpoch)
                        && IsResetLinkReady(response.Data))
                    {
                        log?.Invoke("[状态重置] 新 BGI 已就绪，任务槽、待执行队列和旧接管现场均已清空");
                        return new() { Status = "success", ErrorCode = "global_reset_complete",
                            Message = "本机会话全部 BGI 任务已停止、队列与旧恢复现场已清空，可以重新启动联机" };
                    }
                }
                catch (TimeoutException) when (clock.Elapsed >= timeout) { break; }
                catch (Exception) { /* 冷启动期间的断流仍由同一个就绪期限覆盖。 */ }

                remaining = timeout - clock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250))
                        .ConfigureAwait(false);
            }

            return new() { ErrorCode = "bgi_reset_not_ready", Message = "BGI 已重建，但尚未确认新执行环境就绪且全部队列、接管现场为空" };
        }
        catch (Exception ex)
        {
            return new() { ErrorCode = "bgi_reset_exception", Message = "全局复位异常：" + ex.Message };
        }
        finally
        {
            Interlocked.Exchange(ref _resetInProgress, 0);
            _manualResetGate.Release();
        }
    }

    internal static bool IsManualResetReady(string? json, BgiEpoch? oldEpoch)
        => TryParseEpoch(json, out var processId, out var ticks) && processId > 0 && ticks > 0
           && (oldEpoch is null || oldEpoch.ProcessId != processId || oldEpoch.StartTicksUtc != ticks)
           && IsExecutionIdle(json) && IsAllStopConfirmed(json);

    private bool IsResetLinkReady(string? json)
    {
        if (_externalClientProvider is null) return true;
        var ext = _externalClientProvider();
        return ext is { State: BgiExternalLinkState.Ready, ServerEpoch: { } epoch }
               && TryParseEpoch(json, out var processId, out var ticks)
               && epoch.ProcessId == processId && epoch.StartTicksUtc == ticks;
    }
}
