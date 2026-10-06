from pathlib import Path
import hashlib,json,os,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'drag-effects';phase=sys.argv[1]
sys.path.insert(0,str(ROOT/'tools/mistletoe'));import storage_limits as s;import process_runner as p
out=BASE/phase;assert not out.exists()
source=ROOT/'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs'
with s.Session(ROOT,'final-ui-01a11220-drag-effects-'+phase) as budget:
 budget.track(BASE);out.mkdir(parents=True)
 data=source.read_bytes();s.write(out/'original-view.cs',data)
 if phase=='red':
  s.write(out/'admission.json',json.dumps(dict(function='真实时间轴节点移动/跨道/撤销',gap='实际明确鼠标拖动后23:50未变化；源Move、目标DragOver恒Copy',entry='ScheduleListView.NodeDrag→DragDrop.DoDragDrop→TimelineDragOver/TimelineDrop',consequence='节点无法用鼠标移动到时刻或车道',minimum='真实WPF拖动效果协商红反例；节点Move/资源Copy与AllowedEffects相交，未知拒绝；集中正式UI回归后同产物复验',next='双窗撤销/车道/互导/等待停止/重启完整验收',user_data_moved=False),ensure_ascii=False).encode())
 carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant';budget.track(carrier)
 for f in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/f)
 hashes={f.relative_to(ROOT).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
 s.write(out/'source-hashes.json',json.dumps(hashes).encode());env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['FORMAL_UI_EVIDENCE']=str(out)
 args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
 code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
 if code==0:
  filt='FullyQualifiedName~FormalDragEffects' if phase=='red' else 'FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~Formal|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(out)]
  code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
 drift=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
 s.write(out/'result.json',json.dumps(dict(exit_code=code,source_drift=drift)).encode());print('Drag effects',phase,code,'drift',drift,flush=True)
