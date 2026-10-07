from pathlib import Path
import datetime, hashlib, json, os, subprocess, sys, winreg
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'path-cutoff-01a1176e'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA=ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
CARRIER=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T01-34-33-01a1176e-6732-7bd2-9b74-38989fbe5fbe.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
def sha(b):return hashlib.sha256(b).hexdigest()
def load(path):return json.loads(path.read_bytes())
if sys.argv[1]=='--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT')==str(DATA)
    bgi=subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
    assistant_dir=PRODUCT/'Tools/MultiplayerHoeingAssistant'
    assistant=subprocess.Popen([str(assistant_dir/'MultiplayerHoeingAssistant.exe')],cwd=assistant_dir)
    print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
    exits=[assistant.wait(),bgi.wait()]
    print('NORMAL_APP_EXITS',exits,flush=True)
    sys.exit(0 if exits==[0,0] else 1)
phase=sys.argv[1]
assert phase in ['refresh','actual']
with s.Session(ROOT,'path-cutoff-01a1176e-'+phase) as budget:
    old_roots=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old_roots));assert set(old_roots)==set(budget.old_roots)
    out=BASE/('product-'+phase);out.mkdir(exist_ok=False);budget.track(BASE)
    budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User')
    def write(name,value):s.write(out/name,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    def user_hashes():return {f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')}
    before=user_hashes();write('private/product-user-before.json',before)
    sources=load(BASE/'restored/source-hashes.json')
    assert all(sha((ROOT/n).read_bytes())==h for n,h in sources.items())
    write('source-check.json',dict(source_hashes=str(BASE/'restored/source-hashes.json'),drift=[],head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()))
    if phase=='refresh':
        result=load(BASE/'restored/result.json')
        assert result['build']==0 and not result['other'] and not result['source_drift']
        assert len(result['failed'])==2 and all('LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping' in f for f in result['failed'])
        for stage in ['green','negative-entry','restored-entry','negative-cutoff','restored']:
            r=load(BASE/stage/'result.json');assert r['build']==0 and not r['other'] and not r['source_drift']
            for action in ['build','test']:assert load(BASE/stage/(action+'-tree-terminal.json'))['active_processes']==0
        for stage in ['negative-entry','negative-cutoff']:assert all(v['exact'] for v in load(BASE/(stage+'-source-restored.json')).values())
        previous=load(ROOT/'_workflow/final-ui-01a11220/own-runtime/refresh-cold-resume/result.json')
        assert sha((PRODUCT/'BetterGI.dll').read_bytes())==previous['bgi_sha256']
        assert sha((PRODUCT/'BetterGI.exe').read_bytes())==previous['bgi_exe_sha256']
        carrier_hashes=load(BASE/'restored/products.json');updated=[]
        for row in previous['updated']:
            target=PRODUCT/row['path'];data=(CARRIER/target.name).read_bytes()
            assert sha(target.read_bytes())==row['sha256']
            assert sha(data)==carrier_hashes[target.name]
            s.write(out/'before'/target.name,target.read_bytes())
            temp=target.with_name(target.name+'.path-cutoff-01a1176e.tmp')
            s.write(temp,data);os.replace(temp,target)
            assert target.read_bytes()==data
            updated.append(dict(path=row['path'],sha256=sha(data)))
        assert before==user_hashes()
        write('result.json',dict(updated=updated,bgi_sha256=previous['bgi_sha256'],bgi_exe_sha256=previous['bgi_exe_sha256'],source_hashes=str(BASE/'restored/source-hashes.json'),user_files=len(before),product_user_changed=[],actual_ui=False,product_complete=False))
        print('SAME PRODUCT REFRESHED; User unchanged; no UI acceptance yet',flush=True)
    else:
        current=load(BASE/'product-refresh/result.json')
        assert all(sha((PRODUCT/row['path']).read_bytes())==row['sha256'] for row in current['updated'])
        assert sha((PRODUCT/'BetterGI.dll').read_bytes())==current['bgi_sha256']
        assert sha((PRODUCT/'BetterGI.exe').read_bytes())==current['bgi_exe_sha256']
        config=load(DATA/'assistant-config.json')
        assert config['serverUrl']=='' and config['standaloneMode'] is True and config['bgiPath']==str(PRODUCT/'BetterGI.exe')
        assert not config.get('guardBgi') and not config.get('autoLaunchWithBgi')
        observed=subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','YuanShen.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig')
        processes=json.loads(observed) if observed.strip() else []
        if isinstance(processes,dict):processes=[processes]
        assert all(str(PRODUCT).lower() not in (r.get('ExecutablePath') or '').lower() for r in processes)
        write('private/processes-before.json',processes)
        for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
            for file in (DATA/folder).glob(pattern):s.write(out/'private/before'/folder/file.name,file.read_bytes())
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:protocol,kind=winreg.QueryValueEx(key,'')
        write('private/protocol-before.json',dict(value=protocol,kind=kind))
        write('admission.json',dict(marker='OWN-ROOT-REVIEW1-PATH-CUTOFF-FIX-20261008-FROM-01a114ee',thread='01a1176e-6732-7bd2-9b74-38989fbe5fbe',data_root=str(DATA),product=str(PRODUCT),scope='finite actual timed cutoff/primary path boundary with current same-product modules; prior unchanged UI/migration evidence reused',game_execution=False,real_user_moved=False,product_complete=False))
        env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
        start=datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z')
        print('STARTING UPDATED OWN PRODUCT',flush=True)
        code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=1800)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_SET_VALUE) as key:
            current_protocol,current_kind=winreg.QueryValueEx(key,'')
            if current_protocol!=protocol:
                assert str(PRODUCT/'BetterGI.exe').lower() in current_protocol.lower(),'unexpected protocol writer; original preserved'
                winreg.SetValueEx(key,'',0,kind,protocol)
        after=user_hashes();write('private/product-user-after.json',after)
        for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
            for file in (DATA/folder).glob(pattern):s.write(out/'private/after'/folder/file.name,file.read_bytes())
        raw=b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=start and row.get('type') in ['response_item','event_msg'])+b'\n'
        s.write(out/'private/native-ui-source.jsonl',raw)
        write('result.json',dict(exit_code=code,product_user_changed=[n for n in sorted(set(before)|set(after)) if before.get(n)!=after.get(n)],product_user_removed=sorted(set(before)-set(after)),data_root=str(DATA),protocol_restored=True,raw_ui_source_sha256=sha(raw),game_execution=False,product_complete=False))
        print('OWN PRODUCT EXITED',code,'User changed',len([n for n in set(before)|set(after) if before.get(n)!=after.get(n)]),flush=True)
