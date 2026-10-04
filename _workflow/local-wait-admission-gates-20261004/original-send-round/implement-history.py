from pathlib import Path

def edit(path,f):
 p=Path(path);b=p.read_bytes();nl='\r\n' if b'\r\n' in b else '\n';s=f(b.decode('utf-8-sig'),nl);p.write_bytes((b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'')+s.encode())
def rep(s,a,z):
 assert s.count(a)==1,(a,s.count(a));return s.replace(a,z)
def history(s,nl):
 a='''    public async Task<string?> ReconcileHistoricalSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        try
        {
            var identity'''.replace('\n',nl)
 z='''    public Task<string?> ReconcileHistoricalSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => ReconcileHistoricalSubmissionAsync(run, submission, ct, submission.SendPermit?.OriginalSendIdentity);

    public async Task<string?> ReconcileHistoricalSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct, string? originalSendIdentity)
    {
        try
        {
            if (run.TerminalRelease is not null || originalSendIdentity is not { Length: > 0 }
                || submission.SendPermit is not { Version: 1, Consumed: true } permit
                || !Guid.TryParseExact(permit.Nonce, "N", out _) || permit.OriginalSendIdentity != originalSendIdentity
                || submission.AcceptedSendIdentity is { Length: > 0 } accepted && accepted != originalSendIdentity
                || run.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == TerminalReleaseEvidence.Hash(submission)) != 1
                || !TerminalReleaseEvidence.BodySettled(run, submission) || string.IsNullOrEmpty(submission.JobId)) return null;
            var historicalHash = TerminalReleaseEvidence.Hash(submission);
            var originalPermit = permit;
            var identity'''.replace('\n',nl)
 s=rep(s,a,z)
 a='            return OriginalRequestMatches(hit, identity.RequestEvidence) ? hit!.JobId : null;'
 z='''            if (!OriginalRequestMatches(hit, identity.RequestEvidence) || hit!.JobId != submission.JobId
                || !BgiJobTerminalPolling.IsTerminal(hit.State) || hit.State != submission.ObservedTerminal
                || !hit.ExecutionExitConfirmed || hit.ExecutionExitDisposition != submission.ExecutionExitDisposition
                || hit.ExecutionExitDisposition is not ("execution_exited" or "never_started")) return null;
            var readback = _runs.Load(run.RunId!);
            return readback?.TerminalRelease is null && readback?.WireRunId == run.WireRunId
                && readback.SubmissionHistory.Count(s => TerminalReleaseEvidence.Hash(s) == historicalHash && s.SendPermit == originalPermit) == 1
                ? hit.JobId : null;'''.replace('\n',nl)
 return rep(s,a,z)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs',history)
