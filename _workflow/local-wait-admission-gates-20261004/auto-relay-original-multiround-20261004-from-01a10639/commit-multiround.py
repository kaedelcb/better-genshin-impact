from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639'
protected={'HANDOFF.md','created-thread-observation.json','metadata-add.log','metadata-commit.log','opening-observation.json','opening-status.txt','prepare-prompt.py','relay-prompt.txt','source-model-observation.json','successor-handshake-verification.json','old-goal-active-before-pause.json','old-goal-paused-readback.json'}
files=['MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs','_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']
files += [str(p.relative_to(r)).replace('\\','/') for p in sorted(d.rglob('*')) if p.is_file() and p.name not in protected and p.name not in {'candidate-commit-observation.json','candidate-add.log','candidate-commit.log'}]
def git(*args):return subprocess.check_output(['git',*args],cwd=r).decode('utf-8',errors='replace').strip()
assert git('branch','--show-current')=='main-OldTeaBag-B168'
before=git('rev-parse','HEAD');staged=git('diff','--cached','--name-only')
source_bindings=json.loads((d/'final-r2/candidate-observation.json').read_text(encoding='utf-8'))['sources']
for b in source_bindings:assert hashlib.sha256((r/b['path']).read_bytes()).hexdigest()==b['sha256']
(d/'candidate-selected-files.json').write_text(json.dumps(dict(head_before=before,staged_before=staged,files=files),indent=2),encoding='utf-8');files.append(str((d/'candidate-selected-files.json').relative_to(r)).replace('\\','/'))
with (d/'candidate-add.log').open('wb') as f:subprocess.run(['git','add','--',*files],cwd=r,stdout=f,stderr=subprocess.STDOUT,check=True)
with (d/'candidate-commit.log').open('wb') as f:subprocess.run(['git','commit','--only','-m','fix: retain original successor context and reconcile explicit retry rounds','--',*files],cwd=r,stdout=f,stderr=subprocess.STDOUT,check=True)
head=git('rev-parse','HEAD');actual=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines();assert set(actual)==set(files),(set(actual)-set(files),set(files)-set(actual))
after=git('diff','--cached','--name-only');assert after==staged
for b in source_bindings:assert hashlib.sha256((r/b['path']).read_bytes()).hexdigest()==b['sha256']
obs=dict(kind='local candidate checkpoint; not certification/independent pass/product acceptance',head_before=before,commit=head,files=actual,staged_before=staged,staged_after=after,status_after=git('status','--porcelain'),source_inputs_match=True,production_gate_open=False,goal_complete=False)
(d/'candidate-commit-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8');print(head,len(actual))
