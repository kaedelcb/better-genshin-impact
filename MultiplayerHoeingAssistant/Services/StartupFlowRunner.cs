using System.Diagnostics;
using System.IO;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · 启动中心——启动流程执行引擎（树形分支版）。
/// 主链顺序执行；条件节点求值后递归执行「是」或「否」子链，子链跑完回到父链继续（分支汇合）。
/// 「结束流程」节点经 <see cref="FlowEndException"/> 逐层展开终止整条流程。
/// 全程可取消（CancellationToken），每步结果经 _log 写入冒险日志。
///
/// BGI 进程级动作（启动/关闭）经注入的 _bgiExecutor 委托路由到 MainViewModel → CommandExecutor，
/// 继承既有跨会话守卫；监控端（无本地 BGI）由该委托返回失败，引擎记日志后继续后续节点。
/// 「进入任务中心执行」节点经 _enterTaskCenter 委托交接（任务中心落地前为占位实现）。
/// </summary>
public sealed class StartupFlowRunner
{
    /// <summary>BGI 命令执行入口（cmd, params）→ 结果。由 MainViewModel 注入。</summary>
    private readonly Func<string, Dictionary<string, object>?, Task<CommandResult>> _bgiExecutor;
    /// <summary>进入任务中心交接（null 或占位实现=任务中心未落地）。</summary>
    private readonly Func<Task> _enterTaskCenter;
    /// <summary>定时触发器挂载入口（由宿主 VM 注入：负责登记定时状态、到点执行 FireSteps、取消）。</summary>
    private readonly Action<StartupStep> _armTimer;
    /// <summary>人工确认弹窗入口（由宿主 VM 注入：UI 线程弹窗，返回 (是/否, 判断依据描述)）。</summary>
    private readonly Func<StartupStep, CancellationToken, Task<(bool passed, string desc)>> _confirmHandler;
    /// <summary>BGI 任务状态快照提供方（bgiTaskRunning/bgiTaskName 条件用；由宿主注入，读 MainViewModel 的 10s 状态缓存，不新起 IPC）。</summary>
    private readonly Func<ControlStatus?> _statusProvider;
    private readonly Action<string> _log;

    /// <summary>
    /// 节点运行态回报（可选，由宿主 VM 设置）：每个节点 执行前→Running、执行后→终态，
    /// 条件节点回报 CondTrue/CondFalse 并附判断依据。执行路径可视化的数据来源。
    /// </summary>
    public Action<StartupStep, NodeRunState, string?>? NodeStateSink { get; set; }

    /// <summary>游戏进程名（国服 Yuanshen / 国际服 GenshinImpact），与 ScreenshotService 口径一致。</summary>
    private static readonly string[] GameProcessNames = ["Yuanshen", "GenshinImpact"];

    public StartupFlowRunner(
        Func<string, Dictionary<string, object>?, Task<CommandResult>> bgiExecutor,
        Func<Task> enterTaskCenter,
        Action<StartupStep> armTimer,
        Func<StartupStep, CancellationToken, Task<(bool passed, string desc)>> confirmHandler,
        Action<string> log,
        Func<ControlStatus?> statusProvider)
    {
        _bgiExecutor = bgiExecutor;
        _enterTaskCenter = enterTaskCenter;
        _armTimer = armTimer;
        _confirmHandler = confirmHandler;
        _log = log;
        _statusProvider = statusProvider;
    }

    /// <summary>「结束流程」节点抛出的内部控制流异常（逐层展开到顶层捕获，终止整条流程）。</summary>
    private sealed class FlowEndException : Exception;

    /// <summary>
    /// 从主链开始执行整条启动流程。本方法不抛业务异常（单节点失败不炸整条链）；
    /// OperationCanceledException 会原样抛出给调用方（取消语义）。
    /// </summary>
    public async Task RunAsync(IReadOnlyList<StartupStep> steps, CancellationToken ct)
    {
        if (steps.Count == 0)
        {
            _log("[槲寄生] 启动流程为空，无节点可执行");
            return;
        }

        _log($"[槲寄生] 启动流程开始执行（主链 {steps.Count} 个节点）");
        try
        {
            await RunChainAsync(steps, ct, depth: 0);
            _log("[槲寄生] 启动流程执行完毕");
        }
        catch (FlowEndException)
        {
            _log("[槲寄生] 启动流程已由「结束流程」节点终止");
        }
    }

    /// <summary>递归执行一条节点链（主链或条件的子链）。depth 仅用于日志缩进可读性。</summary>
    private async Task RunChainAsync(IReadOnlyList<StartupStep> steps, CancellationToken ct, int depth)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            var indent = new string('　', depth); // 全角空格缩进，分支层级一目了然
            var display = DisplayName(step, i);

