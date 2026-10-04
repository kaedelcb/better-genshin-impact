from pathlib import Path

def edit(path,f):
 p=Path(path);b=p.read_bytes();nl='\r\n' if b'\r\n' in b else '\n';s=f(b.decode('utf-8-sig'),nl);p.write_bytes((b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'')+s.encode())
def rep(s,a,z):
 assert s.count(a)==1,(a,s.count(a));return s.replace(a,z)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/ArbitrationWorkflowExecutionBoundary.cs',lambda s,nl: rep(rep(rep(rep(s,'    private readonly IWorkflowExecutionBoundary _inner;','    private readonly IWorkflowExecutionBoundary _inner;'+nl+'    private readonly Func<WorkflowRunRecord, WorkflowSubmission, CancellationToken, Task<BoundarySubmitResult>>? _reconcileViaAdmission;'),'        Func<WorkflowSubmitRequest, CancellationToken, Task<BoundarySubmitResult>> submitViaAdmission)','        Func<WorkflowSubmitRequest, CancellationToken, Task<BoundarySubmitResult>> submitViaAdmission,'+nl+'        Func<WorkflowRunRecord, WorkflowSubmission, CancellationToken, Task<BoundarySubmitResult>>? reconcileViaAdmission = null)'),'        _inner = inner ?? throw new ArgumentNullException(nameof(inner));','        _inner = inner ?? throw new ArgumentNullException(nameof(inner));'+nl+'        _reconcileViaAdmission = reconcileViaAdmission;'),'        => _inner.ReconcileSubmissionAsync(run, submission, ct);','        => _reconcileViaAdmission is { } reconcile ? reconcile(run, submission, ct) : _inner.ReconcileSubmissionAsync(run, submission, ct);'))
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',lambda s,nl: rep(s,'? new ArbitrationWorkflowExecutionBoundary(boundary, SubmitSuccessorViaAdmissionAsync)','? new ArbitrationWorkflowExecutionBoundary(boundary, SubmitSuccessorViaAdmissionAsync,'+nl+'                (run, sub, ct) => ReconcileOriginalNodeSubmissionAsync(boundary, run, sub, ct))'))
def host(s,nl):
 start=s.index('            BoundarySubmitResult bodyResult;',s.index('private async Task<HostActionResult> ReconcileUnknownRunForStopAsync'))
 end=s.index('        // 前置动作对账',start)
 s=s[:start]+'''            var result = await ReconcileOriginalNodeSubmissionAsync(boundary, run, sub, budget.Token).ConfigureAwait(false);
            if (!result.Accepted) return HostActionResult.Unavailable("主体原轮次恢复/门面结清未成立：" + result.RejectReason);
        }
'''.replace('\n',nl)+s[end:]
 return s
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',host)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',lambda s,nl: rep(s,'    internal static NodeSendIdentity? ResolveOriginalNodeSendIdentity(','''    private async Task<BoundarySubmitResult> ReconcileOriginalNodeSubmissionAsync(BgiWorkflowExecutionBoundary boundary,
        WorkflowRunRecord run, WorkflowSubmission sub, CancellationToken ct)
    {
        try
        {
            var identity = TryResolveNodeSendIdentity(run, sub);
            if (identity is null) return BoundarySubmitResult.UnknownWith("原发送轮次缺失/冲突，不查询或重发");
            var result = await boundary.ReconcileSubmissionAsync(run, sub, ct, identity.SubmissionIdentity).ConfigureAwait(false);
            if (!result.Accepted) return result;
            var fresh = _runs.Load(run.RunId);
            if (fresh?.CurrentSubmission is not { } persisted || persisted.JobId != result.JobId
                || persisted.AcceptedSendIdentity != identity.SubmissionIdentity || TryResolveNodeSendIdentity(fresh, persisted) != identity)
                return BoundarySubmitResult.UnknownWith("原轮次受理事实读回/关联失效");
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            var handoff = _admissionStore?.Read().File?.Handoff;
            var op = handoff?.Operations.SingleOrDefault(o => o.RequestIdentity == identity.RequestIdentity);
            if (op is null || op.ConflictPending || op.SubmissionIdentity != identity.SubmissionIdentity || op.LastSendSeq != identity.SendSeq)
                return BoundarySubmitResult.UnknownWith("原节点责任不唯一/冲突");
            if (op.RequestState is OperationRequestState.Accepted or OperationRequestState.TerminalCompleted)
            {
                if (op.TakeoverRef != identity.SubmissionIdentity || handoff?.Submission?.SubmissionIdentity == identity.SubmissionIdentity)
                    return BoundarySubmitResult.UnknownWith("原轮次门面尚未关闭");
            }
            else
            {
                if (_admission is not { } facade) return BoundarySubmitResult.UnknownWith("原节点责任门面不可用");
                var settled = await facade.SettleReconciledAsync(identity.RequestIdentity,
                    new ReconcileSettlement.Accepted(identity.SubmissionIdentity, identity.SendSeq, "host:reconcile_hit", run.RunId, result.JobId)).ConfigureAwait(false);
                if (settled.Kind != AdmissionResultKind.Accepted) return BoundarySubmitResult.UnknownWith("原节点门面结清未成立：" + settled.ReasonCode);
            }
            RunStore.RebaseOnto(run, fresh);
            return result;
        }
        catch (Exception ex) { return BoundarySubmitResult.UnknownWith("原节点恢复不可确认：" + ex.GetType().Name); }
    }

    internal static NodeSendIdentity? ResolveOriginalNodeSendIdentity('''.replace('\n',nl)))
