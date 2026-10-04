from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET,collections
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'; prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'; full=prior/'historical-routing-full-r1'
def j(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def save(n,v):(base/n).write_text(json.dumps(v,ensure_ascii=False,indent=2),encoding='utf-8')
def rows(p):
 xml=ET.fromstring(p.read_bytes()); return {r.attrib['testId']:dict(name=r.attrib['testName'],outcome=r.attrib['outcome'],error=''.join(r.find('{*}Output/{*}ErrorInfo/{*}Message').itertext())if r.attrib['outcome']=='Failed'else'') for r in xml.findall('.//{*}UnitTestResult')}
final=rows(full/'results.trx'); old=rows(prior/'node-source-final-r3/results.trx'); counts=dict(collections.Counter(r['outcome']for r in final.values()))
comparison=dict(baseline=str((prior/'node-source-final-r3/results.trx').relative_to(root)),current=str((full/'results.trx').relative_to(root)),counts=counts,added={k:final[k]for k in final.keys()-old.keys()},removed={k:old[k]for k in old.keys()-final.keys()},changed={k:dict(before=old[k],after=final[k])for k in old.keys()&final.keys()if old[k]!=final[k]})
save('routing-testid-comparison.json',comparison)
inputs=j(full/'source-before.json'); assert inputs==j(full/'source-after.json'); actual={r['path']:r['sha256']for r in inputs}
pfp=[]
for kind in ['relation','immutable','manufacture']:
 records=j(base/('routing-pfp-'+kind+'-r3.json')); assert [r['exit']for r in records]==[0,1,0]
 for r in records:
  leg=root/r['directory']; t=rows(leg/'results.trx'); before=j(leg/'source-before.json'); after=j(leg/'source-after.json'); assert before==after
  if r['leg']!='negative':assert {v['path']:v['sha256']for v in before}==actual
  else:
   changed=[v['path']for v in before if actual[v['path']]!=v['sha256']]; assert changed==[r['source']],changed
   errors=[v['error']for v in t.values()if v['outcome']=='Failed']; target='missing historical node relation'if kind=='relation'else'No exception was thrown'; assert errors and all(target in e for e in errors),errors
  pfp.append(dict(**r,counts=dict(collections.Counter(v['outcome']for v in t.values())),failures={k:v for k,v in t.items()if v['outcome']=='Failed'},declared_inputs_equal_current=r['leg']!='negative'))
 assert j(base/('routing-restore-'+kind+'-r3.json'))['equal']
save('routing-pfp-final.json',pfp)
before=j(base/'routing-source-before.json')+[j(base/'routing-test-before.json'),j(base/'routing-store-test-before.json')]; source=[]
for r in before:
 p=root/r['path']; b=p.read_bytes(); new=dict(path=r['path'],sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),crlf=b'\r\n'in b); assert new['bytes']>=r['bytes']*.95; assert new['bom']==r['bom']and new['crlf']==r['crlf']; source.append(dict(before=r,after=new))
save('routing-source-final.json',source)
processes={n:j(full/(n+'-process.json'))for n in ['assistant-build','build','probe-build','test']}
save('routing-final-observation.json',dict(level='ordinary original TRX/input/process/product evidence; not authenticated dependency receipt or independent acceptance',counts=counts,build_exits={n:v['exit_code']for n,v in processes.items()},inputs=len(inputs),inputs_unchanged=True,source_file_count=len(source),failed={k:v for k,v in final.items()if v['outcome']=='Failed'},claim_surface=[dict(id=k,**v)for k,v in final.items()if'ClaimSurface'in v['name']],pfp_groups=3,independent_requests_this_chat=0,remaining_implementation_requests=1,production_authorization=False,remaining='Legacy all-anchors unknown recovery, original stop eligibility conflict, full causal/TTL/port matrix, complete capture-before-build dependency provenance, unified independent review and actual product delivery remain open.'))
print(json.dumps(dict(counts=counts,added=len(comparison['added']),removed=len(comparison['removed']),changed=len(comparison['changed']),inputs=len(inputs),pfp=3),ensure_ascii=False))
