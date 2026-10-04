from pathlib import Path
import json,hashlib
r=Path.cwd(); d=Path(__file__).parent; observations=[]
def edit(path,changes):
 p=r/path; b=p.read_bytes(); (d/(p.name+'.before')).write_bytes(b); s=b.decode('utf-8').replace('\r\n','\n')
 for old,new in changes:
  assert s.count(old)==1,(path,s.count(old)); s=s.replace(old,new)
 out=(s.replace('\n','\r\n') if b.count(b'\r\n') else s).encode('utf-8'); p.write_bytes(out)
 observations.append(dict(path=path,before=dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest()),after=dict(bytes=len(out),lines=len(out.splitlines()),sha256=hashlib.sha256(out).hexdigest()),bom=b.startswith(b'\xef\xbb\xbf'),crlf_before=b.count(b'\r\n'),crlf_after=out.count(b'\r\n')))
helper='''    internal static bool UniqueOriginalOutcomeSet(WorkflowRunRecord run, WorkflowSubmission submission, string sendIdentity)
    {
        if (string.IsNullOrEmpty(submission.Key) || string.IsNullOrEmpty(sendIdentity)
            || Submissions(run).Count(s => s.Key == submission.Key) != 1
            || Submissions(run).Count(s => s.AcceptedSendIdentity == sendIdentity
                || s.SendPermit?.OriginalSendIdentity == sendIdentity) > 1) return false;
        var related = run.NodeOutcomes.Where(o => o.SubmissionKey == submission.Key
            || o.AcceptedSendIdentity == sendIdentity).ToList();
        return related.Count <= 1 && related.All(o => o.SubmissionKey == submission.Key
            && (string.IsNullOrEmpty(o.AcceptedSendIdentity) || o.AcceptedSendIdentity == sendIdentity)
            && o.NodeId == submission.NodeId && o.Occurrence == submission.Occurrence
            && o.LoopIteration == submission.LoopIteration && o.Attempt == submission.Attempt);
    }

'''
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs',[
 ('    internal static RecoveryAssociationRecord? HistoricalObservation',helper+'    internal static RecoveryAssociationRecord? HistoricalObservation'),
 ('            || Submissions(run).Count(s => s.Key == submission.Key) != 1) return false;', '            || !UniqueOriginalOutcomeSet(run, submission, identity)) return false;'),
 ('        var s = resolved;\n        var observation', '        var s = resolved;\n        if (!UniqueOriginalOutcomeSet(r, s, sendIdentity)) return null;\n        var observation'),
 ('        && Submissions(r).All(s => BodySettled(r, s)) && r.PrerequisiteActions.All(a => PrerequisiteSettled(r, a))', '''        && Submissions(r).All(s => BodySettled(r, s)
            && ((s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity) is not { Length: > 0 } identity
                || UniqueOriginalOutcomeSet(r, s, identity))) && r.PrerequisiteActions.All(a => PrerequisiteSettled(r, a))''')])
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',[
 ('            if (identity is null) return HostActionResult.Unavailable("历史提交无法唯一关联原发送身份，保持 Unknown 待重试");', '''            if (identity is null) return HostActionResult.Unavailable("历史提交无法唯一关联原发送身份，保持 Unknown 待重试");
            if (!TerminalReleaseEvidence.UniqueOriginalOutcomeSet(run, historical, identity.SubmissionIdentity))
                return HostActionResult.Unavailable("历史原提交与结果关联缺失/重复/冲突，保持 Unknown 待重试");''')])
(d/'source-edit-observation.json').write_text(json.dumps(observations,indent=2),encoding='utf-8'); print(json.dumps(observations))
