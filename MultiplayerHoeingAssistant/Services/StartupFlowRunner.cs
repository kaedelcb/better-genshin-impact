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
/// 「进入任务中心执行」节点经 _enterTaskCenter 委托真实移交（R4.9：双重身份 + 结构化回执，移交终态后逐层终止本启动链）。
/// </summary>
public sealed class StartupFlowRunner
{
    /// <summary>BGI 命令执行入口（cmd, params）→ 结果。由 MainViewModel 注入。</summary>
    private readonly Func<string, Dictionary<string, object>?, Task<CommandResult>> _bgiExecutor;
    /// <summary>进入任务中心移交入口（R4.9：携带双重身份请求 → 结构化回执；由宿主 VM 注入，路由到 TaskCenterHost.RegisterHandoffAsync）。</summary>
    private readonly Func<StartupHandoffRequest, CancellationToken, Task<StartupHandoffResult>> _enterTaskCenter;
    /// <summary>定时触发器挂载入口（由宿主 VM 注入：负责登记定时状态、到点执行 FireSteps、取消）。</summary>
    private readonly Action<StartupStep> _armTimer;
    /// <summary>电子狗挂载入口（由宿主 VM 注入：负责登记盯梢状态、边沿触发执行 FireSteps、取消）。</summary>
    private readonly Action<StartupStep> _armWatchdog;
    /// <summary>日志触发器挂载入口（由宿主 VM 注入：负责订阅日志流、命中关键字执行 FireSteps、取消）。可为 null（宿主未接日志源时该节点记日志跳过）。</summary>
    private readonly Action<StartupStep>? _armLogTrigger;
    /// <summary>人工确认弹窗入口（由宿主 VM 注入：UI 线程弹窗，返回 (是/否, 判断依据描述)）。</summary>
    private readonly Func<StartupStep, CancellationToken, Task<(bool passed, string desc)>> _confirmHandler;
    /// <summary>BGI 任务状态快照提供方（bgiTaskRunning/bgiTaskName 条件用；由宿主注入，读 MainViewModel 的 10s 状态缓存，不新起 IPC）。</summary>
    private readonly Func<ControlStatus?> _statusProvider;
    private readonly Func<StartupStep, CancellationToken, Task<ControlStatus?>> _targetStatusProvider;
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
        Func<StartupHandoffRequest, CancellationToken, Task<StartupHandoffResult>> enterTaskCenter,
        Action<StartupStep> armTimer,
        Func<StartupStep, CancellationToken, Task<(bool passed, string desc)>> confirmHandler,
        Action<string> log,
        Func<ControlStatus?> statusProvider,
        Action<StartupStep> armWatchdog,
        Func<StartupStep, CancellationToken, Task<ControlStatus?>>? targetStatusProvider = null,
        Action<StartupStep>? armLogTrigger = null)
    {
        _bgiExecutor = bgiExecutor;
        _enterTaskCenter = enterTaskCenter;
        _armTimer = armTimer;
        _confirmHandler = confirmHandler;
        _log = log;
        _statusProvider = statusProvider;
        _targetStatusProvider = targetStatusProvider ?? ((_, _) => Task.FromResult<ControlStatus?>(null));
        _armWatchdog = armWatchdog;
        _armLogTrigger = armLogTrigger;
    }

    /// <summary>「结束流程」节点抛出的内部控制流异常（逐层展开到顶层捕获，终止整条流程）。</summary>
    private sealed class FlowEndException : Exception;

    /// <summary>「进入任务中心执行」节点终态已定（移交成功/已受理/拒绝且须终止链）后抛出的内部控制流异常
    /// （R4.9 §7：逐层终止当前启动链及全部分支回溯，仅最外层 RunAsync 捕获）。</summary>
    private sealed class HandoffCompletedException(string message) : Exception(message);

    /// <summary>
    /// 一次启动链执行的上下文（R4.9 §2/§6.1）：每次 RunAsync 一个实例，参数透传到每个动作节点（不用共享字段，四入口并发共存）。
    /// ExecutionId=本次执行身份；Trigger=触发来源（手动/自动主流程为 null）；
    /// BgiTaskCommitFacts=本链内直接提交 BGI 任务的提交事实（混用检测主证据，成功或结果不确定均计、明确失败不计）；
    /// FailureNote=动作节点返回 false 时附带的可读原因（RunChainAsync 单处回报后消费）。
    /// </summary>
    private sealed class RunContext
    {
        public required string ExecutionId { get; init; }
        public StartupTriggerInfo? Trigger { get; init; }
        public List<string> BgiTaskCommitFacts { get; } = [];
        public string? FailureNote { get; set; }
        /// <summary>执行身份短码（日志/回报用，并发执行可区分，R4.9 §7）。</summary>
        public string ShortId => ExecutionId.Length >= 8 ? ExecutionId[..8] : ExecutionId;
    }

    /// <summary>
    /// 从主链开始执行整条启动流程。本方法不抛业务异常（单节点失败不炸整条链）；
    /// OperationCanceledException 会原样抛出给调用方（取消语义）。
    /// </summary>
    public async Task RunAsync(IReadOnlyList<StartupStep> steps, CancellationToken ct, StartupTriggerInfo? trigger = null)
    {
        if (steps.Count == 0)
        {
            _log("[槲寄生] 启动流程为空，无节点可执行");
            return;
        }

        var ctx = new RunContext { ExecutionId = Guid.NewGuid().ToString("N"), Trigger = trigger };
        _log($"[槲寄生] 启动流程开始执行（主链 {steps.Count} 个节点，执行 {ctx.ShortId}）");
        try
        {
            await RunChainAsync(steps, ct, ctx, depth: 0);
            _log($"[槲寄生] 启动流程执行完毕（执行 {ctx.ShortId}）");
        }
        catch (FlowEndException)
        {
            _log($"[槲寄生] 启动流程已由「结束流程」节点终止（执行 {ctx.ShortId}）");
        }
        catch (HandoffCompletedException ex)
        {
            _log($"[槲寄生] {ex.Message}（执行 {ctx.ShortId}）");
        }
    }

    /// <summary>递归执行一条节点链（主链或条件的子链）。depth 仅用于日志缩进可读性。</summary>
    private async Task RunChainAsync(IReadOnlyList<StartupStep> steps, CancellationToken ct, RunContext ctx, int depth)
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
                // 人工确认是异步交互（UI 弹窗）；其余条件走带防抖的求值（外部状态类条件需稳定性确认）
                var (passed, desc) = step.Kind == StartupStepKinds.ManualConfirm
                    ? await _confirmHandler(step, ct)
                    : await EvaluateConditionStableAsync(step, ct);
                if (passed is null)
                {
                    _log($"[槲寄生] {indent}条件「{display}」状态未知：{desc}；终止本次流程，不执行任一分支");
                    Report(step, NodeRunState.Failed, desc);
                    throw new FlowEndException();
                }
                _log($"[槲寄生] {indent}条件「{display}」：{desc} → {(passed.Value ? "是" : "否")}");
                Report(step, passed.Value ? NodeRunState.CondTrue : NodeRunState.CondFalse, desc);
                var branch = passed.Value ? step.TrueSteps : step.FalseSteps;
                if (branch.Count == 0)
                {
                    _log($"[槲寄生] {indent}「{(passed.Value ? "是" : "否")}」分支为空，继续后续节点");
                }
                else
                {
                    await RunChainAsync(branch, ct, ctx, depth + 1);
                }
                continue;
            }

            // 动作节点
            Report(step, NodeRunState.Running);
            var ok = await ExecuteActionAsync(step, display, indent, ct, ctx);
            if (!ok)
            {
                // 动作失败统一记日志后继续后续节点（启动期动作失败不应阻塞整链，
                // 需要严格守门时用条件节点包一层分支）；FailureNote 单次消费，不污染后续节点
                var note = ctx.FailureNote;
                ctx.FailureNote = null;
                _log($"[槲寄生] {indent}节点「{display}」执行未成功{(note is null ? "" : $"：{note}")}，继续后续节点");
                Report(step, NodeRunState.Failed, note);
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

    /// <summary>稳定性确认的读取间隔（防抖窗口）。</summary>
    private const int StabilityConfirmDelayMs = 1500;

    /// <summary>需要稳定性确认的条件类型：读外部进程/BGI 状态，可能撞上进程重启、任务切换间隙的中间态。
    /// timeRange/weekday 是本机确定性判断、manualConfirm 是人工交互，不在此列。</summary>
    private static bool NeedsStabilityConfirm(string kind) => kind is
        StartupStepKinds.BgiRunning or StartupStepKinds.GameRunning or StartupStepKinds.ProcessRunning
        or StartupStepKinds.BgiTaskRunning or StartupStepKinds.BgiTaskName;

    /// <summary>
    /// 带防抖的条件求值（容错）：外部状态类条件连续读取两次，一致才出结果；不一致再读第三次，
    /// 以第三次为准（bool 三次读取中第三次必与前两次之一相同，即多数派）。全部读取依据写入 desc 留痕。
    /// 代价：外部状态类条件固定多 1.5s（不稳定时 3s）延迟，换取不被中间态误导分支走向。
    /// </summary>
    private async Task<(bool? passed, string desc)> EvaluateConditionStableAsync(StartupStep step, CancellationToken ct)
    {
        if (!NeedsStabilityConfirm(step.Kind))
            return await ReadConditionAsync(step, ct);

        var first = await ReadConditionAsync(step, ct);
        await Task.Delay(StabilityConfirmDelayMs, ct);
        var second = await ReadConditionAsync(step, ct);
        if (first.passed is null) return first;
        if (second.passed is null) return second;
        if (first.passed == second.passed)
            return (second.passed, $"{second.desc}（二次确认一致）");

        await Task.Delay(StabilityConfirmDelayMs, ct);
        var third = await ReadConditionAsync(step, ct);
        return (third.passed, $"状态不稳定（{first.passed}→{second.passed}→{third.passed}），以第三次读取为准：{third.desc}");
    }


    public async Task<(bool? passed, string desc)> ReadConditionAsync(StartupStep step, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            ControlStatus? status = null;
            if (step.Kind is StartupStepKinds.BgiTaskRunning or StartupStepKinds.BgiTaskName)
            {
                status = await GetStatusAsync(step, ct);
                if (status is null && step.StatusSource != StartupStatusSource.CurrentSession)
                    return (null, "目标任务状态查询失败或目标不唯一");
            }
            var result = EvaluateCondition(step, status);
            return (result.passed, result.desc);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log($"[槲寄生] 状态读取失败：{ex.Message}");
            return (null, ex.Message);
        }
    }

    private async Task<ControlStatus?> GetStatusAsync(StartupStep step, CancellationToken ct)
    {
        if (step.StatusSource == StartupStatusSource.CurrentSession)
            return _statusProvider();
        return await _targetStatusProvider(step, ct).ConfigureAwait(false);
    }

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
                var targets = BgiProcessMonitor.SelectBgiProcesses(step.StatusSource, step.StatusTargetOrder, step.StatusTargetUser);
                var running = targets.Length > 0;
                foreach (var target in targets) target.Dispose();
                return (running == step.ExpectRunning, $"BGI {(running ? "正在运行" : "未运行")}，期望 {(step.ExpectRunning ? "运行" : "未运行")}");
            }
            case StartupStepKinds.GameRunning:
            {
                var session = BgiProcessMonitor.ResolveStatusSession(step.StatusSource, step.StatusTargetOrder, step.StatusTargetUser);
                var running = IsGameRunning(session, step.StatusSource != StartupStatusSource.CurrentSession);
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
                // ExpectRunning 复用为匹配方向：true=包含才通过（默认）；false=不包含才通过。
                // 无任务在跑时没有任务名可匹配：「包含」不成立、「不包含」成立
                // （电子狗场景：被盯任务已停止/被切走时「不匹配」应触发，而不是永不触发）
                var expectMatch = step.ExpectRunning;
                if (status == null || !status.TaskRunning)
                {
                    var idlePassed = !expectMatch;
                    return (idlePassed, $"BGI 未在跑任务{(status == null ? "（无状态快照）" : "")}，按「{(expectMatch ? "匹配" : "不匹配")}」判定{(idlePassed ? "成立" : "不成立")}");
                }
                var hit = (status.CurrentTaskName?.Contains(step.TaskName, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (status.CurrentTaskGroupName?.Contains(step.TaskName, StringComparison.OrdinalIgnoreCase) ?? false);
                var current = status.CurrentTaskGroupName is { Length: > 0 } g
                    ? status.CurrentTaskName is { Length: > 0 } n ? $"{g} · {n}" : g
                    : status.CurrentTaskName ?? "（未上报任务名）";
                var passed = expectMatch ? hit : !hit;
                return (passed, $"当前任务「{current}」{(hit ? "包含" : "不包含")}「{step.TaskName}」，按「{(expectMatch ? "匹配" : "不匹配")}」判定{(passed ? "成立" : "不成立")}");

            }
            default:
                return (false, $"未知条件类型 {step.Kind}（按不满足处理）");
        }
    }


    private static string DescribeStatusSource(StartupStep step) => step.StatusSource switch
    {
        StartupStatusSource.StartupOrder => $"启动顺序第 {Math.Max(1, step.StatusTargetOrder)} 个目标",
        StartupStatusSource.UserName => $"用户「{step.StatusTargetUser}」目标",
        _ => "本会话"
    };

    // ================= 动作执行 =================

    private async Task<bool> ExecuteActionAsync(StartupStep step, string display, string indent, CancellationToken ct, RunContext ctx)
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
                    // 带任务参数（startOneDragon/--startGroups/--TaskProgress）的启动会计入提交事实（R4.9 §6.1 混用检测主证据）
                    var taskArgs = HasBgiTaskArgs(step.Arguments);
                    var r = taskArgs
                        ? await ExecuteBgiTaskCommitAsync(ctx, display, "start_bgi", p)
                        : await _bgiExecutor("start_bgi", p);
                    _log($"[槲寄生] {indent}启动 BGI{(taskArgs ? "（带任务参数）" : "")}：{r.Message}");
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
                    // R4.9 §5/§7：真实移交。校验顺序与宿主一致（三轮 重要5：显式未知 mode 先于空目标检查，
                    // 不允许空目标短路掩盖未知语义）；链继续路径（ConfigMissing）由 RunChainAsync 经 FailureNote 单处回报（三轮 重要3）
                    var handoff = await ExecuteHandoffAsync(step, display, indent, ct, ctx);
                    // Accepted/AlreadyAccepted 或 Rejected+TerminateChain：终态已在 ExecuteHandoffAsync 单处回报，
                    // 抛控制流逐层终止本启动链（含分支回溯）——后续节点（含 StopBgi）一律不执行
                    if (handoff.Outcome != HandoffOutcome.Rejected || handoff.TerminateChain)
                        throw new HandoffCompletedException(HandoffChainEndMessage(handoff));
                    ctx.FailureNote = $"{handoff.ReasonCode}：{handoff.Reason}（执行 {ctx.ShortId}）"; // 四轮 建议1：链继续路径回报同样附执行短码
                    return false;
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
                case StartupStepKinds.Watchdog:
                {
                    if (!StartupStepKinds.IsWatchableKind(step.WatchKind))
                    {
                        _log($"[槲寄生] {indent}电子狗「{display}」的被盯条件类型无效（{step.WatchKind}），跳过");
                        return false;
                    }
                    if (step.FireSteps.Count == 0)
                    {
                        _log($"[槲寄生] {indent}电子狗「{display}」的「触发执行」链为空，不挂载");
                        return false;
                    }
                    _armWatchdog(step);
                    return true;
                }
                case StartupStepKinds.LogTrigger:
                {
                    if (string.IsNullOrWhiteSpace(step.LogKeyword))
                    {
                        _log($"[槲寄生] {indent}日志触发器「{display}」未填写日志关键字，跳过");
                        return false;
                    }
                    if (step.FireSteps.Count == 0)
                    {
                        _log($"[槲寄生] {indent}日志触发器「{display}」的「触发执行」链为空，不挂载");
                        return false;
                    }
                    if (_armLogTrigger == null)
                    {
                        _log($"[槲寄生] {indent}日志触发器「{display}」的日志源不可用（宿主未接入日志流），跳过");
                        return false;
                    }
                    _armLogTrigger(step);
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
                    var r = await ExecuteBgiTaskCommitAsync(ctx, display, "start_group", new Dictionary<string, object> { ["groupName"] = step.TaskName });
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
                    var r = await ExecuteBgiTaskCommitAsync(ctx, display, "start_oneclick", new Dictionary<string, object> { ["configName"] = step.TaskName });
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
        catch (HandoffCompletedException)
        {
            throw; // 移交终态控制流，逐层展开（R4.9 §7：绝不能被通用 catch 吞掉）
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

    /// <summary>「进入任务中心执行」移交（R4.9 §6/§7）：语义校验 → 混用守卫（主证据）→ 构建双重身份请求 → 委托回执 → 节点终态单处回报。</summary>
    private async Task<StartupHandoffResult> ExecuteHandoffAsync(StartupStep step, string display, string indent, CancellationToken ct, RunContext ctx)
    {
        // 校验顺序与宿主 RegisterHandoffAsync 一致（三轮 重要5）：显式未知 mode 最先（终止链），空目标其次（旧配置兼容链继续）
        if (!StartupHandoffModes.IsKnown(step.TaskCenterHandoffMode))
        {
            var bad = StartupHandoffResult.Rejected(HandoffReasonCodes.UnsupportedMode,
                $"未支持的移交语义「{step.TaskCenterHandoffMode}」（支持 start/resume/armTrigger，不静默回落 start）");
            _log($"[槲寄生] {indent}移交被拒绝[{bad.ReasonCode}]：{bad.Reason}（执行 {ctx.ShortId}）");
            Report(step, NodeRunState.Failed, $"{bad.ReasonCode}：{bad.Reason}（执行 {ctx.ShortId}）");
            return bad;
        }
        if (string.IsNullOrWhiteSpace(step.TaskCenterFlowId))
        {
            // 空目标=旧配置兼容：不在这里 Report——返回 TerminateChain=false 的拒绝，由 RunChainAsync 经 FailureNote 单处回报（三轮 重要3）
            _log($"[槲寄生] {indent}「{display}」未配置目标任务中心流程，跳过移交（旧配置兼容，继续后续节点，执行 {ctx.ShortId}）");
            return StartupHandoffResult.Rejected(HandoffReasonCodes.ConfigMissing, "未配置目标任务中心流程");
        }
        // 混用检测（§6.1 主证据）：本执行已直接提交 BGI 任务 + start/resume → 拒绝；armTrigger 挂载等待不占槽位，不受限
        if (step.TaskCenterHandoffMode != StartupHandoffModes.ArmTrigger && ctx.BgiTaskCommitFacts.Count > 0)
        {
            var mixed = StartupHandoffResult.Rejected(HandoffReasonCodes.MixedUsage,
                $"本启动链已直接提交 BGI 任务（{string.Join("、", ctx.BgiTaskCommitFacts)}），与任务中心移交混用，请二选一");
            _log($"[槲寄生] {indent}移交被拒绝[{mixed.ReasonCode}]：{mixed.Reason}（执行 {ctx.ShortId}）");
            Report(step, NodeRunState.Failed, $"{mixed.ReasonCode}：{mixed.Reason}（执行 {ctx.ShortId}）");
            return mixed;
        }

        // 双重身份（§2）：ExecutionId=本次执行；IntentKey=计划出现（触发器=种类:实例:日程日；手动=每次新意图）
        var request = new StartupHandoffRequest
        {
            ExecutionId = ctx.ExecutionId,
            StepId = step.Id,
            TriggerKind = ctx.Trigger?.Kind,
            TriggerInstanceId = ctx.Trigger?.InstanceId,
            FireDate = ctx.Trigger?.OccurrenceKey,
            IntentKey = ctx.Trigger is { } trig
                ? $"{trig.Kind}:{trig.InstanceId}:{trig.OccurrenceKey}"
                : $"manual:{ctx.ExecutionId}",
            WorkflowId = step.TaskCenterFlowId,
            Mode = step.TaskCenterHandoffMode,
            IntentNote = display,
        };
        StartupHandoffResult result;
        try
        {
            result = await _enterTaskCenter(request, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消语义原样上传
        }
        catch (Exception ex)
        {
            // 委托异常=受理事实未决（可能已受理）——按 Rejected+终止链兜底，绝不假装成功
            result = StartupHandoffResult.Rejected(HandoffReasonCodes.HandoffError,
                $"移交通道异常：{ex.Message}（受理事实未决）");
        }

        if (result.Outcome == HandoffOutcome.Rejected)
        {
            _log($"[槲寄生] {indent}移交被拒绝[{result.ReasonCode}]：{result.Reason}（执行 {ctx.ShortId}）");
            // 三轮 重要3：链继续路径（TerminateChain=false，仅 ConfigMissing）不在此 Report——由 RunChainAsync 经 FailureNote 单处回报
            if (result.TerminateChain)
                Report(step, NodeRunState.Failed, $"{result.ReasonCode}：{result.Reason}（执行 {ctx.ShortId}）");
            return result;
        }
        // S4：保留宿主回执 Reason（含「受理≠执行成功/驱动激活受阻」等说明）+ 执行短码（§7/S10 并发可区分）
        var okNote = (result.Outcome == HandoffOutcome.Accepted
            ? $"已移交任务中心（运行 {result.RunId}），任务中心接管"
            : $"同一计划出现此前已受理（运行 {result.RunId}）")
            + (result.Reason is null ? "" : $"——{result.Reason}");
        _log($"[槲寄生] {indent}「{display}」{okNote}（执行 {ctx.ShortId}）");
        Report(step, NodeRunState.Success, $"{okNote}（执行 {ctx.ShortId}）");
        return result;
    }

    /// <summary>移交终态后终止启动链的日志文案（R4.9 §7）。</summary>
    private static string HandoffChainEndMessage(StartupHandoffResult r) => r.Outcome switch
    {
        HandoffOutcome.Accepted => "已移交任务中心，本次启动链到此终止（后续节点不再执行）",
        HandoffOutcome.AlreadyAccepted => "同一计划出现已受理，本次启动链到此终止（后续节点不再执行）",
        _ => $"移交被拒绝（{r.ReasonCode}），按策略终止本次启动链（后续节点不再执行）",
    };

    /// <summary>执行会直接提交 BGI 任务的命令并登记提交事实（R4.9 §6.1 混用检测主证据：成功或结果不确定均计，明确失败不计）。</summary>
    private async Task<CommandResult> ExecuteBgiTaskCommitAsync(RunContext ctx, string display, string cmd, Dictionary<string, object>? p)
    {
        try
        {
            var r = await _bgiExecutor(cmd, p);
            if (r.Status == "success")
                ctx.BgiTaskCommitFacts.Add($"节点「{display}」（{cmd} 已受理）");
            else if (string.IsNullOrWhiteSpace(r.ErrorCode)) // 五轮收尾：空白错误码=无有效业务信封，同样按不确定计入
                // 三轮 重要4：无业务信封的失败=结果不确定（BGI 侧合同：连接建立后的命令传输失败 at-least-once，超时≠未执行，
                // 见 CommandExecutor 分层超时注释）——保守计入提交事实
                ctx.BgiTaskCommitFacts.Add($"节点「{display}」（{cmd} 结果不确定：{r.Message}）");
            // failed + ErrorCode = BGI 权威业务拒绝（request_expired/task_busy/batch_busy 等，确定未提交）——不计
            return r;
        }
        catch (Exception)
        {
            // IPC/执行异常=结果不确定（命令可能已送达）——保守计入提交事实，再按既有容错路径返回失败
            ctx.BgiTaskCommitFacts.Add($"节点「{display}」（{cmd} 结果不确定）");
            throw;
        }
    }

    /// <summary>BGI 启动参数是否携带任务参数（R4.9 §6.1 已查证 CommandLineOptions：判定口径与 BGI 解析对齐——
    /// 只看第一个参数 token：startOneDragon 含子串即中；--startGroups/--TaskProgress 精确匹配；裸 start 仅启截图器不算；
    /// 三轮 S1：不做全串子串匹配，参数值里出现同名文本不误判）。</summary>
    internal static bool HasBgiTaskArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        var first = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return first.Contains("startOneDragon", StringComparison.OrdinalIgnoreCase)
               || first.Equals("--startGroups", StringComparison.OrdinalIgnoreCase)
               || first.Equals("--TaskProgress", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsGameRunning(int? targetSession = null, bool strict = false)
    {
        try
        {
            var session = targetSession ?? Process.GetCurrentProcess().SessionId;
            foreach (var name in GameProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                try { if (processes.Any(p => p.SessionId == session)) return true; }
                finally { foreach (var process in processes) process.Dispose(); }
            }
        }
        catch
        {
            if (strict) throw;
            // 本会话保留既有容错；跨会话异常由 ReadConditionAsync 转换为未知。
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
