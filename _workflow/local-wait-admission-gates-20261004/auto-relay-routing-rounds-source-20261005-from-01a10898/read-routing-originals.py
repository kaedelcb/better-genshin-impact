from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
execbase=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
rows=[]
paths=[p for p in prior.iterdir() if p.is_file() and p.suffix in {'.md','.json','.txt','.py'}]
for directory in execbase.iterdir():
 if directory.is_dir() and directory.name.startswith('historical-routing-'):
  paths.extend(p for p in directory.iterdir() if p.is_file() and p.suffix in {'.json','.log','.trx'})
for p in sorted(set(paths)):
 b=p.read_bytes(); row=dict(path=str(p.relative_to(root)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if p.suffix=='.json': json.loads(b.decode('utf-8-sig')); row['parsed']=True
 elif p.suffix=='.trx':
  xml=ET.fromstring(b); results=xml.findall('.//{*}UnitTestResult'); counts={}
  for r in results: counts[r.attrib['outcome']]=counts.get(r.attrib['outcome'],0)+1
  row.update(counts=counts,failed=[dict(id=r.attrib['testId'],name=r.attrib['testName'],error=''.join(r.find('{*}Output/{*}ErrorInfo/{*}Message').itertext())) for r in results if r.attrib['outcome']=='Failed'])
 else: b.decode('utf-8-sig'); row['read_complete_text']=True
 rows.append(row)
(base/'inherited-routing-originals-readback.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(files=len(rows),trx=[dict(path=r['path'],counts=r['counts']) for r in rows if 'counts' in r]),ensure_ascii=False))
