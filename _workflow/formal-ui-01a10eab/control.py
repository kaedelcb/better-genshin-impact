from pathlib import Path
import sys, os, json, hashlib, subprocess
root=Path.cwd(); sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
paths=['MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs']
phase=sys.argv[1]
with storage.Session(root,'formal-ui-01a10eab-'+phase) as budget:
    budget.track(base)
    if phase=='begin':
        facts=[]
        for rel in paths:
            data=(root/rel).read_bytes(); storage.write(base/'before'/rel,data)
            facts.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
        status=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain'],cwd=root,capture_output=True)
        storage.write(base/'opening-status.txt',status.stdout)
        storage.write(base/'before.json',json.dumps(facts,indent=2).encode())
        storage.write(base/'admission.json',json.dumps(dict(marker='UI-RESEARCH-FUNCTION-FIRST-20261006-FROM-01a10e1b',policy='mistletoe-release-first-20261005-v2',storage='mistletoe-storage-limits-20261005-v1',function='正式调度列表和真实节点排程',gap='旧四卡布局无时间轴、资源拖入、共享弹窗；节点无独立时间',entry='MistletoePage / WorkflowEditVm / WorkflowRunner',consequence='不能按预研编排和运行',minimum='Rebuild、编辑保存读回、时间等待停止和布局验证',next='正式调度列表与执行接线',model='gpt-6.1-sol',effort='medium',goal='active',remaining_review=0),ensure_ascii=False,indent=2).encode())
        print('begin saved')
    else:
        carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
        budget.track(carrier); budget.track(root/'MultiplayerHoeingAssistant/obj'); budget.track(root/'Test/MultiplayerHoeingAssistant.UnitTest/obj')
        evidence=base/phase; evidence.mkdir(exist_ok=False)
        subjects=sorted((root/'MultiplayerHoeingAssistant').rglob('*.cs'))+sorted((root/'MultiplayerHoeingAssistant/Views').glob('*.xaml'))
        subjects=[p for p in subjects if 'obj' not in p.relative_to(root).parts and 'bin' not in p.relative_to(root).parts]
        source_hashes={p.relative_to(root).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in subjects}
        storage.write(evidence/'source-hashes.json',json.dumps(source_hashes,indent=2).encode())
        args=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='build',timeout=1200)
        if code==0:
            args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~FormalSchedule|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests','--logger:trx;LogFileName=formal-ui.trx','--ResultsDirectory:'+str(evidence)]
            env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(evidence)
            code,_,_=process_runner.run(args,cwd=root,env=env,directory=evidence,recovery_directory=evidence,phase='test',timeout=600)
        drift=[rel for rel,h in source_hashes.items() if hashlib.sha256((root/rel).read_bytes()).hexdigest()!=h]
        storage.write(evidence/'result.json',json.dumps(dict(exit_code=code,argv=args,source_drift=drift)).encode()); print('exit_code',code,'source_drift',drift,flush=True)
