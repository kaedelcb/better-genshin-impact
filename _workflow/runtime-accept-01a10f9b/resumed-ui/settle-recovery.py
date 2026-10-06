from pathlib import Path
import sys,json,os,hashlib,subprocess
sys.path.insert(0,'tools/mistletoe')
import storage_limits as s
from review_support import lock,encode
root=Path.cwd();base=Path(__file__).resolve().parent;control=s.control_dir(root)
old=control/'writer.lock';record=json.loads(old.read_text(encoding='utf-8-sig'))
assert record['pid']==13744 and record['nonce']=='a34af613e431448a89969c1d46168839'
query="@(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -eq 13744 -or $_.ParentProcessId -eq 13744 -or $_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe') } | Select-Object ProcessId,ParentProcessId,Name) | ConvertTo-Json -Compress"
processes=subprocess.run(['pwsh','-NoProfile','-Command',query],check=True,capture_output=True,text=True)
assert not processes.stdout.strip(),'Application or interrupted recovery process still present'
proof=json.loads((base/'import-recovered.json').read_text())
preserved=Path(proof['preserved']);assert preserved.resolve().is_relative_to(base.resolve())
assert preserved.is_file() and hashlib.sha256(preserved.read_bytes()).hexdigest()==proof['sha256']
assert not Path(proof['source']).exists()
backup=base/'interrupted-import-writer.lock.json';assert not backup.exists()
quote=lambda path:"'"+str(path).replace("'","''")+"'"
subprocess.run(['pwsh','-NoProfile','-Command','Move-Item -LiteralPath '+quote(old)+' -Destination '+quote(backup)+' -ErrorAction Stop'],check=True,capture_output=True)
with lock(control):
 file=control/'ledger.json';ledger=json.loads(file.read_text(encoding='utf-8-sig'))
 unresolved=[e for e in ledger['entries'] if e['state'] not in {'retained','failed','cleaned'}]
 assert len(unresolved)==1
 entry=unresolved[0];assert entry['id']=='c8757e0d658b4587b6152e995ee5afdf' and entry['purpose']=='runtime-accept-01a10f9b-physical-import-recovery'
 s.failure_record(base/'interrupted-import-reservation.json',dict(entry=entry,lock=record,process_and_direct_descendants_absent=True,owned_import_preserved=True,owned_source_absent=True,reason='turn deliberately interrupted after file recovery; reservation finalization absent'))
 entry['state']='failed';entry['failure']='interrupted after verified own file recovery; history and roots retained'
 tmp=control/'c8757e0d658b4587b6152e995ee5afdf.recovery.tmp'
 with tmp.open('xb') as stream:stream.write(encode(ledger));stream.flush();os.fsync(stream.fileno())
 os.replace(tmp,file);s.validate_ledger(control,ledger)
print('interrupted recovery reservation retained as failed; own imported file safe',flush=True)
