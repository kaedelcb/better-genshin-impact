namespace MultiplayerHoeingAssistant.Models;

public class ControlStatus
{
    private TaskExecutionIdentitySnapshot? _currentExecution;
    public static readonly TimeSpan TaskStatusFreshnessWindow = TimeSpan.FromSeconds(20);

    public string RoomCode { get; set; } = string.Empty;
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string BgiStatus { get; set; } = "unknown";
    /// <summary>本机 BGI 的完整版本号（如 "0.64.2+lcb.22.7-NexusBGI-fix13"）。
    /// 优先取执行端快照 bgiVersion，BGI 未运行时回退读 BgiPath 的 ProductVersion。空串=未知。</summary>
    public string BgiVersion { get; set; } = string.Empty;
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
    /// <summary>本轮 task.status 查询是否成功且含合法 running 字段；false 表示执行占用未知，不能视为空闲。</summary>
    public bool TaskStatusAvailable { get; set; }
    /// <summary>BGI 响应携带的进程纪元（PID:StartTicks）；用于拒绝重启前的旧快照。</summary>
    public string? TaskStatusBgiEpoch { get; set; }
    /// <summary>
    /// 当前 BGI 逻辑执行根的版本化身份；为空表示没有可核验的执行根，不能按任务名称推断身份。
    /// 这是本机状态采集字段，不进入房间成员的公开状态载荷。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskExecutionIdentitySnapshot? CurrentExecution
    {
        get => GetFreshCurrentExecution(DateTimeOffset.UtcNow);
        set => _currentExecution = value is not null && HasFreshTaskStatus(DateTimeOffset.UtcNow) ? value : null;
    }

    /// <summary>按观测时效及可选的当前 BGI epoch 读取执行身份；失效身份会从缓存清除。</summary>
    public TaskExecutionIdentitySnapshot? GetFreshCurrentExecution(DateTimeOffset nowUtc, string? expectedBgiEpoch = null)
    {
        if (!HasFreshTaskStatus(nowUtc)
            || (expectedBgiEpoch is not null
                && !string.Equals(TaskStatusBgiEpoch, expectedBgiEpoch, StringComparison.Ordinal)))
        {
            _currentExecution = null;
            return null;
        }
        return _currentExecution;
    }
    /// <summary>该状态快照实际取得时间；超过有效期、来自未来或缺失时不得用于准入。</summary>
    public DateTimeOffset? TaskStatusObservedAtUtc { get; set; }
    /// <summary>最近一次任务是否被用户手动取消（BGI wasCancelled，置位保留到下个任务启动；桌宠表情用）。</summary>
    public bool WasCancelled { get; set; }
    /// <summary>当前正在执行的任务名称（如"锄地一条龙"、"传奇"等）。</summary>
    public string? CurrentTaskName { get; set; }
    /// <summary>当前正在执行的配置组/一条龙名称（groupName，独立任务时为 null）。</summary>
    public string? CurrentTaskGroupName { get; set; }
    /// <summary>当前联机锄地线路展示文本（如"第2条线路: 蒙德城"，非锄地为 null）。</summary>
    public string? CurrentRouteDisplay { get; set; }
    /// <summary>配置组内脚本任务（JS/地图追踪）当前执行的具体线路名（非脚本任务为 null；与锄地线路展示互不覆盖）。</summary>
    public string? CurrentScriptRouteName { get; set; }
    /// <summary>是否正在联机锄地（锄地房间中）。</summary>
    public bool AutoHoeingRunning { get; set; }
    /// <summary>当前锄地进度文本（仅上报给自身用，控制端不展示给对方）。</summary>
    public string? AutoHoeingProgress { get; set; }
    /// <summary>JS 脚本经 BGI 内部状态通道上报的通用任务进度；非脚本任务为 null。</summary>
    public string? ScriptTaskProgress { get; set; }
    /// <summary>好感任务进度文本（BGI FriendshipProgress 合成，如"好感任务：第 3/50 轮，预计剩余 42分10秒，预计 08:30 完成"）。
    /// 纯增量字段，旧 BGI 无此字段时保持 null（桌宠回退本地计时+日志轮次）。</summary>
    public string? FriendshipProgress { get; set; }
    /// <summary>SignalR 房间当前人数（BGI 只读快照透传；0=未知/未在房间，旧 BGI 无此字段）。
    /// 桌宠"已联机"态 chip 分子用。</summary>
    public int RoomPlayerCount { get; set; }
    /// <summary>是否已上线（标记了"已上线"但联机锄地尚未开始）。</summary>
    public bool OnlineReady { get; set; }
    /// <summary>上线方式：scheduled / command / none。</summary>
    public string OnlineMode { get; set; } = "none";
    /// <summary>定时上线时间（HH:mm）。</summary>
    public string ScheduledOnlineTime { get; set; } = "";
    /// <summary>已绑定的联机锄地配置组名列表（顺序即执行顺序）。</summary>
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    /// <summary>一键快捷命令绑定：命令名 → 配置组名/一条龙名。</summary>
    public Dictionary<string, string> QuickCommands { get; set; } = new();
    /// <summary>预期开锄人数（默认 4）。服务端取所有已上线成员的最小值作为触发阈值。</summary>
    public int ExpectedHoeingPlayers { get; set; } = 4;

    public bool HasFreshTaskStatus(DateTimeOffset nowUtc)
    {
        if (!TaskStatusAvailable || string.IsNullOrWhiteSpace(TaskStatusBgiEpoch)
            || TaskStatusObservedAtUtc is not { } observedAtUtc)
            return false;

        var age = nowUtc - observedAtUtc;
        return age >= TimeSpan.Zero && age <= TaskStatusFreshnessWindow;
    }
}

public sealed record TaskExecutionIdentitySnapshot(
    Guid ExecutionInstanceId,
    long StateRevision,
    Guid RunId,
    Guid? JobId,
    string Kind,
    string Source,
    string Name,
    bool StopRequested);
