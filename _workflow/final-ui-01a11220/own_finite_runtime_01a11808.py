"""Finite same-BGI assistant cold restart and real UI insert evidence; no product patch."""
from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys, time, winreg

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/own-runtime/cold-insert-01a11808'
DATA = ROOT / '_workflow/final-ui-01a11220/own-runtime/assistant-data'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
ROLLOUT = Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T04-23-04-01a11808-ac6c-7132-8e5d-391a499678cf.jsonl')
COLD = 'wf-own-cold-same-bgi-01a11808'
INSERT = 'wf-own-insert-arrival-01a11808'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda path: json.loads(path.read_bytes())
def write(path, value):
    s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
def capture(folder):
    for name, pattern in [('flows', '*.flow.json'), ('runs', '*.run.json')]:
        for file in (DATA / name).glob(pattern):
            s.write(BASE / 'private' / folder / name / file.name, file.read_bytes())
def fingerprint(path):
    return {f.relative_to(path).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(path)}

if sys.argv[1] == '--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    due = datetime.datetime.fromisoformat(load(BASE / 'admission.json')['cold_original_due'])
    bgi = subprocess.Popen([str(PRODUCT / 'BetterGI.exe')], cwd=PRODUCT)
    app = PRODUCT / 'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    first = subprocess.Popen([str(app)], cwd=app.parent)
    print('FIRST_OWN_APPS', bgi.pid, first.pid, flush=True)
    first_exit = first.wait()
    capture('assistant-first-exit')
    runs = [load(f) for f in (DATA / 'runs').glob('*.run.json') if load(f).get('workflowId') == COLD]
    write(BASE / 'assistant-first-exit.json', dict(bgi_pid=bgi.pid, bgi_alive=bgi.poll() is None,
          assistant_pid=first.pid, assistant_exit=first_exit, runs=runs))
    assert first_exit == 0 and bgi.poll() is None
    assert len(runs) == 1 and runs[0]['state'] == 3 and not runs[0]['triggerConsumed']
    assert not runs[0]['nodeOutcomes'] and not runs[0]['submissionHistory']
    print('FIRST_ASSISTANT_NORMAL_EXIT; BGI_REMAINS', bgi.pid, 'RESTART_AFTER', (due + datetime.timedelta(seconds=70)).isoformat(), flush=True)
    while datetime.datetime.now().astimezone() < due + datetime.timedelta(seconds=70):
        assert bgi.poll() is None
        time.sleep(.5)
    capture('before-assistant-restart')
    second = subprocess.Popen([str(app)], cwd=app.parent)
    print('SECOND_OWN_ASSISTANT', bgi.pid, second.pid, 'SAME_BGI_ALIVE', bgi.poll() is None, flush=True)
    second_exit = second.wait()
    bgi_alive_after_second = bgi.poll() is None
    capture('assistant-second-exit')
    print('SECOND_ASSISTANT_EXIT', second_exit, 'BGI_STILL_ALIVE', bgi_alive_after_second, flush=True)
    bgi_exit = bgi.wait()
    write(BASE / 'app-lifecycle.json', dict(bgi_pid=bgi.pid, first_assistant_pid=first.pid,
          second_assistant_pid=second.pid, same_bgi_for_both_assistants=True,
          bgi_alive_after_second=bgi_alive_after_second, exits=[first_exit, second_exit, bgi_exit]))
    print('NORMAL_APP_EXITS', [first_exit, second_exit, bgi_exit], flush=True)
    sys.exit(0 if [first_exit, second_exit, bgi_exit] == [0, 0, 0] and bgi_alive_after_second else 1)

