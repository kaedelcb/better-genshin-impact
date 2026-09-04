using System.Diagnostics;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class CommandExecutor
{
    private readonly BgiProcessMonitor _monitor;
    private readonly string _bgiPath;
    /// <summary>本批次是否已通过 RestartBgi 回退重启过 BGI（批次级状态，由上游批次循环管理生命周期）。</summary>
    private bool _hasRestartedThisBatch;

    /// <summary>重置批次状态。由上游批次循环（如 OnAllReadyConfirmedInternal）在新的一批开始时调用。</summary>
    public void ResetBatch()
    {
        _hasRestartedThisBatch = false;
    }

    public CommandExecutor(BgiProcessMonitor monitor, string bgiPath)
    {
        _monitor = monitor;
        _bgiPath = bgiPath;
    }

    /// <summary>
    /// [DUPLAUNCH_PROBE] 探针辅助：追加一行到助手程序目录 assistant_runtime.log，方便定位远程触发路径。
    /// </summary>
    private static void ProbeLog(string message)
    {
        try
        {
            // 日志写入助手程序目录下的 log/ 子目录，按日期 + Windows 会话 ID 分文件，避免多用户会话日志混杂、单文件无限增长
            var logDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 文件写入失败不影响主流程
        }
    }

    /// <summary>
    /// 控制指令会话守卫：多用户多开时命名管道可能指向其他会话的 Primary BGI，
    /// 此时 task.start/stop/suspend 等控制指令会操控"别人会话的 BGI"，必须阻断。
    /// 返回非 null 表示已阻断——调用方应直接返回该结果，不要进入 IPC 失败回退
    /// （回退会 KillBgi+RestartBgi，可能误杀本会话正在跑任务的 BGI）。
    /// </summary>
    private static CommandResult? CheckCrossSessionBlock(IpcClient ipcClient, string commandDesc)
    {
        if (ipcClient.IsSessionTrusted) return null;
        var localSid = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var detail = ipcClient.SessionCheck == IpcSessionCheck.CrossSession
            ? $"管道指向其他会话的 BGI（对端 Session={ipcClient.RemoteSessionId?.ToString() ?? "?"} PID={ipcClient.RemoteProcessId?.ToString() ?? "?"}，本会话 Session={localSid}）"
            : "无法确认管道对端 BGI 所属会话（Ping 握手未通过）";
        ProbeLog($"[CommandExecutor] 控制指令已阻断（{commandDesc}）：{detail}");
        return new CommandResult { Status = "failed", Message = $"IPC 会话校验未通过，{commandDesc} 已阻断：{detail}。请检查是否存在多会话多开" };
    }

    /// <summary>
    /// [A1 治本] RestartBgi 后等待 BGI IPC 管道就绪，避免调用方紧接着的 IPC 请求在 BGI 刚启动时
    /// 连不上再次触发回退，或与命令行 --startGroups 路径并发启动原神。
    /// 轮询：每 1s 尝试连接，最多 10 次，超时后静默返回（不影响主流程，BGI 端锁已兜底）。
    /// </summary>
    private static async Task WaitForBgiIpcReadyAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                using var probe = new IpcClient();
                await probe.ConnectAsync(1000);
                // 发送一个正常命令并等响应，避免"连上立刻断"触发 BGI AcceptLoop 崩溃
                await probe.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
                ProbeLog("[WaitForBgiIpcReadyAsync] BGI IPC 已就绪");
                return;
            }
            catch
            {
                // BGI 尚未就绪，继续等待
            }
            await Task.Delay(1000);
        }
        ProbeLog("[WaitForBgiIpcReadyAsync] BGI IPC 就绪等待超时（10s），继续执行");
    }

    public async Task<CommandResult> ExecuteAsync(RemoteCommand command)
    {
        // 注意：_hasRestartedThisBatch 不在入口重置，而是在 StartGroupAsync 的 IPC 成功路径中重置。
        // 原因：一键锄地/上线循环的每个配置组都独立调用 ExecuteAsync，若在入口重置标记，
        // 第二个配置组进来时标记已清为 false，仍会走 KillBgi+RestartBgi 回退杀掉正在启动原神的第一个 BGI。
        try
        {
            switch (command.Cmd)
            {
                case "stop":
                    _hasRestartedThisBatch = false; // 用户手动停止后，新的一批重新开始
                    return await StopBgiAsync();
                case "start_bgi":
                    return await StartBgiAsync();
                case "start_group":
                    return await StartGroupAsync(
                        GetStringParam(command.Params, "groupName") ?? "",
                        GetIntParam(command.Params, "startFromIndex") ?? 0,
                        GetIntParam(command.Params, "generation") ?? 0,
                        ParseBatchGroupNames(command.Params));
                case "start_oneclick":
                    return await StartOneClickAsync(
                        GetStringParam(command.Params, "configName") ?? "",
                        GetIntParam(command.Params, "startFromIndex") ?? 0,
                        GetIntParam(command.Params, "generation") ?? 0);
                case "hotkey_execute":
                    return await ExecuteHotkeyAsync(
                        GetStringParam(command.Params, "hotkeyConfigName") ?? "");
                case "close_game":
                    return await CloseGameAsync();
                case "set_task_enabled":
                    return await SetTaskEnabledAsync(
                        GetStringParam(command.Params, "groupName") ?? "",
                        GetStringParam(command.Params, "configName") ?? "",
                        GetIntParam(command.Params, "taskIndex") ?? 0,
                        bool.TryParse(command.Params?.GetValueOrDefault("enabled")?.ToString(), out var en) && en);
                default:
                    return new CommandResult { Status = "failed", Message = $"未知命令: {command.Cmd}" };
            }
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
    /// 从 Params 字典解析 batchGroupNames（逗号分隔的配置组名列表）。
    /// 由 MainViewModel 批次循环的第一个配置组传入，用于回退时一次性传给 --startGroups。
    /// 无此字段或为空时返回 null，回退行为保持旧逻辑（只传当前组名）。
    /// </summary>
    private static List<string>? ParseBatchGroupNames(Dictionary<string, object>? dict)
    {
        var raw = GetStringParam(dict, "batchGroupNames");
        if (string.IsNullOrEmpty(raw)) return null;
        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return list.Count > 0 ? list : null;
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
            // 会话守卫：管道指向其他会话的 BGI 时 task.stop 会停掉别人会话的任务，
            // 跳过 IPC 阶段直接走阶段2（KillBgi 只杀本会话进程，语义仍然正确）
            if (ipcClient.IsSessionTrusted)
            {
                await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.stop" });
                await Task.Delay(3000);
                var currentSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                if (System.Diagnostics.Process.GetProcessesByName("BetterGI")
                    .All(p => p.SessionId != currentSession))
                    return new CommandResult { Status = "success", Message = "BGI 已优雅停止" };
            }
            else
            {
                ProbeLog($"[CommandExecutor] StopBgiAsync 跳过 IPC 优雅停止：{ipcClient.SessionCheck}（对端 Session={ipcClient.RemoteSessionId?.ToString() ?? "?"}），直接杀本会话进程");
            }
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
    private async Task<CommandResult> StartGroupAsync(string groupName, int startFromIndex, int generation = 0, List<string>? batchGroupNames = null)
    {
        // [DUPLAUNCH_PROBE] 探针：记录 start_group 命令触发路径（IPC 成功 vs 回退杀进程重启）
        ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] start_group 收到 groupName={groupName} startFromIndex={startFromIndex} generation={generation}");

        // 如果本批次已通过 RestartBgi 命令行重启过 BGI，说明 BGI 命令行已经在串行执行 --startGroups，
        // 此时不应再发 IPC task.start。本批次所有剩余配置组全部跳过 IPC，等待命令行串行完成。
        // 注意：不再检查 BGI 是否空闲——命令行路径（StartGroups）正在等待截图器/进游戏，后续会真正执行任务，
        // 此时 task.status 返回的 running=false 不代表任务已结束。检查空闲并重置标记会导致双入口并发执行同一个配置组。
        // 但有一个例外：如果 BGI 已经被用户手动停止（F11）或进程已退出，标记已过期，此时应重置标记让新批次走正常路径。
        if (_hasRestartedThisBatch)
        {
            // 检查 BGI 进程是否真的还活着并且任务系统可用
            // 如果 BGI 完全不可达（IPC 超时），说明进程已退出，重置标记并走正常路径
            var bgiAlive = false;
            try
            {
                using var probeClient = new IpcClient();
                await probeClient.ConnectAsync(1000);
                bgiAlive = true;
            }
            catch
            {
                // IPC 不可达，BGI 已退出
            }

            if (!bgiAlive)
            {
                // BGI 已退出，标记过期，重置并走正常 IPC 路径
                ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] BGI 已退出，重置标记 groupName={groupName}");
                _hasRestartedThisBatch = false;
                // 不 return，继续走到下面的主 IPC 路径
            }
            else
            {
                ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] 本批次已重启过 BGI，跳过 IPC 等待命令行串行完成 groupName={groupName}");
                await WaitForBgiIpcReadyAsync();
                return new CommandResult { Status = "success", Message = $"配置组 {groupName} 已由 BGI 命令行串行执行" };
            }
        }

        try
        {
            // 通过 IPC 发 task.start
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            // 会话守卫：阻断时直接失败返回，不进入下方的杀进程回退（避免误杀本会话正在跑任务的 BGI）
            var blocked = CheckCrossSessionBlock(ipcClient, $"task.start 配置组「{groupName}」");
            if (blocked != null) return blocked;
            var payload = System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex, generation });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            if (response.Success)
            {
                _hasRestartedThisBatch = false; // IPC 成功 = BGI 在线，后续不再需要回退标记
                ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] IPC task.start 成功 groupName={groupName}");

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
        // 如果本批次已通过 RestartBgi 重启过 BGI，后续配置组不再走 KillBgi+RestartBgi 回退，
        // 而是强制等待 IPC 就绪后走 IPC 路径（避免杀掉正在启动原神的 BGI 进程）。
        // 当前日志已证实：第2个配置组 IPC 再次失败会 KillBgi 并启动新 BGI 进程，
        // 导致原神启动 BGI 被中断、BGI 不执行任务。
        if (!_hasRestartedThisBatch)
        {
            // 如果 batchGroupNames 非空，一次性传全部配置组给 --startGroups，让 BGI 命令行串行执行
            var groupArgs = batchGroupNames != null && batchGroupNames.Count > 0
                ? string.Join(" ", batchGroupNames.Select(n => $"\"{n}\""))
                : $"\"{groupName}\"";
            ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] IPC 失败，回退杀进程重启 BGI with --startGroups {groupArgs}");
            _monitor.KillBgi();
            await Task.Delay(2000);
            _monitor.RestartBgi($"--startGroups {groupArgs}");
            _hasRestartedThisBatch = true;
        }
        // [A 治本] 等待 BGI IPC 就绪，避免调用方（一键锄地/上线循环）紧接着的 start_group
        // 在 BGI 刚启动时连不上再次回退，或与命令行 --startGroups 路径并发启动原神。
        await WaitForBgiIpcReadyAsync();
        return new CommandResult { Status = "success", Message = $"配置组 {groupName} 已通过重启启动" };
    }

    /// <summary>
    /// 启动一条龙：通过 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// 注意：不再预先发 task.stop（原因同 StartGroupAsync）。
    /// </summary>
    private async Task<CommandResult> StartOneClickAsync(string configName, int startFromIndex, int generation = 0)
    {
        try
        {
            // 通过 IPC 发 task.start（一条龙内联启动）
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            // 会话守卫：阻断时直接失败返回，不进入下方的杀进程回退
            var blocked = CheckCrossSessionBlock(ipcClient, $"task.start 一条龙「{configName}」");
            if (blocked != null) return blocked;
            var payload = System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex, generation });
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
            var blocked = CheckCrossSessionBlock(ipcClient, $"快捷键「{hotkeyConfigName}」");
            if (blocked != null) return blocked;
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
            var blocked = CheckCrossSessionBlock(ipcClient, "关闭游戏");
            if (blocked != null) return blocked;
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
            var blocked = CheckCrossSessionBlock(ipcClient, $"设置任务启用状态（group={groupName} config={configName} index={taskIndex}）");
            if (blocked != null) return blocked;
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
            var blocked = CheckCrossSessionBlock(ipcClient, "task.suspend");
            if (blocked != null) return blocked;

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
            var blocked = CheckCrossSessionBlock(ipcClient, cancel ? "task.resume(cancel)" : "task.resume");
            if (blocked != null) return blocked;

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