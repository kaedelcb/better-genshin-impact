using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>本端口调用前的本地证明；不携带远端业务终态或执行退出事实。</summary>
public sealed record LocalNoSendProof(
    string Kind, string ConsumptionId, string RunId, string WireRunId, string Epoch, string Key,
    string NodeId, int Occurrence, int LoopIteration, int Attempt, string Fingerprint, string ExpiresAtUtc,
    WorkflowStopAuthorityRecord StopAuthority,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TransportEvidence = null);

internal static class LocalNoSendEvidence
{
    internal const string PreparedStop = "prepared_stop_before_port_call_v1";
    internal const string TransportNotSent = "transport_proven_no_bytes_v1";

    internal static bool Matches(WorkflowRunRecord run, WorkflowSubmission submission, LocalNoSendProof proof)
        => (proof.Kind == PreparedStop && run.StopRequested && proof.TransportEvidence is null
            || proof.Kind == TransportNotSent && BgiNotSentException.IsKnownEvidenceCode(proof.TransportEvidence!))
           && Guid.TryParseExact(proof.ConsumptionId, "N", out _) && run.StopAuthority == proof.StopAuthority
           && (submission.SendPermit is null || submission.SendPermit is { Version: 1, Consumed: true } permit && permit.Nonce == proof.ConsumptionId)
           && !string.IsNullOrEmpty(proof.RunId) && run.RunId == proof.RunId
           && !string.IsNullOrEmpty(proof.WireRunId) && run.WireRunId == proof.WireRunId
           && !string.IsNullOrEmpty(proof.Epoch) && submission.Epoch == proof.Epoch
           && proof.StopAuthority.Epoch == proof.Epoch
           && !string.IsNullOrEmpty(proof.Key) && submission.Key == proof.Key
           && !string.IsNullOrEmpty(proof.NodeId) && submission.NodeId == proof.NodeId
           && submission.Occurrence == proof.Occurrence && submission.LoopIteration == proof.LoopIteration
           && submission.Attempt == proof.Attempt && proof.Attempt > 0
           && !string.IsNullOrEmpty(proof.Fingerprint) && submission.Fingerprint == proof.Fingerprint
           && !string.IsNullOrEmpty(proof.ExpiresAtUtc) && submission.ExpiresAtUtc == proof.ExpiresAtUtc
           && submission.SendAttempted && submission.Intent is SubmitIntentState.Submitted or SubmitIntentState.Rejected
           && string.IsNullOrEmpty(submission.JobId) && string.IsNullOrEmpty(submission.AcceptedSendIdentity)
           && submission.ObservedTerminal is null && !submission.ExecutionExitConfirmed
           && submission.ServerRejectionEvidence is null;

    internal static bool IsDischarged(WorkflowRunRecord run, WorkflowSubmission submission)
        => submission.LocalNoSendProof is { } proof && submission.Intent == SubmitIntentState.Rejected
           && Matches(run, submission, proof);
}
