using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>实际流程去向。资源节点同时带 flow.route 标记，使旧执行器拒绝而非降级成顺序链。</summary>
public sealed class WorkflowPath
{
    [JsonPropertyName("next")] public string? Next { get; set; }
    [JsonPropertyName("yes")] public string? Yes { get; set; }
    [JsonPropertyName("no")] public string? No { get; set; }
    [JsonPropertyName("condition")] public WorkflowPathCondition? Condition { get; set; }
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtensionData { get; set; }
}

public sealed class WorkflowPathCondition
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "weekdays";
    [JsonPropertyName("value")] public bool? Value { get; set; }
    [JsonPropertyName("days")] public List<string>? Days { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("until")] public string? Until { get; set; }
    [JsonPropertyName("sourceNodeId")] public string? SourceNodeId { get; set; }
    [JsonExtensionData] public Dictionary<string,JsonElement>? ExtensionData { get; set; }
}
