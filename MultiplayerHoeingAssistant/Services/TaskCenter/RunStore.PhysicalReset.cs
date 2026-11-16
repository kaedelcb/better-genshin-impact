using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    private sealed record PhysicalResetBarrier(int Version, string ResetId, DateTimeOffset AbandonedThroughUtc, string[] RunIds);
    private string PhysicalResetBarrierPath => Path.Combine(_runsDir, ".physical-reset.json");

    private PhysicalResetBarrier? ReadPhysicalResetBarrier()
    {
        var text = TryReadAllTextOrNull(PhysicalResetBarrierPath, "read-physical-reset");
        if (text is null) return null;
        var barrier = JsonSerializer.Deserialize<PhysicalResetBarrier>(text);
        if (barrier is not { Version: 1 } || !Guid.TryParseExact(barrier.ResetId, "N", out _)
            || barrier.AbandonedThroughUtc == default || barrier.RunIds is null
            || barrier.RunIds.Any(string.IsNullOrWhiteSpace))
            throw new RunRecordConflictException("物理复位屏障不可确认，保留原件并禁止恢复旧任务。");
        return barrier;
    }

    /// <summary>人工复位仅撤销旧执行资格，既不改变 Unknown/失败历史，也不证明旧任务成功。</summary>
    internal bool IsPhysicallyAbandoned(WorkflowRunRecord run)
    {
        lock (_gate)
            return ReadPhysicalResetBarrier() is { } barrier && barrier.RunIds.Contains(run.RunId, StringComparer.Ordinal);
    }

    /// <summary>
    /// 仅供已经确认物理 BGI 复位的 Host 调用。人工放弃记录独立于旧仲裁所有者，
    /// 使用既有运行目录发布锁；无论旧 owner 是否失效，都必须先读回屏障才放行新一代。
    /// </summary>
    internal void PublishPhysicalResetBarrier(DateTimeOffset abandonedThroughUtc)
    {
        lock (_gate)
        {
            using var publication = AcquirePublicationLock();
            using var drivePermission = TaskCenterDriveAuthority.EnterPublication();
            PhysicalResetBarrier? before;
            try { before = ReadPhysicalResetBarrier(); }
            catch (Exception ex) when (ex is JsonException or RunRecordConflictException)
            {
                // The new physical reset covers every currently persisted run. Keep
                // corrupt prior bytes for diagnosis before establishing this boundary.
                File.Copy(PhysicalResetBarrierPath,
                    PhysicalResetBarrierPath + ".invalid-" + Guid.NewGuid().ToString("N"), overwrite: false);
                before = null;
            }
            var runIds = EnumerateRunFilesOrEmpty().Select(path => Path.GetFileName(path)[..^".run.json".Length])
                .Concat(_knownProgress.Keys).Concat(before?.RunIds ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var barrier = new PhysicalResetBarrier(1, Guid.NewGuid().ToString("N"),
                before is not null && before.AbandonedThroughUtc > abandonedThroughUtc ? before.AbandonedThroughUtc : abandonedThroughUtc, runIds);
            var temporary = PhysicalResetBarrierPath + "." + barrier.ResetId + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(barrier, JsonOptions), Utf8NoBom);
                WithContentionRetry(() => File.Move(temporary, PhysicalResetBarrierPath, overwrite: true));
                if (ReadPhysicalResetBarrier() is not { } saved || saved.ResetId != barrier.ResetId
                    || !saved.RunIds.SequenceEqual(runIds))
                    throw new IOException("物理复位屏障未能读回，未放行新驱动。");
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
    }
}
