from pathlib import Path
import sys,json,hashlib,os,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleTests.cs']
phase=sys.argv[1]
if phase=='begin-observer':
 paths += ['MultiplayerHoeingAssistant/Services/BgiLogTailService.cs','MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowModels.cs','MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowPath.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlan.Path.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs','MultiplayerHoeingAssistant/ViewModels/MainViewModel.BgiExternal.cs','MultiplayerHoeingAssistant/ViewModels/WorkflowPathEditVm.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs']
if (base/phase).exists():raise RuntimeError('Existing evidence refused')
with storage.Session(root,'path-runtime-01a10f14-'+phase) as budget:
 budget.track(base)
 evidence=base/phase;evidence.mkdir()
 if phase.startswith('begin'):
  facts=[]
  for rel in paths:
   data=(root/rel).read_bytes();storage.write(evidence/rel,data)
   facts.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
  storage.write(evidence/'sources.json',json.dumps(facts,indent=2).encode())
  status=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain'],capture_output=True,cwd=root)
  storage.write(evidence/'status.txt',status.stdout)
  record=dict(marker='PATH-RUNTIME-FINISH-20261006-FROM-01a10ed6',goal='active',model='gpt-6.1-sol',effort='medium',head='2e6aa2b13f8f517e3c9bfd5ad6c405dd2cfed8b9',policy='mistletoe-release-first-20261005-v2',storage='mistletoe-storage-limits-20261005-v1',function='节点时刻真实仲裁与跨午夜重复恢复',gap='等待与提交按根触发器排序，节点日期未沿真实路径推进',entries=paths[:4],consequence='固定节点排序错误，跨午夜节点错过或回边错误日期',minimum='同一持久身份和来源匹配、跨午夜/回边/恢复反例及受影响Rebuild回归',next='伴随观察器及正式UI与统一完整产物',reviews='原账原级与2/2余额0保持，无新增请求，综合结果未取得',subagents='not used: timing persistence and host admission share revision identity; unique writer',matrix=[dict(dimension='state',scenario='fixed node overrides root ranking',expected='persistent timing shared by wait and send'),dict(dimension='concurrency',scenario='stale request or cursor changed',expected='host rejects self-reported rank'),dict(dimension='fault',scenario='resume after midnight or back edge',expected='saved date stable, fresh arrival selects next daily occurrence')])
  storage.write(evidence/'admission.json',json.dumps(record,ensure_ascii=False,indent=2).encode());print('begin saved')
 else:
  carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
  for p in [carrier,root/'MultiplayerHoeingAssistant/obj',root/'Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(p)
  subjects=[p for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for p in (root/folder).rglob('*') if p.suffix in ['.cs','.xaml','.csproj'] and not any(x in p.parts for x in ['bin','obj'])]
  hashes={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in subjects}
  storage.write(evidence/'source-hashes.json',json.dumps(hashes,indent=2).encode())
  args=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
  code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='build',timeout=1200)
  if code==0:
   filt=sys.argv[2] if len(sys.argv)>2 else 'FullyQualifiedName~FormalPath|FullyQualifiedName~FormalSchedule|FullyQualifiedName~WorkflowPlannerTests|FullyQualifiedName~WorkflowRunnerTests|FullyQualifiedName~TaskCenterSuccessorPathGateTests|FullyQualifiedName~TaskCenterHostLocalWaitDecisionTests|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests'
   args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=runtime.trx','--ResultsDirectory:'+str(evidence)]
   env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(evidence)
   code,_,_=process_runner.run(args,cwd=root,env=env,directory=evidence,recovery_directory=evidence,phase='test',timeout=600)
  drift=[p for p,h in hashes.items() if hashlib.sha256((root/p).read_bytes()).hexdigest()!=h]
  storage.write(evidence/'result.json',json.dumps(dict(exit_code=code,argv=args,source_drift=drift)).encode());print('exit_code',code,'source_drift',drift,flush=True)
