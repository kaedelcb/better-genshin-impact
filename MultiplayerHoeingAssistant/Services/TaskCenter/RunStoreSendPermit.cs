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
            if (current.CurrentSubmission is not { SendPermit: null } || !apply(current)) return false;
            current.CurrentSubmission.SendPermit = permit;
            return true;
        }, out latest, permit);
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
