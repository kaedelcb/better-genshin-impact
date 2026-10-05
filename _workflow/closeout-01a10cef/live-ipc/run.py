from pathlib import Path
import sys,os,json
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;ev=base/sys.argv[1];out=base/'out';dotnet='C:/Program Files/dotnet/dotnet.exe'
candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3' if len(sys.argv)>3 and sys.argv[3]=='r3' else root/'_workflow/runtime-unified-01a10cef/candidate-r2'
with s.Session(root,'real-candidate-sdk-migration-no-game') as budget:
    budget.track(base);budget.track(candidate/'User');ev.mkdir(exist_ok=False)
    build=[dotnet,'build',str(base/'LiveIpc.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-o',str(out)]
    code,_,_=process_runner.run(build,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=ev,phase='build',timeout=300)
    s.write(ev/'build-argv.json',json.dumps(dict(argv=build,exit_code=code)).encode());print('build',code,flush=True)
    if code:sys.exit(code)
    args=[dotnet,str(out/'LiveIpc.dll'),str(candidate/'Tools/MultiplayerHoeingAssistant'),str(ev),str(base.parent/'legacy-user')]
    if len(sys.argv)>2:args.append(sys.argv[2])
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=ev,phase='real-ipc',timeout=120)
    s.write(ev/'run-argv.json',json.dumps(dict(argv=args,exit_code=code)).encode());print('run',code,flush=True)
    budget.check(measure=True)
sys.exit(code)
