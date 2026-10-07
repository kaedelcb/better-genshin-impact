"""Owned complete-product UI acceptance; explicit assistant root, no real-user moves."""
from pathlib import Path
import hashlib,json,os,subprocess,sys,winreg,datetime
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'own-runtime'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product';DATA=BASE/'assistant-data'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T07-15-42-01a11380-5e25-7451-a15a-e3812284ea34.jsonl')
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
sha=lambda b:hashlib.sha256(b).hexdigest()
if sys.argv[1]=='--apps':
 assert os.environ.get('NEXUSBGI_DATA_ROOT')==str(DATA)
 bgi=subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
 assistant=subprocess.Popen([str(PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe')],cwd=PRODUCT/'Tools/MultiplayerHoeingAssistant')
 print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
 exits=[assistant.wait(),bgi.wait()];print('NORMAL_APP_EXITS',exits,flush=True);sys.exit(0 if exits==[0,0] else 1)
phase=sys.argv[1];assert phase in ['fifth','sixth'];out=BASE/phase
with s.Session(ROOT,'own-root-01a11380-real-ui-'+phase) as budget:
 budget.track(BASE);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User');out.mkdir(parents=True,exist_ok=False)
 def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
 identities=json.loads((BASE/'refresh-context/result.json').read_text());assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in identities['updated'])
 assert sha((PRODUCT/'BetterGI.dll').read_bytes())==identities['bgi_sha256'] and sha((PRODUCT/'BetterGI.exe').read_bytes())==identities['bgi_exe_sha256']
 before={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};write(out/'private/product-user-before.json',before)
 for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
  for f in (DATA/folder).glob(pattern):s.write(out/'private/before'/folder/f.name,f.read_bytes())
 write(out/'admission.json',dict(marker='OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308',finding='OWN-ROOT-FLOW-SWITCH-IMPORTANT-2',important_open=True,assistant_data_root=str(DATA),model='gpt-6.1-sol',effort='xhigh',source_rollout=str(ROLLOUT),user_data_moved=False,game_execution=False,product_acceptance=False))
 with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:protocol,kind=winreg.QueryValueEx(key,'')
 write(out/'private/protocol-before.json',dict(value=protocol,kind=kind));env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
 start=datetime.datetime.now(datetime.timezone.utc).isoformat();print('Starting own complete product',phase,DATA,flush=True)
 code,_,_=p.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=3600)
 with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE) as key:
  current,_=winreg.QueryValueEx(key,'')
  if str(PRODUCT/'BetterGI.exe').lower() in current.lower():winreg.SetValueEx(key,'',0,kind,protocol)
  current,current_kind=winreg.QueryValueEx(key,'');assert current==protocol and current_kind==kind
 after={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};write(out/'private/product-user-after.json',after)
 for folder,pattern in [('flows','*.flow.json'),('runs','*.run.json')]:
  for f in (DATA/folder).glob(pattern):s.write(out/'private/after'/folder/f.name,f.read_bytes())
 records=[]
 for line in ROLLOUT.read_bytes().splitlines():
  row=json.loads(line)
  if row.get('timestamp','')>=start and row.get('type') in ['response_item','event_msg']:records.append(line)
 raw=b'\n'.join(records)+b'\n';s.write(out/'private/native-ui-source.jsonl',raw)
 write(out/'result.json',dict(exit_code=code,assistant_data_root=str(DATA),product_user_changed=[n for n in set(before)|set(after) if before.get(n)!=after.get(n)],protocol_restored=True,raw_ui_source_sha256=sha(raw),game_executed=False,independent_review=False,product_complete=False))
 print('Own apps ended',code,'protocol restored',flush=True)
