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

        var frozen = batch.PolicySubmitPayload;
        if (frozen is null)
        {
            var name = policy.SpecifiedTaskName?.Trim() ?? "";
            var oneDragon = policy.SpecifiedTaskType == "onedragon";
            if (name.Length == 0) return new CommandResult { ErrorCode = "specified_task_missing", Message = "完成动作未配置任务名称" };
            var listed = await SendIpcPreferredAsync("config.list", null, 3000).ConfigureAwait(false);
            if (listed is not { Success: true, Data: not null })
                return new CommandResult { ErrorCode = "state_unavailable", Message = "完成动作配置暂不可读，保留原动作" };
            using var doc = JsonDocument.Parse(listed.Data);
            if (!doc.RootElement.TryGetProperty(oneDragon ? "oneClickConfigs" : "configGroups", out var names)
                || names.ValueKind != JsonValueKind.Array
                || !names.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == name))
                return new CommandResult { ErrorCode = "specified_task_missing", Message = "完成动作配置不存在：" + name };
            var described = await ext.SendCommandAsync("ext.config.describe", new
            {
                groupName = oneDragon ? null : name,
                configName = oneDragon ? name : null
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            string? revision = null;
            if (described is { Success: true, Data: not null })
            {
                using var config = JsonDocument.Parse(described.Data);
                if (TryParseEpoch(described.Data, out var pid, out var ticks)
                    && pid == epoch.ProcessId && ticks == epoch.StartTicksUtc
                    && config.RootElement.TryGetProperty("configRevision", out var value) && value.ValueKind == JsonValueKind.String
                    && config.RootElement.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                    && tasks.EnumerateArray().Any(item => item.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True))
                    revision = value.GetString();
            }
            if (string.IsNullOrWhiteSpace(revision))
                return new CommandResult { ErrorCode = "state_unavailable", Message = "完成动作配置版本暂不可读，保留原动作" };
            batch.PolicySubmitPayload = new Dictionary<string, object?>
            {
                ["name"] = name, ["oneDragon"] = oneDragon, ["revision"] = revision
            };
            frozen = batch.PolicySubmitPayload!;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.ExplicitStopRequested || !CoordinatedBatchLoop.SameEpoch(epoch, ext.ServerEpoch))
            return new CommandResult { ErrorCode = "completion_not_authorized", Message = "原完成动作已撤销，未发送" };
        var frozenName = (string)frozen["name"]!;
        var frozenOneDragon = (bool)frozen["oneDragon"]!;
        batch.PolicyStartAttempted = true;
        batch.PersistRecovery?.Invoke();
        var submitted = await ext.SubmitTaskStartAsync(frozenOneDragon ? null : frozenName,
            frozenOneDragon ? frozenName : null, 0, 0, cancellationToken: cancellationToken,
            idempotencyKey: batch.PolicyRequestKey, expectedConfigRevision: (string)frozen["revision"]!,
            bgiEpoch: new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
            retainUntilAcknowledged: true, expectedStopVersion: batch.StateContinuationStopVersion).ConfigureAwait(false);
        if (!submitted.Success || string.IsNullOrWhiteSpace(submitted.TaskHandle))
            return new CommandResult { ErrorCode = submitted.ErrorCode ?? "result_unknown", Message = submitted.ErrorMessage ?? "原完成动作受理结果尚未取回" };
        return new CommandResult
        {
            Status = "success", JobId = submitted.TaskHandle, TargetProcessId = epoch.ProcessId,
            TargetStartTicksUtc = epoch.StartTicksUtc.ToString(), Message = "原完成动作已受理：" + frozenName
        };
    }
}
