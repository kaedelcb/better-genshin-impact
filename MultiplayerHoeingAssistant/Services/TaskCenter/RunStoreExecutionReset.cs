using System.Globalization;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class ExecutionResetEvidence
{
    internal static string FactsHash(WorkflowRunRecord run)
    {
        var copy = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(run))!;
        copy.ExecutionResetRelease = null;
        copy.RecordRevision = 0;
        copy.UpdatedAt = default;
        return TerminalReleaseEvidence.Hash(copy);
    }

    internal static bool TryEpoch(string? epoch, out int processId, out long startTicks)
    {
        processId = 0; startTicks = 0;
        var parts = epoch?.Split(':');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out startTicks)
            && startTicks > 0 && startTicks <= DateTime.MaxValue.Ticks
            && epoch == processId.ToString(CultureInfo.InvariantCulture) + ":" + startTicks.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool Valid(WorkflowRunRecord run)
    {
        try { return ValidCore(run); }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    private static bool ValidCore(WorkflowRunRecord run)
    {
        var release = run.ExecutionResetRelease;
        if (release is not { Version: 1 } || !Guid.TryParseExact(release.ResetId, "N", out _)
            || release.RunId != run.RunId || release.WorkflowId != run.WorkflowId || release.WireRunId != run.WireRunId
            || release.OriginalState != run.State || release.CompletedAtUtc == default || release.ProcessExits is null
            || release.FactsHash != FactsHash(run)) return false;
        if (release.ProcessExits.Count == 0) return !RunStore.HasUnresolvedTerminalResponsibility(run);
        var required = UnresolvedEpochs(run);
        if (required is null || !required.Order().SequenceEqual(release.ProcessExits.Select(p => p.OriginalEpoch).Order())) return false;
        return release.ProcessExits.Select(p => p.OriginalEpoch).Distinct().Count() == release.ProcessExits.Count
            && release.ProcessExits.All(proof => proof is not null && TryEpoch(proof.OriginalEpoch, out _, out var oldTicks)
                && TryEpoch(proof.CurrentEpoch, out _, out _)
                && proof.OriginalEpoch != proof.CurrentEpoch && proof.ObservedAtUtc != default
                && proof.ObservedAtUtc <= release.CompletedAtUtc
                && (proof.Disposition == "process_absent" && proof.ObservedStartTicksUtc is null
                    || proof.Disposition == "process_id_reused" && proof.ObservedStartTicksUtc is > 0
                        && proof.ObservedStartTicksUtc != oldTicks));
    }

    // Process exit is sufficient only for BGI task bodies and the in-process account switch
    // prerequisite; other prerequisites, effects and handoff tickets continue to require their
    // separate existing observation contracts.
    internal static HashSet<string>? UnresolvedEpochs(WorkflowRunRecord run)
    {
        if (run.OnlineHoeingHandoff is not null
            || run.OnlineHoeingHistory.Any(h => !TerminalReleaseEvidence.ValidOnlineHoeingHandoff(run, h))
            || run.PendingCompletion is { } completion && !TerminalReleaseEvidence.CompletionSettled(run, completion)
            || run.CompletionHistory.Any(c => !TerminalReleaseEvidence.CompletionSettled(run, c))) return null;
        // 账号切换（ext.prerequisite.account）在 BGI 进程内执行：原 BGI 进程退出后不再有在途动作，
        // 新运行开始前会按 expectedUid 重新核对并切换账号，所以与任务本体一样可用原进程退出解除。
        var prerequisites = run.PrerequisiteActions.Where(a => !TerminalReleaseEvidence.PrerequisiteSettled(run, a)).ToList();
        if (prerequisites.Any(a => a.Kind != AccountPrerequisiteKind || !TryEpoch(a.Epoch, out _, out _)
            || string.IsNullOrWhiteSpace(a.IdempotencyKey) || string.IsNullOrWhiteSpace(a.NodeId)
            || a.WireRunId != run.WireRunId || !Guid.TryParseExact(run.WireRunId, "N", out _))) return null;
        var unsettled = TerminalReleaseEvidence.Submissions(run).Where(s => !TerminalReleaseEvidence.BodySettled(run, s)).ToList();
        if (unsettled.Count == 0 && prerequisites.Count == 0 || unsettled.Any(s => !TryEpoch(s.Epoch, out _, out _)
            || string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.NodeId)
            || s.WireRunId != run.WireRunId || !Guid.TryParseExact(run.WireRunId, "N", out _))) return null;
        return unsettled.Select(s => s.Epoch!).Concat(prerequisites.Select(a => a.Epoch!)).ToHashSet(StringComparer.Ordinal);
    }

    private const string AccountPrerequisiteKind = "prerequisite.account";
}

public sealed partial class RunStore
{
    internal WorkflowRunRecord? TryPublishExecutionResetRelease(string runId, ExecutionResetReleaseRecord release)
    {
        lock (_gate)
        {
            var run = Load(runId);
            if (run is null || run.RunId != runId) return null;
            if (run.ExecutionResetRelease is not null) return ExecutionResetEvidence.Valid(run) ? run : null;
            if (release.FactsHash != ExecutionResetEvidence.FactsHash(run)) return null;
            run.ExecutionResetRelease = release;
            if (!ExecutionResetEvidence.Valid(run)) return null;
            Persist(run, run.RecordRevision, authorizedReset: release);
            return Load(runId) is { } saved && ExecutionResetEvidence.Valid(saved) ? saved : null;
        }
    }
}
