using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>完成动作的同一组字段用于快照、持久化、远程回执及监控端镜像。</summary>
internal static class CompletionPolicyFields
{
    internal static TaskConflictPolicySettings Snapshot(AssistConfig? config) => new()
    {
        Policy = config?.OnlineHoeingCompletionPolicy switch
        {
            "stop" or "switchRoom" => TaskConflictPolicy.Stop,
            "runSpecified" => TaskConflictPolicy.RunSpecified,
            _ => TaskConflictPolicy.Resume
        },
        // 显式切房却缺少目标是无效设置，不得被解释成普通停止或恢复。
        RoomSwitch = config?.OnlineHoeingCompletionPolicy == "switchRoom"
            ? config.OnlineHoeingRoomSwitchCompletion ?? new RoomSwitchCompletion() : null,
        SpecifiedTaskType = config?.OnlineHoeingSpecifiedTaskType ?? "group",
        SpecifiedTaskName = config?.OnlineHoeingSpecifiedTaskName ?? "",
        SpecifiedTaskPriority = config?.OnlineHoeingSpecifiedTaskPriority ?? 0,
        SpecifiedStartMode = config?.OnlineHoeingSpecifiedStartMode ?? "default",
        SpecifiedStartTaskId = config?.OnlineHoeingSpecifiedStartTaskId ?? "",
        SpecifiedStartTaskName = config?.OnlineHoeingSpecifiedStartTaskName ?? "",
        SpecifiedConfigRevision = config?.OnlineHoeingSpecifiedConfigRevision ?? "",
        SpecifiedWorkflowId = config?.OnlineHoeingSpecifiedWorkflowId ?? ""
    };

    internal static TaskConflictPolicySettings SnapshotForProfile(AssistConfig config, string profileId)
    {
        var profile = GetProfile(config, profileId);
        return profile.Id == config.ActiveRoomProfileId ? Snapshot(config) : SnapshotProfileFields(profile);
    }

    private static AssistantRoomProfile GetProfile(AssistConfig config, string profileId)
    {
        var matches = config.RoomProfiles.Where(p => p is not null && p.Id == profileId).ToArray();
        return !string.IsNullOrEmpty(profileId) && matches.Length == 1 ? matches[0]
            : throw new InvalidOperationException("无法唯一确认要编辑的房间档案，请重新选择。");
    }

    private static TaskConflictPolicySettings SnapshotProfileFields(AssistantRoomProfile profile)
    {
        var copy = new AssistConfig();
        profile.ApplyCompletionTo(copy);
        return Snapshot(copy);
    }

    internal static Dictionary<string, object> Parameters(TaskConflictPolicySettings settings) => new()
    {
        ["policy"] = settings.RoomSwitch != null ? "switchRoom" : settings.Policy switch
        {
            TaskConflictPolicy.Stop => "stop",
            TaskConflictPolicy.RunSpecified => "runSpecified",
            _ => "resume"
        },
        ["specifiedType"] = settings.SpecifiedTaskType,
        ["specifiedName"] = settings.SpecifiedTaskName,
        ["specifiedPriority"] = settings.SpecifiedTaskPriority.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["specifiedStartMode"] = settings.SpecifiedStartMode,
        ["specifiedStartTaskId"] = settings.SpecifiedStartTaskId,
        ["specifiedStartTaskName"] = settings.SpecifiedStartTaskName,
        ["specifiedConfigRevision"] = settings.SpecifiedConfigRevision,
        ["specifiedWorkflowId"] = settings.SpecifiedWorkflowId,
        ["roomSwitchSourceProfileId"] = settings.RoomSwitch?.SourceProfileId ?? "",
        ["roomSwitchSourceServerUrl"] = settings.RoomSwitch?.SourceServerUrl ?? "",
        ["roomSwitchSourceRoomCode"] = settings.RoomSwitch?.SourceRoomCode ?? "",
        ["roomSwitchTargetProfileId"] = settings.RoomSwitch?.TargetProfileId ?? "",
        ["roomSwitchTargetServerUrl"] = settings.RoomSwitch?.TargetServerUrl ?? "",
        ["roomSwitchTargetRoomCode"] = settings.RoomSwitch?.TargetRoomCode ?? "",
        ["roomSwitchTargetName"] = settings.RoomSwitch?.TargetName ?? "",
        ["roomSwitchRunAfter"] = settings.RoomSwitch?.RunAfterSwitch == true ? "true" : "false",
        ["completionStartVersion"] = CompletionTargetCatalogService.Version
    };

    /// <summary>同版远程读取使用标量字段；未完整收到的切房动作仍是无效的切房动作。</summary>
    internal static RoomSwitchCompletion? RoomSwitchFromParameters(Func<string, string?> read)
    {
        if (read("policy") != "switchRoom") return null;
        var after = read("roomSwitchRunAfter");
        return new RoomSwitchCompletion(
            after is "true" or "false" ? read("roomSwitchSourceProfileId") ?? "" : "",
            read("roomSwitchSourceServerUrl") ?? "", read("roomSwitchSourceRoomCode") ?? "",
            read("roomSwitchTargetProfileId") ?? "", read("roomSwitchTargetServerUrl") ?? "",
            read("roomSwitchTargetRoomCode") ?? "", read("roomSwitchTargetName") ?? "", after == "true");
    }

