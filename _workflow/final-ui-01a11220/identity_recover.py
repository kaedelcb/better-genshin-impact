"""Verify the actual atomic source recovery, then rebuild/retest and refresh own modules."""
from pathlib import Path
import hashlib,json,os,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'flow-identity'
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha=lambda b:hashlib.sha256(b).hexdigest()
with s.Session(ROOT,'own-root-01a113cc-atomic-recovery-regression-refresh') as budget:
    budget.track(BASE)
    out=BASE/'restored-recovery';out.mkdir(exist_ok=False)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    old=json.loads((ROOT/'.git/mistletoe-storage-control/ledger.json').read_text(encoding='utf-8'))
    entry=next(e for e in old['entries'] if e['id']=='9880ab5a19d44eb09fb35ba65907ee0b');assert entry['state']=='failed'
    terminals=[]
    for stage in ['red','green','negative']:
        for job in ['build','test']:
            file=BASE/stage/(job+'-tree-terminal.json');value=json.loads(file.read_text(encoding='utf-8'));assert value['active_processes']==0
            terminals.append(dict(stage=stage,job=job,source=str(file),value=value))
    hashes=json.loads((BASE/'green/source-hashes.json').read_text(encoding='utf-8'))
    assert all(sha((ROOT/rel).read_bytes())==h for rel,h in hashes.items()),'source changed since green'
    assert sha((ROOT/'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml').read_bytes())=='94d2342b72565bc3756a8969b7d53550ab5afefdab8850fbdf717ac6a29c0ce1'
    assert not list((ROOT/'MultiplayerHoeingAssistant/Views').glob('ScheduleListView.xaml.identity-restore-*.tmp'))
    write(out/'recovery.json',dict(original_operation=entry,original_error='OSError errno22 during direct finally overwrite; native source preserved',atomic_replace_verified=True,source_drift=[],six_job_terminals=terminals,original_failure_retained=True,not_relabelled_as_original_success=True))
    write(out/'source-hashes.json',hashes)
    carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant';budget.track(carrier)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
    env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(out/'samples');env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['TEMP']=env['TMP']=str(out/'temp')
    Path(env['FORMAL_UI_EVIDENCE']).mkdir();Path(env['TEMP']).mkdir()
    print('ATOMIC_RECOVERY_VERIFIED: green source bytes, six prior Jobs terminal, failed operation retained',flush=True)
    args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
    build,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
    assert build==0
    filt='FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=identity.trx','--ResultsDirectory:'+str(out)]
    code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
    rows=ET.parse(out/'identity.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    result=dict(stage='restored-recovery',build=build,test=code,source_drift=[rel for rel,h in hashes.items() if sha((ROOT/rel).read_bytes())!=h])
    for kind in ['Passed','Failed','NotExecuted']:result[kind]=[r.get('testName') for r in rows if r.get('outcome')==kind]
    write(out/'result.json',result)
    green=json.loads((BASE/'green/result.json').read_text(encoding='utf-8'))
    assert code==0 and not result['Failed'] and not result['NotExecuted'] and not result['source_drift'] and sorted(result['Passed'])==sorted(green['Passed'])
    for job in ['build','test']:assert json.loads((out/(job+'-tree-terminal.json')).read_text(encoding='utf-8'))['active_processes']==0
    print('RESTORED_RECOVERY',len(result['Passed']),'passed; zero failures/skips/drift; both Jobs terminal',flush=True)
    product=ROOT/'_workflow/runtime-unified-01a10e1b/product';runtime=BASE.parent/'own-runtime'
    prior=json.loads((runtime/'refresh-context/result.json').read_text(encoding='utf-8'))
    assert sha((product/'BetterGI.dll').read_bytes())==prior['bgi_sha256'] and sha((product/'BetterGI.exe').read_bytes())==prior['bgi_exe_sha256']
    assert all(sha((product/m['path']).read_bytes())==m['sha256'] for m in prior['updated'])
    refreshed=runtime/'refresh-identity';refreshed.mkdir(exist_ok=False);budget.track(refreshed);budget.track(product/'Tools/MultiplayerHoeingAssistant');budget.track(product/'User')
    before={f.relative_to(product/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(product/'User')};write(refreshed/'private/product-user-before.json',before)
    updated=[]
    for name in ['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb','MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json']:
        target=product/'Tools/MultiplayerHoeingAssistant'/name;s.write(refreshed/'before'/name,target.read_bytes());s.write(target,(carrier/name).read_bytes(),mode='wb')
        assert target.read_bytes()==(carrier/name).read_bytes();updated.append(dict(path=target.relative_to(product).as_posix(),sha256=sha(target.read_bytes())))
    after={f.relative_to(product/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(product/'User')};assert before==after
    write(refreshed/'result.json',dict(marker='OWN-ROOT-DEV-MATRIX-20261007-FROM-01a11380',updated=updated,bgi_sha256=prior['bgi_sha256'],bgi_exe_sha256=prior['bgi_exe_sha256'],product_user_changed=[],user_files=len(before),source_hashes=str(out/'source-hashes.json'),original_operation_failed=True,atomic_recovery_verified=True,review_requests_new=0,independent_review=False,product_complete=False))
    print('IDENTITY_PRODUCT_REFRESHED; five modules; BGI/User byte-identical; actual UI pending',flush=True)
