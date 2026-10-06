from pathlib import Path
import hashlib, json, os, sys
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'ui-polish'
phase=sys.argv[1]; out=BASE/phase; assert not out.exists()
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
with s.Session(ROOT,'own-root-01a112a0-ui-polish-'+phase) as budget:
 budget.track(BASE); out.mkdir(parents=True)
 def write(name,value):s.write(out/name,json.dumps(value,ensure_ascii=False,indent=2).encode())
 for rel in ['MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml']:
  s.write(out/('original-'+Path(rel).name),(ROOT/rel).read_bytes())
 write('admission.json',dict(function='正式WPF启动、刻度、车道新增及候选辨识',gap='实际启动4个Visual上级异常；午夜卡遮挡车道按钮；分钟标签重叠；候选同名/分支内部类型名/运行状态低对比',entry='ScheduleListView.UpdateViewport/Draw/ChoiceLabel/XAML',minimum='定向真实WPF红反例→集中视图修复→正式UI/路径/独立根回归→同一产物实际重启',scope='既有整版功能修复，原opening与预算不重建',source_goal='01a112a0-504d-7f71-b5ba-96f62711bab0',prior_runtime='own-runtime/second/result.json',user_data_moved=False))
 carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'; budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
 hashes={f.relative_to(ROOT).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
 write('source-hashes.json',hashes)
 env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['FORMAL_UI_EVIDENCE']=str(out)
 args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
 code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
 if code==0:
  filt='FullyQualifiedName~FormalViewportAndChoice' if phase=='red' else 'FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~Formal|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(out)]
  code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
 drift=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
 write('result.json',dict(exit_code=code,source_drift=drift));print('UI polish',phase,code,'drift',drift,flush=True)