    internal static void Apply(AssistConfig config, TaskConflictPolicySettings settings)
    {
        // 旧恢复/停止动作保留其历史隐藏起点字段；离开切房或改成待命则不留下隐式后继任务。
        if (!settings.HasSpecifiedTask && (settings.RoomSwitch != null || config.OnlineHoeingCompletionPolicy == "switchRoom"))
            settings = new TaskConflictPolicySettings { Policy = settings.Policy, RoomSwitch = settings.RoomSwitch };
        config.OnlineHoeingCompletionPolicy = settings.RoomSwitch != null ? "switchRoom" : settings.Policy switch
        {
            TaskConflictPolicy.Stop => "stop",
            TaskConflictPolicy.RunSpecified => "runSpecified",
            _ => "resume"
        };
        config.OnlineHoeingRoomSwitchCompletion = settings.RoomSwitch;
        config.OnlineHoeingSpecifiedTaskType = settings.SpecifiedTaskType;
        config.OnlineHoeingSpecifiedTaskName = settings.SpecifiedTaskName;
        config.OnlineHoeingSpecifiedTaskPriority = settings.SpecifiedTaskPriority;
        config.OnlineHoeingSpecifiedStartMode = settings.SpecifiedStartMode;
        config.OnlineHoeingSpecifiedStartTaskId = settings.SpecifiedStartTaskId;
        config.OnlineHoeingSpecifiedStartTaskName = settings.SpecifiedStartTaskName;
        config.OnlineHoeingSpecifiedConfigRevision = settings.SpecifiedConfigRevision;
        config.OnlineHoeingSpecifiedWorkflowId = settings.SpecifiedWorkflowId;
        config.ActiveRoomProfile?.CaptureCompletion(config);
    }

    internal static string? ValidateRoomSwitch(AssistConfig config, string profileId, TaskConflictPolicySettings settings)
    {
        if (settings.RoomSwitch is not { } room) return null;
        if (settings.Policy != TaskConflictPolicy.Stop || !room.IsValid || room.SourceProfileId != profileId
            || !room.MatchesSource(config) || !room.MatchesTarget(config)
            || profileId == config.ActiveRoomProfileId
                && (!RoomServerAddress.Same(config.ServerUrl, room.SourceServerUrl) || config.EffectiveRoomCode != room.SourceRoomCode))
            return "切房的源或目标房间已删除、改址或无效，请重新选择并确认。";
        if (!room.RunAfterSwitch) return null;
        return settings.SpecifiedStartMode is not ("beginning" or "node")
            ? "切房后指定任务必须明确选择从头或从某个任务开始。"
            : CompletionTargetCatalogService.ValidateShape(settings.PostSwitchTask());
    }

    /// <summary>只修改指定档案的完成动作；活动档案同步运行字段，不触碰其他房间。</summary>
    internal static void ApplyForProfile(AssistConfig config, string profileId, TaskConflictPolicySettings settings)
    {
        var profile = GetProfile(config, profileId);
        if (ValidateRoomSwitch(config, profileId, settings) is { } error)
            throw new InvalidOperationException(error);
        if (profile.Id == config.ActiveRoomProfileId) Apply(config, settings);
        else
        {
            var copy = new AssistConfig();
            profile.ApplyCompletionTo(copy);
            Apply(copy, settings);
            profile.CaptureCompletion(copy);
        }
    }

    /// <summary>Save 内部容错不抛异常，因此读回成功前不更新运行中的配置或宣称成功。</summary>
    internal static void SaveChecked(AssistConfigManager manager, AssistConfig current, TaskConflictPolicySettings settings)
        => SaveChecked(candidate =>
        {
            if (!manager.TrySave(candidate)) throw new IOException("完成动作保存失败，原运行配置未修改。");
        }, manager.ReadRawJson, current, settings);

    internal static void SaveChecked(Action<AssistConfig> save, Func<string> readRawJson,
        AssistConfig current, TaskConflictPolicySettings settings)
    {
        if (ValidateRoomSwitch(current, current.ActiveRoomProfileId, settings) is { } error)
            throw new InvalidOperationException(error);
        var copy = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(current))
            ?? throw new InvalidDataException("无法创建完成动作配置副本。");
        Apply(copy, settings);
        var canonical = Snapshot(copy);
        var expected = Parameters(canonical);
        var expectedRoom = copy.OnlineHoeingRoomSwitchCompletion;
        var activeProfileId = current.ActiveRoomProfile?.Id;
        save(copy);
        var saved = JsonSerializer.Deserialize<AssistConfig>(readRawJson(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("无法读回完成动作配置。");
        if (!Matches(expected, Snapshot(saved)) || saved.OnlineHoeingRoomSwitchCompletion != expectedRoom
            || activeProfileId != null && (saved.ActiveRoomProfileId != activeProfileId
                || saved.ActiveRoomProfile is not { } profile || profile.OnlineHoeingRoomSwitchCompletion != expectedRoom
                || !Matches(expected, SnapshotProfileFields(profile)))
            || ValidateRoomSwitch(saved, saved.ActiveRoomProfileId, canonical) is not null
            || current.ActiveRoomProfile?.Id != activeProfileId
            || ValidateRoomSwitch(current, current.ActiveRoomProfileId, canonical) is not null)
            throw new IOException("完成动作写入结果未确认，请重新读取核对。");
        Apply(current, canonical);
    }

    private static bool Matches(Dictionary<string, object> expected, TaskConflictPolicySettings settings)
    {
        var actual = Parameters(settings);
        return expected.All(pair => actual.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value));
    }
}
