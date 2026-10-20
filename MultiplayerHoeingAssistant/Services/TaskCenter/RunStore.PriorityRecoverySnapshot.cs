using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

// The batch carries its original arrangement, rather than inventing a new run after progress loss.
// Strings keep the captured record and exact workflow bytes immutable between checkpoint publications.
internal sealed record PriorityPlanRecoverySnapshot(
    string BatchId, int Generation, string RunJson, string WorkflowBase64);

public sealed partial class RunStore
{
    /// <summary>Restore the same original record only after the Host has proved its batch/owner/stop identity.</summary>
    internal WorkflowRunRecord RestorePriorityRecoverySnapshot(WorkflowRunRecord captured)
    {
        if (string.IsNullOrWhiteSpace(captured.RunId)
            || captured.RunId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || captured.RunId.Contains("..", StringComparison.Ordinal)
            || !Guid.TryParse(captured.WireRunId, out _) || captured.RecordRevision <= 0
            || captured.StopRequested || captured.NonExecutingDiagnostic || captured.TerminalRelease is not null
            || ExecutionResetEvidence.Valid(captured))
            throw new RunRecordConflictException("原联机计划快照身份或停止状态无效，未创建其他运行。");
        lock (_gate)
        {
            using var owner = PriorityOwnerFence();
            using var publication = AcquirePublicationLock();
            var path = PathFor(captured.RunId);
            var original = TryReadAllTextOrNull(path, "read-priority-recovery");
            if (original is not null)
            {
                try
                {
                    var current = JsonSerializer.Deserialize<WorkflowRunRecord>(original, JsonOptions);
                    if (current is not null && !string.IsNullOrWhiteSpace(current.RunId))
                    {
                        // Any usable current record wins, including a later stop or a newer position.
                        if (current.RunId != captured.RunId || current.WireRunId != captured.WireRunId
                            || current.WorkflowId != captured.WorkflowId || current.StopAuthority != captured.StopAuthority)
                            throw new RunRecordConflictException("原计划路径已有另一有效身份，未覆盖。");
                        RememberProgress(current);
                        return current;
                    }
                }
                catch (JsonException) { /* Preserve these bytes before restoring the known arrangement below. */ }
            }
            var others = ListWithIntegrity("read-priority-recovery-conflicts").Records;
            if (others.Any(r => r.RunId != captured.RunId && r.WorkflowId == captured.WorkflowId
                && !r.IsTerminal && !ExecutionResetEvidence.Valid(r)))
                throw new RunRecordConflictException("同一计划已有其他未结运行，未恢复第二份。");
            if (others.Any(r => r.StopRequested && r.StopAuthority == captured.StopAuthority))
                throw new RunRecordConflictException("原计划来源已停止，未用旧快照恢复。");
            var temporary = path + ".priority-recovery-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (original is not null)
                {
                    Directory.CreateDirectory(_backupDir);
                    File.Copy(path, Path.Combine(_backupDir,
                        captured.RunId + ".priority-original-" + Guid.NewGuid().ToString("N") + ".run.json"));
                }
                File.WriteAllText(temporary, JsonSerializer.Serialize(captured, JsonOptions), Utf8NoBom);
                _owner?.Store.VerifyOwnerFence(_owner);
                File.Move(temporary, path, overwrite: true);
                RememberProgress(captured);
                return CopyProgress(captured);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
