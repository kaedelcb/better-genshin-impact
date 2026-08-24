namespace MultiplayerHoeingAssistant.Models;

public class ControlRoomPlayer
{
    public string ConnectionId { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool Online { get; set; }
    public string BgiStatus { get; set; } = "unknown";
    public List<string> ConfigGroups { get; set; } = [];
    public List<string> OneClickConfigs { get; set; } = [];
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
    public DateTime LastHeartbeat { get; set; }
    /// <summary>是否已上线（标记了"已上线"但联机锄地尚未开始）。</summary>
    public bool OnlineReady { get; set; } = false;
    /// <summary>上线方式：scheduled / command / none。</summary>
    public string OnlineMode { get; set; } = "none";
    /// <summary>定时上线时间（HH:mm）。</summary>
    public string ScheduledOnlineTime { get; set; } = "";
    /// <summary>已绑定的联机锄地配置组名列表（顺序即执行顺序）。与服务端模型保持同步。</summary>
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    /// <summary>一键快捷命令绑定：命令名 → 配置组名/一条龙名。与服务端模型保持同步。</summary>
    public Dictionary<string, string> QuickCommands { get; set; } = new();
    /// <summary>当天上线消费记录列表（只显示自己的）。与服务端模型保持同步。</summary>
    public List<object> OnlineHistory { get; set; } = [];
    /// <summary>预期开锄人数。与服务端模型保持同步。</summary>
    public int ExpectedHoeingPlayers { get; set; } = 4;
    /// <summary>配置组名 → 任务列表（含 name/index/status，供状态显示与编辑）。与服务端模型保持同步。</summary>
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];
    /// <summary>一条龙配置名 → 任务列表（含 name/index/enabled，供状态显示与编辑）。与服务端模型保持同步。</summary>
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];
    /// <summary>快捷键列表（含 configName/functionName/hotkeyText，供 PC 端执行）。与服务端模型保持同步。</summary>
    public List<object> Hotkeys { get; set; } = [];
}