"""Ordinary bounded P/F/P and version verification, not an independent verdict."""
from pathlib import Path
import hashlib, json, subprocess, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/arrival-mode-downgrade-01a11897'
REVIEW = ROOT / '_workflow/final-ui-01a11220/arrival-revision-01a11808/fixed-review-1'
OLD = ROOT / '_workflow/final-ui-01a11220/arrival-revision-01a11808/restored'
INTERRUPTED = ROOT / '_workflow/final-ui-01a11220/own-runtime/startup-supplement-01a11897'
DATA = ROOT / '_workflow/final-ui-01a11220/own-runtime/assistant-data'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
TARGET = 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlan.Path.cs'
TEST = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/PathArrivalRevisionTests.cs'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s

load = lambda p: json.loads(p.read_bytes())
sha = lambda b: hashlib.sha256(b).hexdigest()
def rows(path):
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    result = {}
    for row in ET.parse(path).findall('.//t:UnitTestResult', ns):
        tid = row.get('testId'); assert tid not in result
        result[tid] = dict(name=row.get('testName'), outcome=row.get('outcome'), message=row.findtext('.//t:Message', default='', namespaces=ns), stack=row.findtext('.//t:StackTrace', default='', namespaces=ns))
    return result
def hashes(root): return {f.relative_to(root).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(root)}

with s.Session(ROOT, 'arrival-mode-repair-source-pfp-safe-checkpoint-01a11897') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(INTERRUPTED)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    observations = {}; stages = {}; maps = {}
    for stage in ['red', 'green', 'negative', 'restored']:
        out = BASE / stage; value = load(out / 'result.json'); stages[stage] = rows(out / 'arrival-mode.trx'); maps[stage] = load(out / 'source-hashes.json')
        assert value['build'] == 0 and not value['source_drift'] and not value['other']
        for phase in ['build', 'test']: assert load(out / (phase + '-tree-terminal.json'))['active_processes'] == 0
        observations[stage] = dict(passed=len(value['passed']), failed=value['failed'], test_exit=value['test'], build_exit=value['build'], source_inputs=len(maps[stage]))
    assert maps['green'] == maps['restored']
    assert all(sha((ROOT / name).read_bytes()) == h for name, h in maps['restored'].items())
    assert {name for name in maps['green'] if maps['green'][name] != maps['negative'][name]} == {TARGET}
    assert {name for name in maps['green'] if maps['green'][name] != maps['red'][name]} == {TARGET}
    assert {name for name in maps['restored'] if maps['restored'][name] != load(OLD / 'source-hashes.json')[name]} == {TARGET, TEST}
    original = rows(OLD / 'arrival-revision.trx'); restored = stages['restored']
    old_failed = {tid for tid, row in original.items() if row['outcome'] == 'Failed'}
    assert old_failed == {tid for tid, row in restored.items() if row['outcome'] == 'Failed'} == {'7ff2dc0d-a3c3-d14e-a619-fee985a3deca', '62d390bb-e91f-98d9-6e78-38ce05d3ea47'}
    assert all(restored[tid]['outcome'] == row['outcome'] for tid, row in original.items())
    added = set(restored) - set(original); assert len(added) == 4 and all(restored[tid]['outcome'] == 'Passed' for tid in added)
    red_failed = {tid for tid, row in stages['red'].items() if row['outcome'] == 'Failed'}
    negative_failed = {tid for tid, row in stages['negative'].items() if row['outcome'] == 'Failed'}
    assert red_failed == negative_failed and len(red_failed) == 3 and red_failed <= added
    for tid in red_failed:
        assert stages['green'][tid]['outcome'] == restored[tid]['outcome'] == 'Passed'
        assert 'PathArrivalRevisionTests.cs' in stages['negative'][tid]['stack']
        assert ('Assert.Empty() Failure' in stages['negative'][tid]['message'] or 'No exception was thrown' in stages['negative'][tid]['message'])
    recovery = load(BASE / 'negative-source-restored.json'); assert recovery['exact'] and recovery['sha256'] == sha((ROOT / TARGET).read_bytes())
    assert load(REVIEW / 'review/capture-observation.json')['verdict'] == 'blocked'
    assert load(REVIEW / 'allowance-after-intent.json')['remaining'] == 0
    original_version = load(PRODUCT / 'VERSION.json')
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in original_version['assistant_modules'])
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == original_version['bgi_dll_sha256']
    assert sha((PRODUCT / 'BetterGI.exe').read_bytes()) == original_version['bgi_exe_sha256']
    assert (DATA / 'startup-flow.json').read_bytes() == (INTERRUPTED / 'private/original-startup-flow.json').read_bytes()
    assert (DATA / 'assistant-config.json').read_bytes() == (INTERRUPTED / 'private/original-assistant-config.json').read_bytes()
    preservation = {}
    for label, folder, before in [('runs', DATA / 'runs', INTERRUPTED / 'private/runs-before.json'), ('product_user', PRODUCT / 'User', INTERRUPTED / 'private/product-user-before.json'), ('real_user', ROOT / 'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User', INTERRUPTED / 'private/real-user-before.json')]:
        initial = load(before); current = hashes(folder); assert current == initial
        preservation[label] = dict(files=len(current), changed=[])
    status = subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT)
    s.write(BASE / 'current-workspace-status.txt', status)
    evidence = dict(kind='ordinary source-bound targeted causal regression; no independent pass or legacy receipt', finding='PATH-ARRIVAL-MODE-DOWNGRADE-1', severity='important', status='implemented_with_evidence_pending_independent_verification',
        source_sha256={name: sha((ROOT / name).read_bytes()) for name in [TARGET, TEST]}, unchanged_runner_sha256=sha((ROOT / 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs').read_bytes()), phases=observations,
        new_tests=[dict(test_id=tid, **restored[tid]) for tid in sorted(added)], causal_test_ids=sorted(red_failed), original_failed_ids=sorted(old_failed), original_outcomes_changed=[], tests_removed=[], source_restoration=recovery,
        data_preservation=preservation, own_startup_and_assistant_config_exactly_restored=True, computer_use='stopped by user; no further UI input; startup supplement incomplete',
        review_request='8c795c4e92274393b1d148a722787014', review_verdict_for_previous_candidate='blocked', review_model='gpt-6.1-sol', review_effort='high', used=1, remaining=0, old_fixed_grant_used=2, old_fixed_grant_remaining=0,
        old_product_modules_unchanged=original_version['assistant_modules'], new_product_refresh=False, new_actual_runtime_verification=False, product_complete=False, complete_goal_achieved=False)
    write(BASE / 'verification.json', evidence)
    write(BASE / 'OWNER-CHECKPOINT.json', dict(finding='PATH-ARRIVAL-MODE-DOWNGRADE-1', severity='important', current_status=evidence['status'], source_candidate=evidence['source_sha256'], targeted_evidence='verification.json',
        prior_independent_report=str(REVIEW / 'review/report.json'), original_independent_blocked_preserved=True, requested_if_owner_authorizes=dict(maximum_additional_requests=1, model='gpt-6.1-sol', effort='high', stage='implementation', scope='Only verification of repaired PATH-ARRIVAL-MODE-DOWNGRADE-1 and preservation of already reviewed insertion/path/stop/deadline guards', failed_or_unknown_counts=True, automatic_renewal=False),
        no_additional_authorization_received=True, no_additional_request_dispatched=True, production_authorization=False, actual_runtime_pending=True, startup_supplement_abandoned=True, product_complete=False))
    print('VERIFIED: red/negative7P3F, green/restored313P+same2LocalWait; four new cases; exact restoration; old product/data unchanged; fixed grant exhausted', flush=True)
