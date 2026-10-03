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
        && ActionSettled(BgiWorkflowObservationPersistence.Binding.Freeze(c), c.SendAttempted, c.JobId,
            c.ObservedTerminal, c.ExecutionExitConfirmed, c.ExecutionExitDisposition, c.EffectState, c.ServerRejectionEvidence);
    internal static bool CompletionSettled(WorkflowRunRecord r, PendingCompletionRecord c) => c.WireRunId == r.WireRunId && CompletionSettled(c);

    internal static IEnumerable<WorkflowSubmission> Submissions(WorkflowRunRecord r) =>
        r.SubmissionHistory.Concat(r.CurrentSubmission is { } s ? new[] { s } : Array.Empty<WorkflowSubmission>());

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
        && Submissions(r).All(s => BodySettled(r, s)) && r.PrerequisiteActions.All(a => PrerequisiteSettled(r, a))
        && r.CompletionHistory.All(c => CompletionSettled(r, c))
        && r.NodeOutcomes.All(o => o.Result is "succeeded" or "failed" or "rejected" or "skippedUser" or "skippedFilter" or "cancelled"
            || r.State == WorkflowRunState.Cancelled && r.StopRequested && o.Result is "unknown" or "cancelUnconfirmed"
                && Submissions(r).Any(s => s.NodeId == o.NodeId && s.Occurrence == o.Occurrence && s.LoopIteration == o.LoopIteration
                    && (o.Attempt is null || s.Attempt == o.Attempt) && (o.SubmissionKey is null || s.Key == o.SubmissionKey)
                    && (o.AcceptedSendIdentity is null || s.AcceptedSendIdentity == o.AcceptedSendIdentity)
                    && (o.RawTerminal is null || s.ObservedTerminal == o.RawTerminal) && BodySettled(r, s))
            || o.Result == WorkflowRunner.LocalWaitResultWord && (HistoricalWaitSettled(r, o)
                || r.State == WorkflowRunState.Cancelled && r.StopRequested
                    && o.RawTerminal is null && o.AcceptedSendIdentity is null
                    && Submissions(r).Any(s => s.NodeId == o.NodeId && s.Occurrence == o.Occurrence && s.LoopIteration == o.LoopIteration
                        && s.Intent == SubmitIntentState.LocalWaitDeferred && !s.SendAttempted && string.IsNullOrEmpty(s.JobId))));

    internal static string RunHash(WorkflowRunRecord r)
    {
        var copy = JsonSerializer.Deserialize<WorkflowRunRecord>(JsonSerializer.Serialize(r))!;
        copy.TerminalRelease = null; copy.RecordRevision = 0; copy.UpdatedAt = default; copy.Note = null;
        return Hash(copy);
    }

    internal static string? NodeHash(WorkflowRunRecord r, string sendIdentity, bool requireSettled)
    {
        var matches = Submissions(r).Where(s => s.AcceptedSendIdentity == sendIdentity).ToList();
        if (matches.Count != 1) return null;
        var s = matches[0];
        var outcomes = r.NodeOutcomes.Where(o => o.SubmissionKey == s.Key && o.AcceptedSendIdentity == sendIdentity).ToList();
        var prerequisites = r.PrerequisiteActions.Where(a => a.NodeId == s.NodeId && a.Occurrence == s.Occurrence
            && a.LoopIteration == s.LoopIteration && a.Attempt == s.Attempt).ToList();
        var stoppedAndSettled = r.State == WorkflowRunState.Cancelled && r.StopRequested && RunSettled(r);
        if (requireSettled && (!BodySettled(r, s) || !s.SendAttempted || string.IsNullOrEmpty(s.JobId)
            || (outcomes.Count != 1 && !(outcomes.Count == 0 && stoppedAndSettled))
            || prerequisites.Any(a => !PrerequisiteSettled(r, a)))) return null;
        if (requireSettled && outcomes.Any(o => o.NodeId != s.NodeId || o.Occurrence != s.Occurrence
            || o.LoopIteration != s.LoopIteration || o.Attempt != s.Attempt || o.RawTerminal != s.ObservedTerminal
            || o.Result is not ("succeeded" or "failed" or "rejected" or "skippedUser" or "skippedFilter" or "cancelled") && !stoppedAndSettled)) return null;
        return Hash(new { r.RunId, r.WireRunId, r.StopAuthority, Submission = s, Outcomes = outcomes, Prerequisites = prerequisites });
    }

    internal static bool ValidRunSeal(WorkflowRunRecord r) => r.TerminalRelease is { Scope: "run" } seal
        && seal.RunId == r.RunId && seal.WireRunId == r.WireRunId && seal.FactsHash == RunHash(r) && RunSettled(r);
    internal static TerminalReleaseSeal? NodeSeal(WorkflowRunRecord r, OperationRecord op)
    {
        if (!op.ResourceRef.StartsWith("node:", StringComparison.Ordinal) || string.IsNullOrEmpty(op.SubmissionIdentity)) return null;
        var seals = r.NodeReleaseSeals.Where(s => s.SubmissionIdentity == op.SubmissionIdentity).ToList();
        if (seals.Count != 1) return null;
        var seal = seals[0];
        var submissions = Submissions(r).Where(s => s.AcceptedSendIdentity == op.SubmissionIdentity).ToList();
        if (submissions.Count != 1) return null;
        var sub = submissions[0];
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
}
