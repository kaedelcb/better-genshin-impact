using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 启动流程节点的运行态（执行路径可视化用，纯运行态不持久化）。
/// 每次执行前重置为 None；执行中由 StartupFlowRunner 逐节点回报。
/// </summary>
public enum NodeRunState
{
    /// <summary>本次执行尚未走到。</summary>
    None,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>动作执行成功。</summary>
    Success,
    /// <summary>动作执行失败（流程继续后续节点）。</summary>
    Failed,
    /// <summary>节点已禁用，被跳过。</summary>
    Skipped,
    /// <summary>条件判断成立，走「是」分支。</summary>
    CondTrue,
    /// <summary>条件判断不成立，走「否」分支。</summary>
    CondFalse,
}

/// <summary>
/// 槲寄生 · 启动中心——启动流程配置（助手启动后 → 进入任务中心前的环境准备动作链）。
/// 持久化到 %APPDATA%/NexusBGI/startup-flow.json（按 Windows 用户隔离，不走 SignalR 同步）。
/// 序列化风格与 AssistConfig 一致（System.Text.Json + camelCase）。
/// </summary>
public class StartupFlowConfig
{
    /// <summary>助手启动完成后是否自动执行一遍启动流程。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>自动执行前的延迟秒数（等 SignalR/IPC 稳定，默认 5 秒）。</summary>
    [JsonPropertyName("delaySeconds")]
    public int DelaySeconds { get; set; } = 5;

    /// <summary>主流程节点链（顺序即执行顺序；条件节点内嵌是/否子链）。</summary>
    [JsonPropertyName("steps")]
    public List<StartupStep> Steps { get; set; } = [];

    /// <summary>执行时自动展开实际走到的分支（默认开；未走到的分支保持折叠并灰显）。</summary>
    [JsonPropertyName("autoExpandBranch")]
    public bool AutoExpandBranch { get; set; } = true;
}

/// <summary>
/// 槲寄生 · 启动中心——已保存的方案快照（命名保存整份启动流程配置，改坏了可一键恢复）。
/// 列表持久化到 %APPDATA%/NexusBGI/startup-flow-schemes.json（与主配置同目录，按 Windows 用户隔离）。
/// </summary>
public class StartupFlowScheme
{
    /// <summary>方案名（用户命名；同名保存视为覆盖更新）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>保存时间（本机时间，列表展示用）。</summary>
    [JsonPropertyName("savedAt")]
    public DateTime SavedAt { get; set; } = DateTime.Now;

    /// <summary>完整配置快照（深拷贝，与当前配置互不影响）。</summary>
    [JsonPropertyName("config")]
    public StartupFlowConfig Config { get; set; } = new();
}

