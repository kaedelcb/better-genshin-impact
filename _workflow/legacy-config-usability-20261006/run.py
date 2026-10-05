from pathlib import Path
import hashlib,json,os,sys
root=Path.cwd()
sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
source=root/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
with storage.Session(root,'legacy-seven-config-read-only-usability-probe') as budget:
    budget.track(base)
    evidence=base/'execution'
    evidence.mkdir(exist_ok=False)
    provenance={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'Test/OneDragonMigration/Core').glob('*.cs')}
    storage.write(evidence/'source-binding.json',json.dumps(provenance).encode())
    dotnet='C:/Program Files/dotnet/dotnet.exe'
    build=[dotnet,'build',str(base/'Probe.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-o',str(base/'out')]
    code,_,_=process_runner.run(build,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='build',timeout=180)
    print('build_exit',code,flush=True)
    if code: sys.exit(code)
    code,_,_=process_runner.run([dotnet,str(base/'out/Probe.dll'),str(source),str(base/'private-probe')],cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='probe',timeout=60)
    storage.write(evidence/'result.json',json.dumps({'exit_code':code,'scope':'read-only component probe; no BGI install or activation'}).encode())
    budget.check(measure=True)
    print('probe_exit',code,flush=True)
sys.exit(code)
