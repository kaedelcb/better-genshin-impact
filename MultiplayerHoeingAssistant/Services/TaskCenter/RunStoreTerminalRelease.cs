using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record TerminalReleaseSeal(string Id, string Scope, string RunId, string WireRunId,
    int RecordRevision, string FactsHash, string? SubmissionIdentity);

internal static class TerminalReleaseEvidence
{
    internal static string Hash(object facts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(facts))));
    internal static bool BodySettled(WorkflowRunRecord run, WorkflowSubmission s)
    {
        var recovery = HistoricalObservation(run, s);
        if (recovery is not null) return true;
        var sendIdentity = s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity;
        var associations = run.RecoveryAssociations.Where(a => a.SubmissionKey == s.Key
            || !string.IsNullOrEmpty(sendIdentity) && a.SubmissionIdentity == sendIdentity).ToList();
        if (associations.Count > 1 || associations.Any(a => a.ObservedExecution is not null
            || !ValidRecoveryAssociation(run, a))) return false;
        return OriginalBodySettled(run, s);
    }

    private static bool OriginalBodySettled(WorkflowRunRecord run, WorkflowSubmission s)
    {
        if (LocalNoSendEvidence.IsDischarged(run, s)) return true;
        if (!s.SendAttempted && string.IsNullOrEmpty(s.JobId) && string.IsNullOrEmpty(s.AcceptedSendIdentity))
            return s.ObservedTerminal is null && !s.ExecutionExitConfirmed && s.ServerRejectionEvidence is null
                && s.Intent is SubmitIntentState.None or SubmitIntentState.Rejected or SubmitIntentState.LocalWaitDeferred;
        var identity = new BgiJobTerminalPolling.FrozenIdentity(s.Epoch, s.Key, run.WireRunId, s.NodeId,
            s.LoopIteration, s.Occurrence, s.Attempt);
        if (!identity.Complete || s.WireRunId != run.WireRunId || string.IsNullOrEmpty(s.Fingerprint) || string.IsNullOrEmpty(s.ExpiresAtUtc)
            || !s.ExecutionExitConfirmed || s.ExecutionExitDisposition is not ("execution_exited" or "never_started")
            || !BgiJobTerminalPolling.IsTerminal(s.ObservedTerminal) || s.EffectState != s.ObservedTerminal) return false;
        if (!string.IsNullOrEmpty(s.JobId)) return s.ServerRejectionEvidence is null;
        return s.ObservedTerminal == "rejected" && s.ServerRejectionEvidence is { } proof
            && proof.Epoch == s.Epoch && proof.Key == s.Key && proof.Fingerprint == s.Fingerprint
            && proof.Operation == BgiExternalClient.ExternalOperations.TaskStart;
    }

    private static bool ActionSettled(BgiWorkflowObservationPersistence.Binding binding, bool sent, string? job,
        string? raw, bool exit, string? disposition, string? effect, ServerRejectionEvidence? rejection)
    {
        if (!sent && string.IsNullOrEmpty(job)) return raw is null && !exit && rejection is null && effect is null or "not_executed";
        if (!binding.Identity.Complete || string.IsNullOrEmpty(binding.Fingerprint) || string.IsNullOrEmpty(binding.Expires)
            || !exit || !BgiJobTerminalPolling.IsTerminal(raw)) return false;
        if (!string.IsNullOrEmpty(job)) return rejection is null && effect == raw
            && disposition is "execution_exited" or "never_started";
        var operation = binding.ActionId is not null ? BgiExternalClient.ExternalOperations.TerminalCompletionAction
            : binding.Kind switch
            {
                "prerequisite.account" => BgiExternalClient.ExternalOperations.PrerequisiteAccount,
                "prerequisite.redeemCode" => BgiExternalClient.ExternalOperations.PrerequisiteRedeemCode,
                _ => null
            };
        return raw == "rejected" && disposition == "server_rejected_before_acceptance" && effect == "not_executed" && rejection is { } proof
            && proof.Epoch == binding.Identity.Epoch && proof.Key == binding.Identity.Key
            && proof.Fingerprint == binding.Fingerprint && operation is not null && proof.Operation == operation;
    }

    internal static bool PrerequisiteSettled(PrerequisiteActionRecord a) =>
        a.State is PrerequisiteActionState.Succeeded or PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled
        && (a.SendAttempted || !string.IsNullOrEmpty(a.JobId) || a.State is PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled)
        && ActionSettled(BgiWorkflowObservationPersistence.Binding.Freeze(a), a.SendAttempted, a.JobId,
            a.ObservedTerminal, a.ExecutionExitConfirmed, a.ExecutionExitDisposition, a.EffectState, a.ServerRejectionEvidence);
    internal static bool PrerequisiteSettled(WorkflowRunRecord r, PrerequisiteActionRecord a) =>
        (!a.SendAttempted && string.IsNullOrEmpty(a.JobId) || a.WireRunId == r.WireRunId) && PrerequisiteSettled(a);
    internal static bool CompletionSettled(PendingCompletionRecord c) => c.State is "executed" or "cancelled" or "failed" or "rejected"
        && (c.SendAttempted || !string.IsNullOrEmpty(c.JobId))
        && (c.ObservedTerminal != "succeeded" || !Mistletoe.Shared.TerminalEffectJournal.NeedsIndependentEffect(c.Action)
            || ValidIndependentCompletion(c))
        && ActionSettled(BgiWorkflowObservationPersistence.Binding.Freeze(c), c.SendAttempted, c.JobId,
            c.ObservedTerminal, c.ExecutionExitConfirmed, c.ExecutionExitDisposition, c.EffectState, c.ServerRejectionEvidence);
    internal static bool CompletionSettled(WorkflowRunRecord r, PendingCompletionRecord c) => c.WireRunId == r.WireRunId && CompletionSettled(c);

    internal static bool ValidIndependentCompletion(PendingCompletionRecord c)
    {
        try
        {
            var proof = Newtonsoft.Json.JsonConvert.DeserializeObject<Mistletoe.Shared.TerminalEffectProof>(c.TerminalEffectProofJson ?? "null");
            return proof is not null && Mistletoe.Shared.TerminalEffectJournal.ProofMatches(proof, c.TerminalEffectToken!, c.Epoch,
                c.JobId, c.IdempotencyKey, c.WireRunId, c.Action, c.TerminalRequestFingerprint);
        }
        catch { return false; }
    }

    internal static IEnumerable<WorkflowSubmission> Submissions(WorkflowRunRecord r) =>
        r.SubmissionHistory.Concat(r.CurrentSubmission is { } s ? new[] { s } : Array.Empty<WorkflowSubmission>());

    internal static bool UniqueOriginalOutcomeSet(WorkflowRunRecord run, WorkflowSubmission submission, string sendIdentity)
    {
        if (string.IsNullOrEmpty(submission.Key)
            || Submissions(run).Count(s => s.Key == submission.Key) != 1
            || !string.IsNullOrEmpty(sendIdentity) && Submissions(run).Count(s => s.AcceptedSendIdentity == sendIdentity
                || s.SendPermit?.OriginalSendIdentity == sendIdentity) > 1) return false;
        var associations = run.RecoveryAssociations.Where(a => a.SubmissionKey == submission.Key
            || !string.IsNullOrEmpty(sendIdentity) && a.SubmissionIdentity == sendIdentity).ToList();
        if (associations.Count > 1 || associations.Any(a => a.SubmissionKey != submission.Key
            || a.SubmissionIdentity != sendIdentity)) return false;
        var related = run.NodeOutcomes.Where(o => o.SubmissionKey == submission.Key
            || !string.IsNullOrEmpty(sendIdentity) && o.AcceptedSendIdentity == sendIdentity).ToList();
        return related.Count <= 1 && related.All(o => o.SubmissionKey == submission.Key
            && (string.IsNullOrEmpty(o.AcceptedSendIdentity) || o.AcceptedSendIdentity == sendIdentity)
            && o.NodeId == submission.NodeId && o.Occurrence == submission.Occurrence
            && o.LoopIteration == submission.LoopIteration && o.Attempt == submission.Attempt
            && (!BgiJobTerminalPolling.IsTerminal(submission.ObservedTerminal)
                || !BgiJobTerminalPolling.IsTerminal(o.RawTerminal) || o.RawTerminal == submission.ObservedTerminal)
            && (!BgiJobTerminalPolling.IsTerminal(o.RawTerminal) || OutcomeResultMatches(o, o.RawTerminal, run.StopRequested)));
    }

    private static bool OutcomeResultMatches(WorkflowNodeOutcome outcome, string? terminal, bool allowUnknown) =>
        outcome.Result == terminal || outcome.Result == "skippedUser" && terminal == "cancelled"
        || outcome.Result == "skippedFilter" && terminal == "skipped"
        || allowUnknown && outcome.Result is "unknown" or "cancelUnconfirmed";

    internal static RecoveryAssociationRecord? HistoricalObservation(WorkflowRunRecord run, WorkflowSubmission submission)
    {
        var matches = run.RecoveryAssociations.Where(a => a.ObservedExecution is not null
            && a.SubmissionKey == submission.Key && a.HistoryHash == Hash(submission)
            && ValidRecoveryAssociation(run, a)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool ValidHistoricalExecution(WorkflowRunRecord run, WorkflowSubmission sub, RecoveryAssociationRecord association)
    {
        var job = association.ObservedExecution;
        var identity = new BgiJobTerminalPolling.FrozenIdentity(sub.Epoch, sub.Key, run.WireRunId,
            sub.NodeId, sub.LoopIteration, sub.Occurrence, sub.Attempt);
        return job is not null && run.StopRequested && association.EvidenceSource == "host:historical_original_exit"
            && identity.Complete && identity.Matches(job)
            && WorkflowStopAuthority.Epoch(job.Epoch) == sub.Epoch && job.JobId == sub.JobId
            && BgiWorkflowExecutionBoundary.OriginalRequestMatches(job, sub.OriginalRequestEvidence)
            && sub.SendPermit is { Version: 1, Consumed: true } permit
            && Guid.TryParseExact(permit.Nonce, "N", out _) && permit.OriginalSendIdentity == association.SubmissionIdentity
            && !string.IsNullOrEmpty(sub.Fingerprint) && !string.IsNullOrEmpty(sub.ExpiresAtUtc)
            && BgiJobTerminalPolling.IsTerminal(job.State) && job.ExecutionExitConfirmed
            && job.ExecutionExitDisposition is "execution_exited" or "never_started"
            && (!BgiJobTerminalPolling.IsTerminal(sub.ObservedTerminal) || sub.ObservedTerminal == job.State)
            && (!sub.ExecutionExitConfirmed || sub.ExecutionExitDisposition == job.ExecutionExitDisposition)
            && (sub.EffectState is null or "unknown" || sub.EffectState == job.State);
    }


    /// <summary>[G7-residual·本批] 按完整发送身份定位提交：**直接匹配优先**（`AcceptedSendIdentity == sendIdentity`，
    /// 已封印记录路径不变）；仅当直接匹配 **0 命中** 时，才查追加式恢复关联——唯一 `SubmissionIdentity == sendIdentity`
    /// 且证据完备（非空 JobId/EvidenceSource）的关联，按其 SubmissionKey 定位一条**确实缺身份**的历史提交并视同绑定。
    /// 直接匹配多命中、关联缺失/多命中、关联指向已有身份的提交（歧义）一律 null（保守，绝不改历史原件）。</summary>
    internal static WorkflowSubmission? ResolveSubmissionBySendIdentity(WorkflowRunRecord r, string sendIdentity)
    {
        var direct = Submissions(r).Where(s => s.AcceptedSendIdentity == sendIdentity).ToList();
        if (direct.Count == 1) return direct[0];
        if (direct.Count > 1) return null;
        var links = r.RecoveryAssociations.Where(a => string.Equals(a.SubmissionIdentity, sendIdentity, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(a.JobId) && !string.IsNullOrEmpty(a.EvidenceSource)
            && !string.IsNullOrEmpty(a.SubmissionKey)).ToList();
        if (links.Count != 1) return null;
        var byKey = Submissions(r).Where(s => string.Equals(s.Key, links[0].SubmissionKey, StringComparison.Ordinal)).ToList();
        // 关联只能补「缺身份」的历史提交；目标已有另一身份（或身份冲突）＝歧义，保守拒绝。
        return byKey.Count == 1 && ValidRecoveryAssociation(r, links[0]) ? byKey[0] : null;
    }

    internal static bool ValidRecoveryAssociation(WorkflowRunRecord run, RecoveryAssociationRecord association)
    {
        var identity = association.SubmissionIdentity;
        var separator = identity.LastIndexOf(':');
        if (!identity.StartsWith("sub:", StringComparison.Ordinal) || separator <= 4
            || !int.TryParse(identity[(separator + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0 || sequence != association.SendSeq
            || association.HistoryIndex < 0 || association.HistoryIndex >= run.SubmissionHistory.Count
            || string.IsNullOrWhiteSpace(association.EvidenceSource) || association.ObservedAtUtc == default)
            return false;
        var submission = run.SubmissionHistory[association.HistoryIndex];
        if (string.IsNullOrEmpty(submission.Key)
            || !string.IsNullOrEmpty(submission.AcceptedSendIdentity)
                && (association.ObservedExecution is null || submission.AcceptedSendIdentity != identity)
            || association.SubmissionKey != submission.Key || association.HistoryHash != Hash(submission)
            || string.IsNullOrEmpty(submission.JobId) || association.JobId != submission.JobId
            || string.IsNullOrEmpty(submission.Epoch) || association.Epoch != submission.Epoch
            || submission.WireRunId != run.WireRunId || !submission.SendAttempted
            || !UniqueOriginalOutcomeSet(run, submission, identity)) return false;
        var outcomes = run.NodeOutcomes.Select((outcome, index) => (outcome, index))
            .Where(item => item.outcome.SubmissionKey == submission.Key).ToList();
        if (association.ObservedExecution is not null && !ValidHistoricalExecution(run, submission, association)) return false;
        if (outcomes.Count == 0)
            return association.OutcomeIndex == -1 && association.OutcomeHash is null
                && (association.ObservedExecution is not null && run.StopRequested
                    || run.State == WorkflowRunState.Cancelled && run.StopRequested && OriginalBodySettled(run, submission));
        if (outcomes.Count != 1 || outcomes[0].index != association.OutcomeIndex) return false;
        var outcome = outcomes[0].outcome;
        return association.OutcomeHash == Hash(outcome)
            && (string.IsNullOrEmpty(outcome.AcceptedSendIdentity) || outcome.AcceptedSendIdentity == identity)
            && outcome.NodeId == submission.NodeId && outcome.Occurrence == submission.Occurrence
            && outcome.LoopIteration == submission.LoopIteration && outcome.Attempt == submission.Attempt
            && (association.ObservedExecution is { } observed
                ? (outcome.RawTerminal is null or "result_unknown" || outcome.RawTerminal == observed.State)
                    && (outcome.Result is "unknown" or "cancelUnconfirmed"
                        || outcome.Result == observed.State || outcome.Result == "skippedUser" && observed.State == "cancelled"
                        || outcome.Result == "skippedFilter" && observed.State == "skipped")
                : outcome.RawTerminal == submission.ObservedTerminal);
    }
    private static bool HistoricalWaitSettled(WorkflowRunRecord r, WorkflowNodeOutcome wait)
    {
        var index = r.NodeOutcomes.IndexOf(wait);
        return r.NodeOutcomes.Skip(index + 1).Any(o => o.NodeId == wait.NodeId && o.Occurrence == wait.Occurrence
            && o.LoopIteration == wait.LoopIteration && (o.RawTerminal is null
                ? o.Result is "skippedFilter" or "skippedUser"
                : o.Result is "succeeded" or "failed" or "rejected" or "skippedUser" or "skippedFilter" or "cancelled"
                    && Submissions(r).Any(s => s.Key == o.SubmissionKey && s.AcceptedSendIdentity == o.AcceptedSendIdentity
                        && s.NodeId == o.NodeId && s.Occurrence == o.Occurrence && s.LoopIteration == o.LoopIteration
                        && s.Attempt == o.Attempt && s.ObservedTerminal == o.RawTerminal && BodySettled(r, s))));
    }

    internal static bool RunSettled(WorkflowRunRecord r) => r.IsTerminal && !string.IsNullOrWhiteSpace(r.RunId)
        && !string.IsNullOrWhiteSpace(r.WireRunId) && (r.PendingCompletion is null || CompletionSettled(r, r.PendingCompletion))
        && r.RecoveryAssociations.All(a => ValidRecoveryAssociation(r, a))
        && Submissions(r).All(s => BodySettled(r, s)
            && (!s.SendAttempted && string.IsNullOrEmpty(s.JobId) && string.IsNullOrEmpty(s.AcceptedSendIdentity)
                || UniqueOriginalOutcomeSet(r, s, s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity
                    ?? r.RecoveryAssociations.FirstOrDefault(a => a.SubmissionKey == s.Key)?.SubmissionIdentity ?? "")))
        && r.PrerequisiteActions.All(a => PrerequisiteSettled(r, a))
        && r.CompletionHistory.All(c => CompletionSettled(r, c))
        && r.NodeOutcomes.All(o => LocalBranchSettled(r, o) || o.Result is "succeeded" or "failed" or "rejected" or "skippedUser" or "skippedFilter" or "cancelled"
            || r.State == WorkflowRunState.Cancelled && r.StopRequested && o.Result is "unknown" or "cancelUnconfirmed"
                && Submissions(r).Any(s => s.NodeId == o.NodeId && s.Occurrence == o.Occurrence && s.LoopIteration == o.LoopIteration
                    && (o.Attempt is null || s.Attempt == o.Attempt) && (o.SubmissionKey is null || s.Key == o.SubmissionKey)
                    && (o.AcceptedSendIdentity is null || s.AcceptedSendIdentity == o.AcceptedSendIdentity)
                    && (o.RawTerminal is null || s.ObservedTerminal == o.RawTerminal
                        || o.RawTerminal == "result_unknown" && HistoricalObservation(r, s) is not null) && BodySettled(r, s))
            || o.Result == WorkflowRunner.LocalWaitResultWord && (HistoricalWaitSettled(r, o)
                || r.State == WorkflowRunState.Cancelled && r.StopRequested
                    && o.RawTerminal is null && o.AcceptedSendIdentity is null
                    && Submissions(r).Any(s => s.NodeId == o.NodeId && s.Occurrence == o.Occurrence && s.LoopIteration == o.LoopIteration
                        && s.Intent == SubmitIntentState.LocalWaitDeferred && !s.SendAttempted && string.IsNullOrEmpty(s.JobId))));

    private static bool LocalBranchSettled(WorkflowRunRecord run, WorkflowNodeOutcome outcome) =>
        outcome.Result is "branchYes" or "branchNo"
        && !string.IsNullOrWhiteSpace(outcome.NodeId)
        && outcome.RawTerminal is null && outcome.SubmissionKey is null
        && outcome.AcceptedSendIdentity is null && outcome.Attempt is null
        && !Submissions(run).Any(s => s.NodeId == outcome.NodeId && s.Occurrence == outcome.Occurrence
            && s.LoopIteration == outcome.LoopIteration);

    internal static string RunHash(WorkflowRunRecord r)
    {
        var copy = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(r))!;
        copy.TerminalRelease = null; copy.RecordRevision = 0; copy.UpdatedAt = default; copy.Note = null;
        var facts = JsonSerializer.Serialize(copy);
        if (copy.RecoveryAssociations.Count == 0) facts = facts.Replace(",\"recoveryAssociations\":[]", "", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(facts)));
    }

    internal static string? NodeHash(WorkflowRunRecord r, string sendIdentity, bool requireSettled)
    {
        var resolved = ResolveSubmissionBySendIdentity(r, sendIdentity);
        if (resolved is null) return null;
        var s = resolved;
        if (!UniqueOriginalOutcomeSet(r, s, sendIdentity)) return null;
        var observation = HistoricalObservation(r, s);
        var association = observation ?? (string.IsNullOrEmpty(s.AcceptedSendIdentity)
            ? r.RecoveryAssociations.Single(a => a.SubmissionIdentity == sendIdentity && ValidRecoveryAssociation(r, a)) : null);
        var outcomes = association is { OutcomeIndex: >= 0 }
            ? new List<WorkflowNodeOutcome> { r.NodeOutcomes[association.OutcomeIndex] }
            : r.NodeOutcomes.Where(o => o.SubmissionKey == s.Key && o.AcceptedSendIdentity == sendIdentity).ToList();
        var prerequisites = r.PrerequisiteActions.Where(a => a.NodeId == s.NodeId && a.Occurrence == s.Occurrence
            && a.LoopIteration == s.LoopIteration && a.Attempt == s.Attempt).ToList();
        var stoppedAndSettled = r.State == WorkflowRunState.Cancelled && r.StopRequested && RunSettled(r);
        if (requireSettled && (!BodySettled(r, s) || !s.SendAttempted || string.IsNullOrEmpty(s.JobId)
            || (outcomes.Count != 1 && !(outcomes.Count == 0 && (stoppedAndSettled || observation is not null && r.StopRequested)))
            || prerequisites.Any(a => !PrerequisiteSettled(r, a)))) return null;
        if (requireSettled && outcomes.Any(o => o.NodeId != s.NodeId || o.Occurrence != s.Occurrence
            || o.LoopIteration != s.LoopIteration || o.Attempt != s.Attempt
            || observation is null && (o.RawTerminal != s.ObservedTerminal
                || o.Result is not ("succeeded" or "failed" or "rejected" or "skippedUser" or "skippedFilter" or "cancelled") && !stoppedAndSettled))) return null;
        return association is null
            ? Hash(new { r.RunId, r.WireRunId, r.StopAuthority, Submission = s, Outcomes = outcomes, Prerequisites = prerequisites })
            : Hash(new { r.RunId, r.WireRunId, r.StopAuthority, Submission = s, Outcomes = outcomes, Prerequisites = prerequisites, Association = association });
    }

    internal static bool ValidRunSeal(WorkflowRunRecord r) => r.TerminalRelease is { Scope: "run" } seal
        && seal.RunId == r.RunId && seal.WireRunId == r.WireRunId
        && (seal.FactsHash == RunHash(r) || r.RecoveryAssociations.Count == 0 && seal.FactsHash == EmptyAssociationRunHash(r))
        && RunSettled(r);

    private static string EmptyAssociationRunHash(WorkflowRunRecord run)
    {
        var copy = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(run))!;
        copy.TerminalRelease = null; copy.RecordRevision = 0; copy.UpdatedAt = default; copy.Note = null;
        return Hash(copy);
    }
    internal static TerminalReleaseSeal? NodeSeal(WorkflowRunRecord r, OperationRecord op)
    {
        if (!op.ResourceRef.StartsWith("node:", StringComparison.Ordinal) || string.IsNullOrEmpty(op.SubmissionIdentity)) return null;
        var seals = r.NodeReleaseSeals.Where(s => s.SubmissionIdentity == op.SubmissionIdentity).ToList();
        if (seals.Count != 1) return null;
        var seal = seals[0];
        var sub = ResolveSubmissionBySendIdentity(r, op.SubmissionIdentity);
        if (sub is null) return null;
        return seal.Scope == "node" && seal.RunId == r.RunId && seal.WireRunId == r.WireRunId
            && seal.FactsHash == NodeHash(r, op.SubmissionIdentity, true)
            && op.SubmissionIdentity == $"sub:{op.RequestIdentity}:{op.LastSendSeq}"
            && op.WireSubmitKey == sub.Key && op.TargetEpoch == sub.Epoch && op.ResourceRef == "node:" + sub.NodeId
            && op.Candidate?.NodeId == sub.NodeId
            && op.Candidate?.Occurrence == sub.Occurrence && op.Candidate?.LoopIteration == sub.LoopIteration
            && op.Candidate?.Attempt == sub.Attempt ? seal : null;
    }
}

public sealed partial class RunStore
{
    internal TerminalReleaseSeal? TrySealTerminalRun(string runId)
    {
        lock (_gate)
        {
            var current = Load(runId);
            if (current is null || current.RunId != runId) return null;
            if (current.TerminalRelease is not null) return TerminalReleaseEvidence.ValidRunSeal(current) ? current.TerminalRelease : null;
            if (!TerminalReleaseEvidence.RunSettled(current)) return null;
            var seal = new TerminalReleaseSeal(Guid.NewGuid().ToString("N"), "run", current.RunId, current.WireRunId,
                current.RecordRevision + 1, TerminalReleaseEvidence.RunHash(current), null);
            current.TerminalRelease = seal;
            Persist(current, current.RecordRevision, authorizedSeal: seal);
            return seal;
        }
    }

    internal TerminalReleaseSeal? TrySealTerminalNode(string runId, OperationRecord op)
    {
        lock (_gate)
        {
            var current = Load(runId);
            if (current is null || current.RunId != runId) return null;
            var existing = TerminalReleaseEvidence.NodeSeal(current, op);
            if (existing is not null) return existing;
            if (current.TerminalRelease is not null || string.IsNullOrEmpty(op.SubmissionIdentity)) return null;
            var hash = TerminalReleaseEvidence.NodeHash(current, op.SubmissionIdentity, true);
            if (hash is null) return null;
            var seal = new TerminalReleaseSeal(Guid.NewGuid().ToString("N"), "node", current.RunId, current.WireRunId,
                current.RecordRevision + 1, hash, op.SubmissionIdentity);
            current.NodeReleaseSeals.Add(seal);
            if (TerminalReleaseEvidence.NodeSeal(current, op) is null) return null;
            Persist(current, current.RecordRevision, authorizedSeal: seal);
            return seal;
        }
    }

    /// <summary>[G7-residual·本批] 对账取得合法证据后**追加**一条恢复关联（CAS 下写入＋读回验证）。
    /// 守卫约束：已落盘关联只增不删/不改；目标历史提交必须**确实缺身份**（有身份走关联＝歧义，拒绝）；
    /// 同一 SubmissionKey 已有关联＝幂等返回既有（冲突身份则拒绝）。绝不修改历史原件、不放宽守卫。</summary>
    internal RecoveryAssociationRecord? TryAppendRecoveryAssociation(string runId, RecoveryAssociationRecord association)
    {
        lock (_gate)
        {
            var current = Load(runId);
            if (current is null || current.RunId != runId) return null;
            association = JsonSerializer.Deserialize<RecoveryAssociationRecord>(JsonSerializer.Serialize(association))!;
            if (!TerminalReleaseEvidence.ValidRecoveryAssociation(current, association)) return null;
            if (string.IsNullOrEmpty(association.SubmissionKey) || string.IsNullOrEmpty(association.SubmissionIdentity)
                || string.IsNullOrEmpty(association.JobId) || string.IsNullOrEmpty(association.EvidenceSource)) return null;
            // 目标历史提交必须存在且确实缺身份（不得给已有身份的提交再造关联）。
            var targets = TerminalReleaseEvidence.Submissions(current)
                .Where(s => string.Equals(s.Key, association.SubmissionKey, StringComparison.Ordinal)).ToList();
            if (targets.Count != 1 || !string.IsNullOrEmpty(targets[0].AcceptedSendIdentity)
                && (association.ObservedExecution is null || targets[0].AcceptedSendIdentity != association.SubmissionIdentity)) return null;
            // 幂等/冲突：同 Key 已有关联——同身份同 jobId 幂等返回，否则冲突拒绝。
            var existing = current.RecoveryAssociations.Where(a => string.Equals(a.SubmissionKey, association.SubmissionKey, StringComparison.Ordinal)).ToList();
            if (existing.Count > 0)
            {
                if (existing.Count != 1) return null;
                var prior = existing[0];
                return TerminalReleaseEvidence.ValidRecoveryAssociation(current, prior)
                    && prior.HistoryIndex == association.HistoryIndex && prior.HistoryHash == association.HistoryHash
                    && prior.OutcomeIndex == association.OutcomeIndex && prior.OutcomeHash == association.OutcomeHash
                    && string.Equals(prior.SubmissionIdentity, association.SubmissionIdentity, StringComparison.Ordinal)
                    && prior.SendSeq == association.SendSeq
                    && string.Equals(prior.JobId, association.JobId, StringComparison.Ordinal)
                    && string.Equals(prior.Epoch, association.Epoch, StringComparison.Ordinal)
                    && TerminalReleaseEvidence.Hash(prior.ObservedExecution!) == TerminalReleaseEvidence.Hash(association.ObservedExecution!)
                    ? prior : null;
            }
            if (current.TerminalRelease is not null) return null;
            current.RecoveryAssociations.Add(association);
            Persist(current, current.RecordRevision, authorizedRecoveryAssociation: association);
            // 读回验证：关联必须已耐久落盘且可被关联分支定位。
            var readback = Load(runId);
            return readback is not null && TerminalReleaseEvidence.ValidRecoveryAssociation(readback, association)
                && readback.RecoveryAssociations.Count(a => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(association)) == 1
                ? association : null;
        }
    }
}
