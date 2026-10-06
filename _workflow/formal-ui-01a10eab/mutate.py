from pathlib import Path
import sys,os,json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
subject=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs'
carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sha=lambda data:hashlib.sha256(data).hexdigest()
def execute(directory,filter_text):
    directory.mkdir(exist_ok=False)
    args=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=directory,recovery_directory=base,phase='build',timeout=1200)
    if code==0:
        args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filter_text,'--logger:trx;LogFileName=causal.trx','--ResultsDirectory:'+str(directory)]
        env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(directory)
        code,_,_=process_runner.run(args,cwd=root,env=env,directory=directory,recovery_directory=base,phase='test',timeout=600)
    storage.write(directory/'result.json',json.dumps(dict(exit_code=code,argv=args)).encode())
    return code
with storage.Session(root,'formal-ui-node-time-causal-restoration') as budget:
    for path in (base,carrier,root/'MultiplayerHoeingAssistant/obj',root/'Test/MultiplayerHoeingAssistant.UnitTest/obj',subject):budget.track(path)
    original=subject.read_bytes();expected=json.loads((base/'build-r6/source-hashes.json').read_text())[subject.relative_to(root).as_posix()]
    assert sha(original)==expected
    token=b'LastOrDefault(s => s.Kind == "schedule.time")';assert original.count(token)==1
    mutant=original.replace(token,b'LastOrDefault(s => s.Kind == "schedule.time-disabled")')
    storage.write(base/'causal-before.cs',original)
    try:
        storage.write(subject,mutant,mode='wb')
        assert execute(base/'causal-negative','FullyQualifiedName~WaitingNode_StopPersistsCancellationWithoutSubmittingOrCompleting')!=0
        trx=ET.parse(base/'causal-negative/causal.trx');results=[e for e in trx.iter() if e.tag.endswith('UnitTestResult')]
        assert len(results)==1 and results[0].get('outcome')=='Failed'
        assert 'TimeoutException' in (base/'causal-negative/test-stdout.log').read_text(encoding='utf-8-sig')
    finally:
        storage.write(subject,original,mode='wb');assert sha(subject.read_bytes())==expected
        print('source restored',expected,flush=True)
    filters='FullyQualifiedName~FormalSchedule|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowRunnerTests|FullyQualifiedName~WorkflowPlannerTests'
    code=execute(base/'causal-restored',filters)
    assert sha(subject.read_bytes())==expected
    storage.write(base/'causal-result.json',json.dumps(dict(baseline='build-r6',baseline_passed=31,mutant_sha256=sha(mutant),restored_sha256=expected,restored_exit=code,critical_assertion='WaitingNode_StopPersistsCancellationWithoutSubmittingOrCompleting',independent_review=False)).encode())
    print('restored_exit',code,flush=True)
    assert code==0
