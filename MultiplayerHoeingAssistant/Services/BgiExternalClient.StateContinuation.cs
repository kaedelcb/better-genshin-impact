using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

internal sealed class BgiStateContinuationStoppedException(string message) : OperationCanceledException(message)
{
}

public sealed partial class BgiExternalClient
{
    public const string StateContinuationCapability = "task.stateContinuation.v1";
    internal async Task EnsureCoordinatedRecoveryStopAsync(OnlineHoeingBatch batch, CancellationToken ct)
    {
        if (!HasCapability("task.coordinatedRecoveryStop.v1"))
            throw new System.IO.IOException("BGI 尚不支持跨进程停止事实，等待配套执行通道");
        if (batch.RecoveryStopSignalMissing || batch.RestoredFromRecovery && batch.RecoveryStopSignal is null)
        {
            var original = ServerEpoch ?? throw new System.IO.IOException("原进程暂未确认");
            if (!ViewModels.CoordinatedBatchLoop.SameEpoch(batch.ExecutionEpoch, original))
                throw new System.IO.IOException("原停止事实丢失且进程已变化，不能猜测恢复授权");
            var actualStop = await ReadStateContinuationStopVersionAsync(original, ct)
                ?? throw new System.IO.IOException("等待同一原进程的停止围栏");
            batch.ConfirmOriginalProcessStopFence(original, actualStop);
        }
        batch.EnsureRecoveryStopSignal();
        batch.ObserveRecoveryStopSignal();
        batch.Cts.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
    }

    private readonly SemaphoreSlim _retainedStartPayloadGate = new(1, 1);
    private readonly Dictionary<string, (string Input, Dictionary<string, object?> Payload)> _retainedStartPayloads = new(StringComparer.Ordinal);

    internal async Task<long?> ReadStateContinuationStopVersionAsync(BgiEpoch expected,
        CancellationToken cancellationToken = default)
    {
        if (ServerEpoch is not { } epoch || epoch.ProcessId != expected.ProcessId
            || epoch.StartTicksUtc != expected.StartTicksUtc) return null;
        var response = await SendCommandAsync(ExternalOperations.TaskStatus, null, CommandTimeout, cancellationToken).ConfigureAwait(false);
        if (response is not { Success: true, Data: not null }) return null;
        using var doc = JsonDocument.Parse(response.Data);
        var data = doc.RootElement;
        if (!data.TryGetProperty("bgiEpoch", out var e) || e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("processId", out var pid) || !pid.TryGetInt32(out var processId)
            || !e.TryGetProperty("startTicksUtc", out var ticks) || !ticks.TryGetInt64(out var startTicks)
            || processId != expected.ProcessId || startTicks != expected.StartTicksUtc
            || !data.TryGetProperty("stopVersion", out var fence) || !fence.TryGetInt64(out var version)) return null;
        if (ServerEpoch is not { } current || current.ProcessId != expected.ProcessId
            || current.StartTicksUtc != expected.StartTicksUtc) return null;
        return version;
    }

