"""Reuse slot-1's ordinary immutable-source entry; no legacy permit or receipt."""
from pathlib import Path
import datetime, json, subprocess, sys, uuid
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'_workflow/local-wait-admission-gates-20261004/delivery-review-20261007'
WORK=Path(__file__).resolve().parent/'path-cutoff-01a1176e'
PREVIOUS=Path('E:/CodexReviewSnapshots/terminal-candidate-20261004/407e74062dd342f1a0d590eba122a2fb')
POOL=PREVIOUS.parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import native_review as n
import storage_limits as s
load=lambda path:json.loads(path.read_bytes())
def normalize_roots(budget):
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
GENERATED=['Test/IpcIncidentAudit/baseline-output/','Test/IpcIncidentAudit/product-output/','Test/IpcIncidentAudit/service-test-output/','Test/IpcChannelContractAudit/bgi-output/','Test/IpcChannelContractAudit/bgi-test-output/','_batch15/trx/','_batch16/probe/']
def inventory():
    excluded=[];all_files=n.inventory(ROOT,excluded);text={};nontext={}
    for rel,row in all_files.items():
        if any(rel.startswith(prefix) for prefix in GENERATED):
            excluded.append(dict(path=rel,reason='same specific historical compiled-output exclusion as slot1; preserved in place'))
            continue
        data=n.regular(ROOT/rel)
        try:
            data.decode('utf-8-sig')
            if b'\0' in data:raise UnicodeError('nontext')
        except UnicodeError:nontext[rel]=row;continue
        text[rel]=row
    return text,nontext,excluded
