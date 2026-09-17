using System.Diagnostics;
using System.Linq;
using System.Threading;
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

    /// <summary>[另案②] Resume 策略 task_busy 重试窗口标志（0/1，Interlocked 访问）。
    /// 窗口内 BGI 侧中断上下文是"待重试的恢复"而非孤儿残留——孤儿对账与按键清账
    /// 三处判定把该窗口视同批次在跑，不误清上下文。</summary>
    private int _resumeRetryInFlight;
    private string? _takeoverTicket;
    private BgiEpoch? _takeoverEpoch;
    private readonly SemaphoreSlim _suspendGate = new(1, 1);

    private bool IsResumeRetryInFlight => Interlocked.CompareExchange(ref _resumeRetryInFlight, 0, 0) != 0;
    /// <summary>[切片7] 队列式任务终态事件等待的兜底超时（事件经 SDK 断线续传不丢，超时仅为防永久挂起）。</summary>
    private static readonly TimeSpan TaskTerminalWaitTimeout = TimeSpan.FromHours(24);
    /// <summary>[终态可拉取] 终态事件等待切片长度：每切片超时即拉一次 ext.task.queueStatus 校准（安全网轮询）。</summary>
    private static readonly TimeSpan TerminalStatusPollInterval = TimeSpan.FromSeconds(5);
    /// <summary>[任务策略] 快捷键启动任务后等待其结束的轮询间隔。</summary>
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(5);
    /// <summary>[任务策略] 快捷键下发后等待 task.status 变 running 的检测窗口（热键可能不启动任务，超时按"未启动"直接收尾）。</summary>
    private static readonly TimeSpan HotkeyTaskDetectTimeout = TimeSpan.FromSeconds(15);
    /// <summary>[任务策略] 快捷键启动了新任务时，等待其结束的上限（防永久挂起）。</summary>
    private static readonly TimeSpan HotkeyTaskRunTimeout = TimeSpan.FromHours(4);
    /// <summary>[分层超时 2026-09-12] v2 task.start 的命令超时：BGI 侧 task.start 刻意阻塞到任务
    /// 真正执行完才响应（cancelled 回传依赖此契约），与 ext TaskTerminalWaitTimeout 对齐。
    /// 本地命名管道在 BGI 进程死亡时立刻断流，不存在无限挂死；24h 帽只兜 BGI 活着但 handler 死锁。</summary>
    private static readonly TimeSpan V2TaskStartCommandTimeout = TimeSpan.FromHours(24);
    /// <summary>[分层超时 2026-09-12] task.suspend 的命令超时：BGI 内部等锁 deadline 为 5s
    /// （InstanceRequestHandler.HandleTaskSuspend），客户端必须留余量，否则"超时但实际挂起成功"。
    /// [A6 上调 8s→35s] 有界退出契约（ADR-2026-09-16）下 BGI 会持响应等槽位确认释放，上限
    /// QuiesceBound=30s——客户端必须先于该上界之后超时，才能读到 quiesceConfirmed 字段并据此升级，
    /// 否则 8s 超时会把"超界未释放"的响亮信号整个吞掉。老 BGI 响应即时返回，上调零影响
    /// （本地命名管道在进程死亡时即刻断流，不存在挂到 35s 才察觉的场景）。</summary>
    private static readonly TimeSpan V2TaskSuspendCommandTimeout = TimeSpan.FromSeconds(35);
    /// <summary>[任务策略] 6 键固定收尾策略：执行完停止（清除中断上下文，不恢复）。无 UI、无配置项。</summary>
    private static readonly TaskConflictPolicySettings FixedKeyPolicy = new();

    /// <summary>[弹窗竞态守卫] 在途 config.set_task_enabled 写入计数。
    /// 背景：OnRemoteCommand 是 Action 事件 async void 并发分发，弹窗下发的多条 set_task_enabled
    /// 与紧随的 start_group/start_oneclick 会并发执行，启动动作可能读到旧启用状态。
    /// 纪律：SetTaskEnabledAsync 进入时 ++、finally --；StartGroupAsync/StartOneClickAsync 在
    /// suspend/启动动作之前先等计数归零（窄化顺序守卫，只约束 set→start 的相对顺序）。
    /// 绝不做全量串行化——task.start 会阻塞到组执行完（数小时），stop 必须能随时打断。</summary>
    private int _inflightConfigWrites;

    /// <summary>[弹窗竞态守卫] 等待在途 set_task_enabled 落盘：200ms 轮询，上限 10s。
    /// 超时记日志继续（不阻塞启动）——防 set 路径卡死（IPC 挂起等）拖累启动。</summary>
    private async Task WaitConfigWritesDrainedAsync(string desc)
    {
        if (Volatile.Read(ref _inflightConfigWrites) <= 0) return;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref _inflightConfigWrites) > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        if (Volatile.Read(ref _inflightConfigWrites) > 0)
            Log($"[弹窗竞态守卫] {desc} 等待 set_task_enabled 落盘超时（10s），仍有 {Volatile.Read(ref _inflightConfigWrites)} 条在途，继续启动");
    }

    /// <summary>[A4.4] 批次标记已随 --startGroups 命令行回退一并废弃：执行声明权只走 IPC/reconcile，
    /// 不再存在"命令行串行执行期间 IPC 假空闲"的竞态，无需跨调用的批次级重启标记。</summary>
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
    /// 连不上再次触发回退。
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
        try
        {
            switch (command.Cmd)
            {
                case "stop":
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
                        GetIntParam(command.Params, "generation") ?? 0,
                        ParseBatchGroupNames(command.Params));
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
    /// 由 MainViewModel 批次循环传入，一条龙路径经 IPC 协议字段透传给 BGI 做龙内组间跳过判定。
    /// [A4.4] 不再用于 --startGroups/--batchGroups 命令行回退（该回退已废弃）。
    /// 无此字段或为空时返回 null（非批次来源/老路径），BGI 不跳过任何组。
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
    /// [常开守护] 如实回报结果：启动调用失败（路径错误/权限等）时返回 failed，
    /// 不再无条件报"BGI 已启动"（旧实现对失败静默，启动中心节点会以为已启动）。
    /// </summary>
    private Task<CommandResult> StartBgiAsync(string? args = null)
    {
        var started = _monitor.RestartBgi(args);
        if (!started)
        {
            return Task.FromResult(new CommandResult
            {
                Status = "failed",
                Message = "启动 BGI 失败（无法拉起进程，详见助手日志；守护会在冷却结束后自动重试）"
            });
        }

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
        if (_isBatchInFlight?.Invoke() == true || IsResumeRetryInFlight)
            return new CommandResult { Status = "failed", ErrorCode = "batch_busy", Message = "现有批次/恢复尚未收尾，不能借用其执行权启动另一任务" };
        // [弹窗竞态守卫] 先等弹窗下发的 set_task_enabled 全部落盘，再 suspend/启动，防读到旧启用状态
        await WaitConfigWritesDrainedAsync($"start_group「{groupName}」");

        // [DUPLAUNCH_PROBE] 探针：记录 start_group 命令触发路径（IPC 成功 vs 回退裸拉起重试）
        ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] start_group 收到 groupName={groupName} startFromIndex={startFromIndex} generation={generation}");

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
                return queueResult;
            }
            // null = 通道瞬态失败，落回 v2 路径
        }

        // [分层超时 2026-09-12] 失败语义分离：Connect 失败（BGI 未运行/管道不可达）才走重启回退
        // （既有冷启动语义）；连接建立后的命令传输失败说明 BGI 活着——超时≠未执行（at-least-once），
        // 杀进程会杀掉可能已在跑的任务，必须经 ReconcileV2TaskStartOutcomeAsync 核实对端事实。
        // [A4.4] 回退新语义（总计划 §4.4）：Connect 失败 → 裸拉起 BGI（不带任何执行参数）
        // → 等 IPC 就绪 → 本方法内重试一次 task.start。彻底废弃 --startGroups 命令行串行黑盒
        // （对 ext 队列/注册表完全不可见，是"已下发当已完成"事故的温床）；命令行执行路径消失后，
        // "命令行执行期间 IPC 假空闲导致双入口"的竞态随之消失，_hasRestartedThisBatch 散落标记删除。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var ipcClient = new IpcClient();
            var connected = false;
            try
            {
                // 通过 IPC 发 task.start
                await ipcClient.ConnectAsync(3000);
                connected = true;
            }
            catch
            {
                // BGI 未运行/管道不可达：落到下方裸拉起回退
            }

            if (!connected)
            {
                if (attempt > 0)
                {
                    // 裸拉起 + 就绪等待后仍连不上：放弃（不重启第二次，防"杀启循环"）
                    ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] 裸拉起后 IPC 仍不可达 groupName={groupName}");
                    return new CommandResult { Status = "failed", Message = $"配置组 {groupName} 启动失败：BGI 裸拉起后 IPC 仍不可达，请查看助手日志后重试" };
                }

                // [P2 仲裁] 杀/启收编到仲裁器：信号量串行 + 有意杀死抑制（防守护误判崩溃再拉无参实例）
                // + 等进程真正退净后才拉起；杀不掉（提权）时返回 false，不假成功
                ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartGroupAsync] IPC 不可达，回退裸拉起 BGI（不带执行参数）groupName={groupName}");
                if (!await _monitor.RestartBgiControlledAsync(null, "IPC回退-裸拉起"))
                {
                    Log($"[进程仲裁] 配置组「{groupName}」回退裸拉起未完成：旧进程未退净（可能提权运行）或启动调用失败，详见上方日志");
                    return new CommandResult { Status = "failed", Message = $"配置组 {groupName} 启动失败：BGI 回退裸拉起未完成（无法终止残留进程或启动失败），请查看助手日志后重试" };
                }
                // [A 治本] 等待 BGI IPC 就绪，避免重试的 task.start 在 BGI 刚启动时连不上
                await WaitForBgiIpcReadyAsync();
                continue;
            }

            try
            {
                // 会话守卫：阻断时直接失败返回，不进入重启回退（避免误杀本会话正在跑任务的 BGI）
                var blocked = CheckCrossSessionBlock(ipcClient, $"task.start 配置组「{groupName}」");
                if (blocked != null) return blocked;
                var payload = System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex, generation, takeoverTicket = _takeoverTicket });
                var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
                // [无损拒绝适配 b5386005] task_already_running = BGI 明确应答的业务拒绝（非传输失败），
                // 多半是 suspend 后旧任务退场慢（任务锁未释放）。等 1s 重发，最多 6 次
                // （与 suspend 5s 等锁 + 助手 P1-C 6s 轮询的总容忍对齐）。
                // 幂等安全：BGI 侧 generation 幂等登记已移到拒绝检查之后，被拒请求不会污染去重状态。
                for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
                {
                    ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）groupName={groupName}");
                    await Task.Delay(1000);
                    response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
                }
                if (response.Success)
                {
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
                // 只有 Connect 失败（BGI 未运行）才走裸拉起回退。
                return new CommandResult { Status = "failed", Message = $"BGI 拒绝启动配置组「{groupName}」（{response.ErrorCode ?? "unknown"}）：{response.ErrorMessage ?? "无详情"}。按无损拒绝语义未杀进程，请稍后重试或先停止当前任务" };
            }
            catch (Exception ex)
            {
                // 命令传输失败（BGI 活着）：核实对端事实后再定性，绝不进杀进程回退
                return await ReconcileV2TaskStartOutcomeAsync($"配置组「{groupName}」", ex);
            }
        }

        // 防御：不可达（循环内所有分支都有返回）；保守失败
        return new CommandResult { Status = "failed", Message = $"配置组 {groupName} 启动失败：内部流程异常" };
    }

    /// <summary>
    /// 启动一条龙：通过 IPC 发 task.start（含 startFromIndex），IPC 失败则杀进程重启
    /// 注意：不再预先发 task.stop（原因同 StartGroupAsync）。
    /// batchGroupNames：[批次名单 2026-09-13] 批次绑定列表，透传给 BGI 供一条龙组间跳过判定；
    /// 为 null（非批次来源/老路径）时 BGI 不跳过任何组。
    /// </summary>
    private async Task<CommandResult> StartOneClickAsync(string configName, int startFromIndex, int generation = 0, List<string>? batchGroupNames = null)
    {
        if (_isBatchInFlight?.Invoke() == true || IsResumeRetryInFlight)
            return new CommandResult { Status = "failed", ErrorCode = "batch_busy", Message = "现有批次/恢复尚未收尾，不能借用其执行权启动另一任务" };
        // [批次名单] 逗号分隔编码（与批次循环 Params 的 batchGroupNames 一致），null = 不携带
        var batchGroupNamesRaw = batchGroupNames is { Count: > 0 } ? string.Join(",", batchGroupNames) : null;

        // [任务策略] 按键门控（同 StartGroupAsync，固定行为：立即执行 + 执行完停止）：
        // 本机忙且无既有中断上下文时 suspend 抢占强制 v2；已有中断上下文走无损拒绝；空闲走原路径。
        // [弹窗竞态守卫] 同 StartGroupAsync：先等 set_task_enabled 落盘，再 suspend/启动
        await WaitConfigWritesDrainedAsync($"start_oneclick「{configName}」");

        if (await ShouldPreemptKeyPressAsync($"一条龙「{configName}」"))
        {
            // 抢占路径不透传批次名单（批次场景 MainViewModel 已先行 suspend，抢占极少命中批次项；
            // 不携带时 BGI 不跳过任何组，退化为老助手兼容行为，探针日志可观测）
            return await StartWithPreemptionAsync(FixedKeyPolicy, null, configName, startFromIndex, generation);
        }

        // [切片7] ext 任务队列通道（同 StartGroupAsync）；通道不可用走下方 v2 路径（逐字节保留）。
        var extClient = _externalClientProvider?.Invoke();
        if (extClient is { State: BgiExternalLinkState.Ready }
            && extClient.HasCapability(BgiExternalClient.CapabilityTaskQueue))
        {
            var queueResult = await TryStartViaQueueAsync(extClient, null, configName, startFromIndex, generation, batchGroupNamesRaw);
            if (queueResult != null)
            {
                return queueResult;
            }
            // null = 通道瞬态失败，落回 v2 路径
        }

        // [分层超时 2026-09-12] 失败语义分离（同 StartGroupAsync）：Connect 失败才走重启回退；
        // 命令传输失败（BGI 活着）绝不杀进程，先核实对端事实。
        // [A4.4] 回退新语义（同 StartGroupAsync）：Connect 失败 → 裸拉起（不带 --startOneDragon/
        // --batchGroups）→ 等 IPC 就绪 → 本方法内重试一次 task.start（批次名单走 IPC 协议字段
        // batchGroupNames 透传，BGI 龙内跳过判定不受影响）。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var ipcClient = new IpcClient();
            var connected = false;
            try
            {
                // 通过 IPC 发 task.start（一条龙内联启动）
                await ipcClient.ConnectAsync(3000);
                connected = true;
            }
            catch
            {
                // BGI 未运行/管道不可达：落到下方裸拉起回退
            }

            if (!connected)
            {
                if (attempt > 0)
                {
                    ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartOneClickAsync] 裸拉起后 IPC 仍不可达 configName={configName}");
                    return new CommandResult { Status = "failed", Message = $"一条龙 {configName} 启动失败：BGI 裸拉起后 IPC 仍不可达，请查看助手日志后重试" };
                }

                // [P2 仲裁] 同 StartGroupAsync：收编到仲裁器（串行 + 抑制 + 等退净），杀不掉不假成功
                ProbeLog($"[DUPLAUNCH_PROBE][CommandExecutor.StartOneClickAsync] IPC 不可达，回退裸拉起 BGI（不带执行参数）configName={configName}");
                if (!await _monitor.RestartBgiControlledAsync(null, "IPC回退-裸拉起"))
                {
                    Log($"[进程仲裁] 一条龙「{configName}」回退裸拉起未完成：旧进程未退净（可能提权运行）或启动调用失败，详见上方日志");
                    return new CommandResult { Status = "failed", Message = $"一条龙 {configName} 启动失败：BGI 回退裸拉起未完成（无法终止残留进程或启动失败），请查看助手日志后重试" };
                }
                await WaitForBgiIpcReadyAsync();
                continue;
            }

            try
            {
                // 会话守卫：阻断时直接失败返回，不进入重启回退（避免误杀本会话正在跑任务的 BGI）
                var blocked = CheckCrossSessionBlock(ipcClient, $"task.start 一条龙「{configName}」");
                if (blocked != null) return blocked;
                // [批次名单] 纯加法协议字段：老 BGI 忽略该字段，行为不变
                var payload = batchGroupNamesRaw != null
                    ? System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex, generation, batchGroupNames = batchGroupNamesRaw, takeoverTicket = _takeoverTicket })
                    : System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex, generation, takeoverTicket = _takeoverTicket });
                var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
                // [无损拒绝适配 b5386005] 同 StartGroupAsync：业务拒绝（任务运行中）等锁重试，最多 6 次
                for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
                {
                    ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）configName={configName}");
                    await Task.Delay(1000);
                    response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
                }
                if (response.Success)
                {
                    // 与 StartGroupAsync 对齐：解析 BGI 响应中的 status：cancelled = 一条龙执行中被取消（如 F11）
                    // 必须透传，否则助手端收不到取消信号、批次循环会继续执行下一个配置组（违背取消优先）。
                    if (!string.IsNullOrEmpty(response.Data))
                    {
                        try
                        {
                            var respData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response.Data);
                            var bgiStatus = respData.TryGetProperty("status", out var st) ? st.GetString() : null;
                            if (bgiStatus == "cancelled")
                            {
                                return new CommandResult { Status = "cancelled", Message = $"一条龙 {configName} 执行中被取消" };
                            }
                        }
                        catch
                        {
                            // Data 解析失败不影响，默认走 success 分支
                        }
                    }
                    return new CommandResult { Status = "success", Message = $"一条龙 {configName} 已启动" };
                }

                // [无损拒绝适配 b5386005] 业务拒绝不杀进程，直接失败返回（只有 Connect 失败才进回退）
                return new CommandResult { Status = "failed", Message = $"BGI 拒绝启动一条龙「{configName}」（{response.ErrorCode ?? "unknown"}）：{response.ErrorMessage ?? "无详情"}。按无损拒绝语义未杀进程，请稍后重试或先停止当前任务" };
            }
            catch (Exception ex)
            {
                // 命令传输失败（BGI 活着）：核实对端事实后再定性，绝不进杀进程回退
                return await ReconcileV2TaskStartOutcomeAsync($"一条龙「{configName}」", ex);
            }
        }

        // 防御：不可达（循环内所有分支都有返回）；保守失败
        return new CommandResult { Status = "failed", Message = $"一条龙 {configName} 启动失败：内部流程异常" };
    }

    /// <summary>
    /// [切片7] 经 ext 任务队列通道提交启动：先创建终态事件等待器（先订阅后动作，红线7），
    /// 再 Submit 入队拿 taskHandle，最后等 task.completed/failed/queueCancelled 事件。
    /// 返回 null = 通道瞬态失败（调用方落 v2 路径，1s×6 重试锤子与杀进程回退逐字节保留）；
    /// 明确业务拒绝（queue_full 等）直接失败返回，绝不进杀进程回退（与 b5386005 无损拒绝语义一致）。
    /// 返回时机与 v2 一致：任务真正执行完（或被取消）后才返回，批次循环语义不变。
    /// </summary>
    private async Task<CommandResult?> TryStartViaQueueAsync(
        BgiExternalClient ext, string? groupName, string? configName, int startFromIndex, int generation,
        string? batchGroupNames = null)
    {
        var desc = groupName != null ? $"配置组「{groupName}」" : $"一条龙「{configName}」";
        try
        {
            // 等待器先于 Submit 创建：adopted 场景下既有任务可能在我们 Submit 前就完成，
            // 其终态事件先入等待器缓冲，按句柄匹配时不丢
            using var waiter = ext.CreateTaskTerminalWaiter();
            var submit = await ext.SubmitTaskStartAsync(groupName, configName, startFromIndex, generation, batchGroupNames);
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

            // [终态可拉取 2026-09-09] 事件是快速路径、轮询是安全网：终态事件单帧丢失
            // （推送乱序被 revision 过滤误吞等，实机确诊）曾让批次循环在此永久挂起、
            // 后续配置组全部被吞。每 5s 切片等待，切片超时主动拉 queueStatus 校准；
            // 事件先达则立即返回（常态零轮询开销），事件丢失时 5s 内自愈。
            var waitStartedUtc = DateTime.UtcNow;
            while (DateTime.UtcNow - waitStartedUtc < TaskTerminalWaitTimeout)
            {
                var terminal = await waiter.WaitForHandleAsync(submit.TaskHandle, TerminalStatusPollInterval);
                if (terminal != null)
                {
                    // [假终态探针 2026-09-12] 启动类任务的 completed 终态在提交后 2s 内到达是异常信号
                    // （BGI 一条龙分支曾只等调度完成就登记 completed 的假终态事故），纯留痕不门控，
                    // 便于实机一次定位同类回归。adopted 场景（既有任务恰好收尾）可能误报，仅为警告。
                    if (terminal.Kind == BgiTaskTerminalKind.Completed && !terminal.Cancelled
                        && DateTime.UtcNow - waitStartedUtc < TimeSpan.FromSeconds(2))
                    {
                        ProbeLog($"[CommandExecutor][假终态探针] {desc} completed 终态到达耗时 <2s，疑似 BGI 侧假终态回归 taskHandle={submit.TaskHandle}");
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

                // 安全网轮询：终态事件 5s 未到达，拉取队列项生命周期校准。
                // null = 通道瞬态失败/对端老 BGI 无此操作 → 下一切片再试（不误判）。
                BgiTaskQueueStatus? queueStatus = null;
                try
                {
                    queueStatus = await ext.QueryTaskQueueStatusAsync(submit.TaskHandle);
                }
                catch (Exception pollEx) when (pollEx is InvalidOperationException or System.IO.IOException
                                               or TimeoutException or OperationCanceledException
                                               or System.Text.Json.JsonException)
                {
                    // 通道瞬态失败：下一切片再试
                }

                switch (queueStatus?.Status)
                {
                    case "completed":
                        ProbeLog($"[CommandExecutor][切片7] 终态事件未到达，安全网轮询命中 {desc} queueStatus=completed cancelled={queueStatus.Cancelled} taskHandle={submit.TaskHandle}（事件帧丢失已自愈）");
                        return queueStatus.Cancelled
                            ? new CommandResult { Status = "cancelled", Message = $"{desc} 执行中被取消" }
                            : new CommandResult { Status = "success", Message = $"{desc} 已启动并执行完成（队列通道，轮询校准）" };
                    case "queueCancelled":
                        ProbeLog($"[CommandExecutor][切片7] 终态事件未到达，安全网轮询命中 {desc} queueStatus=queueCancelled taskHandle={submit.TaskHandle}（事件帧丢失已自愈）");
                        return new CommandResult { Status = "cancelled", Message = $"{desc} 排队中被取消" };
                    case "failed":
                        ProbeLog($"[CommandExecutor][切片7] 终态事件未到达，安全网轮询命中 {desc} queueStatus=failed taskHandle={submit.TaskHandle}（事件帧丢失已自愈）");
                        return new CommandResult { Status = "failed", Message = $"{desc} 执行失败（{queueStatus.ErrorCode ?? "unknown"}）：{queueStatus.ErrorMessage ?? "无详情"}" };
                    case "not_found":
                        // 句柄在 BGI 侧不存在：BGI 已重启（任务随进程终止）或句柄从未存在。
                        // 按失败返回让批次循环继续后续组（各组独立提交，新 BGI 上可正常执行）。
                        ProbeLog($"[CommandExecutor][切片7] 安全网轮询 {desc} queueStatus=not_found taskHandle={submit.TaskHandle}（BGI 可能已重启，任务随进程终止）");
                        return new CommandResult { Status = "failed", Message = $"{desc} 任务句柄在 BGI 侧不存在（BGI 可能已重启，任务随进程终止），taskHandle={submit.TaskHandle}" };
                    default:
                        // pending/running/null：任务未终结或状态未知，继续下一切片
                        break;
                }
            }

            return new CommandResult { Status = "failed", Message = $"{desc} 等待执行结果超时（{TaskTerminalWaitTimeout.TotalHours}h 兜底），taskHandle={submit.TaskHandle}" };
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
        // [弹窗竞态守卫] 计入在途配置写入，启动命令会等计数归零再执行（见 WaitConfigWritesDrainedAsync）
        Interlocked.Increment(ref _inflightConfigWrites);
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
        finally
        {
            Interlocked.Decrement(ref _inflightConfigWrites);
        }
    }

    /// <summary>中断当前任务并保存上下文：IPC 发 task.suspend</summary>
    public async Task<CommandResult> ExecuteSuspendAsync(string hoeingGroupName)
    {
        await _suspendGate.WaitAsync();
        try { return await ExecuteSuspendCoreAsync(hoeingGroupName); }
        finally { _suspendGate.Release(); }
    }

    private async Task<CommandResult> ExecuteSuspendCoreAsync(string hoeingGroupName)
    {
        if (_externalClientProvider?.Invoke() is not { State: BgiExternalLinkState.Ready } capable
            || !capable.HasCapability("task.takeover"))
            return new CommandResult { Status = "failed", ErrorCode = "capability_required", Message = "BGI 尚未就绪或不支持可靠接管，请更新配套版本" };
        try
        {
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(3000);
            var blocked = CheckCrossSessionBlock(ipcClient, "task.suspend");
            if (blocked != null) return blocked;

            // 发 task.suspend（35s：A6 有界退出契约下 BGI 持响应等槽位确认，上限 QuiesceBound=30s，
            // 客户端必须留余量覆盖该上界才能读到 quiesceConfirmed 字段，否则"超时但实际挂起成功"
            // ——超时≠未执行，2026-09-12 分层超时）
            if (_takeoverTicket == null)
            {
                _takeoverEpoch = (await capable.QueryJobListAsync()).Epoch;
                if (_takeoverEpoch == null)
                    return new CommandResult { Status = "failed", ErrorCode = "epoch_unknown", Message = "无法确认目标 BGI 进程身份，不执行接管" };
                _takeoverTicket = Guid.NewGuid().ToString("N");
            }
            var ticket = _takeoverTicket;
            if (_externalClientProvider?.Invoke() is { } ext) ext.TakeoverTicket = ticket;
            var payload = System.Text.Json.JsonSerializer.Serialize(new { takeoverTicket = ticket,
                bgiEpoch = new { processId = _takeoverEpoch?.ProcessId, startTicksUtc = _takeoverEpoch?.StartTicksUtc } });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.suspend", Payload = payload }, V2TaskSuspendCommandTimeout);
            if (response.Success)
            {
                // [A6] 解析加法字段（可空读取：老 BGI 无此字段 → null，行为与原逻辑逐字一致）
                bool? liveTask = null;
                bool? quiesceConfirmed = null;
                string? confirmedTicket = null;
                if (!string.IsNullOrEmpty(response.Data))
                {
                    try
                    {
                        var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response.Data);
                        if (data.TryGetProperty("takeoverTicket", out var ticketEl)) confirmedTicket = ticketEl.GetString();
                        if (data.TryGetProperty("liveTask", out var ltEl)
                            && ltEl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                        {
                            liveTask = ltEl.GetBoolean();
                        }
                        if (data.TryGetProperty("quiesceConfirmed", out var qcEl)
                            && qcEl.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                        {
                            quiesceConfirmed = qcEl.GetBoolean();
                        }
                    }
                    catch
                    {
                        // Data 解析失败不影响：按老 BGI 语义继续（字段未知 = 不告警不升级）
                    }
                }

                // [A6 有界退出契约 ADR-2026-09-16] quiesceConfirmed=false = 30s 槽位未确认释放，
                // 旧任务可能卡死：响亮告警 + 按既有策略升级（进程仲裁器受控重启 BGI，
                // 沿用"有意杀死豁免崩溃误判"语义）。重启成功后槽位必然空闲，按成功返回让
                // 调用方（批次/按键）继续后续流程；被中断任务的内存恢复点随重启丢失，
                // 策略收尾的 Resume 分支会因 HasContext=false 自动退化为停止（既有守卫）。
                if (quiesceConfirmed != true || confirmedTicket != ticket)
                {
                    NotifyLoud("接管未完成", "BGI 未确认原流程退出，本次启动已中止，不自动重启 BGI。");
                    return new CommandResult { Status = "failed", ErrorCode = "quiesce_timeout", Message = "原流程未退出或对端缺少可靠接管能力" };
                }

                // [A6] liveTask=false = suspend 到达时无活体任务（组间缝隙）：BGI 已声明抢占意图门
                // （PreemptionGate），后续起步的任务会在持锁处自动让位并释放槽位——照常走 settle 等槽，
                // 门会保证槽位很快空出，不是异常，不告警。
                if (liveTask == false)
                {
                    Log("[接管] 原实例空闲，没有需要恢复的原任务；本批次已保留执行权");
                }

                // 返回包含被中断任务的上下文信息（给调用方日志用）
                return new CommandResult { Status = "success", Message = $"任务已中断" };
            }
            if (response.ErrorCode is "stale_epoch" or "stale_ticket") ClearTicket(ticket);
            return new CommandResult { Status = "failed", ErrorCode = response.ErrorCode, Message = $"task.suspend 失败: {response.ErrorMessage}" };
        }
        catch (Exception ex)
        {
            return new CommandResult { Status = "failed", Message = $"IPC task.suspend 失败: {ex.Message}" };
        }
    }

    /// <summary>[A6] 响亮告警收口：用户可见日志（AddLog + 文件日志双写）+ 托盘气泡。
    /// 抢占链任一段最终失败（quiesce 超界 / settle 中止 / 重试预算耗尽）绝不允许静默（ADR-2026-09-16）。</summary>
    private void NotifyLoud(string title, string message)
    {
        Log($"[告警] {message}");
        try
        {
            (System.Windows.Application.Current as App)?.ShowTrayBalloon(title, message);
        }
        catch
        {
            // 托盘不可用时静默（日志已保底）
        }
    }

    /// <summary>恢复原任务：IPC 发 task.resume。cancel=true 时清除上下文但不恢复。</summary>
    public async Task<CommandResult> ExecuteResumeAsync(bool cancel = false)
    {
        var ticket = _takeoverTicket;
        try
        {
            using var client = new IpcClient();
            await client.ConnectAsync(3000);
            var blocked = CheckCrossSessionBlock(client, "task.resume");
            if (blocked != null) return blocked;
            var payload = System.Text.Json.JsonSerializer.Serialize(new { cancel, takeoverTicket = ticket });
            var response = await client.SendCommandAsync(new IpcRequest { OpCode = "task.resume", Payload = payload }, TimeSpan.FromSeconds(35));
            if (!response.Success)
            {
                if (response.ErrorCode is "stale_ticket" or "stale_context") ClearTicket(ticket);
                return new CommandResult { Status = "failed", ErrorCode = response.ErrorCode, Message = response.ErrorMessage };
            }
            ClearTicket(ticket);
            var status = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(response.Data ?? "{}");
            var noContext = status.TryGetProperty("status", out var state) && state.GetString() == "cleared_not_resumed";
            return new CommandResult { Status = "success", ErrorCode = !cancel && noContext ? "no_context" : null,
                Message = cancel ? "已释放执行权，不恢复原任务" : noContext ? "无原任务需要恢复" : "恢复请求已确认受理" };
        }
        catch (Exception ex) { return new CommandResult { Status = "failed", Message = ex.Message }; }
    }

    private void ClearTicket(string? ticket)
    {
        if (_takeoverTicket != ticket) return;
        _takeoverTicket = null;
        _takeoverEpoch = null;
        if (_externalClientProvider?.Invoke() is { } ext && ext.TakeoverTicket == ticket) ext.TakeoverTicket = null;
    }

    /// <summary>
    /// [另案②] Resume 策略的 task_busy 有限重试：BGI 端恢复改为"确认起步才消费上下文"后，
    /// 槽位被占/派发未起步会回 task_busy 且保留上下文（不再是静默丢失）。这里 10s×3 有限重试；
    /// 重试等待窗口内置 _resumeRetryInFlight，孤儿对账/按键清账把该窗口视同批次在跑，
    /// 不误清待重试的上下文。重试耗尽返回最后一次失败结果——上下文仍保留在 BGI 侧，
    /// 由孤儿对账按死账清理（既有行为）并留痕。
    /// </summary>
    private async Task<CommandResult> ExecuteResumeWithBusyRetryAsync(Action<string>? log)
    {
        const int maxAttempts = 3;
        var busySeen = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var result = await ExecuteResumeAsync();
                if (result.Status == "success" || result.ErrorCode != "task_busy" || attempt >= maxAttempts)
                {
                    return result;
                }

                if (!busySeen)
                {
                    busySeen = true;
                    Interlocked.Exchange(ref _resumeRetryInFlight, 1);
                }
                log?.Invoke($"[任务冲突策略] BGI 任务槽位忙/恢复未起步，10 秒后重试恢复（第 {attempt}/{maxAttempts - 1} 次）...");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            // 覆盖从首个 task_busy 到重试终结的整个窗口（含在途 IPC），不只 10s 等待段
            if (busySeen)
            {
                Interlocked.Exchange(ref _resumeRetryInFlight, 0);
            }
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
        if (_takeoverTicket is { } ticket)
        {
            if (ext != null) ext.TakeoverTicket = ticket;
            var fields = System.Text.Json.Nodes.JsonNode.Parse(payloadJson ?? "{}")!.AsObject();
            fields["takeoverTicket"] = ticket;
            payloadJson = fields.ToJsonString();
        }
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
            && _isBatchInFlight?.Invoke() != true
            && !IsResumeRetryInFlight) // [另案②] 恢复重试窗口内的上下文是"待重试"而非孤儿，视同批次在跑
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
    /// 超时/通道不可用落回 200ms×30 轮询 task.status 兜底。
    /// [A6 状态确认 ADR-2026-09-16] 轮询耗尽后再查一次槽位状态做收口：复核仍忙
    /// （running=true 且无中断上下文）→ 返回 false，调用方必须中止本次启动尝试并响亮告警，
    /// 不再静默继续 task.start；复核空闲/有上下文 → 照常继续；复核查询失败（通道瞬态）→
    /// 保持旧容错语义照常继续（task.start 自有无损拒绝/裸拉起回退，不因一次查询失败误中止）。
    /// 供上线锄地（OnAllReadyConfirmedInternal）与按键抢占两条路径复用。
    /// </summary>
    public async Task<bool> WaitTaskSlotSettledAsync(string logTag, Action<string>? log = null)
    {
        for (var i = 0; i < 30; i++)
        {
            var response = await SendIpcPreferredAsync("task.status", null, 1000);
            if (response is { Success: true } && !string.IsNullOrEmpty(response.Data))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(response.Data);
                    if (doc.RootElement.TryGetProperty("executionIdle", out var idle)
                        && idle.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                }
                catch (System.Text.Json.JsonException) { }
            }
            await Task.Delay(200);
        }
        (log ?? _log)?.Invoke(logTag + " 未确认原流程退出，本次启动中止（未知不作空闲）");
        return false;
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
            // [另案②] 恢复重试窗口内的上下文是"待重试的恢复"而非孤儿，同样无损拒绝；
            // 无批次在跑且非重试窗口 = 孤儿残留，清上下文后按 running && !hasContext 走正常抢占闭环
            if (_isBatchInFlight?.Invoke() == true || IsResumeRetryInFlight)
            {
                Log($"[任务策略] 检测到 BGI 已有中断上下文（上线锄地批次进行中或恢复重试中），按键启动 {desc} 不抢占，走原有无损拒绝路径");
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
        // [A6] settle 状态确认：复核仍忙 → 中止 + 响亮告警（不静默继续动作；中断上下文保留在 BGI 侧，
        // 旧任务卡死时由用户或下一轮孤儿对账处置，绝不杀进程）
        if (!await WaitTaskSlotSettledAsync("[任务策略]", _log))
        {
            NotifyLoud("BGI 任务疑似卡死", "关闭游戏键中止：BGI 任务在 suspend 后超时未释放槽位（旧任务可能卡死），未执行关闭游戏；请检查 BGI 状态");
            return new CommandResult { Status = "failed", Message = "关闭游戏未执行：BGI 任务槽位在 suspend 后超时未释放（旧任务可能卡死），按有界退出语义中止（不杀进程），请检查 BGI 状态后重试" };
        }
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
        if (hotkeyConfigName is "CancelTaskHotkey" or "BgiEnabledHotkey" or "SuspendHotkey")
            return await ExecuteHotkeyAsync(hotkeyConfigName);
        if (_isBatchInFlight?.Invoke() == true || IsResumeRetryInFlight)
            return new CommandResult { Status = "failed", ErrorCode = "batch_busy", Message = "批次/恢复进行中，不执行另一个任务热键" };
        var desc = $"快捷键「{hotkeyConfigName}」";

        var status = await QueryTaskStatusAsync();
        // [P3 对账] 开头先清孤儿上下文（任务已结束但上下文未消费），避免残留把热键永久卡在无损拒绝
        status = await ReconcileOrphanedContextAsync(status, desc);
        if (status is { Running: true, HasContext: true })
        {
            // [P3 对账] 本机确有批次在跑才保持无损拒绝（保护进行中批次）；
            // [另案②] 恢复重试窗口内的上下文是"待重试的恢复"而非孤儿，同样无损拒绝；
            // 无批次在跑且非重试窗口 = 孤儿残留，清上下文后视为 running && !hasContext，走下方正常抢占闭环
            if (_isBatchInFlight?.Invoke() == true || IsResumeRetryInFlight)
            {
                Log($"[任务策略] 检测到 BGI 已有中断上下文（联机锄地批次进行中或恢复重试中），{desc} 不二次抢占，走无损拒绝");
                return new CommandResult { Status = "failed", Message = $"{desc} 未执行：联机锄地批次进行中或恢复重试中（已有中断上下文），按无损拒绝语义不打断，请稍后再试" };
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
        // [A6] settle 状态确认：复核仍忙 → 中止 + 响亮告警（中断上下文保留在 BGI 侧，不杀进程）
        if (!await WaitTaskSlotSettledAsync("[任务策略]", _log))
        {
            NotifyLoud("BGI 任务疑似卡死", $"{desc} 中止：BGI 任务在 suspend 后超时未释放槽位（旧任务可能卡死），热键未下发；请检查 BGI 状态");
            return new CommandResult { Status = "failed", Message = $"{desc} 未执行：BGI 任务槽位在 suspend 后超时未释放（旧任务可能卡死），按有界退出语义中止（不杀进程），请检查 BGI 状态后重试" };
        }
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
        // [A6] settle 状态确认：复核仍忙 → 中止本次启动尝试 + 响亮告警，不再静默继续 task.start。
        // 不做策略收尾：旧任务卡死仍持槽，resume 无意义、清上下文会白丢恢复点——中断上下文保留在
        // BGI 侧，由用户处置或下一轮孤儿对账清理（按键路径绝不杀进程）。
        if (!await WaitTaskSlotSettledAsync("[任务冲突策略]", _log))
        {
            NotifyLoud("BGI 任务疑似卡死", $"按键启动 {desc} 中止：BGI 任务在 suspend 后超时未释放槽位（旧任务可能卡死），新任务未下发；请检查 BGI 状态");
            return new CommandResult { Status = "failed", Message = $"抢占启动 {desc} 中止：BGI 任务槽位在 suspend 后超时未释放（旧任务可能卡死），按有界退出语义未下发新任务（不杀进程），请检查 BGI 状态后重试" };
        }

        // 3. v2 IPC task.start（抢占路径强制 v2，跳过 ext 队列；传输失败不杀进程）
        // [A6] v2 通道无 preempt 字段：本路径前置 suspend+settle 已自行腾空槽位，无需抢占标志
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
                ? System.Text.Json.JsonSerializer.Serialize(new { groupName, startFromIndex, generation, takeoverTicket = _takeoverTicket })
                : System.Text.Json.JsonSerializer.Serialize(new { configName, startFromIndex, generation, takeoverTicket = _takeoverTicket });
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
            for (var retry = 0; !response.Success && response.ErrorCode == "task_already_running" && retry < 6; retry++)
            {
                ProbeLog($"[CommandExecutor] task.start 被无损拒绝（任务运行中），1s 后重试（{retry + 1}/6）{desc}");
                await Task.Delay(1000);
                response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.start", Payload = payload }, V2TaskStartCommandTimeout);
            }
            if (response.Success)
            {
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
            // 抢占/指定任务路径绝不杀进程：传输失败先核实对端事实（超时≠未执行）再定性——
            // 直接返回 failed 会让 StartWithPreemptionAsync 误判"未启动"而触发错误的策略收尾
            // （resume 旧任务与已在跑的新任务打架，另案②的实机触发器）。
            return await ReconcileV2TaskStartOutcomeAsync(desc, ex);
        }
    }

    /// <summary>
    /// [分层超时 2026-09-12] v2 task.start 命令传输失败后的事实核实：
    /// 超时/断流 ≠ 未执行（at-least-once）——请求已到达 BGI，副作用可能正在发生。
    /// 轮询 task.status 核实：在跑 = 启动实际生效（响应帧丢失/超时），等其执行完后按 success 返回
    /// （取消状态在此降级路径不可知，F11 场景由 BGI 侧 HandleTaskResume 的 WasCancelled 守卫兜底）；
    /// 确认不在跑 = 按失败返回；BGI 持续不可达（1 分钟）= 任务随进程终止，按失败返回。
    /// 绝不杀进程、绝不假成功。
    /// </summary>
    private async Task<CommandResult> ReconcileV2TaskStartOutcomeAsync(string desc, Exception transportError)
    {
        var reason = transportError.GetBaseException().Message;
        Log($"[CommandExecutor] v2 task.start {desc} 命令传输失败（{reason}），核实对端真实状态（超时≠未执行，绝不杀进程）");
        // 双探测收窄竞态窗：请求帧可能已被 BGI 读入但任务尚未起到 running，
        // 单次探测"不在跑"会把"即将启动"误判为"未生效"
        var probe = await QueryTaskStatusAsync();
        if (probe is not { Running: true })
        {
            await Task.Delay(2000);
            probe = await QueryTaskStatusAsync();
        }
        if (probe is not { Running: true })
        {
            return new CommandResult { Status = "failed", Message = $"{desc} 启动失败：{reason}（已核实 BGI 侧无任务在跑，启动未生效或任务随进程终止）" };
        }

        Log($"[CommandExecutor] 核实结果：{desc} 实际已在 BGI 侧执行中（响应帧丢失/超时），等待其执行完（5s 轮询，上限 {V2TaskStartCommandTimeout.TotalHours}h）...");
        var deadline = DateTime.UtcNow + V2TaskStartCommandTimeout;
        var consecutiveProbeFailures = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TaskPollInterval);
            probe = await QueryTaskStatusAsync();
            if (probe is { Running: false })
            {
                return new CommandResult { Status = "failed", ErrorCode = "result_unknown", Message = $"{desc} 已不在运行，但缺少对应终态证据，结果未知，不能报告成功" };
            }
            if (probe is null)
            {
                // BGI 持续不可达 = 进程已死（任务随进程终止）或卡死，按失败定性，不无限等
                if (++consecutiveProbeFailures >= 12)
                {
                    return new CommandResult { Status = "failed", Message = $"{desc} 执行中 BGI 持续不可达（1 分钟），任务可能已随进程终止，请检查 BGI 状态" };
                }
            }
            else
            {
                consecutiveProbeFailures = 0;
            }
        }
        return new CommandResult { Status = "failed", Message = $"{desc} 执行中但等待超时（{V2TaskStartCommandTimeout.TotalHours}h 兜底），请检查 BGI 状态" };
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
                    log?.Invoke("[任务冲突策略] 无原任务需要恢复，释放本批次执行权");
                    await ExecuteResumeAsync(cancel: true);
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
                // [另案②] task_busy（槽位占用/派发未起步，BGI 已保留上下文）走 10s×3 有限重试；
                // 其他失败（无上下文/传输失败/重试耗尽）直接响亮失败
                var resumeResult = await ExecuteResumeWithBusyRetryAsync(log);
                if (resumeResult.Status == "success")
                {
                    log?.Invoke(resumeResult.ErrorCode == "no_context"
                        ? "[任务冲突策略] 无原任务需要恢复" : "[任务冲突策略] 原任务恢复尝试已确认受理");
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
                var release = await ExecuteResumeAsync(cancel: true);
                if (release.Status != "success")
                {
                    log?.Invoke($"[任务冲突策略] 执行权未确认释放，不启动指定任务: {release.Message}");
                    return;
                }
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
