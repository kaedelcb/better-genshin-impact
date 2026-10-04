from pathlib import Path
import subprocess,json,hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'; prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
sources=['MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']
def git(*args): return subprocess.run(['git','--literal-pathspecs','-c','core.longpaths=true',*args],cwd=root,capture_output=True)
def save(n,o): (base/n).write_text(json.dumps(o,ensure_ascii=False,indent=2),encoding='utf-8')
obs=json.loads((base/'round-r2-final-observation.json').read_text(encoding='utf-8')); assert obs['inputs_equal'] and obs['build_exits']=={'assistant-build':0,'build':0,'probe-build':0,'test':1}
assert obs['counts']=={'Passed':2151,'NotExecuted':2,'Failed':2}
assert len(obs['failed'])==2 and all('LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping' in r['name'] for r in obs['failed'].values())
final=json.loads((base/'round-r2-source-final.json').read_text(encoding='utf-8'))
for row in final: assert hashlib.sha256((root/row['path']).read_bytes()).hexdigest()==row['sha256']
pfp=json.loads((base/'round-r2-pfp-final.json').read_text(encoding='utf-8')); assert len(pfp)==9
assert all(r['source_unchanged'] and r['raw_evidence_equal'] and r['retained_products_match'] and (r['green_inputs_equal_final'] if r['leg']!='negative' else len(r['failed'])==2) for r in pfp)
old={'create-thread-result.json','HANDOFF.md','new-model-verification-by-source.json','old-goal-paused-readback.json','relay-prompt.txt','source-goal-active-readback.json','source-head.txt','source-model-observation.json','source-status-postcommit.txt','source-status.txt'}
paths=sources+[str(p.relative_to(root).as_posix()) for p in base.iterdir() if p.is_file() and p.name not in old and not p.name.startswith('parent-') and not p.name.startswith('round-commit')]
paths.append('_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md')
relay=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-source-unified-20261005-from-01a108b6'
paths.extend(p.relative_to(root).as_posix() for p in relay.iterdir() if p.is_file())
for directory in prior.iterdir():
 if directory.is_dir() and directory.name.startswith('routing-round-'):
  paths.extend(p.relative_to(root).as_posix() for p in directory.iterdir() if p.is_file() and p.suffix in {'.json','.log','.trx'})
paths=sorted(set(paths)); manifest=base/'round-commit-paths.nul'; manifest.write_bytes(b'\0'.join(p.encode('utf-8') for p in paths)+b'\0'); save('round-commit-paths.json',paths)
before=git('status','--porcelain=v1'); (base/'round-commit-status-before.txt').write_bytes(before.stdout); assert before.returncode==0
staged=git('diff','--cached','--name-only','-z'); (base/'round-commit-staged-before.nul').write_bytes(staged.stdout); assert staged.returncode==0
code=git('add','--pathspec-from-file='+str(manifest),'--pathspec-file-nul'); (base/'round-commit-add.stderr.txt').write_bytes(code.stderr); assert code.returncode==0
code=git('commit','--only','-m','WIP: bind submission route through same-key intents and send permits','--pathspec-from-file='+str(manifest),'--pathspec-file-nul')
(base/'round-commit.stdout.txt').write_bytes(code.stdout); (base/'round-commit.stderr.txt').write_bytes(code.stderr); assert code.returncode==0
head=git('rev-parse','HEAD').stdout.decode().strip(); actual=git('diff-tree','--no-commit-id','--name-only','-r','-z',head).stdout.decode().split('\0'); actual=[p for p in actual if p]
assert set(actual).issubset(set(paths)); assert set(sources).issubset(set(actual))
for row in final: assert hashlib.sha256((root/row['path']).read_bytes()).hexdigest()==row['sha256']
after=git('status','--porcelain=v1'); (base/'round-commit-status-after.txt').write_bytes(after.stdout)
staged_after=git('diff','--cached','--name-only','-z'); assert staged_after.stdout==staged.stdout
save('round-commit-observation.json',dict(head=head,branch=git('branch','--show-current').stdout.decode().strip(),actual_changed_files=actual,source_files=sources,source_inputs_unchanged=True,staged_before_after_equal=True,level='local WIP checkpoint, not independent acceptance or production authorization'))
print(json.dumps(dict(head=head,changed_files=len(actual),source_files=len(sources),staged_preserved=True,source_inputs_unchanged=True)))
