"""Owned TaskCenter mode replacement, stop and cold restart runtime carrier. No Startup Center actions."""
from pathlib import Path
import sys,os,json,hashlib,datetime,subprocess,winreg
R=Path(__file__).resolve().parents[3];B=Path(__file__).resolve().parent/'runtime';D=R/'_workflow/final-ui-01a11220/own-runtime/assistant-data';P=R/'_workflow/runtime-unified-01a10e1b/product'
sys.path.insert(0,str(R/'tools/mistletoe'));import storage_limits as s;import process_runner as runner
original=s.size;s.size=lambda roots:original(list(dict.fromkeys(str(x) for x in roots)))
IDS=['wf-own-mode-dynamic-01a118e2','wf-own-mode-cold-01a118e2','wf-own-mode-stop-01a118e2']
sha=lambda b:hashlib.sha256(b).hexdigest()
def load(p):return json.loads(p.read_bytes())
def write(p,v):s.write(p,s.encode(v))
def fingerprint(p):return {f.relative_to(p).as_posix():sha(f.read_bytes()) for f,_ in s.files_under(p)}
def capture(label):
    for f in (D/'flows').glob('*.flow.json'):
        if load(f).get('workflowId') in IDS:s.write(B/'private'/label/'flows'/f.name,f.read_bytes())
    for f in (D/'runs').glob('*.run.json'):
        if load(f).get('workflowId') in IDS:s.write(B/'private'/label/'runs'/f.name,f.read_bytes())
if sys.argv[1]=='--apps':
    assert os.environ['NEXUSBGI_DATA_ROOT']==str(D)
    bgi=subprocess.Popen([str(P/'BetterGI.exe')],cwd=P)
    app=P/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    first=subprocess.Popen([str(app)],cwd=app.parent);print('FIRST',bgi.pid,first.pid,flush=True)
    one=first.wait();capture('first-exit');assert one==0 and bgi.poll() is None
    write(B/'first-exit.json',dict(bgi_pid=bgi.pid,assistant_pid=first.pid,assistant_exit=one,bgi_alive=True))
    second=subprocess.Popen([str(app)],cwd=app.parent);print('COLD_RESTART_SAME_BGI',bgi.pid,second.pid,flush=True)
    two=second.wait();capture('second-exit');bgi_alive=bgi.poll() is None;three=bgi.wait()
    write(B/'app-lifecycle.json',dict(bgi_pid=bgi.pid,first_pid=first.pid,second_pid=second.pid,exits=[one,two,three],same_bgi_alive_after_second=bgi_alive))
    print('NORMAL_APP_EXITS',[one,two,three],flush=True);sys.exit(0 if [one,two,three]==[0,0,0] and bgi_alive else 1)
