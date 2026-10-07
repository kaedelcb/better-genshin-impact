from pathlib import Path
import hashlib,json,subprocess
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
def git(*args):return subprocess.check_output(['git','-c','core.longpaths=true',*args],cwd=ROOT)
assert not (ROOT/'.git/mistletoe-storage-control/writer.lock').exists(), 'budget writer still active'
checkpoint=json.loads((BASE/'flow-context/checkpoint.json').read_text(encoding='utf-8'));assert git('rev-parse','HEAD').decode().strip()==checkpoint['head_before_commit']
hashes=json.loads((BASE/'flow-context/causal/restored/source-hashes.json').read_text(encoding='utf-8'))
assert all(hashlib.sha256((ROOT/n).read_bytes()).hexdigest()==h for n,h in hashes.items())
files=['MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/ViewModels/WorkflowUndoVm.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalFlowContextTests.cs']
files += [(BASE/n).relative_to(ROOT).as_posix() for n in ['flow_context_check.py','flow_context_fix.py','flow_context_causal.py','flow_context_refresh.py','runtime_context.py','context_checkpoint.py','context_commit.py']]
for f in (BASE/'flow-context').rglob('*'):
 if f.is_file() and not any(part in ['temp','samples'] for part in f.relative_to(BASE/'flow-context').parts):files.append(f.relative_to(ROOT).as_posix())
files += [(BASE/'own-runtime/refresh-context'/n).relative_to(ROOT).as_posix() for n in ['result.json','private/product-user-before.json']]
files=sorted(set(files));assert all((ROOT/n).is_file() for n in files)
before=set(git('diff','--cached','--name-only').decode().splitlines())
git('add','--',*files)
result=subprocess.run(['git','-c','core.longpaths=true','commit','--only','-m','fix: keep schedule workflow selection and activation context consistent','--',*files],cwd=ROOT,capture_output=True,check=True)
actual=set(git('show','--format=','--name-only','HEAD').decode().splitlines());assert actual==set(files)
after=set(git('diff','--cached','--name-only').decode().splitlines());assert after==before-set(files)
assert all(hashlib.sha256((ROOT/n).read_bytes()).hexdigest()==h for n,h in hashes.items())
print(json.dumps(dict(head=git('rev-parse','HEAD').decode().strip(),files=len(actual),staged_other_preserved=after==before-set(files),source_drift=[],kind='local source/causal checkpoint; runtime and independent review pending'),ensure_ascii=False))
