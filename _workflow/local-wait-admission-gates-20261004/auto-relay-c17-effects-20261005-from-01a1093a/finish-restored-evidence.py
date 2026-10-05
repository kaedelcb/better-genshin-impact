from pathlib import Path
import json,subprocess,time,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a';ev=base/'restored-original-bgi-r1';ev.mkdir(exist_ok=False)
source=json.loads((base/'restored-linked-r1/source-before.json').read_text())
def check_source():
 for r in source:assert hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'],r['path']
check_source()
old_products=json.loads((base/'green-final-r1/product-observation.json').read_text())
for name in ['BetterGI.dll','BetterGenshinImpact.UnitTest.dll']:
 r=next(r for r in old_products if Path(r['path']).name==name);assert hashlib.sha256(Path(r['path']).read_bytes()).hexdigest()==r['sha256'],name
argv=json.loads((base/'green-final-r1/BetterGenshinImpact-test-process.json').read_text())['argv'];argv[-1]='--ResultsDirectory:'+str(ev)
row=dict(argv=argv,started=time.time())
with (ev/'test.log').open('w',encoding='utf-8') as log:
 p=subprocess.Popen(argv,cwd=root,stdout=log,stderr=subprocess.STDOUT);row['pid']=p.pid;row['exit_code']=p.wait();row['ended']=time.time()
(ev/'test-process.json').write_text(json.dumps(row,indent=2),encoding='utf-8');print('BGI restored complete-output test',row['exit_code'],flush=True);assert row['exit_code']==0
check_source()
links=json.loads((base/'inventory-immutable-links.json').read_text(encoding='utf-8-sig'));drifts=[r['link'] for r in links if hashlib.sha256(Path(r['link']).read_bytes()).hexdigest().upper()!=r['sha256'] or hashlib.sha256(Path(r['source']).read_bytes()).hexdigest().upper()!=r['sha256']];assert not drifts
(base/'restored-linked-r1/input-comparison.json').write_text(json.dumps(dict(source_equal=True,link_hashes_equal=True,link_count=len(links),bgi_complete_output_binary_equal=True,bgi_complete_output_result=str(ev/'BetterGenshinImpact.trx'),linked_bgi_incomplete_view_failure_preserved=True),indent=2),encoding='utf-8')
(base/'source-after.json').write_text(json.dumps(source,indent=2),encoding='utf-8')
