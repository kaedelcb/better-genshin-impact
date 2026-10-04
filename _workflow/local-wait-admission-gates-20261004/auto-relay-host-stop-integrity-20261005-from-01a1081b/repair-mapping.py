import json,hashlib
from pathlib import Path
base=Path(__file__).parent
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs')
b=p.read_bytes()
(base/'stop-product-before.json').write_text(json.dumps(dict(path=str(p),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
s=b.decode('utf-8').replace('\r\n','\n')
marker='    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync'
helper='''    // Check the immutable run anchors, not just the subset visible in this lease read.
    // Tombstones and archives remain original mappings; absence is never retirement proof.
    private static bool OriginalAdmissionMappingsPresent(WorkflowRunRecord run, IReadOnlyList<OperationRecord> operations)
    {
        var mappings = run.AdmissionMappings ?? [];
        if (mappings.Distinct().Count() != mappings.Count) return false;
        foreach (var mapping in mappings)
        {
            if (mapping.Version != 1 || mapping.SendSeq < 1
                || string.IsNullOrWhiteSpace(mapping.RequestIdentity)
                || mapping.SubmissionIdentity != $"sub:{mapping.RequestIdentity}:{mapping.SendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                || mapping.OperationType is not (OperationType.FlowRegistration or OperationType.Recovery or OperationType.Handoff))
                return false;
            var matches = operations.Where(op => op.RunBinding == run.RunId
                && op.RequestIdentity == mapping.RequestIdentity && op.OperationType == mapping.OperationType
                && op.SubmissionIdentity == mapping.SubmissionIdentity && op.LastSendSeq == mapping.SendSeq
                && op.Candidate?.WorkflowId == run.WorkflowId).ToList();
            if (matches.Count != 1) return false;
        }
        if (run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration } parent
            && (parent.Version != 1 || parent.RunId != run.RunId || parent.WorkflowId != run.WorkflowId
                || operations.Count(op => op.RunBinding == run.RunId && op.RequestIdentity == parent.RequestIdentity
                    && op.OperationType == OperationType.FlowRegistration && op.Candidate?.WorkflowId == run.WorkflowId) != 1))
            return false;
        return true;
    }

'''
assert s.count(marker)==1
s=s.replace(marker,helper+marker)
needle='            originalMappings = operations.Where(o => o.RunBinding == runId).ToList();'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n            if (!OriginalAdmissionMappingsPresent(run, originalMappings))\n                return AdmissionTerminalReconciliationOutcome.Pending;')
needle='            if (originalMappings.Any(original => !current.Any(now => now.RequestIdentity == original.RequestIdentity'
assert s.count(needle)==1
s=s.replace(needle,'            if (!OriginalAdmissionMappingsPresent(run, current)) return AdmissionTerminalReconciliationOutcome.Pending;\n'+needle)
needle='        if (originalMappings.Any(original => !current.Any(now => now.RequestIdentity == original.RequestIdentity'
assert s.count(needle)==2 # substring also present in the loop; anchor using previous blank line
needle='\n        if (originalMappings.Any(original => !current.Any(now => now.RequestIdentity == original.RequestIdentity'
assert s.count(needle)==1
s=s.replace(needle,'\n        if (!OriginalAdmissionMappingsPresent(run, current)) return AdmissionTerminalReconciliationOutcome.Pending;'+needle)
p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
