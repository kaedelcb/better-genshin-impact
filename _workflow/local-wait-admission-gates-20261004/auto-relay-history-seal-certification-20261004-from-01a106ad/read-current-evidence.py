from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
r=Path.cwd(); d=Path(__file__).parent; prev=d.parent/'auto-relay-history-outcome-20261004-from-01a10699'; ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def read(p):
    b=p.read_bytes(); return dict(path=str(p.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
evidence=[]
for relative in ['final-r3/candidate-observation.json','final-r3/impact-comparison.json','final-r3/per-execution-comparison.json','mutation-observations-r2.json','mutation-observations-m5.json','post-mutation-byte-observation-m5.json','candidate-commit-observation.json','original-review-source-read.json']:
    p=prev/relative
    if p.is_file(): evidence.append(read(p))
for p in sorted((prev/'final-r3').rglob('*')):
    if p.is_file() and p.suffix in ['.trx','.log','.json','.txt']: evidence.append(read(p))
source=json.loads((prev/'final-r3/candidate-observation.json').read_text(encoding='utf-8'))
current=[]
for s in source['sources']:
    v=read(r/s['path']);v['prior_sha256']=s['sha256'];v['same_bytes']=v['sha256']==s['sha256'];current.append(v)
g=d.parent/'g10-completion'; raw=json.loads((g/'independent-audit-original-event.json').read_text(encoding='utf-8')); event_text=''.join(c['text'] for c in raw['payload']['content'] if c['type']=='output_text')
report=(g/'independent-audit-original.md').read_text(encoding='utf-8'); actual_source=json.loads((g/'independent-audit-source.json').read_text(encoding='utf-8'))
rollout=Path(actual_source['rollout_path']); events=[json.loads(l) for l in rollout.read_text(encoding='utf-8').splitlines()]
original_matches=[x for x in events if x.get('type')=='response_item' and x.get('payload',{}).get('id')==raw['payload'].get('id')]
g10=dict(event_text_exact_report=event_text==report,trimmed_text_equal=event_text.strip()==report.strip(),event_chars=len(event_text),report_chars=len(report),original_event_match_count=len(original_matches),event_payload_equal=len(original_matches)==1 and original_matches[0]['payload']==raw['payload'],source_thread=actual_source['thread_id'],kind='existing blocked independent report; byte comparison is not a pass')
local=[]
for p in sorted((d.parent/'native-review/requests').rglob('dispatch-failure-*.json')):
    j=json.loads(p.read_text(encoding='utf-8')); local.append(dict(**read(p),request_id=j.get('request_id'),attempt=j.get('attempt'),outcome=j.get('outcome')))
obs=dict(kind='read-only current byte/source observations; not independent pass or certified execution',evidence=evidence,current_source_comparison=current,g10_original_source=g10,local_attempts=local,known_local_charged_dispatches=len(local)+1,full_G_control_R56_budget_reconciliation_complete=False,new_independent_requests=0)
(d/'current-evidence-readback.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(g10=g10,local_attempts=len(local),inherited_source_changes=[x['path'] for x in current if not x['same_bytes']],read_artifacts=len(evidence)),ensure_ascii=False))
