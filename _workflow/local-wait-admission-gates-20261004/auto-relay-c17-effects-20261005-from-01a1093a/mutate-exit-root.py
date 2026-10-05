from pathlib import Path
import subprocess,hashlib,json,os
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a';rows=[]
variants=[('all-nonzero-exit-guards',[('MultiplayerHoeingAssistant/Services/TaskCenter/WindowsTerminalEffectObserver.cs',b'process.HasExited && process.ExitCode == 0',b'process.HasExited'),('BetterGenshinImpact/Service/ExternalInterface/TerminalEffectJournal.cs',b'proof.ExitCode == 0',b'true')],'FullyQualifiedName~WindowsObserver_UsesPinnedRealChildProcess'),('latest-original-binding',[('MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowObservationPersistence.cs',b'|| Binding.Freeze(live) != binding || !live.SendAttempted',b'|| false || !live.SendAttempted')],'FullyQualifiedName~LatestOriginalBindingChanged_RefusesPublishingIndependentEffect')]
for ident,patches,filter in variants:
 backups=[];row=dict(id=ident,files=[])
 try:
  for path,old,new in patches:
   p=root/path;before=p.read_bytes();assert before.count(old)==1;after=before.replace(old,new);backups.append((p,before));p.write_bytes(after)
   row['files'].append(dict(path=path,original_sha256=hashlib.sha256(before).hexdigest(),mutant_sha256=hashlib.sha256(after).hexdigest()))
  row['mutant_exit']=subprocess.run(['python',str(base/'run-c17-check.py'),'mutant-'+ident,filter],cwd=root).returncode
 finally:
  for (p,before),f in zip(backups,row['files']):
   tmp=p.with_name(p.name+'.c17-restore');tmp.write_bytes(before);os.replace(tmp,p);f['restored_sha256']=hashlib.sha256(p.read_bytes()).hexdigest();assert f['restored_sha256']==f['original_sha256']
 rows.append(row);(base/'root-mutations.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
 if row['mutant_exit']==0:raise RuntimeError('Root mutation did not cause failure: '+ident)
