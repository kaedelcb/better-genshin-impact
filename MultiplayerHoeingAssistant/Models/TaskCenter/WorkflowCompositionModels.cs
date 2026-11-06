using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

public sealed class WorkflowFlowDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "流程";
    [JsonPropertyName("steps")] public List<StartupStep> Steps { get; set; } = [];
    [JsonPropertyName("exitStepId")] public string? ExitStepId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WorkflowLanesDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "车道";
    [JsonPropertyName("sourceFlowNodeId")] public string SourceFlowNodeId { get; set; } = "";
    [JsonPropertyName("yesName")] public string YesName { get; set; } = "是";
    [JsonPropertyName("noName")] public string NoName { get; set; } = "否";
    [JsonPropertyName("yesNodeIds")] public List<string> YesNodeIds { get; set; } = [];
    [JsonPropertyName("noNodeIds")] public List<string> NoNodeIds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
