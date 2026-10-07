from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808/fixed-review-1'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T04-23-04-01a11808-ac6c-7132-8e5d-391a499678cf.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
raw=ROLLOUT.read_bytes().splitlines()
matches=[]
for line in raw:
    row=json.loads(line);p=row.get('payload',{})
    if row.get('type')!='response_item' or p.get('type')!='message' or p.get('role')!='user':continue
    text='\n'.join(c.get('text','') for c in p.get('content',[]) if c.get('type') in ['input_text','text'])
    if text.strip()=='授权按结论做。':matches.append((line,row))
assert len(matches)==1
line,row=matches[0]
with s.Session(ROOT,'preserve-direct-fixed-one-arrival-review-authorization') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(OUT);OUT.mkdir(exist_ok=False)
    authorization=dict(grant_id='fixed-arrival-repair-verification-20261008-01a11808',batch='local-wait-admission-gates-20261004',source_thread='01a11808-ac6c-7132-8e5d-391a499678cf',
        source_authority='direct human user authorization of the preceding fixed-one conclusion',user_text='授权按结论做。',source_timestamp=row['timestamp'],source_rollout=str(ROLLOUT),source_line_sha256=hashlib.sha256(line).hexdigest(),
        maximum_additional_requests=1,model='gpt-6.1-sol',effort='high',stage='implementation',
        scope='One independent verification of PATH-ARRIVAL-REVISION-INSERT-1 repair and affected path, stop and deadline protection; source/test/actual candidate already implemented. No renewed whole-history, tools, SDK or unrelated feature scope.',
        failed_or_unknown_dispatch_counts=True,automatic_renewal=False,old_fixed_grant_used=2,old_fixed_grant_remaining=0,old_reports_opening_history_not_reset=True,production_authorization=False)
    s.write(OUT/'authorization-source.jsonl',line+b'\n')
    s.write(OUT/'authorization.json',json.dumps(authorization,ensure_ascii=False,indent=2).encode('utf-8'))
    s.write(OUT/'allowance-initial.json',json.dumps(dict(grant_id=authorization['grant_id'],authorized=1,used=0,remaining=1,old_fixed_grant_used=2,old_fixed_grant_remaining=0,new_dispatches=0),indent=2).encode('utf-8'))
    print('DIRECT HUMAN FIXED ONE AUTHORIZATION PRESERVED; used0 remaining1; no reviewer dispatched',flush=True)
