from pathlib import Path
import sys,os,json,hashlib,subprocess,winreg
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;product=root/'_workflow/runtime-unified-01a10e1b/product';phase=sys.argv[1]
parent=(Path(os.environ['LOCALAPPDATA'])/'Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming').resolve()
live=parent/'NexusBGI';saved=parent/'NexusBGI-preserved-01a10f14';archive=parent/'NexusBGI-tested-01a10f14'
assert all(p.resolve().is_relative_to(parent) for p in [live,saved,archive])
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def inventory(p):return {f.relative_to(p).as_posix():sha(f) for f,st in s.files_under(p)}
def move(source,target):
 assert source.resolve().parent==parent and target.resolve().parent==parent and source.is_dir() and not target.exists()
 q=lambda p:"'"+str(p).replace("'","''")+"'"
 subprocess.run(['pwsh','-NoProfile','-NonInteractive','-Command','Move-Item -LiteralPath '+q(source)+' -Destination '+q(target)+' -ErrorAction Stop'],check=True,capture_output=True)
def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
with s.Session(root,'path-runtime-01a10f14-ui-data-'+phase) as budget:
 budget.track(base);budget.track(live);budget.track(saved);budget.track(archive)
 if phase=='prepare':
  assert live.is_dir() and not saved.exists() and not archive.exists()
  prior=parent/'NexusBGI-tested-01a10e1b';assert prior.is_dir()
  before=inventory(live);prior_before=inventory(prior)
  key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command');value,kind=winreg.QueryValueEx(key,'');winreg.CloseKey(key)
  write(base/'private/ui-data-before.json',dict(physical=str(live),preserved=str(saved),archive=str(archive),original=before,prior_archive=prior_before,protocol_value=value,protocol_kind=kind))
  move(live,saved);assert inventory(saved)==before
  live.mkdir()
  write(live/'assistant-config.json',dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,bgiPath=str(product/'BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime=''))
  s.write(live/'startup-flow.json',b'{"enabled":false,"steps":[]}')
  write(base/'ui-data-prepared.json',dict(physical=str(live),original_files=len(before),prior_test_files=len(prior_before),original_preserved=True,prior_install_records_not_promoted=True,game_start=False,autostart=False,server=False))
  print('prepared isolated own UI namespace; original files',len(before),'prior archive unchanged',len(prior_before),flush=True)
 elif phase=='restore':
  record=json.loads((base/'private/ui-data-before.json').read_text(encoding='utf-8-sig'))
  assert saved.is_dir() and not archive.exists() and inventory(saved)==record['original']
  assert inventory(parent/'NexusBGI-tested-01a10e1b')==record['prior_archive']
  write(base/'private/ui-tested-files.json',inventory(live))
  move(live,archive);move(saved,live);assert inventory(live)==record['original']
  key=winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE);current,kind=winreg.QueryValueEx(key,'')
  if str(product/'BetterGI.exe').lower() in current.lower():winreg.SetValueEx(key,'',0,record['protocol_kind'],record['protocol_value'])
  current,kind=winreg.QueryValueEx(key,'');winreg.CloseKey(key)
  write(base/'ui-data-restored.json',dict(original_files=len(record['original']),all_original_sha_same=True,prior_archive_unchanged=True,archive=str(archive),protocol_restored=current==record['protocol_value'] and kind==record['protocol_kind']))
  print('original namespace restored byte-for-byte; test records retained',flush=True)
 elif phase=='readback':
  label=sys.argv[2];assert label.replace('-','').isalnum()
  write(base/('private/ui-readback-'+label+'.json'),inventory(live));print('readback',label,'files',len(inventory(live)),flush=True)
 else:raise ValueError('Unknown data phase')
