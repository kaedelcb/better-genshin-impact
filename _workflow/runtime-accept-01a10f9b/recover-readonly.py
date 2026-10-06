from pathlib import Path
import sys,ctypes,json
sys.path.insert(0,'tools/mistletoe')
import process_runner as p
import storage_limits as s
base=Path(__file__).resolve().parent
k=p.kernel()
for phase in ['baseline','negative']:
 folder=base/'undo-causal'/phase
 for identity in folder.glob('*-process-identity.json'):
  row=json.loads(identity.read_text(encoding='utf-8-sig'));h=k.OpenJobObjectW(4,False,row['job'])
  result=dict(phase=phase,identity=identity.name,job=row['job'],pid=row['pid'],handle_present=bool(h),winerror=ctypes.get_last_error())
  if h:
   info=p.Accounting();p.checked(k.QueryInformationJobObject(h,1,ctypes.byref(info),ctypes.sizeof(info),None));result['active']=info.active;k.CloseHandle(h)
  print(json.dumps(result))
control=s.control_dir(Path.cwd());print('control',control)
for name in ['writer.lock','recovery-required.json']:
 file=control/name
 if file.exists():print(name,file.read_text(encoding='utf-8-sig'))
