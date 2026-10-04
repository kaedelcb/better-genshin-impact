from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=Path(__file__).parent;prefix=d.as_posix();files=['MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs','_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']
def git(*args):return subprocess.run(['git',*args],capture_output=True,text=True,encoding='utf-8',check=True).stdout.strip()
assert git('branch','--show-current')=='main-OldTeaBag-B168'
o=json.loads((d/'final/candidate-observation.json').read_text(encoding='utf-8'));assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in o['sources'])
new=git('ls-files','--others','--exclude-standard','--',prefix).splitlines();new=[x for x in new if not x.endswith('/created-thread-observation.json')];files+=new;files=sorted(set(files));before=dict(head=git('rev-parse','HEAD'),status=git('status','--porcelain'),staged=git('diff','--cached','--name-only'),paths=files)
(d/'commit-before.json').write_text(json.dumps(before,ensure_ascii=False,indent=2),encoding='utf-8');files.append((d/'commit-before.json').as_posix());files=sorted(set(files))
subprocess.run(['git','add','--',*files],check=True,capture_output=True)
result=subprocess.run(['git','commit','--only','-m','fix(task-center): verify settled archived acceptance replay','--',*files],capture_output=True,text=True,encoding='utf-8');(d/'commit-command.log').write_text(result.stdout+'\n'+result.stderr,encoding='utf-8');assert result.returncode==0,result.stdout+result.stderr
commit=git('rev-parse','HEAD');actual=git('diff-tree','--no-commit-id','--name-only','-r',commit).splitlines();assert set(actual)==set(files)
assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in o['sources'])
(d/'candidate-commit-observation.json').write_text(json.dumps(dict(commit=commit,files=actual,files_count=len(actual),product_test_document_count=3,source_inputs_match=True,status_after=git('status','--porcelain'),staged_after=git('diff','--cached','--name-only'),production_gate_open=False,goal_complete=False),ensure_ascii=False,indent=2),encoding='utf-8');print(commit,len(actual),'files; 1 product source + 1 test + current handoff; candidate only')
