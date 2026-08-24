namespace BgiCoordinatorServer.Models;

public class ControlRoomPlayer
{
    public string ConnectionId { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool Online { get; set; }
    public string BgiStatus { get; set; } = "unknown";  // running / stopped / crashed
    public List<string> ConfigGroups { get; set; } = [];
    public List<string> OneClickConfigs { get; set; } = [];
    /// <summary>配置组名 → 该配置组内任务名列表（供 WEB 端"从此处开始执行"选择起点）。</summary>
    public Dictionary<string, List<string>> ConfigGroupTasks { get; set; } = [];
    /// <summary>配置组名 → 任务列表（含 name/index/status，供状态显示与编辑）。</summary>
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];
    /// <summary>一条龙配置名 → 任务列表（含 name/index/enabled，供状态显示与编辑）。</summary>
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];
    /// <summary>快捷键列表（含 configName/functionName/hotkeyText，供 Web/PC 执行）。</summary>
    public List<object> Hotkeys { get; set; } = [];
    /// <summary>一条龙配置名 → 该配置内任务名列表（供 WEB 端"从此处开始执行"选择起点）。</summary>
    public Dictionary<string, List<string>> OneClickTasks { get; set; } = [];
    /// <summary>实例标识（UUID，同一台机器上同一个助手进程重启后不变）。用于区分同 UID 多个连接。</summary>
    public string ClientInstanceId { get; set; } = "";
    /// <summary>是否正在执行任务（任意任务）。</summary>
    public bool TaskRunning { get; set; }
    /// <summary>当前正在执行的任务名称。</summary>
    public string? CurrentTaskName { get; set; }
    /// <summary>当前正在执行的配置组/一条龙名称（groupName，独立任务时为 null）。</summary>
    public string? CurrentTaskGroupName { get; set; }
    /// <summary>当前联机锄地线路展示文本（如"第2条线路: 蒙德城"，非锄地为 null）。</summary>
    public string? CurrentRouteDisplay { get; set; }
    /// <summary>是否正在联机锄地（锄地房间中）。</summary>
    public bool AutoHoeingRunning { get; set; }
    /// <summary>当前锄地进度文本。</summary>
    public string? AutoHoeingProgress { get; set; }
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    /// <summary>是否已上线（标记了"已上线"但联机锄地尚未开始）。</summary>
    public bool OnlineReady { get; set; } = false;
    /// <summary>上线方式：scheduled / command / none。</summary>
    public string OnlineMode { get; set; } = "none";
    /// <summary>定时上线时间（HH:mm）。</summary>
    public string ScheduledOnlineTime { get; set; } = "";
    /// <summary>已绑定的联机锄地配置组名列表（顺序即执行顺序）。</summary>
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    /// <summary>一键快捷命令绑定：命令名 → 配置组名/一条龙名。与服务端模型保持同步。</summary>
    public Dictionary<string, string> QuickCommands { get; set; } = new();
    /// <summary>任务运行态过期时间戳（UTC）。仅 TaskRunning=true 时有效；超时即复位为 false。默认 MinValue 表示未超时。</summary>
    public DateTime TaskRunningExpireTime { get; set; } = DateTime.MinValue;
    /// <summary>上线状态过期时间戳（UTC），默认 MinValue 表示未过期。</summary>
    public DateTime OnlineReadyExpireTime { get; set; } = DateTime.MinValue;
    /// <summary>当天上线消费记录列表（每次广播 AllReady 消费一次时追加一条）。</summary>
    public List<object> OnlineHistory { get; set; } = [];
    /// <summary>预期开锄人数（默认 4）。服务端取所有已上线成员的最小值作为触发阈值。</summary>
    public int ExpectedHoeingPlayers { get; set; } = 4;
    /// <summary>当前上线事件的代序号（单调递增）。用于边沿检测：OnlineEventGeneration > LastOnlineConsumedGeneration 才算新事件。</summary>
    public int OnlineEventGeneration { get; set; } = 0;
    /// <summary>该 generation 是否已被消费。false = 待就绪，true = 已消费。初始 true（未产生过事件）。</summary>
    public bool OnlineEventConsumed { get; set; } = true;
    /// <summary>该 generation 的上线触发时间（UTC）。</summary>
    public DateTime OnlineEventTime { get; set; } = DateTime.MinValue;
}