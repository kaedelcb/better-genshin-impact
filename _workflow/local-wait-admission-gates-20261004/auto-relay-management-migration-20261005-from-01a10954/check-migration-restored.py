from pathlib import Path
import sys,json,hashlib,os,xml.etree.ElementTree as ET
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as storage;import process_runner
mutation=json.loads((base/'migration-consumer-mutation.json').read_text(encoding='utf-8'))
for item in mutation['sources']:assert hashlib.sha256((root/item['path']).read_bytes()).hexdigest()==item['original_sha256']
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
failed=ET.parse(base/'migration-consumer-negative/assistant.trx').findall('.//t:UnitTestResult[@outcome="Failed"]',ns)
assert len(failed)==2
assert any('ActiveLabelWithoutValidTransactionCannotBeLoadedAsExecutable' in x.attrib['testName'] and 'No exception was thrown' in ''.join(x.itertext()) for x in failed)
assert any('ReplacementPortRejectsWrongInputVersionAndConfirmsExactBytes' in x.attrib['testName'] and 'Assert.False() Failure' in ''.join(x.itertext()) for x in failed)
products=json.loads((base/'migration-consumer-r3/assistant-products.json').read_text(encoding='utf-8'))
for name in ['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.UnitTest.dll']:
    row=next(p for p in products if Path(p['path']).name==name);assert hashlib.sha256(Path(row['path']).read_bytes()).hexdigest()==row['sha256']
out=base/'migration-consumer-restored'
with storage.Session(root,'migration-consumer-restored-check') as budget:
    budget.track(out);budget.check(location=out);out.mkdir(exist_ok=False)
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(root/'_workflow/runtime-01a1097f/migration-consumer-r3/assistant/MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~WorkflowMigrationConsumerTests|FullyQualifiedName~R56ReferenceActivationWiringTests.Added','--logger:trx;LogFileName=assistant.trx','--ResultsDirectory:'+str(out)]
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=out,recovery_directory=base,phase='restored-test',timeout=1800)
    mutation.update(restored_exit=code,restored_products='migration-consumer-r3 immutable compiled products; same guarded source bytes',negative_test_names=[x.attrib['testName'] for x in failed])
    storage.write(out/'source-products-test-observation.json',json.dumps(mutation,ensure_ascii=False,indent=2).encode('utf-8'))
    budget.check(measure=True)
print('restored',code);sys.exit(code)
