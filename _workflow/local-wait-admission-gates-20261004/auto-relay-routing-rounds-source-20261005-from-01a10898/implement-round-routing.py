from pathlib import Path
import json, hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
changes={
'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs':(
'        submission.NodeAdmissionRequired = rec.CurrentSubmission?.Key == submission.Key',
'''        if (nodeAdmissionRequired is { } requestedRoute && rec.CurrentSubmission is { } original
            && original.Key == submission.Key && original.NodeAdmissionRequired is { } originalRoute
            && originalRoute != requestedRoute)
            throw new RunRecordConflictException("同一提交身份的原发送路由不能随恢复边界改变。");
        submission.NodeAdmissionRequired = rec.CurrentSubmission?.Key == submission.Key'''),
'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs':(
'            if (current.CurrentSubmission is not { } submission) return false;',
'''            if (current.CurrentSubmission is not { } submission) return false;
            // A permit belongs to the route fixed before the first send. Null is legacy unknown.
            if (submission.NodeAdmissionRequired is { } originalRoute
                && originalRoute != (originalSendIdentity is not null)) return false;'''),
'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs':(
'                && sub.SendPermit?.OriginalSendIdentity is not { Length: > 0 }',
'''                && sub.SendPermit?.OriginalSendIdentity is not { Length: > 0 }
                && sub.PreviousSendRounds is not { Count: > 0 }''')}
rows=[]
for rel,(old,new) in changes.items():
 p=root/rel; b=p.read_bytes(); (base/('round-before-'+p.name+'.txt')).write_bytes(b)
 rows.append(dict(path=rel,bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b))
 newline='\r\n' if b'\r\n' in b else '\n'
 old=old.replace('\n',newline).encode(); new=new.replace('\n',newline).encode()
 assert b.count(old)==1,(rel,b.count(old))
 p.write_bytes(b.replace(old,new))
(base/'round-source-before.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print('three original-store route guards applied')
