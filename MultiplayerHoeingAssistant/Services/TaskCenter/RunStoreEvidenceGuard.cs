using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class RunStoreEvidenceGuard
{
    private static void Require(bool condition)
    {
        if (!condition) throw new RunRecordConflictException("原冻结身份、耐久终态、退出或收尾历史不能被删除、替换或回退。");
    }

    private static void Facts(string? raw, bool exit, string? job, string? newRaw, bool newExit, string? newJob)
    {
        Require(!BgiJobTerminalPolling.IsTerminal(raw) || raw == newRaw);
        Require(!exit || newExit);
        Require(string.IsNullOrEmpty(job) || job == newJob);
    }

    private static bool Frozen(BgiWorkflowObservationPersistence.Binding binding)
        => binding.Identity.Complete && !string.IsNullOrEmpty(binding.Fingerprint);

    internal static void Validate(WorkflowRunRecord current, WorkflowRunRecord next, LocalNoSendProof? authorizedNoSend = null)
    {
        if (current.CurrentSubmission?.LocalNoSendProof is { } priorNoSend)
        {
            Require(next.CurrentSubmission?.LocalNoSendProof == priorNoSend);
            Require(LocalNoSendEvidence.IsDischarged(next, next.CurrentSubmission!));
        }
        else if (next.CurrentSubmission?.LocalNoSendProof is { } newNoSend)
        {
            Require(newNoSend == authorizedNoSend && LocalNoSendEvidence.IsDischarged(next, next.CurrentSubmission));
        }
        var hasFrozen = current.PrerequisiteActions.Any(a => a.SendAttempted && Frozen(BgiWorkflowObservationPersistence.Binding.Freeze(a)))
            || current.PendingCompletion is { SendAttempted: true } completion && Frozen(BgiWorkflowObservationPersistence.Binding.Freeze(completion))
            || current.CurrentSubmission is { SendAttempted: true, Epoch: not null, Fingerprint: not null };
        Require(!hasFrozen || current.WireRunId == next.WireRunId);

        if (current.CurrentSubmission is { } previous && previous.SendAttempted && !string.IsNullOrEmpty(previous.Epoch)
            && !string.IsNullOrEmpty(previous.Fingerprint))
        {
            var live = next.CurrentSubmission;
            if (live is null || live.Key != previous.Key)
                Require(previous.ExecutionExitConfirmed && BgiJobTerminalPolling.IsTerminal(previous.ObservedTerminal));
            else
            {
                Require(live.Epoch == previous.Epoch && live.NodeId == previous.NodeId && live.Occurrence == previous.Occurrence
                    && live.LoopIteration == previous.LoopIteration && live.Attempt == previous.Attempt
                    && live.Fingerprint == previous.Fingerprint && live.ExpiresAtUtc == previous.ExpiresAtUtc
                    && live.SendAttempted && (string.IsNullOrEmpty(previous.AcceptedSendIdentity) || live.AcceptedSendIdentity == previous.AcceptedSendIdentity));
                Facts(previous.ObservedTerminal, previous.ExecutionExitConfirmed, previous.JobId,
                    live.ObservedTerminal, live.ExecutionExitConfirmed, live.JobId);
            }
        }

        foreach (var priorAction in current.PrerequisiteActions)
        {
            var binding = BgiWorkflowObservationPersistence.Binding.Freeze(priorAction);
            if (!priorAction.SendAttempted || !Frozen(binding)) continue;
            var matches = next.PrerequisiteActions.Where(a => BgiWorkflowObservationPersistence.Binding.Freeze(a) == binding).ToList();
            Require(matches.Count == 1);
            var live = matches[0]; Require(live.SendAttempted);
            Facts(priorAction.ObservedTerminal, priorAction.ExecutionExitConfirmed, priorAction.JobId,
                live.ObservedTerminal, live.ExecutionExitConfirmed, live.JobId);
            Require(!priorAction.ExecutionExitConfirmed || priorAction.ExecutionExitDisposition == live.ExecutionExitDisposition);
            Require(priorAction.EffectState is null or "unknown" || priorAction.EffectState == live.EffectState);
            Require(priorAction.ServerRejectionEvidence is null || priorAction.ServerRejectionEvidence == live.ServerRejectionEvidence);
        }

        if (current.PendingCompletion is { SendAttempted: true } pending)
        {
            var binding = BgiWorkflowObservationPersistence.Binding.Freeze(pending);
            if (Frozen(binding))
            {
                var live = next.PendingCompletion;
                if (live is null || BgiWorkflowObservationPersistence.Binding.Freeze(live) != binding)
                {
                    var matches = next.CompletionHistory.Where(c => BgiWorkflowObservationPersistence.Binding.Freeze(c) == binding).ToList();
                    Require(matches.Count == 1); live = matches[0];
                    // Exit/effect may be first published in the same atomic move to history.
                    Require(live.ExecutionExitConfirmed && BgiJobTerminalPolling.IsTerminal(live.ObservedTerminal)
                        && live.EffectState is not (null or "unknown"));
                }
                Require(live.SendAttempted);
                Facts(pending.ObservedTerminal, pending.ExecutionExitConfirmed, pending.JobId,
                    live.ObservedTerminal, live.ExecutionExitConfirmed, live.JobId);
                Require(!pending.ExecutionExitConfirmed || pending.ExecutionExitDisposition == live.ExecutionExitDisposition);
                Require(pending.EffectState is null or "unknown" || pending.EffectState == live.EffectState);
                Require(pending.ServerRejectionEvidence is null || pending.ServerRejectionEvidence == live.ServerRejectionEvidence);
            }
        }

        // Full records, including extension facts, are immutable once moved into durable completion history.
        var retained = next.CompletionHistory.Select(c => JsonSerializer.Serialize(c)).ToList();
        foreach (var history in current.CompletionHistory)
        {
            var encoded = JsonSerializer.Serialize(history);
            Require(retained.Remove(encoded));
        }
    }
}