phase=sys.argv[1]
if phase=='freeze':
    assert not (BASE/'dispatch-intent-2.json').exists(),'slot2 intent exists; do not create another reviewer'
    assert load(BASE/'allowance-after-slot1.json')['remaining']==1
    prior=load(PREVIOUS/'prior.json');assert (len(prior['findings']),len(prior['unknowns']))==(99,39)
    inherited=load(PREVIOUS/'prior.json')
    report=load(BASE/'review-1/report.json')
    for finding in report['findings']:
        key=n.sha(n.encode(finding))
        inherited['findings'][key]=dict(key=key,original=finding,source='_workflow/local-wait-admission-gates-20261004/delivery-review-20261007/review-1/report.json')
    for key,value in prior['findings'].items():assert inherited['findings'][key]==value
    for key,value in prior['unknowns'].items():assert inherited['unknowns'][key]==value
    inherited['sources']['_workflow/local-wait-admission-gates-20261004/delivery-review-20261007/review-1/report.json']=n.sha(n.regular(BASE/'review-1/report.json'))
    rid=uuid.uuid4().hex;out=POOL/rid
    with s.Session(ROOT,'fixed-consolidated-repair-freeze-01a1176e-slot2') as budget:
        normalize_roots(budget);budget.track(out);budget.track(BASE)
        files,nontext,excluded=inventory();git_before=n.git_identity(ROOT);patches=n.git_patches(ROOT)
        contracts={}
        def add(rel,path,expected=None):
            content=n.regular(path);digest=n.sha(content)
            if expected is not None:assert digest==expected,(rel,'frozen historical input drift')
            if rel in contracts:assert contracts[rel]['sha256']==digest,rel
            contracts[rel]=dict(origin=str(path),sha256=digest,bytes=len(content))
        previous_meta=load(PREVIOUS/'snapshot.json')
        for rel,row in previous_meta['contract_files'].items():add(rel,PREVIOUS/'contracts'/rel,row['sha256'])
        for path in PREVIOUS.iterdir():
            if path.is_file():add('_slot1/'+path.name,path)
        supplement=load(PREVIOUS/'supplement-1/manifest.json')
        add('_slot1-supplement/manifest.json',PREVIOUS/'supplement-1/manifest.json')
        for rel,row in supplement['files'].items():add('_slot1-supplement/'+rel,PREVIOUS/'supplement-1/contracts'/rel,row['sha256'])
        for path in (BASE/'review-1').iterdir():
            if path.is_file():add('_slot1-report/'+path.name,path)
        for name in ['authorization.json','authorization-source.jsonl','allowance.json','allowance-after-slot1.json','dispatch-intent-1.json','dispatch-actual-1.json']:
            add(BASE.relative_to(ROOT).as_posix()+'/'+name,BASE/name)
        for path in WORK.rglob('*'):
            if path.is_file() and path.suffix in {'.json','.jsonl','.log','.trx','.md','.patch','.cs'} and 'before' not in path.relative_to(WORK).parts:
                add(path.relative_to(ROOT).as_posix(),path)
        for name in ['path_cutoff_check.py','path_cutoff_pipeline.py','path_cutoff_repair.py','path_cutoff_runtime.py','path_cutoff_review.py']:
            path=WORK.parent/name;add(path.relative_to(ROOT).as_posix(),path)
        for rel in ['AGENTS.md','Docs/technical/mistletoe-startup-migration-recovery.md','_workflow/usable-delivery-20261003/DELIVERY-COVERAGE.md']:
            # Slot1's old original remains under a distinct prefix if current prose changed.
            if rel in contracts and n.sha(n.regular(ROOT/rel))!=contracts[rel]['sha256']:
                old=contracts.pop(rel);contracts['_slot1-original-current-doc/'+rel]=old
            add(rel,ROOT/rel)
        records=[(row['sha256'],row['bytes']) for row in [*files.values(),*contracts.values()]]+[(n.sha(data),len(data)) for _,data in patches]
        s.preflight_objects(POOL,records);budget.check(location=out)
        print('SLOT2_COPY_PREFLIGHT',rid,len(files),sum(v['bytes'] for v in files.values()),len(contracts),sum(v['bytes'] for v in contracts.values()),flush=True)
        for rel,row in files.items():
            data=n.regular(ROOT/rel);assert n.sha(data)==row['sha256'];s.immutable(POOL,out/'source'/rel,data)
        for rel,row in contracts.items():
            data=n.regular(Path(row['origin']));assert n.sha(data)==row['sha256'];s.immutable(POOL,out/'contracts'/rel,data)
        current,current_nontext,_=inventory();assert current==files and current_nontext==nontext
        assert n.git_identity(ROOT)==git_before and n.git_patches(ROOT)==patches
        for index,(meta,data) in enumerate(patches):s.immutable(POOL,out/'git'/(meta['phase']+'-'+str(index)+'.patch'),data)
        s.write(out/'git-identity.json',n.encode(git_before))
        longpath=subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT)
        s.write(out/'git-status-longpaths.txt',longpath)
        s.write(out/'original-prior.json',n.regular(PREVIOUS/'prior.json'))
        s.write(out/'prior.json',n.encode(inherited))
        obligations=dict(original_99_finding_keys=99,original_39_unknown_keys=39,inherited_finding_keys=len(inherited['findings']),prior_report='_slot1-report/report.json',new_findings=['PATH-STRUCTURAL-LOOP-ENTRY-1','LOOP-CUTOFF-TIMED-NODE-1'],repair='Four shared planner/runner sources plus finite PathLoopCutoffTests; real Rebuild/TRX/PFP and exact restored bytes; current same product finite actual boundary observation.',actual_scope='Conditions are quiet finite native WPF workflow inputs at shared path/wait boundary. Resource side-effect rejection proven in fake-boundary Runner fixtures; actual game/resource/Skip/account/team/shutdown effects remain user-pending. Do not claim these from local conditions.',unchanged_prior_dispositions='Slot1 limited resolutions retained with original report/source; unchanged UI/migration/software-exit evidence reused only when dependency/conditions equivalent.',historical_obligations='Original open grades, keys, failures, SDK/tool/bundle/r61 gaps retained. Fixed owner complete-usable contract permits supported-version boundary retention, not fake closed.',no_legacy_permit_or_receipt=True)
        s.write(out/'executor-obligations.json',n.encode(obligations))
        assessment=dict(model='gpt-6.1-sol',effort='high',stage='implementation',risk_unresolved=True,reason='Two important normal path/timed-wait risks share durable arrival, stop and deadline settlement. Verify one stable repair and complete product scope with original obligations; fixed final owner slot, no new plan/function review.',dimensions={
            'scope':'Complete current BGI+assistant agreed functionality and formal WPF; concentrated slot1 repair verification, no new function or history certification.',
            'state':'Canonical primary entry, fresh round/cursor identity, absolute deadline and original external/LocalWait settlement.',
            'concurrency':'Asynchronous node/round wakeup, actual stop authority, pause/resume and no post-cutoff new arrival; no stress permutations.',
            'fault':'Late wakeup, unavailable facts, preserved old corrupt/unsupported failures; exact byte restoration and same product identity.',
            'impact_chain':'Formal editor global sorting to Planner/Runner shared timing, Host/admission/terminal and BGI boundary; freely chase frozen callers.',
            'change_scale':'Four shared source files and one finite test class since slot1; protected csproj and migration dependencies remain included.',
            'uncertainty':'Separate fake-boundary tests, actual quiet WPF boundary runs, original local migration/UI evidence and user-pending game/effects.',
            'prior_findings':'All original 99 findings/39 unknown keys structurally preserved plus slot1 new findings; original report blocked stays unchanged.'})
        s.write(out/'assessment.json',n.encode(assessment))
        meta=dict(kind='equivalent immutable full textual project source; ordinary provenance, no legacy certification',request_id=rid,source=str(out/'source'),contracts=str(out/'contracts'),source_root=str(ROOT),files=files,contract_files=contracts,excluded=excluded,non_text_metadata=nontext,git=git_before,model='gpt-6.1-sol',effort='high',stage='implementation',grant=str(BASE/'authorization.json'),original_99_39_objects_preserved=True,prior_slot1_snapshot_hash='b5d8dd2d0d366986bf4acf857dfcbec5087d2f66e59928133455531c907d2470',scope='Final fixed-slot concentrated implementation repair verification; whole relevant project/caller dependencies readable; agreed complete product and formal UI, bounded real evidence, no history/tool/SDK expansion.')
        encoded=n.encode(meta);s.write(out/'snapshot.json',encoded)
        request=dict(kind='bounded independent native implementation review; equivalent source evidence, not legacy certification',request_id=rid,stage='implementation',snapshot_hash=n.sha(encoded),snapshot=str(out/'snapshot.json'),prior=str(out/'prior.json'),executor_obligations=str(out/'executor-obligations.json'),grant_id=load(BASE/'authorization.json')['grant_id'],grant_slot=2,agent_path='/root/consolidated_repair_20261008_2',parent_thread='01a1176e-6732-7bd2-9b74-38989fbe5fbe',model='gpt-6.1-sol',effort='high',original_opening_history_policy_unchanged=True,production_authorization=False)
        s.write(out/'request.json',n.encode(request))
        observation=dict(request_id=rid,snapshot=str(out),snapshot_sha256=n.sha(encoded),source_files=len(files),source_bytes=sum(v['bytes'] for v in files.values()),contract_files=len(contracts),contract_bytes=sum(v['bytes'] for v in contracts.values()),original_findings=99,original_unknowns=39,inherited_findings=len(inherited['findings']),inherited_unknowns=len(inherited['unknowns']),new_dispatches=0,source_and_git_drift=False,legacy_receipt_or_permit_claimed=False)
        s.write(BASE/'review-snapshot-observation-2.json',n.encode(observation));budget.check(measure=True)
        print('SLOT2_FIXED_PACKET',json.dumps(observation),flush=True)
