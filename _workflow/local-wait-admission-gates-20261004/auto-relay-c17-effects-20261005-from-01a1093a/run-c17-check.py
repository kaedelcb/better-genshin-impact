from pathlib import Path
import sys,subprocess,json,time,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a'
out=Path('E:/BgiVersionCheck-01a10954/c17-products');out.mkdir(parents=True,exist_ok=True)
ev=base/sys.argv[1];ev.mkdir(exist_ok=False)
dotnet='C:/Program Files/dotnet/dotnet.exe'
def run(name,args):
 row=dict(argv=args,started=time.time())
 with (ev/(name+'.log')).open('w',encoding='utf-8') as log:
  p=subprocess.Popen(args,cwd=root,stdout=log,stderr=subprocess.STDOUT);row.update(pid=p.pid,exit_code=p.wait(),ended=time.time())
 (ev/(name+'-process.json')).write_text(json.dumps(row,indent=2),encoding='utf-8');print(name,row['exit_code'],flush=True)
 return row['exit_code']
code=0
for project,dll,filter in [('Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','MultiplayerHoeingAssistant.UnitTest.dll',sys.argv[2])]+([] if len(sys.argv)<4 else [('Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj','BetterGenshinImpact.UnitTest.dll',sys.argv[3])]):
 name=dll.split('.')[0]
 build=run(name+'-build',[dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(out)])
 code=code or build
 if build:break
 test=run(name+'-test',[dotnet,'vstest',str(out/dll),'--TestCaseFilter:'+filter,'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)])
 code=code or test
 if test:break
(ev/'product-observation.json').write_text(json.dumps([dict(path=str(p),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in sorted(out.iterdir()) if p.is_file()],indent=2),encoding='utf-8')
sys.exit(code)
