from pathlib import Path
import sys,json,hashlib
root=Path.cwd();base=Path(__file__).resolve().parent;sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
rollout=Path('E:/CodexData/home/sessions/2026/10/05/rollout-2026-10-05T23-23-52-01a10caa-0961-7342-bd54-6d58b0de2eb0.jsonl')
raw=rollout.read_bytes();events=[json.loads(line) for line in raw.decode('utf-8').splitlines() if line]
finals=[e for e in events if e.get('type')=='response_item' and e.get('payload',{}).get('type')=='message' and e['payload'].get('role')=='assistant' and (e['payload'].get('channel')=='final' or e['payload'].get('phase')=='final_answer')]
assert len(finals)==1,'not one completed final'
content=''.join(c.get('text','') for c in finals[0]['payload']['content']);report,end=json.JSONDecoder().raw_decode(content)
report_text=content[:end]
assert not content[end:].strip() or content[end:].strip().startswith('<oai-mem-citation>'),'unexpected material after JSON'
assert report['request_id']=='bf73360e2886405bad322e9dbe41983d' and report['verdict']=='blocked'
contexts=[e['payload'] for e in events if e.get('type')=='turn_context'];context=contexts[-1]
assert context['model']=='gpt-6.1-sol' and context['effort']=='high'
complete=[e for e in events if e.get('type')=='event_msg' and e.get('payload',{}).get('type') in ['task_complete','turn_complete']]
assert complete,'no native completion event'
prior=json.loads(Path('E:/CodexReviewSnapshots/terminal-candidate-20261004/bf73360e2886405bad322e9dbe41983d/prior.json').read_text(encoding='utf-8'))
bykey={x['key']:x for x in report['prior_dispositions']};assert set(bykey)==set(prior['findings'])
for key,row in prior['findings'].items():
    original=row['original'];actual=bykey[key]
    assert all(actual[k]==original[k] for k in ['id','severity','obligation'])
with storage.Session(root,'final-review-original-source-capture') as budget:
    for name,data in [('comprehensive-review-2-native-rollout.jsonl',raw),('comprehensive-review-2-original-final.txt',content.encode('utf-8')),('comprehensive-review-2-report.json',report_text.encode('utf-8'))]:storage.write(base/name,data)
    observation=dict(kind='actual independent final/complete capture, no native receipt',request_id=report['request_id'],verdict=report['verdict'],model=context['model'],effort=context['effort'],rollout_sha256=hashlib.sha256(raw).hexdigest(),report_sha256=hashlib.sha256(content.encode()).hexdigest(),prior_keys=len(bykey),prior_identity_and_level_unchanged=True,completion_events=complete,extra_implementation_used=2,extra_implementation_remaining=0,findings=report['findings'],product_accepted=False)
    storage.write(base/'comprehensive-review-2-capture-observation.json',json.dumps(observation,ensure_ascii=False,indent=2).encode('utf-8'))
    print(json.dumps({k:observation[k] for k in ['request_id','verdict','model','effort','prior_keys','extra_implementation_remaining']}),flush=True)
    budget.check(measure=True)
