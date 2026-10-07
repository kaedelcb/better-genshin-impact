"""Capture one actual native final without changing independent judgments."""
from pathlib import Path
import json, sys

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/arrival-revision-01a11808/fixed-review-1'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
import native_review as n
import storage_limits as s

load = lambda p: json.loads(p.read_bytes())
observation = load(BASE / 'snapshot-observation.json'); packet = Path(observation['snapshot'])
request = load(packet / 'request.json'); intent = load(BASE / 'dispatch-intent.json')
rollout = Path(sys.argv[1]); raw = n.regular(rollout); events = [json.loads(line) for line in raw.splitlines()]
meta = next(e['payload'] for e in events if e['type'] == 'session_meta')
spawn = meta['source']['subagent']['thread_spawn']
assert spawn['parent_thread_id'] == request['parent_thread'] and spawn['agent_path'] == request['agent_path'] and spawn['depth'] == 1
matches = []
for index, event in enumerate(events):
    value = event['payload']
    if event['type'] != 'response_item' or value.get('type') != 'message' or value.get('role') != 'assistant' or value.get('phase') not in ['final_answer', 'final']: continue
    text = '\n'.join(c['text'] for c in value['content'] if c.get('type') in ['output_text', 'text'])
    try: report = n.json_report(text)
    except (ValueError, TypeError): continue
    if report.get('request_id') != request['request_id']: continue
    context_event = [e for e in events[:index] if e['type'] == 'turn_context'][-1]
    ctx = context_event['payload']; settings = ctx['collaboration_mode']['settings']
    assert ctx['model'] == settings['model'] == 'gpt-6.1-sol' and ctx['effort'] == settings['reasoning_effort'] == 'high'
    assert context_event['timestamp'] >= intent['created_utc'].replace('+00:00', 'Z')
    complete = [e for e in events[index + 1:] if e['type'] == 'event_msg' and e['payload'].get('type') == 'task_complete' and e['payload'].get('turn_id') == ctx['turn_id']]
    assert len(complete) == 1 and request['request_id'] in complete[0]['payload'].get('last_agent_message', '')
    matches.append((text, report, event, complete[0], ctx))
assert len(matches) == 1, 'native final not uniquely completed; do not synthesize or redispatch'
text, report, final, completed, ctx = matches[0]
assert report['snapshot_hash'] == request['snapshot_hash'] and report['stage'] == 'implementation'
assert report['verdict'] in ['pass', 'blocked']
assert n.sha(n.regular(packet / 'snapshot.json')) == request['snapshot_hash']
snapshot = load(packet / 'snapshot.json'); drift = []
for folder, rows in [('source', snapshot['files']), ('contracts', snapshot['contract_files'])]:
    for rel, row in rows.items():
        file = packet / folder / rel
        if not file.is_file() or n.sha(n.regular(file)) != row['sha256']: drift.append(folder + '/' + rel)
assert not drift, drift
prior = load(packet / 'prior.json'); assert (len(prior['findings']), len(prior['unknowns'])) == (101, 39)
dispositions = report.get('prior_dispositions', []); unknown_dispositions = report.get('unknown_dispositions', [])
finding_keys = [r.get('key') for r in dispositions]; unknown_keys = [r.get('key') for r in unknown_dispositions]
errors = []
if len(finding_keys) != len(set(finding_keys)) or set(finding_keys) != set(prior['findings']): errors.append('prior finding key coverage mismatch')
if len(unknown_keys) != len(set(unknown_keys)) or set(unknown_keys) != set(prior['unknowns']): errors.append('prior unknown key coverage mismatch')
rank = {'suggestion': 0, 'important': 1, 'must': 2}
for row in dispositions:
    if row.get('key') not in prior['findings']: continue
    original = prior['findings'][row['key']]['original']
    if rank.get(row.get('severity'), -1) < rank.get(original.get('severity'), 0): errors.append('prior severity missing/downgraded: ' + str(row.get('key')))
    if row.get('obligation') != original.get('obligation'): errors.append('prior obligation mismatch: ' + str(row.get('key')))
    if 'original' in row and row['original'] != original: errors.append('prior original changed: ' + str(row.get('key')))
for row in unknown_dispositions:
    if row.get('key') in prior['unknowns'] and 'original' in row and row['original'] != prior['unknowns'][row['key']]['original']: errors.append('unknown original changed: ' + str(row.get('key')))
out = BASE / 'review'
with s.Session(ROOT, 'fixed-arrival-one-native-final-capture-01a11897') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); out.mkdir(exist_ok=False)
    s.write(out / 'native-rollout.jsonl', raw)
    s.write(out / 'original-final.txt', text.encode('utf-8'))
    s.write(out / 'report.json', n.encode(report))
    actual = dict(request_id=request['request_id'], agent_path=spawn['agent_path'], child_id=meta['id'], parent_thread_id=spawn['parent_thread_id'], source_rollout=str(rollout), model=ctx['model'], effort=ctx['effort'], settings_model=ctx['collaboration_mode']['settings']['model'], settings_effort=ctx['collaboration_mode']['settings']['reasoning_effort'], final_timestamp=final['timestamp'], completed_timestamp=completed['timestamp'], native_rollout_sha256=n.sha(raw), original_final_sha256=n.sha(text.encode('utf-8')))
    s.write(BASE / 'dispatch-actual.json', n.encode(actual))
    record = dict(**actual, verdict=report['verdict'], source_candidate_verdict=report.get('source_candidate_verdict'), snapshot_sha256=request['snapshot_hash'], frozen_file_hashes_checked=len(snapshot['files']), frozen_contract_hashes_checked=len(snapshot['contract_files']), frozen_drift=[], original_finding_keys=101, original_unknown_keys=39, all_original_keys_disposed=not errors, schema_observations=errors, report_judgments_parent_modified=False, used=1, remaining=0, old_fixed_grant_used=2, old_fixed_grant_remaining=0, legacy_receipt_or_permit_claimed=False, complete_product_delivered=False)
    s.write(out / 'capture-observation.json', n.encode(record))
    print(json.dumps(dict(verdict=report['verdict'], source_candidate_verdict=report.get('source_candidate_verdict'), findings=len(report.get('findings', [])), unknowns=len(report.get('unknowns', [])), schema_observations=errors, child_id=meta['id'], used=1, remaining=0), ensure_ascii=False), flush=True)