elif phase=='intent':
    observation=load(BASE/'review-snapshot-observation-2.json');out=Path(observation['snapshot']);request=load(out/'request.json')
    assert n.sha(n.regular(out/'snapshot.json'))==request['snapshot_hash']
    assert load(BASE/'allowance-after-slot1.json')['remaining']==1
    with s.Session(ROOT,'fixed-consolidated-repair-intent-01a1176e-slot2') as budget:
        normalize_roots(budget);budget.track(BASE)
        intent=dict(agent_path=request['agent_path'],created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),effort='high',grant_id=request['grant_id'],legacy_receipt_or_permit=False,model='gpt-6.1-sol',original_counts_opening_reports_unchanged=True,phase='consolidated repair verification',request_id=request['request_id'],slot=2,snapshot_hash=request['snapshot_hash'],status='dispatch_intent; failure/unknown charges final slot')
        s.write(BASE/'dispatch-intent-2.json',n.encode(intent))
        s.write(BASE/'allowance-after-slot2-intent.json',n.encode(dict(grant_id=request['grant_id'],authorized=2,used=2,remaining=0,intent='dispatch-intent-2.json',request_id=request['request_id'],original_counts_not_reset=True,actual_dispatch='not yet confirmed; uncertain counts')))
        print('FINAL SLOT INTENT PERSISTED',request['request_id'],flush=True)
else:raise ValueError(phase)
