from pathlib import Path

def edit(path, transform):
 p=Path(path);b=p.read_bytes();nl='\r\n' if b'\r\n' in b else '\n';s=b.decode('utf-8-sig');s=transform(s,nl);p.write_bytes((b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'')+s.encode())
def rep(s,a,z):
 assert s.count(a)==1,(a,s.count(a));return s.replace(a,z)
edit('MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs',lambda s,nl: rep(s,'    public PreparedSendPermit? SendPermit { get; set; }','    public PreparedSendPermit? SendPermit { get; set; }'+nl+nl+'    [JsonPropertyName("previousSendRounds")]'+nl+'    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]'+nl+'    public List<DischargedNodeSendRound>? PreviousSendRounds { get; set; }')+nl+'''public sealed record DischargedNodeSendRound(PreparedSendPermit Permit,
    MultiplayerHoeingAssistant.Services.LocalNoSendProof Proof, FrozenOriginalRequestEvidence RequestEvidence);
'''.replace('\n',nl))
edit('MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs',lambda s,nl: rep(s,'    [JsonPropertyName("lastResult")] public OperationResult? LastResult { get; set; }','    [JsonPropertyName("lastResult")] public OperationResult? LastResult { get; set; }'+nl+'    [JsonPropertyName("rejectedSendRounds")]'+nl+'    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]'+nl+'    public List<OperationResult>? RejectedSendRounds { get; set; }'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs',lambda s,nl: rep(s,'''            if (current.CurrentSubmission is not { SendPermit: null } || !apply(current)) return false;
            current.CurrentSubmission.SendPermit = permit;'''.replace('\n',nl),'''            if (current.CurrentSubmission is not { } submission) return false;
            var renewal = CanRenewPreparedSubmission(current, submission, originalSendIdentity);
            if (submission.SendPermit is not null && !renewal) return false;
            var previous = renewal ? new DischargedNodeSendRound(submission.SendPermit!, submission.LocalNoSendProof!, submission.OriginalRequestEvidence!) : null;
            if (!apply(current)) return false;
            if (previous is not null)
            {
                (submission.PreviousSendRounds ??= []).Add(previous);
                submission.LocalNoSendProof = null;
            }
            submission.SendPermit = permit;'''.replace('\n',nl)))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs',lambda s,nl: rep(s,'    internal bool TryConsumePreparedSubmission(','''    internal static bool CanRenewPreparedSubmission(WorkflowRunRecord run, WorkflowSubmission submission, string? nextIdentity)
        => !run.StopRequested && submission.SendPermit is { Version: 1, Consumed: true, OriginalSendIdentity: not null } permit
            && Guid.TryParseExact(permit.Nonce, "N", out _) && submission.OriginalRequestEvidence is not null
            && LocalNoSendEvidence.IsDischarged(run, submission)
            && submission.LocalNoSendProof is { Kind: LocalNoSendEvidence.TransportNotSent }
            && NextSendRound(permit.OriginalSendIdentity, nextIdentity);

    internal static bool NextSendRound(string? prior, string? next)
    {
        if (prior is null || next is null || !prior.StartsWith("sub:", StringComparison.Ordinal)) return false;
        var separator = prior.LastIndexOf(':');
        return separator > 4 && int.TryParse(prior[(separator + 1)..], out var seq) && seq > 0 && seq < int.MaxValue
            && prior == prior[..(separator + 1)] + seq.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && next == prior[..(separator + 1)] + (seq + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal bool TryConsumePreparedSubmission('''.replace('\n',nl)))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs',lambda s,nl: rep(rep(rep(s,'        var submission = run.CurrentSubmission;','        var submission = run.CurrentSubmission;'+nl+'        var renewal = submission is not null && RunStore.CanRenewPreparedSubmission(run, submission, originalSendIdentity);'),'            || submission.Intent != SubmitIntentState.IntentRecorded)','            || submission.Intent != SubmitIntentState.IntentRecorded && !renewal)'), '        var frozenExpiresAt = DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");','        var frozenExpiresAt = renewal ? submission.ExpiresAtUtc! : DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs',lambda s,nl: rep(s,'            if (live.Intent != SubmitIntentState.IntentRecorded) return false;   // 合法前态：意图已落盘、尚未提交','            var renewing = RunStore.CanRenewPreparedSubmission(latest, live, originalSendIdentity);'+nl+'            if (live.Intent != SubmitIntentState.IntentRecorded && !renewing) return false;'+nl+'            if (renewing && (live.Epoch != frozenEpoch || live.ExpiresAtUtc != frozenExpiresAt'+nl+'                || live.Fingerprint != fingerprint || live.OriginalRequestEvidence != originalEvidence)) return false;'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',lambda s,nl: rep(s,'        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key)','''        var renewing = authorizedPermit is { Version: 1, Consumed: false }
            && current.CurrentSubmission is { } renewalSource
            && RunStore.CanRenewPreparedSubmission(current, renewalSource, authorizedPermit.OriginalSendIdentity)
            && nextPermit == authorizedPermit;
        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key)'''.replace('\n',nl)))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',lambda s,nl: rep(s,'|| oldPermit is { Version: 1, Consumed: false } && nextPermit == oldPermit with { Consumed = true }));','|| oldPermit is { Version: 1, Consumed: false } && nextPermit == oldPermit with { Consumed = true }'+nl+'                        || renewing));'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',lambda s,nl: rep(s,'        else Require(nextPermit is null); // New identities receive permits only through prepare.','''        else Require(nextPermit is null); // New identities receive permits only through prepare.
        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key && current.CurrentSubmission is { } prior && next.CurrentSubmission is { } after)
        {
            var priorRounds = prior.PreviousSendRounds ?? [];
            var nextRounds = after.PreviousSendRounds ?? [];
            Require(nextRounds.Count == priorRounds.Count + (renewing ? 1 : 0));
            for (var index = 0; index < priorRounds.Count; index++) Require(nextRounds[index] == priorRounds[index]);
            if (renewing) Require(nextRounds[^1] == new DischargedNodeSendRound(oldPermit!, prior.LocalNoSendProof!, prior.OriginalRequestEvidence!)
                && after.LocalNoSendProof is null);
        }
        else Require(next.CurrentSubmission?.PreviousSendRounds is null);'''.replace('\n',nl)))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',lambda s,nl: rep(s,'        if (current.CurrentSubmission?.LocalNoSendProof is { } priorNoSend','        if (!renewing && current.CurrentSubmission?.LocalNoSendProof is { } priorNoSend'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs',lambda s,nl: rep(s,'rec.CurrentSubmission?.SendPermit is not null || rec.CurrentSubmission?.LocalNoSendProof is not null','rec.CurrentSubmission?.SendPermit is not null || rec.CurrentSubmission?.LocalNoSendProof is not null || rec.CurrentSubmission?.PreviousSendRounds is not null').replace('s.SendPermit is not null || s.LocalNoSendProof is not null)))','s.SendPermit is not null || s.LocalNoSendProof is not null || s.PreviousSendRounds is not null)))'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs',lambda s,nl: rep(s,'            var sendSeq = op.LastSendSeq + 1;','''            if (op.OperationType == OperationType.NodeExecution && op.LastSendSeq > 0)
            {
                if (op.LastResult is not { Outcome: OperationOutcome.Rejected, Retryable: true } rejectedRound
                    || rejectedRound.AnsweredSendSeq != op.LastSendSeq || op.ConflictPending || op.AcceptanceClaim is not null)
                    return "node_previous_round_not_discharged";
                var retainedRounds = op.RejectedSendRounds ??= [];
                if (retainedRounds.Count != op.LastSendSeq - 1) return "node_previous_round_evidence_missing";
                retainedRounds.Add(CloneOperationResult(rejectedRound));
            }
            var sendSeq = op.LastSendSeq + 1;'''.replace('\n',nl)))