assert sys.argv[1]=='run';assert not B.exists(),'inspect existing operation before retry'
with s.Session(R,'current-unified-product-taskcenter-mode-runtime-01a118e2') as session:
    session.track(B);session.track(D);session.track(P/'User');B.mkdir()
    started=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z')
    v=load(P/'VERSION.json');assert all(sha((P/m['path']).read_bytes())==m['sha256'] for m in v['assistant_modules']);assert sha((P/'BetterGI.dll').read_bytes())==v['bgi_dll_sha256'];assert sha((P/'BetterGI.exe').read_bytes())==v['bgi_exe_sha256'];assert all(sha((R/n).read_bytes())==h for n,h in load(Path(v['compiled_input_hashes'])).items())
    config=load(D/'assistant-config.json');assert config['serverUrl']=='' and config['standaloneMode'] and config['bgiPath']==str(P/'BetterGI.exe');assert not config['guardBgi'] and not config['autoLaunchWithBgi']
    facts=subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig');write(B/'processes-before.json',json.loads(facts));assert str(P).lower() not in facts.lower();assert subprocess.check_output(['powershell.exe','-NoProfile','-Command','(Get-Process -Id $PID).SessionId'],text=True).strip()=='1'
    before=fingerprint(P/'User');real=R/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User';real_before=fingerprint(real);write(B/'private/product-user-before.json',before);write(B/'private/real-user-before.json',real_before);s.write(B/'private/assistant-config-before.json',(D/'assistant-config.json').read_bytes())
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:protocol,kind=winreg.QueryValueEx(key,'')
    write(B/'private/protocol-before.json',dict(value=protocol,kind=kind))
    now=datetime.datetime.now(datetime.timezone.utc).astimezone(datetime.timezone(datetime.timedelta(hours=8))).replace(second=0,microsecond=0);due=now+datetime.timedelta(minutes=9)
    for index,id in enumerate(IDS):
        time=(due+datetime.timedelta(minutes=index*20)).strftime('%H:%M')
        gate=dict(nodeId='gate',kind='control.condition',name='等待后判断',scheduleLane=0,path=dict(yes='end',no='$end',condition=dict(kind='constant',value=True)),strategies=[dict(kind='flow.route'),dict(kind='schedule.time',mode='sequence',time=time)])
        doc=dict(schema='mistletoe.workflow',schemaVersion=1,workflowId=id,name=['任务中心 模式切换 01a118e2','任务中心 冷恢复模式 01a118e2','任务中心 停止保留 01a118e2'][index],nodes=[gate,dict(nodeId='end',kind='control.end',name='结束',scheduleLane=0,strategies=[dict(kind='flow.route')])],triggers=[],terminal=[],loop=None,scheduleLanes=['主车道'])
        target=D/'flows'/(id+'.flow.json');assert not target.exists();write(B/'fixtures'/target.name,doc);write(target,doc)
    write(B/'admission.json',dict(thread='01a118e2-6f11-7b10-8135-8f4a8a3b2a6d',head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=R,text=True).strip(),authorized='user explicitly restored only TaskCenter input; Startup Center excluded',source_inputs=v['compiled_input_hashes'],modules=v['assistant_modules'],dynamic_due=due.isoformat(),workflows=IDS,new_review_requests=0,game_executed=False,product_complete=False,storage_reservation=session.id))
    env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(D);print('TASKCENTER_READY',due.isoformat(),flush=True)
    try:code,_,_=runner.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=R,env=env,directory=B,recovery_directory=B,phase='apps',timeout=3300)
    finally:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_SET_VALUE) as key:
            current,_=winreg.QueryValueEx(key,'')
            if current!=protocol:assert str(P/'BetterGI.exe').lower() in current.lower();winreg.SetValueEx(key,'',0,kind,protocol)
            assert winreg.QueryValueEx(key,'')==(protocol,kind)
    capture('after');after=fingerprint(P/'User');real_after=fingerprint(real);write(B/'private/product-user-after.json',after);write(B/'private/real-user-after.json',real_after)
    rollout=next(Path('E:/CodexData/home/sessions/2026/10/08').glob('*01a118e2-6f11-7b10-8135-8f4a8a3b2a6d.jsonl'));raw=b'\n'.join(line for line in rollout.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=started and row.get('type') in ['response_item','event_msg'])+b'\n';s.write(B/'private/native-ui-source.jsonl',raw)
    runs=[load(f) for f in (D/'runs').glob('*.run.json') if load(f).get('workflowId') in IDS];write(B/'result.json',dict(exit_code=code,runs=runs,product_user_changed=[n for n in set(before)|set(after) if before.get(n)!=after.get(n)],real_user_changed=[n for n in set(real_before)|set(real_after) if real_before.get(n)!=real_after.get(n)],native_ui_source_sha256=sha(raw),protocol_restored=True,source_modified=False,new_review_requests=0,game_executed=False,product_complete=False));assert code==0 and before==after and real_before==real_after;print('TASKCENTER_RUNTIME_TERMINAL',flush=True)
