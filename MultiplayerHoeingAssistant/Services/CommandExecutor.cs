using System.Diagnostics;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class CommandExecutor
{
    private readonly BgiProcessMonitor _monitor;
    private readonly string _bgiPath;

    public CommandExecutor(BgiProcessMonitor monitor, string bgiPath)
    {
        _monitor = monitor;
        _bgiPath = bgiPath;
    }

    public async Task<CommandResult> ExecuteAsync(RemoteCommand command)
    {
        try
        {
            return command.Cmd switch
            {
                "stop" => await StopBgiAsync(),
                "start_bgi" => await StartBgiAsync(),
                "start_group" => await StartGroupAsync(
                    GetStringParam(command.Params, "groupName") ?? "",
                    GetIntParam(command.Params, "startFromIndex") ?? 0),
                "start_oneclick" => await StartOneClickAsync(
                    GetStringParam(command.Params, "configName") ?? "",
                    GetIntParam(command.Params, "startFromIndex") ?? 0),
                "hotkey_execute" => await ExecuteHotkeyAsync(
                    GetStringParam(command.Params, "hotkeyConfigName") ?? ""),
                "close_game" => await CloseGameAsync(),
                "set_task_enabled" => await SetTaskEnabledAsync(
                    GetStringParam(command.Params, "groupName") ?? "",
                    GetStringParam(command.Params, "configName") ?? "",
                    GetIntParam(command.Params, "taskIndex") ?? 0,
                    bool.TryParse(command.Params?.GetValueOrDefault("enabled")?.ToString(), out var en) && en),
                _ => new CommandResult { Status = "failed", Message = $"未知命令: {command.Cmd}" }
            };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = ex.Message };
        }
    }

    /// <summary>
    /// 从 Params 字典安全取出字符串值。
    /// SignalR 反序列化后 value 可能是 string 或 JsonElement，需分别处理。
    /// </summary>
    private static string? GetStringParam(Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je)
        {
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return val.ToString();
    }

    /// <summary>
    /// 从 Params 字典安全取出 int 值。处理 SignalR 反序列化后的 JsonElement（Number）。
    /// </summary>
    private static int? GetIntParam(Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is int i) return i;
        if (val is long l) return (int)l;
        if (val is System.Text.Json.JsonElement je)
        {
            return je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var n) ? n : null;
        }
        return int.TryParse(val.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// 停止 BGI：两阶段策略（IPC 优雅停止 → 杀进程）
    /// </summary>
    private async Task<CommandResult> StopBgiAsync()
    {
        // 阶段1：IPC 优雅停止
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.stop" });
            await Task.Delay(3000);
            var currentSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            if (System.Diagnostics.Process.GetProcessesByName("BetterGI")
                .All(p => p.SessionId != currentSession))
                return new CommandResult { Status = "success", Message = "BGI 已优雅停止" };
        }
        catch
        {
            // IPC 不可用，进入阶段2
        }

        // 阶段2：杀进程
        _monitor.KillBgi();
        return new CommandResult { Status = "success", Message = "BGI 已强制停止" };
    }

    /// <summary>
    /// 启动 BGI：直接调用进程监控启动 BGI 进程。
    /// </summary>
    private Task<CommandResult> StartBgiAsync()
    {
        _monitor.RestartBgi();
        return Task.FromResult(new CommandResult { Status = "success", Message = "BGI 已启动" });
    }

    /// <summary>
    /// 启动配置组：通过 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// 注意：不再预先发 task.stop，因为 HandleTaskStart 内部自己会 Cancel() 中断当前任务
    /// + 轮询 TaskSemaphore 等锁释放。task.stop 的异步 Cancel() 延迟到 RunMulti
    /// 执行期间触发会取消新配置组（wasCancelled=True）。
    /// </summary>
    private async Task<CommandResult> StartGroupAsync(string groupName, int startFromIndex)
    {
        try
        {
            // 通过 IPC 发 task.start
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            if (response.Success)
            {
                // 解析 BGI 响应中的 status：cancelled = 配置组执行中被取消（如 F11）
                // 必须透传，否则助手端收不到取消信号、会继续执行下一个配置组。
                if (!string.IsNullOrEmpty(response.Data))
                {
                    try
                    {
                        var respData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response.Data);
                        var bgiStatus = respData.TryGetProperty("status", out var st) ? st.GetString() : null;
                        if (bgiStatus == "cancelled")
                        {
                            return new CommandResult { Status = "cancelled", Message = $"配置组 {groupName} 执行中被取消" };
                        }
                    }
                    catch
                    {
                        // Data 解析失败不影响，默认走 success 分支
                    }
                }
                return new CommandResult { Status = "success", Message = $"配置组 {groupName} 已启动" };
            }
        }
        catch
        {
            // IPC 失败，回退到杀进程重启
        }

        // 回退：杀进程 + 重启带 --startGroups
        _monitor.KillBgi();
        await Task.Delay(2000);
        _monitor.RestartBgi($"--startGroups \"{groupName}\"");
        return new CommandResult { Status = "success", Message = $"配置组 {groupName} 已通过重启启动" };
    }

    /// <summary>
    /// 启动一条龙：通过 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// 注意：不再预先发 task.stop（原因同 StartGroupAsync）。
    /// </summary>
    private async Task<CommandResult> StartOneClickAsync(string configName, int startFromIndex)
    {
        try
        {
            // 通过 IPC 发 task.start（一条龙内联启动）
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            if (response.Success)
                return new CommandResult { Status = "success", Message = $"一条龙 {configName} 已启动" };
        }
        catch
        {
            // IPC 失败，回退到杀进程重启
        }

        // 回退：杀进程 + 重启带 --startOneDragon
        _monitor.KillBgi();
        await Task.Delay(2000);
        _monitor.RestartBgi($"--startOneDragon \"{configName}\"");
        return new CommandResult { Status = "success", Message = $"一条龙 {configName} 已通过重启启动" };
    }

    /// <summary>执行快捷键：IPC 发 action.execute_hotkey</summary>
    private async Task<CommandResult> ExecuteHotkeyAsync(string hotkeyConfigName)
    {
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { hotkeyConfigName });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "action.execute_hotkey", Payload = payload });
            if (response.Success)
                return new CommandResult { Status = "success", Message = $"快捷键 {hotkeyConfigName} 已执行" };
            return new CommandResult { Status = "failed", Message = $"快捷键执行失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC 快捷键失败: {ex.Message}" };
        }
    }

    /// <summary>关闭游戏：IPC 发 action.close_game</summary>
    private async Task<CommandResult> CloseGameAsync()
    {
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "action.close_game" });
            if (response.Success)
                return new CommandResult { Status = "success", Message = "关闭游戏指令已下发" };
            return new CommandResult { Status = "failed", Message = $"关闭游戏失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC 关闭游戏失败: {ex.Message}" };
        }
    }

    /// <summary>设置任务启用状态：IPC 发 config.set_task_enabled</summary>
    private async Task<CommandResult> SetTaskEnabledAsync(string groupName, string configName, int taskIndex, bool enabled)
    {
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { groupName, configName, taskIndex, enabled });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "config.set_task_enabled", Payload = payload });
            if (response.Success)
                return new CommandResult { Status = "success", Message = $"任务 {taskIndex} 启用状态已设为 {enabled}" };
            return new CommandResult { Status = "failed", Message = $"设置启用状态失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC 设置启用状态失败: {ex.Message}" };
        }
    }

    /// <summary>中断当前任务并保存上下文：IPC 发 task.suspend</summary>
    public async Task<CommandResult> ExecuteSuspendAsync(string hoeingGroupName)
    {
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);

            // 发 task.suspend
            var payload = System.Text.Json.JsonSerializer.Serialize(new { });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.suspend", Payload = payload });
            if (response.Success)
            {
                // 返回包含被中断任务的上下文信息（给调用方日志用）
                return new CommandResult { Status = "success", Message = $"任务已中断" };
            }
            return new CommandResult { Status = "failed", Message = $"task.suspend 失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC task.suspend 失败: {ex.Message}" };
        }
    }

    /// <summary>恢复原任务：IPC 发 task.resume。cancel=true 时清除上下文但不恢复。</summary>
    public async Task<CommandResult> ExecuteResumeAsync(bool cancel = false)
    {
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);

            if (cancel)
            {
                // 取消恢复：发 task.resume 带 cancel=true 参数
                var cancelPayload = System.Text.Json.JsonSerializer.Serialize(new { cancel = true });
                var cancelResponse = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.resume", Payload = cancelPayload });
                return new CommandResult { Status = "success", Message = "已取消恢复，BGI 保持空闲" };
            }

            // 正常恢复
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.resume" });
            if (response.Success)
                return new CommandResult { Status = "success", Message = "原任务已恢复" };
            return new CommandResult { Status = "failed", Message = $"task.resume 失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC task.resume 失败: {ex.Message}" };
        }
    }
}