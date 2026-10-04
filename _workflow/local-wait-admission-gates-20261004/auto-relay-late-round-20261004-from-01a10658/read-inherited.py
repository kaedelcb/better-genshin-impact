from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-late-round-20261004-from-01a10658';p=d.parent/'auto-relay-original-multiround-20261004-from-01a10639'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};records=[]
for f in sorted(p.rglob('*')):
 if not f.is_file() or f.suffix.lower() not in ('.json','.trx','.log','.md'):continue
 b=f.read_bytes();o=dict(path=str(f.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if f.suffix=='.trx':
  rows=ET.fromstring(b).findall('.//t:UnitTestResult',ns);o['counts']={c:sum(x.get('outcome')==c for x in rows) for c in ['Passed','Failed','NotExecuted']};o['failures']=[dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in rows if x.get('outcome')=='Failed']
 elif f.suffix=='.json':
  try:o['json_type']=type(json.loads(b.decode('utf-8-sig'))).__name__
  except (UnicodeError,ValueError) as e:o['parse_error']=str(e)
 else:
  lines=b.decode('utf-8',errors='replace').splitlines();o['tail']=lines[-5:];o['errors']=[x for x in lines if 'error ' in x or '失败!' in x][-8:]
 records.append(o)
sources=json.loads((p/'final-r2/candidate-observation.json').read_text(encoding='utf-8'))['sources'];matches=[dict(path=x['path'],expected=x['sha256'],current=hashlib.sha256((r/x['path']).read_bytes()).hexdigest()) for x in sources]
(d/'inherited-evidence-read.json').write_text(json.dumps(dict(kind='ordinary actual byte/TRX read, not certification',records=records,source_comparison=matches),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(files=len(records),trx=sum('counts' in x for x in records),changed_sources=[x['path'] for x in matches if x['current']!=x['expected']]),ensure_ascii=False))
