from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;stage=sys.argv[1];ev=base/('fence-'+stage);target=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant';candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3/Tools/MultiplayerHoeingAssistant';dotnet='C:/Program Files/dotnet/dotnet.exe'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
class BuildSession(s.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
def write(p,v):s.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
def run(name,args):
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name,timeout=1800)
    write(ev/(name+'-argv.json'),dict(argv=args,exit_code=code));print(name,code,flush=True);return code
def build(project,out):return [dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(out)]
with BuildSession(root,'actual-fresh-stop-fence-wire-'+stage) as budget:
    budget.track(ev);budget.track(target);ev.mkdir(exist_ok=False)
    source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStopAuthority.cs'
    write(ev/'source-before.json',dict(path=str(source),sha256=sha(source),bytes=source.stat().st_size));s.write(ev/'source-before.cs',source.read_bytes())
    if stage=='green':
        budget.track(candidate);assert run('runtime-build',build('MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj',candidate))==0
    assert run('build',build('Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj',target))==0
    if stage=='green':
        for p in candidate.glob('MultiplayerHoeingAssistant.*'):
            if p.suffix in ['.dll','.exe','.pdb','.json']:s.write(target/p.name,p.read_bytes(),mode='wb')
        for p in (root/'_workflow/runtime-unified-01a10c87/regression-r2/probe').glob('ControlledWriterProbe.*'):
            s.write(target/p.name,p.read_bytes(),mode='wb' if (target/p.name).exists() else 'xb')
    phases=['targeted']+(['full'] if stage=='green' else [])
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    for phase in phases:
        args=[dotnet,'vstest',str(target/'MultiplayerHoeingAssistant.UnitTest.dll'),'--logger:trx;LogFileName='+phase+'.trx','--ResultsDirectory:'+str(ev)]
        if phase=='targeted':args.append('--TestCaseFilter:FullyQualifiedName~StopFenceWireCompatibilityTests|FullyQualifiedName~StopAuthorityPersistenceTests|FullyQualifiedName~BgiWorkflowExecutionBoundaryPortSeamTests')
        code=run(phase,args);tree=ET.parse(ev/(phase+'.trx'));rows=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:3500]) for n in tree.findall('.//t:UnitTestResult',ns)]
        result=dict(counters=tree.find('.//t:Counters',ns).attrib,failed=[n for n in rows if n['outcome']=='Failed'],wire_tests=[n for n in rows if 'StopFenceWireCompatibility' in n['name']],exit_code=code)
        write(ev/(phase+'-summary.json'),result)
    write(ev/'source-after.json',dict(path=str(source),sha256=sha(source),bytes=source.stat().st_size));write(ev/'module.json',dict(path=str(target/'MultiplayerHoeingAssistant.dll'),sha256=sha(target/'MultiplayerHoeingAssistant.dll')))
    budget.check(measure=True)
