exec(open('_workflow/local-wait-admission-gates-20261004/typed-parent/implement.py',encoding='utf-8').read().split('edit(paths[0]')[0].replace("(d/'source-before.json').write_text(json.dumps(facts,indent=2))", "(d/'facade-before.json').write_text(json.dumps(facts,indent=2))"))
def facade(s):
 s=rep(s,'    public string? RunBinding { get; set; }','    public string? RunBinding { get; set; }\n    public AdmissionParentSource? ParentSource { get; set; }')
 s=rep(s,'public sealed class AdmissionHooks\n{','public sealed class AdmissionHooks\n{\n    // RunStore-only, synchronous read; never reads Lease or awaits an external port.\n    public Func<string, string, AdmissionParentSource?>? HandoffParentProvider { get; set; }')
 s=rep(s,'                    RunBinding = request.RunBinding,','                    RunBinding = request.RunBinding,\n                    ParentSource = request.ParentSource,')
 s=rep(s,'                    file.Handoff.Operations.Add(new OperationRecord','                    var parentSource = frz.OperationType == OperationType.NodeExecution\n                        ? ResolveTypedParent(file.Handoff, frz.RunBinding, frz.Candidate.WorkflowId) : null;\n                    if (frz.Candidate.Namespace == "successor"\n                        && (parentSource is null || parentSource != frz.ParentSource\n                            || parentSource.Value.Scope != frz.Candidate.Scope))\n                        return "parent_source_unavailable";\n                    file.Handoff.Operations.Add(new OperationRecord')
 s=rep(s,'                        ParentRequestIdentity = frz.OperationType == OperationType.NodeExecution\n                            ? ResolveParentRequestIdentity(file.Handoff, frz.RunBinding, frz.Candidate.WorkflowId)\n                            : null,','                        ParentRequestIdentity = parentSource?.RequestIdentity,\n                        ParentSource = parentSource,')
 anchor='            if (op is null || op.Zone != OperationZone.Active) return "stale_operation_identity";'
 index=s.index(anchor,s.index('private LeaseMutateResult ValidateAndOccupy'))
 s=s[:index]+s[index:].replace(anchor,anchor+'\n            if (op.Candidate?.Namespace == "successor" || op.ParentSource is not null)\n            {\n                var currentParent = ResolveTypedParent(file.Handoff, op.RunBinding, op.Candidate?.WorkflowId);\n                if (currentParent is null || currentParent != op.ParentSource\n                    || op.ParentRequestIdentity != currentParent.Value.RequestIdentity\n                    || op.Candidate?.Scope != currentParent.Value.Scope) return "parent_source_unavailable";\n            }',1)
 start=s.index('    private static string? ResolveParentRequestIdentity(');end=s.index('\n    /// <summary>',start)
 s=s[:start]+'''    private AdmissionParentSource? ResolveTypedParent(LeaseHandoffSegment? handoff, string? runBinding, string? workflowId)
    {
        if (string.IsNullOrEmpty(runBinding) || string.IsNullOrEmpty(workflowId)) return null;
        var parents = (handoff?.Operations ?? [])
            .Concat((handoff?.ArchivedOperations ?? []).Select(a => a.Operation))
            .Where(o => o is not null && IsFlowRegistrationParent(o, runBinding)).ToList();
        AdmissionParentSource? original;
        try { original = _hooks.HandoffParentProvider?.Invoke(runBinding, workflowId); }
        catch { return null; }
        if (parents.Count > 0)
        {
            if (parents.Count != 1 || original is not null) return null;
            var parent = parents[0];
            var scope = parent.Candidate?.Scope;
            return parent.Candidate?.WorkflowId == workflowId && TaskCenterHost.IsCanonicalAdmissionScope(scope)
                ? new AdmissionParentSource(1, AdmissionParentKind.PanelFlowRegistration, runBinding, workflowId, scope!, parent.RequestIdentity)
                : null;
        }
        return original is { Version: 1, Kind: AdmissionParentKind.StartupHandoff } h
            && h.RunId == runBinding && h.WorkflowId == workflowId && TaskCenterHost.IsCanonicalAdmissionScope(h.Scope)
            ? h : null;
    }
'''+s[end:]
 s=s.replace('private static bool IsOwnParentOccupationExempt(', 'private bool IsOwnParentOccupationExempt(')
 start=s.index('        var parents = (handoff.Operations ?? [])',s.index('private bool IsOwnParentOccupationExempt'))
 end=s.index('        // ④ 同 run',start)
 s=s[:start]+'''        var currentSource = ResolveTypedParent(handoff, runBinding, op.Candidate?.WorkflowId);
        if (currentSource is null || currentSource != op.ParentSource
            || op.ParentRequestIdentity != currentSource.Value.RequestIdentity
            || op.Candidate?.Scope != currentSource.Value.Scope) return false;
        if (currentSource.Value.Kind == AdmissionParentKind.PanelFlowRegistration)
        {
            var panel = (handoff.Operations ?? []).Concat((handoff.ArchivedOperations ?? []).Select(a => a.Operation))
                .Single(o => IsFlowRegistrationParent(o, runBinding));
            if (panel.RequestState is not (OperationRequestState.Accepted or OperationRequestState.TerminalCompleted)
                || string.IsNullOrEmpty(panel.SubmissionIdentity) || panel.LastSendSeq <= 0) return false;
        }
'''+s[end:]
 start=s.index('        // ⑤ 父子绑定已持久化',s.index('private bool IsOwnParentOccupationExempt'));end=s.index('        return true;',start)
 s=s[:start]+s[end:]
 return s
edit(paths[4],facade)
