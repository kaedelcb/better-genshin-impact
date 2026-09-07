using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

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

    // ===== 条件判断参数 =====

    /// <summary>期望进程处于运行状态（bgiRunning/gameRunning/processRunning 用）：true=正在运行才通过，false=未运行才通过。</summary>
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

    /// <summary>[旧版遗留] BGI 任务名（startGroup/startOneClick 用）。
    /// 2026-09-08 起这两个类型已从节点目录移除（启动中心不再直接配 BGI 任务，由「进入任务中心执行」节点接管），
    /// 字段与 Runner 分支保留仅为兼容旧配置。</summary>
    [JsonPropertyName("taskName")]
    public string TaskName { get; set; } = "";
}

/// <summary>
/// 启动流程节点类型目录。Key 即 StartupStep.Kind；新增类型时在此加目录项，
/// 并在 StartupFlowRunner 加执行分支、MistletoePage 编辑器加字段区。
/// 启动中心定位：进入任务中心前的环境准备（起 BGI/游戏/三方程序/CMD/等待），
/// 具体 BGI 任务序列不属于这里——用「进入任务中心执行」节点交接。
/// </summary>
public static class StartupStepKinds
{
    // ---- 条件判断 ----
    public const string TimeRange = "timeRange";
    public const string Weekday = "weekday";
    public const string BgiRunning = "bgiRunning";
    public const string GameRunning = "gameRunning";
    public const string ProcessRunning = "processRunning";

    // ---- 动作 ----
    public const string StartBgi = "startBgi";
    public const string StopBgi = "stopBgi";
    public const string StartGame = "startGame";
    public const string StartProgram = "startProgram";
    public const string RunCmd = "runCmd";
    public const string Wait = "wait";
    /// <summary>进入任务中心执行：流程走到此节点，把控制权交给任务中心的任务序列（任务中心落地前为占位，记日志）。</summary>
    public const string EnterTaskCenter = "enterTaskCenter";
    /// <summary>结束流程：立即终止整条启动流程（含所有外层链）。</summary>
    public const string EndFlow = "endFlow";

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
        // 动作
        new(StartBgi, "action", "启动 BGI", "▶", "启动本机 BGI（复用成员卡片「启动BGI」同款逻辑）"),
        new(StopBgi, "action", "关闭 BGI", "▶", "强制结束当前会话的 BGI 进程"),
        new(StartGame, "action", "启动游戏", "▶", "按路径启动原神（Yuanshen.exe / GenshinImpact.exe）"),
        new(StartProgram, "action", "启动第三方程序", "▶", "按路径启动任意程序，可带参数"),
        new(RunCmd, "action", "执行 CMD 命令", "▶", "以 cmd /c 隐藏窗口执行一条命令，不等待结果"),
        new(Wait, "action", "等待", "▶", "流程内延时 N 秒（等程序就绪时常用）"),
        new(EnterTaskCenter, "action", "进入任务中心执行", "➤", "环境准备完毕，交接给任务中心执行任务序列（任务中心规划中，当前为占位节点）"),
        new(EndFlow, "action", "结束流程", "■", "立即终止整条启动流程（常用于「否」分支收尾）"),
    ];

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
            case RunCmd:
                step.Arguments = "echo hello";
                break;
            case Wait:
                step.WaitSeconds = 10;
                break;
        }
        return step;
    }
}
