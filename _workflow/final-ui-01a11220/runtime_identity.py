"""Actual own-product identity/resource acceptance and assistant restart, without real-user moves."""
from pathlib import Path
import datetime,hashlib,json,os,subprocess,sys,winreg
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'own-runtime'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product';DATA=BASE/'assistant-data'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T09-40-46-01a11405-2f49-7ae2-b0a7-73e423bd691c.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha=lambda b:hashlib.sha256(b).hexdigest()
if sys.argv[1]=='--apps':
    assert os.environ.get('NEXUSBGI_DATA_ROOT')==str(DATA)
    bgi=subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
    assistant_path=PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe'
    assistant=subprocess.Popen([str(assistant_path)],cwd=assistant_path.parent)
    print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
    first=assistant.wait();print('FIRST_ASSISTANT_EXIT',first,flush=True)
    if first==0:
        assistant=subprocess.Popen([str(assistant_path)],cwd=assistant_path.parent)
        print('OWN_ASSISTANT_RESTART',assistant.pid,flush=True)
        second=assistant.wait()
    else:second=None
    bgi_exit=bgi.wait();print('NORMAL_APP_EXITS',first,second,bgi_exit,flush=True)
    sys.exit(0 if first==second==bgi_exit==0 else 1)
phase=sys.argv[1];assert phase=='sixth-identity';out=BASE/phase
with s.Session(ROOT,'own-root-01a11405-actual-identity-resource-restart') as budget:
    budget.track(BASE);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User')
    out.mkdir(parents=True,exist_ok=False)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    identities=json.loads((BASE/'refresh-identity/result.json').read_text(encoding='utf-8'))
    assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in identities['updated'])
    assert sha((PRODUCT/'BetterGI.dll').read_bytes())==identities['bgi_sha256'] and sha((PRODUCT/'BetterGI.exe').read_bytes())==identities['bgi_exe_sha256']
    before={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};write(out/'private/product-user-before.json',before)
    # Preserve the small authoring files before any UI parameter change; never copy the User tree.
    for file in (PRODUCT/'User/OneDragon').glob('*.json'):s.write(out/'private/parameter-before'/file.name,file.read_bytes())
    for name in ['config.json']:
        file=PRODUCT/'User'/name
        if file.is_file():s.write(out/'private/user-root-before'/name,file.read_bytes())
    for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
        for file in (DATA/folder).glob(pattern):s.write(out/'private/before'/folder/file.name,file.read_bytes())
    write(out/'admission.json',dict(marker='OWN-ROOT-IDENTITY-UI-20261007-FROM-01a113cc',assistant_data_root=str(DATA),source_rollout=str(ROLLOUT),model='gpt-6.1-sol',effort='xhigh',game_execution=False,user_data_moved=False,review_requests_new=0,product_acceptance=False))
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:protocol,kind=winreg.QueryValueEx(key,'')
    write(out/'private/protocol-before.json',dict(value=protocol,kind=kind));env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
    start=datetime.datetime.now(datetime.timezone.utc).isoformat();print('Starting own complete product and one normal assistant restart',flush=True)
    code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=3600)
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE) as key:
        current,_=winreg.QueryValueEx(key,'')
        if str(PRODUCT/'BetterGI.exe').lower() in current.lower():winreg.SetValueEx(key,'',0,kind,protocol)
        current,current_kind=winreg.QueryValueEx(key,'');assert current==protocol and current_kind==kind
    after={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};write(out/'private/product-user-after.json',after)
    for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
        for file in (DATA/folder).glob(pattern):s.write(out/'private/after'/folder/file.name,file.read_bytes())
    raw=b'\n'.join(line for line in ROLLOUT.read_bytes().splitlines() if (row:=json.loads(line)).get('timestamp','')>=start and row.get('type') in ['response_item','event_msg'])+b'\n'
    s.write(out/'private/native-ui-source.jsonl',raw)
    write(out/'result.json',dict(exit_code=code,assistant_data_root=str(DATA),product_user_changed=[n for n in sorted(set(before)|set(after)) if before.get(n)!=after.get(n)],product_user_removed=sorted(set(before)-set(after)),protocol_restored=True,raw_ui_source_sha256=sha(raw),game_executed=False,independent_review=False,product_complete=False))
    print('Own apps terminal',code,'protocol restored',flush=True)
