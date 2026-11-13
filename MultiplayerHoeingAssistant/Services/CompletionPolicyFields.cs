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
            "stop" => TaskConflictPolicy.Stop,
            "runSpecified" => TaskConflictPolicy.RunSpecified,
            _ => TaskConflictPolicy.Resume
        },
        SpecifiedTaskType = config?.OnlineHoeingSpecifiedTaskType ?? "group",
        SpecifiedTaskName = config?.OnlineHoeingSpecifiedTaskName ?? "",
        SpecifiedTaskPriority = config?.OnlineHoeingSpecifiedTaskPriority ?? 0,
        SpecifiedStartMode = config?.OnlineHoeingSpecifiedStartMode ?? "default",
        SpecifiedStartTaskId = config?.OnlineHoeingSpecifiedStartTaskId ?? "",
        SpecifiedStartTaskName = config?.OnlineHoeingSpecifiedStartTaskName ?? "",
        SpecifiedConfigRevision = config?.OnlineHoeingSpecifiedConfigRevision ?? "",
        SpecifiedWorkflowId = config?.OnlineHoeingSpecifiedWorkflowId ?? ""
    };

    internal static Dictionary<string, object> Parameters(TaskConflictPolicySettings settings) => new()
    {
        ["policy"] = settings.Policy switch
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
        ["completionStartVersion"] = CompletionTargetCatalogService.Version
    };

    internal static void Apply(AssistConfig config, TaskConflictPolicySettings settings)
    {
        config.OnlineHoeingCompletionPolicy = settings.Policy switch
        {
            TaskConflictPolicy.Stop => "stop",
            TaskConflictPolicy.RunSpecified => "runSpecified",
            _ => "resume"
        };
        config.OnlineHoeingSpecifiedTaskType = settings.SpecifiedTaskType;
        config.OnlineHoeingSpecifiedTaskName = settings.SpecifiedTaskName;
        config.OnlineHoeingSpecifiedTaskPriority = settings.SpecifiedTaskPriority;
        config.OnlineHoeingSpecifiedStartMode = settings.SpecifiedStartMode;
        config.OnlineHoeingSpecifiedStartTaskId = settings.SpecifiedStartTaskId;
        config.OnlineHoeingSpecifiedStartTaskName = settings.SpecifiedStartTaskName;
        config.OnlineHoeingSpecifiedConfigRevision = settings.SpecifiedConfigRevision;
        config.OnlineHoeingSpecifiedWorkflowId = settings.SpecifiedWorkflowId;
    }

    /// <summary>Save 内部容错不抛异常，因此读回成功前不更新运行中的配置或宣称成功。</summary>
    internal static void SaveChecked(AssistConfigManager manager, AssistConfig current, TaskConflictPolicySettings settings)
        => SaveChecked(manager.Save, manager.ReadRawJson, current, settings);

    internal static void SaveChecked(Action<AssistConfig> save, Func<string> readRawJson,
        AssistConfig current, TaskConflictPolicySettings settings)
    {
        var copy = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(current))
            ?? throw new InvalidDataException("无法创建完成动作配置副本。");
        Apply(copy, settings);
        save(copy);
        var saved = JsonSerializer.Deserialize<AssistConfig>(readRawJson(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("无法读回完成动作配置。");
        var actual = Parameters(Snapshot(saved));
        if (Parameters(settings).Any(pair => !actual.TryGetValue(pair.Key, out var value) || !Equals(value, pair.Value)))
            throw new IOException("完成动作写入结果未确认，请重新读取核对。");
        Apply(current, settings);
    }
}
