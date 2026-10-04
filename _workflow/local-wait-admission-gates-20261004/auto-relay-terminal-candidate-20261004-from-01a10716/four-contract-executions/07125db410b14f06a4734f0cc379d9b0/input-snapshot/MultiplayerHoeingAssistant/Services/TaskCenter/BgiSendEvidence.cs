using System;
using System.Collections.Generic;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// **[P8／§24.62]「可证实未发送」的证据载体（网络前失败的唯一判据来源）**。
///
/// 抛出本异常＝**本进程在任何字节写入线路之前**已确定失败（ext 通道未就绪／管道未连接／本地请求被拒），
/// 因此**不可能**在远端产生本笔受理事实或任何副作用。
///
/// 语义分离（P8 核心判据；不得互相代替）：
/// - **本类型** ⇒ 可证实未发送 ⇒ 远端不存在本笔受理事实 ⇒ 上层可判「确定拒绝」（§3.2a：无损拒绝类开重试窗口）；
/// - **其余一切异常／超时／无响应／回执缺失** ⇒ **已进入（或可能已进入）线路后失败** ⇒ 必须保留未决责任
///   （`Unknown` 停驻待对账，禁止据此判「未受理」、禁止换通道重发）。
///
/// **兼容**：继承 <see cref="InvalidOperationException"/>——既有 `catch (InvalidOperationException)` 降级路径
/// **逐字不变**（本类型只**增加**证据，不改变既有捕获语义）。
///
/// **纪律**：分类**只认本证据载体**（见 `BgiWorkflowExecutionBoundary.IsProvenNotSent`），
/// **不得**按异常消息文本或「异常类型看起来很弱」泛化推断——那会把「可能已发送」误判为未发送（双跑风险）。
/// </summary>
internal sealed class BgiNotSentException : InvalidOperationException
{
    /// <summary>证据码白名单（枚举值域，新增值须同时登记 §24.62 与分类夹具）。</summary>
    public const string ChannelNotReady = "channel_not_ready";
    public const string PipeNotConnected = "pipe_not_connected";
    public const string LocalRequestRejected = "local_request_rejected";

    /// <summary>
    /// **[会诊重要项处置] 构造即校验证据码**：未知码**直接拒绝**（fail-fast）——否则程序集内任何误用
    /// （例如 `new BgiNotSentException("write_failed", …)`）都会打开重试窗口，使「三码枚举」作为安全边界失效。
    /// </summary>
    public BgiNotSentException(string evidenceCode, string message) : base(message)
    {
        if (!IsKnownEvidenceCode(evidenceCode))
            throw new ArgumentException($"未登记的可证实未发送证据码：{evidenceCode ?? "<null>"}", nameof(evidenceCode));
        EvidenceCode = evidenceCode;
    }

    /// <summary>证据码（枚举值域之一；用于上层判据与留痕，不参与任何序列化）。</summary>
    public string EvidenceCode { get; }

    /// <summary>证据码是否为已登记白名单值（用于「新增码必须登记」的机器守卫）。</summary>
    public static bool IsKnownEvidenceCode(string? evidenceCode)
        => evidenceCode is ChannelNotReady or PipeNotConnected or LocalRequestRejected;

    /// <summary>
    /// **仅测试接缝**（**生产路径不可达**：仅夹具调用；生产抛点只有 `BgiExternalClient` 三处、全部用已登记码）：
    /// **绕过构造期白名单校验**地构造证据载体——用于证明**判据层的白名单校验独立生效**（第二层防线：
    /// 即使有人绕过构造期校验，未登记码也**不会**被判为「可证实未发送」）。
    /// </summary>
    internal static BgiNotSentException CreateBypassingWhitelistForTest(string? evidenceCode)
    {
        var ex = (BgiNotSentException)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BgiNotSentException));
        typeof(BgiNotSentException)
            .GetField("<EvidenceCode>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(ex, evidenceCode);
        return ex;
    }

    /// <summary>**枚举全表**（§17.4-A ⑥：安全敏感面冻结须交枚举）；**只读包装**（内容不可被改写）。</summary>
    public static readonly IReadOnlyList<string> AllEvidenceCodes =
        Array.AsReadOnly([ChannelNotReady, PipeNotConnected, LocalRequestRejected]);
}

/// <summary>只接受同调用响应内完整载荷回显、同连接和响应纪元的类型化建job前拒绝。</summary>
internal static class BgiServerRejectionEvidence
{
    internal static bool HasTypedDisposition(string? data)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(data ?? "null");
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("executionDisposition", out _);
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    internal static Models.ServerRejectionEvidence? Verify(BgiExternalResponse response, string operation,
        object payload, string expectedEpoch, string key, string fingerprint, BgiEpoch? currentEpoch)
    {
        if (response.Success || currentEpoch is null
            || $"{currentEpoch.ProcessId}:{currentEpoch.StartTicksUtc}" != expectedEpoch) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response.Data ?? "null");
            var r = doc.RootElement;
            if (r.GetProperty("executionDisposition").GetString() != "server_rejected_before_acceptance"
                || r.GetProperty("accepted").ValueKind != System.Text.Json.JsonValueKind.False
                || r.GetProperty("operation").GetString() != operation) return null;
            var epoch = r.GetProperty("bgiEpoch");
            if ($"{epoch.GetProperty("processId").GetInt32()}:{epoch.GetProperty("startTicksUtc").GetInt64()}" != expectedEpoch) return null;
            var expected = System.Text.Json.JsonSerializer.SerializeToElement(payload);
            if (expected.GetProperty("idempotencyKey").GetString() != key
                || !Equal(expected, r.GetProperty("request"))) return null;
            return new Models.ServerRejectionEvidence(expectedEpoch, key, fingerprint, operation);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }

    private static bool Equal(System.Text.Json.JsonElement expected, System.Text.Json.JsonElement actual, string? name = null)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        switch (expected.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var fields = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
                foreach (var field in actual.EnumerateObject()) if (!fields.TryAdd(field.Name, field.Value)) return false;
                var count = 0;
                foreach (var field in expected.EnumerateObject())
                {
                    count++;
                    if (!fields.TryGetValue(field.Name, out var value) || !Equal(field.Value, value, field.Name)) return false;
                }
                return count == fields.Count;
            case System.Text.Json.JsonValueKind.Array:
                if (expected.GetArrayLength() != actual.GetArrayLength()) return false;
                for (var i = 0; i < expected.GetArrayLength(); i++) if (!Equal(expected[i], actual[i])) return false;
                return true;
            case System.Text.Json.JsonValueKind.String:
                if (name == "expiresAtUtc")
                    return DateTimeOffset.TryParse(expected.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var e)
                        && DateTimeOffset.TryParse(actual.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var a) && e == a;
                return expected.GetString() == actual.GetString();
            case System.Text.Json.JsonValueKind.Number:
                return expected.GetRawText() == actual.GetRawText();
            default:
                return true;
        }
    }
}
