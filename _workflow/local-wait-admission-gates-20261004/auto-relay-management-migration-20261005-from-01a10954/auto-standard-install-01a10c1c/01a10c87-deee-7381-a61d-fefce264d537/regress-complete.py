from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent;candidate=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
ev=base/'regression-r2';outputs=root/'_workflow/runtime-unified-01a10c87/regression-r2'
dotnet='C:/Program Files/dotnet/dotnet.exe'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
class BuildSession(storage.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
def run(budget,phase,args):
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=phase,timeout=1800)
    storage.write(ev/(phase+'-argv.json'),json.dumps(dict(argv=args,exit_code=code,level='ordinary controlled execution'),indent=2).encode())
    print(phase,code,flush=True);return code
with BuildSession(root,'unified-candidate-complete-test-fixtures') as budget:
    budget.track(ev);budget.track(outputs);budget.check(location=outputs)
    ev.mkdir(exist_ok=False);outputs.mkdir(exist_ok=False)
    # Each test process has its own dependency graph. Never overlay MHA 8.x
    # package DLLs on BGI's 9.x package DLLs in a single test root.
    builds=[('probe','Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj'),('assistant','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),('bgi','Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj')]
    for name,project in builds:
        target=outputs/name;target.mkdir()
        args=[dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(target)]
        if run(budget,name+'-build',args):sys.exit(1)
    mapping=[]
    for name in ['assistant','bgi']:
        target=outputs/name
        for p in (outputs/'probe').glob('ControlledWriterProbe.*'):
            storage.write(target/p.name,p.read_bytes());mapping.append(dict(role='probe',path=str(target/p.name),sha256=sha(p)))
        # Exact executable candidate assemblies; test project's resolved packages
        # retain its independently built runtime graph and fixture content.
        sources=[candidate/'Tools/MultiplayerHoeingAssistant']+([candidate] if name=='bgi' else [])
        for source in sources:
            prefix='BetterGI.' if source==candidate else 'MultiplayerHoeingAssistant.'
            for p in source.glob(prefix+'*'):
                if p.suffix not in ['.dll','.exe','.pdb','.json']:continue
                storage.write(target/p.name,p.read_bytes(),mode='wb')
                mapping.append(dict(role='candidate-product',path=str(target/p.name),source=str(p),sha256=sha(p)))
    storage.write(ev/'products-before.json',json.dumps(mapping,indent=2).encode())
    results=[]
    for name,dll in [('assistant','MultiplayerHoeingAssistant.UnitTest.dll'),('bgi','BetterGenshinImpact.UnitTest.dll')]:
        args=[dotnet,'vstest',str(outputs/name/dll),'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
        code=run(budget,name+'-full',args)
        trx=ET.parse(ev/(name+'.trx'));ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        counters=trx.find('.//t:Counters',ns).attrib
        failures=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:3500]) for n in trx.findall('.//t:UnitTestResult',ns) if n.attrib['outcome'] not in ['Passed','NotExecuted']]
        results.append(dict(name=name,exit_code=code,counters=counters,failures=failures));print(name,counters,flush=True)
    storage.write(ev/'summary.json',json.dumps(results,indent=2).encode())
    for item in mapping:assert sha(Path(item['path']))==item['sha256'],'test product drift'
    storage.write(ev/'product-readback.json',json.dumps(dict(all_same_sha=True,files=len(mapping)),indent=2).encode())
    budget.check(measure=True)
