using System.IO;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    internal void CapturePriorityPlanRecovery(OnlineHoeingBatch batch)
    {
        if (!batch.PriorityRecoveryEnabled || batch.ManagedRunId is not { } runId) return;
        try
        {
            var run = _runs.Load(runId);
            if (run is null || run.NonExecutingDiagnostic || run.WorkflowId is not { Length: > 0 }
                || !RecoveryHandoffMatches(batch, run)) return;
            ValidatePriorityPlanIdentity(batch, run);
            var path = PriorityRecoveryWorkflowPath(run.WorkflowId);
            var bytes = File.ReadAllBytes(path);
            if (WorkflowStore.HashBytes(bytes) != run.WorkflowRevision) return;
            var current = _workflows.LoadSnapshot(run.WorkflowId);
            if (current.Revision != run.WorkflowRevision) return;
            batch.ManagedPlanSnapshot = new(batch.BatchId, batch.Generation,
                JsonSerializer.Serialize(run), Convert.ToBase64String(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or WorkflowQuarantinedException or ArgumentException or InvalidDataException)
        {
            // A failed progress read must retain the last known original arrangement.
            _log?.Invoke("[联机恢复] 原计划快照暂未刷新，保留上次原安排：" + ex.Message);
        }
    }

    /// <summary>Reconstitute only this dead helper owner's original arrangement, before the existing Runner is reattached.</summary>
    internal async Task<bool> RestorePriorityPlanRecoveryAsync(OnlineHoeingBatch batch)
    {
        if (!batch.RestoredFromRecovery || !batch.PriorityRecoveryEnabled) return false;
        batch.ObserveRecoveryStopSignal();
        if (batch.IsCancellationRequested || batch.ExplicitStopRequested
            || batch.RecoveryStopSignal is not { IsStopped: false }) return false;
        if (batch.ManagedRunId is not { } runId) return true; // Older intact records still use the original receipt association.
        try
        {
            if (_runs.Load(runId) is { } intact)
            {
                if (intact.StopRequested || IsExecutionResetReleased(intact)) { batch.Cancel(); return false; }
                ValidatePriorityPlanIdentity(batch, intact);
                if (_workflows.LoadSnapshot(intact.WorkflowId).Revision == intact.WorkflowRevision) return true;
            }
        }
        catch (Exception ex) when (ex is JsonException or WorkflowQuarantinedException
            || ex is IOException) { }
        if (batch.ManagedPlanSnapshot is not { } saved || saved.BatchId != batch.BatchId
            || saved.Generation != batch.Generation || batch.RecoveryOwnerPid <= 0 || batch.RecoveryOwnerStartTicks <= 0
            || ProbeOriginalBgiProcess(batch.RecoveryOwnerPid, batch.RecoveryOwnerStartTicks) != false)
            return false;
        batch.ObserveRecoveryStopSignal();
        if (batch.IsCancellationRequested || batch.ExplicitStopRequested
            || batch.RecoveryStopSignal is not { IsStopped: false }) return false;
        WorkflowRunRecord captured;
        byte[] workflowBytes;
        try
        {
            captured = JsonSerializer.Deserialize<WorkflowRunRecord>(saved.RunJson)
                ?? throw new InvalidDataException("原计划快照为空");
            workflowBytes = Convert.FromBase64String(saved.WorkflowBase64);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new InvalidDataException("原计划快照无法解析，未构造替代任务", ex); }
        if (captured.RunId != runId || !RecoveryHandoffMatches(batch, captured)
            || WorkflowStore.HashBytes(workflowBytes) != captured.WorkflowRevision)
            throw new InvalidDataException("原计划快照与本批身份或原修订不一致");
        var document = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(workflowBytes));
        if (document is null || document.WorkflowId != captured.WorkflowId
            || document.Schema != WorkflowDocumentSchema.Name || document.SchemaVersion != WorkflowDocumentSchema.Version
            || document.Nodes is null || document.Triggers is null || document.Terminal is null)
            throw new InvalidDataException("原计划内容身份或结构不完整");
        await EnsureRecoveredAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_shutdown || _manualResetActive != 0 || _drives.ContainsKey(captured.WorkflowId)
                || _reservedWorkflows.Contains(captured.WorkflowId)) return false;
            batch.ObserveRecoveryStopSignal();
            if (batch.IsCancellationRequested || batch.RecoveryStopSignal is not { IsStopped: false }) return false;
            WorkflowRunRecord? existing;
            try { existing = _runs.Load(runId); }
            catch (JsonException) { existing = null; }
            if (existing is { StopRequested: true } || existing is not null && IsExecutionResetReleased(existing))
            { batch.Cancel(); return false; }
            using var workflowWindow = _workflows.AcquireMigrationWindow();
            RestorePriorityWorkflowBytes(captured, workflowBytes);
            var restored = _runs.RestorePriorityRecoverySnapshot(captured);
            if (restored.StopRequested || IsExecutionResetReleased(restored))
            { batch.Cancel(); return false; }
            return restored.RunId == runId;
        }
    }

    private static void ValidatePriorityPlanIdentity(OnlineHoeingBatch batch, WorkflowRunRecord current)
    {
        var pinned = batch.ManagedPlanSnapshot is { } saved && saved.BatchId == batch.BatchId && saved.Generation == batch.Generation
            ? JsonSerializer.Deserialize<WorkflowRunRecord>(saved.RunJson) : null;
        if (!RecoveryHandoffMatches(batch, current) || pinned is not null
            && (pinned.RunId != current.RunId || pinned.WireRunId != current.WireRunId
                || pinned.WorkflowId != current.WorkflowId || pinned.StopAuthority != current.StopAuthority
                || pinned.WorkflowRevision != current.WorkflowRevision))
            throw new InvalidDataException("现有计划的身份或内容与原批次快照不一致，未接回其他安排");
    }

    private static bool RecoveryHandoffMatches(OnlineHoeingBatch batch, WorkflowRunRecord run)
    {
        var handoffs = run.OnlineHoeingHistory.Concat(run.OnlineHoeingHandoff is { } current ? [current] : []);
        var originalEpoch = WorkflowStopAuthority.Epoch(batch.ProcessRecoveryPreviousEpoch ?? batch.ExecutionEpoch);
        return handoffs.Any(h => h.StateContinuation && h.PriorityRecovery && h.Generation == batch.Generation
            && (h.Ticket is not null && h.Ticket == batch.TakeoverTicket
                || h.Ticket is null && originalEpoch is not null && h.Epoch == originalEpoch
                || batch.PlanResumeContinuation?.GetValueOrDefault("idempotencyKey") is string key && h.ResumeKey == key
                || batch.ProcessRecoveryPreviousEpoch is { } previous && h.Epoch == WorkflowStopAuthority.Epoch(previous)
                || run.PriorityProcessContinuations.Any(c => c.BatchId == batch.BatchId && c.Generation == batch.Generation
                    && c.PreviousEpoch == h.Epoch)));
    }

    private string PriorityRecoveryWorkflowPath(string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId) || workflowId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || workflowId.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("原计划文件身份非法");
        return Path.Combine(_workflows.RootDirectory, workflowId + ".flow.json");
    }

    private void RestorePriorityWorkflowBytes(WorkflowRunRecord captured, byte[] bytes)
    {
        var path = PriorityRecoveryWorkflowPath(captured.WorkflowId);
        byte[]? previous;
        try { previous = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { previous = null; }
        catch (DirectoryNotFoundException) { previous = null; }
        if (previous is not null)
        {
            try
            {
                var current = _workflows.LoadSnapshot(captured.WorkflowId);
                if (current.Revision != captured.WorkflowRevision)
                    throw new WorkflowRevisionConflictException("原计划已有新的有效内容，未用旧快照覆盖。");
                return;
            }
            catch (WorkflowQuarantinedException)
            {
                var brokenJson = false;
                try { using var parsed = JsonDocument.Parse(previous); }
                catch (JsonException) { brokenJson = true; }
                if (!brokenJson)
                    throw new WorkflowRevisionConflictException("现有流程为有效JSON但版本或迁移状态不可确认，保留原件，不用旧快照覆盖。");
            }
        }
        Directory.CreateDirectory(_workflows.RootDirectory);
        if (previous is not null)
        {
            var backup = Path.Combine(_workflows.RootDirectory, "_backup");
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, captured.WorkflowId + ".priority-original-"
                + Guid.NewGuid().ToString("N") + ".flow.json"), previous);
        }
        var temporary = path + ".priority-recovery-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            if (_workflows.LoadSnapshot(captured.WorkflowId).Revision != captured.WorkflowRevision)
                throw new WorkflowRevisionConflictException("原计划恢复字节与固定修订不符");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
