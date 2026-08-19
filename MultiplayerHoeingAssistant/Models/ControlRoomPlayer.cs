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
    /// <summary>是否正在联机锄地（锄地房间中）。</summary>
    public bool AutoHoeingRunning { get; set; }
    public DateTime LastHeartbeat { get; set; }
    /// <summary>配置组名 → 任务列表（含 name/index/status，供状态显示与编辑）。与服务端模型保持同步。</summary>
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];
    /// <summary>一条龙配置名 → 任务列表（含 name/index/enabled，供状态显示与编辑）。与服务端模型保持同步。</summary>
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];
    /// <summary>快捷键列表（含 configName/functionName/hotkeyText，供 PC 端执行）。与服务端模型保持同步。</summary>
    public List<object> Hotkeys { get; set; } = [];
}