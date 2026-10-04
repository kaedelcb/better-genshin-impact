from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET,sys
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'; execbase=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
version=sys.argv[1] if len(sys.argv)>1 else 'r1'
finaldir=execbase/('routing-round-full-'+version)
def read(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def tests(p):
 t=ET.parse(p); result={}
 for r in t.findall('.//{*}UnitTestResult'):
  m=r.find('{*}Output/{*}ErrorInfo/{*}Message'); result[r.attrib['testId']]=dict(name=r.attrib['testName'],outcome=r.attrib['outcome'],error=''.join(m.itertext()) if m is not None else '')
 return result
def save(n,o): (base/n.replace('round-','round-'+version+'-',1)).write_text(json.dumps(o,ensure_ascii=False,indent=2),encoding='utf-8')
current=tests(finaldir/'results.trx'); old=tests(execbase/'historical-routing-full-r1/results.trx')
counts={}
for r in current.values(): counts[r['outcome']]=counts.get(r['outcome'],0)+1
comparison=dict(added={k:current[k] for k in current.keys()-old.keys()},removed={k:old[k] for k in old.keys()-current.keys()},changed={k:dict(before=old[k],after=current[k]) for k in current.keys()&old.keys() if (current[k]['name'],current[k]['outcome'])!=(old[k]['name'],old[k]['outcome'])})
save('round-testid-comparison.json',comparison)
final=read(finaldir/'source-after.json'); save('round-source-final.json',final)
groups=[]
for kind,pfpversion in ([('prepare','r2'),('samekey','r1'),('history','r1')] if version=='r1' else [('prepare','r3'),('samekey','r3'),('history','r3')]):
 records=read(base/('round-pfp-'+kind+'-'+pfpversion+'.json'))
 for row in records:
  d=root/row['directory']; ts=tests(d/'results.trx'); cs={}
  for t in ts.values(): cs[t['outcome']]=cs.get(t['outcome'],0)+1
  row.update(counts=cs,failed={k:r for k,r in ts.items() if r['outcome']=='Failed'},source_unchanged=read(d/'source-before.json')==read(d/'source-after.json'),green_inputs_equal_final=read(d/'source-after.json')==final if row['leg']!='negative' else None)
  if (d/'external-products.json').exists():
   raw=Path(read(d/'external-products.json')['absolute_output'])
   row['external_output']=str(raw)
   row['raw_evidence_equal']=all(p.read_bytes()==(raw/p.name).read_bytes() for p in d.iterdir() if p.is_file())
   row['retained_products_match']=all(hashlib.sha256((raw/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in read(d/'product-observation.json'))
   assert row['raw_evidence_equal'] and row['retained_products_match']
  groups.append(row)
 restore=read(base/('round-restore-'+kind+'-'+pfpversion+'.json')); assert restore['equal'] and restore['atomic_same_directory']
save('round-pfp-final.json',groups)
sources=read(base/'round-source-before.json')+[read(base/'round-test-before.json'),read(base/'round-host-test-before.json')]
rows=[]
for row in sources:
 p=root/row['path']; b=p.read_bytes(); after=dict(path=row['path'],bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)
 rows.append(dict(before=row,after=after)); assert len(b)>=row['bytes']*.95
save('round-source-size-format.json',rows)
obs=dict(level='ordinary input/process/product/TRX/PFP; not authenticated full dependency closure/independent pass or product acceptance',counts=counts,inputs=len(final),inputs_equal=read(finaldir/'source-before.json')==final,failed={k:r for k,r in current.items() if r['outcome']=='Failed'},claim_surface={k:r for k,r in current.items() if 'ClaimSurfaceGuardTests'in r['name']},build_exits={n:read(finaldir/(n+'-process.json'))['exit_code'] for n in ['assistant-build','build','probe-build','test']},new_tests=len(comparison['added']),removed_tests=len(comparison['removed']),changed_name_outcome=len(comparison['changed']),pfp_groups=3,independent_requests_this_chat=0,remaining_implementation_requests=1,production_authorization=False)
save('round-final-observation.json',obs)
print(json.dumps({k:v for k,v in obs.items() if k not in {'failed','claim_surface'}},ensure_ascii=False))
