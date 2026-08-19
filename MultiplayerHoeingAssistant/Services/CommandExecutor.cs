using System.Diagnostics;
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
                "start_group" => await StartGroupAsync(
                    command.Params?.GetValueOrDefault("groupName")?.ToString() ?? "",
                    int.TryParse(command.Params?.GetValueOrDefault("startFromIndex")?.ToString(), out var idx) ? idx : 0),
                "start_oneclick" => await StartOneClickAsync(
                    command.Params?.GetValueOrDefault("configName")?.ToString() ?? "",
                    int.TryParse(command.Params?.GetValueOrDefault("startFromIndex")?.ToString(), out var idx2) ? idx2 : 0),
                "hotkey_execute" => await ExecuteHotkeyAsync(
                    command.Params?.GetValueOrDefault("hotkeyConfigName")?.ToString() ?? ""),
                "close_game" => await CloseGameAsync(),
                "set_task_enabled" => await SetTaskEnabledAsync(
                    command.Params?.GetValueOrDefault("groupName")?.ToString() ?? "",
                    command.Params?.GetValueOrDefault("configName")?.ToString() ?? "",
                    int.TryParse(command.Params?.GetValueOrDefault("taskIndex")?.ToString(), out var tidx) ? tidx : 0,
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
            if (Process.GetProcessesByName("BetterGI").Length == 0)
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
    /// 启动配置组：先 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// </summary>
    private async Task<CommandResult> StartGroupAsync(string groupName, int startFromIndex)
    {
        try
        {
            // 先停止当前任务
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.stop" });
            await Task.Delay(1000);

            // 通过 IPC 发 task.start
            var payload = System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            if (response.Success)
                return new CommandResult { Status = "success", Message = $"配置组 {groupName} 已启动" };
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
    /// 启动一条龙：先 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// </summary>
    private async Task<CommandResult> StartOneClickAsync(string configName, int startFromIndex)
    {
        try
        {
            // 先停止当前任务
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.stop" });
            await Task.Delay(1000);

            // 通过 IPC 发 task.start（一条龙内联启动）
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
}