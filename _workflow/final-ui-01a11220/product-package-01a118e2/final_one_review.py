"""One owner-authorized whole-version review using existing immutable provenance helpers."""
from pathlib import Path
import datetime,json,os,subprocess,sys,uuid
R=Path(__file__).resolve().parents[3];W=Path(__file__).resolve().parent
B=W/'final-one-review';PREVIOUS=Path('E:/CodexReviewSnapshots/terminal-candidate-20261004/8c795c4e92274393b1d148a722787014');POOL=PREVIOUS.parent
sys.path.insert(0,str(R/'tools/mistletoe'))
import native_review as n
import storage_limits as s
original_size=s.size
def same_root_set_size(roots):
    original=[str(p) for p in roots];unique=list(dict.fromkeys(original))
    assert set(original)==set(unique)
    return original_size(unique)
s.size=same_root_set_size
load=lambda p:json.loads(p.read_bytes())
GENERATED=['Test/IpcIncidentAudit/baseline-output/','Test/IpcIncidentAudit/product-output/','Test/IpcIncidentAudit/service-test-output/','Test/IpcChannelContractAudit/bgi-output/','Test/IpcChannelContractAudit/bgi-test-output/','_batch15/trx/','_batch16/probe/']
def budget(purpose,limit):
    b=s.Session(R,purpose);b.policy=dict(b.policy,operation_bytes=limit);return b
def dedup(b):
    old=b.old_roots[:];b.old_roots=list(dict.fromkeys(old));assert set(old)==set(b.old_roots)
def inventory():
    excluded=[];rows=n.inventory(R,excluded);text={};nontext={}
    for rel,row in rows.items():
        if any(rel.startswith(p) for p in GENERATED):
            excluded.append(dict(path=rel,reason='same explicit historical compiled-output exclusion as original requests; preserved'));continue
        data=n.regular(R/rel)
        try:
            data.decode('utf-8-sig')
            if b'\0' in data:raise UnicodeError('nontext')
        except UnicodeError:nontext[rel]=row;continue
        text[rel]=row
    return text,nontext,excluded
def candidate():
    product=R/'_workflow/runtime-unified-01a10e1b/product';v=load(product/'VERSION.json');rows=load(Path(v['compiled_input_hashes']))
    assert len(rows)==313 and all(n.sha(n.regular(R/p))==h for p,h in rows.items())
    assert len(v['assistant_modules'])==5 and all(n.sha(n.regular(product/m['path']))==m['sha256'] for m in v['assistant_modules'])
    assert n.sha(n.regular(product/'BetterGI.dll'))==v['bgi_dll_sha256'] and n.sha(n.regular(product/'BetterGI.exe'))==v['bgi_exe_sha256']
    return dict(compiled_inputs=rows,assistant_modules=v['assistant_modules'],bgi_dll=v['bgi_dll_sha256'],bgi_exe=v['bgi_exe_sha256'])
scope='Complete agreed BetterGI + assistant Mistletoe functionality and formal WPF UI; current mode-downgrade/Host cold resume and paused Stop fixes, original stop/owner/Unknown/epoch/path/cutoff contracts, normal old-data migration and evidence reuse; one unified implementation disposition, no local plan or renewed history/tools/SDK certification.'
assessment=dict(model='gpt-6.1-sol',effort='high',stage='implementation',scope=scope,risk_unresolved=True,
    reason='Owner approved exactly one whole-version high review; two important durable run/admission repair obligations and source/actual reuse require unified independent judgment.',dimensions={
    'scope':scope,'state':'Path-bound durable cursor cannot downgrade to sequential; refused Resume leaves Paused bytes unchanged; explicit paused Stop settles durable intent/run seal/original operation.',
    'concurrency':'Same BGI/epoch cold Host, runner waits and definitions saved while paused, explicit Stop retry/seal failure; original ownership/Unknown/epoch guards retained.',
    'fault':'Red/green/negative/restored causal proofs, original corrupt/unsupported LocalWait failures, cold actual refusal and durable terminal release; carrier Id/id assertion failure and interruption preserved.',
    'impact_chain':'Formal WPF editor/task entry -> Planner/Runner/Host -> RunStore/admission -> BGI native, group/JS/macro and terminal/migration consumers; frozen project autonomously readable.',
    'change_scale':'Current 313 compiled inputs and five assistant modules; two shared Host/Runner fixes after prior review, protected csproj/migration dependency changes included.',
    'uncertainty':'Local WPF/stop/restart/data acceptance directly observed; previous UI/migration/C17 evidence reused only by source/method/dependency/condition. Game/resources/account/team/Skip/game-exit/OS-shutdown effects remain user feedback boundary.',
    'prior_findings':'Original 101 findings/39 unknown objects unchanged; previous final blocked report and new paused-Stop obligation retained, original opening/counts/reports not reset.'})
