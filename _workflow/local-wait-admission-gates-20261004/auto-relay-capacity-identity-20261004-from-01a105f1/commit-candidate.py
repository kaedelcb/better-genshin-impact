from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1'
scope=str(d.relative_to(r)).replace('\\','/')
code=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']
def git(*args):return subprocess.check_output(['git',*args],text=True,encoding='utf-8').strip()
assert git('branch','--show-current')=='main-OldTeaBag-B168'
files=code+git('ls-files','--others','--exclude-standard','--',scope).splitlines()
assert all(p.startswith(scope+'/') or p in code for p in files)
assert len(files)==len(set(files));assert all((r/p).is_file() for p in files)
obs=dict(branch=git('branch','--show-current'),head_before=git('rev-parse','HEAD'),files=files,staged_before=git('diff','--cached','--name-only'),scoped_hashes={p:hashlib.sha256((r/p).read_bytes()).hexdigest() for p in code})
(d/'precommit-observation.json').write_text(json.dumps(obs,indent=2));files.append(str((d/'precommit-observation.json').relative_to(r)).replace('\\','/'))
subprocess.run(['git','add','--',*[p for p in files if p not in code]],check=True)
with (d/'candidate-commit.log').open('wb') as f:subprocess.run(['git','commit','--only','-m','test: verify 33 actual admissions and preserve archived send identity (candidate)','--',*files],stdout=f,stderr=subprocess.STDOUT,check=True)
commit=git('rev-parse','HEAD');actual=git('diff-tree','--no-commit-id','--name-only','-r',commit).splitlines();assert set(actual)==set(files)
out=dict(commit=commit,branch=git('branch','--show-current'),files=actual,staged_after=git('diff','--cached','--name-only'),source_hashes={p:hashlib.sha256((r/p).read_bytes()).hexdigest() for p in code},quality='candidate, not independently reviewed/certified/accepted',production_gate_open=False)
assert out['staged_after']==obs['staged_before'];assert out['source_hashes']==obs['scoped_hashes']
(d/'candidate-commit-observation.json').write_text(json.dumps(out,indent=2));print(commit,len(actual),flush=True)
