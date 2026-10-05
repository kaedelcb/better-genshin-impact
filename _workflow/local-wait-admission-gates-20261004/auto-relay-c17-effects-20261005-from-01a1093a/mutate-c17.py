from pathlib import Path
import subprocess,hashlib,json,os
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a'
run=base/'run-c17-check.py'; rows=[]
variants=[('no-original-epoch','BetterGenshinImpact/Service/ExternalInterface/TerminalEffectJournal.cs',b'progress.Epoch == epoch',b'true','FullyQualifiedName~TerminalEffectJournalTests'),('nonzero-process-exit','MultiplayerHoeingAssistant/Services/TaskCenter/WindowsTerminalEffectObserver.cs',b'process.HasExited && process.ExitCode == 0',b'process.HasExited','FullyQualifiedName~WindowsObserver_UsesPinnedRealChildProcess')]
for ident,path,old,new,filter in variants:
 p=root/path; original=p.read_bytes();assert original.count(old)==1
 row=dict(id=ident,path=path,original_sha256=hashlib.sha256(original).hexdigest())
 try:
  changed=original.replace(old,new);p.write_bytes(changed);row['mutant_sha256']=hashlib.sha256(changed).hexdigest()
  row['mutant_exit']=subprocess.run(['python',str(run),'mutant-'+ident,filter],cwd=root).returncode
 finally:
  temp=p.with_name(p.name+'.c17-restore');temp.write_bytes(original);os.replace(temp,p)
  row['restored_sha256']=hashlib.sha256(p.read_bytes()).hexdigest();assert row['restored_sha256']==row['original_sha256']
 rows.append(row);(base/'mutations.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
 if row['mutant_exit']==0:raise RuntimeError('Mutation did not cause failure: '+ident)
print('mutants restored; positive restored regression must now run',flush=True)
