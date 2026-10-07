"""Owned same-product startup timer, automatic branch, and monitor entrance evidence."""
from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/own-runtime/startup-supplement-01a11897'
DATA = ROOT / '_workflow/final-ui-01a11220/own-runtime/assistant-data'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
ROLLOUT = Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T06-58-33-01a11897-06ae-7f83-a925-438e173e79f4.jsonl')
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as runner

sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda p: json.loads(p.read_bytes())
if sys.argv[1] == '--app':
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    exe = PRODUCT / 'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    app = subprocess.Popen([str(exe)], cwd=exe.parent)
    print('OWN_SUPPLEMENT_ASSISTANT', app.pid, flush=True)
    code = app.wait(); print('NORMAL_ASSISTANT_EXIT', code, flush=True); sys.exit(code)

assert sys.argv[1] == 'run'
with s.Session(ROOT, 'owned-startup-timer-auto-monitor-supplement-01a11897') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(DATA); budget.track(PRODUCT / 'User')
    BASE.mkdir(exist_ok=False)
    def write(p, value): s.write(p, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    def hashes(root): return {f.relative_to(root).as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(root)}
    def replace_owned(p, data):
        temporary = p.with_name(p.name + '.own-01a11897.tmp')
        s.write(temporary, data); os.replace(temporary, p); assert p.read_bytes() == data
    version = load(PRODUCT / 'VERSION.json'); inputs = load(Path(version['compiled_input_hashes']))
    assert len(inputs) == 313 and all(sha((ROOT / n).read_bytes()) == h for n, h in inputs.items())
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in version['assistant_modules'])
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == version['bgi_dll_sha256']
    assert sha((PRODUCT / 'BetterGI.exe').read_bytes()) == version['bgi_exe_sha256']
    config_path = DATA / 'assistant-config.json'; startup = DATA / 'startup-flow.json'
    original_config = config_path.read_bytes(); original_startup = startup.read_bytes(); config = load(config_path)
    assert config['standaloneMode'] and config['serverUrl'] == '' and config['bgiPath'] == str(PRODUCT / 'BetterGI.exe')
    assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi') and not config.get('autoLaunchOnBoot')
    assert load(startup) == dict(enabled=False, steps=[])
    s.write(BASE / 'private/original-assistant-config.json', original_config)
    s.write(BASE / 'private/original-startup-flow.json', original_startup)
    runs_before = hashes(DATA / 'runs'); product_before = hashes(PRODUCT / 'User')
    real_root = ROOT / 'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    real_before = hashes(real_root)
    write(BASE / 'private/runs-before.json', runs_before); write(BASE / 'private/product-user-before.json', product_before); write(BASE / 'private/real-user-before.json', real_before)
    observed = subprocess.check_output(['powershell.exe', '-NoProfile', '-Command', "Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"], text=True, encoding='utf-8-sig')
    existing = json.loads(observed) if observed.strip() else []
    if isinstance(existing, dict): existing = [existing]
    assert all(str(PRODUCT).lower() not in (x.get('ExecutablePath') or '').lower() for x in existing)
    write(BASE / 'processes-before.json', existing)
    write(BASE / 'admission.json', dict(thread='01a11897-06ae-7f83-a925-438e173e79f4', head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        function='Master plan startup center timer fire/cancel/daily rearm, automatic condition branch and monitor-only BGI skip', gap='Located finite startup actual covers manual tree only; no raw timer/automatic/monitor entrance source located',
        minimal_verification='Three own-instance phases through native WPF; finite original wait/end actions; no BGI/game launch, actual source/log/Job/data identity', next='Bind current actual limits, version and fixed-one independent repair outcome',
        source_modified=False, reviews_dispatched=1, review_remaining=0, product_complete=False, product_modules=version['assistant_modules']))
    env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(DATA)
    def phase(name):
        out = BASE / name; out.mkdir()
        start = datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00', 'Z')
        s.write(out / 'startup-before.json', startup.read_bytes())
        print('STARTUP_SUPPLEMENT_PHASE', name, flush=True)
        code, _, _ = runner.run([sys.executable, '-B', str(Path(__file__).resolve()), '--app'], cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='app', timeout=2400)
        s.write(out / 'startup-after.json', startup.read_bytes())
        raw = b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row := json.loads(line)).get('timestamp', '') >= start and row.get('type') in ['response_item', 'event_msg']) + b'\n'
        s.write(out / 'private/native-ui-source.jsonl', raw)
        write(out / 'result.json', dict(exit_code=code, raw_ui_sha256=sha(raw), source_modified=False, game_executed=False, product_complete=False))
        assert code == 0
        return load(startup)
    try:
        first = phase('edit-timer-false-branch-enable-auto')
        def tree(steps):
            for step in steps:
                yield step
                for field in ['trueSteps', 'falseSteps', 'fireSteps']: yield from tree(step.get(field, []))
        assert first['enabled'] is True and first['steps']
        assert all(x['kind'] in ['timeRange', 'wait', 'endFlow', 'timerTrigger'] for x in tree(first['steps']))
        second = phase('cold-automatic-branch-and-disable')
        assert second['enabled'] is False
        monitor = dict(config); monitor['observerMode'] = True
        replace_owned(config_path, json.dumps(monitor, ensure_ascii=False, indent=2).encode('utf-8'))
        monitor_flow = dict(enabled=False, steps=[dict(id='own-monitor-start-bgi-01a11897', kind='startBgi', enabled=True), dict(id='own-monitor-end-01a11897', kind='endFlow', enabled=True)])
        replace_owned(startup, json.dumps(monitor_flow, ensure_ascii=False, indent=2).encode('utf-8'))
        phase('monitor-skip-bgi-and-end')
    finally:
        replace_owned(startup, original_startup); replace_owned(config_path, original_config)
    product_after = hashes(PRODUCT / 'User'); real_after = hashes(real_root); runs_after = hashes(DATA / 'runs')
    write(BASE / 'private/product-user-after.json', product_after); write(BASE / 'private/real-user-after.json', real_after); write(BASE / 'private/runs-after.json', runs_after)
    assert product_after == product_before and real_after == real_before and runs_after == runs_before
    assert all(sha((ROOT / n).read_bytes()) == h for n, h in inputs.items())
    write(BASE / 'result.json', dict(normal_app_exits=[0, 0, 0], own_startup_original_restored=True, own_assistant_original_restored=True, taskcenter_runs_unchanged=True, product_user_files=len(product_before), real_user_files=len(real_before), user_changed=[], source_drift=[], source_modified=False, reviews_dispatched=1, review_remaining=0, final_acceptance_requires_raw_state_inspection=True, game_executed=False, product_complete=False))
    print('STARTUP SUPPLEMENT NORMAL TERMINAL; exact own config restored; inspect native UI/log source', flush=True)
