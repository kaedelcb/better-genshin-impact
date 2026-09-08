using System.Diagnostics;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class CommandExecutor
{
    private readonly BgiProcessMonitor _monitor;
    private readonly string _bgiPath;
    /// <summary>[切片7] ext 通道客户端提供器（MainViewModel 注入 () => _externalClient）；null = 无 ext 通道，全部走 v2 旧路径。</summary>
    private readonly Func<BgiExternalClient?>? _externalClientProvider;
    /// <summary>[任务冲突策略] 用户可见日志出口（MainViewModel 注入 AddLog）；null 时只写 ProbeLog 文件日志。</summary>
    private readonly Action<string>? _log;
    /// <summary>[P3 对账] 本机是否确有上线锄地批次在跑（MainViewModel 注入 _activeBatch?.IsAlive 判定）；
    /// null = 未注入（保守视为无批次在跑，残留上下文按孤儿对账清除）。</summary>
    private readonly Func<bool>? _isBatchInFlight;
    /// <summary>[切片7] 队列式任务终态事件等待的兜底超时（事件经 SDK 断线续传不丢，超时仅为防永久挂起）。</summary>
    private static readonly TimeSpan TaskTerminalWaitTimeout = TimeSpan.FromHours(24);
    /// <summary>[任务策略] 快捷键启动任务后等待其结束的轮询间隔。</summary>
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(5);
    /// <summary>[任务策略] 快捷键下发后等待 task.status 变 running 的检测窗口（热键可能不启动任务，超时按"未启动"直接收尾）。</summary>
    private static readonly TimeSpan HotkeyTaskDetectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>[任务策略] 快捷键启动了新任务时，等待其结束的上限（防永久挂起）。</summary>
    private static readonly TimeSpan HotkeyTaskRunTimeout = TimeSpan.FromHours(4);
    /// <summary>[任务策略] 6 键固定收尾策略：执行完停止（清除中断上下文，不恢复）。无 UI、无配置项。</summary>
    private static readonly TaskConflictPolicySettings FixedKeyPolicy = new();
    /// <summary>本批次是否已通过 RestartBgi 回退重启过 BGI（批次级状态，由上游批次循环管理生命周期）。</summary>
    private bool _hasRestartedThisBatch;

    /// <summary>重置批次状态。由上游在新的一批开始时调用：批次循环（如 OnAllReadyConfirmedInternal）、
    /// 以及每次新的用户下发边界（OnRemoteCommand 接收端 / ExecuteLocalCommandAsync 本机执行 / 快捷指令弹窗本机执行）。
    /// 注意：批次循环内部（多配置组依次执行）不得调用，否则会破坏"同批次只回退重启一次"的保护。</summary>
    public void ResetBatch()
    {
        _hasRestartedThisBatch = false;
    }

    public CommandExecutor(BgiProcessMonitor monitor, string bgiPath, Func<BgiExternalClient?>? externalClientProvider = null,
        Action<string>? logger = null, Func<bool>? isBatchInFlight = null)
    {
        _monitor = monitor;
        _bgiPath = bgiPath;
        _externalClientProvider = externalClientProvider;
        _log = logger;
        _isBatchInFlight = isBatchInFlight;
    }

    /// <summary>[任务冲突策略] 用户可见日志 + 文件日志双写。</summary>
    private void Log(string message)
    {
        _log?.Invoke(message);
        ProbeLog(message);
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
                    return await StopWithKeyPolicyAsync();
                case "start_bgi":
                    return await StartBgiWithKeyPolicyAsync(GetStringParam(command.Params, "args"));
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
                    return await ExecuteHotkeyWithKeyPolicyAsync(
                        GetStringParam(command.Params, "hotkeyConfigName") ?? "");
                case "close_game":
                    return await CloseGameWithKeyPolicyAsync();
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
                // [切片7] ext 通道活跃且能力命中时走 ext.task.stop {clearQueue:true}（"停止"含"别再继续"语义，
                // 清空在队项）；通道瞬态失败落回 v2 task.stop。两阶段骨架（3s 等待 + 进程检查 + 杀进程回退）逐字节保留。
                var stopSent = false;
                var ext = _externalClientProvider?.Invoke();
                if (ext is { State: BgiExternalLinkState.Ready }
                    && ext.HasCapability(BgiExternalClient.CapabilityTaskQueue))
                {
                    try
                    {
                        var extStop = await ext.StopTaskAsync(clearQueue: true);
                        stopSent = extStop.Success;
                    }
                    catch
                    {
                        // 通道瞬态失败，落回 v2 task.stop
                    }
                }

                if (!stopSent)
                {
                    await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.stop" });
                }

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

        // 阶段2：杀进程（走仲裁器：有意杀死对崩溃守护豁免 + 等进程退净，防止守护误判崩溃把 BGI 拉回来）
        var killed = await _monitor.KillBgiControlledAsync("stop 命令强制停止");
        return killed
            ? new CommandResult { Status = "success", Message = "BGI 已强制停止" }
            : new CommandResult { Status = "failed", Message = "无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试" };
    }

    /// <summary>
    /// 启动 BGI：直接调用进程监控启动 BGI 进程。
    /// </summary>
    private Task<CommandResult> StartBgiAsync(string? args = null)
    {
        _monitor.RestartBgi(args);
        var msg = string.IsNullOrWhiteSpace(args) ? "BGI 已启动" : $"BGI 已启动（参数：{args}）";
        return Task.FromResult(new CommandResult { Status = "success", Message = msg });
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

        // [任务策略] 按键门控（固定行为：立即执行 + 执行完停止，无配置项）。
        // 本机忙且无既有中断上下文时 suspend 抢占（强制 v2，跳过下方 ext 队列通道——队列语义与抢占冲突）；
        // 已有中断上下文（上线锄地批次进行中）不二次抢占，走原有无损拒绝；空闲直接走下方原路径。
        if (await ShouldPreemptKeyPressAsync($"配置组「{groupName}」"))
        {
            return await StartWithPreemptionAsync(FixedKeyPolicy, groupName, null, startFromIndex, generation);
        }

        // [切片7] ext 任务队列通道（capability task.queue）：入队即返回 + 事件驱动等终态，
        // 全程无 task_already_running 撞锁、无 1s×6 重试噪音；通道不可用走下方 v2 路径（逐字节保留）。
        var extClient = _externalClientProvider?.Invoke();
        if (extClient is { State: BgiExternalLinkState.Ready }
            && extClient.HasCapability(BgiExternalClient.CapabilityTaskQueue))
        {
            var queueResult = await TryStartViaQueueAsync(extClient, groupName, null, startFromIndex, generation);
            if (queueResult != null)
            {
                _hasRestartedThisBatch = false; // ext 通道成功 = BGI 在线，后续不再需要回退标记
                return queueResult;
            }
            // null = 通道瞬态失败，落回 v2 路径
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
            // [无损拒绝适配 b5386005] task_already_running = BGI 明确应答的业务拒绝（非传输失败），
            // 多半是 suspend 后旧任务退场慢（任务锁未释放）。等 1s 重发，最多 6 次
            // （与 suspend 5s 等锁 + 助手 P1-C 6s 轮询的总容忍对齐）。
            // 幂等安全：BGI 侧 generation 幂等登记已移到拒绝检查之后，被拒请求不会污染去重状态。
            for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
            {
                ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）groupName={groupName}");
                await Task.Delay(1000);
                response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            }
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

            // [无损拒绝适配 b5386005] BGI 明确应答但拒绝启动：直接失败返回，绝不进杀进程回退——
            // 杀进程会把 BGI 正在运行的任务一起杀死，恰恰违背无损拒绝的初衷。
            // 只有传输层异常（catch）才走 KillBgi+RestartBgi 回退。
            return new CommandResult { Status = "failed", Message = $"BGI 拒绝启动配置组「{groupName}」（{response.ErrorCode ?? "unknown"}）：{response.ErrorMessage ?? "无详情"}。按无损拒绝语义未杀进程，请稍后重试或先停止当前任务" };
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
            // [P2 仲裁] 杀/启收编到仲裁器：信号量串行 + 有意杀死抑制（防守护误判崩溃再拉无参实例）
            // + 等进程真正退净后才拉起；杀不掉（提权）时返回 false，不假成功
            if (!await _monitor.RestartBgiControlledAsync($"--startGroups {groupArgs}", "IPC回退"))
            {
                Log($"[进程仲裁] 配置组「{groupName}」回退重启失败：无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试");
                return new CommandResult { Status = "failed", Message = $"配置组 {groupName} 启动失败：无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试" };
            }
            _hasRestartedThisBatch = true; // 只在仲裁器确认杀净+拉起后置位，失败不算"本批次已重启"
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
        // [任务策略] 按键门控（同 StartGroupAsync，固定行为：立即执行 + 执行完停止）：
        // 本机忙且无既有中断上下文时 suspend 抢占强制 v2；已有中断上下文走无损拒绝；空闲走原路径。
        if (await ShouldPreemptKeyPressAsync($"一条龙「{configName}」"))
        {
            return await StartWithPreemptionAsync(FixedKeyPolicy, null, configName, startFromIndex, generation);
        }

        // [切片7] ext 任务队列通道（同 StartGroupAsync）；通道不可用走下方 v2 路径（逐字节保留）。
        var extClient = _externalClientProvider?.Invoke();
        if (extClient is { State: BgiExternalLinkState.Ready }
            && extClient.HasCapability(BgiExternalClient.CapabilityTaskQueue))
        {
            var queueResult = await TryStartViaQueueAsync(extClient, null, configName, startFromIndex, generation);
            if (queueResult != null)
            {
                return queueResult;
            }
            // null = 通道瞬态失败，落回 v2 路径
        }

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
            // [无损拒绝适配 b5386005] 同 StartGroupAsync：业务拒绝（任务运行中）等锁重试，最多 6 次
            for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
            {
                ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）configName={configName}");
                await Task.Delay(1000);
                response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            }
            if (response.Success)
                return new CommandResult { Status = "success", Message = $"一条龙 {configName} 已启动" };

            // [无损拒绝适配 b5386005] 业务拒绝不杀进程，直接失败返回（只有 catch 传输异常才进回退）
            return new CommandResult { Status = "failed", Message = $"BGI 拒绝启动一条龙「{configName}」（{response.ErrorCode ?? "unknown"}）：{response.ErrorMessage ?? "无详情"}。按无损拒绝语义未杀进程，请稍后重试或先停止当前任务" };
        }
        catch
        {
            // IPC 失败，回退到杀进程重启
        }

        // 回退：杀进程 + 重启带 --startOneDragon
        // [P2 仲裁] 同 StartGroupAsync：收编到仲裁器（串行 + 抑制 + 等退净），杀不掉不假成功
        if (!await _monitor.RestartBgiControlledAsync($"--startOneDragon \"{configName}\"", "IPC回退"))
        {
            Log($"[进程仲裁] 一条龙「{configName}」回退重启失败：无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试");
            return new CommandResult { Status = "failed", Message = $"一条龙 {configName} 启动失败：无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试" };
        }
        return new CommandResult { Status = "success", Message = $"一条龙 {configName} 已通过重启启动" };
    }

    /// <summary>
    /// [切片7] 经 ext 任务队列通道提交启动：先创建终态事件等待器（先订阅后动作，红线7），
    /// 再 Submit 入队拿 taskHandle，最后等 task.completed/failed/queueCancelled 事件。
    /// 返回 null = 通道瞬态失败（调用方落 v2 路径，1s×6 重试锤子与杀进程回退逐字节保留）；
    /// 明确业务拒绝（queue_full 等）直接失败返回，绝不进杀进程回退（与 b5386005 无损拒绝语义一致）。
    /// 返回时机与 v2 一致：任务真正执行完（或被取消）后才返回，批次循环语义不变。
    /// </summary>
    private async Task<CommandResult?> TryStartViaQueueAsync(
        BgiExternalClient ext, string? groupName, string? configName, int startFromIndex, int generation)
    {
        var desc = groupName != null ? $"配置组「{groupName}」" : $"一条龙「{configName}」";
        try
        {
            // 等待器先于 Submit 创建：adopted 场景下既有任务可能在我们 Submit 前就完成，
            // 其终态事件先入等待器缓冲，按句柄匹配时不丢
            using var waiter = ext.CreateTaskTerminalWaiter();
            var submit = await ext.SubmitTaskStartAsync(groupName, configName, startFromIndex, generation);
            if (!submit.Success)
            {
                ProbeLog($"[CommandExecutor][切片7] ext.task.start 被队列拒绝 {desc} errorCode={submit.ErrorCode}");
                return new CommandResult { Status = "failed", Message = $"BGI 任务队列拒绝启动{desc}（{submit.ErrorCode ?? "unknown"}）：{submit.ErrorMessage ?? "无详情"}。请稍后重试或先取消排队任务" };
            }

            if (submit.Status == "already_executed")
            {
                // 与 v2 路径一致：幂等命中按成功处理（同 generation+name 已执行过）
                ProbeLog($"[CommandExecutor][切片7] ext.task.start 幂等命中 already_executed {desc} generation={generation}");
                return new CommandResult { Status = "success", Message = $"{desc} 已执行过（generation={generation}，幂等跳过）" };
            }

            if (string.IsNullOrEmpty(submit.TaskHandle))
            {
                // 畸形响应（queued/adopted 但无句柄）：无法路由终态事件，按通道瞬态失败落 v2 路径，
                // 避免 null 句柄穿透 WaitForHandleAsync（ArgumentNullException 不在下方 catch 过滤器内）
                ProbeLog($"[CommandExecutor][切片7] ext.task.start 响应缺少 taskHandle（status={submit.Status}），落回 v2 路径 {desc}");
                return null;
            }

            ProbeLog($"[CommandExecutor][切片7] ext.task.start 已入队 {desc} status={submit.Status} taskHandle={submit.TaskHandle} queuePosition={submit.QueuePosition}");
            var terminal = await waiter.WaitForHandleAsync(submit.TaskHandle, TaskTerminalWaitTimeout);
            if (terminal == null)
            {
                return new CommandResult { Status = "failed", Message = $"{desc} 等待执行结果超时（{TaskTerminalWaitTimeout.TotalHours}h 兜底），taskHandle={submit.TaskHandle}" };
            }

            return terminal.Kind switch
            {
                BgiTaskTerminalKind.Completed when terminal.Cancelled =>
                    new CommandResult { Status = "cancelled", Message = $"{desc} 执行中被取消" },
                BgiTaskTerminalKind.Completed =>
                    new CommandResult { Status = "success", Message = $"{desc} 已启动并执行完成（队列通道）" },
                BgiTaskTerminalKind.QueueCancelled =>
                    new CommandResult { Status = "cancelled", Message = $"{desc} 排队中被取消" },
                _ => new CommandResult { Status = "failed", Message = $"{desc} 执行失败（{terminal.ErrorCode ?? "unknown"}）：{terminal.ErrorMessage ?? "无详情"}" },
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException
                                   or TimeoutException or OperationCanceledException
                                   or System.Text.Json.JsonException)
        {
            // 通道瞬态失败（断线/超时/握手失效）→ null 让调用方落 v2 路径
            ProbeLog($"[CommandExecutor][切片7] 任务队列通道瞬态失败，落回 v2 路径 {desc}: {ex.Message}");
            return null;
        }
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

    // ==================== [任务冲突策略] 共享原语与抢占闭环 ====================

    /// <summary>
    /// [切片4 复刻] ext 优先的 BGI IPC 发送（与 MainViewModel.SendBgiIpcPreferredAsync 同语义）：
    /// ext 通道 Ready 且操作有 ext 映射时走长连接；否则回退 v2 IpcClient 短连接。
    /// 返回 null = 两条路径都不可用（调用方按容错语义处理）。
    /// </summary>
    private async Task<IpcResponse?> SendIpcPreferredAsync(string v2OpCode, string? payloadJson, int connectTimeoutMs = 2000)
    {
        var ext = _externalClientProvider?.Invoke();
        if (ext is { State: BgiExternalLinkState.Ready }
            && BgiExternalClient.TryMapToExtOperation(v2OpCode, out var extOp))
        {
            try
            {
                var extResp = await ext.SendCommandAsync(
                    extOp,
                    payloadJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(payloadJson),
                    TimeSpan.FromMilliseconds(Math.Max(connectTimeoutMs, 2000)));
                return new IpcResponse
                {
                    Success = extResp.Success,
                    Data = extResp.Data,
                    ErrorMessage = extResp.ErrorMessage,
                    ErrorCode = extResp.ErrorCode,
                };
            }
            catch
            {
                // ext 通道瞬态失败 → 落回 v2 短连接
            }
        }

        try
        {
            using var ipc = new IpcClient();
            await ipc.ConnectAsync(connectTimeoutMs);
            return await ipc.SendCommandAsync(new IpcRequest { OpCode = v2OpCode, Payload = payloadJson });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>[任务冲突策略] 查询 BGI 任务状态（running / hasSuspendedTaskContext / 中断上下文身份）。查询失败返回 null（按现状容错）。</summary>
    private async Task<(bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)?> QueryTaskStatusAsync(int connectTimeoutMs = 1500)
    {
        var resp = await SendIpcPreferredAsync("task.status", null, connectTimeoutMs);
        if (resp is not { Success: true } || string.IsNullOrEmpty(resp.Data)) return null;
        try
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resp.Data);
            var running = data.TryGetProperty("running", out var rEl)
                && rEl.ValueKind == System.Text.Json.JsonValueKind.True;
            var hasCtx = data.TryGetProperty("hasSuspendedTaskContext", out var hEl)
                && hEl.ValueKind == System.Text.Json.JsonValueKind.True;
            // [协议加法] 中断上下文身份（BGI 新增可选字段，旧版 BGI 无此字段时为 null，判定自动失效退化为原行为）
            string? suspendedType = data.TryGetProperty("suspendedTaskType", out var stEl)
                && stEl.ValueKind == System.Text.Json.JsonValueKind.String ? stEl.GetString() : null;
            string? suspendedName = data.TryGetProperty("suspendedTaskName", out var snEl)
                && snEl.ValueKind == System.Text.Json.JsonValueKind.String ? snEl.GetString() : null;
            return (running, hasCtx, suspendedType, suspendedName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>「联机锄地上线」信号任务名（与 BGI 端 NotifyOnlineTask.TaskName 保持一致；助手独立进程无法引用 BGI 程序集，用常量对齐）。</summary>
    private const string OnlineSignalTaskName = "联机锄地上线";

    /// <summary>
    /// [兜底] 中断上下文身份是否为「联机锄地上线」信号任务：type=solo 且任务名匹配，或 type=group 且组名匹配。
    /// 这里按名匹配可以——这只是最后一道防线（主修复在 BGI 端：suspend 时根本不保存信号任务上下文）。
    /// </summary>
    private static bool IsOnlineSignalContext((bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)? status)
        => status is { HasContext: true } s
           && s.SuspendedName == OnlineSignalTaskName
           && (s.SuspendedType == "solo" || s.SuspendedType == "group");

    /// <summary>
    /// [P3 对账] 孤儿中断上下文清理：BGI 侧 SuspendedTaskContext 只有 resume 一条清理路径，
    /// 批次被新轮打断/收尾 IPC 失败时会永久残留，把后续按键启动永久卡死在无损拒绝分支。
    /// 本方法在 task.status 查询后调用：Running=false && HasContext=true（任务已结束但上下文未消费）
    /// 且本机无批次在跑 → 发 task.resume(cancel:true) 清孤儿上下文并记对账日志，返回对账后状态。
    /// 本机确有批次在跑时 Running=false+HasContext=true 是批次的正常间隙态（suspend 后等下一组），不动。
    /// </summary>
    private async Task<(bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)?> ReconcileOrphanedContextAsync(
        (bool Running, bool HasContext, string? SuspendedType, string? SuspendedName)? status, string caller)
    {
        if (status is { Running: false, HasContext: true }
            && _isBatchInFlight?.Invoke() != true)
        {
            Log($"[P3 对账] {caller}：检测到残留中断上下文（任务已结束但上下文未消费）且本机无批次在跑，按孤儿对账发 task.resume(cancel:true) 清除");
            await ExecuteResumeAsync(cancel: true);
            return (false, false, null, null); // 对账后视为空闲无上下文（清除失败由后续路径按现状容错）
        }
        return status;
    }

    /// <summary>
    /// [P1-C/切片7 共享] 等待 BGI 任务槽位释放（settle）：suspend 之后、task.start 之前调用。
    /// 先订阅 slotReleased 事件等待（先订阅后动作），再一次快照探测（已落定则直接通过，
    /// 覆盖"suspend 时本就无任务在跑、不会发 slotReleased"的场景）；未落定则等事件（6s 上限），
    /// 超时/通道不可用落回 200ms×30 轮询 task.status 兜底。超时仅记日志容错，返回是否已落定。
    /// 供上线锄地（OnAllReadyConfirmedInternal）与按键抢占两条路径复用。
    /// </summary>
    public async Task<bool> WaitTaskSlotSettledAsync(string logTag, Action<string>? log = null)
    {
        log ??= _log;
        var bgiSettled = false;
        var extForSettle = _externalClientProvider?.Invoke();
        if (extForSettle is { State: BgiExternalLinkState.Ready }
            && extForSettle.HasCapability(BgiExternalClient.CapabilityTaskQueue))
        {
            var slotWait = extForSettle.WaitSlotReleasedAsync(TimeSpan.FromSeconds(6));
            try
            {
                var probe = await SendIpcPreferredAsync("task.status", null, 1000);
                if (probe is { Success: true } && !string.IsNullOrEmpty(probe.Data))
                {
                    var pdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(probe.Data);
                    var stillRunning = pdata.TryGetProperty("running", out var prEl)
                        && prEl.ValueKind == System.Text.Json.JsonValueKind.True;
                    var hasCtxNow = pdata.TryGetProperty("hasSuspendedTaskContext", out var phEl)
                        && phEl.ValueKind == System.Text.Json.JsonValueKind.True;
                    if (!stillRunning || hasCtxNow)
                    {
                        bgiSettled = true;
                    }
                }

                if (!bgiSettled)
                {
                    bgiSettled = await slotWait;
                    if (bgiSettled)
                    {
                        log?.Invoke($"{logTag} 收到 task.slotReleased 事件，BGI 任务槽位已释放");
                    }
                }
            }
            catch
            {
                // 通道瞬态失败，落轮询兜底
            }
        }

        if (!bgiSettled)
        {
            for (var waitRound = 0; waitRound < 30; waitRound++)
            {
                try
                {
                    var waitResp = await SendIpcPreferredAsync("task.status", null, 1000);
                    if (waitResp is { Success: true } && !string.IsNullOrEmpty(waitResp.Data))
                    {
                        var wdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(waitResp.Data);
                        var stillRunning = wdata.TryGetProperty("running", out var rEl)
                            && rEl.ValueKind == System.Text.Json.JsonValueKind.True;
                        var hasCtx = wdata.TryGetProperty("hasSuspendedTaskContext", out var hEl)
                            && hEl.ValueKind == System.Text.Json.JsonValueKind.True;
                        if (!stillRunning || hasCtx)
                        {
                            bgiSettled = true;
                            break;
                        }
                    }
                }
                catch
                {
                    // IPC 暂不可达（BGI 忙/重启中），继续等待下一轮
                }
                await Task.Delay(200);
            }
        }
        if (!bgiSettled)
        {
            log?.Invoke($"{logTag} 等待 BGI 任务停止超时（6s），按容错策略继续执行 task.start");
        }
        return bgiSettled;
    }

    /// <summary>
    /// [任务冲突策略] 按键抢占入口判定：本机忙且无既有中断上下文时才抢占。
    /// 返回 true 表示应走抢占闭环（suspend → settle → v2 task.start → 策略收尾）。
    /// 空闲（running=false）：不抢占，走原有路径；已有中断上下文且本机确有批次在跑：
    /// 不抢占（再 suspend 会用锄地任务覆盖原任务上下文），走原有无损拒绝路径；
    /// 有中断上下文但本机无批次在跑：[P3 对账] 判孤儿，清上下文后照常抢占。
    /// </summary>
    private async Task<bool> ShouldPreemptKeyPressAsync(string desc)
    {
        var status = await QueryTaskStatusAsync();
        // [P3 对账] 判定前先清孤儿上下文（任务已结束但上下文未消费），避免残留把抢占判定永久卡死
        status = await ReconcileOrphanedContextAsync(status, $"按键抢占判定（{desc}）");
        if (status is not { Running: true }) return false; // 空闲或状态未知：不抢占
        if (status.Value.HasContext)
        {
            // [P3 对账] 本机确有批次在跑才保持无损拒绝（保护进行中批次）；
            // 无批次在跑 = 孤儿残留，清上下文后按 running && !hasContext 走正常抢占闭环
            if (_isBatchInFlight?.Invoke() == true)
            {
                Log($"[任务策略] 检测到 BGI 已有中断上下文（上线锄地批次进行中？），按键启动 {desc} 不抢占，走原有无损拒绝路径");
                return false;
            }
            Log($"[P3 对账] {desc}：检测到残留中断上下文但本机无批次在跑，按孤儿对账清除后继续抢占闭环");
            await ExecuteResumeAsync(cancel: true);
            return true;
        }
        return true;
    }

    /// <summary>
    /// [任务策略] 停止键闭环（固定行为：立即执行 + 执行完停止，无配置项）。
    /// 本机忙：suspend 即停止（绝不杀进程），再清上下文（不恢复）。
    /// 上线锄地批次进行中（已有中断上下文）：不二次抢占，走无损拒绝。
    /// 空闲或状态未知：保持原"停止 BGI"语义（IPC task.stop → 杀进程兜底）。
    /// </summary>
    private async Task<CommandResult> StopWithKeyPolicyAsync()
    {
        var status = await QueryTaskStatusAsync();
        if (status is { Running: true, HasContext: true })
        {
            Log("[任务策略] 检测到 BGI 已有中断上下文（联机锄地批次进行中），停止键不二次抢占，走无损拒绝");
            return new CommandResult { Status = "failed", Message = "停止未执行：联机锄地批次进行中（已有中断上下文），按无损拒绝语义不打断，请等批次结束后再试" };
        }
        if (status is not { Running: true })
        {
            return await StopBgiAsync(); // 空闲/状态未知：原语义
        }

        Log("[任务策略] 本机任务运行中，停止键以 task.suspend 中断当前任务（固定行为：执行完停止，不恢复）");
        var suspendResult = await ExecuteSuspendAsync("停止键");
        if (suspendResult.Status != "success")
        {
            Log($"[任务策略] task.suspend 失败/被阻断：{suspendResult.Message}；按键路径不回退杀进程，直接失败");
            return new CommandResult { Status = "failed", Message = $"停止失败：{suspendResult.Message}（按键路径绝不杀进程，请稍后重试）" };
        }
        await ApplyPolicyTeardownAsync(FixedKeyPolicy, "停止键中断的原任务", userCancelled: false);
        return new CommandResult { Status = "success", Message = "当前任务已中断（suspend），中断上下文已清除（固定行为：执行完停止）" };
    }

    /// <summary>
    /// [任务策略] 启动BGI 键闭环：BGI 未运行时纯启动；
    /// BGI 已在跑任务时无实际动作、记日志即可，不做抢占；BGI 空闲时保持原启动行为。
    /// </summary>
    private async Task<CommandResult> StartBgiWithKeyPolicyAsync(string? args = null)
    {
        if (!_monitor.IsBgiRunning)
        {
            return await StartBgiAsync(args); // BGI 未运行：纯启动
        }
        var status = await QueryTaskStatusAsync();
        if (status is { Running: true })
        {
            Log("[任务策略] BGI 已在运行任务，启动BGI 键无实际动作（不做抢占）");
            return new CommandResult { Status = "success", Message = "BGI 已在运行任务，启动BGI 键无实际动作" };
        }
        return await StartBgiAsync(args); // BGI 空闲：保持原行为（BGI 侧单实例锁兜底）
    }

    /// <summary>
    /// [任务策略] 关闭游戏键闭环（固定行为：立即执行 + 执行完停止，无配置项）。
    /// 本机忙：suspend → settle → 关游戏 → 清上下文不恢复（游戏已关导致的收尾失败只打日志，绝不杀进程）。
    /// 上线锄地批次进行中（已有中断上下文）：不二次抢占，走无损拒绝。空闲：直接关游戏（原语义）。
    /// </summary>
    private async Task<CommandResult> CloseGameWithKeyPolicyAsync()
    {
        var status = await QueryTaskStatusAsync();
        if (status is { Running: true, HasContext: true })
        {
            Log("[任务策略] 检测到 BGI 已有中断上下文（联机锄地批次进行中），关闭游戏键不二次抢占，走无损拒绝");
            return new CommandResult { Status = "failed", Message = "关闭游戏未执行：联机锄地批次进行中（已有中断上下文），按无损拒绝语义不打断，请等批次结束后再试" };
        }
        if (status is not { Running: true })
        {
            return await CloseGameAsync(); // 空闲/状态未知：原语义
        }

        Log("[任务策略] 本机任务运行中，关闭游戏键先中断当前任务（固定行为：执行完停止，不恢复）");
        var suspendResult = await ExecuteSuspendAsync("关闭游戏键");
        if (suspendResult.Status != "success")
        {
            Log($"[任务策略] task.suspend 失败/被阻断：{suspendResult.Message}；按键路径不回退杀进程，直接失败");
            return new CommandResult { Status = "failed", Message = $"关闭游戏失败：{suspendResult.Message}（按键路径绝不杀进程，请稍后重试）" };
        }
        await WaitTaskSlotSettledAsync("[任务策略]", _log);
        var closeResult = await CloseGameAsync();
        // 收尾失败（游戏已关导致清上下文失败）只打日志，不杀进程（ApplyPolicyTeardownAsync 内部已逐条容错）
        await ApplyPolicyTeardownAsync(FixedKeyPolicy, "关闭游戏", userCancelled: false);
        return closeResult;
    }

    /// <summary>
    /// [任务策略] 快捷键键闭环（固定行为：立即执行 + 执行完停止，无配置项）。
    /// 本机忙：suspend → settle → 发热键 → 15s 检测窗：热键未启动新任务则直接清上下文收尾；
    /// 启动了则等其结束后清上下文收尾。上线锄地批次进行中（已有中断上下文）：不二次抢占，走无损拒绝。
    /// 空闲：直接发热键（原语义）。
    /// </summary>
    private async Task<CommandResult> ExecuteHotkeyWithKeyPolicyAsync(string hotkeyConfigName)
    {
        var desc = $"快捷键「{hotkeyConfigName}」";

        var status = await QueryTaskStatusAsync();
        // [P3 对账] 开头先清孤儿上下文（任务已结束但上下文未消费），避免残留把热键永久卡在无损拒绝
        status = await ReconcileOrphanedContextAsync(status, desc);
        if (status is { Running: true, HasContext: true })
        {
            // [P3 对账] 本机确有批次在跑才保持无损拒绝（保护进行中批次）；
            // 无批次在跑 = 孤儿残留，清上下文后视为 running && !hasContext，走下方正常抢占闭环
            if (_isBatchInFlight?.Invoke() == true)
            {
                Log($"[任务策略] 检测到 BGI 已有中断上下文（联机锄地批次进行中），{desc} 不二次抢占，走无损拒绝");
                return new CommandResult { Status = "failed", Message = $"{desc} 未执行：联机锄地批次进行中（已有中断上下文），按无损拒绝语义不打断，请等批次结束后再试" };
            }
            Log($"[P3 对账] {desc}：检测到残留中断上下文但本机无批次在跑，按孤儿对账清除后继续");
            await ExecuteResumeAsync(cancel: true);
            status = (true, false, null, null);
        }
        if (status is not { Running: true })
        {
            return await ExecuteHotkeyAsync(hotkeyConfigName); // 空闲/状态未知：原语义（无被中断任务，无收尾）
        }

        Log($"[任务策略] 本机任务运行中，{desc} 先中断当前任务（固定行为：执行完停止，不恢复）");
        var suspendResult = await ExecuteSuspendAsync(desc);
        if (suspendResult.Status != "success")
        {
            Log($"[任务策略] task.suspend 失败/被阻断：{suspendResult.Message}；按键路径不回退杀进程，直接失败");
            return new CommandResult { Status = "failed", Message = $"抢占中断失败：{suspendResult.Message}（按键路径绝不杀进程，请稍后重试或先手动停止当前任务）" };
        }
        await WaitTaskSlotSettledAsync("[任务策略]", _log);
        var execResult = await ExecuteHotkeyAsync(hotkeyConfigName);
        if (execResult.Status != "success")
        {
            // 热键下发失败：原任务已被中断，仍按固定行为清上下文收尾
            await ApplyPolicyTeardownAsync(FixedKeyPolicy, desc, userCancelled: false);
            return execResult;
        }

        // 热键可能不启动任务：15s 内 task.status 未变 running 则直接清上下文收尾
        var started = false;
        var detectDeadline = DateTime.UtcNow + HotkeyTaskDetectTimeout;
        while (DateTime.UtcNow < detectDeadline)
        {
            var probe = await QueryTaskStatusAsync();
            if (probe is { Running: true }) { started = true; break; }
            await Task.Delay(1000);
        }
        if (!started)
        {
            Log($"[任务策略] {desc} 下发后 {HotkeyTaskDetectTimeout.TotalSeconds}s 内未启动新任务，直接清上下文收尾（固定行为：执行完停止）");
            await ApplyPolicyTeardownAsync(FixedKeyPolicy, desc, userCancelled: false);
            return execResult;
        }

        Log($"[任务策略] {desc} 已启动新任务，等待其结束后清上下文收尾（上限 {HotkeyTaskRunTimeout.TotalHours}h，5s 轮询）...");
        var runDeadline = DateTime.UtcNow + HotkeyTaskRunTimeout;
        while (DateTime.UtcNow < runDeadline)
        {
            var probe = await QueryTaskStatusAsync();
            if (probe is { Running: false }) break;
            // 查询失败（BGI 忙/重启中）：按容错继续等下一轮
            await Task.Delay(TaskPollInterval);
        }
        await ApplyPolicyTeardownAsync(FixedKeyPolicy, desc, userCancelled: false);
        return execResult;
    }

    /// <summary>
    /// [任务冲突策略] 按键抢占闭环：suspend → settle → v2 task.start（强制跳过 ext 队列通道，
    /// 队列语义与抢占冲突）→ 同步等执行完 → 按策略收尾（resume / resume(cancel:true) /
    /// resume(cancel:true)+启动指定任务）。suspend 失败或被跨会话守卫阻断时直接失败返回，
    /// 绝不走 KillBgi 回退（按键路径不杀进程）。
    /// </summary>
    private async Task<CommandResult> StartWithPreemptionAsync(
        TaskConflictPolicySettings policy, string? groupName, string? configName, int startFromIndex, int generation)
    {
        var desc = groupName != null ? $"配置组「{groupName}」" : $"一条龙「{configName}」";
        Log($"[任务冲突策略] 本机任务运行中，按键启动 {desc} 按策略（{policy.PolicyDisplayName}）抢占：先中断当前任务");

        // 1. suspend（跨会话守卫阻断/失败 → 直接失败返回，不杀进程）
        var suspendResult = await ExecuteSuspendAsync(desc);
        if (suspendResult.Status != "success")
        {
            Log($"[任务冲突策略] task.suspend 失败/被阻断：{suspendResult.Message}；按键路径不回退杀进程，直接失败");
            return new CommandResult { Status = "failed", Message = $"抢占中断失败：{suspendResult.Message}（按键路径绝不杀进程，请稍后重试或先手动停止当前任务）" };
        }

        // 2. settle 等待（与上线锄地共用同一方法）
        await WaitTaskSlotSettledAsync("[任务冲突策略]", _log);

        // 3. v2 IPC task.start（抢占路径强制 v2，跳过 ext 队列；传输失败不杀进程）
        var startResult = await StartViaV2IpcNoKillAsync(groupName, configName, startFromIndex, generation);

        // 4. 策略收尾（F11 取消永远压过配置策略）
        await ApplyPolicyTeardownAsync(policy, desc, startResult.Status == "cancelled");

        return startResult;
    }

    /// <summary>
    /// [任务冲突策略] v2 IPC task.start（无杀进程回退版）：抢占闭环与指定任务启动共用。
    /// 保留 1s×6 task_already_running 无损拒绝重试；业务拒绝/传输异常均直接失败返回，绝不 KillBgi。
    /// </summary>
    private async Task<CommandResult> StartViaV2IpcNoKillAsync(string? groupName, string? configName, int startFromIndex, int generation)
    {
        var desc = groupName != null ? $"配置组「{groupName}」" : $"一条龙「{configName}」";
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var blocked = CheckCrossSessionBlock(ipcClient, $"task.start {desc}");
            if (blocked != null) return blocked;
            var payload = groupName != null
                ? System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex, generation })
                : System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex, generation });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
            {
                ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）{desc}");
                await Task.Delay(1000);
                response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload });
            }
            if (response.Success)
            {
                _hasRestartedThisBatch = false; // IPC 成功 = BGI 在线，后续不再需要回退标记
                // 透传 cancelled（BGI 侧用户 F11 取消），与 StartGroupAsync 一致
                if (!string.IsNullOrEmpty(response.Data))
                {
                    try
                    {
                        var respData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response.Data);
                        var bgiStatus = respData.TryGetProperty("status", out var st) ? st.GetString() : null;
                        if (bgiStatus == "cancelled")
                        {
                            return new CommandResult { Status = "cancelled", Message = $"{desc} 执行中被取消" };
                        }
                    }
                    catch
                    {
                        // Data 解析失败不影响，默认走 success 分支
                    }
                }
                return new CommandResult { Status = "success", Message = $"{desc} 已启动" };
            }
            return new CommandResult { Status = "failed", Message = $"BGI 拒绝启动{desc}（{response.ErrorCode ?? "unknown"}）：{response.ErrorMessage ?? "无详情"}。按无损拒绝语义未杀进程" };
        }
        catch (Exception ex)
        {
            // 抢占/指定任务路径绝不杀进程：传输失败直接失败返回
            return new CommandResult { Status = "failed", Message = $"IPC task.start {desc} 失败: {ex.Message}（按键路径绝不杀进程）" };
        }
    }

    /// <summary>
    /// [任务冲突策略] 策略收尾：新任务执行完后按策略处置被中断的原任务。
    /// userCancelled=true（用户 F11 取消新任务）永远压过配置策略：清上下文，不恢复、不启动指定任务。
    /// Resume：BGI 侧无中断上下文时（用户刚 F11 导致 suspend 未保存 / BGI 曾重启）自动退化为停止并日志说明。
    /// RunSpecified：resume(cancel:true) 后校验并启动指定配置组/一条龙；名称为空或不存在则日志报错退化为停止。
    /// 供按键抢占闭环（CommandExecutor 内部）与上线锄地两条恢复触发路径（MainViewModel）复用。
    /// </summary>
    public async Task ApplyPolicyTeardownAsync(TaskConflictPolicySettings policy, string executedDesc, bool userCancelled, Action<string>? log = null)
    {
        log ??= _log;

        if (userCancelled)
        {
            log?.Invoke($"[任务冲突策略] {executedDesc}被用户取消（F11），优先于配置策略：清除中断上下文，不执行后续动作");
            await ExecuteResumeAsync(cancel: true);
            return;
        }

        switch (policy.Policy)
        {
            case TaskConflictPolicy.Resume:
            {
                // WasCancelled/丢上下文守卫：BGI 侧用户刚 F11 时 suspend 不保存上下文（BGI 既有行为），
                // 或 BGI 曾被重启导致内存上下文丢失，此时"恢复"必然失败，退化为停止
                var status = await QueryTaskStatusAsync();
                if (status is { HasContext: false })
                {
                    log?.Invoke("[任务冲突策略] BGI 侧无中断上下文（任务可能刚被 F11 取消，或 BGI 曾重启导致内存上下文丢失），「恢复」策略退化为停止");
                    return;
                }
                // [兜底 2026-09-08] 被中断的是「联机锄地上线」信号任务本身：恢复会重复触发上线（无限循环），
                // 退化为停止并清上下文。主修复在 BGI 端（suspend 不保存信号任务上下文），这里按名匹配只做最后一道防线。
                if (IsOnlineSignalContext(status))
                {
                    log?.Invoke("[任务冲突策略] 被中断的是上线触发任务本身，恢复会重复触发上线，退化为停止（清除中断上下文）");
                    await ExecuteResumeAsync(cancel: true);
                    return;
                }
                var resumeResult = await ExecuteResumeAsync();
                if (resumeResult.Status == "success")
                {
                    log?.Invoke("[任务冲突策略] 原任务已按策略恢复");
                }
                else
                {
                    log?.Invoke($"[任务冲突策略] 恢复原任务失败: {resumeResult.Message}；恢复上下文只存内存，BGI 被重启则无法恢复，请手动在 BGI 中重新启动调度器/一条龙");
                }
                break;
            }
            case TaskConflictPolicy.Stop:
            {
                await ExecuteResumeAsync(cancel: true);
                log?.Invoke("[任务冲突策略] 已按策略清除中断上下文，不恢复原任务（执行完停止）");
                break;
            }
            case TaskConflictPolicy.RunSpecified:
            {
                await ExecuteResumeAsync(cancel: true);
                if (string.IsNullOrWhiteSpace(policy.SpecifiedTaskName))
                {
                    log?.Invoke("[任务冲突策略] 策略为「不恢复并执行指定任务」但未配置指定任务名称，退化为停止");
                    return;
                }
                await StartSpecifiedTaskAsync(policy, log);
                break;
            }
        }
    }

    /// <summary>
    /// [任务冲突策略] RunSpecified 收尾：校验指定配置组/一条龙存在后 v2 task.start 启动。
    /// 名称不存在（或 config.list 查询失败）则日志报错退化为停止；启动失败不杀进程。
    /// </summary>
    private async Task StartSpecifiedTaskAsync(TaskConflictPolicySettings policy, Action<string>? log)
    {
        var isOneDragon = policy.SpecifiedTaskType == "onedragon";
        var typeDesc = isOneDragon ? "一条龙" : "配置组";
        var name = policy.SpecifiedTaskName;

        // 执行时校验存在性（设置里是自由文本输入，可能填错或 BGI 侧已删除）
        try
        {
            var listResp = await SendIpcPreferredAsync("config.list", null, 3000);
            if (listResp is { Success: true } && !string.IsNullOrEmpty(listResp.Data))
            {
                var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(listResp.Data);
                var listKey = isOneDragon ? "oneClickConfigs" : "configGroups";
                var exists = data.TryGetProperty(listKey, out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                    && arr.EnumerateArray().Any(e => e.ValueKind == System.Text.Json.JsonValueKind.String && e.GetString() == name);
                if (!exists)
                {
                    log?.Invoke($"[任务冲突策略] 指定{typeDesc}「{name}」在 BGI 中不存在，退化为停止（请在设置页检查指定任务配置）");
                    return;
                }
            }
            else
            {
                log?.Invoke($"[任务冲突策略] 无法读取 BGI 配置列表，无法校验指定{typeDesc}「{name}」，退化为停止");
                return;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[任务冲突策略] 校验指定任务失败: {ex.Message}，退化为停止");
            return;
        }

        log?.Invoke($"[任务冲突策略] 按策略启动指定{typeDesc}「{name}」");
        var startResult = await StartViaV2IpcNoKillAsync(isOneDragon ? null : name, isOneDragon ? name : null, 0, 0);
        log?.Invoke($"[任务冲突策略] 指定{typeDesc}「{name}」: {startResult.Message}");
    }
}