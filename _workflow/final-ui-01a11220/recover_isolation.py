from pathlib import Path
import ctypes,hashlib,json,os,sys,subprocess
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent
OUT=BASE/'isolated-data/green2'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
from review_support import lock,encode
control=s.control_dir(ROOT)
owner_file=control/'writer.lock';owner=json.loads(owner_file.read_text(encoding='utf-8'))
assert owner['pid']==17052 and owner['nonce']=='24b8f7cc2adf47f5a9f964544de42ae3'
query=subprocess.run(['powershell','-NoProfile','-NonInteractive','-Command',
    '@(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -eq 17052 -or $_.ParentProcessId -eq 17052 -or $_.Name -in @("dotnet.exe","testhost.exe") } | Select-Object ProcessId,ParentProcessId,Name) | ConvertTo-Json -Compress'],capture_output=True,text=True,check=True)
assert not query.stdout.strip(),'Original writer or build/test processes still present'
k=p.kernel();checks=[]
for request in OUT.rglob('*-process-request.json'):
 row=json.loads(request.read_text(encoding='utf-8'));handle=k.OpenJobObjectW(4,False,row['job']);error=ctypes.get_last_error()
 if handle:
  info=p.Accounting();p.checked(k.QueryInformationJobObject(handle,1,ctypes.byref(info),ctypes.sizeof(info),None));k.CloseHandle(handle)
  assert info.active==0,'Original owned Job still active'
  checks.append(dict(job=row['job'],active_processes=0))
 else:
  assert error==2,'Job state unknown'
  checks.append(dict(job=row['job'],absent=True,winerror=error))
assert len(checks)==3,checks
helper=ROOT/'MultiplayerHoeingAssistant/Services/AssistantDataDirectory.cs'
original=(OUT/'negative-original.cs').read_bytes();mutant=(OUT/'negative-patch.cs').read_bytes()
expected=json.loads((OUT/'source-hashes.json').read_text(encoding='utf-8'))[helper.relative_to(ROOT).as_posix()]
assert hashlib.sha256(original).hexdigest()==expected
assert helper.read_bytes()==mutant,'Source identity changed; refuse restore'
ledger_file=control/'ledger.json';ledger_raw=ledger_file.read_bytes();ledger=json.loads(ledger_raw)
unresolved=[e for e in ledger['entries'] if e['state'] not in {'retained','failed','cleaned'}]
assert len(unresolved)==1
entry=unresolved[0]
assert entry['purpose']=='final-ui-01a11220-isolation-green2' and entry['state']=='reserved'
before=dict(entry=entry,lock=owner,ledger_sha256=hashlib.sha256(ledger_raw).hexdigest())
s.failure_record(BASE/'isolation-recovery-before.json',before)
temp=helper.with_name(helper.name+'.recovery-01a11220');assert not temp.exists()
with temp.open('xb') as f:f.write(original);f.flush();os.fsync(f.fileno())
os.replace(temp,helper);assert helper.read_bytes()==original
saved=BASE/'isolation-interrupted-writer.lock.json';assert not saved.exists()
os.replace(owner_file,saved)
with lock(control):
 assert ledger_file.read_bytes()==ledger_raw
 entry['state']='failed';entry['failure']='Execution handle disappeared during negative build evidence finalization; no negative test or restored terminal evidence'
 temp_ledger=control/(entry['id']+'.isolated-recovery.tmp');assert not temp_ledger.exists()
 with temp_ledger.open('xb') as f:f.write(encode(ledger));f.flush();os.fsync(f.fileno())
 os.replace(temp_ledger,ledger_file);s.validate_ledger(control,ledger)
 s.failure_record(BASE/'isolation-recovered.json',dict(reservation=entry['id'],state='failed',
     original_roots_and_budget_preserved=True,source_byte_restored=True,source_sha256=expected,jobs=checks,
     baseline_passed=48,negative_test_not_executed=True,mutation_complete=False,user_data_moved=False))
print('Source atomically restored; three original Jobs terminal/absent; reservation retained as failed',flush=True)
