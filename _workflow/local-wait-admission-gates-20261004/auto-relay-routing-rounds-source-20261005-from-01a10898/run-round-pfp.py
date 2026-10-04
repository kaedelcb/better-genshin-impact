from pathlib import Path
import hashlib,json,subprocess,sys,os
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'; prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
kind=sys.argv[1]
version=sys.argv[2] if len(sys.argv)>2 else 'r1'
if kind=='prepare':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs'; old='''            if (submission.NodeAdmissionRequired is { } originalRoute
                && originalRoute != (originalSendIdentity is not null)) return false;'''; new=''; filt='FullyQualifiedName~OriginalRouteRounds_PrepareCannotCrossDurableRouting'
elif kind=='samekey':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs'; old='''        if (nodeAdmissionRequired is { } requestedRoute && rec.CurrentSubmission is { } original
            && original.Key == submission.Key && original.NodeAdmissionRequired is { } originalRoute
            && originalRoute != requestedRoute)
            throw new RunRecordConflictException("同一提交身份的原发送路由不能随恢复边界改变。");'''; new=''; filt='FullyQualifiedName~OriginalRouteRounds_SameKeyCannotSilentlyReuseDifferentBoundary'
elif kind=='history':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'; old='                && sub.PreviousSendRounds is not { Count: > 0 }'; new=''; filt='FullyQualifiedName~OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest'
else: raise ValueError(kind)
p=root/rel; original=p.read_bytes(); nl='\r\n' if b'\r\n' in original else '\n'; old=old.replace('\n',nl).encode(); new=new.replace('\n',nl).encode(); assert original.count(old)==1
records=[]
def run(leg):
 name='routing-round-'+kind+'-pfp-'+version+'-'+leg
 helper=base/'run-round-check.py' if version=='r3' else prior/'run-stop-check.py'
 c=subprocess.run([sys.executable,'-B',str(helper),name,filt],cwd=root)
 records.append(dict(kind=kind,leg=leg,exit=c.returncode,directory=str((prior/name).relative_to(root)),source=rel,sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
 (base/('round-pfp-'+kind+'-'+version+'.json')).write_text(json.dumps(records,indent=2),encoding='utf-8')
 return c.returncode
try:
 assert run('baseline')==0
 mutation=p.with_name(p.name+'.round-mutation.tmp'); mutation.write_bytes(original.replace(old,new)); os.replace(mutation,p)
 assert run('negative')==1
finally:
 tmp=p.with_name(p.name+'.round-restore.tmp'); tmp.write_bytes(original); os.replace(tmp,p); assert p.read_bytes()==original
 (base/('round-restore-'+kind+'-'+version+'.json')).write_text(json.dumps(dict(source=rel,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),equal=p.read_bytes()==original,atomic_same_directory=True)),encoding='utf-8')
assert run('restored')==0
print('P/F/P',kind,'complete',flush=True)
