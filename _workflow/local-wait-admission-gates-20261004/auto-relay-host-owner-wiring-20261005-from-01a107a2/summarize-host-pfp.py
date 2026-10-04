import json,hashlib
from pathlib import Path
b=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-host-owner-wiring-20261005-from-01a107a2')
files=[p for p in b.glob('host-pfp-*-observation.json') if '-r2-' in p.name or 'new-mapping-guard-r3' in p.name]
assert len(files)==2
expected={r['path']:r['sha256'] for r in json.loads((b/'host-full-final-r2/source-after.json').read_text(encoding='utf-8'))}
summary=[]
for p in files:
    for r in json.loads(p.read_text(encoding='utf-8'))['records']:
        if r['leg']=='source-restoration':
            assert r['restored_sha256']==expected[r['source']]
            continue
        if r['leg']!='negative': assert r['source_sha256']==expected[r['source']]
        snapshot=json.loads((Path(r['directory'])/'source-before.json').read_text(encoding='utf-8'))
        assert all(z['sha256']==expected[z['path']] for z in snapshot if not(r['leg']=='negative' and z['path']==r['source']))
        summary.append(dict(mutation=r['mutation'],leg=r['leg'],exit=r['exit'],counts={k:sum(z['outcome']==k for z in r['results']) for k in ['Passed','Failed']}))
for relative,sha in expected.items(): assert hashlib.sha256(Path(relative).read_bytes()).hexdigest()==sha,relative
assert len(summary)==21
(b/'host-pfp-final-observation.json').write_text(json.dumps(dict(kind='ordinary causal P/F/P, not authenticated mutation receipts',original_observations=[str(p) for p in files],accepted_legs=summary,invalid_r1_startup_preserved='host-pfp-r1-invalid-startup.json',declared_inputs_after_last_pfp_equal=True),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary))
