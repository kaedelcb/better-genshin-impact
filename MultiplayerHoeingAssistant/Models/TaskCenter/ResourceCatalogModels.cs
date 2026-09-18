using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>任务中心资源类型（锚点 1：龙/配置组/单项任务一律是资源）。</summary>
public enum TaskCenterResourceKind
{
    OneDragonConfig,
    ConfigGroup,
    SingleTask,
}

/// <summary>
/// 槲寄生 · 任务中心——资源目录条目（R4.3，R4 分解 D5/D14）。
/// 流程只持类型化引用（稳定标识 + 配置修订号），本目录是执行端资源的实时投影：
/// 不复制配置内容；缓存仅用于展示降级，执行时必须重新验证能力/epoch/revision。
/// </summary>
public sealed class TaskCenterResourceEntry
{
    [JsonPropertyName("kind")]
    public TaskCenterResourceKind Kind { get; set; }

    /// <summary>目录内稳定标识：onedragon:&lt;名&gt; / group:&lt;名&gt; / single:&lt;所属配置&gt;:&lt;taskId&gt;。</summary>
    [JsonPropertyName("stableId")]
    public string StableId { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>单项任务的所属配置（SingleTask 时必填）。</summary>
    [JsonPropertyName("ownerConfig")]
    public string? OwnerConfig { get; set; }

    /// <summary>配置修订号（describe 实时值；单项任务继承所属配置修订）。</summary>
    [JsonPropertyName("configRevision")]
    public string? ConfigRevision { get; set; }

    /// <summary>启用态（SingleTask；配置/组为 null）。</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>原生单项执行能力（BGI describe 声明；false 时执行预检响亮拒绝，D4）。</summary>
    [JsonPropertyName("singleExecutionSupported")]
    public bool SingleExecutionSupported { get; set; }

    /// <summary>执行端进程纪元（"pid:ticks"；换纪元后旧快照不可用于执行决策）。</summary>
    [JsonPropertyName("bgiEpoch")]
    public string? BgiEpoch { get; set; }

    /// <summary>来自本机缓存（BGI 不可达降级展示），不可作为执行依据。</summary>
    [JsonPropertyName("isFromCache")]
    public bool IsFromCache { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>资源目录快照（一次实时拉取或缓存降级）。</summary>
public sealed class ResourceCatalogSnapshot
{
    [JsonPropertyName("entries")]
    public List<TaskCenterResourceEntry> Entries { get; set; } = [];

    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>降级快照（缓存展示）；false = 实时拉取。</summary>
    [JsonPropertyName("isDegraded")]
    public bool IsDegraded { get; set; }

    /// <summary>降级原因（留痕纪律：能力门控/离线/失败必须可见）。</summary>
    [JsonPropertyName("degradedReason")]
    public string? DegradedReason { get; set; }

    /// <summary>拉取到的执行端纪元（快照级；逐条目另存）。</summary>
    [JsonPropertyName("bgiEpoch")]
    public string? BgiEpoch { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
