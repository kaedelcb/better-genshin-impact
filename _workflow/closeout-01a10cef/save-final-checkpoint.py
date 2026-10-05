from pathlib import Path
import sys,subprocess,json
root=Path.cwd();base=Path(__file__).resolve().parent
files=['BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStopAuthority.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/NativeSingleAuthoringTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/StopFenceWireCompatibilityTests.cs','_workflow/storage-policy.json','_workflow/local-wait-admission-gates-20261004/plan.json']
for p in base.rglob('*'):
    if not p.is_file() or p.suffix not in ['.py','.md','.json','.trx','.log','.txt','.csproj','.cs']:continue
    rel=p.relative_to(base).as_posix()
    if any(part in ['bin','obj','out','modules','host-data','legacy-user','private-user-backup'] for part in p.relative_to(base).parts):continue
    if 'private' in rel or rel.startswith('ui-data-recovery') or rel.endswith('test-files-before-archive.json'):continue
    if rel.startswith('ui-r3/readback-') or rel.startswith('ui-appdata-physical'):continue
    if rel in ['FINAL-COMMIT.json','completion-audit.json']:continue
    files.append(p.relative_to(root).as_posix())
files=sorted(set(files))
assert subprocess.check_output(['git','diff','--cached','--name-only'],cwd=root)==b''
before=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip()
tracked=set(subprocess.check_output(['git','ls-files','-z'],cwd=root).decode().split('\0'))
new=[p for p in files if p not in tracked]
for i in range(0,len(new),24):subprocess.run(['git','-c','core.longpaths=true','add','--intent-to-add','--',*new[i:i+24]],cwd=root,check=True,capture_output=True)
pathspec=base/'final-explicit-pathspec.txt'
pathspec.write_bytes(('\0'.join(files)+'\0').encode('utf-8'))
result=subprocess.run(['git','-c','core.longpaths=true','commit','--only','-m','fix: expose native single authoring and accept fresh stop-fence wire shape','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul'],cwd=root,capture_output=True)
sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
with s.Session(root,'candidate-final-local-checkpoint-readback'):
    s.write(base/'final-commit-stdout.log',result.stdout);s.write(base/'final-commit-stderr.log',result.stderr)
    if result.returncode:print(result.stderr.decode('utf-8',errors='replace'));sys.exit(result.returncode)
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip()
    actual=subprocess.check_output(['git','show','--format=','--name-only','HEAD'],cwd=root).decode().splitlines()
    assert set(actual).issubset(set(files))
    required=['BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStopAuthority.cs','_workflow/storage-policy.json','_workflow/closeout-01a10cef/VALIDATION.md']
    assert all(p in actual for p in required)
    assert not subprocess.check_output(['git','diff','--cached','--name-only'],cwd=root)
    s.write(base/'FINAL-COMMIT.json',json.dumps(dict(before_head=before,head=head,files=actual,staged_empty=True,product_accepted=False,independent_repair_review=False),ensure_ascii=False,indent=2).encode())
    print(json.dumps(dict(head=head,files=len(actual),staged_empty=True)),flush=True)
