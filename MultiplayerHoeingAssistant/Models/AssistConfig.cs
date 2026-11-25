using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

public class AssistConfig : IJsonOnDeserialized
{
    // 供现有连接、重连和状态订阅使用的当前房间地址；未设置房间时是新建档案的默认地址。
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "http://localhost:5000";

    /// <summary>总开关·手动单机（离线优先原则）：true=整个软件不连接服务器，联机功能全部不可用；
    /// ServerUrl 留空时无论本字段如何都恒为单机。持久化，重启保持。</summary>
    [JsonPropertyName("standaloneMode")]
    public bool StandaloneMode { get; set; } = false;

    [JsonPropertyName("controlRoomPassword")]
    public string ControlRoomPassword { get; set; } = string.Empty;

    [JsonPropertyName("teamUids")]
    public List<string> TeamUids { get; set; } = [];

    private List<AssistantRoomProfile> _roomProfiles = [];
    private bool _roomProfilesSpecified;

    [JsonPropertyName("roomProfiles")]
    public List<AssistantRoomProfile> RoomProfiles
    {
        get => _roomProfiles;
        set
        {
            _roomProfiles = value ?? [];
            _roomProfilesSpecified = true;
        }
    }

    [JsonPropertyName("activeRoomProfileId")]
    public string ActiveRoomProfileId { get; set; } = string.Empty;

    [JsonPropertyName("localRoomProfileId")]
    public string LocalRoomProfileId { get; set; } = string.Empty;

    /// <summary>耕地机页面「所有房间」汇总视图标识。</summary>
    public const string AllRoomsViewId = "__all_rooms__";

    /// <summary>耕地机页面当前查看的房间视图 ID：空或 "__all_rooms__" 表示「所有房间」，否则为具体房间档案 Id。持久化，重启恢复。</summary>
    [JsonPropertyName("viewedRoomProfileId")]
    public string ViewedRoomProfileId { get; set; } = AllRoomsViewId;

    /// <summary>本机耕地机内容页记忆；与当前登录和所查看的房间 ID 独立。</summary>
    [JsonPropertyName("showRoomOrchestration")]
    public bool ShowRoomOrchestration { get; set; }

    [JsonIgnore]
    public AssistantRoomProfile? LocalRoomProfile => FindRoomProfile(LocalRoomProfileId);

    private AssistantRoomProfile? FindRoomProfile(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        AssistantRoomProfile? selected = null;
        foreach (var profile in RoomProfiles)
        {
            if (profile is null || !string.Equals(profile.Id, id, StringComparison.Ordinal)) continue;
            if (selected != null) return null;
            selected = profile;
        }
        return selected;
    }

    [JsonIgnore]
    public AssistantRoomProfile? ActiveRoomProfile => FindRoomProfile(ActiveRoomProfileId);

    /// <summary>旧设置未固定本机房间时，沿用原本机设置所编辑的当前房间。</summary>
    internal bool EnsureLocalRoomProfile()
    {
        if (!string.IsNullOrEmpty(LocalRoomProfileId) || ActiveRoomProfile is not { } active) return false;
        LocalRoomProfileId = active.Id;
        return true;
    }

    [JsonIgnore]
    public string EffectiveRoomCode => ActiveRoomProfile?.RoomCode
        ?? (CanMigrateLegacyRoom ? GenerateLegacyRoomCode(TeamUids) : string.Empty);

    internal string GetRoomServerUrl(AssistantRoomProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.ServerUrl) ? ServerUrl ?? "" : profile.ServerUrl;

