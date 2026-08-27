using BgiCoordinatorServer.RoomControl.Domain;

namespace BgiCoordinatorServer.RoomControl.Dto;

public class MemberDto
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string ClientInstanceId { get; set; } = "";
    public bool IsOnline { get; set; }
    public string BgiStatus { get; set; } = "unknown";
    public bool TaskRunning { get; set; }
    public string? CurrentTaskName { get; set; }
    public string? CurrentTaskGroupName { get; set; }
    public string? CurrentRouteDisplay { get; set; }
    public bool AutoHoeingRunning { get; set; }
    public string? AutoHoeingProgress { get; set; }
    public bool OnlineReady { get; set; }
    public string OnlineMode { get; set; } = "none";
    public string? ScheduledOnlineTime { get; set; }
    public List<string> OnlineHoeingGroupNames { get; set; } = [];
    public List<string> OnlineHoeingGroupTypes { get; set; } = [];
    public int ExpectedHoeingPlayers { get; set; } = 4;
    public Dictionary<string, string> QuickCommands { get; set; } = new();
    public List<string> ConfigGroups { get; set; } = [];
    public List<string> OneClickConfigs { get; set; } = [];
    public Dictionary<string, List<string>> ConfigGroupTasks { get; set; } = [];
    public Dictionary<string, List<string>> OneClickTasks { get; set; } = [];
    public List<object> Hotkeys { get; set; } = [];

    public static MemberDto FromDomain(ControlRoomMember member)
    {
        return new MemberDto
        {
            PlayerUid = member.PlayerUid,
            PlayerName = member.PlayerName,
            ClientInstanceId = member.ClientInstanceId,
            IsOnline = member.IsOnline,
            BgiStatus = member.BgiStatus,
            TaskRunning = member.TaskRunning,
            CurrentTaskName = member.CurrentTaskName,
            CurrentTaskGroupName = member.CurrentTaskGroupName,
            CurrentRouteDisplay = member.CurrentRouteDisplay,
            AutoHoeingRunning = member.AutoHoeingRunning,
            AutoHoeingProgress = member.AutoHoeingProgress,
            OnlineReady = member.OnlineReady,
            OnlineMode = member.OnlineMode,
            ScheduledOnlineTime = member.ScheduledOnlineTime,
            OnlineHoeingGroupNames = member.OnlineHoeingGroupNames,
            OnlineHoeingGroupTypes = member.OnlineHoeingGroupTypes,
            ExpectedHoeingPlayers = member.ExpectedHoeingPlayers,
            QuickCommands = member.QuickCommands,
            ConfigGroups = member.ConfigGroups,
            OneClickConfigs = member.OneClickConfigs,
            ConfigGroupTasks = member.ConfigGroupTasks,
            OneClickTasks = member.OneClickTasks,
            Hotkeys = member.Hotkeys
        };
    }
}

public class MemberDesiredStateDto
{
    public string PlayerUid { get; set; } = "";
    public string? ScheduledOnlineTime { get; set; }
    public List<string>? OnlineHoeingGroupNames { get; set; }
    public List<string>? OnlineHoeingGroupTypes { get; set; }
    public int? ExpectedHoeingPlayers { get; set; }
    public Dictionary<string, string>? QuickCommands { get; set; }
}

public class ControlStatusDto
{
    public string? BgiStatus { get; set; }
    public bool? TaskRunning { get; set; }
    public string? CurrentTaskName { get; set; }
    public string? CurrentTaskGroupName { get; set; }
    public string? CurrentRouteDisplay { get; set; }
    public bool? AutoHoeingRunning { get; set; }
    public string? AutoHoeingProgress { get; set; }
    public List<string>? ConfigGroups { get; set; }
    public List<string>? OneClickConfigs { get; set; }
    public Dictionary<string, List<string>>? ConfigGroupTasks { get; set; }
    public Dictionary<string, List<string>>? OneClickTasks { get; set; }
    public List<object>? Hotkeys { get; set; }
}
