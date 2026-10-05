from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;focused=len(sys.argv)>1 and sys.argv[1]=='focused';ev=base/('bgi-focused' if focused else 'bgi-sequential');target=root/'_workflow/runtime-unified-01a10cef/single-tests/bgi';old=root/'_workflow/runtime-unified-01a10cef/candidate-r2';new=root/'_workflow/runtime-unified-01a10cef/candidate-r3';dotnet='C:/Program Files/dotnet/dotnet.exe'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def write(p,v):s.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
class BuildSession(s.Session):
    def check(self,*a,**kw):
        for attempt in range(5):
            try:return super().check(*a,**kw)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
def inject(source):
    records=[]
    for p in source.glob('BetterGI.*'):
        if p.suffix in ['.dll','.exe','.pdb','.json']:
            dst=target/p.name;s.write(dst,p.read_bytes(),mode='wb');records.append(dict(path=str(dst),source=str(p),sha256=sha(dst)))
    return records
with BuildSession(root,'same-carrier-bgi-whole-suite-sequential-baseline-and-candidate') as budget:
    budget.track(ev);budget.track(target);ev.mkdir(exist_ok=False)
    settings=target/'xunit.runner.json'
    if settings.exists():s.write(ev/'settings-before.json',settings.read_bytes())
    data=json.dumps(dict(parallelizeTestCollections=False,maxParallelThreads=1,diagnosticMessages=True)).encode()
    s.write(settings,data,mode='wb' if settings.exists() else 'xb')
    write(ev/'conditions.json',dict(settings=json.loads(data),settings_sha=sha(settings),test_module_sha=sha(target/'BetterGenshinImpact.UnitTest.dll'),all_tests_selected=True,reason='prior 611-result run aborted; current parallel suite exposes global App initialization failure',source_not_modified=True))
    results=[];ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    if focused:
        args=[dotnet,'build',str(base/'live-ipc/LiveIpc.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-o',str(base/'live-ipc/out')]
        code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase='counterexample-helper-build',timeout=300)
        assert code==0
    try:
        for name,source in [('baseline',old),('candidate',new)]:
            mapping=inject(source);write(ev/(name+'-modules.json'),mapping)
            args=[dotnet,'vstest',str(target/'BetterGenshinImpact.UnitTest.dll'),'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
            if focused:args.append('--TestCaseFilter:FullyQualifiedName~StandardConfigurationMigrationTests.ActualPhysicalSlotRejectsBusyAndAllowsPassiveCaptureWithoutJobs')
            code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name,timeout=900)
            tree=ET.parse(ev/(name+'.trx'));rows=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:3500]) for n in tree.findall('.//t:UnitTestResult',ns)]
            result=dict(name=name,argv=args,exit_code=code,counters=tree.find('.//t:Counters',ns).attrib,failed=[r for r in rows if r['outcome']=='Failed'],native=[r for r in rows if 'NativeSingleCatalogTests' in r['name']])
            write(ev/(name+'-summary.json'),result);results.append(result)
            assert all(sha(Path(r['path']))==r['sha256'] for r in mapping)
            print(name,result['counters'],flush=True)
            if focused:
                directory=ev/(name+'-property');directory.mkdir();budget.track(directory)
                args=[dotnet,str(base/'live-ipc/out/LiveIpc.dll'),str(target),str(directory),'unused','property']
                code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=directory,recovery_directory=base,phase='property',timeout=120)
                write(directory/'argv.json',dict(argv=args,exit_code=code));assert code==0
    finally:
        mapping=inject(new);write(ev/'candidate-modules-final-readback.json',mapping)
    write(ev/'summary.json',results)
    budget.check(measure=True)
