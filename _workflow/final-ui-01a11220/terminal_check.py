from pathlib import Path
import hashlib, json, os, sys
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'terminal-path'
phase=sys.argv[1]; out=BASE/phase
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
with s.Session(ROOT,'own-root-01a11308-terminal-path-'+phase) as budget:
 budget.track(BASE); out.mkdir(parents=True,exist_ok=False)
 def write(name,value): s.write(out/name,json.dumps(value,ensure_ascii=False,indent=2).encode())
 sources=['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalPathTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FastLocalTerminalAdmissionTests.cs']
 for rel in sources: s.write(out/('original-'+Path(rel).name),(ROOT/rel).read_bytes())
 write('admission.json',dict(marker='OWN-ROOT-TERMINAL-20261007-FROM-01a112a0',finding='OWN-ROOT-FAST-END-IMPORTANT-1',level='important',function='条件路径运行完成后仲裁终局及重复入口',gap='RunSettled拒branchYes/branchNo，原样本Succeeded而Accepted且无seal',entry='WorkflowRunner条件CommitOutcome→RunStore.TrySealTerminalRun→Host终局回写',scope='既有完整产品限定修复；不重开opening/额度',matrix=['true/false实际本机Runner到结束→封印','分支携带外部身份或同出现提交→拒绝','Unknown词→拒绝','封印冷重载及事实变更→拒绝'],agents='同一共享终局状态链直接追查；预算0，不派额外只读Agent',review='无新增会诊；原级责任待必要综合复核',policies=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1']))
 carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'; budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']: budget.track(ROOT/folder)
 hashes={f.relative_to(ROOT).as_posix():hashlib.sha256(f.read_bytes()).hexdigest() for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
 write('source-hashes.json',hashes)
 env=os.environ.copy(); env['NEXUSBGI_DATA_ROOT']=str(out/'own-data'); env['FORMAL_TERMINAL_EVIDENCE_DIR']=str(out/'samples')
 args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
 code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
 if code==0:
  filt='FullyQualifiedName~ConditionEnd_TerminalSeal' if phase=='red' else 'FullyQualifiedName~FormalPathTests|FullyQualifiedName~FastLocalTerminalAdmissionTests|FullyQualifiedName~TerminalReleaseSealTests|FullyQualifiedName~LocalWaitFinalizationContractTests|FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~LegacyHistoricalSealIntegrityTests'
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=terminal.trx','--ResultsDirectory:'+str(out)]
  code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
 drift=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
 write('result.json',dict(exit_code=code,source_drift=drift)); print(phase,code,drift,flush=True)
