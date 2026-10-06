from pathlib import Path
import sys,json,os,hashlib,base64,subprocess,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent;out=base/'causal';carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
sha=lambda data:hashlib.sha256(data).hexdigest()
oracle='FullyQualifiedName~RunnerWaitRequest_UsesBoundNodeTimeInsteadOfRootTrigger|FullyQualifiedName~Leaf_ArmsBeforeSend_FreezesAndClosesBeforeRouting'
full='FullyQualifiedName~FormalObservation|FullyQualifiedName~FormalPath|FullyQualifiedName~FormalSchedule|FullyQualifiedName~WorkflowPlannerTests|FullyQualifiedName~WorkflowRunnerTests|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~TaskCenterSuccessorPathGateTests'
if out.exists():raise RuntimeError('Existing causal evidence refused')
with storage.Session(root,'path-runtime-01a10f14-critical-causal') as budget:
 for path in [base,carrier,root/'MultiplayerHoeingAssistant/obj',root/'Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(path)
 out.mkdir();original=source.read_bytes();storage.write(out/'original-runner.bin',original)
 original_tier=b'Tier = TaskCenterMechanismPolicy.TierOfTrigger((node is null ? run.TriggerTiming : WorkflowNodeSchedule.Effective(run, node, occurrence))?.Kind),'
 assert original.count(original_tier)==1 and original.count(b'await using(session)')==1
 mutant=original.replace(original_tier,b'Tier = TaskCenterMechanismPolicy.TierOfTrigger(run.TriggerTiming?.Kind),').replace(b'await using(session)',b'if (true)')
 storage.write(out/'mutant-runner.bin',mutant)
 subjects=[p for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for p in (root/folder).rglob('*') if p.suffix in ['.cs','.xaml','.csproj'] and not any(x in p.parts for x in ['bin','obj'])]
 hashes={p.relative_to(root).as_posix():sha(p.read_bytes()) for p in subjects};storage.write(out/'source-hashes.json',json.dumps(hashes,indent=2).encode())
 def put(variant):
  def quote(p):return "'"+str(p).replace("'","''")+"'"
  ps='[System.IO.File]::WriteAllBytes('+quote(source)+',[System.IO.File]::ReadAllBytes('+quote(variant)+'))'
  encoded=base64.b64encode(ps.encode('utf-16-le')).decode()
  result=subprocess.run(['pwsh','-NoProfile','-NonInteractive','-EncodedCommand',encoded],cwd=root,capture_output=True)
  if result.returncode:raise RuntimeError('Scoped source write failed: '+result.stderr.decode(errors='replace'))
 def execute(name,filter,build=True):
  evidence=out/name;evidence.mkdir()
  if build:
   args=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
   code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='build',timeout=1200)
   storage.write(evidence/'build.json',json.dumps(dict(exit_code=code)).encode())
   if code:raise RuntimeError(name+' build failed')
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filter,'--logger:trx;LogFileName=causal.trx','--ResultsDirectory:'+str(evidence)]
  env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(evidence)
  code,_,_=process_runner.run(args,cwd=root,env=env,directory=evidence,recovery_directory=evidence,phase='test',timeout=900)
  rows=[]
  if (evidence/'causal.trx').exists():
   tree=ET.parse(evidence/'causal.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
   rows=[dict(name=e.attrib.get('testName'),outcome=e.attrib.get('outcome'),message=e.findtext('t:Output/t:ErrorInfo/t:Message',default='',namespaces=ns)) for e in tree.findall('.//t:UnitTestResult',ns)]
  storage.write(evidence/'result.json',json.dumps(dict(exit_code=code,tests=rows),ensure_ascii=False,indent=2).encode());print(name,'exit',code,'tests',len(rows),flush=True)
  return code,rows
 baseline=execute('baseline',oracle);assert baseline[0]==0 and len(baseline[1])==3 and all(r['outcome']=='Passed' for r in baseline[1])
 try:
  put(out/'mutant-runner.bin');assert source.read_bytes()==mutant
  negative=execute('negative',oracle);assert negative[0]!=0 and len(negative[1])==3 and all(r['outcome']=='Failed' for r in negative[1])
 finally:
  put(out/'original-runner.bin');assert source.read_bytes()==original
  storage.write(out/'restored-source.json',json.dumps(dict(original_sha=sha(original),mutant_sha=sha(mutant),restored_sha=sha(source.read_bytes()),byte_restored=True)).encode())
 restored=execute('restored',oracle);assert restored[0]==0 and len(restored[1])==3 and all(r['outcome']=='Passed' for r in restored[1])
 final=execute('affected-final',full,False)
 drift=[rel for rel,h in hashes.items() if sha((root/rel).read_bytes())!=h]
 storage.write(out/'result.json',json.dumps(dict(baseline_exit=baseline[0],negative_exit=negative[0],restored_exit=restored[0],affected_exit=final[0],source_drift=drift,assistant_sha=sha((carrier/'MultiplayerHoeingAssistant.dll').read_bytes()),known_failure_preserved='ProductionCtor_DoesNotEnableSuccessorPathGate is already inconsistent with opening source; no assertion modified',goal_complete=False),ensure_ascii=False,indent=2).encode());print('causal final source drift',drift,flush=True)
