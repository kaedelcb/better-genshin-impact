from pathlib import Path
import hashlib,json,os,sys,time
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/bgi'
class BuildSession(storage.Session):
    def check(self,*args,**kwargs):
        for attempt in range(5):
            try: return super().check(*args,**kwargs)
            except FileNotFoundError:
                if attempt==4: raise
                time.sleep(.1)
with BuildSession(root,'legacy-config-migration-ui-rebuild-fixed-carrier') as budget:
    budget.track(base);budget.track(carrier)
    evidence=base/'rebuild';evidence.mkdir(exist_ok=False)
    binding={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in [root/'BetterGenshinImpact/View/Pages/OneDragonFlowPage.xaml',root/'BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs']}
    storage.write(evidence/'source-binding.json',json.dumps(binding).encode())
    argv=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'BetterGenshinImpact/BetterGenshinImpact.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
    code,_,_=process_runner.run(argv,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='rebuild',timeout=1200)
    modules={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in carrier.glob('BetterGI.*') if p.suffix in ('.dll','.exe','.pdb','.json')}
    storage.write(evidence/'result.json',json.dumps({'argv':argv,'exit_code':code,'modules':modules,'scope':'UI source Rebuild only; no product acceptance'}).encode())
    assert all(hashlib.sha256((root/p).read_bytes()).hexdigest()==v for p,v in binding.items())
    print('rebuild_exit',code,flush=True)
    budget.check(measure=True)
sys.exit(code)
