from pathlib import Path
import sys, os, json, hashlib, time, subprocess
root=Path.cwd(); sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
out=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
ev=base/'build-r1'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def sources():
    paths=[]
    for d in ['BetterGenshinImpact','MultiplayerHoeingAssistant','Fischless.GameCapture','Fischless.HotkeyCapture','Fischless.WindowsInput','BgiCoordinatorServer/Gateway','Test/OneDragonMigration/Core']:
        for p in (root/d).rglob('*'):
            if p.is_file() and p.suffix in ['.cs','.xaml','.csproj','.json','.props','.targets'] and not set(p.relative_to(root/d).parts)&{'bin','obj','User','.kiro'}: paths.append(p)
    return {str(p.relative_to(root)):sha(p) for p in paths}
class BuildSession(storage.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
with BuildSession(root,'unified-complete-runtime-candidate') as budget:
    budget.track(ev);budget.track(out);budget.check(location=out)
    ev.mkdir(exist_ok=False);out.mkdir(parents=True,exist_ok=False)
    before=sources();storage.write(ev/'source-before.json',json.dumps(before,indent=2).encode())
    storage.write(ev/'git-head.txt',subprocess.check_output(['git','rev-parse','HEAD']))
    storage.write(ev/'git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain'],stderr=subprocess.STDOUT))
    dotnet='C:/Program Files/dotnet/dotnet.exe'
    storage.write(ev/'dotnet-info.txt',subprocess.check_output([dotnet,'--info']))
    for name,project,target in [('bgi','BetterGenshinImpact/BetterGenshinImpact.csproj',out),('assistant','MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj',out/'Tools/MultiplayerHoeingAssistant')]:
        target.mkdir(parents=True,exist_ok=True)
        args=[dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(target)]
        code,stdout,stderr=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name+'-build',timeout=1800)
        storage.write(ev/(name+'-argv.json'),json.dumps({'argv':args,'exit_code':code,'evidence_level':'ordinary controlled build, not certified receipt'},indent=2).encode())
        print(name,code,flush=True)
        if code:sys.exit(code)
    after=sources();storage.write(ev/'source-after.json',json.dumps(after,indent=2).encode());assert before==after,'source drift'
    files=[dict(path=str(p.relative_to(out)),bytes=p.stat().st_size,sha256=sha(p)) for p in out.rglob('*') if p.is_file()]
    storage.write(ev/'runtime-files.json',json.dumps(files,indent=2).encode())
    storage.write(ev/'build-summary.json',json.dumps(dict(runtime=str(out),head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),files=len(files),bytes=sum(x['bytes'] for x in files),source_unchanged=True,copy_local_dependencies='SDK default; full copy',user='separate writable candidate User; no live data copied',accepted=False),indent=2).encode())
    print('runtime',out,'files',len(files),'bytes',sum(x['bytes'] for x in files),flush=True)
    budget.check(measure=True)
