"""Refresh only tested assistant modules, then repeat the real UI insert on a new owned run."""
from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys, winreg
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808'
PRODUCT = ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA = ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
CARRIER = ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
ROLLOUT = Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T04-23-04-01a11808-ac6c-7132-8e5d-391a499678cf.jsonl')
WF = 'wf-own-repair-insert-01a11808'
sys.path.insert(0, str(ROOT/'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda path: json.loads(path.read_bytes())
def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
def fingerprint(root): return {f.relative_to(root).as_posix():sha(f.read_bytes()) for f,_ in s.files_under(root)}
if sys.argv[1] == '--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    bgi = subprocess.Popen([str(PRODUCT/'BetterGI.exe')], cwd=PRODUCT)
    app = PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    assistant = subprocess.Popen([str(app)], cwd=app.parent)
    print('OWN_REPAIRED_APPS', bgi.pid, assistant.pid, flush=True)
    exits = [assistant.wait(), bgi.wait()]
    print('NORMAL_APP_EXITS', exits, flush=True)
    sys.exit(0 if exits == [0,0] else 1)
assert sys.argv[1] == 'run'
with s.Session(ROOT, 'same-product-arrival-revision-refresh-and-actual-01a11808') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant'); budget.track(PRODUCT/'VERSION.json'); budget.track(PRODUCT/'User'); budget.track(DATA)
    out = BASE/'actual'; out.mkdir(exist_ok=False)
    sources = load(BASE/'restored/source-hashes.json')
    assert all(sha((ROOT/n).read_bytes()) == h for n,h in sources.items())
    baseline = load(ROOT/'_workflow/final-ui-01a11220/path-cutoff-01a1176e/restored/result.json')
    for stage in ['green','restored']:
        result = load(BASE/stage/'result.json')
        assert result['build'] == 0 and not result['other'] and not result['source_drift']
        assert set(result['failed']) == set(baseline['failed'])
        assert len([x for x in result['passed'] if 'PathArrivalRevisionTests.' in x]) == 6
    assert load(BASE/'negative-source-restored.json')['exact']
    negative = load(BASE/'negative/result.json')
    assert negative['build'] == 0 and len(negative['failed']) == 2 and not negative['other']
    observed = subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig')
    existing = json.loads(observed) if observed.strip() else []
    if isinstance(existing, dict): existing = [existing]
    assert all(str(PRODUCT).lower() not in (x.get('ExecutablePath') or '').lower() for x in existing)
    config = load(DATA/'assistant-config.json')
    assert config['standaloneMode'] and config['serverUrl'] == '' and config['bgiPath'] == str(PRODUCT/'BetterGI.exe')
    assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
    write(out/'processes-before.json', existing)
    before = fingerprint(PRODUCT/'User'); write(out/'private/product-user-before.json', before)
    real_user = ROOT/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    real_before = fingerprint(real_user); write(out/'private/real-user-before.json', real_before)
    version = load(PRODUCT/'VERSION.json')
    s.write(out/'before/VERSION.json', (PRODUCT/'VERSION.json').read_bytes())
    assert sha((PRODUCT/'BetterGI.dll').read_bytes()) == version['bgi_dll_sha256']
    assert sha((PRODUCT/'BetterGI.exe').read_bytes()) == version['bgi_exe_sha256']
    carrier = load(BASE/'restored/products.json'); updated = []
    for row in version['assistant_modules']:
        target = PRODUCT/row['path']; original = target.read_bytes(); assert sha(original) == row['sha256']
        payload = (CARRIER/target.name).read_bytes(); assert sha(payload) == carrier[target.name]
        s.write(out/'before'/target.name, original)
        temporary = target.with_name(target.name+'.arrival-01a11808.tmp'); s.write(temporary, payload); os.replace(temporary, target)
        assert target.read_bytes() == payload
        updated.append(dict(path=row['path'], sha256=sha(payload)))
    version['last_independently_reviewed_source'] = version.pop('frozen_source', version.get('last_independently_reviewed_source'))
    version['last_independently_reviewed_snapshot_sha256'] = version.pop('snapshot_sha256', version.get('last_independently_reviewed_snapshot_sha256'))
    version.update(kind='arrival revision repair candidate; independent repair verification and whole-product runtime pending',
        source_checkpoint=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),
        compiled_input_hashes=str(BASE/'restored/source-hashes.json'), assistant_modules=updated,
        actual_scope=str(BASE/'ACTUAL.md'), product_complete=False, complete_product_goal_status='active; not achieved',
        current_repair=dict(id='PATH-ARRIVAL-REVISION-INSERT-1', severity='important', source_implemented=True, independent_verification='pending; original grant exhausted'),
        original_review_applies_to_current_candidate=False)
    version['independent_review']['applies_to_current_candidate'] = False
    version['independent_review']['subsequent_source_change'] = 'PATH-ARRIVAL-REVISION-INSERT-1'
    temporary = PRODUCT/'VERSION.json.arrival-01a11808.tmp'; write(temporary, version); os.replace(temporary, PRODUCT/'VERSION.json')
    write(out/'refresh.json', dict(updated=updated, source_hashes=version['compiled_input_hashes'], bgi_dll_sha256=version['bgi_dll_sha256'], bgi_exe_sha256=version['bgi_exe_sha256'], candidate_not_independently_reverified=True))
    for folder, pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
        for file in (DATA/folder).glob(pattern): s.write(out/'private/before'/folder/file.name, file.read_bytes())
    now = datetime.datetime.now().astimezone().replace(second=0,microsecond=0); due = now+datetime.timedelta(minutes=12)
    doc = dict(schema='mistletoe.workflow', schemaVersion=1, workflowId=WF, name='本机插入修复验证 01a11808',
        nodes=[dict(nodeId='gate',kind='control.condition',scheduleLane=0,path=dict(yes='end',no='unselected',condition=dict(kind='constant',value=True)),strategies=[dict(kind='flow.route'),dict(kind='schedule.time',mode='sequence',time=due.strftime('%H:%M'))]),
        dict(nodeId='end',kind='control.end',scheduleLane=0,strategies=[dict(kind='flow.route')]),
        dict(nodeId='unselected',kind='control.condition',scheduleLane=1,path=dict(yes='$end',no='$end',condition=dict(kind='constant',value=True)),strategies=[dict(kind='flow.route')])],
        scheduleLanes=['主车道','未选支线'],triggers=[],terminal=[],loop=None)
    target = DATA/'flows'/(WF+'.flow.json'); assert not target.exists(); write(out/'fixtures'/target.name,doc); write(target,doc)
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key: protocol,kind = winreg.QueryValueEx(key,'')
    write(out/'private/protocol-before.json',dict(value=protocol,kind=kind))
    write(out/'admission.json',dict(thread='01a11808-ac6c-7132-8e5d-391a499678cf',function='actual UI insert/save/boundary reload after six-line arrival repair',workflow_id=WF, due=due.isoformat(), product=version,
        source_modified_for_repair=True, game_executed=False, review_remaining=0, new_reviews=0, whole_product_complete=False))
    env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
    start=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z')
    print('REPAIRED_PRODUCT_READY; UI NODE DUE',due.isoformat(),flush=True)
    try:
        code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=2100)
    finally:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_SET_VALUE) as key:
            current,_=winreg.QueryValueEx(key,'')
            if current != protocol:
                assert str(PRODUCT/'BetterGI.exe').lower() in current.lower();winreg.SetValueEx(key,'',0,kind,protocol)
            assert winreg.QueryValueEx(key,'') == (protocol,kind)
    for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
        for file in (DATA/folder).glob(pattern):s.write(out/'private/after'/folder/file.name,file.read_bytes())
    raw=b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=start and row.get('type') in ['response_item','event_msg'])+b'\n'
    s.write(out/'private/native-ui-source.jsonl',raw)
    after=fingerprint(PRODUCT/'User');real_after=fingerprint(real_user)
    write(out/'private/product-user-after.json',after);write(out/'private/real-user-after.json',real_after)
    runs=[load(f) for f in (DATA/'runs').glob('*.run.json') if load(f).get('workflowId')==WF]
    write(out/'result.json',dict(exit_code=code,runs=runs,protocol_restored=True,product_user_changed=[n for n in sorted(set(before)|set(after)) if before.get(n)!=after.get(n)],
        real_user_changed=[n for n in sorted(set(real_before)|set(real_after)) if real_before.get(n)!=real_after.get(n)],source_hashes=str(BASE/'restored/source-hashes.json'),native_ui_source_sha256=sha(raw),new_reviews=0,review_remaining=0,product_complete=False))
    assert code==0 and before==after and real_before==real_after
    assert len(runs)==1
    final=load(target);added=set(n['nodeId'] for n in final['nodes'])-set(n['nodeId'] for n in doc['nodes']);assert len(added)==1
    added=added.pop();run=runs[0]
    assert final['nodes'][0]['path']['yes']==added and next(n for n in final['nodes'] if n['nodeId']==added)['path']['yes']=='end'
    assert run['state']==4 and [n['nodeId'] for n in run['nodeOutcomes']]==['gate',added,'end'] and not run['submissionHistory']
    assert run['workflowRevision']==sha(target.read_bytes())
    write(out/'actual-arrival.json',dict(workflow_id=WF,run_id=run['runId'],inserted_node_id=added,revision=run['workflowRevision'],actual_node_order=['gate',added,'end'],unselected_node_arrivals=0,resource_submissions=0,
        terminal_state='Succeeded',normal_app_exits=True,product_user_unchanged=True,real_user_unchanged=True,independent_verification_pending=True,product_complete=False))
    print('FINITE REPAIRED INSERT ACTUAL VERIFIED; independent verification and whole-product remain pending',flush=True)
