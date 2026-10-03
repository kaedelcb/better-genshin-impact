using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

// The original wire identity and payload are fixed before any await. Only owned observation fields merge.
internal static class BgiWorkflowObservationPersistence
{
    internal sealed record Binding(BgiJobTerminalPolling.FrozenIdentity Identity, string Fingerprint,
        string? Ticket, string Expires, int StrategyIndex, string Kind, string? AccountKey, string? ActionId, string? Action)
    {
        internal static Binding Freeze(PrerequisiteActionRecord r) => new(new(r.Epoch, r.IdempotencyKey, r.WireRunId,
            r.NodeId, r.LoopIteration, r.Occurrence, r.Attempt), r.Fingerprint, r.TakeoverTicket, r.ExpiresAtUtc,
            r.StrategyIndex, r.Kind, r.AccountKey, null, null);
        internal static Binding Freeze(PendingCompletionRecord r) => new(new(r.Epoch, r.IdempotencyKey, r.WireRunId,
            "$flow", 0, r.Occurrence, r.Attempt), r.Fingerprint, r.TakeoverTicket, r.ExpiresAtUtc, 0, r.Kind, null, r.ActionId, r.Action);
    }

    internal static WorkflowRunRecord? FindOwner(RunStore runs, Binding binding, bool completion)
    {
        var snapshot = runs.ListWithIntegrity("read-adapter-owner");
        if (snapshot.UnknownFiles.Count != 0) return null;
        var owners = snapshot.Records.Where(r => r.WireRunId == binding.Identity.WireRunId && (completion
            ? r.PendingCompletion is { } c && Binding.Freeze(c) == binding
            : r.PrerequisiteActions.Count(a => Binding.Freeze(a) == binding) == 1)).ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    private static void CheckJob(string? currentJob, string? frozenJob, string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !string.IsNullOrEmpty(currentJob) && currentJob != jobId
            || !string.IsNullOrEmpty(frozenJob) && frozenJob != jobId)
            throw new RunRecordConflictException("原作业绑定冲突，拒绝覆盖或取消其他作业。");
    }

    private static (string? Raw, bool Exit, string? Disposition, string? Effect) Merge(
        string? raw, bool exit, string? disposition, string? effect, BgiJobInfo? job)
    {
        if (job is null) return (raw, exit, disposition, effect);
        if (BgiJobTerminalPolling.IsTerminal(raw))
        {
            if (BgiJobTerminalPolling.IsTerminal(job.State) && raw != job.State)
                throw new RunRecordConflictException("原业务终态冲突，拒绝改写已读事实。");
            if (job.State != raw) return (raw, exit, disposition, effect);
        }
        var confirmed = job.ExecutionExitConfirmed && job.ExecutionExitDisposition is "execution_exited" or "never_started";
        return (BgiJobTerminalPolling.IsTerminal(job.State) || job.State == "result_unknown" ? job.State : raw,
            exit || confirmed, exit ? disposition : job.ExecutionExitDisposition,
            confirmed && BgiJobTerminalPolling.IsTerminal(job.State) ? job.State : effect ?? "unknown");
    }

    internal static void Save(RunStore runs, WorkflowRunRecord run, PrerequisiteActionRecord record,
        Binding binding, string? frozenJob, string jobId, BgiJobInfo? job = null)
    {
        if (Binding.Freeze(record) != binding || run.WireRunId != binding.Identity.WireRunId)
            throw new RunRecordConflictException("观察期间前置冻结身份改变。");
        var applied = runs.UpdateMergingIf(run.RunId, latest =>
        {
            if (latest.WireRunId != binding.Identity.WireRunId) return false;
            var matches = latest.PrerequisiteActions.Where(a => Binding.Freeze(a) == binding).ToList();
            if (matches.Count != 1) return false;
            var live = matches[0]; CheckJob(live.JobId, frozenJob, jobId);
            if (job is not null && !binding.Identity.Matches(job)) return false;
            (live.ObservedTerminal, live.ExecutionExitConfirmed, live.ExecutionExitDisposition, live.EffectState) =
                Merge(live.ObservedTerminal, live.ExecutionExitConfirmed, live.ExecutionExitDisposition, live.EffectState, job);
            live.JobId = jobId;
            return true;
        }, out var merged);
        if (!applied || merged is null) throw new RunRecordConflictException("前置原身份不在最新记录中，未发布观察事实。");
        var saved = merged.PrerequisiteActions.Single(a => Binding.Freeze(a) == binding);
        Copy(saved, record); merged.PrerequisiteActions[merged.PrerequisiteActions.IndexOf(saved)] = record;
        RunStore.RebaseOnto(run, merged);
    }

