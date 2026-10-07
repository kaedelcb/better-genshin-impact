from pathlib import Path
import hashlib,json,os,sys,subprocess,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'flow-context'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
phase=sys.argv[1]
carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
with s.Session(ROOT,'own-root-01a11380-flow-context-'+phase) as budget:
 budget.track(BASE);out=BASE/phase;out.mkdir(parents=True,exist_ok=False)
 budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
 def write(name,value):s.write(out/name,json.dumps(value,ensure_ascii=False,indent=2).encode())
 hashes={f.relative_to(ROOT).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
 write('source-hashes.json',hashes)
 if phase=='red':
  for rel in ['MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/ViewModels/WorkflowUndoVm.cs']:
   s.write(out/('original-'+Path(rel).name),(ROOT/rel).read_bytes())
  status=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT,capture_output=True,check=True)
  s.write(out/'git-status.txt',status.stdout)
  write('admission.json',dict(marker='OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308',finding='OWN-ROOT-FLOW-SWITCH-IMPORTANT-2',level='important',policies=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1'],entry='ScheduleListView FlowChanged/SaveEditing, PanelVM draft/preview/refresh, choice label binding',consequence='same-name selection retains old runnable identity while candidate preview changes; activated label stale',matrix=['saved/clean draft to same-name candidate in shared views','invalid unsaved input blocks both views and preview change','unchanged draft with external revision conflict retained','real imported-candidate activation refreshes label/preview/actions without execution','shared undo/render/path/transfer regression'],review_requests_new=0,review_budget_remaining=0,agents='one shared WPF/VM state chain; direct tracing preserves coherent edit; no extra agent',scope='original complete product limited repair; original opening/history/budget retained',product_complete=False))
 env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(out/'samples');Path(env['FORMAL_UI_EVIDENCE']).mkdir();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data')
 env['TEMP']=env['TMP']=str(out/'temp');Path(env['TEMP']).mkdir()
 args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
 build,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
 result=dict(phase=phase,build=build)
 if build==0:
  filt='FullyQualifiedName~FormalFlowContextTests' if phase in ['red','negative'] else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=context.trx','--ResultsDirectory:'+str(out)]
  result['test'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
  ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};rows=ET.parse(out/'context.trx').findall('.//t:UnitTestResult',ns)
  for kind in ['Passed','Failed','NotExecuted']:result[kind]=[r.get('testName') for r in rows if r.get('outcome')==kind]
 result['source_drift']=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
 write('result.json',result);print(json.dumps(dict(phase=phase,build=build,test=result.get('test'),passed=len(result.get('Passed',[])),failed=result.get('Failed'),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
