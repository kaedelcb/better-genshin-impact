"""Recover only the exact owned runtime reservation after its controller disappeared."""
from pathlib import Path
import ctypes, hashlib, json, os, subprocess, sys, winreg

R=Path(__file__).resolve().parents[3]
B=Path(__file__).resolve().parent/'runtime-paused-stop-resume1'
P=R/'_workflow/runtime-unified-01a10e1b/product'
D=R/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
sys.path.insert(0,str(R/'tools/mistletoe'))
import storage_limits as s
import process_runner as runner
sha=lambda data:hashlib.sha256(data).hexdigest()
load=lambda p:json.loads(p.read_bytes())
control=s.control_dir(R)
lock=control/'writer.lock'; lock_bytes=lock.read_bytes(); owner=load(lock)
assert owner['pid']==62096 and owner['nonce']=='a7503c2904b44ae49018934f5cd51bb4'
ledger_path=control/'ledger.json'; ledger_bytes=ledger_path.read_bytes(); ledger=load(ledger_path)
entry=next(e for e in ledger['entries'] if e['id']=='dec28fb1ce33450aab5d2d2f1f968fb0')
assert entry['state']=='reserved' and entry['reserved_bytes']==134217728
assert entry['purpose']=='same-product-cold-paused-stop-terminal-runtime-01a11940'
assert set(entry['roots'])=={str(B),str(D),str(P/'User')}
identity=load(B/'apps-process-identity.json'); request=load(B/'apps-process-request.json')
assert identity['pid']==64468 and identity['job']==request['job']==load(B/'inflight.json')['job']
facts=json.loads(subprocess.check_output(['powershell.exe','-NoProfile','-Command',
    'Get-CimInstance Win32_Process | Select-Object Name,ProcessId,ParentProcessId,CreationDate,ExecutablePath | ConvertTo-Json -Compress'],text=True,encoding='utf-8-sig'))
assert not any(f['ProcessId'] in {62096,64468,19576,67028,62816} for f in facts)
assert not any(str(P).lower() in (f.get('ExecutablePath') or '').lower() for f in facts)
k=runner.kernel(); ctypes.set_last_error(0)
handle=k.OpenJobObjectW(4,False,identity['job']); job_error=ctypes.get_last_error()
assert not handle and job_error==2,('owned Job still exists or lookup uncertain',job_error)
assert not (B/'apps-process-result.json').exists() and not (B/'app-lifecycle.json').exists()
run=D/'runs/run-524252fdcd04.run.json'
assert sha(run.read_bytes())=='2af2bbc20bd0a2f5aa2d4ca6869cd713f4886f00409bd0560f98caea1450963b'
first=load(B/'first-exit.json'); assert first['assistant_exit']==0 and first['bgi_pid']==19576
def fingerprint(root):
    return {f.relative_to(root).as_posix():sha(f.read_bytes()) for f,_ in s.files_under(root)}
before=load(B/'private/product-user-before.json'); after=fingerprint(P/'User')
real=R/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
real_before=load(B/'private/real-user-before.json'); real_after=fingerprint(real)
assert before==after and real_before==real_after
original=load(B/'private/protocol-before.json')
with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_SET_VALUE) as key:
    current,kind=winreg.QueryValueEx(key,'')
    if (current,kind)!=(original['value'],original['kind']):
        assert str(P/'BetterGI.exe').lower() in current.lower()
        winreg.SetValueEx(key,'',0,original['kind'],original['value'])
    assert winreg.QueryValueEx(key,'')==(original['value'],original['kind'])
protected=[f for f in facts if f['ProcessId'] in {43292,54668}]
assert len(protected)==2 and all(f['SessionId']==3 for f in json.loads(subprocess.check_output(
    ['powershell.exe','-NoProfile','-Command','Get-CimInstance Win32_Process | Where-Object {$_.ProcessId -in @(43292,54668)} | Select-Object ProcessId,SessionId | ConvertTo-Json -Compress'],text=True,encoding='utf-8-sig')))
proof=dict(reason='controller and owned Job disappeared after desktop tool reconnection; no normal terminal result',
    controller_exit=None,actual_verified=False,user_stopped_computer_use=False,product_complete=False,
    owned_pids_absent=[62096,64468,19576,67028,62816],owned_job=identity['job'],job_open_error=job_error,
    job_not_found=True,first_assistant_exit=0,second_assistant_and_bgi_normal_exits_unverified=True,
    paused_sha256=sha(run.read_bytes()),paused_record_unchanged=True,product_user_changed=[],real_user_changed=[],
    product_user_fingerprint_sha256=sha(s.encode(after)),real_user_fingerprint_sha256=sha(s.encode(real_after)),
    protocol_restored=True,d_user_processes_preserved=protected,storage_reservation=entry['id'],
    original_lock=owner,original_ledger_sha256=sha(ledger_bytes),new_review_requests=0)
s.failure_record(B/'interrupted-terminal.json',proof)
entry['state']='failed'; entry['failure']=proof['reason']
entry['recovery_evidence']=str(B/'interrupted-terminal.json')
entry['recovery_measured_artifact_bytes']=s.size([B])
entry['actual_increment_unknown_reason']='controller died before Session close; complete tracked roots remain charged by live size'
tmp=control/(entry['id']+'.recovery.tmp'); assert not tmp.exists()
assert ledger_path.read_bytes()==ledger_bytes and lock.read_bytes()==lock_bytes
with tmp.open('xb') as f:
    f.write(s.encode(ledger)); f.flush(); os.fsync(f.fileno())
os.replace(tmp,ledger_path)
assert load(ledger_path)==ledger and lock.read_bytes()==lock_bytes
lock.unlink()
print(json.dumps(proof,ensure_ascii=False))
