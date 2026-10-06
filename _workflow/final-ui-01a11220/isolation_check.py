from pathlib import Path
import hashlib, json, os, subprocess, sys
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner
BASE = Path(__file__).resolve().parent / 'isolated-data'
phase = sys.argv[1]
paths = [
 'MultiplayerHoeingAssistant/Services/AssistConfigManager.cs',
 'MultiplayerHoeingAssistant/Services/ConfigTransferService.cs',
 'MultiplayerHoeingAssistant/Services/DodocoSettingsService.cs',
 'MultiplayerHoeingAssistant/Services/KeywordWatchService.cs',
 'MultiplayerHoeingAssistant/Services/MemberConfigCacheManager.cs',
 'MultiplayerHoeingAssistant/Services/PetSettingsService.cs',
 'MultiplayerHoeingAssistant/Services/OnlineIntentLifecycle.cs',
 'MultiplayerHoeingAssistant/Services/StartupFlowStore.cs',
 'MultiplayerHoeingAssistant/Services/StartupFlowSchemeStore.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/ResourceCatalogService.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs',
 'MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs',
]
out = BASE/phase
assert not out.exists(), 'Existing evidence refused'
with s.Session(ROOT,'final-ui-01a11220-isolation-'+phase) as budget:
 budget.track(BASE);out.mkdir(parents=True)
 if phase == 'begin':
  records=[]
  for rel in paths:
   data=(ROOT/rel).read_bytes();s.write(out/rel,data)
   records.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest()))
  s.write(out/'sources.json',json.dumps(records,indent=2).encode())
  status=subprocess.run(['git','status','--porcelain=v1'],cwd=ROOT,capture_output=True)
  s.write(out/'status.txt',status.stdout)
  s.write(out/'admission.json',json.dumps(dict(function='同一完整版本真实验收及数据保留',gap='默认 AppData 受 MSIX 联合重定向；无独立验收数据根，目录移动无法安全隔离',entry='AssistConfigManager 与 DefaultFlowsDir/DefaultRunsDir/DefaultCacheFile 等实际启动存储入口',consequence='UI 编辑或运行可能写真实用户目录',minimum='显式 NEXUSBGI_DATA_ROOT 统一存储根；无指定保持旧默认；非法相对路径拒绝；独立根禁旧配置自动迁入；实际服务持久化及全入口静态核验、正式UI回归和真实产物文件句柄证明',next='同一product实际拖放/撤销/互导/等待停止/重启验收',requests=0,original_budget_unchanged=True,user_boundary='不从 Codex 内移动真实用户数据目录'),ensure_ascii=False,indent=2).encode())
  print('Isolation correction admitted; 13 original sources saved',flush=True)
 else:
  carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
  for p in [carrier,ROOT/'MultiplayerHoeingAssistant/obj',ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/obj']:
   budget.track(p)
  subjects=[p for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for p in (ROOT/folder).rglob('*') if p.suffix in ['.cs','.xaml','.csproj'] and not any(x in p.parts for x in ['bin','obj'])]
  hashes={p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in subjects}
  s.write(out/'source-hashes.json',json.dumps(hashes,indent=2).encode())
  env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');budget.track(out/'own-data')
  args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
  code,_,_=process_runner.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
  if code==0:
   filt='FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~Formal|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
   args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(out)]
   env['FORMAL_UI_EVIDENCE']=str(out)
   code,_,_=process_runner.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
  if phase=='green2' and code==0:
   import xml.etree.ElementTree as ET
   helper=ROOT/'MultiplayerHoeingAssistant/Services/AssistantDataDirectory.cs'
   original=helper.read_bytes();budget.track(helper)
   mutant=original.replace(b'return Path.GetFullPath(explicitRoot);',b'return Path.GetFullPath(Path.Combine(explicitRoot, "unexpected"));')
   assert mutant!=original
   def execute(folder,filter_text):
    folder.mkdir();s.write(folder/'source.json',json.dumps(dict(helper_sha256=hashlib.sha256(helper.read_bytes()).hexdigest())).encode())
    build=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
    c,_,_=process_runner.run(build,cwd=ROOT,env=env,directory=folder,recovery_directory=folder,phase='build',timeout=1200)
    assert c==0
    run=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filter_text,'--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(folder)]
    env['FORMAL_UI_EVIDENCE']=str(folder)
    c,_,_=process_runner.run(run,cwd=ROOT,env=env,directory=folder,recovery_directory=folder,phase='test',timeout=600)
    return c
   try:
    s.write(out/'negative-original.cs',original);s.write(out/'negative-patch.cs',mutant)
    helper.write_bytes(mutant)
    negative=execute(out/'negative','FullyQualifiedName~IsolatedDataRootTests.ProductionStores_ShareExplicitRoot')
    failed=ET.parse(out/'negative/interaction.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    assert negative!=0 and len(failed)==1 and failed[0].get('outcome')=='Failed'
    print('Explicit-root persistence oracle detects wrong implementation',flush=True)
   finally:
    helper.write_bytes(original);assert helper.read_bytes()==original
   restored=execute(out/'restored',filt)
   assert restored==0
   s.write(out/'causal-result.json',json.dumps(dict(baseline_exit=code,negative_exit=negative,restored_exit=restored,source_restored=True)).encode())
   code=restored
  drift=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
  s.write(out/'result.json',json.dumps(dict(exit_code=code,source_drift=drift,argv=args),indent=2).encode())
  print('exit_code',code,'source_drift',drift,flush=True)
