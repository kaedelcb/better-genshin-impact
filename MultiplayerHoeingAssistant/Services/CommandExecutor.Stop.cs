using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    private int _allStopVersion;
    private readonly AsyncLocal<int?> _commandStopVersion = new();
    internal Func<string, Task<bool>>? StopProcessOverrideForTest { get; set; }

    private void ThrowIfStartStopped()
    {
        if (_commandStopVersion.Value is { } version && version != Volatile.Read(ref _allStopVersion))
            throw new OperationCanceledException("启动请求已被用户全停止撤销");
    }

    // A stop request is an action, never a status-dependent conflict policy.
    private async Task<CommandResult> StopAllExecutionAsync()
    {
        Interlocked.Increment(ref _allStopVersion);
        _monitor.HoldAutomaticRestartForUserStop();
        Log("[全停止] 正在取消当前执行、清空队列并撤销恢复现场");
        var sent = false;
        try
        {
            var ext = _externalClientProvider?.Invoke();
            if (ext is { State: BgiExternalLinkState.Ready })
            {
                var response = await ext.SendCommandAsync("ext.task.stop",
                    new { stopScope = "all", clearQueue = true }, TimeSpan.FromSeconds(3));
                sent = response.Success;
            }
        }
        catch (Exception ex) { Log("[全停止] 外部通道未确认，继续本机停止：" + ex.Message); }

        if (!sent)
        {
            try
            {
                using var ipc = new IpcClient(System.IO.Path.GetDirectoryName(_bgiPath));
                await ipc.ConnectAsync(1000);
                if (ipc.IsSessionTrusted)
                {
                    var response = await ipc.SendCommandAsync(new IpcRequest
                    {
                        OpCode = "task.stop", Payload = "{\"stopScope\":\"all\",\"clearQueue\":true}"
                    }, TimeSpan.FromSeconds(3));
                    sent = response.Success;
                }
            }
            catch (Exception ex) { Log("[全停止] 本机管道未确认：" + ex.Message); }
        }

        // ACK only proves receipt. Wait for the execution slot and coordinator to drain.
        if (sent)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                try
                {
                    var response = await SendIpcPreferredAsync("task.status", null, 1000);
                    if (response is { Success: true } && IsAllStopConfirmed(response.Data))
                        return new() { Status = "success", Message = "全部任务已停止，待执行队列与恢复现场已清除" };
                }
                catch { /* Failure to observe exit must reach the process fallback. */ }
                await Task.Delay(100);
            } while (DateTime.UtcNow < deadline);
        }

        // An unresponsive script must not hold the stop button hostage.
        // The existing arbiter only terminates BGI in this Windows session and suppresses its guard.
        var killed = await (StopProcessOverrideForTest?.Invoke("全停止：执行退出未确认")
            ?? _monitor.KillBgiControlledAsync("全停止：执行退出未确认"));
        return killed
            ? new() { Status = "success", Message = "全部停止已确认（BGI 未响应，已通过本机会话进程兜底停止）" }
            : new() { ErrorCode = "stop_unconfirmed", ResponsibilityState = ResponsibilityState.Pending,
                Message = "已请求全停止，但无法确认 BGI 退出；进程终止失败，未标记停止成功" };
    }

    internal static bool IsAllStopConfirmed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            return data.ValueKind == JsonValueKind.Object
                && False("running") && False("slotOccupied") && False("takeoverActive")
                && False("hasSuspendedTaskContext")
                && data.TryGetProperty("queueDepth", out var depth) && depth.ValueKind == JsonValueKind.Number
                && depth.TryGetInt32(out var count) && count == 0
                && (!data.TryGetProperty("currentTaskHandle", out var current) || current.ValueKind == JsonValueKind.Null);

            bool False(string name) => data.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.False;
        }
        catch (JsonException) { return false; }
    }
}
