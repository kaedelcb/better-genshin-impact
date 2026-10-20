namespace MultiplayerHoeingAssistant.Services;

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason)
{
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
    public string Protocol { get; init; } = "";
    public string Source { get; init; } = "";
    public string RequestId { get; init; } = "";
    public string QuickCommandKey { get; init; } = "";
    public IReadOnlyList<string> ParticipantUids { get; init; } = [];
    public IReadOnlyDictionary<string, int> IntentGenerations { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> Bindings { get; init; }
        = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>();
    public IReadOnlyList<CoordinatedHoeingMember> Members { get; init; } = [];
    public bool StateContinuation { get; init; }
    public bool UseCompletionActions { get; init; }
    public bool CleanupComplete { get; init; }
}

public sealed record CoordinatedHoeingBinding(string Name, string Type);
public sealed record CoordinatedHoeingMember(string Uid, string Stage, string Reason)
{
    public string Result { get; init; } = "";
}

public sealed record CoordinatedBatchResetResult
{
    public string ResetId { get; init; } = "";
    public bool Found { get; init; }
    public string BatchId { get; init; } = "";
    public int Generation { get; init; }
    public string Phase { get; init; } = "";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> TargetUids { get; init; } = [];
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
}

/// <summary>成员已有“绑定”完成动作的只读展示，不是另一份设置。</summary>
public sealed record CoordinatedCompletionAction(string Policy, string TaskType, string TaskName, int Priority)
{
    public bool IsValid() => Policy is "resume" or "stop" or "runSpecified"
        && TaskType is "group" or "onedragon" && TaskName != null && TaskName.Length <= 1024
        && (Policy != "runSpecified" || !string.IsNullOrWhiteSpace(TaskName));

    [System.Text.Json.Serialization.JsonIgnore]
    public string Description => !IsValid() ? "动作配置不完整"
        : Policy == "resume" ? "恢复原任务"
        : Policy == "stop" ? "锄完停止"
        : $"执行{(TaskType == "onedragon" ? "一条龙" : "配置组")}「{TaskName}」";

    public static CoordinatedCompletionAction FromSettings(Models.TaskConflictPolicySettings settings) => new(
        settings.Policy switch { Models.TaskConflictPolicy.Resume => "resume",
            Models.TaskConflictPolicy.RunSpecified => "runSpecified", _ => "stop" },
        settings.SpecifiedTaskType ?? "group", settings.SpecifiedTaskName?.Trim() ?? "", settings.SpecifiedTaskPriority);
}
