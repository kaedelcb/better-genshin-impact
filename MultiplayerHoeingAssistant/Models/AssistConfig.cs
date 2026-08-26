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

    /// <summary>实例标识（UUID，助手进程启动时自动生成）。用于服务端区分同 UID 的多个连接实例。</summary>
    [JsonPropertyName("clientInstanceId")]
    public string ClientInstanceId { get; set; } = "";

    /// <summary>是否使用新的 /control-hub 架构（服务器端 SSOT）。默认 true。</summary>
    [JsonPropertyName("useNewControlRoomHub")]
    public bool UseNewControlRoomHub { get; set; } = true;
}