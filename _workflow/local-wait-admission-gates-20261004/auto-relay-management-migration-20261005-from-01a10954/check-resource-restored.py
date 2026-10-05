from pathlib import Path
import json,hashlib,sys,os,xml.etree.ElementTree as ET
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as storage;import process_runner
record=json.loads((base/'resource-mutation.json').read_text(encoding='utf-8'))
assert hashlib.sha256(Path(record['source']).read_bytes()).hexdigest()==record['original_sha256']
products=json.loads((base/'resource-bgi-r4/bgi-products.json').read_text(encoding='utf-8'))
for name in ['BetterGI.dll','BetterGenshinImpact.UnitTest.dll']:
    item=next(p for p in products if Path(p['path']).name==name)
    assert hashlib.sha256(Path(item['path']).read_bytes()).hexdigest()==item['sha256']
negative=ET.parse(base/'resource-revision-negative/bgi.trx')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
failed=negative.findall('.//t:UnitTestResult[@outcome="Failed"]',ns)
assert len(failed)==1 and 'InvalidReferenceDoesNotNavigateOrWrite' in failed[0].attrib['testName'] and 'stale' in failed[0].attrib['testName']
assert 'Assert.False() Failure' in ''.join(failed[0].itertext())
out=base/'resource-restored-r1'
with storage.Session(root,'delivery-resource-restored-immutable-check') as budget:
    budget.track(out);budget.check(location=out);out.mkdir(exist_ok=False)
    dll=root/'_workflow/runtime-01a1097f/resource-bgi-r4/bgi/BetterGenshinImpact.UnitTest.dll'
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(dll),'--TestCaseFilter:FullyQualifiedName~ExternalResourceEditorTests','--logger:trx;LogFileName=bgi.trx','--ResultsDirectory:'+str(out)]
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=out,recovery_directory=base,phase='restored-test',timeout=1800)
    record.update(restored_exit=code,restored_trx=str(out/'bgi.trx'),negative_failed_test=failed[0].attrib['testName'],restoration='same source bytes and immutable r4 compiled products; no relevant change requiring rebuild')
    storage.write(out/'source-products-test-observation.json',json.dumps(record,ensure_ascii=False,indent=2).encode('utf-8'))
    budget.check(measure=True)
print('restored-test',code);sys.exit(code)