    internal static void Save(RunStore runs, WorkflowRunRecord run, PendingCompletionRecord record,
        Binding binding, string? frozenJob, string jobId, BgiJobInfo? job = null)
    {
        if (Binding.Freeze(record) != binding || run.WireRunId != binding.Identity.WireRunId)
            throw new RunRecordConflictException("观察期间收尾冻结身份改变。");
        var applied = runs.UpdateMergingIf(run.RunId, latest =>
        {
            if (latest.WireRunId != binding.Identity.WireRunId || latest.PendingCompletion is not { } live
                || Binding.Freeze(live) != binding) return false;
            CheckJob(live.JobId, frozenJob, jobId);
            if (job is not null && !binding.Identity.Matches(job)) return false;
            (live.ObservedTerminal, live.ExecutionExitConfirmed, live.ExecutionExitDisposition, live.EffectState) =
                Merge(live.ObservedTerminal, live.ExecutionExitConfirmed, live.ExecutionExitDisposition, live.EffectState, job);
            live.JobId = jobId;
            return true;
        }, out var merged);
        if (!applied || merged?.PendingCompletion is not { } saved)
            throw new RunRecordConflictException("收尾原身份不在最新记录中，未发布观察事实。");
        Copy(saved, record); merged.PendingCompletion = record;
        RunStore.RebaseOnto(run, merged);
    }

    internal static void Save(RunStore runs, WorkflowRunRecord run, WorkflowSubmission record,
        BgiJobTerminalPolling.FrozenIdentity identity, string? fingerprint, string jobId, BgiJobInfo job)
    {
        bool Matches(WorkflowRunRecord owner, WorkflowSubmission r) => owner.WireRunId == identity.WireRunId
            && r.Epoch == identity.Epoch && r.Key == identity.Key && r.NodeId == identity.NodeId
            && r.LoopIteration == identity.Iteration && r.Occurrence == identity.Occurrence && r.Attempt == identity.Attempt
            && r.Fingerprint == fingerprint;
        if (!Matches(run, record) || record.JobId != jobId || !identity.Matches(job))
            throw new RunRecordConflictException("观察期间主体原身份改变。");
        var applied = runs.UpdateMergingIf(run.RunId, latest =>
        {
            if (latest.CurrentSubmission is not { } live || !Matches(latest, live)) return false;
            CheckJob(live.JobId, jobId, jobId);
            var merged = Merge(live.ObservedTerminal, live.ExecutionExitConfirmed, null, null, job);
            live.ObservedTerminal = merged.Raw;
            live.ExecutionExitConfirmed = merged.Exit;
            if (merged.Exit)
            {
                live.ExecutionExitDisposition ??= job.ExecutionExitDisposition;
                live.EffectState = merged.Raw;
                live.WireRunId ??= identity.WireRunId;
            }
            return true;
        }, out var saved);
        if (!applied || saved?.CurrentSubmission is not { } submission)
            throw new RunRecordConflictException("主体原身份不在最新记录中，未发布观察事实。");
        Copy(submission, record); saved.CurrentSubmission = record;
        RunStore.RebaseOnto(run, saved);
    }

    private static void Copy<T>(T source, T target) where T : class
    {
        foreach (var property in typeof(T).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(target, property.GetValue(source));
    }
}
