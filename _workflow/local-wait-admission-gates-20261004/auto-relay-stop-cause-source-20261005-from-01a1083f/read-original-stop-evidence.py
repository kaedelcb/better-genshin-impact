import json,hashlib,xml.etree.ElementTree as ET
from pathlib import Path
root=Path.cwd();base=Path(__file__).parent;prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-stop-integrity-20261005-from-01a1081b';rows=[]
def read(p):
    b=p.read_bytes();rows.append(dict(path=p.relative_to(root).as_posix(),sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),level='inherited original; no current semantic closure'))
    return b
for name in ['relay-prompt.txt','HANDOFF.md','STOP-INTEGRITY-REPAIR.md','STOP-INTEGRITY-CANDIDATE.md','stop-final-observation.json','stop-source-final.json','stop-testid-comparison.json','stop-commit-observation.json','inherited-original-evidence-readback.json']:
    b=read(prior/name)
    if name.endswith('.json'):json.loads(b.decode('utf-8-sig'))
folders=[prior/'stop-integrity-final-r3',prior/'unknown-owner-contract-candidate-r1']+sorted(prior.glob('host-pfp-*-r3-baseline'))+sorted(prior.glob('host-pfp-*-r3-negative'))+sorted(prior.glob('host-pfp-*-r3-restored'))
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results=[]
for folder in folders:
    for p in sorted(folder.glob('*.json')):json.loads(read(p).decode('utf-8-sig'))
    for p in sorted(folder.glob('*.log')):read(p).decode('utf-8-sig')
    trx=folder/'results.trx';tree=ET.fromstring(read(trx))
    results.append(dict(folder=folder.relative_to(root).as_posix(),results=[dict(id=r.get('testId'),name=r.get('testName'),outcome=r.get('outcome'),error=r.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for r in tree.findall('.//t:UnitTestResult',ns)]))
for p in sorted(prior.glob('host-pfp-*-r3-*-r3-observation.json')):json.loads(read(p).decode('utf-8-sig'))
report=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-terminal-candidate-20261004-from-01a10716/comprehensive-review-1-original-final.txt'
data=json.loads(read(report).decode('utf-8-sig'))
(base/'original-stop-evidence-readback.json').write_text(json.dumps(dict(rows=rows,trx_results=results,report=dict(verdict=data['verdict'],finding_ids=[r['id'] for r in data['findings']],unknowns=data.get('unknowns',[])),limitation='Raw original parsed completely; current changed source needs fresh regression. No receipt or semantic closure inferred.'),ensure_ascii=False,indent=2),encoding='utf-8')
print('Original files read:',len(rows),'TRX legs:',len(results),'findings:',len(data['findings']),'unknowns:',len(data.get('unknowns',[])))
