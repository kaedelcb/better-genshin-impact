namespace BgiCoordinatorServer.RoomControl.Domain;

/// <summary>
/// 控制房间成员实体。同时维护“期望状态”（Desired）和“实际上报状态”（Reported）。
/// </summary>
public class ControlRoomMember
{
    public long Id { get; private set; }
    public string RoomCode { get; private set; } = "";
    public string PlayerUid { get; private set; } = "";
    public string PlayerName { get; private set; } = "";
    public string ClientInstanceId { get; private set; } = "";

    // ---------- 期望状态（Desired State）----------
    public string? ScheduledOnlineTime { get; private set; }
    public List<string> OnlineHoeingGroupNames { get; private set; } = [];
    public List<string> OnlineHoeingGroupTypes { get; private set; } = [];
    public int ExpectedHoeingPlayers { get; private set; } = 4;
    public Dictionary<string, string> QuickCommands { get; private set; } = new();
    /// <summary>Schedule  today's already-fired date (yyyy-MM-dd in Asia/Shanghai), empty/null means not fired today.</summary>
    public string ScheduledOnlineTimeFiredDate { get; private set; } = "";

    // ---------- 上报状态（Reported State）----------
    public bool IsOnline { get; private set; }
    public string? ConnectionId { get; private set; }
    public string BgiStatus { get; private set; } = "unknown";
    public bool TaskRunning { get; private set; }
    public string? CurrentTaskName { get; private set; }
    public string? CurrentTaskGroupName { get; private set; }
    public string? CurrentRouteDisplay { get; private set; }
    public bool AutoHoeingRunning { get; private set; }
    public string? AutoHoeingProgress { get; private set; }
    public List<string> ConfigGroups { get; private set; } = [];
    public List<string> OneClickConfigs { get; private set; } = [];
    public Dictionary<string, List<string>> ConfigGroupTasks { get; private set; } = [];
    public Dictionary<string, List<string>> OneClickTasks { get; private set; } = [];
    public List<object> Hotkeys { get; private set; } = [];
    public DateTime LastHeartbeatAt { get; private set; }

    // ---------- 上线事件状态 ----------
    public bool OnlineReady { get; private set; }
    public string OnlineMode { get; private set; } = "none";
    public int OnlineEventGeneration { get; private set; }
    public bool OnlineEventConsumed { get; private set; } = true;
    public DateTime? OnlineEventTime { get; private set; }
    public DateTime? OnlineReadyExpireTime { get; private set; }

    // ---------- 历史 ----------
    public List<OnlineHistoryEntry> OnlineHistory { get; private set; } = [];

    private ControlRoomMember() { }

    internal static ControlRoomMember Create(ControlRoom room, string playerUid, string playerName,
        string clientInstanceId, string connectionId)
    {
        return new ControlRoomMember
        {
            RoomCode = room.RoomCode,
            PlayerUid = playerUid,
            PlayerName = playerName,
            ClientInstanceId = clientInstanceId,
            ConnectionId = connectionId,
            IsOnline = true,
            LastHeartbeatAt = DateTime.UtcNow,
            OnlineEventGeneration = 0,
            OnlineEventConsumed = true
        };
    }

    public void MarkOnline(string connectionId, string clientInstanceId, string playerName)
    {
        ConnectionId = connectionId;
        ClientInstanceId = clientInstanceId;
        PlayerName = playerName;
        IsOnline = true;
        OnlineEventGeneration = 0;
        OnlineEventConsumed = true;
        UpdateHeartbeat();
    }

    public void MarkOffline()
    {
        IsOnline = false;
        ConnectionId = null;
        OnlineReady = false;
        OnlineEventConsumed = true;
    }

    public void UpdateHeartbeat() => LastHeartbeatAt = DateTime.UtcNow;

    public void UpdateDesiredState(MemberDesiredState state)
    {
        if (state.ScheduledOnlineTime != null)
        {
            // 定时时间变更后重置今天已触发标记，允许新时间再次触发
            if (ScheduledOnlineTime != state.ScheduledOnlineTime)
                ScheduledOnlineTimeFiredDate = "";
            ScheduledOnlineTime = state.ScheduledOnlineTime;
        }
        if (state.OnlineHoeingGroupNames != null)
            OnlineHoeingGroupNames = state.OnlineHoeingGroupNames.ToList();
        if (state.OnlineHoeingGroupTypes != null)
            OnlineHoeingGroupTypes = state.OnlineHoeingGroupTypes.ToList();
        if (state.ExpectedHoeingPlayers.HasValue)
            ExpectedHoeingPlayers = Math.Max(1, state.ExpectedHoeingPlayers.Value);
        if (state.QuickCommands != null)
            QuickCommands = new Dictionary<string, string>(state.QuickCommands);
    }