assert sys.argv[1] == 'run'
with s.Session(ROOT, 'finite-same-bgi-cold-restart-ui-insert-01a11808') as budget:
    old = budget.old_roots[:]
    budget.old_roots = list(dict.fromkeys(old))
    assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(DATA); budget.track(PRODUCT / 'User')
    BASE.mkdir(exist_ok=False)
    started = datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z')
    version = load(PRODUCT / 'VERSION.json')
    modules = version['assistant_modules'] + [dict(path='BetterGI.dll', sha256=version['bgi_dll_sha256']), dict(path='BetterGI.exe', sha256=version['bgi_exe_sha256'])]
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in modules)
    source = load(Path(version['compiled_input_hashes']))
    assert all(sha((ROOT / n).read_bytes()) == h for n, h in source.items())
    config = load(DATA / 'assistant-config.json')
    assert config['serverUrl'] == '' and config['standaloneMode'] and config['bgiPath'] == str(PRODUCT / 'BetterGI.exe')
    assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
    contexts = [json.loads(line) for line in ROLLOUT.read_bytes().splitlines() if json.loads(line).get('type') == 'turn_context']
    ctx = contexts[-1]['payload']; settings = ctx['collaboration_mode']['settings']
    assert ctx['model'] == settings['model'] == 'gpt-6.1-sol' and ctx['effort'] == settings['reasoning_effort'] == 'ultra'
    command = "Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"
    output = subprocess.check_output(['powershell.exe', '-NoProfile', '-Command', command], text=True, encoding='utf-8-sig')
    existing = json.loads(output) if output.strip() else []
    if isinstance(existing, dict): existing = [existing]
    assert all(str(PRODUCT).lower() not in (x.get('ExecutablePath') or '').lower() for x in existing)
    session = subprocess.check_output(['powershell.exe', '-NoProfile', '-Command', '(Get-Process -Id $PID).SessionId'], text=True).strip()
    assert session == '1'
    status = subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT)
    s.write(BASE / 'private/opening-status.txt', status)
    tool_observations = []
    for args in [['tools/mistletoe/review_process.py', '--root', str(ROOT), 'verify-bundle'],
                 ['tools/mistletoe/deliveries.py', '--root', str(ROOT), '--registry', str(ROOT / 'Docs/design/mistletoe-parallel-deliveries.json')]]:
        result = subprocess.run([sys.executable, '-B', *args], cwd=ROOT, capture_output=True)
        tool_observations.append(dict(argv=args, exit_code=result.returncode, stdout=result.stdout.decode('utf-8', 'replace'), stderr=result.stderr.decode('utf-8', 'replace')))
    write(BASE / 'tool-observations.json', tool_observations)
    before = fingerprint(PRODUCT / 'User'); write(BASE / 'private/product-user-before.json', before)
    real_user = ROOT / 'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    real_before = fingerprint(real_user); write(BASE / 'private/real-user-before.json', real_before)
    capture('before')
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command') as key:
        protocol, kind = winreg.QueryValueEx(key, '')
    write(BASE / 'private/protocol-before.json', dict(value=protocol, kind=kind))
    now = datetime.datetime.now().astimezone().replace(second=0, microsecond=0)
    cold_due = now + datetime.timedelta(minutes=6)
    insert_due = now + datetime.timedelta(minutes=20)
    cold = dict(schema='mistletoe.workflow', schemaVersion=1, workflowId=COLD,
        name='本机同BGI冷恢复确认 01a11808', nodes=[dict(nodeId='end', kind='control.end', path=dict(next='$end'), strategies=[])],
        triggers=[dict(kind='trigger.timeFixed', time=cold_due.strftime('%H:%M'), missPolicy='nextDay')], terminal=[], loop=None)
    gate = dict(nodeId='gate', kind='control.condition', name='等待后判断', scheduleLane=0,
        path=dict(yes='end', no='unselected', condition=dict(kind='constant', value=True)),
        strategies=[dict(kind='flow.route'), dict(kind='schedule.time', mode='sequence', time=insert_due.strftime('%H:%M'))])
    insert = dict(schema='mistletoe.workflow', schemaVersion=1, workflowId=INSERT,
        name='本机运行中插入确认 01a11808', nodes=[gate, dict(nodeId='end', kind='control.end', name='结束', scheduleLane=0, strategies=[dict(kind='flow.route')]),
        dict(nodeId='unselected', kind='control.condition', name='未选分支', scheduleLane=1, path=dict(yes='$end', no='$end', condition=dict(kind='constant', value=True)), strategies=[dict(kind='flow.route')])],
        triggers=[], terminal=[], loop=None, scheduleLanes=['主车道', '未选支线'])
    for doc in [cold, insert]:
        target = DATA / 'flows' / (doc['workflowId'] + '.flow.json')
        assert not target.exists()
        write(BASE / 'fixtures' / target.name, doc); write(target, doc)
    write(BASE / 'admission.json', dict(marker='OWN-ROOT-NONGAME-COLD-INSERT-20261008-FROM-01a1176e', thread='01a11808-ac6c-7132-8e5d-391a499678cf',
        head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), model=ctx['model'], effort=ctx['effort'], session=1,
        adopted=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1'],
        function='C07 durable fixed missed -> nextDay on same BGI epoch; UI insert/save/boundary reload/arrival; no gameplay',
        next_delivery='reconcile existing whole-product actual coverage and remaining voluntary user feedback', cold_workflow=COLD, insert_workflow=INSERT,
        cold_original_due=cold_due.isoformat(), insert_node_due=insert_due.isoformat(), product_modules=modules, compiled_input_hashes=version['compiled_input_hashes'],
        source_modified=False, additional_reviews=0, review_remaining=0, product_complete=False, storage_policy=budget.policy, monitor_root_set_preserved=True))
    env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(DATA)
    print('OWN_FINITE_READY', cold_due.isoformat(), insert_due.isoformat(), flush=True)
    try:
        code, _, _ = p.run([sys.executable, '-B', str(Path(__file__).resolve()), '--apps'], cwd=ROOT, env=env,
            directory=BASE, recovery_directory=BASE, phase='apps', timeout=3300)
    finally:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Classes\BetterGI\shell\open\command', 0, winreg.KEY_READ | winreg.KEY_SET_VALUE) as key:
            current, _ = winreg.QueryValueEx(key, '')
            if current != protocol:
                assert str(PRODUCT / 'BetterGI.exe').lower() in current.lower()
                winreg.SetValueEx(key, '', 0, kind, protocol)
            assert winreg.QueryValueEx(key, '') == (protocol, kind)
    capture('after')
    after = fingerprint(PRODUCT / 'User'); write(BASE / 'private/product-user-after.json', after)
    real_after = fingerprint(real_user); write(BASE / 'private/real-user-after.json', real_after)
    raw = b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row := json.loads(line)).get('timestamp', '') >= started and row.get('type') in ['response_item','event_msg']) + b'\n'
    s.write(BASE / 'private/native-ui-source.jsonl', raw)
    runs = [load(f) for f in (DATA / 'runs').glob('*.run.json') if load(f).get('workflowId') in [COLD, INSERT]]
    write(BASE / 'result.json', dict(exit_code=code, runs=runs, protocol_restored=True,
        product_user_changed=[n for n in sorted(set(before)|set(after)) if before.get(n) != after.get(n)],
        real_user_changed=[n for n in sorted(set(real_before)|set(real_after)) if real_before.get(n) != real_after.get(n)],
        product_user_files=len(before), real_user_files=len(real_before), native_ui_source_sha256=sha(raw),
        source_modified=False, additional_reviews=0, game_executed=False, product_complete=False))
    assert code == 0 and before == after and real_before == real_after
    print('OWN_FINITE_NORMAL_TERMINAL; inspect actual run/flow evidence before any acceptance claim', flush=True)
