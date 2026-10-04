from pathlib import Path
import hashlib, json
root=Path.cwd(); out=Path(__file__).parent
requests=root/'_workflow/r56-reference-activation-wiring-2026-09-29/review-process/requests'
files=list(requests.glob('*/intent.json'))
files+=list((root/'_workflow/local-wait-admission-gates-20261004/native-review/requests').glob('*/dispatch-failure-*.json'))
files+=[root/'_workflow/r56-reference-activation-wiring-2026-09-29/review-process/authorizations/owner-grant.json',
    root/'_workflow/local-wait-admission-gates-20261004/owner-policy.json']
rows=[]
for path in files:
    raw=path.read_bytes(); value=json.loads(raw.decode('utf-8-sig'))
    rows.append(dict(path=str(path.relative_to(root)),sha256=hashlib.sha256(raw).hexdigest(),
        request_id=value.get('request_id'),stage=value.get('stage'),state=value.get('state'),
        count=value.get('count'),cap_disabled=value.get('request_cap_stop_disabled_for_this_goal')))
observation=dict(kind='additional original read-only budget sources; not allowance or exhaustive cross-batch totals',
    sources=rows,cli_dispatch_intents=len(list(requests.glob('*/intent.json'))),new_requests=0,
    remaining_budget_certified=False,original_history_preserved=True)
(out/'additional-budget-source-observation.json').write_text(json.dumps(observation,indent=2),encoding='utf-8')
print(json.dumps(dict(cli_dispatch_intents=observation['cli_dispatch_intents'],source_count=len(rows),new_requests=0)))
