using System.Text.Json.Serialization;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 中断上下文模型。保存 BGI 被中断时正在执行的任务信息。
/// 仅进程内有效；AllConfig 不持久化此恢复现场。
/// 设计符合"助手做决策，BGI 做执行"的架构原则（bgi-implementation-patterns.md §31）。
/// </summary>
public class SuspendedTaskContext
{
    public System.Guid? RootRunId { get; set; }
    public System.Guid? AttemptId { get; set; }
    public string? NodeId { get; set; }
    public int? Iteration { get; set; }
    public string? TaskId { get; set; }
    public string? ConfigRevision { get; set; }
    public System.Collections.Generic.Dictionary<string, string>? ConfigurationRevisions { get; set; }
    public string? TakeoverTicket { get; set; }
    public long StopVersion { get; set; }
    /// <summary>任务类型：group / onedragon / solo</summary>
    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = "";

    /// <summary>配置组名称或一条龙配置名称</summary>
    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = "";

    /// <summary>配置组内当前被中断项目的 0-based 索引；恢复时转换为 1-based 请求游标。</summary>
    [JsonPropertyName("taskIndex")]
    public int TaskIndex { get; set; }

    /// <summary>配置组文件夹名</summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; set; } = "";

    /// <summary>任务名</summary>
    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    /// <summary>一条龙场景：当前正在执行的一条龙任务项稳定字符串 ID（R3 起为 GUID，来自 NextTaskId）。group/solo 场景为空。</summary>
    [JsonPropertyName("oneDragonTaskId")]
    public string OneDragonTaskId { get; set; } = string.Empty;

    /// <summary>一条龙场景：当前正在执行的配置组名（一条龙内嵌配置组）。非一条龙场景为空字符串。</summary>
    [JsonPropertyName("subTaskGroupName")]
    public string SubTaskGroupName { get; set; } = "";

    /// <summary>solo 场景：组内独立任务的设置快照（Newtonsoft 序列化的 SoloTaskSettingsObject，含联机开关等组级覆盖）。
    /// 空 = 无快照（恢复时退化为全局默认配置，同旧行为）。仅在挂起/恢复间传递，随 AllConfig 不持久化。</summary>
    [JsonPropertyName("soloSettingsJson")]
    public string SoloSettingsJson { get; set; } = "";

    /// <summary>配置组当前项目后面是否仍有项目。仅用于配置组内上线信号的续跑判定。</summary>
    [JsonIgnore]
    public bool HasNextGroupProject { get; set; } = true;

    /// <summary>R4.6 B6：被中断执行的收尾抑制权限（任务中心整龙调用为 true）；恢复时随合成请求携带。</summary>
    [JsonPropertyName("suppressCompletionAction")]
    public bool SuppressCompletionAction { get; set; }

    /// <summary>R4.6 B1：出现序号/尝试号（缺省 null，旧现场零变化）。</summary>
    [JsonPropertyName("occurrence")]
    public int? Occurrence { get; set; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; set; }
}
