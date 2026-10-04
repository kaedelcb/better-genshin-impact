from pathlib import Path
import hashlib,json,subprocess,sys,os
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'; prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
kind=sys.argv[1]
suffix='-r3'
if kind=='relation':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'; old='if (sub.NodeAdmissionRequired == false &&'; new='if (!_successorAdmissionWired &&'; filt='FullyQualifiedName~HistoricalNodeStop_'
elif kind=='immutable':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs'; old='Require(current.CurrentSubmission?.NodeAdmissionRequired == next.CurrentSubmission?.NodeAdmissionRequired);'; new='Require(true);'; filt='FullyQualifiedName~OriginalNodeRouting_'
elif kind=='manufacture':
 rel='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs'; old='Require(authorizedRouting == routing && next.CurrentSubmission.Intent == SubmitIntentState.IntentRecorded\n                && !next.CurrentSubmission.SendAttempted && string.IsNullOrEmpty(next.CurrentSubmission.JobId));'; new='Require(true);'; filt='FullyQualifiedName~OriginalNodeRouting_'
else: raise ValueError(kind)
p=root/rel; original=p.read_bytes(); nl='\r\n'if b'\r\n'in original else '\n'; old=old.replace('\n',nl).encode(); new=new.encode(); assert original.count(old)==1
records=[]
def run(leg):
 name='historical-routing-'+kind+suffix+'-'+leg
 c=subprocess.run([sys.executable,'-B',str(prior/'run-stop-check.py'),name,filt],cwd=root)
 records.append(dict(kind=kind,leg=leg,exit=c.returncode,directory=str((prior/name).relative_to(root)),source=rel,sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
 (base/('routing-pfp-'+kind+suffix+'.json')).write_text(json.dumps(records,indent=2),encoding='utf-8')
 return c.returncode
def restore():
 tmp=p.with_name(p.name+'.routing-restore.tmp'); tmp.write_bytes(original); os.replace(tmp,p); assert p.read_bytes()==original
try:
 assert run('baseline')==0
 p.write_bytes(original.replace(old,new)); assert run('negative')==1
finally:
 restore()
 (base/('routing-restore-'+kind+suffix+'.json')).write_text(json.dumps(dict(source=rel,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),equal=p.read_bytes()==original,atomic_same_directory=True)),encoding='utf-8')
assert run('restored')==0
print('P/F/P',kind,'complete with atomic exact restore',flush=True)
