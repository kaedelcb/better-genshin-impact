from pathlib import Path
import subprocess,os,hashlib,json
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
files=['MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj','MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs','MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','Test/OneDragonMigration/Core/OneDragonMigrationEngine.cs']
before={p:(root/p).read_bytes() for p in files}
def replace(p,b):
    path=root/p;tmp=path.with_suffix('.normalize-restoring');tmp.write_bytes(b);os.replace(tmp,path)
try:
    for p,b in before.items():replace(p,b.replace(b'\r\n',b'\n'))
    with (base/'normalization-final-commit.txt').open('w',encoding='utf-8') as log:
        result=subprocess.run(['git','-c','core.longpaths=true','commit','--only','-m','chore(migration): retain repository line ending normalization','--',*files],cwd=root,stdout=log,stderr=subprocess.STDOUT)
    assert result.returncode==0,'normalization commit failed'
finally:
    for p,b in before.items():replace(p,b);assert (root/p).read_bytes()==b
    (base/'normalization-source-restored.json').write_text(json.dumps({p:hashlib.sha256(b).hexdigest() for p,b in before.items()},indent=2),encoding='utf-8')
print(subprocess.check_output(['git','log','-1','--format=%H %s'],cwd=root,text=True).strip())
