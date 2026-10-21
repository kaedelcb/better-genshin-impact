namespace MultiplayerHoeingAssistant.Models;

public class ControlRoomPlayer
{
    public string ConnectionId { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string ClientInstanceId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public bool Online { get; set; }
    public string BgiStatus { get; set; } = "unknown";
    /// <summary>该成员本机 BGI 的完整版本号（服务端 reportStatus 转发）。空串=未上报，UI 显示"-"。</summary>
    public string BgiVersion { get; set; } = string.Empty;
    public string? HoeingBatchId { get; set; }
    public string? HoeingStartPhase { get; set; }
    public string? HoeingStartMessage { get; set; }
    /// <summary>服务端确认该后续任务不再占用本轮等待；null 表示旧服务端或尚未确认。</summary>
    public bool? HoeingCompletionWaitReleased { get; set; }
    public Services.CoordinatedCompletionAction? HoeingCompletionAction { get; set; }
    public List<string> ConfigGroups { get; set; } = [];
    public List<string> OneClickConfigs { get; set; } = [];
    /// <summary>是否正在执行任务（任意任务）。</summary>
    public bool TaskRunning { get; set; }
    /// <summary>最近一次任务快照是否可信；null 兼容旧服务端。</summary>
    public bool? TaskStatusAvailable { get; set; }
    /// <summary>正在运行的 BGI 任务是否暂停。</summary>
    public bool TaskPaused { get; set; }
    /// <summary>当前正在执行的任务名称。</summary>
    public string? CurrentTaskName { get; set; }
    /// <summary>当前正在执行的配置组/一条龙名称（groupName，独立任务时为 null）。</summary>
    public string? CurrentTaskGroupName { get; set; }
    /// <summary>当前联机锄地线路展示文本（如"第2条线路: 蒙德城"，非锄地为 null）。</summary>
    public string? CurrentRouteDisplay { get; set; }
    /// <summary>配置组内脚本任务（JS/地图追踪）当前执行的具体线路名（非脚本任务为 null；与锄地线路展示互不覆盖）。与服务端模型保持同步。</summary>
    public string? CurrentScriptRouteName { get; set; }
    /// <summary>是否正在联机锄地（锄地房间中）。</summary>
    public bool AutoHoeingRunning { get; set; }
    /// <summary>当前联机锄地进度文本（如"当前进度：开始第 2/4 条线路: xx，本线路预计用时..."，非锄地为 null）。与服务端模型保持同步。</summary>
    public string? AutoHoeingProgress { get; set; }
    /// <summary>JS 脚本经执行端 BGI 内部状态通道上报的通用任务进度。</summary>
    public string? ScriptTaskProgress { get; set; }
    /// <summary>好感任务进度文本（BGI FriendshipProgress 合成，好感任务不在跑为 null）。与服务端模型保持同步。</summary>
    public string? FriendshipProgress { get; set; }
    public DateTime LastHeartbeat { get; set; }
    /// <summary>是否已上线（标记了"已上线"但联机锄地尚未开始）。</summary>
    public bool OnlineReady { get; set; } = false;
    /// <summary>上线方式：scheduled / command / none。</summary>
    public string OnlineMode { get; set; } = "none";
    /// <summary>定时上线时间（HH:mm）。</summary>
    public string ScheduledOnlineTime { get; set; } = "";
    /// <summary>已绑定的联机锄地项目名称列表（配置组/一条龙，顺序即执行顺序）。与服务端模型保持同步。</summary>
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    /// <summary>联机锄地绑定类型列表，与 OnlineHoeingGroupNames 按位置一一对应；旧客户端缺失时按配置组兼容。</summary>
    public List<string> OnlineHoeingGroupTypes { get; set; } = [];
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
