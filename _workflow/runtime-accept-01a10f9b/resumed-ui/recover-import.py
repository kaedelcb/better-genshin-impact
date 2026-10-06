from pathlib import Path
import sys,json,hashlib,subprocess,ctypes,msvcrt
from ctypes import wintypes as w
sys.path.insert(0,'tools/mistletoe');import storage_limits as s
root=Path.cwd();base=Path(__file__).resolve().parent
physical=Path('C:/Users/Administrator/AppData/Roaming/NexusBGI')
source=physical/'flows/wf-22f5a13e.flow.json'
original=Path('C:/Users/Administrator/AppData/Local/Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Roaming/NexusBGI-tested-01a10f9b/flows/wf-22f5a13e.flow.json')
target=base/'unexpected-root-import/wf-22f5a13e.flow.json'
k=ctypes.WinDLL('kernel32',use_last_error=True)
k.GetFinalPathNameByHandleW.argtypes=[w.HANDLE,w.LPWSTR,w.DWORD,w.DWORD];k.GetFinalPathNameByHandleW.restype=w.DWORD
def opened(path):
 with path.open('rb') as stream:
  buf=ctypes.create_unicode_buffer(32768);n=k.GetFinalPathNameByHandleW(msvcrt.get_osfhandle(stream.fileno()),buf,len(buf),0);assert n and n<len(buf);return buf.value
sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
processes=subprocess.run(['pwsh','-NoProfile','-Command',"@(Get-Process BetterGI,MultiplayerHoeingAssistant -ErrorAction SilentlyContinue | Select-Object Id,SessionId) | ConvertTo-Json -Compress"],capture_output=True,text=True,check=True)
assert not processes.stdout.strip(),'Application process still present'
assert source.is_file() and not target.exists() and sha(source)==sha(original)=='39a2183f9addb13828d96be0cc5766d652f926c4eafe0424ac820cb8621dbcf2'
assert opened(source).lower()==('\\\\?\\'+str(source)).lower()
with s.Session(root,'runtime-accept-01a10f9b-physical-import-recovery') as budget:
 budget.track(base);budget.track(source)
 target.parent.mkdir(exist_ok=True)
 quote=lambda path:"'"+str(path).replace("'","''")+"'"
 subprocess.run(['pwsh','-NoProfile','-Command','Move-Item -LiteralPath '+quote(source)+' -Destination '+quote(target)+' -ErrorAction Stop'],check=True,capture_output=True)
 assert not source.exists() and sha(target)==sha(original)
 s.write(base/'import-recovered.json',json.dumps(dict(only_owned_import_moved=True,source=str(source),preserved=str(target),sha256=sha(target),other_physical_root_data_not_modified=True,config_opened_at_msix=True,flow_opened_at_roaming=True,product_acceptance=False),indent=2).encode())
 print('unique own imported file preserved and withdrawn; other physical data untouched',flush=True)
