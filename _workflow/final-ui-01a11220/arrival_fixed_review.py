"""Fixed owner-authorized arrival repair review, ordinary immutable provenance."""
from pathlib import Path
import datetime, json, os, subprocess, sys, uuid

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / '_workflow/final-ui-01a11220/arrival-revision-01a11808'
BASE = WORK / 'fixed-review-1'
PREVIOUS = Path('E:/CodexReviewSnapshots/terminal-candidate-20261004/2543a6b07d42494ca1a0983208fa9921')
POOL = PREVIOUS.parent
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
import native_review as n
import storage_limits as s

load = lambda p: json.loads(p.read_bytes())
GENERATED = ['Test/IpcIncidentAudit/baseline-output/', 'Test/IpcIncidentAudit/product-output/', 'Test/IpcIncidentAudit/service-test-output/', 'Test/IpcChannelContractAudit/bgi-output/', 'Test/IpcChannelContractAudit/bgi-test-output/', '_batch15/trx/', '_batch16/probe/']

def normalize_roots(budget):
    old = budget.old_roots[:]
    budget.old_roots = list(dict.fromkeys(old))
    assert set(old) == set(budget.old_roots)

def inventory():
    excluded = []; all_files = n.inventory(ROOT, excluded); text = {}; nontext = {}
    for rel, row in all_files.items():
        if any(rel.startswith(prefix) for prefix in GENERATED):
            excluded.append(dict(path=rel, reason='same specific historical compiled-output exclusion as prior slots; preserved in place'))
            continue
        data = n.regular(ROOT / rel)
        try:
            data.decode('utf-8-sig')
            if b'\0' in data: raise UnicodeError('nontext')
        except UnicodeError:
            nontext[rel] = row; continue
        text[rel] = row
    return text, nontext, excluded

def candidate_identity():
    rows = load(WORK / 'restored/source-hashes.json')
    assert len(rows) == 313
    assert all(n.sha(n.regular(ROOT / p)) == h for p, h in rows.items()), 'compiled source drift'
    version = load(WORK / 'delivery/VERSION.json'); product = Path(version['product'])
    assert len(version['assistant_modules']) == 5
    assert all(n.sha(n.regular(product / x['path'])) == x['sha256'] for x in version['assistant_modules']), 'assistant module drift'
    assert n.sha(n.regular(product / 'BetterGI.dll')) == version['bgi_dll_sha256']
    assert n.sha(n.regular(product / 'BetterGI.exe')) == version['bgi_exe_sha256']
    return dict(compiled_inputs=rows, assistant_modules=version['assistant_modules'], bgi_dll=version['bgi_dll_sha256'], bgi_exe=version['bgi_exe_sha256'])

