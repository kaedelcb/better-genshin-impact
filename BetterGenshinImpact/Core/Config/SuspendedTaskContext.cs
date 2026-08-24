using System.Text.Json.Serialization;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 中断上下文模型。保存 BGI 被中断时正在执行的任务信息。
/// 值类型，持久化到 config.json。
/// 设计符合"助手做决策，BGI 做执行"的架构原则（bgi-implementation-patterns.md §31）。
/// </summary>
public class SuspendedTaskContext
{
    /// <summary>任务类型：group / onedragon / solo</summary>
    [JsonPropertyName("taskType")]
    public string TaskType { get; set; } = "";

    /// <summary>配置组名称或一条龙配置名称</summary>
    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = "";

    /// <summary>当前任务在配置组/一条龙中的索引（1-based）</summary>
    [JsonPropertyName("taskIndex")]
    public int TaskIndex { get; set; }

    /// <summary>配置组文件夹名</summary>
    [JsonPropertyName("folderName")]
    public string FolderName { get; set; } = "";

    /// <summary>任务名</summary>
    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    /// <summary>一条龙场景：当前正在执行的一条龙任务条目索引（1-based，来自 NextTaskIndex）。group/solo 场景为 0。</summary>
    [JsonPropertyName("oneDragonTaskIndex")]
    public int OneDragonTaskIndex { get; set; }

    /// <summary>一条龙场景：当前正在执行的配置组名（一条龙内嵌配置组）。非一条龙场景为空字符串。</summary>
    [JsonPropertyName("subTaskGroupName")]
    public string SubTaskGroupName { get; set; } = "";
}