    internal bool FillLegacyRoomServerUrls()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)) return false;
        var changed = false;
        foreach (var profile in RoomProfiles)
        {
            if (profile is null || !string.IsNullOrWhiteSpace(profile.ServerUrl)) continue;
            profile.ServerUrl = ServerUrl;
            changed = true;
        }
        return changed;
    }

    // 显式空列表是新版的未配置状态，不可仅因用户后来改了本机身份而匹配到遗留 UID 列表。
    private bool CanMigrateLegacyRoom => !_roomProfilesSpecified && RoomProfiles.Count == 0
        && string.IsNullOrEmpty(ActiveRoomProfileId)
        && !string.IsNullOrWhiteSpace(PlayerUid)
        && !string.IsNullOrEmpty(ControlRoomPassword)
        && TeamUids is { Count: > 0 }
        && TeamUids.All(uid => !string.IsNullOrWhiteSpace(uid))
        && TeamUids.Contains(PlayerUid, StringComparer.Ordinal);

    /// <summary>原房间号用于查询旧资料；已有档案的激活指针有歧义时不猜测。</summary>
    public bool EnsureRoomProfiles()
    {
        ActiveRoomProfileId ??= string.Empty;
        if (!CanMigrateLegacyRoom) return false;

        var code = GenerateLegacyRoomCode(TeamUids);
        var profile = new AssistantRoomProfile { Id = $"legacy-{code}", Name = "默认房间", RoomCode = code };
        profile.Capture(this);
        RoomProfiles.Add(profile);
        _roomProfilesSpecified = true;
        ActiveRoomProfileId = profile.Id;
        LocalRoomProfileId = profile.Id;
        return true;
    }

    internal static string GenerateLegacyRoomCode(IEnumerable<string> playerUids)
    {
        var input = string.Join(",", playerUids.OrderBy(uid => uid));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..6];
    }

    [JsonPropertyName("bgiPath")]
    public string BgiPath { get; set; } = string.Empty;

    /// <summary>“更新BGI”弹窗记忆的安装包目录（扫描 .7z 更新包用）。持久化，重启保持。</summary>
    [JsonPropertyName("bgiPackageDir")]
    public string BgiPackageDir { get; set; } = string.Empty;

    /// <summary>更新时排除不覆盖的目录（相对 BGI 目录，如 "User"、“Tool\MultiplayerHoeingAssistant”）。
    /// 解压时这些目录下的条目一律跳过。持久化，下次打开弹窗自动带回。</summary>
    [JsonPropertyName("bgiUpdateExcludeDirs")]
    public List<string> BgiUpdateExcludeDirs { get; set; } = [];

    /// <summary>"更新BGI"弹窗里通过"选择指定压缩包…"手动加入的包完整路径列表。持久化，下次打开自动带回；
    /// 文件已被移动/删除或名字失效的自动剔除。与扫描目录重复的以扫描条目为准。</summary>
    [JsonPropertyName("bgiPickedPackages")]
    public List<string> BgiPickedPackages { get; set; } = [];

    [JsonPropertyName("bgiUpdateSourceId")]
    public string? BgiUpdateSourceId { get; set; }

    [JsonPropertyName("bgiCustomUpdateSources")]
    public List<BgiCustomUpdateSource> BgiCustomUpdateSources { get; set; } = [];

    /// <summary>更新前备份的目录，相对 BGI 目录。与排除目录分别保存；空列表表示未选目录。</summary>
    [JsonPropertyName("bgiUpdateBackupDirs")]
    public List<string> BgiUpdateBackupDirs { get; set; } = DefaultBgiUpdateBackupDirs();

    internal static List<string> DefaultBgiUpdateBackupDirs() => ["User", "Tools", "log"];

    private int _bgiUpdateBackupDirsVersion = 1;
    private bool _bgiUpdateBackupDirsVersionRead;

    // 区分旧版默认列表与新版中用户主动选择的同名目录，避免反复改写用户设置。
    [JsonPropertyName("bgiUpdateBackupDirsVersion")]
    public int BgiUpdateBackupDirsVersion
    {
        get => _bgiUpdateBackupDirsVersion;
        set
        {
            _bgiUpdateBackupDirsVersion = value;
            _bgiUpdateBackupDirsVersionRead = true;
        }
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (_bgiUpdateBackupDirsVersionRead) return;
        // 仅转换历史上实际发布过的完整默认列表；用户修改过的列表保持原样。
        if (BgiUpdateBackupDirs is ["User", "Tool", "Log"])
            BgiUpdateBackupDirs = ["User", "Tools", "log"];
        else if (BgiUpdateBackupDirs is ["User", "Tool"])
            BgiUpdateBackupDirs = ["User", "Tools"];
    }

    /// <summary>更新前是否备份指定目录（复制到 BGI\_update_backup\时间戳\）。默认开启。</summary>
    [JsonPropertyName("bgiUpdateBackupBeforeUpdate")]
    public bool BgiUpdateBackupBeforeUpdate { get; set; } = true;

    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = string.Empty;

    [JsonPropertyName("playerUid")]
    public string PlayerUid { get; set; } = string.Empty;

    /// <summary>用户是否已阅读并同意免责声明（首次启动弹出，同意后置 true，不再弹）</summary>
    [JsonPropertyName("disclaimerAccepted")]
    public bool DisclaimerAccepted { get; set; }

    /// <summary>一键快捷命令绑定：命令名 → 配置组名/一条龙名</summary>
    [JsonPropertyName("quickCommands")]
    public Dictionary<string, string> QuickCommands { get; set; } = DefaultQuickCommands();

    internal static Dictionary<string, string> DefaultQuickCommands() => new()
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

    [JsonPropertyName("checkBgiUpdatesOnStartup")]
    public bool CheckBgiUpdatesOnStartup { get; set; }

    // ========== 抢占式中断（multiplayer-hoeing-preempt-interrupt spec）==========

    /// <summary>定时上线时间（HH:mm），空字符串表示未设置</summary>
    [JsonPropertyName("scheduledOnlineTime")]
    public string ScheduledOnlineTime { get; set; } = "";

    /// <summary>迁入的闹钟或其执行目标尚未经本机确认；重启和普通设置保存均不能自动武装。</summary>
    [JsonPropertyName("scheduledOnlineRequiresConfirmation")]
    public bool ScheduledOnlineRequiresConfirmation { get; set; }

    /// <summary>当前房间最近一次定时上线已触发或已跳过的本地日期。</summary>
    [JsonPropertyName("scheduledOnlineLastFireDate")]
    public DateTime? ScheduledOnlineLastFireDate { get; set; }

    /// <summary>联机锄地配置组名称列表（多个，如传奇/精英/小怪）</summary>
    [JsonPropertyName("onlineHoeingGroupNames")]
    public List<string> OnlineHoeingGroupNames { get; set; } = [];

    /// <summary>联机锄地配置组类型列表（与 OnlineHoeingGroupNames 一一对应，每项为 "group" 或 "onedragon"）。</summary>
    [JsonPropertyName("onlineHoeingGroupTypes")]
    public List<string> OnlineHoeingGroupTypes { get; set; } = [];

    /// <summary>当前使用的联机配置组索引（0 = 第一个）</summary>
    [JsonPropertyName("onlineHoeingGroupIndex")]
    public int OnlineHoeingGroupIndex { get; set; } = 0;

    /// <summary>预期开锄人数（默认 4）。服务端取所有在线成员的最小值作为就绪阈值，且不按在线人数封顶：缺人不开锄。</summary>
    [JsonPropertyName("expectedHoeingPlayers")]
    public int ExpectedHoeingPlayers { get; set; } = 4;

    /// <summary>遥控器模式：本机无 BGI 时启用，跳过 BGI 进程监控，所有操作通过远程命令发送给其他成员。</summary>
    [JsonPropertyName("observerMode")]
    public bool ObserverMode { get; set; } = false;

    /// <summary>绕过系统代理直连服务器。本机开加速器/代理时勾选：.NET 的系统代理配置是进程级缓存，
    /// 代理软件（如 okz/加速器）挂掉后运行中的进程不会感知，会一直往失效的代理口撞（"目标计算机积极拒绝"），
    /// 勾选后新建连接显式禁用代理，不受系统代理状态影响。</summary>
    [JsonPropertyName("bypassSystemProxy")]
    public bool BypassSystemProxy { get; set; } = false;

    /// <summary>实例标识（UUID，助手进程启动时自动生成）。用于服务端区分同 UID 的多个连接实例。</summary>
    [JsonPropertyName("clientInstanceId")]
    public string ClientInstanceId { get; set; } = "";

    // ========== 上线锄地「完成后动作」（持久化；绑定弹窗配置，全员就绪触发上线锄地固定打断当前任务）==========

    /// <summary>上线锄地完成后动作："resume"=恢复任务（默认）/ "stop"=停止 / "runSpecified"=执行指定任务 / "switchRoom"=切换助手房间。</summary>
    [JsonPropertyName("onlineHoeingCompletionPolicy")]
    public string OnlineHoeingCompletionPolicy { get; set; } = "resume";

    /// <summary>切房完成动作的源、目标档案身份；不是旧批次的执行/恢复授权。</summary>
    [JsonPropertyName("onlineHoeingRoomSwitchCompletion")]
    public RoomSwitchCompletion? OnlineHoeingRoomSwitchCompletion { get; set; }

    /// <summary>上线锄地指定任务类型（"group"=配置组 / "onedragon"=一条龙 / "workflow"=计划表），供普通指定任务或切房后的指定任务使用。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedTaskType")]
    public string OnlineHoeingSpecifiedTaskType { get; set; } = "group";

    /// <summary>上线锄地指定任务名称，供普通指定任务或切房后的指定任务使用；执行时校验目录目标。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedTaskName")]
    public string OnlineHoeingSpecifiedTaskName { get; set; } = "";

    /// <summary>上线锄地完成后指定任务的普通仲裁优先级（int32，大者优先；缺省 0）。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedTaskPriority")]
    public int OnlineHoeingSpecifiedTaskPriority { get; set; }

    /// <summary>default 保留旧入口语义；beginning 明确从头；node 从稳定任务节点开始。</summary>
    [JsonPropertyName("onlineHoeingSpecifiedStartMode")]
    public string OnlineHoeingSpecifiedStartMode { get; set; } = "default";

    [JsonPropertyName("onlineHoeingSpecifiedStartTaskId")]
    public string OnlineHoeingSpecifiedStartTaskId { get; set; } = "";

    [JsonPropertyName("onlineHoeingSpecifiedStartTaskName")]
    public string OnlineHoeingSpecifiedStartTaskName { get; set; } = "";

    [JsonPropertyName("onlineHoeingSpecifiedConfigRevision")]
    public string OnlineHoeingSpecifiedConfigRevision { get; set; } = "";

    [JsonPropertyName("onlineHoeingSpecifiedWorkflowId")]
    public string OnlineHoeingSpecifiedWorkflowId { get; set; } = "";

    // 注意：成员卡片 6 个按键（配置组/一条龙/快捷键/停止/启动BGI/关闭游戏）的策略为固定行为：
    // 本机忙时一律「立即执行 + 执行完停止」（suspend 抢占 → 执行键动作 → 清上下文不恢复），无 UI、无配置项、不持久化。
}

public sealed record BgiCustomUpdateSource(string Label, string ManifestUrl);
