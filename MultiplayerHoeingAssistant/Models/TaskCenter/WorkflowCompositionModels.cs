using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

public sealed class WorkflowFlowDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "流程";
    [JsonPropertyName("steps")] public List<StartupStep> Steps { get; set; } = [];
    [JsonPropertyName("exitStepId")] public string? ExitStepId { get; set; }
    /// <summary>流程行执行完后跳到的主计划行 NodeId；为空=继续下一行。只对计划里的流程行有效，为空时不写入。</summary>
    [JsonPropertyName("jumpTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JumpTo { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WorkflowLanesDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "车道";
    /// <summary>车道自己的选择步骤；最后一个判断决定上/下车道。旧版独立流程配对保留 sourceFlowNodeId。</summary>
    [JsonPropertyName("decision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowFlowDefinition? Decision { get; set; }
    [JsonPropertyName("sourceFlowNodeId")] public string SourceFlowNodeId { get; set; } = "";
    [JsonPropertyName("yesName")] public string YesName { get; set; } = "是";
    [JsonPropertyName("noName")] public string NoName { get; set; } = "否";
    [JsonPropertyName("yesNodeIds")] public List<string> YesNodeIds { get; set; } = [];
    [JsonPropertyName("noNodeIds")] public List<string> NoNodeIds { get; set; } = [];
    /// <summary>「满足条件」路线走完后跳到的主计划行 NodeId；为空=继续下一行，为空时不写入（旧配对车道的布局键不变）。</summary>
    [JsonPropertyName("yesJumpTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? YesJumpTo { get; set; }
    /// <summary>「其他情况」路线走完后跳到的主计划行 NodeId；为空=继续下一行，为空时不写入。</summary>
    [JsonPropertyName("noJumpTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NoJumpTo { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
