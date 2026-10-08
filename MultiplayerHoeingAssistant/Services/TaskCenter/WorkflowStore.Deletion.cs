using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>删除保留的原字节；恢复按归档身份重新校验，不能覆盖后续计划。</summary>
public sealed record WorkflowDeletionReceipt(string RecoveryId, string WorkflowId, string Name,
    string Revision, string RecoveryFilePath, DateTimeOffset DeletedAt,
    bool CanRestore = true, string? RecoveryProblem = null);

public sealed partial class WorkflowStore
{
    private const string DeletionTimeFormat = "yyyyMMddHHmmssfffffff";
    private string DeletedDir => Path.Combine(_flowsDir, "_deleted");

    private sealed class WorkflowWriteState
    {
        internal readonly object Gate = new();
        internal FileStream? Publication;
        internal int Depth;
    }

    private static readonly ConcurrentDictionary<string, WorkflowWriteState> WriteStates =
        new(StringComparer.OrdinalIgnoreCase);

    // Save, migration, delete and restore share one reentrant in-process window and
    // one fixed physical lock. Different Store instances cannot pass the same revision.
    private IDisposable AcquireWriteWindow()
    {
        Monitor.Enter(_gate);
        try
        {
            if (_writeState.Depth == 0)
            {
                EnsureUnlinked(_flowsDir);
                Directory.CreateDirectory(_flowsDir);
                // Keep the lock outside the flow snapshot root: migration takes a
                // complete byte snapshot while this exclusive window is held.
                var lockPath = Path.Combine(Path.GetDirectoryName(_flowsDir)!, ".workflowstore-" +
                    HashBytes(Encoding.UTF8.GetBytes(_flowsDir.ToUpperInvariant()))[..16] + ".lock");
                EnsureUnlinked(lockPath);
                _writeState.Publication = RunStore.WithContentionRetry(() =>
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            _writeState.Depth++;
            return new WorkflowWriteWindow(_writeState);
        }
        catch { Monitor.Exit(_gate); throw; }
    }

    private sealed class WorkflowWriteWindow(WorkflowWriteState state) : IDisposable
    {
        private WorkflowWriteState? _state = state;
        public void Dispose()
        {
            if (_state is not { } current) return;
            _state = null;
            try
            {
                if (--current.Depth == 0)
                {
                    current.Publication!.Dispose();
                    current.Publication = null;
                }
            }
            finally { Monitor.Exit(current.Gate); }
        }
    }

    private static void EnsureUnlinked(string path)
    {
        if (MigrationSwitchTransaction.HasReparsePoint(path))
            throw new IOException("流程管理路径含目录链接，已保留原件，未修改。");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("流程管理文件含链接，已保留原件，未修改。");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
    }

    /// <summary>仅移入可恢复区；流程资源、运行历史和迁移事务不删除。</summary>
    public WorkflowDeletionReceipt Delete(string workflowId, string expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new WorkflowRevisionConflictException("删除必须携带当前计划修订，请刷新后重试。");
        using var window = AcquireWriteWindow();
        var source = PathFor(workflowId);
        EnsureUnlinked(source);
        var bytes = File.ReadAllBytes(source);
        var revision = HashBytes(bytes);
        if (!revision.Equals(expectedRevision, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowRevisionConflictException("计划已被修改，未删除；请刷新后重新确认。");

        var entry = InspectBytes(bytes, source);
        var deletedAt = DateTimeOffset.UtcNow;
        var recoveryId = workflowId + "." + deletedAt.ToString(DeletionTimeFormat, CultureInfo.InvariantCulture)
            + "." + revision + "." + Guid.NewGuid().ToString("N");
        EnsureUnlinked(DeletedDir);
        Directory.CreateDirectory(DeletedDir);
        var archive = Path.Combine(DeletedDir, recoveryId + ".flow.json");
        // Recheck immediately before the single atomic move. Cooperative writers hold
        // this window; a noncooperating replacement must not be reported as deleted.
        if (!HashFileBytes(source).Equals(revision, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowRevisionConflictException("计划在删除前已变化，未删除。");
        File.Move(source, archive, overwrite: false);
        if (!HashFileBytes(archive).Equals(revision, StringComparison.OrdinalIgnoreCase))
        {
            // Never overwrite a newer file while recovering an unexpected external race.
            try { File.Move(archive, source, overwrite: false); }
            catch (IOException) { }
            throw new WorkflowRevisionConflictException("删除时检测到外部修改，原字节已保留；请刷新检查。");
        }
        return new WorkflowDeletionReceipt(recoveryId, workflowId, entry.Name, revision, archive, deletedAt);
    }

    public IReadOnlyList<WorkflowDeletionReceipt> ListDeleted()
    {
        lock (_gate)
        {
            if (!Directory.Exists(DeletedDir)) return [];
            EnsureUnlinked(DeletedDir);
            var result = new List<WorkflowDeletionReceipt>();
            foreach (var archive in Directory.EnumerateFiles(DeletedDir, "*.flow.json"))
            {
                var recoveryId = Path.GetFileName(archive)[..^".flow.json".Length];
                var receipt = ParseRecoveryIdentity(recoveryId);
                try
                {
                    EnsureUnlinked(archive);
                    var bytes = File.ReadAllBytes(archive);
                    if (!HashBytes(bytes).Equals(receipt.Revision, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("保留副本校验失败，不能自动恢复。");
                    receipt = receipt with { Name = InspectBytes(bytes, PathFor(receipt.WorkflowId)).Name };
                    if (File.Exists(PathFor(receipt.WorkflowId)))
                        receipt = receipt with { CanRestore = false, RecoveryProblem = "同身份计划已存在，恢复不会覆盖它。" };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    receipt = receipt with { CanRestore = false, RecoveryProblem = ex.Message };
                }
                result.Add(receipt);
            }
            return result.OrderByDescending(r => r.DeletedAt).ToList();
        }
    }

    public WorkflowDeletionReceipt RestoreDeleted(string recoveryId)
    {
        using var window = AcquireWriteWindow();
        var receipt = ParseRecoveryIdentity(recoveryId);
        var target = PathFor(receipt.WorkflowId);
        EnsureUnlinked(receipt.RecoveryFilePath);
        EnsureUnlinked(target);
        var bytes = File.ReadAllBytes(receipt.RecoveryFilePath);
        if (!HashBytes(bytes).Equals(receipt.Revision, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowRevisionConflictException("保留副本已变化，原件保留，不能自动恢复。");
        if (File.Exists(target))
            throw new WorkflowRevisionConflictException("同身份计划已存在，未覆盖；请保留当前计划。");
        var temporary = Path.Combine(_flowsDir, ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return receipt with { Name = InspectBytes(bytes, target).Name };
    }

    private WorkflowDeletionReceipt ParseRecoveryIdentity(string recoveryId)
    {
        if (string.IsNullOrWhiteSpace(recoveryId) || Path.GetFileName(recoveryId) != recoveryId)
            throw new ArgumentException("恢复身份无效。", nameof(recoveryId));
        var guidAt = recoveryId.LastIndexOf('.');
        var hashAt = guidAt > 0 ? recoveryId.LastIndexOf('.', guidAt - 1) : -1;
        var timeAt = hashAt > 0 ? recoveryId.LastIndexOf('.', hashAt - 1) : -1;
        if (timeAt <= 0 || !Guid.TryParseExact(recoveryId[(guidAt + 1)..], "N", out _))
            throw new ArgumentException("恢复身份无效。", nameof(recoveryId));
        var workflowId = recoveryId[..timeAt];
        _ = PathFor(workflowId);
        var revision = recoveryId[(hashAt + 1)..guidAt];
        if (revision.Length != 64 || revision.Any(c => !Uri.IsHexDigit(c)) ||
            !DateTimeOffset.TryParseExact(recoveryId[(timeAt + 1)..hashAt], DeletionTimeFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var deletedAt))
            throw new ArgumentException("恢复身份无效。", nameof(recoveryId));
        return new WorkflowDeletionReceipt(recoveryId, workflowId, workflowId, revision,
            Path.Combine(DeletedDir, recoveryId + ".flow.json"), deletedAt);
    }
}
