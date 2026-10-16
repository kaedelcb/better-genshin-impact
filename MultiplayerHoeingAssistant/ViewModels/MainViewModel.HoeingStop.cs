using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    // A normal stop owns this admission interval until its IPC result returns.
    // Otherwise a stop observed while idle could catch a newly admitted batch.
    private int _ordinaryStopInProgress;

    internal event Action? StopAllAutomationRequested;
    private readonly Dictionary<string, Task<CommandResult>> _handledAllStops = new(StringComparer.Ordinal);

    private static bool IsAllStop(RemoteCommand command) => command.Cmd == "stop"
        && command.Params?.GetValueOrDefault("stopScope")?.ToString() == "all";

    private async Task ExecuteUserStopAsync(List<string>? targets, bool broadcastAll = false)
    {
        var self = _config?.PlayerUid ?? "";
        var selected = (targets ?? []).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        var stop = new RemoteCommand
        {
            Cmd = "stop", Sender = _config?.PlayerName ?? "", SenderUid = self,
            CommandId = "stop_" + Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            Params = new() { ["stopScope"] = "all" }
        };
        // Start locally before the first network await. The broadcast echo shares this command ID.
        var local = IsExecutorMode && (broadcastAll || selected.Count == 0 || selected.Contains("*") || selected.Contains(self))
            ? StopLocalAsync() : Task.CompletedTask;
        try
        {
            var remote = IsObserverMode ? selected : selected.Where(t => t != self).ToList();
            if (broadcastAll) remote = ["*"];
            if (remote.Count > 0)
            {
                stop.Target = remote;
                if (_signalRClient is { IsConnected: true } client)
                {
                    await client.SendRemoteCommandAsync(stop);
                    AddLog("全停止命令已发送，等待目标成员确认；离线成员无法实时接收");
                }
                else AddLog("控制连接不可用，远端停止未发送");
            }
        }
        catch (Exception ex) { AddLog("远端停止发送失败：" + ex.Message); }
        finally { await local; }

        async Task StopLocalAsync()
        {
            var result = await ExecuteCommandWithHoeingStopAsync(stop);
            AddLog("停止结果：" + result.Message);
        }
    }

    private Task<CommandResult> ExecuteAllStopAsync(RemoteCommand command)
    {
        if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return Task.FromResult(new CommandResult { ErrorCode = "request_expired", Message = "停止命令已过期，未执行" });
        TaskCompletionSource<CommandResult> completion;
        lock (_batchGate)
        {
            // The server echo may add an authoritative generation after the local stop has started.
            if (int.TryParse(command.Params?.GetValueOrDefault("hoeingStopGeneration")?.ToString(), out var generation))
                _resetFenceGeneration = Math.Max(_resetFenceGeneration, generation);
            if (!string.IsNullOrEmpty(command.CommandId)
                && _handledAllStops.TryGetValue(command.CommandId, out var existing)) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!string.IsNullOrEmpty(command.CommandId)) _handledAllStops[command.CommandId] = completion.Task;
            if (_handledAllStops.Count > 256)
            {
                var finished = _handledAllStops.FirstOrDefault(p => p.Value.IsCompleted);
                if (finished.Key is not null) _handledAllStops.Remove(finished.Key);
            }
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { completion.TrySetResult(await ExecuteAllStopCoreAsync(command)); }
            catch (Exception ex)
            {
                completion.TrySetResult(new() { ErrorCode = "stop_unconfirmed", Message = "全停止尚未确认：" + ex.Message,
                    ResponsibilityState = ResponsibilityState.Pending });
            }
        }
    }

    private async Task<CommandResult> ExecuteAllStopCoreAsync(RemoteCommand command)
    {
        if (!IsExecutorMode)
            return new() { ErrorCode = "observer_mode", Message = "遥控端不执行本机任务，停止需发送给对应执行端" };
        if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return new() { ErrorCode = "request_expired", Message = "停止命令已过期，未执行" };
        OnlineHoeingBatch? batch;
        lock (_batchGate)
        {
            Interlocked.Increment(ref _ordinaryStopInProgress);
            batch = _activeBatch;
            var stopGeneration = batch?.Generation ?? 0;
            if (int.TryParse(command.Params?.GetValueOrDefault("hoeingStopGeneration")?.ToString(), out var remoteGeneration))
                stopGeneration = Math.Max(stopGeneration, remoteGeneration);
            _resetFenceGeneration = Math.Max(_resetFenceGeneration, stopGeneration);
        }
        try
        {
            _intentLifecycle.OnManualCleared();
            _intentLifecycle.SealBaselineAfterManualClear(int.MaxValue);
            _resumeTimeoutTimer?.Dispose();
            _resumeTimeoutTimer = null;
            // Native cancellation must not wait on arbitration/store or network cleanup.
            var executionStop = GetStopExecutor().ExecuteAsync(command);
            var automationErrors = new List<string>();
            var cancelBatch = batch is null ? Task.CompletedTask : Task.Run(() => batch.Cancel());
            try { StopAllAutomationRequested?.Invoke(); }
            catch (Exception ex) { automationErrors.Add("自动流程撤销失败：" + ex.Message); }
            TaskCenterHost? host;
            lock (_taskCenterHostGate) host = _taskCenterHost;
            if (host is not null)
            {
                try { automationErrors.AddRange(await host.StopAllRunsForUserAsync()); }
                catch (Exception ex) { automationErrors.Add(ex.Message); }
            }
            var result = await executionStop;
            if (await Task.WhenAny(cancelBatch, Task.Delay(2000)) != cancelBatch)
                automationErrors.Add("联机批次取消回调尚未退出");
            else
            {
                try { await cancelBatch; }
                catch (Exception ex) { automationErrors.Add(ex.Message); }
            }
            if (automationErrors.Count > 0)
                return new() { ErrorCode = "automation_stop_pending", Message = result.Message + "；自动流程停止仍待确认："
                    + string.Join("；", automationErrors), ResponsibilityState = ResponsibilityState.Pending };
            ObserveOnlineTask(ReportStatusAsync(), "全停止状态上报");
            return result;
        }
        finally { Interlocked.Decrement(ref _ordinaryStopInProgress); }
    }

    private CommandExecutor GetStopExecutor()
    {
        lock (_batchGate)
        {
            if (_commandExecutor is { } executor) return executor;
            // Stopping remains available when normal initialization could not bind an executor.
            var path = string.IsNullOrWhiteSpace(_config?.BgiPath)
                ? System.IO.Path.Combine(ResolveConfiguredBgiDirectory(), "BetterGI.exe") : _config.BgiPath;
            _processMonitor ??= new BgiProcessMonitor(path, AddLog);
            return _commandExecutor ??= new CommandExecutor(_processMonitor, path, () => _externalClient, AddLog);
        }
    }

    private Dictionary<string, object> FreezeStopTargets(IEnumerable<string> targetUids,
        Dictionary<string, object>? parameters = null)
    {
        var result = parameters is null ? new Dictionary<string, object>() : new(parameters);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var uid in targetUids.Distinct(StringComparer.Ordinal))
        {
            if (uid == _config?.PlayerUid && IsExecutorMode)
            {
                lock (_batchGate)
                    expected[uid] = _activeBatch is { Start: not null } current ? current.BatchId : "";
            }
            else
            {
                var member = Members.FirstOrDefault(m => m.PlayerUid == uid);
                // Last completed results retain their ID for display, not execution authority.
                expected[uid] = member?.HoeingStartPhase is "registering" or "preparing" or "takeover" or "running"
                    or "stopping" or "aborted" or "cleaning" or "cleanup" or "cleanupPending"
                    ? member.HoeingBatchId ?? "" : "";
            }
        }
        result["hoeingBatchIds"] = expected;
        return result;
    }

    private async Task<CommandResult> ExecuteCommandWithHoeingStopAsync(RemoteCommand command)
    {
        if (command.Cmd is ("start_group" or "start_oneclick" or "start_bgi" or "hotkey_execute")
            && _localResetGate.CurrentCount == 0)
            return new CommandResult { ErrorCode = "reset_in_progress", Message = "正在完成状态重置，恢复完成后可重新启动联机" };
        if (IsAllStop(command)) return await ExecuteAllStopAsync(command);
        if (command.Cmd != "stop")
            return _commandExecutor is not null
                ? await _commandExecutor.ExecuteAsync(command)
                : new CommandResult { Message = "本机执行器不可用" };

        object? value = null;
        var hasExactTarget = command.Params?.TryGetValue("hoeingBatchId", out value) == true;
        var expectedBatchId = hasExactTarget ? value?.ToString() ?? "" : "";
        lock (_batchGate)
        {
            if (_activeBatch is { Start: not null } active)
            {
                if (!hasExactTarget || !string.Equals(active.BatchId, expectedBatchId, StringComparison.Ordinal))
                    return new CommandResult
                    {
                        ErrorCode = "hoeing_stop_target_changed",
                        Message = "停止目标已变化，未影响当前联机批次"
                    };
                active.Cancel();
                return new CommandResult
                {
                    Status = "success",
                    Message = "已请求停止指定联机批次，正在确认任务退出并清理",
                    ResponsibilityState = ResponsibilityState.Pending
                };
            }
            if (!string.IsNullOrEmpty(expectedBatchId))
                return new CommandResult
                {
                    ErrorCode = "hoeing_stop_target_changed",
                    Message = "指定联机批次已退出，未停止其他任务"
                };
            _activeBatch?.Cancel(); // Retain legacy local sequence cancellation for its own target.
            Interlocked.Increment(ref _ordinaryStopInProgress);
        }
        try
        {
            return _commandExecutor is not null
                ? await _commandExecutor.ExecuteAsync(command)
                : new CommandResult { Message = "本机执行器不可用" };
        }
        finally { Interlocked.Decrement(ref _ordinaryStopInProgress); }
    }

    private RemoteCommand FreezeLocalStop(RemoteCommand command)
    {
        if (command.Cmd != "stop") return command;
        command.Params = command.Params is null ? new() : new(command.Params);
        lock (_batchGate)
            command.Params["hoeingBatchId"] = _activeBatch is { Start: not null } current ? current.BatchId : "";
        return command;
    }
}
