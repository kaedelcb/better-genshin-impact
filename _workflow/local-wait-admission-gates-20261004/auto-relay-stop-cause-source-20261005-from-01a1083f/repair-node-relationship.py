from pathlib import Path
import hashlib,json
base=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f')
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs')
b=p.read_bytes();bom=b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'';n='\r\n' if b'\r\n' in b else '\n';s=b.decode('utf-8-sig').replace('\r\n','\n')
(base/'node-product-before.json').write_text(json.dumps(dict(path=str(p),sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),newline=repr(n)),indent=2))
a='            || run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait, Binding: not null }'
assert s.count(a)==1
s=s.replace(a,'            || run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait }\n            || TerminalReleaseEvidence.Submissions(run).Any(s => s.SendAttempted || !string.IsNullOrEmpty(s.JobId)\n                || !string.IsNullOrEmpty(s.AcceptedSendIdentity))')
a='''        if (run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration } parent'''
assert s.count(a)==1
block='''        // Every possibly sent node retains its original operation, even when flow registration remains.
        // Legacy records use the preserved wire tuple; modern credentials must agree with that tuple.
        foreach (var sub in TerminalReleaseEvidence.Submissions(run).Where(s => s.SendAttempted
            || !string.IsNullOrEmpty(s.JobId) || !string.IsNullOrEmpty(s.AcceptedSendIdentity)))
        {
            var nodes = operations.Where(op => op.RunBinding == run.RunId && op.OperationType == OperationType.NodeExecution
                && op.WireSubmitKey == sub.Key && op.TargetEpoch == sub.Epoch
                && op.Candidate is { } c && c.WorkflowId == run.WorkflowId && c.NodeId == sub.NodeId
                && c.Occurrence == sub.Occurrence && c.LoopIteration == sub.LoopIteration && c.Attempt == sub.Attempt).ToList();
            if (nodes.Count != 1 || string.IsNullOrWhiteSpace(sub.Key) || string.IsNullOrWhiteSpace(sub.Epoch)) return false;
            var node = nodes[0];
            if (node.LastSendSeq < 1 || string.IsNullOrWhiteSpace(node.RequestIdentity)
                || node.SubmissionIdentity != $"sub:{node.RequestIdentity}:{node.LastSendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                || !string.IsNullOrEmpty(sub.AcceptedSendIdentity) && sub.AcceptedSendIdentity != node.SubmissionIdentity
                || sub.SendPermit is { } permit && (!permit.Consumed || permit.OriginalSendIdentity != node.SubmissionIdentity)) return false;
            var links = run.RecoveryAssociations.Where(a => a.SubmissionKey == sub.Key).ToList();
            if (links.Count > 1 || links.Any(a => a.SubmissionIdentity != node.SubmissionIdentity
                || a.SendSeq != node.LastSendSeq || !TerminalReleaseEvidence.ValidRecoveryAssociation(run, a))) return false;
        }
'''
s=s.replace(a,block+a)
p.write_bytes(bom+s.replace('\n',n).encode())