# Strong association readback, not just a key and identity probe.
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs',lambda s,nl:rep(s,'return readback?.RecoveryAssociations.Any(a => string.Equals(a.SubmissionKey, association.SubmissionKey, StringComparison.Ordinal)','return readback is not null && TerminalReleaseEvidence.ValidRecoveryAssociation(readback, association)'+nl+'                && readback.RecoveryAssociations.Any(a => string.Equals(a.SubmissionKey, association.SubmissionKey, StringComparison.Ordinal)'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',lambda s,nl:rep(s,'boundary.ReconcileHistoricalSubmissionAsync(run, historical, budget.Token)','boundary.ReconcileHistoricalSubmissionAsync(run, historical, budget.Token, identity.SubmissionIdentity)'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',lambda s,nl:rep(s,'''            if (_runs.TryAppendRecoveryAssociation(runId, association) is null)
                return HostActionResult.Unavailable("历史提交恢复关联落盘失败/冲突，保持 Unknown 待重试");'''.replace('\n',nl),'''            if (_runs.TryAppendRecoveryAssociation(runId, association) is null)
                return HostActionResult.Unavailable("历史提交恢复关联落盘失败/冲突，保持 Unknown 待重试");
            var associated = _runs.Load(runId);
            if (associated is null || !TerminalReleaseEvidence.ValidRecoveryAssociation(associated, association))
                return HostActionResult.Unavailable("历史原轮关联读回未成立，保持 Unknown 待重试");
            var associatedOriginal = associated.SubmissionHistory[association.HistoryIndex];
            if (TryResolveNodeSendIdentity(associated, associatedOriginal) != identity
                || !await SettleOriginalNodeAcceptanceAsync(associated, identity, hitJobId).ConfigureAwait(false))
                return HostActionResult.Unavailable("历史原轮门面结清未成立，保持 Unknown 待重试");
            var originalOperation = _admissionStore?.Read().File?.Handoff is { } originalHandoff
                ? originalHandoff.Operations.Concat(originalHandoff.ArchivedOperations.Select(a => a.Operation))
                    .SingleOrDefault(o => o.RequestIdentity == identity.RequestIdentity) : null;
            if (originalOperation is null || _runs.TrySealTerminalNode(runId, originalOperation) is null)
                return HostActionResult.Unavailable("历史原轮封印未成立，保持 Unknown 待重试");
            RunStore.RebaseOnto(run, _runs.Load(runId)!);'''.replace('\n',nl)))
def hooks(s,nl):
 a='''                    if (run.CurrentSubmission is not { } sub) return Task.FromResult<string?>("submission_missing");'''
 z='''                    var matchingSubmissions = TerminalReleaseEvidence.Submissions(run)
                        .Where(s => s.Key == op.WireSubmitKey && s.NodeId == cand.NodeId && s.Occurrence == cand.Occurrence
                            && s.LoopIteration == cand.LoopIteration && s.Attempt == cand.Attempt).ToList();
                    if (matchingSubmissions.Count != 1) return Task.FromResult<string?>("submission_missing_or_ambiguous");
                    var sub = matchingSubmissions[0];'''.replace('\n',nl)
 s=rep(s,a,z)
 a='''                    if (!string.Equals(sub.AcceptedSendIdentity, entry.SubmissionIdentity, StringComparison.Ordinal))
                        return Task.FromResult<string?>("receipt_send_identity_mismatch");'''.replace('\n',nl)
 z='''                    var recoveredHistory = run.RecoveryAssociations.Where(a => a.SubmissionIdentity == entry.SubmissionIdentity
                        && a.SendSeq == entry.SendSeq && a.JobId == sub.JobId && TerminalReleaseEvidence.ValidRecoveryAssociation(run, a)).ToList();
                    if (sub.AcceptedSendIdentity != entry.SubmissionIdentity && (recoveredHistory.Count != 1
                        || ResolveOriginalNodeSendIdentity(run, sub, _admissionStore?.Read().File?.Handoff)?.SubmissionIdentity != entry.SubmissionIdentity))
                        return Task.FromResult<string?>("receipt_send_identity_mismatch");
                    if (entry.JobId is { Length: > 0 } && entry.JobId != sub.JobId)
                        return Task.FromResult<string?>("receipt_job_mismatch");'''.replace('\n',nl)
 return rep(s,a,z)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',hooks)
def common(s,nl):
 start=s.index('            await EnsureAdmissionFacadeAsync(_shutdownCts.Token)',s.index('private async Task<BoundarySubmitResult> ReconcileOriginalNodeSubmissionAsync'))
 end=s.index('            return result;',start)
 s=s[:start]+'''            if (!await SettleOriginalNodeAcceptanceAsync(fresh, identity, result.JobId!).ConfigureAwait(false))
                return BoundarySubmitResult.UnknownWith("原节点门面结清未成立");
            var final = _runs.Load(run.RunId);
            if (final?.CurrentSubmission is not { } finalSub || finalSub.JobId != result.JobId
                || finalSub.AcceptedSendIdentity != identity.SubmissionIdentity || TryResolveNodeSendIdentity(final, finalSub) != identity)
                return BoundarySubmitResult.UnknownWith("原轮次结清后读回失效");
            RunStore.RebaseOnto(run, final);
'''.replace('\n',nl)+s[end:]
 a='    internal static NodeSendIdentity? ResolveOriginalNodeSendIdentity('
 z='''    private async Task<bool> SettleOriginalNodeAcceptanceAsync(WorkflowRunRecord run, NodeSendIdentity identity, string jobId)
    {
        await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
        var handoff = _admissionStore?.Read().File?.Handoff;
        if (handoff is null) return false;
        var matches = handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation))
            .Where(o => o.RequestIdentity == identity.RequestIdentity).ToList();
        if (matches.Count != 1) return false;
        var op = matches[0];
        if (op.ConflictPending || op.SubmissionIdentity != identity.SubmissionIdentity || op.LastSendSeq != identity.SendSeq
            || op.RunBinding != run.RunId || op.TargetEpoch != identity.TargetEpoch) return false;
        if (op.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted)
            return op.TakeoverRef == identity.SubmissionIdentity && handoff.Submission?.SubmissionIdentity != identity.SubmissionIdentity;
        if (!handoff.Operations.Contains(op) || _admission is not { } facade) return false;
        var settled = await facade.SettleReconciledAsync(identity.RequestIdentity,
            new ReconcileSettlement.Accepted(identity.SubmissionIdentity, identity.SendSeq, "host:reconcile_hit", run.RunId, jobId)).ConfigureAwait(false);
        if (settled.Kind != AdmissionResultKind.Accepted) return false;
        var closed = _admissionStore?.Read().File?.Handoff;
        var confirmed = closed?.Operations.SingleOrDefault(o => o.RequestIdentity == identity.RequestIdentity);
        return confirmed is not null && confirmed.SubmissionIdentity == identity.SubmissionIdentity && confirmed.LastSendSeq == identity.SendSeq
            && !confirmed.ConflictPending && confirmed.TakeoverRef == identity.SubmissionIdentity
            && confirmed.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted
            && closed?.Submission?.SubmissionIdentity != identity.SubmissionIdentity;
    }

    internal static NodeSendIdentity? ResolveOriginalNodeSendIdentity('''.replace('\n',nl)
 return rep(s,a,z)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',common)
