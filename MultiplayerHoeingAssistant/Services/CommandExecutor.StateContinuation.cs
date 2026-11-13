using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Services;

public partial class CommandExecutor
{
    internal static bool IsQuiescentTakeoverReceipt(string? json, string ticket)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var data = doc.RootElement;
            return data.TryGetProperty("receiptOnly", out var read) && read.ValueKind == JsonValueKind.True
                && data.TryGetProperty("takeoverTicket", out var actual) && actual.ValueKind == JsonValueKind.String
                && actual.GetString() == ticket
                && data.TryGetProperty("quiesceConfirmed", out var exited) && exited.ValueKind == JsonValueKind.True
                && data.TryGetProperty("expectedExecutionInstanceId", out var root)
                && data.TryGetProperty("expectedStateRevision", out var revision)
                && (root.ValueKind == JsonValueKind.Null && revision.ValueKind == JsonValueKind.Null
                    || root.ValueKind == JsonValueKind.String && Guid.TryParse(root.GetString(), out _)
                    && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var value) && value > 0);
        }
        catch (JsonException) { return false; }
    }

    internal static bool HasManagedResumeSnapshot(string? raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw ?? "{}");
            if (!document.RootElement.TryGetProperty("recoverySnapshot", out var snapshot)
                || snapshot.ValueKind != JsonValueKind.Object) return false;
            return snapshot.EnumerateObject().Any(p =>
                string.Equals(p.Name, "ManagedByWorkflow", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True
                || string.Equals(p.Name, "NodeId", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()));
        }
        catch (JsonException) { return false; }
    }

    internal static bool TryReadNativeResumeSnapshot(string? raw, out JsonElement snapshot)
    {
        snapshot = default;
        try
        {
            using var document = JsonDocument.Parse(raw ?? "{}");
            if (!document.RootElement.TryGetProperty("recoverySnapshot", out var source)
                || source.ValueKind != JsonValueKind.Object) return false;
            string? Text(string name) => source.EnumerateObject().FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value
                    ? value.GetString() : null;
            var managed = source.EnumerateObject().FirstOrDefault(p =>
                string.Equals(p.Name, "ManagedByWorkflow", StringComparison.OrdinalIgnoreCase)).Value;
            var node = source.EnumerateObject().FirstOrDefault(p =>
                string.Equals(p.Name, "NodeId", StringComparison.OrdinalIgnoreCase)).Value;
            if (managed.ValueKind != JsonValueKind.False || node.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                return false; // Unknown/managed snapshots stay with the original plan runner.
            var type = Text("TaskType");
            if (type is not ("group" or "onedragon" or "solo")
                || string.IsNullOrWhiteSpace(Text(type == "solo" ? "ProjectName" : "GroupName"))) return false;
            snapshot = source.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal Action<string, BgiEpoch>? CoordinatedTakeoverAllocated { get; set; }

    internal void RestoreCoordinatedTakeover(OnlineHoeingBatch batch)
    {
        if (!batch.PolicyTicketReleased && batch.TakeoverTicket is { } ticket && batch.ExecutionEpoch is { } epoch)
            RestoreCoordinatedTakeover(ticket, epoch);
    }

    private void RestoreCoordinatedTakeover(string ticket, BgiEpoch epoch)
    {
        lock (_resetStateGate)
        {
            if (_takeoverTicket is not null && (_takeoverTicket != ticket
                || IsBgiEpochChanged(_takeoverEpoch, epoch)))
                throw new InvalidOperationException("已有另一接管责任，不能覆盖为恢复记录");
            _takeoverTicket = ticket;
            _takeoverEpoch = epoch;
            if (_externalClientProvider?.Invoke() is { } ext
                && !IsBgiEpochChanged(epoch, ext.ServerEpoch)) ext.TakeoverTicket = ticket;
        }
    }

    internal async Task ReconcileCompletionTicketAsync(OnlineHoeingBatch batch, CancellationToken ct)
    {
        if (batch.PolicyExecution is null && !(batch.ProcessRecoveryPreviousEpoch is not null && batch.CompletionRecoveryReservationAcquired)
            || batch.ExecutionEpoch is not { } epoch
            || batch.TakeoverTicket is not { } ticket || _takeoverTicket != ticket) return;
        if (await ConfirmTakeoverRevokedAsync(epoch, batch.TakenOver, ct).ConfigureAwait(false))
        {
            ClearTicket(ticket);
            batch.PolicyTicketReleased = true;
            batch.PersistRecovery?.Invoke();
        }
    }

    private string? _retainedSuspendFenceTicket;
    private long? _retainedSuspendStopVersion;

    // This is the frozen completion action of a completed coordinated batch, not a new manual start.
    // The caller retains the batch/epoch/stop authority and waits for an actually available slot.
    internal async Task<CommandResult> StartRetainedCompletionActionAsync(OnlineHoeingBatch batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!batch.StateContinuationEnabled || !batch.LocalCompletionReady || batch.ExplicitStopRequested
            || batch.Policy is not { Policy: TaskConflictPolicy.RunSpecified } policy)
            return new CommandResult { ErrorCode = "completion_not_authorized", Message = "原批次完成动作授权已撤销，未发送" };
        var ext = _externalClientProvider?.Invoke();
        var epoch = batch.ExecutionEpoch ?? batch.LocalSuccessEpoch;
        if (ext is not { State: BgiExternalLinkState.Ready } || epoch is null
            || !CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch))
            return new CommandResult { ErrorCode = "state_unavailable", Message = "原 BGI 通道暂不可用，保留完成动作" };

        CompletionStart start;
        try
        {
            if (batch.PolicySubmitPayload is null)
            {
                start = await DescribeCompletionStartAsync(policy, ext, epoch, cancellationToken).ConfigureAwait(false);
                batch.PolicySubmitPayload = FreezeCompletionStart(start);
            }
            start = ReadFrozenCompletionStart(batch.PolicySubmitPayload!);
        }
        catch (CompletionStartException ex)
        { return new CommandResult { ErrorCode = ex.Code, Message = ex.Message }; }
        if (!start.OneDragon && start.Index > 0 && !ext.HasCapability("task.start.explicitGroupStart.v1"))
            return new CommandResult { ErrorCode = "explicit_group_start_unsupported", Message = "BGI 不支持配置组明确起点，保留原起点且未发送" };
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.ExplicitStopRequested || !CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch))
            return new CommandResult { ErrorCode = "completion_not_authorized", Message = "原完成动作已撤销，未发送" };
        batch.PolicyStartAttempted = true;
        batch.PersistRecovery?.Invoke();
        var submitted = await ext.SubmitTaskStartAsync(start.OneDragon ? null : start.Name,
            start.OneDragon ? start.Name : null, start.Index, 0, cancellationToken: cancellationToken,
            startFromTaskId: start.TaskId,
            idempotencyKey: batch.PolicyRequestKey, expectedConfigRevision: start.Revision,
            bgiEpoch: new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
            retainUntilAcknowledged: true, expectedStopVersion: batch.StateContinuationStopVersion).ConfigureAwait(false);
        if (!submitted.Success || string.IsNullOrWhiteSpace(submitted.TaskHandle))
            return new CommandResult { ErrorCode = submitted.ErrorCode ?? "result_unknown", Message = submitted.ErrorMessage ?? "原完成动作受理结果尚未取回" };
        return new CommandResult
        {
            Status = "success", JobId = submitted.TaskHandle, TargetProcessId = epoch.ProcessId,
            TargetStartTicksUtc = epoch.StartTicksUtc.ToString(), Message = "原完成动作已受理：" + start.Name
        };
    }
}
