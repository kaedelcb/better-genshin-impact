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
    // 仅由建job/入队之前的拒绝站点调用。失败码本身不是零执行证据。
    internal static InstanceIpcEnvelope RejectBeforeAcceptance(InstanceIpcEnvelope request, InstanceIpcEnvelope rejection)
    {
        if (rejection.Success != false) throw new ArgumentException("需要明确拒绝响应");
        return new InstanceIpcEnvelope
        {
            RequestId = rejection.RequestId, Operation = rejection.Operation, Success = false,
            ErrorCode = rejection.ErrorCode, ErrorMessage = rejection.ErrorMessage,
            Data = JObject.FromObject(new
            {
                executionDisposition = "server_rejected_before_acceptance", accepted = false,
                operation = request.Operation,
                bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc },
                request = request.Data?.DeepClone(),
            }),
        };
    }

    public static InstanceIpcEnvelope? Validate(InstanceIpcEnvelope request, bool allowExpiredReplay = false)
    {
        try { return ValidateCore(request, allowExpiredReplay); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidCastException)
        { return InstanceIpcEnvelope.Failure(request, "invalid_request", ex.Message); }
    }

    private static InstanceIpcEnvelope? ValidateCore(InstanceIpcEnvelope request, bool allowExpiredReplay)
    {
        var data = request.Data;
        var strict = data?["executionContractVersion"] is { Type: not JTokenType.Null };
        var fence = data?["expectedStopVersion"];
        if (strict && fence is null or { Type: JTokenType.Null })
            return InstanceIpcEnvelope.Failure(request, "invalid_request", "严格执行请求要求 expectedStopVersion");
        if (fence is { Type: not JTokenType.Null })
        {
            if (fence.Type != JTokenType.Integer || fence.Value<long>() < 0)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "expectedStopVersion 必须是非负整数");
            if (data?["bgiEpoch"] is not JObject)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "停止围栏要求原 bgiEpoch");
            if (fence.Value<long>() != ExecutionScope.StopVersionNow)
                return InstanceIpcEnvelope.Failure(request, "manual_stop_fence", "用户停止后旧运行不得再次执行");
        }
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
            // R4.6 E1'/B1：前置/收尾操作强制严格合同 v1 + 流程身份 + 操作必需参数，全部在副作用前拒绝
        if (request.Operation is "ext.prerequisite.account" or "ext.prerequisite.redeemCode" or "ext.terminal.completionAction")
        {
            if (data?["executionContractVersion"]?.Value<int?>() != 1)
                return InstanceIpcEnvelope.Failure(request, "capability_required", "前置/收尾操作要求执行合同 v1");
            if (ReadIdentity(data) == null)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "前置/收尾操作要求流程身份（workflowRunId/nodeId/iteration）");
            if (request.Operation is "ext.prerequisite.account" or "ext.prerequisite.redeemCode"
                && string.IsNullOrWhiteSpace(InstanceIpcProtocol.GetStringOrNull(data, "uid")))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "前置操作要求 uid（空 UID 严格拒绝）");
            if (request.Operation == "ext.terminal.completionAction"
                && InstanceIpcProtocol.GetStringOrNull(data, "action") is not ("closeGame" or "closeSoftware" or "closeGameAndSoftware" or "shutdown"))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "收尾动作必须是 closeGame/closeSoftware/closeGameAndSoftware/shutdown 之一");
        }

        if (request.Operation is "ext.task.start" or "task.start")
            {
                if (ReadIdentity(data) == null || string.IsNullOrWhiteSpace(data?["expectedConfigRevision"]?.ToString()))
                    return InstanceIpcEnvelope.Failure(request, "invalid_request", "计划启动要求流程节点身份和配置版本");
                // R4.10 C2（ASTRA 阻断2）：严格合同流程提交的单项任务节点——task.single.native=false 期间一律
                // 副作用前显式拒绝（capability_required；前置合同字段有效时不得以修订/纪元类错误替代——
                // 前置字段无效的请求先返回对应错误，顺序合理不构成缺陷；不进入执行、不记成功）。
                // 非严格合同的单项选择（R3 配置面 PrepareExecutionAsync 路径，审计 T36/T37/T39）不受影响；
                // 能力开放时移除本闸（上方身份+修订校验保留）。
                if (data?["taskId"] is { Type: not JTokenType.Null })
                    return InstanceIpcEnvelope.Failure(request, "capability_required",
                        "单项任务原生执行能力未开放（task.single.native=false），响亮拒绝");
            }
        }
        // R4.6 E1'/B1：前置/收尾操作强制严格合同 v1 + 流程身份 + 操作必需参数，全部在副作用前拒绝
        if (request.Operation is "ext.prerequisite.account" or "ext.prerequisite.redeemCode" or "ext.terminal.completionAction")
        {
            if (data?["executionContractVersion"]?.Value<int?>() != 1)
                return InstanceIpcEnvelope.Failure(request, "capability_required", "前置/收尾操作要求执行合同 v1");
            if (ReadIdentity(data) == null)
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "前置/收尾操作要求流程身份（workflowRunId/nodeId/iteration）");
            if (request.Operation is "ext.prerequisite.account" or "ext.prerequisite.redeemCode"
                && string.IsNullOrWhiteSpace(InstanceIpcProtocol.GetStringOrNull(data, "uid")))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "前置操作要求 uid（空 UID 严格拒绝）");
            if (request.Operation == "ext.terminal.completionAction"
                && InstanceIpcProtocol.GetStringOrNull(data, "action") is not ("closeGame" or "closeSoftware" or "closeGameAndSoftware" or "shutdown"))
                return InstanceIpcEnvelope.Failure(request, "invalid_request", "收尾动作必须是 closeGame/closeSoftware/closeGameAndSoftware/shutdown 之一");
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

    /// <summary>R4.6 D10/E4'：读取收尾抑制标记（仅严格合同调用方承认；缺省 false 行为不变）。</summary>
    public static long? ReadExpectedStopVersion(JObject? data)
        => data?["expectedStopVersion"] is { Type: JTokenType.Integer } value ? value.Value<long>() : null;

    public static bool ReadSuppressConfigCompletionAction(Newtonsoft.Json.Linq.JObject? data)
        => data?["suppressConfigCompletionAction"]?.ToObject<bool?>() == true;

    /// <summary>R4.6 E2-9：读取期望 UID（执行权取得后复验；null=不校验）。</summary>
    public static string? ReadExpectedUid(Newtonsoft.Json.Linq.JObject? data)
        => InstanceIpcProtocol.GetStringOrNull(data, "expectedUid");

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
        return new(run, node, iteration.Value, data?["taskId"]?.ToString(), data?["expectedConfigRevision"]?.ToString(),
            data?["occurrence"]?.Value<int?>(), data?["attempt"]?.Value<int?>()); // R4.6 B1：出现/尝试号 add-only
    }

    private static JToken Canonical(JToken value) => value switch
    {
        JObject obj => new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value)))),
        JArray array => new JArray(array.Select(Canonical)),
        _ => value.DeepClone()
    };
}
