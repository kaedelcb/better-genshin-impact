from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();base=Path(__file__).resolve().parent;sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
phase=sys.argv[1];ev=base/'recovery-fix'/phase;out=root/'_workflow/runtime-unified-01a10c87'/('recovery-'+phase)
class BuildSession(storage.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowMigrationConsumerTests.cs']
def hashes():return {p:hashlib.sha256((root/p).read_bytes()).hexdigest() for p in paths}
def execute(name,args):
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name,timeout=1800)
    storage.write(ev/(name+'-argv.json'),json.dumps(dict(argv=args,exit_code=code,level='ordinary controlled source/actual result'),indent=2).encode());print(name,code,flush=True);return code
with BuildSession(root,'bounded-migration-recovery-'+phase) as budget:
    budget.track(ev);budget.track(out);budget.check(location=out);ev.mkdir(exist_ok=False);out.mkdir(exist_ok=False)
    before=hashes();storage.write(ev/'source-before.json',json.dumps(before,indent=2).encode())
    build=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(out)]
    code=execute('build',build)
    if code:sys.exit(code)
    for p in (root/'_workflow/runtime-unified-01a10c87/regression-r2/probe').glob('ControlledWriterProbe.*'):
        storage.write(out/p.name,p.read_bytes())
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(out/'MultiplayerHoeingAssistant.UnitTest.dll'),'--logger:trx;LogFileName=assistant.trx','--ResultsDirectory:'+str(ev)]
    if '--full' not in sys.argv:args.append('--TestCaseFilter:FullyQualifiedName~WorkflowMigrationConsumerTests|FullyQualifiedName~StandardMigrationHostTests|FullyQualifiedName~LegacyMigrationCandidateServiceTests|FullyQualifiedName~WorkflowStore')
    code=execute('test',args)
    tree=ET.parse(ev/'assistant.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counters=tree.find('.//t:Counters',ns).attrib
    failures=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:3000]) for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome'] not in ['Passed','NotExecuted']]
    products=[dict(path=p.name,bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in out.iterdir() if p.is_file()]
    after=hashes();assert before==after,'source changed during execution'
    storage.write(ev/'source-after.json',json.dumps(after,indent=2).encode());storage.write(ev/'products.json',json.dumps(products,indent=2).encode())
    storage.write(ev/'summary.json',json.dumps(dict(counters=counters,failures=failures,exit_code=code),indent=2).encode());print(counters,flush=True)
    budget.check(measure=True)
sys.exit(code)
