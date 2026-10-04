from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
names=['node-final-observation.json','node-source-final.json','node-testid-comparison.json','node-commit-observation.json','node-pfp-r3-observation.json','original-stop-evidence-readback.json']
rows=[]
for name in names:
 p=prior/name; b=p.read_bytes(); j=json.loads(b.decode('utf-8-sig')); rows.append(dict(path=str(p.relative_to(root)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),parsed=True))
legs=[prior/'node-source-final-r3']+[prior/(m+'-'+l) for m in ['node-original-mapping-r3','legacy-all-anchors-r3'] for l in ['baseline','negative','restored']]
for leg in legs:
 for p in sorted(leg.glob('*')):
  if not p.is_file(): continue
  b=p.read_bytes(); row=dict(path=str(p.relative_to(root)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
  if p.suffix=='.json': json.loads(b.decode('utf-8-sig')); row['parsed']=True
  elif p.suffix=='.trx':
   xml=ET.fromstring(b); results=xml.findall('.//{*}UnitTestResult'); counts={}
   for r in results: counts[r.attrib['outcome']]=counts.get(r.attrib['outcome'],0)+1
   row.update(counts=counts,failed=[dict(id=r.attrib['testId'],name=r.attrib['testName'],error=''.join(r.find('{*}Output/{*}ErrorInfo/{*}Message').itertext())) for r in results if r.attrib['outcome']=='Failed'])
  else: b.decode('utf-8-sig'); row['read_complete_text']=True
  rows.append(row)
(base/'inherited-evidence-readback.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(files=len(rows),trx=[dict(path=r['path'],counts=r['counts']) for r in rows if 'counts'in r]),ensure_ascii=False))
