from pathlib import Path
import hashlib,json
r=Path.cwd();d=Path(__file__).parent;p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';b=p.read_bytes();s=b.decode('utf-8')
(d/'admission-before.cs').write_bytes(b)
needle='''                if (archivedMatch is not null)
                {
                    var rehydrateFailure'''.replace('\n','\r\n')
replacement='''                if (archivedMatch is not null)
                {
                    if (archivedMatch.Operation.RequestState == OperationRequestState.TerminalCompleted)
                    {
                        // A settled archive retains its original terminal and audit; replay verifies those
                        // immutable facts instead of reopening a rejected operation or reviving responsibility.
                        if (!SettledArchivedTerminalReplayMatches(read.File!, archivedMatch.Operation, fact))
                            scanFactConflicts++;
                        continue;
                    }
                    var rehydrateFailure'''.replace('\n','\r\n')
assert s.count(needle)==1;s=s.replace(needle,replacement)
needle='    /// <summary>当前所有者把已归档、终局拒绝的操作重新纳入责任热区，以处理之后才到达的旧轮 Accepted 回执。</summary>'
method='''    private bool SettledArchivedTerminalReplayMatches(LogicalOwnerLeaseFile file, OperationRecord op, TakeoverLedgerFact fact)
    {
        if (op.OperationType != OperationType.ExternalStart || op.ConflictPending
            || op.RequestState != OperationRequestState.TerminalCompleted
            || !fact.AcceptedReceipt || !fact.Terminal || fact.OperationType != OperationType.ExternalStart
            || fact.TerminalKind is not (ExecutionResultKind.Succeeded or ExecutionResultKind.Failed or ExecutionResultKind.Cancelled)
            || fact.TerminalObservedAtUtc is not { } observedAt || observedAt == default
            || string.IsNullOrWhiteSpace(fact.JobId) || string.IsNullOrWhiteSpace(fact.RawTerminal)
            || string.IsNullOrWhiteSpace(fact.TerminalEvidenceSource)
            || fact.TerminalKind == ExecutionResultKind.Failed && string.IsNullOrWhiteSpace(fact.ExecutionErrorCode)
            || !IsCanonicalSubmissionIdentity(op.RequestIdentity, fact.SubmissionIdentity, fact.SendSeq)) return false;
        var result = new ExecutionResult
        {
            Kind = fact.TerminalKind.Value, RawTerminal = fact.RawTerminal, ExecutionErrorCode = fact.ExecutionErrorCode,
            JobId = fact.JobId, EvidenceSource = fact.TerminalEvidenceSource, ObservedAtUtc = observedAt,
            SubmissionIdentity = fact.SubmissionIdentity, SendSeq = fact.SendSeq,
        };
        if (!ExecutionSnapshotsEqual(op.ExecutionResult, result)
            || _hooks.TakeoverTerminalPayloadConfirmed?.Invoke(fact.SubmissionIdentity, fact.SendSeq,
                fact.RawTerminal, fact.ExecutionErrorCode, fact.JobId, fact.TerminalEvidenceSource, observedAt, result.Kind) != true)
            return false;
        if (fact.SendSeq == op.LastSendSeq)
            return op.SubmissionIdentity == fact.SubmissionIdentity && op.TakeoverRef == fact.SubmissionIdentity
                && (TerminalFactsConsistent(op) || ArbitrationRetentionPolicy.IsSettledAcceptanceClaim(op));
        if (!HistoricalResolvedExecutionMatchesOperation(op, result)) return false;
        var auditId = DeriveConflictAuditId(op.RequestIdentity, fact.SubmissionIdentity, fact.SendSeq,
            ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal);
        var audits = (file.Handoff?.ConflictResolutionAudits ?? []).Where(a => a.AuditId == auditId).ToList();
        return audits.Count == 1 && audits[0].RequestIdentity == op.RequestIdentity
            && audits[0].SubmissionIdentity == fact.SubmissionIdentity && audits[0].SendSeq == fact.SendSeq
            && audits[0].Resolution == ConflictResolutionKind.ResolvedHistoricalAcceptedTerminal
            && ExecutionSnapshotsEqual(audits[0].ResolutionEvidenceSnapshot, result)
            && audits[0].RelatedCurrentRoundRejectedResultSnapshot is { Outcome: OperationOutcome.Rejected } rejection
            && op.LastResult is { } currentRejection && System.Text.Json.JsonSerializer.Serialize(rejection) == System.Text.Json.JsonSerializer.Serialize(currentRejection);
    }

'''.replace('\n','\r\n')
assert s.count(needle)==1;s=s.replace(needle,method+needle);after=s.encode('utf-8');p.write_bytes(after)
(d/'source-edit-observation.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before=dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest()),after=dict(bytes=len(after),lines=len(after.splitlines()),sha256=hashlib.sha256(after).hexdigest())),indent=2),encoding='utf-8');print(len(b),len(after))
