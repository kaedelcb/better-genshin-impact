from pathlib import Path
import sys,os,json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
import process_runner
base=Path(__file__).resolve().parent;carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
changes={
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs':(b'initialCursor: plan.ExplicitEntryCursor(entryNodeId)',b'initialCursor: null'),
 'MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs':(b'Nodes.Move(from, destination);',b'return; // mutation: drop requested drag\n        Nodes.Move(from, destination);')}
with s.Session(root,'full-product-entry-and-drag-mutation') as budget:
    budget.track(base);budget.track(carrier);ev=base/'mutation';ev.mkdir(exist_ok=False)
    before={rel:(root/rel).read_bytes() for rel in changes}
    for rel,data in before.items():s.write(ev/'original'/rel,data)
    def execute(phase):
        out=ev/phase;out.mkdir();build=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        code,_,_=process_runner.run(build,cwd=root,env=os.environ.copy(),directory=out,recovery_directory=ev,phase='build',timeout=1200)
        assert code==0
        args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~FullProduct_|FullyQualifiedName~FullProductUiTests','--logger:trx;LogFileName=result.trx','--ResultsDirectory:'+str(out)]
        code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=out,recovery_directory=ev,phase='test',timeout=180)
        tree=ET.parse(out/'result.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        result=dict(exit_code=code,counters=tree.find('.//t:Counters',ns).attrib,failed=[n.attrib['testName'] for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome']=='Failed'])
        s.write(out/'summary.json',json.dumps(result,indent=2).encode());print(phase,json.dumps(result),flush=True);return result
    try:
        for rel,(old,new) in changes.items():
            assert before[rel].count(old)==1;s.write(root/rel,before[rel].replace(old,new),mode='wb')
        red=execute('red')
        assert any('ExplicitEntryRunsOnlySelectedTail' in n for n in red['failed'])
        assert any('DragReorder' in n for n in red['failed'])
    finally:
        for rel,data in before.items():s.write(root/rel,data,mode='wb');assert (root/rel).read_bytes()==data
        s.write(ev/'restored-source.json',json.dumps({rel:hashlib.sha256(data).hexdigest() for rel,data in before.items()},indent=2).encode())
        green=execute('restored')
        assert green['exit_code']==0