            if (!step.Enabled)
            {
                _log($"[槲寄生] {indent}节点「{display}」已禁用，跳过");
                Report(step, NodeRunState.Skipped, "已禁用");
                continue;
            }

            if (step.NodeType == "condition")
            {
                Report(step, NodeRunState.Running);
                // 人工确认是异步交互（UI 弹窗），其余条件是同步求值
                var (passed, desc) = step.Kind == StartupStepKinds.ManualConfirm
                    ? await _confirmHandler(step, ct)
                    : EvaluateCondition(step, _statusProvider());
                _log($"[槲寄生] {indent}条件「{display}」：{desc} → {(passed ? "是" : "否")}");
                Report(step, passed ? NodeRunState.CondTrue : NodeRunState.CondFalse, desc);
                var branch = passed ? step.TrueSteps : step.FalseSteps;
                if (branch.Count == 0)
                {
                    _log($"[槲寄生] {indent}「{(passed ? "是" : "否")}」分支为空，继续后续节点");
                }
                else
                {
                    await RunChainAsync(branch, ct, depth + 1);
                }
                continue;
            }

            // 动作节点
            Report(step, NodeRunState.Running);
            var ok = await ExecuteActionAsync(step, display, indent, ct);
            if (!ok)
            {
                // 动作失败统一记日志后继续后续节点（启动期动作失败不应阻塞整链，
                // 需要严格守门时用条件节点包一层分支）
                _log($"[槲寄生] {indent}节点「{display}」执行未成功，继续后续节点");
                Report(step, NodeRunState.Failed);
            }
            else
            {
                Report(step, NodeRunState.Success);
            }
        }
    }

    /// <summary>向宿主回报节点运行态（未设置接收方时为空操作）。</summary>
    private void Report(StartupStep step, NodeRunState state, string? note = null)
    {
        try
        {
            NodeStateSink?.Invoke(step, state, note);
        }
        catch
        {
            // 可视化回报绝不影响流程执行
        }
    }

    /// <summary>节点显示名（用户命名优先，否则用类型默认名；旧版遗留类型显示原名）。</summary>
    public static string DisplayName(StartupStep step, int index)
    {
        if (!string.IsNullOrWhiteSpace(step.Name)) return step.Name;
        return StartupStepKinds.Find(step.Kind)?.DisplayName ?? step.Kind;
    }

    // ================= 条件求值 =================

    /// <summary>求值条件节点，返回 (是否通过, 人类可读的判断依据)。
    /// status 为 BGI 任务状态快照（bgiTaskRunning/bgiTaskName 用），null=无快照（视为未在跑任务，依据中留痕）。</summary>
    public static (bool passed, string desc) EvaluateCondition(StartupStep step, ControlStatus? status = null)
    {
        switch (step.Kind)
        {
            case StartupStepKinds.TimeRange:
            {
                if (!TimeOnly.TryParse(step.TimeStart, out var start) || !TimeOnly.TryParse(step.TimeEnd, out var end))
                    return (false, $"时间格式无效（{step.TimeStart}~{step.TimeEnd}）");
                var now = TimeOnly.FromDateTime(DateTime.Now);
                // 起点<=终点：区间内；起点>终点：跨零点（>=起点 或 <=终点）
                var inRange = start <= end
                    ? now >= start && now <= end
                    : now >= start || now <= end;
                return (inRange, $"当前 {now:HH:mm}，区间 {step.TimeStart}~{step.TimeEnd}");
            }
            case StartupStepKinds.Weekday:
            {
                var d = (int)DateTime.Now.DayOfWeek;
                d = d == 0 ? 7 : d; // 1=周一 … 7=周日
                return (step.Weekdays.Contains(d), $"今天周{"一二三四五六日"[d - 1]}，勾选 [{string.Join(",", step.Weekdays)}]");
            }
            case StartupStepKinds.BgiRunning:
            {
                var running = BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length > 0;
                return (running == step.ExpectRunning, $"BGI {(running ? "正在运行" : "未运行")}，期望 {(step.ExpectRunning ? "运行" : "未运行")}");
            }
            case StartupStepKinds.GameRunning:
            {
                var running = IsGameRunning();
                return (running == step.ExpectRunning, $"游戏 {(running ? "正在运行" : "未运行")}，期望 {(step.ExpectRunning ? "运行" : "未运行")}");
            }
            case StartupStepKinds.ProcessRunning:
            {
                if (string.IsNullOrWhiteSpace(step.ProcessName))
                    return (false, "未填写进程名");
                var running = IsProcessRunning(step.ProcessName);
                return (running == step.ExpectRunning, $"进程 {step.ProcessName} {(running ? "存在" : "不存在")}，期望 {(step.ExpectRunning ? "存在" : "不存在")}");
            }
            case StartupStepKinds.BgiTaskRunning:
            {
                // 判断口径用快照 TaskRunning（权威：BGI 侧任务信号量），不用任务名非空——任务停止后任务名有残留窗口
                var running = status?.TaskRunning ?? false;
                var actual = status == null ? "无状态快照，视为未在跑任务" : running ? "有任务在执行" : "空闲";
                return (running == step.ExpectRunning, $"BGI {actual}，期望 {(step.ExpectRunning ? "在跑任务" : "空闲")}");
            }
            case StartupStepKinds.BgiTaskName:
            {
                if (string.IsNullOrWhiteSpace(step.TaskName))
                    return (false, "未填写任务名");
                if (status == null || !status.TaskRunning)
                    return (false, $"BGI 未在跑任务{(status == null ? "（无状态快照）" : "")}，无法匹配「{step.TaskName}」");
                var hit = (status.CurrentTaskName?.Contains(step.TaskName, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (status.CurrentTaskGroupName?.Contains(step.TaskName, StringComparison.OrdinalIgnoreCase) ?? false);
                var current = status.CurrentTaskGroupName is { Length: > 0 } g
                    ? status.CurrentTaskName is { Length: > 0 } n ? $"{g} · {n}" : g
                    : status.CurrentTaskName ?? "（未上报任务名）";
                return (hit, $"当前任务「{current}」{(hit ? "包含" : "不包含")}「{step.TaskName}」");
            }
            default:
                return (false, $"未知条件类型 {step.Kind}（按不满足处理）");
        }
    }

    // ================= 动作执行 =================

    private async Task<bool> ExecuteActionAsync(StartupStep step, string display, string indent, CancellationToken ct)
    {
        try
        {
            switch (step.Kind)
            {
                case StartupStepKinds.StartBgi:
                {
                    // 先关闭再启动：BGI 已在运行时 start_bgi 走不抢占策略（参数不会生效），
                    // 勾选后先强杀本会话 BGI 再带参数启动
                    if (step.KillBeforeStart && BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length > 0)
                    {
                        var kr = await _bgiExecutor("kill_bgi", null);
                        _log($"[槲寄生] {indent}启动前先关闭 BGI：{kr.Message}");
                        // 等进程退出，避免紧接着启动撞上未退净的旧进程
                        await Task.Delay(2000, ct);
                    }
                    var p = string.IsNullOrWhiteSpace(step.Arguments)
                        ? null
                        : new Dictionary<string, object> { ["args"] = step.Arguments };
                    var r = await _bgiExecutor("start_bgi", p);
                    _log($"[槲寄生] {indent}启动 BGI：{r.Message}");
                    return r.Status == "success";
                }
                case StartupStepKinds.StopBgi:
                {
                    var r = await _bgiExecutor("kill_bgi", null);
                    _log($"[槲寄生] {indent}关闭 BGI：{r.Message}");
                    return r.Status == "success";
                }
                case StartupStepKinds.EnterTaskCenter:
                {
                    _log($"[槲寄生] {indent}环境准备完毕，进入任务中心执行任务序列");
                    await _enterTaskCenter();
                    return true;
                }
                case StartupStepKinds.EndFlow:
                {
                    _log($"[槲寄生] {indent}「{display}」：终止启动流程");
                    Report(step, NodeRunState.Success, "已终止整条流程");
                    throw new FlowEndException();
                }
                case StartupStepKinds.TimerTrigger:
                {
                    if (!TimeOnly.TryParse(step.TriggerTime, out _))
                    {
                        _log($"[槲寄生] {indent}定时触发器「{display}」时间格式无效（{step.TriggerTime}），跳过");
                        return false;
                    }
                    if (step.FireSteps.Count == 0)
                    {
                        _log($"[槲寄生] {indent}定时触发器「{display}」的「到点执行」链为空，不挂载");
                        return false;
                    }
                    _armTimer(step);
                    return true;
                }
                // 旧版遗留节点（目录已移除，旧配置仍可执行）
                case StartupStepKinds.StartGroup:
                {
                    if (string.IsNullOrWhiteSpace(step.TaskName))
                    {
                        _log($"[槲寄生] {indent}节点「{display}」未填写配置组名，跳过");
                        return false;
                    }
                    var r = await _bgiExecutor("start_group", new Dictionary<string, object> { ["groupName"] = step.TaskName });
                    _log($"[槲寄生] {indent}启动配置组「{step.TaskName}」（旧版节点）：{r.Message}");
                    return r.Status == "success";
                }
                case StartupStepKinds.StartOneClick:
                {
                    if (string.IsNullOrWhiteSpace(step.TaskName))
                    {
                        _log($"[槲寄生] {indent}节点「{display}」未填写一条龙名，跳过");
                        return false;
                    }
                    var r = await _bgiExecutor("start_oneclick", new Dictionary<string, object> { ["configName"] = step.TaskName });
                    _log($"[槲寄生] {indent}启动一条龙「{step.TaskName}」（旧版节点）：{r.Message}");
                    return r.Status == "success";
                }
                case StartupStepKinds.StartGame:
                case StartupStepKinds.StartProgram:
                {
                    return StartProcess(step, display, indent);
                }
                case StartupStepKinds.KillProgram:
                {
                    return KillProcess(step, display, indent);
                }
                case StartupStepKinds.RunCmd:
                {
                    if (string.IsNullOrWhiteSpace(step.Arguments))
                    {
                        _log($"[槲寄生] {indent}节点「{display}」未填写 CMD 命令，跳过");
                        return false;
                    }
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + step.Arguments,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    Process.Start(psi);
                    _log($"[槲寄生] {indent}已执行 CMD：{step.Arguments}");
                    return true;
                }
                case StartupStepKinds.Wait:
                {
                    var seconds = Math.Max(0, step.WaitSeconds);
                    _log($"[槲寄生] {indent}等待 {seconds} 秒…");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    return true;
                }
                default:
                    _log($"[槲寄生] {indent}未知动作类型 {step.Kind}，跳过");
                    return false;
            }
        }
        catch (FlowEndException)
        {
            throw; // 流程终止控制流，逐层展开
        }
        catch (OperationCanceledException)
        {
            throw; // 取消是正常控制流，抛给 RunAsync 的调用方处理
        }
        catch (Exception ex)
        {
            _log($"[槲寄生] {indent}节点「{display}」执行异常：{ex.Message}");
            return false;
        }
    }

    private bool StartProcess(StartupStep step, string display, string indent)
    {
        if (string.IsNullOrWhiteSpace(step.Path))
        {
            _log($"[槲寄生] {indent}节点「{display}」未填写程序路径，跳过");
            return false;
        }
        if (!File.Exists(step.Path))
        {
            _log($"[槲寄生] {indent}节点「{display}」路径不存在：{step.Path}");
            return false;
        }
        var psi = new ProcessStartInfo
        {
            FileName = step.Path,
            Arguments = step.Arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(step.Path) ?? "",
        };
        Process.Start(psi);
        _log($"[槲寄生] {indent}已启动程序：{step.Path}{(string.IsNullOrWhiteSpace(step.Arguments) ? "" : $"（参数 {step.Arguments}）")}");
        return true;
    }

    /// <summary>按进程名强制结束当前会话的指定程序（与条件检测同口径：仅当前 Windows 会话，不碰其他用户/会话的同名进程）。</summary>
    private bool KillProcess(StartupStep step, string display, string indent)
    {
        if (string.IsNullOrWhiteSpace(step.ProcessName))
        {
            _log($"[槲寄生] {indent}节点「{display}」未填写进程名，跳过");
            return false;
        }
        var name = step.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? step.ProcessName[..^4]
            : step.ProcessName;
        var session = Process.GetCurrentProcess().SessionId;
        Process[] targets;
        try
        {
            targets = Process.GetProcessesByName(name).Where(p => p.SessionId == session).ToArray();
        }
        catch (Exception ex)
        {
            _log($"[槲寄生] {indent}枚举进程 {name} 失败：{ex.Message}");
            return false;
        }
        if (targets.Length == 0)
        {
            _log($"[槲寄生] {indent}进程 {name} 当前会话中不存在，无需关闭");
            return true;
        }
        var killed = 0;
        foreach (var p in targets)
        {
            try
            {
                p.Kill();
                killed++;
            }
            catch (Exception ex)
            {
                _log($"[槲寄生] {indent}结束进程 {name}（PID {p.Id}）失败：{ex.Message}");
            }
            finally
            {
                p.Dispose();
            }
        }
        _log($"[槲寄生] {indent}已结束进程 {name} ×{killed}/{targets.Length}");
        return killed == targets.Length;
    }

    // ================= 进程检测（全部按当前 Windows 会话过滤） =================

    private static bool IsGameRunning()
    {
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            foreach (var name in GameProcessNames)
            {
                if (Process.GetProcessesByName(name).Any(p => p.SessionId == session))
                    return true;
            }
        }
        catch
        {
            // 进程枚举失败按未运行处理（与 IsProcessRunning 同策略）
        }
        return false;
    }

    private static bool IsProcessRunning(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        var session = Process.GetCurrentProcess().SessionId;
        try
        {
            return Process.GetProcessesByName(name).Any(p => p.SessionId == session);
        }
        catch
        {
            return false;
        }
    }
}
