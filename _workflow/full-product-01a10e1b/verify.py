from pathlib import Path
import sys,os,json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as s
import process_runner
base=Path(__file__).resolve().parent;phase=sys.argv[1]
carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
with s.Session(root,'full-product-regression-'+phase) as budget:
    budget.track(base);evidence=base/phase;evidence.mkdir(exist_ok=False)
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--logger:trx;LogFileName=result.trx','--ResultsDirectory:'+str(evidence)]
    if phase!='full':
        args.append('--TestCaseFilter:FullyQualifiedName~WorkflowRunnerTests|FullyQualifiedName~WorkflowReconcilerTests|FullyQualifiedName~TaskCenterHostTests|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~LocalWaitFinalizationContractTests')
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=evidence,recovery_directory=evidence,phase='test',timeout=900)
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};tree=ET.parse(evidence/'result.trx')
    result=dict(exit_code=code,module_sha=hashlib.sha256((carrier/'MultiplayerHoeingAssistant.dll').read_bytes()).hexdigest(),counters=tree.find('.//t:Counters',ns).attrib,failed=[n.attrib['testName'] for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome']=='Failed'])
    s.write(evidence/'summary.json',json.dumps(result,ensure_ascii=False,indent=2).encode());print(json.dumps(result,ensure_ascii=False),flush=True)