phase=sys.argv[1]
if phase=='precheck':
    assert not B.exists()
    with budget('owner-final-one-mechanical-precheck-01a1198f',16*1024*1024) as b:
        dedup(b);b.track(B);s.write(B/'assessment.json',n.encode(assessment))
        commands=[['python','-B','tools/mistletoe/review_process.py','verify-bundle'],
            ['python','-B','tools/mistletoe/workflow.py','audit','--manifest','_workflow/local-wait-admission-gates-20261004/manifest.json','--out',str(B.relative_to(R)/'mechanical-audit'),'--stage','review']]
        observations=[]
        for i,argv in enumerate(commands):
            result=subprocess.run(argv,cwd=R,capture_output=True)
            s.write(B/('precheck-'+str(i)+'.log'),result.stdout+result.stderr)
            observations.append(dict(argv=argv,exit_code=result.returncode,output='precheck-'+str(i)+'.log'))
        try:
            n.prepare(str(R),'_workflow/local-wait-admission-gates-20261004/native-review-config.json','implementation',str((B/'assessment.json').relative_to(R)),'/root/final_one_complete_version')
            raise AssertionError('old unrestricted policy unexpectedly accepted')
        except n.Blocked as error:
            observations.append(dict(native_prepare_blocked=str(error),request_sent=False))
        s.write(B/'mechanical-observations.json',n.encode(observations));print(json.dumps(observations),flush=True)
