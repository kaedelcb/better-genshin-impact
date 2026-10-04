from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f'
def git(*args):return subprocess.check_output(['git',*args],text=True,encoding='utf-8').strip()
assert git('branch','--show-current')=='main-OldTeaBag-B168';assert git('rev-parse','HEAD')=='72bd5f8f0831e362a21a8240c810a7a99969b903';assert not git('diff','--cached','--name-only')
obs=json.loads((d/'final/candidate-observation.json').read_text(encoding='utf-8'))
for x in obs['sources']:assert hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256']
outside={'metadata-add.log','metadata-commit.log','parent-reception-observation.json'}
untracked=git('ls-files','--others','--exclude-standard','--',str(d.relative_to(r))).splitlines();files=[p for p in untracked if Path(p).name not in outside]
files+=['MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs','_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md',str((d/'precommit-observation.json').relative_to(r))]
files=sorted(set(files));assert len(files)>20
(d/'precommit-observation.json').write_text(json.dumps(dict(head=git('rev-parse','HEAD'),branch=git('branch','--show-current'),files=files,porcelain=git('status','--porcelain'),outside_excluded=sorted(outside),source_hashes=obs['sources'],evidence='ordinary candidate; not independent/receipt/product acceptance'),ensure_ascii=False,indent=2),encoding='utf-8')
assert sum(len(p)+3 for p in files)<29000
with (d/'candidate-add.log').open('wb') as f:assert subprocess.run(['git','add','--',*files],stdout=f,stderr=subprocess.STDOUT).returncode==0
with (d/'candidate-commit.log').open('wb') as f:assert subprocess.run(['git','commit','--only','-m','fix(task-center): serialize recovery dispatch through takeover closure','--',*files],stdout=f,stderr=subprocess.STDOUT).returncode==0
head=git('rev-parse','HEAD');actual=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines();assert sorted(actual)==files
(d/'candidate-commit-observation.json').write_text(json.dumps(dict(head=head,files=actual,porcelain=git('status','--porcelain'),staged=git('diff','--cached','--name-only'),production_gate_open=False,goal_complete=False,independent_review=False),ensure_ascii=False,indent=2),encoding='utf-8');print(head,len(actual))
