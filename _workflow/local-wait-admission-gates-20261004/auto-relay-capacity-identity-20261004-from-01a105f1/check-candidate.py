from pathlib import Path
import json,hashlib,subprocess
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1';obs=json.loads((d/'final/candidate-observation.json').read_text());comparison=json.loads((d/'final/impact-comparison.json').read_text());sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
matches=[dict(path=x['path'],expected=x['sha256'],actual=sha(r/x['path'])) for x in obs['sources']];assert all(x['expected']==x['actual'] for x in matches)
mutations=[json.loads((d/m/'observation.json').read_text()) for m in ['M1-no-terminal-slot-migration','M2-archive-wire-identity']];assert all(m['specified_failure_verified'] and m['original_sha256']==m['restored_sha256'] and m['restored_exit']==0 for m in mutations)
assert not comparison['removed'] and not comparison['changed'] and comparison['same_failed_ids']
inherited=[]
for p in [r/'_workflow/local-wait-admission-gates-20261004/typed-parent/final/candidate-observation.json',r/'_workflow/local-wait-admission-gates-20261004/typed-parent/final/impact-comparison.json',r/'_workflow/local-wait-admission-gates-20261004/typed-parent/final/post-mutation-byte-observation.json',r/'_workflow/local-wait-admission-gates-20261004/typed-parent/candidate-commit-observation.json']:
 b=p.read_bytes();j=json.loads(b);inherited.append(dict(path=str(p.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),keys=list(j)))
for p in (r/'_workflow/local-wait-admission-gates-20261004/typed-parent').rglob('*.trx'):
 inherited.append(dict(path=str(p.relative_to(r)),bytes=p.stat().st_size,sha256=sha(p)))
for p in (r/'_workflow/local-wait-admission-gates-20261004/typed-parent').rglob('*.log'):
 b=p.read_bytes();inherited.append(dict(path=str(p.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),last_lines=b.decode('utf-8',errors='replace').splitlines()[-3:]))
(d/'inherited-evidence-read.json').write_text(json.dumps(inherited,ensure_ascii=False,indent=2))
summary=dict(kind='ordinary final-input SHA/PFP/TRX comparison observation; not certification or independent pass',source_matches=matches,mutations=[dict(id=m['id'],negative=m['negative'],specified_failure_verified=m['specified_failure_verified'],original_sha256=m['original_sha256'],restored_sha256=m['restored_sha256']) for m in mutations],added_count=len(comparison['added']),removed_count=len(comparison['removed']),changed_count=len(comparison['changed']),same_failed_ids=comparison['same_failed_ids'],source_set_unchanged=True,goal_complete=False,production_gate_open=False)
(d/'post-mutation-byte-observation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2));print(json.dumps({k:v for k,v in summary.items() if k not in ['source_matches','mutations']}),flush=True)