    public void UpdateReportedState(MemberReportedState state)
    {
        if (state.BgiStatus != null) BgiStatus = state.BgiStatus;
        if (state.TaskRunning.HasValue) TaskRunning = state.TaskRunning.Value;
        if (state.CurrentTaskName != null) CurrentTaskName = state.CurrentTaskName;
        if (state.CurrentTaskGroupName != null) CurrentTaskGroupName = state.CurrentTaskGroupName;
        if (state.CurrentRouteDisplay != null) CurrentRouteDisplay = state.CurrentRouteDisplay;
        if (state.AutoHoeingRunning.HasValue) AutoHoeingRunning = state.AutoHoeingRunning.Value;
        if (state.AutoHoeingProgress != null) AutoHoeingProgress = state.AutoHoeingProgress;
        if (state.ConfigGroups != null) ConfigGroups = state.ConfigGroups.ToList();
        if (state.OneClickConfigs != null) OneClickConfigs = state.OneClickConfigs.ToList();
        if (state.ConfigGroupTasks != null) ConfigGroupTasks = state.ConfigGroupTasks.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        if (state.OneClickTasks != null) OneClickTasks = state.OneClickTasks.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        if (state.Hotkeys != null) Hotkeys = state.Hotkeys.ToList();
        UpdateHeartbeat();
    }

    public void ReportOnlineEvent(int generation)
    {
        if (generation <= OnlineEventGeneration) return;

        OnlineEventGeneration = generation;
        OnlineEventConsumed = false;
        OnlineEventTime = DateTime.UtcNow;
        OnlineReady = true;
        OnlineReadyExpireTime = DateTime.UtcNow.AddMinutes(30);
    }

    public void MarkScheduledFired(string date) => ScheduledOnlineTimeFiredDate = date;

    public void ClearOnlineHistory() => OnlineHistory.Clear();

    public bool IsScheduleFiredToday(string date) => ScheduledOnlineTimeFiredDate == date;

    public void ConsumeOnlineEvent(DateTime consumeTime)
    {
        if (OnlineEventConsumed) return;

        OnlineHistory.Add(new OnlineHistoryEntry
        {
            Mode = OnlineMode,
            OnlineTime = OnlineEventTime ?? DateTime.UtcNow,
            ConsumeTime = consumeTime,
            Date = GetGameDate(consumeTime)
        });

        while (OnlineHistory.Count > 20)
            OnlineHistory.RemoveAt(0);

        OnlineEventConsumed = true;
        OnlineReady = false;
        OnlineMode = "none";
        OnlineReadyExpireTime = null;
    }

    public void ResetOnlineEvent()
    {
        OnlineReady = false;
        OnlineEventConsumed = true;
        OnlineReadyExpireTime = null;
    }

    private static string GetGameDate(DateTime utc)
    {
        var shanghai = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"));
        var date = shanghai.Hour < 4 ? shanghai.AddDays(-1) : shanghai;
        return date.ToString("yyyy-MM-dd");
    }
}

public class OnlineHistoryEntry
{
    public string Mode { get; set; } = "";
    public DateTime OnlineTime { get; set; }
    public DateTime ConsumeTime { get; set; }
    public string Date { get; set; } = "";
}

public record MemberDesiredState(
    string? ScheduledOnlineTime = null,
    List<string>? OnlineHoeingGroupNames = null,
    List<string>? OnlineHoeingGroupTypes = null,
    int? ExpectedHoeingPlayers = null,
    Dictionary<string, string>? QuickCommands = null
);

public record MemberReportedState(
    string? BgiStatus = null,
    bool? TaskRunning = null,
    string? CurrentTaskName = null,
    string? CurrentTaskGroupName = null,
    string? CurrentRouteDisplay = null,
    bool? AutoHoeingRunning = null,
    string? AutoHoeingProgress = null,
    List<string>? ConfigGroups = null,
    List<string>? OneClickConfigs = null,
    Dictionary<string, List<string>>? ConfigGroupTasks = null,
    Dictionary<string, List<string>>? OneClickTasks = null,
    List<object>? Hotkeys = null
);