elif phase=='freeze':
    assert (B/'mechanical-observations.json').exists() and not (B/'snapshot-observation.json').exists() and not (B/'dispatch-intent.json').exists()
    auth=load(W/'owner-resume-final-one-20261008/OWNER-AUTHORIZATION.json')
    assert auth['marker']=='OWNER-RESUME-FINAL-ONE-20261008-FROM-01a11963' and auth['review']['maximum_additional_requests']==1 and auth['review']['remaining']==1 and not auth['review']['request_sent']
    actual=load(W/'runtime-paused-stop-resume2/result-supplement.json');assert actual['actual_verified_for_this_scene'] and actual['app_exit_codes']==[0,0,0] and actual['controller_exit']==1
    with budget('owner-final-one-whole-version-freeze-01a1198f',256*1024*1024) as b:
        dedup(b);rid=uuid.uuid4().hex;out=POOL/rid;b.track(out);b.track(B)
        ident=candidate();files,nontext,excluded=inventory();git=n.git_identity(R);patches=n.git_patches(R);contracts={}
        def add(rel,path,expected=None):
            content=n.regular(path);h=n.sha(content)
            if expected is not None:assert h==expected,(rel,'original frozen input drift')
            if rel in contracts:assert contracts[rel]['sha256']==h,rel
            contracts[rel]=dict(origin=str(path),sha256=h,bytes=len(content))
        meta=load(PREVIOUS/'snapshot.json')
        for rel,row in meta['contract_files'].items():add('_previous-contracts/'+rel,PREVIOUS/'contracts'/rel,row['sha256'])
        for p in PREVIOUS.iterdir():
            if p.is_file():add('_previous-request/'+p.name,p)
        report_dir=R/'_workflow/final-ui-01a11220/arrival-revision-01a11808/fixed-review-1/review'
        for p in report_dir.iterdir():
            if p.is_file():add('_previous-report/'+p.name,p)
        original=load(PREVIOUS/'prior.json');assert (len(original['findings']),len(original['unknowns']))==(101,39)
        prior=json.loads(n.encode(original));report=load(report_dir/'report.json')
        for finding in report['findings']:
            key=n.sha(n.encode(finding));prior['findings'][key]=dict(key=key,original=finding,source=str((report_dir/'report.json').relative_to(R)))
        for key,row in original['findings'].items():assert prior['findings'][key]==row
        for key,row in original['unknowns'].items():assert prior['unknowns'][key]==row
        prior['sources'][str((report_dir/'report.json').relative_to(R))]=n.sha(n.regular(report_dir/'report.json'))
        evidence_dirs=[W/'host-mode-repair',W/'paused-stop-repair',W/'runtime-hostfix',W/'runtime-paused-stop',W/'runtime-paused-stop-resume1',W/'runtime-paused-stop-resume2',W/'final-disposition-preparation',W/'owner-resume-final-one-20261008',R/'_workflow/final-ui-01a11220/arrival-mode-downgrade-01a11897']
        for directory in evidence_dirs:
            for p in directory.rglob('*'):
                rel=p.relative_to(directory)
                if not p.is_file() or p.suffix not in {'.json','.jsonl','.md','.txt','.log','.trx','.patch','.cs'}:continue
                if any(part in {'private','temp','obj','bin','metadata-checkpoint','previous'} for part in rel.parts):continue
                add(p.relative_to(R).as_posix(),p)
        for rel in ['AGENTS.md','槲寄生调度器总计划.md','Docs/design/mistletoe-complete-usable-delivery-20261006.md','Docs/technical/mistletoe-startup-migration-recovery.md','_workflow/usable-delivery-20261003/DELIVERY-FIRST-POLICY.md','_workflow/usable-delivery-20261003/DELIVERY-COVERAGE.md','_workflow/full-product-01a10e1b/ui-research-source-binding.json','_workflow/final-ui-01a11220/arrival-revision-01a11808/INTAKE.md','_workflow/final-ui-01a11220/product-package-01a118e2/evidence-reuse.json','_workflow/runtime-unified-01a10e1b/product/VERSION.json',str((B/'mechanical-observations.json').relative_to(R)),str(Path(__file__).resolve().relative_to(R))]:add(rel,R/rel)
        for rel in ['private/native-ui-source.jsonl','private/first-exit/runs/run-064d11070de2.run.json','private/second-exit/runs/run-064d11070de2.run.json','private/after/runs/run-064d11070de2.run.json','private/arbitration-final.json']:
            p=W/'runtime-paused-stop-resume2'/rel;add(p.relative_to(R).as_posix(),p)
        records=[(row['sha256'],row['bytes']) for row in [*files.values(),*contracts.values()]]+[(n.sha(data),len(data)) for _,data in patches]
        needed=s.preflight_objects(POOL,records);b.check(location=out)
        print('FINAL_ONE_COPY_PREFLIGHT',rid,len(files),len(contracts),needed,flush=True)
        for folder,rows in [('source',files),('contracts',contracts)]:
            for rel,row in rows.items():
                data=n.regular(R/rel if folder=='source' else Path(row['origin']));assert n.sha(data)==row['sha256'];s.immutable(POOL,out/folder/rel,data)
        now,now_nontext,_=inventory();assert now==files and now_nontext==nontext and n.git_identity(R)==git and n.git_patches(R)==patches and candidate()==ident
        for i,(row,data) in enumerate(patches):s.immutable(POOL,out/'git'/(row['phase']+'-'+str(i)+'.patch'),data)
        s.write(out/'git-identity.json',n.encode(git));s.write(out/'git-status-longpaths.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=R))
        s.write(out/'original-prior.json',n.regular(PREVIOUS/'prior.json'));s.write(out/'prior.json',n.encode(prior));s.write(out/'candidate-identity.json',n.encode(ident));s.write(out/'assessment.json',n.encode(assessment))
        obligations=dict(complete_goal=load(W/'final-disposition-preparation/unified-disposition-proposal.json')['proposed_after_current_actual']['scope'],scope=scope,
            original_finding_keys=101,original_unknown_keys=39,current_inherited_finding_keys=len(prior['findings']),
            current_important_obligations=['PATH-ARRIVAL-MODE-DOWNGRADE-1','PAUSED-STOP-TERMINAL-RECONCILIATION-1'],
            current_actual=str((W/'runtime-paused-stop-resume2/result-supplement.json').relative_to(R)),
            actual_carrier_failure_retained='controller exit1 KeyError id; original app exits0/0/0, Job0 and exact persisted Id supplementary oracle',
            prior_report='_previous-report/report.json',original_opening='_workflow/local-wait-admission-gates-20261004/opening.json',
            old_allowances='old 2/2 plus 1/1 exhausted; this separate explicit owner grant is exactly one, no count reset or renewal',
            protected='Real User, third-party JS, D-drive/game, original reports/grades/failures/history/material-outside dependencies unchanged',
            game_boundary='User supplies game/resource/account/team/Skip/game-exit/OS shutdown effects voluntarily; not accepted by these local scenes',
            production_authorization=False,legacy_certification_claimed=False)
        s.write(out/'executor-obligations.json',n.encode(obligations))
        snapshot=dict(kind='equivalent immutable full textual project and contracts; ordinary provenance',request_id=rid,source=str(out/'source'),contracts=str(out/'contracts'),source_root=str(R),files=files,contract_files=contracts,non_text_metadata=nontext,excluded=excluded,git=git,model='gpt-6.1-sol',effort='high',stage='implementation',scope=scope)
        encoded=n.encode(snapshot);s.write(out/'snapshot.json',encoded)
        request=dict(kind='owner final one whole-version native implementation review; no legacy receipt',request_id=rid,stage='implementation',snapshot_hash=n.sha(encoded),snapshot=str(out/'snapshot.json'),prior=str(out/'prior.json'),executor_obligations=str(out/'executor-obligations.json'),grant_id=auth['marker'],grant_slot=1,agent_path='/root/final_one_complete_version',parent_thread=os.environ['CODEX_THREAD_ID'],model='gpt-6.1-sol',effort='high',original_opening_history_policy_unchanged=True,production_authorization=False)
        s.write(out/'request.json',n.encode(request));observation=dict(request_id=rid,snapshot=str(out),snapshot_sha256=request['snapshot_hash'],source_files=len(files),contract_files=len(contracts),new_object_bytes=needed,findings=len(prior['findings']),unknowns=39,new_dispatches=0,source_and_git_drift=False,legacy_receipt_or_permit_claimed=False)
        s.write(B/'snapshot-observation.json',n.encode(observation));print(json.dumps(observation),flush=True)
elif phase=='intent':
    obs=load(B/'snapshot-observation.json');out=Path(obs['snapshot']);q=load(out/'request.json');assert candidate()==load(out/'candidate-identity.json') and n.sha(n.regular(out/'snapshot.json'))==q['snapshot_hash']
    assert not (B/'dispatch-intent.json').exists()
    with budget('owner-final-one-dispatch-intent-01a1198f',4*1024*1024) as b:
        dedup(b);b.track(B);intent=dict(agent_path=q['agent_path'],parent_thread=q['parent_thread'],created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),model='gpt-6.1-sol',effort='high',grant_id=q['grant_id'],slot=1,request_id=q['request_id'],snapshot_hash=q['snapshot_hash'],status='dispatch_intent; failure/unknown consumes fixed one',original_counts_opening_reports_unchanged=True)
        s.write(B/'dispatch-intent.json',n.encode(intent));s.write(B/'allowance-after-intent.json',n.encode(dict(grant_id=q['grant_id'],authorized=1,used=1,remaining=0,request_id=q['request_id'],intent='dispatch-intent.json',old_grants_remaining=0,automatic_renewal=False)))
        print(json.dumps(intent),flush=True)
else:raise ValueError(phase)
