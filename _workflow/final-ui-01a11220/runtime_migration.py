"""Continue owned-product cold-start and migration acceptance under the existing budget."""
from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys, winreg

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'own-runtime'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
DATA = BASE / 'assistant-data'
ROLLOUT = Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T12-33-40-01a114a3-7bb3-7391-9e23-f3124294a101.jsonl')
MARKER = 'OWN-ROOT-PREVIEW-PARAMS-20261007-FROM-01a11405'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p

sha = lambda b: hashlib.sha256(b).hexdigest()
if sys.argv[1] == '--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    bgi = subprocess.Popen([str(PRODUCT / 'BetterGI.exe')], cwd=PRODUCT)
    exe = PRODUCT / 'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    assistant = subprocess.Popen([str(exe)], cwd=exe.parent)
    print('OWN_APPS', bgi.pid, assistant.pid, flush=True)
    exits = [assistant.wait(), bgi.wait()]
    print('NORMAL_APP_EXITS', exits, flush=True)
    sys.exit(0 if exits == [0, 0] else 1)

phase = sys.argv[1]
assert phase in ['ninth-cold-migration', 'tenth-migration-activation', 'eleventh-migration-rollback']
out = BASE / phase
with s.Session(ROOT, 'own-root-01a114a3-actual-' + phase) as budget:
    old_roots = budget.old_roots[:]
    budget.old_roots = list(dict.fromkeys(old_roots))
    assert set(budget.old_roots) == set(old_roots)
    budget.track(BASE)
    budget.track(PRODUCT / 'Tools/MultiplayerHoeingAssistant')
    budget.track(PRODUCT / 'User')
    out.mkdir(parents=True, exist_ok=False)
    def write(name, value):
        s.write(out / name, json.dumps(value, ensure_ascii=False, indent=2).encode())
    source_root = BASE / 'legacy-normal-input/User'
    if phase == 'tenth-migration-activation':
        assert not source_root.exists()
        fixtures = ROOT / 'Test/OneDragonMigration/Fixtures'
        normal_inputs = ['teabag-full.json', 'teabag-nextconfig-a.json', 'teabag-sample.json', 'public-current.json', 'legacy-namebool.json', 'teabag-daily-wins.json']
        copied = {}
        for name in normal_inputs:
            file = fixtures / name
            target = source_root / 'OneDragon' / name
            s.write(target, file.read_bytes())
            assert target.read_bytes() == file.read_bytes()
            copied[str(target)] = dict(source=str(file), sha256=sha(file.read_bytes()))
        file = fixtures / 'global-schedule.json'
        s.write(source_root / 'config.json', file.read_bytes())
        copied[str(source_root / 'config.json')] = dict(source=str(file), sha256=sha(file.read_bytes()))
        write('source-copy-map.json', dict(kind='owned normal legacy-format runtime sample; formal product UI/IPC, no rehearsal or stub', input_root=str(source_root), exact_bytes=copied, game_actions_not_executed=True))
    source_before = {f.relative_to(source_root).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(source_root)}
    write('private/legacy-source-before.json', source_before)
    identities = json.loads((BASE / 'refresh-popout/result.json').read_text(encoding='utf-8'))
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in identities['updated'])
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == identities['bgi_sha256']
    assert sha((PRODUCT / 'BetterGI.exe').read_bytes()) == identities['bgi_exe_sha256']
    sources = json.loads(Path(identities['source_hashes']).read_text(encoding='utf-8'))
    drift = [name for name, digest in sources.items() if sha((ROOT / name).read_bytes()) != digest]
    assert not drift, drift
    write('source-check.json', dict(input=identities['source_hashes'], drift=drift))
    write('monitor-root-set.json', dict(original_count=len(old_roots), distinct_count=len(budget.old_roots), same_path_set=True, policy=budget.policy))
    s.write(out / 'private/git-status.txt', subprocess.check_output(['git', 'status', '--porcelain=v1'], cwd=ROOT))
    before = {f.relative_to(PRODUCT / 'User').as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(PRODUCT / 'User')}
    write('private/product-user-before.json', before)
    for root in [PRODUCT / 'User/migration-installations', DATA / 'legacy-migration-candidates', DATA / 'workflow-migrations']:
        for file, _ in s.files_under(root):
            s.write(out / 'private/migration-before' / root.name / file.relative_to(root), file.read_bytes())
    for folder, pattern in [('flows', '*.flow.json'), ('runs', '*.run.json')]:
        for file in (DATA / folder).glob(pattern):
            s.write(out / 'private/before' / folder / file.name, file.read_bytes())
    rows = [json.loads(line) for line in ROLLOUT.read_bytes().splitlines()]
    latest = [row for row in rows if row.get('type') == 'turn_context'][-1]
    ctx = latest['payload']; settings = ctx['collaboration_mode']['settings']
    assert ctx['model'] == settings['model'] == 'gpt-6.1-sol'
    assert ctx['effort'] == settings['reasoning_effort'] == 'ultra'
    write('actual-model.json', dict(rollout=str(ROLLOUT), timestamp=latest['timestamp'], cwd=ctx['cwd'], model=ctx['model'], effort=ctx['effort'], settings_model=settings['model'], settings_effort=settings['reasoning_effort']))
    write('admission.json', dict(marker=MARKER, thread='01a114a3-7bb3-7391-9e23-f3124294a101', head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), adopted=['mistletoe-release-first-20261005-v2', 'mistletoe-complete-usable-delivery-20261006-v1', 'mistletoe-storage-limits-20261005-v1'], function='cold parameter/reference and normal migration/install/activation/rollback', product=str(PRODUCT), assistant_data_root=str(DATA), source_read_only=True, game_execution=False, user_data_moved=False, review_requests_new=0, review_budget_remaining=0, subagents='not dispatched: no remaining consultation budget; one writer and native UI stream', product_complete=False))
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command') as key:
        protocol, kind = winreg.QueryValueEx(key, '')
    write('private/protocol-before.json', dict(value=protocol, kind=kind))
    env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(DATA)
    start = datetime.datetime.now(datetime.timezone.utc).isoformat()
    print('START_OWN_COLD_PRODUCT', phase, flush=True)
    try:
        code, _, _ = p.run([sys.executable, '-B', str(Path(__file__).resolve()), '--apps'], cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='apps', timeout=7200)
    finally:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command', 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            current, _ = winreg.QueryValueEx(key, '')
            if str(PRODUCT / 'BetterGI.exe').lower() in current.lower():
                winreg.SetValueEx(key, '', 0, kind, protocol)
            current, current_kind = winreg.QueryValueEx(key, '')
            assert current == protocol and current_kind == kind
    after = {f.relative_to(PRODUCT / 'User').as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(PRODUCT / 'User')}
    write('private/product-user-after.json', after)
    source_after = {f.relative_to(source_root).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(source_root)}
    assert source_before == source_after, 'legacy input changed'
    write('private/legacy-source-after.json', source_after)
    for root in [PRODUCT / 'User/migration-installations', DATA / 'legacy-migration-candidates', DATA / 'workflow-migrations']:
        for file, _ in s.files_under(root):
            s.write(out / 'private/migration-after' / root.name / file.relative_to(root), file.read_bytes())
    for folder, pattern in [('flows', '*.flow.json'), ('runs', '*.run.json')]:
        for file in (DATA / folder).glob(pattern):
            s.write(out / 'private/after' / folder / file.name, file.read_bytes())
    raw = b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row := json.loads(line)).get('timestamp', '') >= start and row.get('type') in ['response_item', 'event_msg']) + b'\n'
    s.write(out / 'private/native-ui-source.jsonl', raw)
    write('result.json', dict(exit_code=code, assistant_data_root=str(DATA), product_user_changed=[name for name in sorted(set(before) | set(after)) if before.get(name) != after.get(name)], product_user_removed=sorted(set(before) - set(after)), protocol_restored=True, raw_ui_source_sha256=sha(raw), game_executed=False, independent_review=False, product_complete=False))
    print('OWN_COLD_APPS_TERMINAL', code, 'PROTOCOL_RESTORED', flush=True)
