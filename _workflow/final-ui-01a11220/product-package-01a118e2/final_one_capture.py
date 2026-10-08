"""Capture the unique completed independent final and retain every judgment verbatim."""
from pathlib import Path
import json,sys
R=Path(__file__).resolve().parents[3];B=Path(__file__).resolve().parent/'final-one-review'
sys.path.insert(0,str(R/'tools/mistletoe'))
import native_review as n
import storage_limits as s
load=lambda p:json.loads(p.read_bytes())
obs=load(B/'snapshot-observation.json');packet=Path(obs['snapshot']);q=load(packet/'request.json');intent=load(B/'dispatch-intent.json')
rollout=Path(sys.argv[1]);raw=n.regular(rollout);events=[json.loads(line) for line in raw.splitlines()]
meta=next(e['payload'] for e in events if e['type']=='session_meta');spawn=meta['source']['subagent']['thread_spawn']
assert spawn['parent_thread_id']==q['parent_thread'] and spawn['agent_path']==q['agent_path'] and spawn['depth']==1
matches=[]
for i,e in enumerate(events):
    p=e['payload']
    if e['type']!='response_item' or p.get('type')!='message' or p.get('role')!='assistant' or p.get('phase') not in ['final_answer','final']:continue
    text='\n'.join(c['text'] for c in p['content'] if c.get('type') in ['output_text','text'])
    try:report=n.json_report(text)
    except (ValueError,TypeError):continue
    if report.get('request_id')!=q['request_id']:continue
    context=[x for x in events[:i] if x['type']=='turn_context'][-1];ctx=context['payload'];settings=ctx['collaboration_mode']['settings']
    assert ctx['model']==settings['model']=='gpt-6.1-sol' and ctx['effort']==settings['reasoning_effort']=='high'
    assert context['timestamp']>=intent['created_utc'].replace('+00:00','Z')
    complete=[x for x in events[i+1:] if x['type']=='event_msg' and x['payload'].get('type')=='task_complete' and x['payload'].get('turn_id')==ctx['turn_id']]
    assert len(complete)==1 and q['request_id'] in complete[0]['payload'].get('last_agent_message','')
    matches.append((text,report,e,complete[0],ctx))
assert len(matches)==1,'final not uniquely completed; never synthesize or redispatch'
text,report,final,completed,ctx=matches[0]
assert report['snapshot_hash']==q['snapshot_hash'] and report['stage']=='implementation' and report['verdict'] in ['pass','blocked']
assert n.sha(n.regular(packet/'snapshot.json'))==q['snapshot_hash']
snapshot=load(packet/'snapshot.json');drift=[]
for folder,rows in [('source',snapshot['files']),('contracts',snapshot['contract_files'])]:
    for rel,row in rows.items():
        p=packet/folder/rel
        if not p.is_file() or n.sha(n.regular(p))!=row['sha256']:drift.append(folder+'/'+rel)
assert not drift,drift
prior=load(packet/'prior.json');original=load(packet/'original-prior.json')
assert (len(original['findings']),len(original['unknowns']))==(101,39)
assert all(prior['findings'][k]==v for k,v in original['findings'].items()) and all(prior['unknowns'][k]==v for k,v in original['unknowns'].items())
errors=[];rank={'suggestion':0,'important':1,'must':2}
findings=report.get('prior_dispositions',[]);unknowns=report.get('unknown_dispositions',[])
for name,rows in [('findings',findings),('unknowns',unknowns)]:
    keys=[r.get('key') for r in rows]
    if len(keys)!=len(set(keys)) or set(keys)!=set(prior[name]):errors.append(name+' prior key coverage mismatch')
for row in findings:
    if row.get('key') not in prior['findings']:continue
    old=prior['findings'][row['key']]['original']
    if rank.get(row.get('severity'),-1)<rank.get(old.get('severity'),0):errors.append('grade missing/downgraded: '+row['key'])
    if row.get('obligation')!=old.get('obligation'):errors.append('obligation changed: '+row['key'])
    if 'original' in row and row['original']!=old:errors.append('original changed: '+row['key'])
for row in unknowns:
    if row.get('key') in prior['unknowns'] and 'original' in row and row['original']!=prior['unknowns'][row['key']]['original']:errors.append('unknown original changed: '+row['key'])
out=B/'review';assert not out.exists()
session=s.Session(R,'owner-final-one-native-final-capture-01a1198f')
session.policy=dict(session.policy,operation_bytes=max(16*1024*1024,len(raw)*2+len(text.encode('utf-8'))*2+1024*1024))
assert session.policy['operation_bytes']<=134217728,'known input too large for reserved capture; inspect before any copy'
original_size=s.size;s.size=lambda roots:original_size(list(dict.fromkeys(str(p) for p in roots)))
with session:
    old=session.old_roots[:];session.old_roots=list(dict.fromkeys(old));assert set(old)==set(session.old_roots);session.track(B)
    s.write(out/'native-rollout.jsonl',raw);s.write(out/'original-final.txt',text.encode('utf-8'));s.write(out/'report.json',n.encode(report))
    actual=dict(request_id=q['request_id'],agent_path=spawn['agent_path'],child_id=meta['id'],parent_thread_id=spawn['parent_thread_id'],source_rollout=str(rollout),model=ctx['model'],effort=ctx['effort'],settings_model=ctx['collaboration_mode']['settings']['model'],settings_effort=ctx['collaboration_mode']['settings']['reasoning_effort'],final_timestamp=final['timestamp'],completed_timestamp=completed['timestamp'],native_rollout_sha256=n.sha(raw),original_final_sha256=n.sha(text.encode('utf-8')))
    s.write(B/'dispatch-actual.json',n.encode(actual))
    observation=dict(**actual,verdict=report['verdict'],source_candidate_verdict=report.get('source_candidate_verdict'),complete_product_acceptance=report.get('complete_product_acceptance'),snapshot_sha256=q['snapshot_hash'],frozen_file_hashes_checked=len(snapshot['files']),frozen_contract_hashes_checked=len(snapshot['contract_files']),frozen_drift=[],original_finding_keys=101,original_unknown_keys=39,inherited_finding_keys=len(prior['findings']),all_original_keys_disposed=not errors,schema_observations=errors,report_judgments_parent_modified=False,used=1,remaining=0,old_grants_remaining=0,legacy_receipt_or_permit_claimed=False,complete_product_delivered=False)
    s.write(out/'capture-observation.json',n.encode(observation));print(json.dumps(dict(verdict=report['verdict'],source_candidate_verdict=report.get('source_candidate_verdict'),complete_product_acceptance=report.get('complete_product_acceptance'),findings=len(report.get('findings',[])),unknowns=len(report.get('unknowns',[])),schema_observations=errors,child_id=meta['id'],used=1,remaining=0),ensure_ascii=False),flush=True)
