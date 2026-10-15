using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    // A normal stop owns this admission interval until its IPC result returns.
    // Otherwise a stop observed while idle could catch a newly admitted batch.
    private int _ordinaryStopInProgress;

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
