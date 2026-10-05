from pathlib import Path
import sys,os,json,hashlib,winreg
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;ev=base/'ui-r3';candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3'
logical=Path(os.environ['APPDATA'])/'NexusBGI';live=logical.resolve()
allowed=Path(os.environ['LOCALAPPDATA'])/'Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming'
assert live.parent in [Path(os.environ['APPDATA']).resolve(),allowed.resolve()]
saved=live.parent/'NexusBGI-preserved-01a10cef-r3';archive=live.parent/'NexusBGI-tested-01a10cef-r3'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def inventory(p):return {f.relative_to(p).as_posix():sha(f) for f,_ in s.files_under(p)}
def write(p,v):s.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
with s.Session(root,'candidate-r3-ui-'+sys.argv[1]) as budget:
    budget.track(ev);budget.track(candidate/'User')
    if sys.argv[1]=='prepare':
        assert live.exists() and not saved.exists() and not archive.exists() and not ev.exists()
        ev.mkdir();before=inventory(live)
        write(ev/'private-recovery.json',dict(logical=str(logical),physical=str(live),preserved=str(saved),archive=str(archive),before=before))
        os.rename(live,saved);assert inventory(saved)==before
        live.mkdir();budget.track(live)
        config=dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,bgiPath=str(candidate/'BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime='')
        write(live/'assistant-config.json',config);s.write(live/'startup-flow.json',b'{"enabled":false,"steps":[]}')
        rows=[]
        for src,dst in [(root/'_workflow/runtime-unified-01a10cef/candidate-r2/User/config.json',candidate/'User/config.json'),(base/'legacy-user/OneDragon/验收-统一版本-01a10c87.json',candidate/'User/OneDragon/验收-统一版本-01a10c87.json')]:
            assert not dst.exists();s.write(dst,src.read_bytes());rows.append(dict(source=str(src),target=str(dst),sha256=sha(dst)))
        write(ev/'own-resource-inputs.json',rows)
        key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command');value,kind=winreg.QueryValueEx(key,'')
        write(ev/'protocol-before.json',dict(value=value,kind=kind))
        write(ev/'prepared.json',dict(original_files=len(before),original_sha_preserved=True,physical_namespace=str(live.parent),no_server=True,no_autorun=True,linked_game_start=False,candidate=str(candidate)))
    elif sys.argv[1]=='restore':
        record=json.loads((ev/'private-recovery.json').read_text());assert saved.exists() and not archive.exists() and inventory(saved)==record['before']
        own=inventory(live);write(ev/'test-files-before-archive.json',own)
        os.rename(live,archive);os.rename(saved,live);assert inventory(live)==record['before']
        budget.track(archive)
        prior=json.loads((ev/'protocol-before.json').read_text());key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE)
        current,kind=winreg.QueryValueEx(key,'');changed=False
        if str(candidate/'BetterGI.exe').lower() in current.lower():
            winreg.SetValueEx(key,'',0,prior['kind'],prior['value']);changed=True
        current,kind=winreg.QueryValueEx(key,'')
        write(ev/'restored.json',dict(original_files=len(record['before']),all_original_sha_same=True,test_archive=str(archive),protocol_owned_compare_restore=changed,protocol_exact_before_value_and_kind=current==prior['value'] and kind==prior['kind']))
    elif sys.argv[1]=='readback':
        write(ev/('readback-'+sys.argv[2]+'.json'),inventory(live))
    budget.check(measure=True)
