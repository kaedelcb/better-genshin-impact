"""Finite actual cold fixed-nextDay and edit/insert acceptance; no product writes."""
from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,winreg
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'own-runtime/wait-edit-20261008-01a1176e'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA=ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T01-34-33-01a1176e-6732-7bd2-9b74-38989fbe5fbe.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda path:json.loads(path.read_bytes())
if sys.argv[1]=='--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT')==str(DATA)
    bgi=subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
    appdir=PRODUCT/'Tools/MultiplayerHoeingAssistant'
    assistant=subprocess.Popen([str(appdir/'MultiplayerHoeingAssistant.exe')],cwd=appdir)
    print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
    exits=[assistant.wait(),bgi.wait()];print('NORMAL_APP_EXITS',exits,flush=True)
    sys.exit(0 if exits==[0,0] else 1)
assert sys.argv[1] in ['run','cold']
recover=sys.argv[1]=='cold'
with s.Session(ROOT,'finite-owned-cold-fixed-nextday-and-insert-01a1176e') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(BASE);budget.track(DATA);budget.track(PRODUCT/'User')
    BASE.mkdir(exist_ok=recover)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    def user():return {f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')}
    version=load(PRODUCT/'VERSION.json');assert all(sha((PRODUCT/r['path']).read_bytes())==r['sha256'] for r in version['assistant_modules'])
    source=load(Path(version['compiled_input_hashes']));assert all(sha((ROOT/n).read_bytes())==h for n,h in source.items())
    config=load(DATA/'assistant-config.json');assert config['serverUrl']=='' and config['standaloneMode'] and config['bgiPath']==str(PRODUCT/'BetterGI.exe') and not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
    process_cmd="Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"
    observed=subprocess.check_output(['powershell.exe','-NoProfile','-Command',process_cmd],text=True,encoding='utf-8-sig')
    existing=json.loads(observed) if observed.strip() else []
    if isinstance(existing,dict):existing=[existing]
    assert all(str(PRODUCT).lower() not in (x.get('ExecutablePath') or '').lower() for x in existing)
    if not recover:write(BASE/'processes-before.json',existing)
    before=user()
    if recover:
        assert before==load(BASE/'private/product-user-before.json')
        saved_protocol=load(BASE/'private/protocol-before.json');protocol=saved_protocol['value'];kind=saved_protocol['kind']
    else:
        write(BASE/'private/product-user-before.json',before)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:protocol,kind=winreg.QueryValueEx(key,'')
        write(BASE/'private/protocol-before.json',dict(value=protocol,kind=kind))
    now=datetime.datetime.now().astimezone().replace(second=0,microsecond=0)
    due=now+datetime.timedelta(minutes=6)
    wf='wf-own-zcold-fixed-01a1176e'
    doc=dict(schema='mistletoe.workflow',schemaVersion=1,workflowId=wf,name='本机固定冷恢复确认 01a1176e',nodes=[dict(nodeId='end',kind='control.end',path=dict(next='$end'),strategies=[])],triggers=[dict(kind='trigger.timeFixed',time=due.strftime('%H:%M'),missPolicy='nextDay')],terminal=[],loop=None)
    target=DATA/'flows'/(wf+'.flow.json')
    if recover:
        admission=load(BASE/'admission.json');due=datetime.datetime.fromisoformat(admission['original_due']);doc=load(BASE/'fixtures'/target.name)
        assert load(target)==doc
    else:
        assert not target.exists()
        payload=json.dumps(doc,ensure_ascii=False,indent=2).encode('utf-8');s.write(BASE/'fixtures'/target.name,payload);s.write(target,payload)
        write(BASE/'admission.json',dict(scope='C07 durable fixed-time missed during normal cold restart -> nextDay original policy -> UI stop; optional finite insert/reload local controls, no gameplay or pressure',source_head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),source_sha256=dict(source),product_modules=version['assistant_modules'],author_time=now.isoformat(),original_due=due.isoformat(),workflow_id=wf,policies=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1'],source_modified=False,additional_reviews=0,review_remaining=0,game_executed=False,product_complete=False))
    env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
    def apps(phase):
        out=BASE/phase;out.mkdir();start=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z')
        for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
            for file in (DATA/folder).glob(pattern):s.write(out/'private/before'/folder/file.name,file.read_bytes())
        print('STARTING',phase,'original fixed due',due.isoformat(),flush=True)
        code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=2400)
        for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
            for file in (DATA/folder).glob(pattern):s.write(out/'private/after'/folder/file.name,file.read_bytes())
        raw=b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=start and row.get('type') in ['response_item','event_msg'])+b'\n'
        s.write(out/'private/native-ui-source.jsonl',raw);write(out/'result.json',dict(exit_code=code,raw_ui_source_sha256=sha(raw),source_modified=False,game_executed=False,product_complete=False))
        assert code==0
        return [load(file) for file in (DATA/'runs').glob('*.run.json') if load(file).get('workflowId')==wf]
    first=([load(file) for file in (BASE/'first-start-and-pause/private/after/runs').glob('*.run.json') if load(file).get('workflowId')==wf] if recover else apps('first-start-and-pause'));assert len(first)==1
    run=first[0];assert run['state']==3 and not run['triggerConsumed'] and not run['nodeOutcomes'] and not run['submissionHistory']
    assert run['triggerTiming']['Kind']=='trigger.timeFixed' and run['triggerTiming']['MissPolicy']=='nextDay'
    write(BASE/'paused-run.json',run)
    print('FIRST COMPLETE; original run',run['runId'],'paused; resume only after',due+datetime.timedelta(minutes=1),flush=True)
    if not recover:
        command=input('COLD> ').strip();assert command=='cold'
    else:
        write(BASE/'first-controller-parser-failure.json',dict(original_exit=1,error="KeyError: 'kind'",layer='task-specific capture assertion used camel-case instead of actual PascalCase WorkflowTriggerTiming; product state already Paused and both apps exited normally',original_phase='first-start-and-pause',same_original_run=True,source_modified=False,first_actual_not_rerun=True))
    assert datetime.datetime.now().astimezone()>=due+datetime.timedelta(minutes=1),'original fixed minute not expired; do not relabel'
    second=apps('cold-resume-nextday-stop');assert len(second)==1
    final=second[0];assert final['runId']==run['runId'] and final['state']==6 and final['stopRequested'] and not final['triggerConsumed'] and not final['nodeOutcomes'] and not final['submissionHistory']
    assert final['triggerTiming']['OriginalScheduledAt']==run['triggerTiming']['OriginalScheduledAt']
    assert datetime.datetime.fromisoformat(final['triggerTiming']['ScheduledAt'])==datetime.datetime.fromisoformat(run['triggerTiming']['ScheduledAt'])+datetime.timedelta(days=1)
    write(BASE/'cancelled-nextday-run.json',final)
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_SET_VALUE) as key:
        current,_=winreg.QueryValueEx(key,'')
        if current!=protocol:assert str(PRODUCT/'BetterGI.exe').lower() in current.lower();winreg.SetValueEx(key,'',0,kind,protocol)
    after=user();assert before==after;write(BASE/'private/product-user-after.json',after)
    current=load(target);assert current==doc;current.update(triggers=[],terminal=[],loop=None)
    temp=target.with_name(target.name+'.withdraw-own.tmp');s.write(temp,json.dumps(current,ensure_ascii=False,indent=2).encode());os.replace(temp,target)
    write(BASE/'result.json',dict(source_modified=False,workflow_id=wf,run_id=run['runId'],same_durable_run=True,original_policy='nextDay',actual_nextday_wait=True,original_scheduled_at=run['triggerTiming']['OriginalScheduledAt'],next_day_at=final['triggerTiming']['ScheduledAt'],cancelled=True,stop_requested=True,node_outcomes=[],submission_history=[],actual_nextday_execution_observed=False,normal_app_exits=True,product_user_files=len(before),product_user_changed=[],protocol_restored=True,temporary_trigger_withdrawn=True,additional_reviews=0,game_executed=False,product_complete=False))
    print('finite cold nextDay actual acceptance complete; no game/effect or whole-product claim',flush=True)