/// <summary>
/// 启动流程节点。NodeType 区分「条件判断」与「动作」两大类；
/// Kind 是具体类型键（见 StartupStepKinds 目录）；参数采用扁平字段（按 Kind 取用对应字段），
/// 新增节点类型不需要改序列化格式。
///
/// 分支模型（2026-09-08 二轮）：条件节点带 TrueSteps/FalseSteps 两条子链，
/// 判断成立走「是」链、不成立走「否」链，子链执行完后回到父链继续（分支汇合）；
/// 子链可再嵌套条件，形成树形流程图。旧版 OnFail（终止/跳过/忽略）字段已被分支取代，废弃不序列化。
/// </summary>
public class StartupStep
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"condition"=条件判断 / "action"=动作。</summary>
    [JsonPropertyName("nodeType")]
    public string NodeType { get; set; } = "action";

    /// <summary>节点类型键，见 StartupStepKinds。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>用户可改的显示名（空时 UI 显示类型默认名）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>是否启用（禁用的节点执行时跳过，UI 半透明显示）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // ===== 分支（仅条件节点使用） =====

    /// <summary>条件成立（是）时执行的子链。</summary>
    [JsonPropertyName("trueSteps")]
    public List<StartupStep> TrueSteps { get; set; } = [];

    /// <summary>条件不成立（否）时执行的子链。</summary>
    [JsonPropertyName("falseSteps")]
    public List<StartupStep> FalseSteps { get; set; } = [];

    /// <summary>到点执行的子链（timerTrigger 用；流程跑到该节点时挂载定时器，到点执行此链）。
    /// watchdog 复用此字段作为「触发执行」子链（条件由不成立变成立时执行）。</summary>
    [JsonPropertyName("fireSteps")]
    public List<StartupStep> FireSteps { get; set; } = [];

    // ===== 条件判断参数 =====

    /// <summary>期望处于运行状态（bgiRunning/gameRunning/processRunning/bgiTaskRunning 用）：true=正在运行（在跑任务）才通过，false=未运行（空闲）才通过。</summary>
    [JsonPropertyName("expectRunning")]
    public bool ExpectRunning { get; set; } = true;

    /// <summary>指定进程名（processRunning 用，可带或不带 .exe）。</summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    /// <summary>时间区间起点 HH:mm（timeRange 用）。</summary>
    [JsonPropertyName("timeStart")]
    public string TimeStart { get; set; } = "00:00";

    /// <summary>时间区间终点 HH:mm（timeRange 用；起点&gt;终点表示跨零点）。</summary>
    [JsonPropertyName("timeEnd")]
    public string TimeEnd { get; set; } = "23:59";

    /// <summary>星期集合（weekday 用；1=周一 … 7=周日）。</summary>
    [JsonPropertyName("weekdays")]
    public List<int> Weekdays { get; set; } = [];

    /// <summary>弹窗提示内容（manualConfirm 用；空时显示节点名）。</summary>
    [JsonPropertyName("confirmMessage")]
    public string ConfirmMessage { get; set; } = "";

    /// <summary>超时秒数（manualConfirm 用；0=不限时等人点）。</summary>
    [JsonPropertyName("confirmTimeoutSeconds")]
    public int ConfirmTimeoutSeconds { get; set; } = 60;

    /// <summary>超时后自动走的分支（manualConfirm 用）：true=走「是」，false=走「否」。</summary>
    [JsonPropertyName("confirmTimeoutGoTrue")]
    public bool ConfirmTimeoutGoTrue { get; set; } = false;

    // ===== 动作参数 =====

    /// <summary>程序路径（startGame/startProgram 用）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>启动参数 / CMD 命令文本（startGame/startProgram/runCmd 用）。</summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";

    /// <summary>等待秒数（wait 用）。</summary>
    [JsonPropertyName("waitSeconds")]
    public int WaitSeconds { get; set; } = 10;

    /// <summary>触发时间 HH:mm（timerTrigger 用；今天的该时刻已过则顺延到明天）。</summary>
    [JsonPropertyName("triggerTime")]
    public string TriggerTime { get; set; } = "08:00";

    /// <summary>是否每天重复触发（timerTrigger 用；false=只触发一次）。</summary>
    [JsonPropertyName("repeatDaily")]
    public bool RepeatDaily { get; set; } = false;

    // ===== 电子狗参数（watchdog 用；被盯条件的参数复用上方的条件参数字段） =====

    /// <summary>被盯的条件类型（watchdog 用），取值限 <see cref="StartupStepKinds.WatchableKinds"/>。</summary>
    [JsonPropertyName("watchKind")]
    public string WatchKind { get; set; } = StartupStepKinds.BgiTaskRunning;

    /// <summary>检测间隔秒数（watchdog 用；下限 1 秒）。防抖按 WatchConfirmSeconds×WatchConfirmTimes 配置复核，
    /// 等效确认延迟 ≈ 间隔（发现事件）+ 复核秒数×复核次数（默认 1s×1 ≈ 1 秒）。</summary>
    [JsonPropertyName("watchIntervalSeconds")]
    public int WatchIntervalSeconds { get; set; } = 30;

    /// <summary>是否重复触发（watchdog 用）：true=条件每次由不成立变成立都触发；false=触发一次后自动撤下。</summary>
    [JsonPropertyName("watchRepeat")]
    public bool WatchRepeat { get; set; } = true;

    /// <summary>防抖复核的间隔秒数（watchdog 用）：发现新状态后每隔这么久复核一轮。默认 1 秒。</summary>
    [JsonPropertyName("watchConfirmSeconds")]
    public int WatchConfirmSeconds { get; set; } = 1;

    /// <summary>防抖复核的次数（watchdog 用）：全部复核轮一致为新状态才认定翻转。默认 1 次。</summary>
    [JsonPropertyName("watchConfirmTimes")]
    public int WatchConfirmTimes { get; set; } = 1;

    /// <summary>启动前先关闭 BGI（startBgi 用）：已在运行的 BGI 不会因新参数自动重启（不抢占策略），
    /// 勾选后先强杀本会话 BGI 再带参数启动，让参数生效。</summary>
    [JsonPropertyName("killBeforeStart")]
    public bool KillBeforeStart { get; set; } = false;

    /// <summary>状态来源：本会话（默认）、按 BGI 启动顺序、按 Windows 用户名/SID。</summary>
    [JsonPropertyName("statusSource")]
    public string StatusSource { get; set; } = StartupStatusSource.CurrentSession;

    /// <summary>按启动顺序选择的逻辑序号，1 开始。</summary>
    [JsonPropertyName("statusTargetOrder")]
    public int StatusTargetOrder { get; set; } = 1;

    /// <summary>按用户名选择时的 Windows 用户名。</summary>
    [JsonPropertyName("statusTargetUser")]
    public string StatusTargetUser { get; set; } = "";

    /// <summary>[旧版遗留] BGI 任务名（startGroup/startOneClick 用）。
    /// 2026-09-08 起这两个类型已从节点目录移除（启动中心不再直接配 BGI 任务，由「进入任务中心执行」节点接管），
    /// 字段与 Runner 分支保留仅为兼容旧配置。bgiTaskName 条件节点复用此字段存匹配文本。</summary>
    [JsonPropertyName("taskName")]
    public string TaskName { get; set; } = "";
}

