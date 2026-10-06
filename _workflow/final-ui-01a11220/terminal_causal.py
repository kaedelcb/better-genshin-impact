from pathlib import Path
import hashlib,json,os,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'terminal-path'/'causal'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
full='FullyQualifiedName~FormalPathTests|FullyQualifiedName~FastLocalTerminalAdmissionTests|FullyQualifiedName~TerminalReleaseSealTests|FullyQualifiedName~LocalWaitFinalizationContractTests|FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~LegacyHistoricalSealIntegrityTests'
focused='FullyQualifiedName~ConditionEnd_TerminalSeal|FullyQualifiedName~FastLocalTerminalAdmissionTests|FullyQualifiedName~LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping'
known='LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping'
carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
files=['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs']
original={n:(ROOT/n).read_bytes() for n in files}
def sha(b):return hashlib.sha256(b).hexdigest()
with s.Session(ROOT,'own-root-01a11308-terminal-path-causal') as budget:
 budget.track(BASE);BASE.mkdir(parents=True,exist_ok=False);budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
 def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
 for n,b in original.items():s.write(BASE/('original-'+Path(n).name),b)
 write(BASE/'scope.json',dict(finding='OWN-ROOT-FAST-END-IMPORTANT-1',review='0 new requests; original important remains open',baseline_failure_preserved=known,mutations=['remove local branch sealing path','restore cancelled-only terminal retry'],recovery='two exact owned source byte arrays; no checkout/reset; finally rebuild restored source',prior='terminal-path/red/terminal.trx; green first fixture failure preserved'))
 outcomes=[]
 def execute(phase,filt):
  out=BASE/phase;out.mkdir();env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['FORMAL_TERMINAL_EVIDENCE_DIR']=str(out/'samples')
  hashes={f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
  write(out/'source-hashes.json',hashes)
  args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
  build,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
  result=dict(phase=phase,build=build)
  if build==0:
   args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=terminal.trx','--ResultsDirectory:'+str(out)]
   result['test'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
   ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};tree=ET.parse(out/'terminal.trx');rows=tree.findall('.//t:UnitTestResult',ns)
   result['passed']=[r.get('testName') for r in rows if r.get('outcome')=='Passed'];result['failed']=[r.get('testName') for r in rows if r.get('outcome')=='Failed'];result['other']=[r.get('testName') for r in rows if r.get('outcome') not in ['Passed','Failed']]
  result['source_drift']=[n for n,h in hashes.items() if sha((ROOT/n).read_bytes())!=h]
  write(out/'result.json',result);outcomes.append(result);print(phase,build,len(result.get('passed',[])),len(result.get('failed',[])),flush=True)
  return result
 def good(r):return r['build']==0 and not r['source_drift'] and not r['other'] and len(r['failed'])==2 and all(known in n for n in r['failed'])
 baseline=execute('baseline',full)
 assert good(baseline), 'baseline contains new failure; no mutation performed'
 try:
  mutated={}
  for n,b in original.items():
   old,new=(b'LocalBranchSettled(r, o) || ',b'') if n.endswith('RunStoreTerminalRelease.cs') else (b'&& run.IsTerminal)',b'&& run.State == WorkflowRunState.Cancelled)')
   assert b.count(old)==1;mutated[n]=b.replace(old,new);assert (ROOT/n).read_bytes()==b
  write(BASE/'mutation-identity.json',{n:dict(original_sha=sha(original[n]),mutant_sha=sha(b),original_path=str(BASE/('original-'+Path(n).name))) for n,b in mutated.items()})
  for n,b in mutated.items():(ROOT/n).write_bytes(b)
  negative=execute('negative',focused)
 finally:
  for n,b in original.items():(ROOT/n).write_bytes(b)
  write(BASE/'source-restored.json',{n:dict(sha256=sha((ROOT/n).read_bytes()),same=(ROOT/n).read_bytes()==b) for n,b in original.items()})
  restored=execute('restored',full)
 write(BASE/'result.json',dict(phases=[dict(phase=r['phase'],build=r['build'],passed=len(r.get('passed',[])),failed=r.get('failed',[]),other=r.get('other',[]),source_drift=r['source_drift']) for r in outcomes],source_restored=all((ROOT/n).read_bytes()==b for n,b in original.items()),production_acceptance=False,independent_review=False))
 assert good(restored)
 assert negative['build']==0 and not negative['source_drift'] and not negative['other']
 for token in ['ConditionEnd_TerminalSeal','ProductionRunner_LocalBranchCompletesOriginalAdmission','ColdHost_ExplicitTerminalRetry']:
  assert any(token in n for n in negative['failed']),token
 assert any('ColdHost_ExplicitTerminalRetry' in n and 'False' in n for n in negative['failed']), 'direct end retry counterexample missing'
 print('causal restored; original two LocalWait failures retained; product acceptance pending',flush=True)
