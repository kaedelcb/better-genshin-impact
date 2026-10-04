from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639';p=d.parent/'auto-relay-four-entry-recovery-20261004-from-01a1061f'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};observations=[]
for f in sorted(p.rglob('*')):
 if not f.is_file() or f.suffix.lower() not in ('.json','.trx','.log','.md'):continue
 b=f.read_bytes();o=dict(path=str(f.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if f.suffix=='.trx':
  rows=ET.fromstring(b).findall('.//t:UnitTestResult',ns)
  o['counts']={c:sum(x.get('outcome')==c for x in rows) for c in ['Passed','Failed','NotExecuted']}
  o['failures']=[dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for x in rows if x.get('outcome')=='Failed']
 elif f.suffix=='.json':
  try:o['json_type']=type(json.loads(b.decode('utf-8-sig'))).__name__
  except (UnicodeError,ValueError) as e:o['original_parse_error']=str(e)
 else:
  text=b.decode('utf-8',errors='replace');o['tail']=text.splitlines()[-6:];o['error_lines']=[line for line in text.splitlines() if 'error ' in line or '失败!' in line or '已通过!' in line][-8:]
 observations.append(o)
sources=json.loads((p/'final/candidate-observation.json').read_text(encoding='utf-8'))['sources']
source_match=[dict(path=x['path'],expected=x['sha256'],current=hashlib.sha256((r/x['path']).read_bytes()).hexdigest()) for x in sources]
(d/'inherited-evidence-read.json').write_text(json.dumps(dict(kind='actual bytes/TRX parsing; not authenticated execution',records=observations,source_comparison=source_match),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(files_read=len(observations),trx_read=sum('counts' in x for x in observations),changed_sources=[x['path'] for x in source_match if x['current']!=x['expected']]),ensure_ascii=False))
