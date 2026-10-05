from pathlib import Path
import sys,json,hashlib,os
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
paths=['MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs']
with storage.Session(root,'full-product-ui-'+sys.argv[1]) as budget:
    budget.track(base)
    if sys.argv[1]=='begin':
        facts=[]
        for rel in paths:
            data=(root/rel).read_bytes();storage.write(base/'before'/rel,data)
            facts.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
        storage.write(base/'before.json',json.dumps(facts,indent=2).encode())
        plan=root/'_workflow/local-wait-admission-gates-20261004/plan.json'
        data=plan.read_bytes();storage.write(base/'plan-before.json',data)
        doc=json.loads(data.decode('utf-8-sig'))
        doc['full_product_ui_admission_01a10e1b']=dict(marker='FULL-PRODUCT-DELIVERY-RESTORE-20261006-FROM-01a10cef',policy='mistletoe-release-first-20261005-v2',function='C02 drag authoring; C20 LocalWait controls',gap='task editor has no drag; parking run cannot stop/resume via UI',entry='MistletoePage and WorkflowRunItemVm.Update',consequence='promised interaction missing; parked run inaccessible',minimum='identity-preserving reorder; parked UI action red-green and existing host action regressions',next='complete product source/compiled/UI audit',remaining_review=0,independent_closed=False)
        storage.write(plan,json.dumps(doc,ensure_ascii=False,indent=2).encode(),mode='wb')
    else:
        phase=sys.argv[1];carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
        budget.track(carrier);evidence=base/phase;evidence.mkdir(exist_ok=False)
        argv=['C:/Program Files/dotnet/dotnet.exe','build',str(root/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        code,_,_=process_runner.run(argv,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='build',timeout=1200)
        if code==0:
            argv=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests','--logger:trx;LogFileName=ui.trx','--ResultsDirectory:'+str(evidence)]
            code,_,_=process_runner.run(argv,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='test',timeout=600)
        storage.write(evidence/'result.json',json.dumps(dict(exit_code=code,argv=argv)).encode())
        print('exit_code',code,flush=True)