/// <summary>
/// 启动流程节点类型目录。Key 即 StartupStep.Kind；新增类型时在此加目录项，
/// 并在 StartupFlowRunner 加执行分支、MistletoePage 编辑器加字段区。
/// 启动中心定位：进入任务中心前的环境准备（起 BGI/游戏/三方程序/CMD/等待），
/// 具体 BGI 任务序列不属于这里——用「进入任务中心执行」节点交接。
/// </summary>
public static class StartupStatusSource
{
    public const string CurrentSession = "currentSession";
    public const string StartupOrder = "startupOrder";
    public const string UserName = "userName";

    public static readonly string[] All = [CurrentSession, StartupOrder, UserName];
}

public static class StartupStepKinds
{
    // ---- 条件判断 ----
    public const string TimeRange = "timeRange";
    public const string Weekday = "weekday";
    public const string BgiRunning = "bgiRunning";
    public const string GameRunning = "gameRunning";
    public const string ProcessRunning = "processRunning";
    /// <summary>BGI 任务状态：按 BGI 当前是否有任务在执行分两支（读 10s 状态快照，任务级判断，区别于进程级的 bgiRunning）。</summary>
    public const string BgiTaskRunning = "bgiTaskRunning";
    /// <summary>BGI 当前任务名：当前任务名/配置组名包含指定文本走「是」分支（读同一份状态快照）。</summary>
    public const string BgiTaskName = "bgiTaskName";
    /// <summary>人工确认：弹窗由人点「是/否」决定走哪条分支；可设超时，超时自动走预设分支。</summary>
    public const string ManualConfirm = "manualConfirm";

    // ---- 动作 ----
    public const string StartBgi = "startBgi";
    public const string StopBgi = "stopBgi";
    public const string StartGame = "startGame";
    public const string StartProgram = "startProgram";
    public const string KillProgram = "killProgram";
    public const string RunCmd = "runCmd";
    public const string Wait = "wait";
    /// <summary>进入任务中心执行：流程走到此节点，把控制权交给任务中心的任务序列（任务中心落地前为占位，记日志）。</summary>
    public const string EnterTaskCenter = "enterTaskCenter";
    /// <summary>结束流程：立即终止整条启动流程（含所有外层链）。</summary>
    public const string EndFlow = "endFlow";
    /// <summary>定时触发器：流程跑到此节点时挂载定时器，到指定时间执行 FireSteps 子链；定时中页面显示状态、可取消。</summary>
    public const string TimerTrigger = "timerTrigger";
    /// <summary>电子狗：流程跑到此节点时挂载循环检测，每 N 秒求值一次被盯条件，
    /// 条件由不成立变成立（边沿）时执行 FireSteps 子链；盯梢中页面显示状态、可查看流程、可取消。</summary>
    public const string Watchdog = "watchdog";

    // ---- 旧版遗留（不在目录中，Runner 保留执行分支兼容旧配置） ----
    public const string StartGroup = "startGroup";
    public const string StartOneClick = "startOneClick";

    public sealed record KindInfo(string Key, string NodeType, string DisplayName, string Icon, string Description);

