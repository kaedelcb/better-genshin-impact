from pathlib import Path
import hashlib,json,os,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'flow-context/causal'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
full='FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
sha=lambda b:hashlib.sha256(b).hexdigest()
files=['MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs']
original={n:(ROOT/n).read_bytes() for n in files}
with s.Session(ROOT,'own-root-01a11380-flow-context-causal') as budget:
 budget.track(BASE);BASE.mkdir(parents=True,exist_ok=False);budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
 def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
 green=BASE.parent/'green';baseline=json.loads((green/'result.json').read_text());assert baseline['build']==baseline['test']==0 and not baseline['source_drift'] and not baseline['Failed']
 hashes=json.loads((green/'source-hashes.json').read_text());assert all(sha((ROOT/n).read_bytes())==h for n,h in hashes.items())
 for name in ['build','test']:assert json.loads((green/(name+'-tree-terminal.json')).read_text())['active_processes']==0
 for n,b in original.items():s.write(BASE/('original-'+Path(n).name),b)
 write(BASE/'scope.json',dict(finding='OWN-ROOT-FLOW-SWITCH-IMPORTANT-2',baseline=str(green/'context.trx'),mutations=['allow discarding dirty/conflicted drafts','revert shared selection to edit-only identity','disable revision-based preview refresh'],recovery='exact two owned byte arrays; guarded finally plus restored Rebuild/regression',review_requests_new=0,important_open=True))
 outcomes=[]
 def execute(phase,filt):
  out=BASE/phase;out.mkdir();env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(out/'samples');Path(env['FORMAL_UI_EVIDENCE']).mkdir();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data')
  env['TEMP']=env['TMP']=str(out/'temp');Path(env['TEMP']).mkdir()
  hashes={f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
  write(out/'source-hashes.json',hashes)
  args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
  build,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200);result=dict(phase=phase,build=build)
  if build==0:
   args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=context.trx','--ResultsDirectory:'+str(out)]
   result['test'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
   rows=ET.parse(out/'context.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
   for kind in ['Passed','Failed','NotExecuted']:result[kind]=[r.get('testName') for r in rows if r.get('outcome')==kind]
  result['source_drift']=[n for n,h in hashes.items() if sha((ROOT/n).read_bytes())!=h]
  write(out/'result.json',result);outcomes.append(result);print(phase,build,len(result.get('Passed',[])),len(result.get('Failed',[])),flush=True);return result
 mutated={}
 for n,b in original.items():
  replacements=[(b'if (!Editing.HasUnsavedChanges && _host.LoadFlowSnapshot(Editing.Draft.WorkflowId!).Revision == Editing.BaseRevision)',b'if (true)'),(b'if (current.Revision != preview.Revision)',b'if (false)')] if n.endswith('ViewModel.cs') else [(b'f.WorkflowId == Host.SelectedWorkflowId',b'f.WorkflowId == Draft?.Draft.WorkflowId')]
  for a,z in replacements:assert b.count(a)==1;b=b.replace(a,z)
  mutated[n]=b
 write(BASE/'mutation-identity.json',{n:dict(original_sha=sha(original[n]),mutant_sha=sha(b)) for n,b in mutated.items()})
 try:
  for n,b in mutated.items():assert (ROOT/n).read_bytes()==original[n];(ROOT/n).write_bytes(b)
  negative=execute('negative','FullyQualifiedName~FormalFlowContextTests')
 finally:
  for n,b in original.items():
   assert (ROOT/n).read_bytes() in [b,mutated[n]],'unexpected external source write'
   (ROOT/n).write_bytes(b)
  write(BASE/'source-restored.json',{n:dict(sha256=sha((ROOT/n).read_bytes()),same=(ROOT/n).read_bytes()==b) for n,b in original.items()})
  restored=execute('restored',full)
 write(BASE/'result.json',dict(phases=[dict(phase=r['phase'],build=r['build'],passed=len(r.get('Passed',[])),failed=r.get('Failed',[]),other=r.get('NotExecuted',[]),source_drift=r['source_drift']) for r in outcomes],source_restored=all((ROOT/n).read_bytes()==b for n,b in original.items()),production_acceptance=False,independent_review=False))
 assert negative['build']==0 and len(negative['Failed'])==5 and not negative['Passed'] and not negative['source_drift']
 assert restored['build']==restored['test']==0 and not restored['Failed'] and not restored['NotExecuted'] and not restored['source_drift']
 print('causal complete; important review/runtime responsibility retained',flush=True)
 import flow_context_refresh
 flow_context_refresh.perform(budget)
