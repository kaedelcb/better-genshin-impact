using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    internal sealed record CompletionStart(string Name, bool OneDragon, string Revision, int Index, string? TaskId);
    internal sealed class CompletionStartException(string code, string message) : Exception(message)
    { internal string Code { get; } = code; }

    // Resolve stable identities against the execution endpoint's current, revision-bound directory.
    // Zero/null is deliberately reserved for legacy default semantics (which may use a saved cursor).
    internal static CompletionStart ResolveCompletionStart(TaskConflictPolicySettings policy, JsonElement description)
    {
        var name = policy.SpecifiedTaskName?.Trim() ?? "";
        if (policy.SpecifiedTaskType is not ("group" or "onedragon") || name.Length == 0)
            throw new CompletionStartException("specified_task_missing", "指定配置不存在或类型无效");
        var mode = policy.SpecifiedStartMode ?? "default";
        if (mode is not ("default" or "beginning" or "node"))
            throw new CompletionStartException("invalid_request", "指定起点模式无效");
        if (!description.TryGetProperty("configRevision", out var revisionValue)
            || revisionValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(revisionValue.GetString())
            || !description.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            throw new CompletionStartException("state_unavailable", "配置目录或修订尚未确认");
        var revision = revisionValue.GetString()!;
        if (mode != "default" && string.IsNullOrWhiteSpace(policy.SpecifiedConfigRevision)
            || !string.IsNullOrWhiteSpace(policy.SpecifiedConfigRevision) && policy.SpecifiedConfigRevision != revision)
            throw new CompletionStartException("configuration_revision_mismatch", "指定配置修订已改变，请重新选择起点");
        var entries = tasks.EnumerateArray().ToArray();
        static bool Enabled(JsonElement entry) => entry.TryGetProperty("enabled", out var value) && value.ValueKind == JsonValueKind.True;
        if (!entries.Any(Enabled)) throw new CompletionStartException("specified_task_missing", "指定配置没有启用任务");
        var oneDragon = policy.SpecifiedTaskType == "onedragon";
        if (mode == "default") return new(name, oneDragon, revision, 0, null);
        var selected = mode == "beginning" ? entries.First(Enabled) : entries.FirstOrDefault(entry =>
            entry.TryGetProperty("taskId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == policy.SpecifiedStartTaskId);
        var idValue = selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty("taskId", out var taskId)
            && taskId.ValueKind == JsonValueKind.String ? taskId.GetString() : null;
        if (string.IsNullOrWhiteSpace(idValue) || entries.Count(entry => entry.TryGetProperty("taskId", out var id)
            && id.ValueKind == JsonValueKind.String && id.GetString() == idValue) != 1 || !Enabled(selected))
            throw new CompletionStartException("specified_task_missing", "指定起点已不存在、已禁用或身份不唯一");
        if (oneDragon) return new(name, true, revision, 0, idValue);
        if (!selected.TryGetProperty("legacyIndex", out var indexValue) || indexValue.ValueKind != JsonValueKind.Number
            || !indexValue.TryGetInt32(out var index) || index <= 0
            || entries.Count(entry => entry.TryGetProperty("legacyIndex", out var other)
                && other.ValueKind == JsonValueKind.Number && other.TryGetInt32(out var n) && n == index) != 1)
            throw new CompletionStartException("specified_task_missing", "指定配置组起点位置无效或不唯一");
        return new(name, false, revision, index, null);
    }

    private async Task<CompletionStart> DescribeCompletionStartAsync(TaskConflictPolicySettings policy,
        BgiExternalClient ext, BgiEpoch epoch, CancellationToken ct)
    {
        if (policy.SpecifiedTaskType is not ("group" or "onedragon"))
            throw new CompletionStartException("invalid_request", "此入口只接受配置组或一条龙");
        if (policy.SpecifiedTaskType == "group" && policy.SpecifiedStartMode is ("beginning" or "node")
            && !ext.HasCapability("task.start.explicitGroupStart.v1"))
            throw new CompletionStartException("explicit_group_start_unsupported", "BGI 尚不支持配置组明确起点，请更新 BGI 后执行");
        var oneDragon = policy.SpecifiedTaskType == "onedragon";
        var described = await ext.SendCommandAsync("ext.config.describe", new
        { groupName = oneDragon ? null : policy.SpecifiedTaskName, configName = oneDragon ? policy.SpecifiedTaskName : null },
            cancellationToken: ct).ConfigureAwait(false);
        if (described is not { Success: true, Data: not null })
            throw new CompletionStartException(described.ErrorCode is "configuration_not_found" or "file_not_found"
                ? "specified_task_missing" : "state_unavailable", described.ErrorMessage ?? "完成动作配置暂不可读");
        if (!TryParseEpoch(described.Data, out var pid, out var ticks) || pid != epoch.ProcessId || ticks != epoch.StartTicksUtc)
            throw new CompletionStartException("stale_epoch", "配置目录不属于本批原 BGI");
        using var document = JsonDocument.Parse(described.Data);
        return ResolveCompletionStart(policy, document.RootElement);
    }

    internal static CompletionStart ReadFrozenCompletionStart(IReadOnlyDictionary<string, object?> frozen)
    {
        static string Text(IReadOnlyDictionary<string, object?> data, string key) => data.GetValueOrDefault(key) is string text ? text : "";
        var index = 0;
        if (frozen.TryGetValue("startFromIndex", out var raw))
        {
            // Recovery materializes JSON integers as Int64. Never cast the boxed value to Int32.
            var number = raw switch { int i => (long)i, long l => l,
                JsonElement value when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) => n, _ => -1 };
            if (number < 0 || number > int.MaxValue) throw new CompletionStartException("invalid_request", "冻结起点位置无效");
            index = (int)number;
        }
        var name = Text(frozen, "name"); var revision = Text(frozen, "revision");
        if (name.Length == 0 || revision.Length == 0 || frozen.GetValueOrDefault("oneDragon") is not bool oneDragon)
            throw new CompletionStartException("invalid_request", "冻结完成动作身份不完整");
        var taskId = Text(frozen, "startFromTaskId");
        return new(name, oneDragon, revision, index, taskId.Length == 0 ? null : taskId);
    }

    internal static Dictionary<string, object?> FreezeCompletionStart(CompletionStart start) => new()
    {
        ["name"] = start.Name, ["oneDragon"] = start.OneDragon, ["revision"] = start.Revision,
        ["startFromIndex"] = start.Index, ["startFromTaskId"] = start.TaskId,
    };
}