    /// <summary>全部可选节点类型（目录顺序即「添加节点」弹层的展示顺序）。</summary>
    public static readonly KindInfo[] All =
    [
        // 条件判断
        new(TimeRange, "condition", "时间区间", "◇", "当前时间落在起止时间之间走「是」分支，否则走「否」分支（支持跨零点）"),
        new(Weekday, "condition", "星期判断", "◇", "今天属于勾选的星期走「是」分支，否则走「否」分支"),
        new(BgiRunning, "condition", "BGI 进程状态", "◇", "按当前会话的 BGI 是否正在运行分两支"),
        new(GameRunning, "condition", "游戏进程状态", "◇", "按当前会话的原神是否正在运行分两支"),
        new(ProcessRunning, "condition", "指定进程状态", "◇", "按任意进程名是否存在分两支（可用来等第三方工具）"),
        new(BgiTaskRunning, "condition", "BGI 任务状态", "◇", "按 BGI 当前是否有任务在执行分两支（读任务状态快照，区别于「BGI 进程状态」只查进程）"),
        new(BgiTaskName, "condition", "BGI 当前任务名", "◇", "BGI 当前任务名/配置组名包含指定文本走「是」分支（读任务状态快照）"),
        new(ManualConfirm, "condition", "人工确认", "◇", "弹窗由人点「是/否」决定走哪条分支，弹窗列出两条分支的后续动作；可设超时自动走向"),
        // 动作
        new(StartBgi, "action", "启动 BGI", "▶", "启动本机 BGI（可带命令行参数，复用成员卡片「启动BGI」同款逻辑）"),
        new(StopBgi, "action", "关闭 BGI", "▶", "强制结束当前会话的 BGI 进程"),
        new(StartGame, "action", "启动游戏", "▶", "按路径启动原神（Yuanshen.exe / GenshinImpact.exe）"),
        new(StartProgram, "action", "启动第三方程序", "▶", "按路径启动任意程序，可带参数"),
        new(KillProgram, "action", "关闭程序", "▶", "按进程名强制结束当前会话的指定程序（如第三方工具）"),
        new(RunCmd, "action", "执行 CMD 命令", "▶", "以 cmd /c 隐藏窗口执行一条命令，不等待结果"),
        new(Wait, "action", "等待", "▶", "流程内延时 N 秒（等程序就绪时常用）"),
        new(EnterTaskCenter, "action", "进入任务中心执行", "➤", "环境准备完毕，交接给任务中心执行任务序列（任务中心规划中，当前为占位节点）"),
        new(TimerTrigger, "action", "定时触发器", "⏰", "挂载定时器，到指定时间执行「到点执行」子链；定时中显示状态、可随时取消"),
        new(Watchdog, "action", "电子狗", "🐕", "循环盯梢：每 N 秒检查条件，由不成立变成立时执行「触发执行」子链；防抖复核可调，挂载时已成立则确认后触发一次"),
        new(EndFlow, "action", "结束流程", "■", "立即终止整条启动流程（常用于「否」分支收尾）"),
    ];

    /// <summary>电子狗可盯的条件类型（数组顺序即编辑器下拉框顺序）。
    /// timeRange/weekday 已由定时触发器与星期条件覆盖，manualConfirm 是交互弹窗、不适合无人值守循环。</summary>
    public static readonly string[] WatchableKinds = [BgiRunning, GameRunning, ProcessRunning, BgiTaskRunning, BgiTaskName];

    /// <summary>该类型键是否可作为电子狗的被盯条件。</summary>
    public static bool IsWatchableKind(string kind) => WatchableKinds.Contains(kind);

    public static KindInfo? Find(string kind) => All.FirstOrDefault(k => k.Key == kind);

    /// <summary>按类型键创建一个新节点（带合理默认值）。</summary>
    public static StartupStep Create(string kind)
    {
        var info = Find(kind);
        var step = new StartupStep
        {
            NodeType = info?.NodeType ?? "action",
            Kind = kind,
        };
        switch (kind)
        {
            case Weekday:
                step.Weekdays = [1, 2, 3, 4, 5, 6, 7];
                break;
            case ProcessRunning:
                step.ProcessName = "notepad";
                break;
            case ManualConfirm:
                step.ConfirmTimeoutSeconds = 60;
                step.ConfirmTimeoutGoTrue = false;
                break;
            case KillProgram:
                step.ProcessName = "";
                break;
            case RunCmd:
                step.Arguments = "echo hello";
                break;
            case Wait:
                step.WaitSeconds = 10;
                break;
            case TimerTrigger:
                step.TriggerTime = "08:00";
                break;
            case Watchdog:
                step.WatchKind = BgiTaskRunning;
                step.ExpectRunning = true;
                step.WatchIntervalSeconds = 30;
                step.WatchRepeat = true;
                break;
        }
        return step;
    }
}
