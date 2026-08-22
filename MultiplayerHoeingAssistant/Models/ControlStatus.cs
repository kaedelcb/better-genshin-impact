namespace MultiplayerHoeingAssistant.Models;

public class ControlStatus
{
    public string RoomCode { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string BgiStatus { get; set; } = "unknown";
    public List<string> ConfigGroups { get; set; } = [];
    public List<string> OneClickConfigs { get; set; } = [];
    /// <summary>配置组名 → 该配置组内任务名列表（WEB 端"从此处开始执行"用）。</summary>
    public Dictionary<string, List<string>> ConfigGroupTasks { get; set; } = [];
    /// <summary>配置组名 → 任务列表（含 name/index/status，供状态显示与编辑）。</summary>
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];
    /// <summary>一条龙配置名 → 任务列表（含 name/index/enabled，供状态显示与编辑）。</summary>
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];
    /// <summary>快捷键列表（含 configName/functionName/hotkeyText，供 PC 端执行）。</summary>
    public List<object> Hotkeys { get; set; } = [];
    /// <summary>一条龙配置名 → 该配置内任务名列表（WEB 端"从此处开始执行"用）。</summary>
    public Dictionary<string, List<string>> OneClickTasks { get; set; } = [];
    /// <summary>是否正在执行任务（任意任务，包括锄地、一条龙、配置组等）。</summary>
    public bool TaskRunning { get; set; }
    /// <summary>当前正在执行的任务名称（如"锄地一条龙"、"传奇"等）。</summary>
    public string? CurrentTaskName { get; set; }
    /// <summary>是否正在联机锄地（锄地房间中）。</summary>
    public bool AutoHoeingRunning { get; set; }
    /// <summary>当前锄地进度文本（仅上报给自身用，控制端不展示给对方）。</summary>
    public string? AutoHoeingProgress { get; set; }
    /// <summary>是否已上线（标记了"已上线"但联机锄地尚未开始）。</summary>
    public bool OnlineReady { get; set; }
    /// <summary>上线方式：scheduled / command / none。</summary>
    public string OnlineMode { get; set; } = "none";
    /// <summary>定时上线时间（HH:mm）。</summary>
    public string ScheduledOnlineTime { get; set; } = "";
    /// <summary>已绑定的联机锄地配置组名列表（顺序即执行顺序）。</summary>
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    /// <summary>预期开锄人数（默认 4）。服务端取所有已上线成员的最小值作为触发阈值。</summary>
    public int ExpectedHoeingPlayers { get; set; } = 4;
}