phase = sys.argv[1]
if phase == 'freeze':
    assert not (BASE / 'dispatch-intent.json').exists(), 'intent exists; never dispatch another reviewer'
    assert not (BASE / 'snapshot-observation.json').exists(), 'existing packet identity must be recovered, not replaced'
    auth = load(BASE / 'authorization.json'); allowance = load(BASE / 'allowance-initial.json')
    assert auth['maximum_additional_requests'] == 1 and allowance['used'] == 0 and allowance['remaining'] == 1
    prior = load(PREVIOUS / 'prior.json'); assert (len(prior['findings']), len(prior['unknowns'])) == (101, 39)
    rid = uuid.uuid4().hex; out = POOL / rid
    with s.Session(ROOT, 'fixed-arrival-repair-freeze-01a11897-one') as budget:
        normalize_roots(budget); budget.track(out); budget.track(BASE)
        identity = candidate_identity(); files, nontext, excluded = inventory()
        git_before = n.git_identity(ROOT); patches = n.git_patches(ROOT)
        contracts = {}
        def add(rel, path, expected=None):
            content = n.regular(path); digest = n.sha(content)
            if expected is not None: assert digest == expected, (rel, 'historical contract drift')
            if rel in contracts: assert contracts[rel]['sha256'] == digest, rel
            contracts[rel] = dict(origin=str(path), sha256=digest, bytes=len(content))
        previous_meta = load(PREVIOUS / 'snapshot.json')
        for rel, row in previous_meta['contract_files'].items(): add('_previous-contracts/' + rel, PREVIOUS / 'contracts' / rel, row['sha256'])
        for path in PREVIOUS.iterdir():
            if path.is_file(): add('_previous-request/' + path.name, path)
        old_report = ROOT / '_workflow/local-wait-admission-gates-20261004/delivery-review-20261007/review-2'
        for path in old_report.iterdir():
            if path.is_file(): add('_previous-report/' + path.name, path)
        for path in WORK.rglob('*'):
            rel = path.relative_to(WORK)
            if not path.is_file() or path.suffix not in {'.json', '.jsonl', '.log', '.trx', '.md', '.txt', '.patch', '.cs'}: continue
            if any(x in rel.parts for x in ['before', 'private', 'temp', 'obj', 'bin']): continue
            if rel.parts[0] == 'fixed-review-1' and path.name not in ['authorization.json', 'authorization-source.jsonl', 'allowance-initial.json', 'adoption.json']: continue
            add(path.relative_to(ROOT).as_posix(), path)
        startup = ROOT / '_workflow/final-ui-01a11220/own-runtime/startup-center-01a11808'
        for name in ['ACTUAL.md', 'observations.json', 'evidence-index.json', 'VERSION.json']:
            add((startup / name).relative_to(ROOT).as_posix(), startup / name)
        # Own-instance native UI evidence only; private User/protocol inventories stay excluded.
        for rel in ['_workflow/final-ui-01a11220/arrival-revision-01a11808/actual/private/native-ui-source.jsonl', '_workflow/final-ui-01a11220/own-runtime/startup-center-01a11808/first-tree-run-cancel-save/private/native-ui-source.jsonl', '_workflow/final-ui-01a11220/own-runtime/startup-center-01a11808/cold-reload-tree/private/native-ui-source.jsonl']:
            add(rel, ROOT / rel)
        for rel in ['AGENTS.md', 'Docs/technical/mistletoe-startup-migration-recovery.md', '_workflow/usable-delivery-20261003/DELIVERY-COVERAGE.md', '_workflow/final-ui-01a11220/arrival_fixed_review.py', '_workflow/final-ui-01a11220/path_cutoff_review.py', '_workflow/final-ui-01a11220/path_cutoff_capture.py']:
            add(rel, ROOT / rel)
        records = [(row['sha256'], row['bytes']) for row in [*files.values(), *contracts.values()]] + [(n.sha(data), len(data)) for _, data in patches]
        s.preflight_objects(POOL, records); budget.check(location=out)
        print('ARRIVAL_COPY_PREFLIGHT', rid, len(files), sum(x['bytes'] for x in files.values()), len(contracts), sum(x['bytes'] for x in contracts.values()), flush=True)
        for folder, rows in [('source', files), ('contracts', contracts)]:
            for rel, row in rows.items():
                data = n.regular(ROOT / rel if folder == 'source' else Path(row['origin']))
                assert n.sha(data) == row['sha256']; s.immutable(POOL, out / folder / rel, data)
        current, current_nontext, _ = inventory(); assert current == files and current_nontext == nontext
        assert n.git_identity(ROOT) == git_before and n.git_patches(ROOT) == patches
        assert candidate_identity() == identity
        for index, (meta, data) in enumerate(patches): s.immutable(POOL, out / 'git' / (meta['phase'] + '-' + str(index) + '.patch'), data)
        s.write(out / 'git-identity.json', n.encode(git_before))
        s.write(out / 'git-status-longpaths.txt', subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT))
        s.write(out / 'prior.json', n.regular(PREVIOUS / 'prior.json'))
        s.write(out / 'candidate-identity.json', n.encode(identity))
        scope = 'Exactly one implementation verification of PATH-ARRIVAL-REVISION-INSERT-1 and reachable affected path/stop/deadline protection. Full frozen relevant callers readable; no renewed whole-history/tools/SDK/unrelated-feature review. Prior objects and old reported scoped dispositions retained, not reclosed.'
        assessment = dict(model='gpt-6.1-sol', effort='high', stage='implementation', scope=scope, reason='Waiting rebase changes the same durable arrival/cursor lifecycle. Current deletion/type/resource edits, selected path identity and stop/cutoff guards need one bounded independent high verification.', dimensions={
            'scope': scope, 'state': 'Same run/cursor, post-wait rebase and current node identity/type/resource changes; legal repeats remain path/cycle-defined.',
            'concurrency': 'Asynchronous waits, definitions saved during wait, durable stop/paused authority and elapsed loop cutoff.',
            'fault': 'Deleted/type-changed current node, unavailable facts, original corrupt/unsupported failures and exact mutation restoration.',
            'impact_chain': 'WPF edit/save -> Planner -> shared WorkflowRunner -> Host/admission/resources -> durable arrival/outcome.',
            'change_scale': 'Six production lines in WorkflowRunner plus six PathArrivalRevisionTests; 313 source hashes and five assistant modules unchanged since restored run.',
            'uncertainty': 'Separate quiet condition actual run, fixture resource rejection, user-pending game/resources/Skip/effects; ordinary provenance only.',
            'prior_findings': 'Original 101 finding and 39 unknown objects byte-preserved; old slot2 applies only to old source, old failures/budgets/opening retained.'})
        s.write(out / 'assessment.json', n.encode(assessment))
        obligations = dict(scope=scope, current_finding=dict(id='PATH-ARRIVAL-REVISION-INSERT-1', severity='important', obligation='implementation', status='implemented_with_evidence_pending_independent_verification'), prior_report='_previous-report/report.json', preserved_prior_findings=101, preserved_prior_unknowns=39, current_evidence=WORK.relative_to(ROOT).as_posix(), real_acceptance_pending='Game/resource/Skip/account/team/game-exit/OS shutdown; no feedback solicitation. Full product not complete.', bundle='verify-bundle remains drifted; ordinary immutable evidence, no synthesized legacy receipt or permit', protected='Real User, JS, historical failures/opening/reports and material-outside dependencies remain intact')
        s.write(out / 'executor-obligations.json', n.encode(obligations))
        meta = dict(kind='equivalent immutable full textual project; ordinary provenance', request_id=rid, source=str(out / 'source'), contracts=str(out / 'contracts'), source_root=str(ROOT), files=files, contract_files=contracts, non_text_metadata=nontext, excluded=excluded, git=git_before, model='gpt-6.1-sol', effort='high', stage='implementation', scope=scope)
        encoded = n.encode(meta); s.write(out / 'snapshot.json', encoded)
        request = dict(kind='bounded independent native implementation repair verification; no legacy certification', request_id=rid, stage='implementation', snapshot_hash=n.sha(encoded), snapshot=str(out / 'snapshot.json'), prior=str(out / 'prior.json'), executor_obligations=str(out / 'executor-obligations.json'), grant_id=auth['grant_id'], grant_slot=1, agent_path='/root/arrival_repair_fixed_one', parent_thread=os.environ['CODEX_THREAD_ID'], model='gpt-6.1-sol', effort='high', original_opening_history_policy_unchanged=True, production_authorization=False)
        s.write(out / 'request.json', n.encode(request))
        observation = dict(request_id=rid, snapshot=str(out), snapshot_sha256=request['snapshot_hash'], source_files=len(files), source_bytes=sum(x['bytes'] for x in files.values()), contract_files=len(contracts), contract_bytes=sum(x['bytes'] for x in contracts.values()), findings=101, unknowns=39, new_dispatches=0, source_and_git_drift=False, legacy_receipt_or_permit_claimed=False)
        s.write(BASE / 'snapshot-observation.json', n.encode(observation)); budget.check(measure=True)
        print(json.dumps(observation), flush=True)
