using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiplayerHoeingAssistant.Models;

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
    // 冻结受理时各成员上报的有效动作。手动未勾选完成动作的批次不消费此字典。
    public IReadOnlyDictionary<string, CoordinatedCompletionAction> ConfirmedCompletionActions { get; init; }
        = new Dictionary<string, CoordinatedCompletionAction>();
    public IReadOnlyList<CoordinatedHoeingMember> Members { get; init; } = [];
    public bool StateContinuation { get; init; }
    public bool PriorityRecovery { get; init; }
    public bool UseCompletionActions { get; init; }
    public bool CleanupComplete { get; init; }

    /// <summary>执行端冻结完整本机策略后，与受理的动作核对；不得从 Start 摘要重建房间配置。</summary>
    public bool MatchesRoomSwitchCompletion(string uid, TaskConflictPolicySettings settings)
        => CoordinatedCompletionAction.MatchesRoomSwitchConfirmation(
            ConfirmedCompletionActions?.GetValueOrDefault(uid), CoordinatedCompletionAction.FromSettings(settings));
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

/// <summary>成员已有“绑定”完成动作的只读展示与确认摘要，不传房间登录凭据、也不作为执行配置。</summary>
public sealed record CoordinatedCompletionAction(string Policy, string TaskType, string TaskName, int Priority,
    string StartMode = "default", string StartTaskId = "", string StartTaskName = "", string ConfigRevision = "", string WorkflowId = "",
    string TargetName = "", string TargetRoomCode = "", bool RunAfterSwitch = false, string ActionRevision = "")
{
    public bool IsValid()
    {
        var validShape = Policy is "resume" or "stop" or "runSpecified" or "switchRoom"
            && TaskType is "group" or "onedragon" or "workflow" && TaskName != null && TaskName.Length <= 1024
            && StartMode is "default" or "beginning" or "node"
            && StartTaskId != null && StartTaskName != null && ConfigRevision != null && WorkflowId != null
            && StartTaskId.Length <= 1024 && StartTaskName.Length <= 1024 && ConfigRevision.Length <= 1024 && WorkflowId.Length <= 1024;
        if (!validShape) return false;
        if (Policy == "switchRoom")
        {
            if (string.IsNullOrWhiteSpace(TargetName) || TargetName.Length > 1024
                || string.IsNullOrWhiteSpace(TargetRoomCode) || TargetRoomCode.Length > 1024
                || ActionRevision is not { Length: 67 } || !ActionRevision.StartsWith("v1:", StringComparison.Ordinal)
                || !ActionRevision[3..].All(Uri.IsHexDigit)) return false;
            if (!RunAfterSwitch) return true;
            // 新后继任务没有隐式旧入口：必须冻结具体任务、起点和目录修订。
            if (string.IsNullOrWhiteSpace(TaskName) || string.IsNullOrWhiteSpace(ConfigRevision)
                || (TaskType == "workflow" ? string.IsNullOrWhiteSpace(WorkflowId) : WorkflowId.Length > 0)) return false;
            return StartMode switch
            {
                "beginning" => StartTaskId.Length == 0 && StartTaskName.Length == 0,
                "node" => !string.IsNullOrWhiteSpace(StartTaskId) && !string.IsNullOrWhiteSpace(StartTaskName),
                _ => false
            };
        }
        return Policy != "runSpecified" || !string.IsNullOrWhiteSpace(TaskName)
            && (StartMode == "default" || !string.IsNullOrWhiteSpace(ConfigRevision))
            && (StartMode != "node" || !string.IsNullOrWhiteSpace(StartTaskId))
            && (TaskType != "workflow" || StartMode != "default" && !string.IsNullOrWhiteSpace(WorkflowId));
    }

    private string SpecifiedDescription => $"执行{(TaskType == "workflow" ? "计划表" : TaskType == "onedragon" ? "一条龙" : "配置组")}「{TaskName}」"
        + (StartMode == "beginning" ? "（从头）" : StartMode == "node" ? $"（从「{StartTaskName}」）" : "");

    [JsonIgnore]
    public string Description => Policy == "switchRoom"
        ? $"切换助手房间{(string.IsNullOrWhiteSpace(TargetName) ? "（未选择目标）" : $"「{TargetName} · {TargetRoomCode}」")}"
            + (RunAfterSwitch ? $"，切换成功后{SpecifiedDescription}（优先级 {Priority}）" : "，切换成功后待命")
            + (!IsValid() ? "（配置无效，请核对）" : "")
        : !IsValid() ? "动作配置不完整"
        : Policy == "resume" ? "恢复原任务"
        : Policy == "stop" ? "锄完停止"
        : SpecifiedDescription;

    /// <summary>任何一侧涉及切房，必须显式确认同一完整有效动作；原三种动作不新增版本限制。</summary>
    public static bool MatchesRoomSwitchConfirmation(CoordinatedCompletionAction? confirmed, CoordinatedCompletionAction? actual)
    {
        if (confirmed?.Policy != "switchRoom" && actual?.Policy != "switchRoom") return true;
        return confirmed?.Policy == "switchRoom" && actual?.Policy == "switchRoom"
            && confirmed.IsValid() && actual.IsValid()
            && (confirmed with { TargetName = "" }) == (actual with { TargetName = "" });
    }

    public static CoordinatedCompletionAction FromSettings(TaskConflictPolicySettings settings, AssistConfig? config = null)
    {
        var action = new CoordinatedCompletionAction(
            settings.RoomSwitch != null ? "switchRoom" : settings.Policy switch
            {
                TaskConflictPolicy.Resume => "resume", TaskConflictPolicy.RunSpecified => "runSpecified", _ => "stop"
            },
            settings.SpecifiedTaskType ?? "group", settings.SpecifiedTaskName?.Trim() ?? "", settings.SpecifiedTaskPriority,
            settings.SpecifiedStartMode ?? "default", settings.SpecifiedStartTaskId ?? "", settings.SpecifiedStartTaskName ?? "",
            settings.SpecifiedConfigRevision ?? "", settings.SpecifiedWorkflowId ?? "");
        if (settings.RoomSwitch is not { } room) return action;
        var targetName = room.TargetName;
        var matchesConfig = config == null || (config.ActiveRoomProfileId == room.SourceProfileId
            && room.MatchesSource(config) && room.MatchesTarget(config)
            && RoomServerAddress.Same(config.ServerUrl, room.SourceServerUrl) && config.EffectiveRoomCode == room.SourceRoomCode);
        if (config != null && matchesConfig)
            targetName = config.RoomProfiles.Single(p => p is not null && p.Id == room.TargetProfileId).Name;
        action = action with
        {
            TargetName = string.IsNullOrWhiteSpace(targetName) ? room.TargetRoomCode ?? "" : targetName,
            TargetRoomCode = room.TargetRoomCode ?? "", RunAfterSwitch = room.RunAfterSwitch
        };
        if (!room.RunAfterSwitch)
            action = action with { TaskType = "group", TaskName = "", Priority = 0, StartMode = "default",
                StartTaskId = "", StartTaskName = "", ConfigRevision = "", WorkflowId = "" };
        if (!matchesConfig || !room.IsValid || settings.Policy != TaskConflictPolicy.Stop
            || !RoomServerAddress.TryNormalize(room.SourceServerUrl, out var sourceServer)
            || !RoomServerAddress.TryNormalize(room.TargetServerUrl, out var targetServer)) return action;

        // 只对实际执行语义生成确定性修订；房间显示名变化不改变稳定关系，待命不消费隐藏的旧任务字段。
        // 摘要从不包含密码、登录票据或配置文件内容，只有任务本身已确认的目录修订。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            room.SourceProfileId, sourceServer, room.SourceRoomCode,
            room.TargetProfileId, targetServer, room.TargetRoomCode,
            action.RunAfterSwitch, action.TaskType, action.TaskName, action.Priority, action.StartMode,
            action.StartTaskId, action.StartTaskName, action.ConfigRevision, action.WorkflowId
        });
        return action with { ActionRevision = "v1:" + Convert.ToHexString(SHA256.HashData(bytes)) };
    }
}
