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

    private static bool SameCursor(WorkflowRunRecord a, WorkflowRunRecord b)
        => a.Cursor?.NodeId == b.Cursor?.NodeId && a.Cursor?.Occurrence == b.Cursor?.Occurrence
           && a.Cursor?.LoopIteration == b.Cursor?.LoopIteration && a.Cursor?.Attempt == b.Cursor?.Attempt
           && a.TailReached == b.TailReached;

    private static bool Frozen(BgiWorkflowObservationPersistence.Binding binding)
        => binding.Identity.Complete && !string.IsNullOrEmpty(binding.Fingerprint);

    internal static void Validate(WorkflowRunRecord current, WorkflowRunRecord next, LocalNoSendProof? authorizedNoSend = null, TerminalReleaseSeal? authorizedSeal = null, RecoveryAssociationRecord? authorizedRecoveryAssociation = null, PreparedSendPermit? authorizedPermit = null)
    {
        Require(current.AdmissionSourceScope == next.AdmissionSourceScope);
        Require(current.AdmissionParentSource == next.AdmissionParentSource);
        var originalBindings = next.Handoffs.Select(h => JsonSerializer.Serialize(h)).ToList();
        foreach (var binding in current.Handoffs)
            Require(originalBindings.Remove(JsonSerializer.Serialize(binding)));
        if (current.AdmissionParentSource is { } parent)
            Require(parent.MatchesHandoff(next));
        var oldPermit = current.CurrentSubmission?.SendPermit;
        var nextPermit = next.CurrentSubmission?.SendPermit;
        var renewing = authorizedPermit is { Version: 1, Consumed: false }
            && current.CurrentSubmission is { } renewalSource
            && RunStore.CanRenewPreparedSubmission(current, renewalSource, authorizedPermit.OriginalSendIdentity)
            && nextPermit == authorizedPermit;
        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key)
        {
            if (oldPermit != nextPermit)
                Require(nextPermit == authorizedPermit && nextPermit is { Version: 1 }
                    && Guid.TryParseExact(nextPermit.Nonce, "N", out _)
                    && (oldPermit is null && current.CurrentSubmission?.SendAttempted != true && !nextPermit.Consumed
                        || oldPermit is { Version: 1, Consumed: false } && nextPermit == oldPermit with { Consumed = true }
                        || renewing));
        }
        else Require(nextPermit is null); // New identities receive permits only through prepare.
        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key && current.CurrentSubmission is { } prior && next.CurrentSubmission is { } after)
        {
            var priorRounds = prior.PreviousSendRounds ?? [];
            var nextRounds = after.PreviousSendRounds ?? [];
            Require(nextRounds.Count == priorRounds.Count + (renewing ? 1 : 0));
            for (var index = 0; index < priorRounds.Count; index++) Require(nextRounds[index] == priorRounds[index]);
            if (renewing) Require(nextRounds[^1] == new DischargedNodeSendRound(oldPermit!, prior.LocalNoSendProof!, prior.OriginalRequestEvidence!)
                && after.LocalNoSendProof is null);
        }
        else Require(next.CurrentSubmission?.PreviousSendRounds is null);
        if (oldPermit is not null && current.CurrentSubmission is { } held
            && !TerminalReleaseEvidence.BodySettled(current, held)
            && (next.CurrentSubmission?.Key != held.Key || !TerminalReleaseEvidence.BodySettled(next, next.CurrentSubmission)))
            Require(SameCursor(current, next));

        if (current.TerminalRelease is { } runSeal)
        {
            Require(next.TerminalRelease == runSeal && TerminalReleaseEvidence.ValidRunSeal(next));
            Require(TerminalReleaseEvidence.RunHash(current) == TerminalReleaseEvidence.RunHash(next));
        }
        else if (next.TerminalRelease is { } newRunSeal)
            Require(newRunSeal == authorizedSeal && TerminalReleaseEvidence.ValidRunSeal(next));
        foreach (var seal in current.NodeReleaseSeals)
        {
            Require(next.NodeReleaseSeals.Count(s => s == seal) == 1);
            Require(seal.FactsHash == TerminalReleaseEvidence.NodeHash(next, seal.SubmissionIdentity!, true));
        }
        foreach (var seal in next.NodeReleaseSeals.Where(s => !current.NodeReleaseSeals.Contains(s)))
            Require(seal == authorizedSeal && seal.FactsHash == TerminalReleaseEvidence.NodeHash(next, seal.SubmissionIdentity!, true));
        var submissions = next.SubmissionHistory.Select(s => JsonSerializer.Serialize(s)).ToList();
        foreach (var history in current.SubmissionHistory) Require(submissions.Remove(JsonSerializer.Serialize(history)));
        if (current.CurrentSubmission is { } oldSubmission && next.CurrentSubmission?.Key != oldSubmission.Key)
        {
            Require(submissions.Remove(JsonSerializer.Serialize(oldSubmission)));
            Require(TerminalReleaseEvidence.BodySettled(current, oldSubmission));
        }
        Require(submissions.Count == 0); // Ordinary callbacks cannot manufacture history.

        // Even an old possibly-sent record lacking the legacy local fingerprint must
        // not acquire a caller-fabricated original-request proof during recovery.
        if (current.CurrentSubmission is { SendAttempted: true } frozenOriginal
            && next.CurrentSubmission is { } nextOriginal && nextOriginal.Key == frozenOriginal.Key)
            Require(nextOriginal.OriginalRequestEvidence == frozenOriginal.OriginalRequestEvidence);

        if (!renewing && current.CurrentSubmission?.LocalNoSendProof is { } priorNoSend
            && current.CurrentSubmission.Key == next.CurrentSubmission?.Key)
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
        hasFrozen |= current.SubmissionHistory.Any(s => s.SendAttempted || !string.IsNullOrEmpty(s.JobId));
        Require(!hasFrozen || current.WireRunId == next.WireRunId);

        if (current.CurrentSubmission is { } previous && previous.SendAttempted && !string.IsNullOrEmpty(previous.Epoch)
            && !string.IsNullOrEmpty(previous.Fingerprint))
        {
            var live = next.CurrentSubmission;
            if (live is null || live.Key != previous.Key)
                Require(TerminalReleaseEvidence.BodySettled(current, previous));
            else
            {
                Require(live.Epoch == previous.Epoch && live.NodeId == previous.NodeId && live.Occurrence == previous.Occurrence
                    && live.LoopIteration == previous.LoopIteration && live.Attempt == previous.Attempt
                    && (previous.WireRunId is null || live.WireRunId == previous.WireRunId)
                    && live.Fingerprint == previous.Fingerprint && live.ExpiresAtUtc == previous.ExpiresAtUtc
                    && live.OriginalRequestEvidence == previous.OriginalRequestEvidence
                    && live.SendAttempted && (string.IsNullOrEmpty(previous.AcceptedSendIdentity) || live.AcceptedSendIdentity == previous.AcceptedSendIdentity));
                Facts(previous.ObservedTerminal, previous.ExecutionExitConfirmed, previous.JobId,
                    live.ObservedTerminal, live.ExecutionExitConfirmed, live.JobId);
                Require(!previous.ExecutionExitConfirmed || previous.ExecutionExitDisposition is null || previous.ExecutionExitDisposition == live.ExecutionExitDisposition);
                Require(previous.EffectState is null or "unknown" || previous.EffectState == live.EffectState);
                Require(previous.ServerRejectionEvidence is null || previous.ServerRejectionEvidence == live.ServerRejectionEvidence);
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

        // **追加式恢复关联（G7-residual·本批；只增不删/不改）**：current 已落盘的每条关联必须在 next 中
        // 逐字保留（序列化逐项抵消）；next 允许**追加**新关联（对账取得合法证据后写入），但不得删除或改写既有项。
        var retainedAssociations = next.RecoveryAssociations.Select(a => JsonSerializer.Serialize(a)).ToList();
        foreach (var association in current.RecoveryAssociations)
            Require(retainedAssociations.Remove(JsonSerializer.Serialize(association)));
        if (authorizedRecoveryAssociation is not null)
            Require(retainedAssociations.Remove(JsonSerializer.Serialize(authorizedRecoveryAssociation)));
        Require(retainedAssociations.Count == 0);
    }
}
