"""Finite native Startup Center UI sample; no gameplay, resource substitute or source patch."""
from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'_workflow/final-ui-01a11220/own-runtime/startup-center-01a11808'
DATA=ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T04-23-04-01a11808-ac6c-7132-8e5d-391a499678cf.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda path:json.loads(path.read_bytes())
if sys.argv[1]=='--app':
    assert os.environ.get('NEXUSBGI_DATA_ROOT')==str(DATA)
    exe=PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    app=subprocess.Popen([str(exe)],cwd=exe.parent)
    print('OWN_STARTUP_ASSISTANT',app.pid,flush=True)
    code=app.wait();print('NORMAL_ASSISTANT_EXIT',code,flush=True);sys.exit(code)
assert sys.argv[1]=='run'
with s.Session(ROOT,'finite-startup-center-tree-cancel-persistence-01a11808') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(BASE);budget.track(DATA);budget.track(PRODUCT/'User')
    BASE.mkdir(exist_ok=False)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    def hashes(root):return {f.relative_to(root).as_posix():sha(f.read_bytes()) for f,_ in s.files_under(root)}
    version=load(PRODUCT/'VERSION.json');assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in version['assistant_modules'])
    inputs=load(Path(version['compiled_input_hashes']));assert all(sha((ROOT/n).read_bytes())==h for n,h in inputs.items())
    before_user=hashes(PRODUCT/'User');write(BASE/'private/product-user-before.json',before_user)
    real_root=ROOT/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    real_before=hashes(real_root);write(BASE/'private/real-user-before.json',real_before)
    config=load(DATA/'assistant-config.json');assert config['standaloneMode'] and config['serverUrl']=='' and config['bgiPath']==str(PRODUCT/'BetterGI.exe')
    assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
    startup=DATA/'startup-flow.json';original=startup.read_bytes();assert load(startup)==dict(enabled=False,steps=[])
    s.write(BASE/'private/startup-original.json',original)
    run_before=hashes(DATA/'runs');write(BASE/'private/runs-before.json',run_before)
    observed=subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig')
    existing=json.loads(observed) if observed.strip() else []
    if isinstance(existing,dict):existing=[existing]
    assert all(str(PRODUCT).lower() not in (x.get('ExecutablePath') or '').lower() for x in existing)
    write(BASE/'processes-before.json',existing)
    write(BASE/'admission.json',dict(thread='01a11808-ac6c-7132-8e5d-391a499678cf',head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),
        function='master plan startup center native condition/tree/wait/end, manual cancellation, automatic parameter persistence and cold reload, diagram entry',
        gap='full-product MATRIX only states existing UI; no located native runtime source for this entrance',
        allowed_kinds=['timeRange','wait','endFlow'],automatic_startup_disabled=True,no_bgi_or_game_launch=True,
        not_taskcenter_resource_skip=True,source_modified=False,new_reviews=0,review_remaining=0,product_modules=version['assistant_modules'],source_hashes=version['compiled_input_hashes'],
        next='same owner checkpoint for one pending fixed repair verification; voluntary game feedback only',product_complete=False))
    env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
    def phase(name):
        out=BASE/name;out.mkdir()
        start=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z')
        s.write(out/'startup-before.json',startup.read_bytes())
        print('STARTUP_PHASE',name,flush=True)
        code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--app'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='app',timeout=2400)
        s.write(out/'startup-after.json',startup.read_bytes())
        raw=b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=start and row.get('type') in ['response_item','event_msg'])+b'\n'
        s.write(out/'private/native-ui-source.jsonl',raw)
        write(out/'result.json',dict(exit_code=code,raw_ui_sha256=sha(raw),source_modified=False,game_executed=False,not_taskcenter_resource_skip=True,product_complete=False))
        assert code==0
        return load(startup)
    try:
        first=phase('first-tree-run-cancel-save')
        assert first['enabled'] is False and len(first['steps'])>0
        def tree(steps):
            for step in steps:
                yield step
                for field in ['trueSteps','falseSteps','fireSteps']:yield from tree(step.get(field,[]))
        steps=list(tree(first['steps']));assert all(x['kind'] in ['timeRange','wait','endFlow'] for x in steps)
        assert any(x['kind']=='timeRange' for x in steps) and any(x['kind']=='wait' for x in steps) and any(x['kind']=='endFlow' for x in steps)
        second=phase('cold-reload-tree')
        assert second==first
    finally:
        temporary=startup.with_name(startup.name+'.restore-own-01a11808.tmp');s.write(temporary,original);os.replace(temporary,startup)
        assert startup.read_bytes()==original
    after_user=hashes(PRODUCT/'User');real_after=hashes(real_root)
    write(BASE/'private/product-user-after.json',after_user);write(BASE/'private/real-user-after.json',real_after)
    assert after_user==before_user and real_after==real_before
    assert hashes(DATA/'runs')==run_before
    assert all(sha((ROOT/n).read_bytes())==h for n,h in inputs.items())
    write(BASE/'result.json',dict(normal_app_exits=[0,0],startup_config_cold_preserved=True,original_startup_config_restored=True,taskcenter_run_files_unchanged=True,
        product_user_files=len(before_user),real_user_files=len(real_before),user_changed=[],source_drift=[],source_modified=False,new_reviews=0,review_remaining=0,
        final_ui_acceptance_requires_raw_state_inspection=True,not_taskcenter_resource_skip=True,game_executed=False,product_complete=False))
    print('STARTUP ACTUAL NORMAL TERMINAL; original config/data/source retained; inspect raw UI before acceptance claim',flush=True)