    private async Task<Dictionary<string, object?>> FreezeRetainedStartPayloadAsync(
        Dictionary<string, object?> payload, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || !HasCapability(StateContinuationCapability))
            throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "原请求编号或状态续办能力缺失，未发送");
        var input = JsonSerializer.Serialize(payload);
        await _retainedStartPayloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_retainedStartPayloads.TryGetValue(key, out var frozen))
            {
                if (frozen.Input != input)
                    throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "同一请求编号的任务内容已经冻结，未发送变更内容");
                RequireRetainedStartEpoch(frozen.Payload);
                return new Dictionary<string, object?>(frozen.Payload);
            }
            var epoch = RequireRetainedStartEpoch(payload);
            var stopVersion = await ReadStateContinuationStopVersionAsync(epoch, cancellationToken).ConfigureAwait(false);
            var afterStatus = RequireRetainedStartEpoch(payload);
            if (afterStatus.ProcessId != epoch.ProcessId || afterStatus.StartTicksUtc != epoch.StartTicksUtc)
                throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "状态查询期间 BGI 已变化，未重绑定原请求");
            if (stopVersion is null)
                throw new BgiNotSentException(BgiNotSentException.ChannelNotReady, "原 BGI 停止状态暂不可读，未发送");
            if (payload.TryGetValue("expectedStopVersion", out var expectedFence) && expectedFence is long expectedVersion
                && expectedVersion != stopVersion.Value)
                throw new BgiStateContinuationStoppedException("BGI 已收到本批之后的停止操作，原批次不得启动新任务");
            cancellationToken.ThrowIfCancellationRequested();
            var copy = new Dictionary<string, object?>(payload)
            {
                ["takeoverTicket"] = TakeoverTicket,
                ["expectedStopVersion"] = expectedFence is long providedVersion ? providedVersion : stopVersion.Value
            };
            if (copy.GetValueOrDefault("bgiEpoch") is null)
                copy["bgiEpoch"] = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc };
            _retainedStartPayloads.Add(key, (input, copy));
            return new Dictionary<string, object?>(copy);
        }
        finally { _retainedStartPayloadGate.Release(); }
    }


    private BgiEpoch RequireRetainedStartEpoch(IReadOnlyDictionary<string, object?> payload)
    {
        var current = ServerEpoch ?? throw new BgiNotSentException(BgiNotSentException.ChannelNotReady,
            "原 BGI 身份暂不可读，未发送");
        if (!payload.TryGetValue("bgiEpoch", out var rawEpoch) || rawEpoch is null) return current;
        var epoch = JsonSerializer.SerializeToElement(rawEpoch);
        if (epoch.ValueKind != JsonValueKind.Object
            || !epoch.TryGetProperty("processId", out var pid) || !pid.TryGetInt32(out var processId)
            || !epoch.TryGetProperty("startTicksUtc", out var ticks) || !ticks.TryGetInt64(out var startTicks)
            || processId != current.ProcessId || startTicks != current.StartTicksUtc)
            throw new BgiNotSentException(BgiNotSentException.LocalRequestRejected,
                "请求目标不是原 BGI 进程，未向后继进程重放");
        return new BgiEpoch { ProcessId = processId, StartTicksUtc = startTicks };
    }

    public async Task<BgiTaskQueueStatus?> QueryTaskQueueStatusByKeyAsync(string key,
        CancellationToken cancellationToken = default)
    {
        if (!HasCapability(StateContinuationCapability)) return null;
        var response = await SendCommandAsync(ExternalOperations.TaskQueueStatus,
            new { idempotencyKey = key }, CommandTimeout, cancellationToken).ConfigureAwait(false);
        return response is { Success: true, Data: not null }
            ? ParseStateContinuationQueueStatus(response.Data) : null;
    }

    public async Task<bool> AcknowledgeTaskResultAsync(string key, string taskHandle, BgiEpoch epoch,
        CancellationToken cancellationToken = default)
    {
        if (!HasCapability(StateContinuationCapability)) return false;
        var response = await SendCommandAsync("ext.task.acknowledge", new
        {
            submissionKey = key,
            taskHandle,
            bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
            idempotencyKey = "ack/" + key
        }, CommandTimeout, cancellationToken).ConfigureAwait(false);
        return response.Success;
    }

    private static BgiTaskQueueStatus ParseStateContinuationQueueStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        CoordinatedHoeingEvidence? evidence = null;
        if (root.TryGetProperty("hoeingEvidence", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var parsed = item.Deserialize<CoordinatedHoeingEvidence>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed?.IsValid == true) evidence = parsed;
            }
            catch (JsonException) { }
        }
        BgiEpoch? epoch = null;
        if (root.TryGetProperty("bgiEpoch", out var epochElement)
            && epochElement.ValueKind == JsonValueKind.Object
            && epochElement.TryGetProperty("processId", out var pid) && pid.TryGetInt32(out var processId)
            && epochElement.TryGetProperty("startTicksUtc", out var ticks) && ticks.TryGetInt64(out var startTicks))
            epoch = new BgiEpoch { ProcessId = processId, StartTicksUtc = startTicks };
        return new BgiTaskQueueStatus
        {
            TaskHandle = Text(root, "taskHandle"), IdempotencyKey = Text(root, "idempotencyKey"), Epoch = epoch,
            Status = Text(root, "status"), HoeingEvidence = evidence,
            ExecutionExitConfirmed = root.TryGetProperty("executionExitConfirmed", out var exited) && exited.ValueKind == JsonValueKind.True,
            Cancelled = root.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True,
            ErrorCode = Text(root, "errorCode"), ErrorMessage = Text(root, "message")
        };
    }
}
