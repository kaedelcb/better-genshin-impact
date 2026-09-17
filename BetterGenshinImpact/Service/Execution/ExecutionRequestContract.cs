using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>Additive v2 contract. Legacy requests may omit fencing; supplied constraints are never ignored.</summary>
internal static class ExecutionRequestContract
{
    public static InstanceIpcEnvelope? Validate(InstanceIpcEnvelope request, bool allowExpiredReplay = false)
    {
        try { return ValidateCore(request, allowExpiredReplay); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidCastException)
        { return InstanceIpcEnvelope.Failure(request, "invalid_request", ex.Message); }
    }

    private static InstanceIpcEnvelope? ValidateCore(InstanceIpcEnvelope request, bool allowExpiredReplay)
    {
        var data = request.Data;
        if (data?["bgiEpoch"] is { Type: not JTokenType.Null } epoch
            && (epoch.Type != JTokenType.Object
                || epoch["processId"]?.Value<int?>() != JobRegistry.CurrentEpoch.ProcessId
                || epoch["startTicksUtc"]?.Value<long?>() != JobRegistry.CurrentEpoch.StartTicksUtc))
            return InstanceIpcEnvelope.Failure(request, "stale_epoch", "请求目标不是当前 BGI 进程纪元");
        if (data?["expiresAtUtc"] is { Type: not JTokenType.Null } expiry)
        {
            DateTimeOffset deadline;
            // Json.NET parses ISO timestamps into Date tokens. ToString() loses fractional
            // seconds/offset on those tokens and can incorrectly expire an accepted request.
            if (expiry.Type == JTokenType.Date) deadline = expiry.ToObject<DateTimeOffset>();
            else if (expiry.Type != JTokenType.String || !DateTimeOffset.TryParse(expiry.Value<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out deadline))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "expiresAtUtc 格式无效");
            if (!allowExpiredReplay && deadline <= DateTimeOffset.UtcNow)
                return InstanceIpcEnvelope.Failure(request, "request_expired", "请求已过期，未执行");
        }
        if (data?["executionContractVersion"] is { Type: not JTokenType.Null } version)
        {
            if (version.Value<int>() != 1)
                return InstanceIpcEnvelope.Failure(request, "capability_required", "不支持的执行合同版本");
            if (!request.Operation.StartsWith("ext.", StringComparison.Ordinal))
                return InstanceIpcEnvelope.Failure(request, "capability_required", "执行合同 v1 必须使用 ext 通道，不允许降级至旧控制入口");
            if (data?["bgiEpoch"] is not JObject || string.IsNullOrWhiteSpace(data?["idempotencyKey"]?.ToString()))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "执行合同要求 bgiEpoch 和 idempotencyKey");
            if (data?["expiresAtUtc"] is null or { Type: JTokenType.Null })
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "执行合同要求请求有效期 expiresAtUtc");
            if (request.Operation is "ext.task.start" or "task.start")
            {
                if (ReadIdentity(data) == null || string.IsNullOrWhiteSpace(data?["expectedConfigRevision"]?.ToString()))
                    return InstanceIpcEnvelope.Failure(request, "invalid_request", "计划启动要求流程节点身份和配置版本");
            }
        }
        if (request.Operation is "ext.task.start" or "task.start")
        {
            var group = InstanceIpcProtocol.GetStringOrNull(data, "groupName");
            var config = InstanceIpcProtocol.GetStringOrNull(data, "configName");
            if ((group == null) == (config == null))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "必须且只能指定一个 groupName/configName");
            _ = ReadIdentity(data);
            if (data?["taskId"] is { Type: not JTokenType.Null }
                && string.IsNullOrWhiteSpace(data?["expectedConfigRevision"]?.ToString()))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "单项启动要求配置版本");
        }
        return null;
    }

    public static string Fingerprint(InstanceIpcEnvelope request)
    {
        var data = (JObject?)request.Data?.DeepClone() ?? new JObject();
        data.Remove("idempotencyKey");
        var json = request.Operation + "\n" + Canonical(data).ToString(Formatting.None);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static JobExecutionIdentity? ReadIdentity(JObject? data)
    {
        var raw = InstanceIpcProtocol.GetStringOrNull(data, "workflowRunId");
        var node = InstanceIpcProtocol.GetStringOrNull(data, "nodeId");
        var iteration = data?["iteration"]?.Value<int?>();
        if (raw == null && node == null && iteration == null) return null;
        if (!Guid.TryParse(raw, out var run) || run == Guid.Empty || string.IsNullOrWhiteSpace(node)
            || node.Length > 256 || iteration is null or < 0)
            throw new ArgumentException("流程身份要求有效 workflowRunId、nodeId 和非负 iteration");
        return new(run, node, iteration.Value, data?["taskId"]?.ToString(), data?["expectedConfigRevision"]?.ToString());
    }

    private static JToken Canonical(JToken value) => value switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value)))),
        JArray array => new JArray(array.Select(Canonical)),
        _ => value.DeepClone()
    };
}
