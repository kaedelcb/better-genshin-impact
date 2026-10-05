from pathlib import Path
import sys,os,json,hashlib,time,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;ev=base/'single-restored-full';out=root/'_workflow/runtime-unified-01a10cef/single-tests';candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3';dotnet='C:/Program Files/dotnet/dotnet.exe'
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
def build():return [dotnet,'build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(out/'assistant')]
def parse(p):
    tree=ET.parse(p);ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:3500]) for n in tree.findall('.//t:UnitTestResult',ns)]
    return dict(counters=tree.find('.//t:Counters',ns).attrib,failed=[n for n in rows if n['outcome']=='Failed'],native_tests=[n for n in rows if 'NativeSingle' in n['name']])
def test(name,dll,full):
    args=[dotnet,'vstest',str(dll),'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
    if not full:args.append('--TestCaseFilter:FullyQualifiedName~NativeSingleAuthoringTests')
    code=run(name,args);v=parse(ev/(name+'.trx'));v['exit_code']=code;write(ev/(name+'-summary.json'),v);return v
with BuildSession(root,'native-single-causal-mutations-and-restored-full-regression') as budget:
    budget.track(ev);budget.track(out);ev.mkdir(exist_ok=False)
    source=root/'MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs';original=source.read_bytes();original_sha=sha(source)
    s.write(ev/'source-original.cs',original)
    paths=['BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowMigrationConsumerTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/NativeSingleAuthoringTests.cs','Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance/NativeSingleCatalogTests.cs']
    before={p:sha(root/p) for p in paths};write(ev/'source-before.json',before)
    for name,old,new in [('owner',b'Config = src.Kind == TaskCenterResourceKind.SingleTask ? src.OwnerConfig : src.DisplayName,',b'Config = src.DisplayName,'),('task-id',b'TaskId = src.TaskId,',b'TaskId = null,')]:
        assert original.count(old)==1 and sha(source)==original_sha and source.stat().st_nlink==1
        try:
            s.write(source,original.replace(old,new),mode='wb')
            write(ev/(name+'-mutation.json'),dict(source=str(source),before_sha=original_sha,mutated_sha=sha(source),change=old.decode()+' -> '+new.decode()))
            assert run(name+'-build',build())==0
            result=test(name,out/'assistant/MultiplayerHoeingAssistant.UnitTest.dll',False)
            assert result['exit_code']==1 and len(result['failed'])==1 and 'NativeSingleAuthoringTests' in result['failed'][0]['name']
        finally:
            temp=source.with_name(source.name+'.01a10cef-restore');s.write(temp,original);os.replace(temp,source)
            assert sha(source)==original_sha
            write(ev/(name+'-restored.json'),dict(source_sha=sha(source),same_original_bytes=True))
    assert before=={p:sha(root/p) for p in paths}
    assert run('restored-assistant-build',build())==0
    mapping=[]
    for name in ['assistant','bgi']:
        for source_dir,prefix in [(candidate/'Tools/MultiplayerHoeingAssistant','MultiplayerHoeingAssistant.')]+([(candidate,'BetterGI.')] if name=='bgi' else []):
            for p in source_dir.glob(prefix+'*'):
                if p.suffix in ['.dll','.exe','.pdb','.json']:
                    dst=out/name/p.name;s.write(dst,p.read_bytes(),mode='wb');mapping.append(dict(path=str(dst),source=str(p),sha256=sha(dst)))
    write(ev/'candidate-modules-before.json',mapping)
    for p in (root/'_workflow/runtime-unified-01a10c87/regression-r2/probe').glob('ControlledWriterProbe.*'):
        s.write(out/'assistant'/p.name,p.read_bytes(),mode='wb' if (out/'assistant'/p.name).exists() else 'xb')
    results=[]
    for name,dll in [('assistant','MultiplayerHoeingAssistant.UnitTest.dll'),('bgi','BetterGenshinImpact.UnitTest.dll')]:
        result=test(name+'-full',out/name/dll,True);results.append(dict(name=name,**result))
    assert all(sha(Path(r['path']))==r['sha256'] for r in mapping)
    assert before=={p:sha(root/p) for p in paths}
    write(ev/'source-after.json',before);write(ev/'summary.json',results);write(ev/'candidate-module-readback.json',dict(all_same_sha=True,files=len(mapping)))
    print(json.dumps([dict(name=v['name'],counters=v['counters']) for v in results]),flush=True)
    budget.check(measure=True)
