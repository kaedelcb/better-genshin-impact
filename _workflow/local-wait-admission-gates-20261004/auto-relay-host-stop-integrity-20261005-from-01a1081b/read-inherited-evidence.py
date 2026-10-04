import json,hashlib,xml.etree.ElementTree as ET
from pathlib import Path
root=Path.cwd()
base=Path(__file__).parent
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-writer-qualified-20261005-from-01a107e1'
rows=[]
for name in ['qualified-final-observation.json','qualified-source-final.json','qualified-testid-comparison.json','qualified-commit-observation.json','actual-shutdown-proof-observation.json','inherited-seven-pfp-readback.json']:
    p=prior/name
    data=json.loads(p.read_text(encoding='utf-8-sig'))
    rows.append(dict(path=p.relative_to(root).as_posix(),sha256=hashlib.sha256(p.read_bytes()).hexdigest(),bytes=p.stat().st_size,kind=type(data).__name__,status='historical candidate read; not current certification'))
for p in sorted(prior.glob('host-pfp-*-observation.json')):
    data=json.loads(p.read_text(encoding='utf-8-sig'))
    for leg in data['records']:
        if leg['leg']=='source-restoration': continue
        folder=root/leg['directory']
        trx=folder/'results.trx'
        tree=ET.parse(trx)
        ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        actual=[dict(id=r.attrib['testId'],name=r.attrib['testName'],outcome=r.attrib['outcome'],message=r.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for r in tree.findall('.//t:UnitTestResult',ns)]
        rows.append(dict(mutation=leg['mutation'],leg=leg['leg'],trx=trx.relative_to(root).as_posix(),trx_sha256=hashlib.sha256(trx.read_bytes()).hexdigest(),exit=leg['exit'],actual=actual,status='original ordinary PFP; changed related source, not current receipt'))
report=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-terminal-candidate-20261004-from-01a10716/comprehensive-review-1-original-final.txt'
data=json.loads(report.read_text(encoding='utf-8-sig'))
rows.append(dict(report=report.relative_to(root).as_posix(),sha256=hashlib.sha256(report.read_bytes()).hexdigest(),verdict=data.get('verdict'),finding_ids=[r['id'] for r in data.get('findings',[])],unknown_count=len(data.get('unknowns',[])),prior_keys=[r.get('key') for r in data.get('prior_dispositions',[])],status='original unchanged; all original responsibility retained; no semantic closure inferred'))
(base/'inherited-original-evidence-readback.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
print('read original observations and nine original PFP legs; original report retained',len(rows))
