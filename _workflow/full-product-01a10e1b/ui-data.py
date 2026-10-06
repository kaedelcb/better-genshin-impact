from pathlib import Path
import sys,os,json,hashlib,winreg,datetime
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;runtime=root/'_workflow/runtime-unified-01a10e1b/product'
logical=Path(os.environ['APPDATA'])/'NexusBGI';live=logical.resolve()
allowed=Path(os.environ['LOCALAPPDATA'])/'Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming'
assert live.parent in [Path(os.environ['APPDATA']).resolve(),allowed.resolve()]
saved=live.parent/'NexusBGI-preserved-01a10e1b';archive=live.parent/'NexusBGI-tested-01a10e1b'
if saved.exists():
    saved=saved.resolve();live=saved.parent/'NexusBGI';archive=saved.parent/'NexusBGI-tested-01a10e1b'
    assert saved.parent==allowed.resolve()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def inventory(p):return {f.relative_to(p).as_posix():sha(f) for f,st in s.files_under(p)}
def write(p,v):s.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
with s.Session(root,'full-product-ui-data-'+sys.argv[1]) as budget:
    budget.track(base)
    if sys.argv[1] in ('prepare','prepare-resume'):
        if sys.argv[1]=='prepare':
            assert live.is_dir() and not saved.exists() and not archive.exists()
            before=inventory(live);write(base/'private/ui-recovery.json',dict(logical=str(logical),physical=str(live),preserved=str(saved),archive=str(archive),before=before))
            os.rename(live,saved);assert inventory(saved)==before
        else:
            before=json.loads((base/'private/ui-recovery.json').read_text(encoding='utf-8-sig'))['before']
            assert saved.is_dir() and not live.exists() and not archive.exists() and inventory(saved)==before
            write(base/'private/ui-physical-correction.json',dict(physical=str(live),preserved=str(saved),archive=str(archive),original_sha_same=True))
        live.mkdir();budget.track(live)
        write(live/'assistant-config.json',dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,bgiPath=str(runtime/'BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime=''))
        s.write(live/'startup-flow.json',b'{"enabled":false,"steps":[]}')
        doc=json.loads((root/'_workflow/closeout-01a10cef/ui-r3/public-proof/wf-edc5b117.flow.json').read_text(encoding='utf-8-sig'))
        doc['workflowId']='wf-full-ui-01a10e1b';doc['name']='整版-界面-01a10e1b'
        future=(datetime.datetime.now()+datetime.timedelta(hours=4)).strftime('%H:%M')
        doc['triggers']=[dict(kind='trigger.time',params=dict(time=future,missPolicy='nextDay'))]
        write(live/'flows/wf-full-ui-01a10e1b.flow.json',doc)
        key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command');value,kind=winreg.QueryValueEx(key,'')
        write(base/'private/protocol-before.json',dict(value=value,kind=kind))
        write(base/'ui-prepared.json',dict(original_files=len(before),source_data_preserved=True,physical_namespace=str(live),future_trigger=future,autostart=False,game_start=False,server=False,runtime=str(runtime)))
    elif sys.argv[1]=='restore':
        record=json.loads((base/'private/ui-recovery.json').read_text(encoding='utf-8-sig'));assert saved.exists() and not archive.exists() and inventory(saved)==record['before']
        write(base/'private/tested-files.json',inventory(live));os.rename(live,archive);os.rename(saved,live);assert inventory(live)==record['before'];budget.track(archive)
        original=json.loads((base/'private/protocol-before.json').read_text(encoding='utf-8-sig'));key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE)
        current,kind=winreg.QueryValueEx(key,'')
        if str(runtime/'BetterGI.exe').lower() in current.lower():winreg.SetValueEx(key,'',0,original['kind'],original['value'])
        current,kind=winreg.QueryValueEx(key,'');assert current==original['value'] and kind==original['kind']
        write(base/'ui-restored.json',dict(all_original_sha_same=True,original_files=len(record['before']),archive=str(archive),protocol_restored=True))
    elif sys.argv[1]=='readback':write(base/('private/ui-readback-'+sys.argv[2]+'.json'),inventory(live))
