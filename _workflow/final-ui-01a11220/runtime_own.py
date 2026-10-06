"""Run the complete own product with a dedicated assistant data root; no user-directory moves."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, winreg
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'own-runtime'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
CARRIER = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
DATA = BASE / 'assistant-data'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

if sys.argv[1] == '--apps':
    # Both are owned descendants of process_runner's Job. UI actions and normal
    # exits are performed through Computer Use, never through this launcher.
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    bgi = subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
    assistant = subprocess.Popen([str(PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe')],
        cwd=PRODUCT/'Tools/MultiplayerHoeingAssistant')
    print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
    assistant_exit = assistant.wait()
    bgi_exit = bgi.wait()
    print('NORMAL_APP_EXITS',assistant_exit,bgi_exit,flush=True)
    sys.exit(0 if assistant_exit==bgi_exit==0 else 1)

phase = sys.argv[1]
out = BASE/phase
assert not out.exists(), 'Existing runtime evidence refused'
with s.Session(ROOT,'final-ui-01a11220-own-runtime-'+phase) as budget:
    budget.track(BASE);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User')
    out.mkdir(parents=True)
    def write(path,value): s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    user_before={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
    write(out/'private/product-user-before.json',user_before)
    if phase == 'refresh':
        green=ROOT/'_workflow/final-ui-01a11220/isolated-data/green3'
        result=json.loads((green/'result.json').read_text(encoding='utf-8'))
        assert result['exit_code']==0 and result['source_drift']==[]
        for name in ['build','test']:
            assert json.loads((green/(name+'-tree-terminal.json')).read_text(encoding='utf-8'))['active_processes']==0
        hashes=json.loads((green/'source-hashes.json').read_text(encoding='utf-8'))
        assert all(sha(ROOT/rel)==value for rel,value in hashes.items())
        old=json.loads((ROOT/'_workflow/runtime-accept-01a10f9b/runtime-refresh-polish/result.json').read_text(encoding='utf-8'))
        assert sha(PRODUCT/'BetterGI.dll')==old['bgi_sha']
        source_rows=json.loads((ROOT/'_workflow/full-product-01a10e1b/source-manifest.json').read_text(encoding='utf-8-sig'))
        bgi_inputs=[row for row in source_rows if row['path'].split('/')[0] in
            ['BetterGenshinImpact','Fischless.WindowsInput','Fischless.HotkeyCapture','Fischless.GameCapture']]
        assert all(sha(ROOT/row['path'])==row['sha256'] for row in bgi_inputs)
        for row in old['updated']:
            assert sha(PRODUCT/row['path'])==row['sha256']
        updated=[]
        for name in ['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb',
                     'MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json']:
            target=PRODUCT/'Tools/MultiplayerHoeingAssistant'/name
            s.write(out/'before'/name,target.read_bytes())
            temp=target.with_name(name+'.isolated-01a11220')
            s.write(temp,(CARRIER/name).read_bytes());os.replace(temp,target)
            assert sha(target)==sha(CARRIER/name)
            updated.append(dict(path=target.relative_to(PRODUCT).as_posix(),sha256=sha(target)))
        assert user_before=={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
        write(out/'result.json',dict(updated=updated,bgi_sha256=sha(PRODUCT/'BetterGI.dll'),
            bgi_inputs_unchanged=len(bgi_inputs),product_user_unchanged=True,
            product_user_files=len(user_before),apps_launched=False,acceptance=False))
        print('Own product modules refreshed; User untouched',flush=True)
    else:
        if phase == 'first':
            assert not DATA.exists()
            DATA.mkdir()
            write(DATA/'assistant-config.json',dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,
                bgiPath=str(PRODUCT/'BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,
                autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime=''))
            s.write(DATA/'startup-flow.json',b'{"enabled":false,"steps":[]}')
        else:
            assert DATA.is_dir()
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:
            protocol,kind=winreg.QueryValueEx(key,'')
        write(out/'private/protocol-before.json',dict(value=protocol,kind=kind))
        env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
        print('Starting owned software; dedicated data root',DATA,flush=True)
        code,_,_=process_runner.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],
            cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=3600)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE) as key:
            current,_=winreg.QueryValueEx(key,'')
            if str(PRODUCT/'BetterGI.exe').lower() in current.lower():
                winreg.SetValueEx(key,'',0,kind,protocol)
            current,current_kind=winreg.QueryValueEx(key,'')
            assert current==protocol and current_kind==kind
        user_after={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
        write(out/'result.json',dict(exit_code=code,assistant_data_root=str(DATA),
            product_user_changed=[p for p in set(user_before)|set(user_after) if user_before.get(p)!=user_after.get(p)],
            protocol_restored=True,game_executed=False,acceptance=False))
        print('Owned apps terminal',code,'protocol restored',flush=True)
