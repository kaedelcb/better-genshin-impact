namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 成员配置缓存：最近一次通过 IPC 成功读取的 BGI 配置列表快照。
/// 当 IPC 不可用（BGI 未启动/连接不上）时，回退到此缓存数据，
/// 确保房主在下发配置组/一条龙时仍能看到列表。
/// </summary>
public class MemberConfigCache
{
    /// <summary>配置组名列表（对应 User/ScriptGroup/*.json 文件名）</summary>
    public List<string> ConfigGroups { get; set; } = [];

    /// <summary>一条龙配置名列表（对应 User/OneDragon/*.json 文件名）</summary>
    public List<string> OneClickConfigs { get; set; } = [];

    /// <summary>配置组名 → 任务列表（含 name/index/status）</summary>
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];

    /// <summary>一条龙配置名 → 任务列表（含 name/index/enabled）</summary>
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];

    /// <summary>快捷键列表</summary>
    public List<object> Hotkeys { get; set; } = [];

    /// <summary>缓存最后更新时间（UTC）</summary>
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}