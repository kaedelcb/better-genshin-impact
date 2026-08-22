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
}