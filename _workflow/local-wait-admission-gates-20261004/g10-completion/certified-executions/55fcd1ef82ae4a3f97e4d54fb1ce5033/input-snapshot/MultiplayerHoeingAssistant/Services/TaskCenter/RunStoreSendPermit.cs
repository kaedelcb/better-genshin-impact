using System.Security.Cryptography;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class RunStore
{
    internal bool TryPrepareSubmission(string runId, Func<WorkflowRunRecord, bool> apply,
        out WorkflowRunRecord? latest, string? originalSendIdentity = null)
    {
        var permit = new PreparedSendPermit(1, Guid.NewGuid().ToString("N"), false, originalSendIdentity);
        return UpdateMergingCore(runId, current =>
        {
            if (current.CurrentSubmission is not { } submission) return false;
            var renewal = CanRenewPreparedSubmission(current, submission, originalSendIdentity);
            if (submission.SendPermit is not null && !renewal) return false;
            if (submission.SendPermit is null && (submission.PreviousSendRounds is not null
                || originalSendIdentity is not null && RoundSequence(originalSendIdentity) == 0)) return false;
            var previous = renewal ? new DischargedNodeSendRound(submission.SendPermit!, submission.LocalNoSendProof!, submission.OriginalRequestEvidence!) : null;
            if (!apply(current)) return false;
            if (previous is not null)
            {
                (submission.PreviousSendRounds ??= []).Add(previous);
                submission.LocalNoSendProof = null;
            }
            submission.SendPermit = permit;
            return true;
        }, out latest, permit);
    }

    internal static bool CanRenewPreparedSubmission(WorkflowRunRecord run, WorkflowSubmission submission, string? nextIdentity)
        => !run.StopRequested && submission.SendPermit is { Version: 1, Consumed: true, OriginalSendIdentity: not null } permit
            && Guid.TryParseExact(permit.Nonce, "N", out _) && submission.OriginalRequestEvidence is not null
            && LocalNoSendEvidence.IsDischarged(run, submission)
            && submission.LocalNoSendProof is { Kind: LocalNoSendEvidence.TransportNotSent }
            && PreviousRoundsComplete(run, submission, permit)
            && NextSendRound(permit.OriginalSendIdentity, nextIdentity);

    private static int RoundSequence(string? identity)
    {
        if (identity is null || !identity.StartsWith("sub:", StringComparison.Ordinal)) return 0;
        var separator = identity.LastIndexOf(':');
        return separator > 4 && int.TryParse(identity[(separator + 1)..], out var seq) && seq > 0
            && identity == identity[..(separator + 1)] + seq.ToString(System.Globalization.CultureInfo.InvariantCulture) ? seq : 0;
    }

    private static bool PreviousRoundsComplete(WorkflowRunRecord run, WorkflowSubmission sub, PreparedSendPermit permit)
    {
        var sequence = RoundSequence(permit.OriginalSendIdentity);
        var rounds = sub.PreviousSendRounds ?? [];
        if (sequence < 1 || rounds.Any(r => r is null || r.Permit is null || r.Proof is null || r.RequestEvidence is null)
            || rounds.Count != sequence - 1
            || rounds.Select(r => r.Permit.Nonce).Append(permit.Nonce).Distinct(StringComparer.Ordinal).Count() != sequence) return false;
        var prefix = permit.OriginalSendIdentity![..(permit.OriginalSendIdentity.LastIndexOf(':') + 1)];
        return !rounds.Where((r, i) => r.Permit.OriginalSendIdentity != prefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            || !LocalNoSendEvidence.PriorRoundMatches(run, sub, r)).Any();
    }

    internal static bool NextSendRound(string? prior, string? next)
    {
        if (prior is null || next is null || !prior.StartsWith("sub:", StringComparison.Ordinal)) return false;
        var separator = prior.LastIndexOf(':');
        return separator > 4 && int.TryParse(prior[(separator + 1)..], out var seq) && seq > 0 && seq < int.MaxValue
            && prior == prior[..(separator + 1)] + seq.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && next == prior[..(separator + 1)] + (seq + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal bool TryConsumePreparedSubmission(BgiWorkflowExecutionBoundary.PreparedSubmit prepared,
        out WorkflowRunRecord? latest)
    {
        latest = null;
        if (prepared.Run is not { } run || prepared.Reconcile is not { } identity
            || prepared.FrozenPermit is not { Version: 1, Consumed: false } permit
            || prepared.Payload is null || prepared.FrozenFingerprint is null) return false;
        var raw = JsonSerializer.Serialize(prepared.Payload);
        if (Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant() != prepared.FrozenFingerprint
            || identity.RequestEvidence is not { } evidence
            || BgiOriginalRequestFingerprint.Compute(prepared.Operation, raw) != evidence.Fingerprint) return false;
        var consumed = permit with { Consumed = true };
        lock (_gate)
        {
            var applied = UpdateMergingCore(run.RunId, current =>
            {
                var s = current.CurrentSubmission;
                if (current.TerminalRelease is not null || s is null || s.SendPermit != permit
                    || current.StopAuthority != prepared.FrozenStopAuthority || current.WireRunId != identity.WireRunId
                    || s.Key != identity.Key || s.Epoch != identity.Epoch || s.NodeId != identity.NodeId
                    || s.Occurrence != identity.Occurrence || s.LoopIteration != identity.LoopIteration || s.Attempt != identity.Attempt
                    || s.Fingerprint != prepared.FrozenFingerprint || s.OriginalRequestEvidence != evidence
                    || s.Intent != SubmitIntentState.Submitted || !s.SendAttempted
                    || !string.IsNullOrEmpty(s.JobId) || !string.IsNullOrEmpty(s.AcceptedSendIdentity)
                    || s.LocalNoSendProof is not null || s.ObservedTerminal is not null || s.ExecutionExitConfirmed
                    || current.Cursor is not { } cursor || cursor.NodeId != identity.NodeId
                    || cursor.Occurrence != identity.Occurrence || cursor.LoopIteration != identity.LoopIteration
                    || cursor.Attempt != identity.Attempt) return false;
                s.SendPermit = consumed;
                return true;
            }, out var published, consumed);
            if (!applied || published is null) return false;
            // A failed/unknown readback never restores an unconsumed permit.
            var readback = Load(run.RunId);
            if (readback?.RecordRevision != published.RecordRevision || readback.CurrentSubmission?.SendPermit != consumed) return false;
            latest = readback;
            return true;
        }
    }
}
