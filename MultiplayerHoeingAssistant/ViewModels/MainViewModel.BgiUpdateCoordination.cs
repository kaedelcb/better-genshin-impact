using System.Diagnostics;
using System.IO;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 同机更新协作的参与方（每个助手都运行，执行端与监控端相同）：
/// 准备：记下本会话 BGI 是否在运行，暂停守护并关闭本会话 BGI；
/// 完成：重新启动原来在运行的 BGI，到约定时间后重启本助手；
/// 失败或准备超时：只恢复原来在运行的 BGI。
/// </summary>
public partial class MainViewModel
{
    private System.Threading.Timer? _updateCoordinationTimer;
    private string? _handledCoordinationId;
    private string? _preparingCoordinationId;
    private bool _bgiRunningBeforeCoordination;
    private bool _guardHeldBeforeCoordination;
    private int _coordinationTick;

    private void StartBgiUpdateCoordinationWatcher()
    {
        // 助手刚因上次更新而重启时，协作文件仍是“完成”，必须视为已处理，否则会反复重启。
        // 正在“准备”中的协作照常参与（新启动的助手同样需要关闭自己的 BGI）。
        try
        {
            var existing = BgiUpdateCoordination.Read(ResolveConfiguredBgiDirectory());
            if (existing is { Phase: not BgiUpdateCoordination.Preparing }) _handledCoordinationId = existing.Id;
        }
        catch { }
        _updateCoordinationTimer ??= new System.Threading.Timer(_ => CheckBgiUpdateCoordination(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    }

    private void StopBgiUpdateCoordinationWatcher()
    {
        _updateCoordinationTimer?.Dispose();
        _updateCoordinationTimer = null;
    }

    private void CheckBgiUpdateCoordination()
    {
        if (_disposing || Interlocked.Exchange(ref _coordinationTick, 1) == 1) return;
        try
        {
            var bgiDir = ResolveConfiguredBgiDirectory();
            if (string.IsNullOrEmpty(bgiDir)) return;
            var state = BgiUpdateCoordination.Read(bgiDir);
            if (state is null || state.Id == _handledCoordinationId) return;

            if (state.Phase == BgiUpdateCoordination.Preparing)
            {
                if (state.ExpiresUtc <= DateTimeOffset.UtcNow)
                {
                    // 发起方没有写入结果就消失了：恢复，不重启。
                    if (_preparingCoordinationId == state.Id) FinishCoordination(state.Id, restartAssistant: false, null);
                    else _handledCoordinationId = state.Id;
                    return;
                }
                if (_preparingCoordinationId == state.Id) return;
                _preparingCoordinationId = state.Id;
                var processes = BgiProcessMonitor.GetCurrentSessionBgiProcesses();
                _bgiRunningBeforeCoordination = processes.Length > 0;
                AddLog(_bgiRunningBeforeCoordination
                    ? "[更新BGI] 本机正在更新 BGI，关闭本会话的 BGI 并暂停守护"
                    : "[更新BGI] 本机正在更新 BGI，暂停守护（本会话 BGI 未运行）");
                _guardHeldBeforeCoordination = _processMonitor?.IsAutomaticRestartHeld == true;
                _processMonitor?.HoldAutomaticRestartForUserStop();
                foreach (var process in processes)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) { AddLog("[更新BGI] 关闭本会话 BGI 失败：" + ex.Message); }
                    process.Dispose();
                }
                return;
            }

            if (state.Phase is BgiUpdateCoordination.Done or BgiUpdateCoordination.Failed)
            {
                // 准备阶段结束前就已启动的助手没有参与关闭，不擅自改动它的 BGI，只在完成时重启助手本身。
                if (_preparingCoordinationId != state.Id) _bgiRunningBeforeCoordination = false;
                var restart = state.Phase == BgiUpdateCoordination.Done && state.RestartAssistants;
                FinishCoordination(state.Id, restart, state.RestartAfterUtc);
            }
        }
        catch (Exception ex) { AddLog("[更新BGI] 协作检查异常（已忽略）：" + ex.Message); }
        finally { Interlocked.Exchange(ref _coordinationTick, 0); }
    }

    private void FinishCoordination(string id, bool restartAssistant, DateTimeOffset? restartAfterUtc)
    {
        var wasPreparing = _preparingCoordinationId == id;
        _handledCoordinationId = id;
        _preparingCoordinationId = null;
        var startBgi = _bgiRunningBeforeCoordination;
        _bgiRunningBeforeCoordination = false;
        // 只撤销本次协作自己加上的守护暂停；用户原先的“停止后不自动拉起”保持不变。
        if (wasPreparing && !_guardHeldBeforeCoordination) _processMonitor?.ResumeAutomaticRestartForUserStart();
        if (restartAssistant)
        {
            var delay = restartAfterUtc is { } at && at > DateTimeOffset.UtcNow ? at - DateTimeOffset.UtcNow : TimeSpan.Zero;
            AddLog("[更新BGI] 更新完成" + (startBgi ? "，将重新启动 BGI 并重启助手" : "，将重启助手"));
            RelaunchAfterBgiUpdate(startBgi, delay);
        }
        else if (startBgi)
        {
            AddLog("[更新BGI] 更新未完成，恢复本会话的 BGI");
            StartCoordinatedBgi();
        }
    }

    /// <summary>重新启动本会话 BGI：执行端经进程监控启动；监控端没有进程监控，直接按配置路径启动。</summary>
    private bool StartCoordinatedBgi()
    {
        if (_processMonitor is { } monitor) return monitor.RestartBgi();
        var exe = _config?.BgiPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return false;
        try
        {
            return BgiPackageUpdateService.TryLaunchWhileNotInstalling(exe, () =>
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose());
        }
        catch (Exception ex) { AddLog("[更新BGI] 启动 BGI 失败：" + ex.Message); return false; }
    }
}