elif phase == 'intent':
    observation = load(BASE / 'snapshot-observation.json'); out = Path(observation['snapshot']); request = load(out / 'request.json')
    assert n.sha(n.regular(out / 'snapshot.json')) == request['snapshot_hash']
    assert load(BASE / 'allowance-initial.json')['remaining'] == 1
    assert candidate_identity() == load(out / 'candidate-identity.json')
    with s.Session(ROOT, 'fixed-arrival-repair-intent-01a11897-one') as budget:
        normalize_roots(budget); budget.track(BASE)
        intent = dict(agent_path=request['agent_path'], parent_thread=request['parent_thread'], created_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), model='gpt-6.1-sol', effort='high', grant_id=request['grant_id'], slot=1, request_id=request['request_id'], snapshot_hash=request['snapshot_hash'], status='dispatch_intent; failure/unknown consumes fixed one', original_counts_opening_reports_unchanged=True, legacy_receipt_or_permit=False)
        s.write(BASE / 'dispatch-intent.json', n.encode(intent))
        s.write(BASE / 'allowance-after-intent.json', n.encode(dict(grant_id=request['grant_id'], authorized=1, used=1, remaining=0, intent='dispatch-intent.json', request_id=request['request_id'], old_fixed_grant_used=2, old_fixed_grant_remaining=0, actual_dispatch='not yet confirmed; uncertain counts')))
        print('FIXED ONE INTENT PERSISTED', request['request_id'], flush=True)
else:
    raise ValueError(phase)
