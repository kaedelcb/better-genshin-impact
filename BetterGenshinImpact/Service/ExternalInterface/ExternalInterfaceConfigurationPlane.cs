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
        if (!task.Enabled) throw new InvalidOperationException("task_disabled");
        // R3：原生一条龙单项——数字索引语义退役，单项由执行段按 Descriptor.TaskId（GUID）过滤（R3.2 核心提前落地）
        if (task.LegacyIndex == null) return (snapshot, null);
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
                    startTaskId = TaskConfigurationContract.ReadStartTaskId(snapshot, oneDragon),
                    tasks = snapshot.Tasks.Select(t => new { taskId = t.TaskId, name = t.Name, enabled = t.Enabled,
                        legacyIndex = t.LegacyIndex, schema = t.Schema, singleExecutionSupported = t.LegacyIndex != null || t.Schema == "native" }),
                    bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc } });
            }
            var id = InstanceIpcProtocol.GetStringOrNull(request.Data, "taskId");
            var revision = InstanceIpcProtocol.GetStringOrNull(request.Data, "expectedConfigRevision");
            if (request.Operation == ExternalInterfaceOperations.ConfigApplyTaskLayout)
            {
                if (revision == null || request.Data?["tasks"] is not JArray tasks
                    || request.Data["startTaskId"] is { Type: not (JTokenType.String or JTokenType.Null) })
                    return InstanceIpcEnvelope.Failure(request, "invalid_request", "任务列表保存要求修订和完整任务列表");
                var layout = new System.Collections.Generic.List<TaskConfigurationContract.LayoutTask>();
                foreach (var task in tasks)
                {
                    if (task is not JObject entry || entry["taskId"]?.Type != JTokenType.String
                        || entry["enabled"]?.Type != JTokenType.Boolean)
                        return InstanceIpcEnvelope.Failure(request, "invalid_request", "任务列表包含无效条目");
                    layout.Add(new(entry["taskId"]!.Value<string>()!, entry["enabled"]!.Value<bool>()));
                }
                var appliedLayout = await store.ApplyLayoutAsync(name, oneDragon, layout,
                    InstanceIpcProtocol.GetStringOrNull(request.Data, "startTaskId"), revision,
                    InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"), beforeCommit: () =>
                    {
                        if (ExecutionRequestContract.Validate(request) is { } expired)
                            throw new InvalidOperationException(expired.ErrorCode);
                    });
                return InstanceIpcEnvelope.Response(request, new { status = "config_applied",
                    configRevision = appliedLayout.Revision,
                    startTaskId = TaskConfigurationContract.ReadStartTaskId(appliedLayout, oneDragon) });
            }
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
        catch (InvalidDataException ex)
        {
            // 坏配置文件只拒绝本次请求；异常若逃出请求处理会关闭整条 ext 管道，助手重连后又会读到它，形成断线循环。
            return InstanceIpcEnvelope.Failure(request, "configuration_failed", DescribeInvalidConfiguration(request, ex));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or Newtonsoft.Json.JsonException)
        {
            var code = ex.Message is "configuration_changed" or "takeover_conflict" or "task_not_found"
                or "task_disabled_or_missing" or "invalid_task_layout" or "request_expired" or "stale_epoch"
                ? ex.Message : "configuration_failed";
            return InstanceIpcEnvelope.Failure(request, code, ex.Message);
        }
    }

    /// <summary>把内部校验码译成带配置名和处理办法的说明；已是中文说明的原文保留。</summary>
    private static string DescribeInvalidConfiguration(InstanceIpcEnvelope request, InvalidDataException ex)
    {
        var group = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
        var config = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
        var label = config != null ? $"一条龙配置「{config}」" : $"配置组「{group}」";
        var folder = config != null ? "User\\OneDragon" : "User\\ScriptGroup";
        var reason = ex.Message switch
        {
            "invalid_project_list" => "缺少 projects 列表，不是有效的配置组文件",
            "invalid_project" => "projects 列表中含有无效条目",
            "invalid_task_list" => "缺少任务列表",
            "unsupported_task_schema" => "任务格式不受支持",
            var other => other,
        };
        return $"{label}无法读取：{reason}。请在 BGI 的 {folder} 目录中修复该文件，或先移走备份后再重试。";
    }
}
