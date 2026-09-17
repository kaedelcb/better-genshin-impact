using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

internal static class ExternalInterfaceConfigurationPlane
{
    internal static async Task<(TaskConfigurationContract.Snapshot? Snapshot, int? SingleIndex)> PrepareExecutionAsync(
        InstanceIpcEnvelope request, TaskConfigurationContract? store = null)
    {
        var id = InstanceIpcProtocol.GetStringOrNull(request.Data, "taskId");
        var expected = InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision");
        if (id == null && expected == null) return (null, null);
        if (id != null && expected == null) throw new InvalidOperationException("configuration_revision_required");
        var group = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
        var config = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
        if ((group == null) == (config == null)) throw new ArgumentException("ambiguous_configuration");
        var snapshot = await (store ?? TaskConfigurationContract.Default).ReadAsync(config ?? group!, config != null);
        if (snapshot.Revision != expected) throw new InvalidOperationException("configuration_changed");
        if (id == null) return (snapshot, null);
        var task = snapshot.Tasks.SingleOrDefault(t => t.TaskId == id) ?? throw new InvalidOperationException("task_not_found");
        if (task.LegacyIndex == null) throw new InvalidOperationException("native_single_execution_not_supported");
        if (!task.Enabled) throw new InvalidOperationException("task_disabled");
        return (snapshot, task.LegacyIndex);
    }

    internal static async Task<InstanceIpcEnvelope> DispatchAsync(InstanceIpcEnvelope request,
        TaskConfigurationContract? store = null)
    {
        store ??= TaskConfigurationContract.Default;
        try
        {
            if (ExecutionRequestContract.Validate(request) is { } rejected) return rejected;
            var group = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
            var config = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
            if ((group == null) == (config == null))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "必须且只能指定一个 groupName/configName");
            var oneDragon = config != null;
            var name = config ?? group!;
            if (request.Operation == ExternalInterfaceOperations.ConfigDescribe)
            {
                var snapshot = await store.ReadAsync(name, oneDragon);
                return InstanceIpcEnvelope.Response(request, new { configRevision = snapshot.Revision,
                    tasks = snapshot.Tasks.Select(t => new { taskId = t.TaskId, name = t.Name, enabled = t.Enabled,
                        legacyIndex = t.LegacyIndex, schema = t.Schema, singleExecutionSupported = t.LegacyIndex != null }),
                    bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc } });
            }
            var id = InstanceIpcProtocol.GetStringOrNull(request.Data, "taskId");
            var revision = InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision");
            if (request.Operation == ExternalInterfaceOperations.ConfigApplyTaskState && (id == null || revision == null))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "配置应用要求 taskId 和 expectedConfigRevision");
            if (request.Data?["enabled"]?.Type != Newtonsoft.Json.Linq.JTokenType.Boolean)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "enabled 必须为布尔值");
            var applied = await store.ApplyEnabledAsync(name, oneDragon, id, request.Data?["taskIndex"]?.Value<int?>(),
                request.Data!["enabled"]!.Value<bool>(), revision,
                InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"), beforeCommit: () =>
                {
                    if (ExecutionRequestContract.Validate(request) is { } expired)
                        throw new InvalidOperationException(expired.ErrorCode);
                });
            return InstanceIpcEnvelope.Response(request, new { status = "config_applied", configRevision = applied.Revision,
                commandId = request.Data?["commandId"]?.ToString(), taskId = id,
                bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc } });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Newtonsoft.Json.JsonException)
        {
            var code = ex.Message is "configuration_changed" or "takeover_conflict" or "task_not_found" or "request_expired" or "stale_epoch" ? ex.Message : "configuration_failed";
            return InstanceIpcEnvelope.Failure(request, code, ex.Message);
        }
    }
}
