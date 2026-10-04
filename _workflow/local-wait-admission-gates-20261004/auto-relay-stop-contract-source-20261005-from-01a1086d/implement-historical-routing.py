from pathlib import Path
import hashlib,json
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
paths=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/ArbitrationWorkflowExecutionBoundary.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs']
before=[]
def edit(rel,old,new):
 p=root/rel; b=p.read_bytes(); bom=b.startswith(b'\xef\xbb\xbf'); s=b.decode('utf-8-sig'); crlf='\r\n'in s; s=s.replace('\r\n','\n'); assert s.count(old)==1,(rel,s.count(old),old[:70]); s=s.replace(old,new); p.write_bytes((b'\xef\xbb\xbf' if bom else b'')+s.replace('\n','\r\n' if crlf else '\n').encode('utf-8'))
for rel in paths:
 b=(root/rel).read_bytes(); before.append(dict(path=rel,sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),crlf=b'\r\n'in b)); (base/('before-'+Path(rel).name+'.txt')).write_bytes(b)
(base/'routing-source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
edit(paths[0], 'public sealed class WorkflowSubmission\n{', '''public sealed class WorkflowSubmission
{
    /// <summary>发送意图同次固定的原节点仲裁模式；缺字段为未知历史，不能用当前宿主开关补造。</summary>
    [JsonPropertyName("nodeAdmissionRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NodeAdmissionRequired { get; set; }
''')
edit(paths[1], 'public interface IWorkflowExecutionBoundary\n{', '''public interface IWorkflowExecutionBoundary
{
    /// <summary>此边界实际节点发送是否经原节点仲裁；装饰器必须传递自己的发送路由合同。</summary>
    bool RequiresNodeAdmission => false;
''')
edit(paths[1], '_runs.RecordIntent(run, submission);', '_runs.RecordIntentForBoundary(run, submission, _boundary.RequiresNodeAdmission);')
edit(paths[2], '    public bool RequiresStopAuthority => _inner.RequiresStopAuthority;', '    public bool RequiresNodeAdmission => true;\n    public bool RequiresStopAuthority => _inner.RequiresStopAuthority;')
edit(paths[3], '''    public void RecordIntent(WorkflowRunRecord rec, WorkflowSubmission submission)
    {''', '''    public void RecordIntent(WorkflowRunRecord rec, WorkflowSubmission submission)
        => RecordIntentCore(rec, submission, null);

    internal void RecordIntentForBoundary(WorkflowRunRecord rec, WorkflowSubmission submission, bool nodeAdmissionRequired)
        => RecordIntentCore(rec, submission, nodeAdmissionRequired);

    private void RecordIntentCore(WorkflowRunRecord rec, WorkflowSubmission submission, bool? nodeAdmissionRequired)
    {
        if (submission.NodeAdmissionRequired is not null)
            throw new RunRecordConflictException("发送路由只能由意图边界同次固定，不接受调用者补造。");
        submission.NodeAdmissionRequired = rec.CurrentSubmission?.Key == submission.Key
            ? rec.CurrentSubmission.NodeAdmissionRequired : nodeAdmissionRequired;''')
edit(paths[3], '''        rec.CurrentSubmission = submission;
        Persist(rec, rec.RecordRevision);
    }''','''        rec.CurrentSubmission = submission;
        Persist(rec, rec.RecordRevision, authorizedRouting: nodeAdmissionRequired);
    }''')
edit(paths[3], 'bool authorizedDiagnostic = false)\n    {', 'bool authorizedDiagnostic = false, bool? authorizedRouting = null)\n    {')
edit(paths[3], 'RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation, authorizedPermit, authorizedMapping);', 'RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation, authorizedPermit, authorizedMapping, authorizedRouting);')
edit(paths[3], '        if (currentText is null && rec.AdmissionMappings is not null)', '''        if (currentText is null && (rec.CurrentSubmission?.NodeAdmissionRequired is not null
            || rec.SubmissionHistory.Any(s => s.NodeAdmissionRequired is not null)))
            throw new RunRecordConflictException("新记录不能补造历史发送路由。");
        if (currentText is null && rec.AdmissionMappings is not null)''')
edit(paths[4], 'RunAdmissionMapping? authorizedMapping = null)', 'RunAdmissionMapping? authorizedMapping = null, bool? authorizedRouting = null)')
edit(paths[4], '        var submissions = next.SubmissionHistory.Select(s => JsonSerializer.Serialize(s)).ToList();', '''        if (current.CurrentSubmission?.Key == next.CurrentSubmission?.Key)
            Require(current.CurrentSubmission?.NodeAdmissionRequired == next.CurrentSubmission?.NodeAdmissionRequired);
        else if (next.CurrentSubmission?.NodeAdmissionRequired is { } routing)
            Require(authorizedRouting == routing && next.CurrentSubmission.Intent == SubmitIntentState.IntentRecorded
                && !next.CurrentSubmission.SendAttempted && string.IsNullOrEmpty(next.CurrentSubmission.JobId));
        var submissions = next.SubmissionHistory.Select(s => JsonSerializer.Serialize(s)).ToList();''')
edit(paths[5], '''            if (!_successorAdmissionWired && string.IsNullOrEmpty(sub.AcceptedSendIdentity)''', '''            if (sub.NodeAdmissionRequired == false && string.IsNullOrEmpty(sub.AcceptedSendIdentity)''')
edit(paths[5], '''            // The legacy driver without node admission owns only its flow registration.
            // Persisted node credentials or existing original node operations still require node checks.''','''            // Only the original durable direct-route proof can waive node admission.
            // A missing legacy proof never inherits the current Host switch.''')
# The historical legacy case really removes the new routing anchor too.
p=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'; b=p.read_bytes(); s=b.decode('utf-8').replace('\r\n','\n'); s=s.replace('                    _ = removeRoutingProof;', '                    Assert.True(sub.NodeAdmissionRequired);\n                    if (removeRoutingProof) sub.NodeAdmissionRequired = null;'); p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
print('historical routing candidate implemented')
