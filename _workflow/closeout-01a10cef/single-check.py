from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;stage=sys.argv[1];ev=base/('single-'+stage);outputs=root/'_workflow/runtime-unified-01a10cef/single-tests';candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3';dotnet='C:/Program Files/dotnet/dotnet.exe'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
class BuildSession(s.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
def run(name,args):
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name,timeout=1800)
    s.write(ev/(name+'-argv.json'),json.dumps(dict(argv=args,exit_code=code)).encode());print(name,code,flush=True);return code
def build(project,target):return [dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(target)]
with BuildSession(root,'native-single-authoring-'+stage) as budget:
    budget.track(ev);budget.track(outputs)
    ev.mkdir(exist_ok=False);budget.check(location=outputs)
    if stage.startswith('green'):
        if stage=='green':assert not candidate.exists()
        else:assert candidate.exists()
        budget.track(candidate)
        for name,project,target in [('bgi','BetterGenshinImpact/BetterGenshinImpact.csproj',candidate),('assistant','MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj',candidate/'Tools/MultiplayerHoeingAssistant')]:
            if stage!='green' and name=='bgi':continue
            if run(name+'-runtime-build',build(project,target)):sys.exit(1)
        # Only exact previously verified route resources; full default runtime build supplies normal dependencies.
        previous=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
        prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954/auto-standard-install-01a10c1c/01a10c87-deee-7381-a61d-fefce264d537'
        resources=json.loads((prior/'runtime-facts/supplemental-route-resources.json').read_text())
        for row in resources:
            # The historical record supplies exact target identity; do not discover/copy a whole tree.
            relative=Path('GameTask/AutoHoeing/Assets')/row['path'];target=previous/relative
            assert sha(target)==row['sha256'];s.write(candidate/relative,target.read_bytes(),mode='wb' if (candidate/relative).exists() else 'xb')
    for name,project in [('assistant','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),('bgi','Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj')]:
        if stage!='green' and stage.startswith('green') and name=='bgi':continue
        if run(name+'-build',build(project,outputs/name)):sys.exit(1)
    mapping=[]
    for name in ['assistant','bgi']:
        target=outputs/name
        if stage.startswith('green'):
            for source,prefix in [(candidate/'Tools/MultiplayerHoeingAssistant','MultiplayerHoeingAssistant.')]+([(candidate,'BetterGI.')] if name=='bgi' else []):
                for p in source.glob(prefix+'*'):
                    if p.suffix in ['.dll','.exe','.pdb','.json']:s.write(target/p.name,p.read_bytes(),mode='wb')
            for p in (root/'_workflow/runtime-unified-01a10c87/regression-r2/probe').glob('ControlledWriterProbe.*'):s.write(target/p.name,p.read_bytes(),mode='wb' if (target/p.name).exists() else 'xb')
        for p in target.glob('*.dll'):
            if p.name.startswith(('BetterGI','MultiplayerHoeingAssistant')):
                mapping.append(dict(path=str(p),sha256=sha(p)));s.write(ev/'modules'/name/p.name,p.read_bytes())
    s.write(ev/'modules-before.json',json.dumps(mapping,indent=2).encode())
    summaries=[];ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    for name,dll in [('assistant','MultiplayerHoeingAssistant.UnitTest.dll'),('bgi','BetterGenshinImpact.UnitTest.dll')]:
        args=[dotnet,'vstest',str(outputs/name/dll),'--TestCaseFilter:FullyQualifiedName~NativeSingleAuthoringTests|FullyQualifiedName~NativeSingleCatalogTests','--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
        code=run(name+'-targeted',args);tree=ET.parse(ev/(name+'.trx'))
        summaries.append(dict(name=name,exit_code=code,counters=tree.find('.//t:Counters',ns).attrib,failed=[n.attrib['testName'] for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome']=='Failed']))
    s.write(ev/'summary.json',json.dumps(summaries,indent=2).encode())
    assert all(sha(Path(r['path']))==r['sha256'] for r in mapping)
    budget.check(measure=True)
