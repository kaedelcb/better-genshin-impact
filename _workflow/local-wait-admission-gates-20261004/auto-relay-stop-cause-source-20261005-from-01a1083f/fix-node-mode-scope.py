from pathlib import Path
import hashlib,json
base=Path(__file__).parent;p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs');b=p.read_bytes();bom=b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b'';nl='\r\n' if b'\r\n' in b else '\n';s=b.decode('utf-8-sig').replace('\r\n','\n')
(base/'node-mode-scope-before.json').write_text(json.dumps(dict(sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),newline=repr(nl)),indent=2))
a='    private static bool OriginalAdmissionMappingsPresent(WorkflowRunRecord run, IReadOnlyList<OperationRecord> operations)';assert s.count(a)==1;s=s.replace(a,a.replace('static ',''))
a='''        {
            var nodes = operations.Where(op => op.RunBinding == run.RunId && op.OperationType == OperationType.NodeExecution''';assert s.count(a)==1
s=s.replace(a,'''        {
            // The legacy driver without node admission owns only its flow registration.
            // Persisted node credentials or existing original node operations still require node checks.
            if (!_successorAdmissionWired && string.IsNullOrEmpty(sub.AcceptedSendIdentity)
                && sub.SendPermit?.OriginalSendIdentity is not { Length: > 0 }
                && !run.RecoveryAssociations.Any(a => a.SubmissionKey == sub.Key)
                && !operations.Any(op => op.RunBinding == run.RunId && op.OperationType == OperationType.NodeExecution
                    && op.WireSubmitKey == sub.Key)) continue;
            var nodes = operations.Where(op => op.RunBinding == run.RunId && op.OperationType == OperationType.NodeExecution''')
p.write_bytes(bom+s.replace('\n',nl).encode())
# Use a fresh P/F/P identity after this actual product scope repair; keep earlier legs unchanged.
pfp=(base/'node-pfp.py').read_text();pfp=pfp.replace("'node-original-mapping-r1'","'node-original-mapping-r2'").replace("'legacy-all-anchors-r1'","'legacy-all-anchors-r2'").replace("'node-pfp-observation.json'","'node-pfp-r2-observation.json'");(base/'node-pfp-r2.py').write_text(pfp,encoding='utf-8')
