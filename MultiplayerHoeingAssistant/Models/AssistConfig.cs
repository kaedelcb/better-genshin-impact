using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

public class AssistConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "http://localhost:5000";

    [JsonPropertyName("controlRoomPassword")]
    public string ControlRoomPassword { get; set; } = string.Empty;

    [JsonPropertyName("teamUids")]
    public List<string> TeamUids { get; set; } = [];

    [JsonPropertyName("bgiPath")]
    public string BgiPath { get; set; } = string.Empty;

    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = string.Empty;

    [JsonPropertyName("playerUid")]
    public string PlayerUid { get; set; } = string.Empty;

    /// <summary>用户是否已阅读并同意免责声明（首次启动弹出，同意后置 true，不再弹）</summary>
    [JsonPropertyName("disclaimerAccepted")]
    public bool DisclaimerAccepted { get; set; }

    /// <summary>一键快捷命令绑定：命令名 → 配置组名/一条龙名</summary>
    [JsonPropertyName("quickCommands")]
    public Dictionary<string, string> QuickCommands { get; set; } = new()
    {
        ["一键传奇"] = "",
        ["一键次数盾"] = "",
        ["一键精英"] = "",
        ["一键小怪"] = "",
        ["一键自定义"] = ""
    };

    // ========== 启动策略（multiplayer-hoeing-assistant-settings）==========

    /// <summary>① 随 BGI 启动：助手检测到 BGI 进程存在时自动启动</summary>
    [JsonPropertyName("autoLaunchWithBgi")]
    public bool AutoLaunchWithBgi { get; set; } = false;

    /// <summary>① 随 BGI 启动时的启动方式：true=静默缩小到托盘，false=弹窗启动</summary>
    [JsonPropertyName("autoLaunchWithBgiMinimized")]
    public bool AutoLaunchWithBgiMinimized { get; set; } = true;

    /// <summary>② 开机自启动：系统启动时助手自动启动</summary>
    [JsonPropertyName("autoLaunchOnBoot")]
    public bool AutoLaunchOnBoot { get; set; } = false;

    /// <summary>② 开机自启动时的启动方式：true=静默缩小到托盘，false=弹窗启动</summary>
    [JsonPropertyName("autoLaunchOnBootMinimized")]
    public bool AutoLaunchOnBootMinimized { get; set; } = true;

    /// <summary>③ 守护 BGI：助手运行时，BGI 异常退出则自动重启 BGI</summary>
    [JsonPropertyName("guardBgi")]
    public bool GuardBgi { get; set; } = false;

    // ========== 抢占式中断（multiplayer-hoeing-preempt-interrupt spec）==========

    /// <summary>定时上线时间（HH:mm），空字符串表示未设置</summary>
    [JsonPropertyName("scheduledOnlineTime")]
    public string ScheduledOnlineTime { get; set; } = "";

    /// <summary>联机锄地配置组名称列表（多个，如传奇/精英/小怪）</summary>
    [JsonPropertyName("onlineHoeingGroupNames")]
    public List<string> OnlineHoeingGroupNames { get; set; } = [];

    /// <summary>联机锄地配置组类型列表（与 OnlineHoeingGroupNames 一一对应，每项为 "group" 或 "onedragon"）。</summary>
    [JsonPropertyName("onlineHoeingGroupTypes")]
    public List<string> OnlineHoeingGroupTypes { get; set; } = [];

    /// <summary>当前使用的联机配置组索引（0 = 第一个）</summary>
    [JsonPropertyName("onlineHoeingGroupIndex")]
    public int OnlineHoeingGroupIndex { get; set; } = 0;

    /// <summary>预期开锄人数（默认 4）。服务端取所有已上线成员的最小值作为就绪阈值。</summary>
    [JsonPropertyName("expectedHoeingPlayers")]
    public int ExpectedHoeingPlayers { get; set; } = 4;

    /// <summary>遥控器模式：本机无 BGI 时启用，跳过 BGI 进程监控，所有操作通过远程命令发送给其他成员。</summary>
    [JsonPropertyName("observerMode")]
    public bool ObserverMode { get; set; } = false;

    /// <summary>绕过系统代理直连服务器。本机开加速器/代理时勾选：.NET 的系统代理配置是进程级缓存，
    /// 代理软件（如 okz/加速器）挂掉后运行中的进程不会感知，会一直往失效的代理口撞（"目标计算机积极拒绝"），
    /// 勾选后新建连接显式禁用代理，不受系统代理状态影响。</summary>
    [JsonPropertyName("bypassSystemProxy")]
    public bool BypassSystemProxy { get; set; } = false;

    /// <summary>实例标识（UUID，助手进程启动时自动生成）。用于服务端区分同 UID 的多个连接实例。</summary>
    [JsonPropertyName("clientInstanceId")]
    public string ClientInstanceId { get; set; } = "";

    // ========== 上线锄地「完成后动作」（持久化；绑定弹窗配置，全员就绪触发上线锄地固定打断当前任务）==========

    /// <summary>上线锄地完成后动作："resume"=恢复任务（默认）/ "stop"=执行动作-停止 / "runSpecified"=执行动作-执行指定任务。
    /// 作用于全员就绪上线锄地打断的原任务；批次末尾与 10s resume 定时器两处都按此配置走。</summary>
    [JsonPropertyName("onlineHoeingCompletionPolicy")]
    public string OnlineHoeingCompletionPolicy { get; set; } = "resume";

    /// <summary>上线锄地指定任务类型（"group"=配置组 / "onedragon"=一条龙），仅 "runSpecified" 使用。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedTaskType")]
    public string OnlineHoeingSpecifiedTaskType { get; set; } = "group";

    /// <summary>上线锄地指定任务名称，仅 "runSpecified" 使用；执行时校验存在性，不存在则日志报错退化为停止。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedTaskName")]
    public string OnlineHoeingSpecifiedTaskName { get; set; } = "";

    // 注意：成员卡片 6 个按键（配置组/一条龙/快捷键/停止/启动BGI/关闭游戏）的策略为固定行为：
    // 本机忙时一律「立即执行 + 执行完停止」（suspend 抢占 → 执行键动作 → 清上下文不恢复），无 UI、无配置项、不持久化。
}