using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record ManualStopFence(string Epoch, long Version, long? LastManualStopTimestamp, long Frequency);
internal sealed class StopAuthorityUnknownException(string message) : Exception(message);

internal static class WorkflowStopAuthority
{
    internal const string Capability = "execution.manualStopFence.v1";
    internal const string Operation = "ext.execution.manualStopFence";
    internal static string? Epoch(BgiEpoch? epoch) => epoch is null ? null : $"{epoch.ProcessId}:{epoch.StartTicksUtc}";

    internal static async Task<ManualStopFence?> QueryAsync(IBgiExecutionPort port, CancellationToken ct)
    {
        if (!port.IsReady || !port.HasCapability(Capability)) return null;
        var before = Epoch(port.ServerEpoch);
        var response = await port.SendCommandAsync(Operation, null, ct).ConfigureAwait(false);
        return Parse(response, before, Epoch(port.ServerEpoch));
    }

    internal static ManualStopFence? Parse(BgiExternalResponse response, string? before, string? after)
    {
        if (!response.Success || response.Data is null || before is null || before != after) return null;
        try
        {
            using var doc = JsonDocument.Parse(response.Data);
            var data = doc.RootElement;
            var e = data.GetProperty("bgiEpoch");
            var epoch = $"{e.GetProperty("processId").GetInt32()}:{e.GetProperty("startTicksUtc").GetInt64()}";
            var version = data.GetProperty("stopVersion").GetInt64();
            var frequency = data.GetProperty("monotonicFrequency").GetInt64();
            var timestamp = data.GetProperty("lastManualStopTimestamp");
            long? last = timestamp.ValueKind == JsonValueKind.Null ? null : timestamp.GetInt64();
            if (epoch != before || version < 0 || frequency != Stopwatch.Frequency
                || (version > 0 && last is null) || (version == 0 && last is not null)) return null;
            return new(epoch, version, last, frequency);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { return null; }
    }

    internal static WorkflowStopAuthorityRecord FromExplicitIntent(ManualStopFence fence, string intentId, long timestamp)
    {
        if (string.IsNullOrWhiteSpace(intentId) || timestamp <= 0 || fence.LastManualStopTimestamp >= timestamp)
            throw new InvalidOperationException("启动意图已被手动停止撤销；需要新的明确启动操作");
        return new(fence.Epoch, fence.Version, intentId, timestamp, fence.Frequency);
    }
}
