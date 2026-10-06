from pathlib import Path
import sys,json,hashlib,os,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as s
import process_runner
base=Path(__file__).resolve().parent
phase=sys.argv[1];out=base/phase
if out.exists():raise RuntimeError('Existing evidence refused')
paths=['MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/ViewModels/WorkflowUndoVm.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalPathWindowTests.cs']
with s.Session(root,'runtime-accept-01a10f9b-'+phase) as budget:
 budget.track(base);out.mkdir()
 if phase.startswith('begin'):
  facts=[]
  for rel in paths:
   data=(root/rel).read_bytes();s.write(out/rel,data)
   facts.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
  s.write(out/'sources.json',json.dumps(facts,indent=2).encode())
  status=subprocess.run(['git','status','--porcelain'],capture_output=True,cwd=root)
  s.write(out/'status.txt',status.stdout)
  live=Path('C:/Users/Administrator/AppData/Local/Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming/NexusBGI')
  for folder in ['workflows','runs']:
   if (live/folder).exists():
    for p in (live/folder).rglob('*.json'):s.write(out/'private'/p.relative_to(live),p.read_bytes())
  record=dict(marker='RUNTIME-ACCEPT-20261006-FROM-01a10f14',head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),policy='mistletoe-release-first-20261005-v2',storage='mistletoe-storage-limits-20261005-v1',function='正式时间轴选择、拖放及双窗共享撤销',gap='实际点击使草稿23:50变23:55；撤销恢复Lanes为空时同步绘制访问索引0抛错',consequence='普通点击可能误改时间；共享两窗撤销弹UI异常',minimum='拖放须源控件内按下且超过系统阈值；撤销恢复期间抑制绘制并完整恢复后通知双窗；真实WPF红反例、回归、反向突变、同产物实机复验',next='继续完整版本实际矩阵、数据恢复及版本交付',requests=0,original_budget_unchanged=True,subagents='not used: direct pointer and shared draft interactions require sequential real UI; no independent review quota remains',matrix=['click/jitter: no schedule mutation','intentional drag: real destination changes time and undo restores','shared windows undo: no empty-lane partial rendering','future wait/stop/restart: Cancelled persists, zero resource submissions'])
  if phase=='begin-polish':
   record.update(function='正式UI简化与窗口自适应',gap='常用与低频操作同层堆叠、固定510高度造成小窗口拥挤、添加引导黑字暗底',consequence='新手难以找到操作，编辑区可见高度不足，三步引导说明难读',minimum='保留全部原命令，将低频管理归入菜单，主区随真实窗口调整高度，引导复用暗金控件；同一次Rebuild及正式UI/路径/互导回归',next='用受保护双存储根完成最新统一产物的集中实际验收',matrix=['全部原操作仍有实际命令入口','窗口收缩/放大与独立弹窗共享同一草稿','新手三步引导可读，参数/时间/车道写入合同不变'])
  s.write(out/'admission.json',json.dumps(record,ensure_ascii=False,indent=2).encode());print('correction admitted and original source saved',flush=True)
 else:
  carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
  for p in [carrier,root/'MultiplayerHoeingAssistant/obj',root/'Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(p)
  subjects=[p for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for p in (root/folder).rglob('*') if p.suffix in ['.cs','.xaml','.csproj'] and not any(x in p.parts for x in ['bin','obj'])]
  hashes={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in subjects}
  s.write(out/'source-hashes.json',json.dumps(hashes,indent=2).encode())
  args=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
  code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=out,recovery_directory=out,phase='build',timeout=1200)
  if code==0:
   filt=sys.argv[2] if len(sys.argv)>2 else 'FullyQualifiedName~Formal|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
   args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(out)]
   env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(out)
   code,_,_=process_runner.run(args,cwd=root,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
  drift=[p for p,h in hashes.items() if hashlib.sha256((root/p).read_bytes()).hexdigest()!=h]
  s.write(out/'result.json',json.dumps(dict(exit_code=code,argv=args,source_drift=drift)).encode());print('exit_code',code,'source_drift',drift,flush=True)
