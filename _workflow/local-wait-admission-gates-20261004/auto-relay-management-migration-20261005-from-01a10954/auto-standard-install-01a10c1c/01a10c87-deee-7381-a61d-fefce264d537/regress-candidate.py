from pathlib import Path
import sys,os,json,hashlib,shutil,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent;candidate=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
ev=base/'regression-r1';carrier=root/'_workflow/runtime-unified-01a10c87/test-carrier-r1'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
with storage.Session(root,'unified-candidate-full-regression') as budget:
    budget.track(ev);budget.track(carrier);budget.check(location=carrier)
    ev.mkdir(exist_ok=False);carrier.mkdir(exist_ok=False)
    mapping=[]
    # Old carrier is a test fixture/dependency source, never the tested product.
    old=root/'_workflow/runtime-01a10c1c/current-paired-restored-r1/bgi'
    assert old.is_dir()
    for p in old.rglob('*'):
        if not p.is_file():continue
        rel=p.relative_to(old)
        if rel.parts[0] in ['User','log','Tools']:continue
        target=carrier/rel;target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(p,target) if p.name.startswith(('BetterGI.','MultiplayerHoeingAssistant.')) else os.link(p,target)
    # Replace copied product bytes with this candidate, leaving immutable source untouched.
    for source in [candidate,candidate/'Tools/MultiplayerHoeingAssistant']:
        for p in source.iterdir():
            if not p.is_file():continue
            target=carrier/p.name
            data=p.read_bytes();storage.write(target,data,mode='wb')
            mapping.append(dict(path=p.name,source=str(p),sha256=sha(p)))
    # The assistant tests were built with the latest assistant source.
    assistantTests=root/'_workflow/runtime-01a10c1c/bound-root-final-r1/assistant'
    for p in assistantTests.iterdir():
        if p.is_file() and p.name.startswith('MultiplayerHoeingAssistant.UnitTest.'):
            storage.write(carrier/p.name,p.read_bytes(),mode='wb')
    storage.write(ev/'tested-candidate-binaries.json',json.dumps(mapping,indent=2).encode())
    summaries=[]
    for name,dll in [('assistant','MultiplayerHoeingAssistant.UnitTest.dll'),('bgi','BetterGenshinImpact.UnitTest.dll')]:
        args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/dll),'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)]
        code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=name+'-full',timeout=1800)
        storage.write(ev/(name+'-argv.json'),json.dumps(dict(argv=args,exit_code=code,evidence_level='ordinary regression, actual unified candidate modules, prior same-source test assembly'),indent=2).encode())
        trx=ET.parse(ev/(name+'.trx'));ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        counters=trx.find('.//t:Counters',ns).attrib
        failed=[dict(testId=n.attrib['testId'],name=n.attrib['testName'],outcome=n.attrib['outcome'],message=''.join(n.itertext())[:2500]) for n in trx.findall('.//t:UnitTestResult',ns) if n.attrib['outcome'] not in ['Passed','NotExecuted']]
        summaries.append(dict(name=name,exit_code=code,counters=counters,failures=failed))
        print(name,code,counters,flush=True)
    storage.write(ev/'summary.json',json.dumps(summaries,indent=2).encode())
    for item in mapping:assert sha(carrier/item['path'])==item['sha256'],'product drift during regression'
    budget.check(measure=True)
