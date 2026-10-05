from pathlib import Path
import sys,os,json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s;import process_runner
base=Path(__file__).resolve().parent;ev=base/'startup-negative';target=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant';new=root/'_workflow/runtime-unified-01a10cef/candidate-r3/Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll';old=base/'single-green-r2/modules/assistant/MultiplayerHoeingAssistant.dll';dotnet='C:/Program Files/dotnet/dotnet.exe'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'same-empty-root-startup-negative-baseline-and-candidate') as budget:
    budget.track(ev);budget.track(target);ev.mkdir(exist_ok=False)
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};results=[]
    try:
        for name,source in [('baseline',old),('candidate',new)]:
            s.write(target/new.name,source.read_bytes(),mode='wb')
            args=[dotnet,'vstest',str(target/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~R410ProductionWiringTests.C5_StartupChain_HandoffDelegate_BindingAndNegativePaths','--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
            code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name,timeout=120)
            tree=ET.parse(ev/(name+'.trx'));result=dict(name=name,argv=args,exit_code=code,module_sha=sha(source),counters=tree.find('.//t:Counters',ns).attrib,failed=[dict(name=n.attrib['testName'],message=''.join(n.itertext())[:3500]) for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome']=='Failed'])
            s.write(ev/(name+'-summary.json'),json.dumps(result,ensure_ascii=False,indent=2).encode());results.append(result);print(name,result['counters'],flush=True)
    finally:
        s.write(target/new.name,new.read_bytes(),mode='wb');assert sha(target/new.name)==sha(new)
    s.write(ev/'summary.json',json.dumps(results,ensure_ascii=False,indent=2).encode())
    budget.check(measure=True)
