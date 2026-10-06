from pathlib import Path
import json,sys,os,hashlib,ctypes
sys.path.insert(0,'tools/mistletoe')
import storage_limits as s
import process_runner as p
from review_support import lock,encode
root=Path.cwd();base=Path(__file__).resolve().parent;control=s.control_dir(root)
with lock(control):
 file=control/'ledger.json';raw=file.read_bytes();ledger=json.loads(raw)
 entries=[e for e in ledger['entries'] if e['state'] not in {'retained','failed','cleaned'}]
 assert len(entries)==1
 entry=entries[0]
 assert entry['id']=='2c11fc9ee13248ea89b93caaa16ad915' and entry['purpose']=='runtime-accept-01a10f9b-undo-causal' and entry['state']=='reserved'
 k=p.kernel();checks=[]
 for phase in ['baseline','negative']:
  for identity in (base/'undo-causal'/phase).glob('*-process-identity.json'):
   row=json.loads(identity.read_text(encoding='utf-8-sig'));handle=k.OpenJobObjectW(4,False,row['job']);error=ctypes.get_last_error()
   assert not handle and error==2
   checks.append(dict(job=row['job'],absent=True,error=error))
 source=root/'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs'
 original=base/'undo-causal/original-view.bin'
 assert source.read_bytes()==original.read_bytes()
 proof=dict(reservation=entry['id'],state='failed',reason='execution handle lost during negative evidence finalization; no normal tree terminal receipt',jobs=checks,source_sha=hashlib.sha256(source.read_bytes()).hexdigest(),byte_restored=True,original_budget_and_roots_preserved=True)
 s.failure_record(base/'interrupted-reservation-before.json',dict(entry=entry,ledger_sha=hashlib.sha256(raw).hexdigest(),other_entries=len(ledger['entries'])-1))
 s.failure_record(base/'interrupted-recovery.json',proof)
 entry['state']='failed';entry['failure']=proof['reason']
 # Preserve roots and reservation; the next Session measures all retained roots.
 temp=control/'2c11fc9ee13248ea89b93caaa16ad915.recovery.tmp'
 with temp.open('xb') as stream:stream.write(encode(ledger));stream.flush();os.fsync(stream.fileno())
 os.replace(temp,file);s.validate_ledger(control,ledger)
 print('original reservation recorded failed; source restored; four Jobs absent',flush=True)
