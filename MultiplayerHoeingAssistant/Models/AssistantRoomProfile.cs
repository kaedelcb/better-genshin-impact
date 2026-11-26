using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>本机保存的单个控制房间档案。实例和玩家身份不随服务器或房间切换。</summary>
public sealed class AssistantRoomProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsLocalRoom { get; set; }

    [JsonIgnore]
    public bool IsActiveRoom { get; set; }

    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    [JsonPropertyName("roomCode")]
    public string RoomCode { get; set; } = string.Empty;

    /// <summary>房间簿导入的房间号是已存在的身份，编辑成员时保持原号码。</summary>
    [JsonPropertyName("preserveRoomCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveRoomCode { get; set; }

    [JsonIgnore]
    public bool NeedsRoomPassword => string.IsNullOrWhiteSpace(Password);

    [JsonPropertyName("teamUids")]
    public List<string> TeamUids { get; set; } = [];

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("expectedHoeingPlayers")]
    public int ExpectedHoeingPlayers { get; set; } = 4;

    [JsonPropertyName("onlineHoeingGroupNames")]
    public List<string> OnlineHoeingGroupNames { get; set; } = [];

    [JsonPropertyName("onlineHoeingGroupTypes")]
    public List<string> OnlineHoeingGroupTypes { get; set; } = [];

    [JsonPropertyName("onlineHoeingGroupIndex")]
    public int OnlineHoeingGroupIndex { get; set; }

    [JsonPropertyName("onlineHoeingCompletionPolicy")]
    public string OnlineHoeingCompletionPolicy { get; set; } = "resume";

    [JsonPropertyName("onlineHoeingRoomSwitchCompletion")]
    public RoomSwitchCompletion? OnlineHoeingRoomSwitchCompletion { get; set; }

    [JsonPropertyName("onlineHoeingSpecifiedTaskType")]
    public string OnlineHoeingSpecifiedTaskType { get; set; } = "group";

    [JsonPropertyName("onlineHoeingSpecifiedTaskName")]
    public string OnlineHoeingSpecifiedTaskName { get; set; } = string.Empty;

    [JsonPropertyName("onlineHoeingSpecifiedTaskPriority")]
    public int OnlineHoeingSpecifiedTaskPriority { get; set; }

    [JsonPropertyName("onlineHoeingSpecifiedStartMode")]
    public string OnlineHoeingSpecifiedStartMode { get; set; } = "default";

    [JsonPropertyName("onlineHoeingSpecifiedStartTaskId")]
    public string OnlineHoeingSpecifiedStartTaskId { get; set; } = string.Empty;

    [JsonPropertyName("onlineHoeingSpecifiedStartTaskName")]
    public string OnlineHoeingSpecifiedStartTaskName { get; set; } = string.Empty;

    [JsonPropertyName("onlineHoeingSpecifiedConfigRevision")]
    public string OnlineHoeingSpecifiedConfigRevision { get; set; } = string.Empty;

    [JsonPropertyName("onlineHoeingSpecifiedWorkflowId")]
    public string OnlineHoeingSpecifiedWorkflowId { get; set; } = string.Empty;

    [JsonPropertyName("quickCommands")]
    public Dictionary<string, string> QuickCommands { get; set; } = AssistConfig.DefaultQuickCommands();

    [JsonPropertyName("scheduledOnlineTime")]
    public string ScheduledOnlineTime { get; set; } = string.Empty;

    [JsonPropertyName("scheduledOnlineRequiresConfirmation")]
    public bool ScheduledOnlineRequiresConfirmation { get; set; }

    [JsonPropertyName("scheduledOnlineLastFireDate")]
    public DateTime? ScheduledOnlineLastFireDate { get; set; }

    /// <summary>从当前运行设置捕获房间字段，不改档案身份或房间号。</summary>
    public void Capture(AssistConfig config)
    {
        ServerUrl = config.ServerUrl;
        Password = config.ControlRoomPassword;
        TeamUids = config.TeamUids?.ToList() ?? [];
        ExpectedHoeingPlayers = config.ExpectedHoeingPlayers;
        OnlineHoeingGroupNames = config.OnlineHoeingGroupNames?.ToList() ?? [];
        OnlineHoeingGroupTypes = config.OnlineHoeingGroupTypes?.ToList() ?? [];
        OnlineHoeingGroupIndex = config.OnlineHoeingGroupIndex;
        CaptureCompletion(config);
        QuickCommands = CloneQuickCommands(config.QuickCommands);
        ScheduledOnlineTime = config.ScheduledOnlineTime;
        ScheduledOnlineRequiresConfirmation = config.ScheduledOnlineRequiresConfirmation;
        ScheduledOnlineLastFireDate = config.ScheduledOnlineLastFireDate;
    }

    /// <summary>定向完成动作保存不覆盖档案中的其他房间设置。</summary>
    internal void CaptureCompletion(AssistConfig config)
    {
        OnlineHoeingCompletionPolicy = config.OnlineHoeingCompletionPolicy;
        OnlineHoeingRoomSwitchCompletion = OnlineHoeingCompletionPolicy == "switchRoom"
            ? config.OnlineHoeingRoomSwitchCompletion : null;
        OnlineHoeingSpecifiedTaskType = config.OnlineHoeingSpecifiedTaskType;
        OnlineHoeingSpecifiedTaskName = config.OnlineHoeingSpecifiedTaskName;
        OnlineHoeingSpecifiedTaskPriority = config.OnlineHoeingSpecifiedTaskPriority;
        OnlineHoeingSpecifiedStartMode = config.OnlineHoeingSpecifiedStartMode;
        OnlineHoeingSpecifiedStartTaskId = config.OnlineHoeingSpecifiedStartTaskId;
        OnlineHoeingSpecifiedStartTaskName = config.OnlineHoeingSpecifiedStartTaskName;
        OnlineHoeingSpecifiedConfigRevision = config.OnlineHoeingSpecifiedConfigRevision;
        OnlineHoeingSpecifiedWorkflowId = config.OnlineHoeingSpecifiedWorkflowId;
    }

    /// <summary>将房间字段复制到运行设置；何时确认激活由调用方决定。</summary>
    public void ApplyTo(AssistConfig config)
    {
        config.ServerUrl = config.GetRoomServerUrl(this);
        config.ControlRoomPassword = Password;
        config.TeamUids = TeamUids?.ToList() ?? [];
        config.ExpectedHoeingPlayers = ExpectedHoeingPlayers;
        config.OnlineHoeingGroupNames = OnlineHoeingGroupNames?.ToList() ?? [];
        config.OnlineHoeingGroupTypes = OnlineHoeingGroupTypes?.ToList() ?? [];
        config.OnlineHoeingGroupIndex = OnlineHoeingGroupIndex;
        ApplyCompletionTo(config);
        config.QuickCommands = CloneQuickCommands(QuickCommands);
        config.ScheduledOnlineTime = ScheduledOnlineTime;
        config.ScheduledOnlineRequiresConfirmation = ScheduledOnlineRequiresConfirmation;
        config.ScheduledOnlineLastFireDate = ScheduledOnlineLastFireDate;
    }

    internal void ApplyCompletionTo(AssistConfig config)
    {
        config.OnlineHoeingCompletionPolicy = OnlineHoeingCompletionPolicy;
        config.OnlineHoeingRoomSwitchCompletion = OnlineHoeingCompletionPolicy == "switchRoom"
            ? OnlineHoeingRoomSwitchCompletion : null;
        config.OnlineHoeingSpecifiedTaskType = OnlineHoeingSpecifiedTaskType;
        config.OnlineHoeingSpecifiedTaskName = OnlineHoeingSpecifiedTaskName;
        config.OnlineHoeingSpecifiedTaskPriority = OnlineHoeingSpecifiedTaskPriority;
        config.OnlineHoeingSpecifiedStartMode = OnlineHoeingSpecifiedStartMode;
        config.OnlineHoeingSpecifiedStartTaskId = OnlineHoeingSpecifiedStartTaskId;
        config.OnlineHoeingSpecifiedStartTaskName = OnlineHoeingSpecifiedStartTaskName;
        config.OnlineHoeingSpecifiedConfigRevision = OnlineHoeingSpecifiedConfigRevision;
        config.OnlineHoeingSpecifiedWorkflowId = OnlineHoeingSpecifiedWorkflowId;
    }

    private static Dictionary<string, string> CloneQuickCommands(Dictionary<string, string>? commands)
        => commands == null ? AssistConfig.DefaultQuickCommands() : new(commands, commands.Comparer);
}
