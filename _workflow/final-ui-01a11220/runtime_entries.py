"""Continue normal own-product control/restart and real entries after the scoped backup fix."""
from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys, winreg

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'own-runtime'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
DATA = BASE / 'assistant-data'
ROLLOUT = Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T13-55-09-01a114ee-12be-7040-87aa-e7c88699f75c.jsonl')
MARKER = 'OWN-ROOT-MIGRATION-VIEWPORT-20261007-FROM-01a114a3'
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
assert phase in ['fifteenth-normal-entry']
out = BASE / phase
with s.Session(ROOT, 'own-root-01a114ee-actual-' + phase) as budget:
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
    source_before = {f.relative_to(source_root).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(source_root)}
    write('private/legacy-source-before.json', source_before)
    identities = json.loads((BASE / 'refresh-backup/result.json').read_text(encoding='utf-8'))
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
    write('admission.json', dict(marker=MARKER, thread='01a114ee-12be-7040-87aa-e7c88699f75c', head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), adopted=['mistletoe-release-first-20261005-v2', 'mistletoe-complete-usable-delivery-20261006-v1', 'mistletoe-storage-limits-20261005-v1'], function='normal cold recovery/explicit stop, authoring restoration and remaining usable UI/resource entries; no high-speed loop stress or added proof scope', product=str(PRODUCT), assistant_data_root=str(DATA), source_read_only=True, game_execution=False, user_data_moved=False, review_requests_new=0, review_budget_remaining=0, subagents='one read-only mechanical evidence index; no substantive review, no write authority', product_complete=False))
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command') as key:
        protocol, kind = winreg.QueryValueEx(key, '')
    write('private/protocol-before.json', dict(value=protocol, kind=kind))
    config = json.loads((DATA / 'assistant-config.json').read_text(encoding='utf-8-sig'))
    assert config['serverUrl'] == '' and config['standaloneMode'] is True
    assert config['bgiPath'] == str(PRODUCT / 'BetterGI.exe')
    assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
    command = "$ErrorActionPreference='Stop'; Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"
    process_output = subprocess.check_output(['powershell','-NoProfile','-Command',command], text=True, encoding='utf-8-sig')
    current_processes = json.loads(process_output) if process_output.strip() else []
    if isinstance(current_processes, dict): current_processes = [current_processes]
    assert all(str(PRODUCT).lower() not in (row.get('ExecutablePath') or '').lower() for row in current_processes)
    session = subprocess.check_output(['powershell','-NoProfile','-Command','(Get-Process -Id $PID).SessionId'], text=True).strip()
    assert session == '1'
    write('preflight.json', dict(session=int(session), processes=current_processes, standalone=True, assistant_data_root=str(DATA), own_product_not_running=True, bundle='known review bundle drift preserved; no new review dispatched'))
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
