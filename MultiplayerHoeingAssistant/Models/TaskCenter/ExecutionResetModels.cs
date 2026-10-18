using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>用户显式放弃此次执行的追加审计；不改写原运行状态、失败、游标或外部执行结果。</summary>
public sealed record ExecutionResetReleaseRecord
{
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("resetId")] public string ResetId { get; init; } = "";
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("workflowId")] public string WorkflowId { get; init; } = "";
    [JsonPropertyName("wireRunId")] public string WireRunId { get; init; } = "";
    [JsonPropertyName("originalState")] public WorkflowRunState OriginalState { get; init; }
    [JsonPropertyName("factsHash")] public string FactsHash { get; init; } = "";
    [JsonPropertyName("completedAtUtc")] public DateTimeOffset CompletedAtUtc { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "用户重置执行状态；保留原运行历史，未重发旧任务。";
    [JsonPropertyName("processExits")] public List<ExecutionResetProcessExitRecord> ProcessExits { get; init; } = [];
}

public sealed record ExecutionResetProcessExitRecord
{
    [JsonPropertyName("originalEpoch")] public string OriginalEpoch { get; init; } = "";
    [JsonPropertyName("currentEpoch")] public string CurrentEpoch { get; init; } = "";
    [JsonPropertyName("disposition")] public string Disposition { get; init; } = "";
    [JsonPropertyName("observedStartTicksUtc")] public long? ObservedStartTicksUtc { get; init; }
    [JsonPropertyName("observedAtUtc")] public DateTimeOffset ObservedAtUtc { get; init; }
}
