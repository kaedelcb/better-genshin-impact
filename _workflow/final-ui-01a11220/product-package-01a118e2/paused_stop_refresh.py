"""Refresh only the existing five assistant modules after restored source verification."""
from pathlib import Path
import os,sys,json,hashlib,subprocess
R=Path(__file__).resolve().parents[3];B=Path(__file__).resolve().parent/'paused-stop-refresh'
P=R/'_workflow/runtime-unified-01a10e1b/product'; C=R/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
E=Path(__file__).resolve().parent/'paused-stop-repair/retry1/restored'
sys.path.insert(0,str(R/'tools/mistletoe'));import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest();load=lambda p:json.loads(p.read_bytes())
original=s.size;s.size=lambda roots:original(list(dict.fromkeys(str(x) for x in roots)))
session=s.Session(R,'same-product-paused-stop-terminal-module-refresh-01a11940');root_policy=dict(session.policy)
session.policy=dict(root_policy,operation_bytes=128*1024*1024);assert session.policy['operation_bytes']<=root_policy['operation_bytes']
with session:
    assert not B.exists();session.track(B);session.track(P/'Tools/MultiplayerHoeingAssistant');session.track(P/'VERSION.json');B.mkdir()
    write=lambda p,v:s.write(p,s.encode(v));v=load(P/'VERSION.json');raw=(P/'VERSION.json').read_bytes()
    assert load(E/'build-process-result.json')['exit_code']==0 and load(E/'build-tree-terminal.json')['active_processes']==0
    assert load(E/'test-tree-terminal.json')['active_processes']==0
    evidence=load(E.parent/'verification.json');assert evidence['source_restored'] and not evidence['independent_verified']
    hashes=load(E/'source-hashes.json');assert all(sha((R/n).read_bytes())==h for n,h in hashes.items())
    processes=json.loads(subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig'))
    assert all(str(P).lower() not in str(q).lower() for q in processes)
    assert sha((P/'BetterGI.dll').read_bytes())==v['bgi_dll_sha256'] and sha((P/'BetterGI.exe').read_bytes())==v['bgi_exe_sha256']
    before={f.relative_to(P/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(P/'User')}
    s.write(B/'previous/VERSION.json',raw);updated=[]
    for m in v['assistant_modules']:
        target=P/m['path'];old=target.read_bytes();assert sha(old)==m['sha256'];replacement=(C/target.name).read_bytes()
        s.write(B/'previous/assistant'/target.name,old)
        if old!=replacement:
            tmp=target.with_name(target.name+'.paused-stop-refresh-01a11940.tmp');s.write(tmp,replacement);os.replace(tmp,target)
        assert target.read_bytes()==replacement;updated.append(dict(path=m['path'],sha256=sha(replacement)))
    assert before=={f.relative_to(P/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(P/'User')}
    old=v.get('module_integration');v.setdefault('module_integration_history',[]).append(old)
    binding=dict(thread_id=os.environ['CODEX_THREAD_ID'],record=str(B/'integration.json'),previous_version=str(B/'previous/VERSION.json'),previous_modules=str(B/'previous/assistant'),build_request=str(E/'build-process-request.json'),build_result=str(E/'build-process-result.json'),build_tree_terminal=str(E/'build-tree-terminal.json'),source_inputs_count=len(hashes),ordinary_provenance_only=True,unit_test_modules_copied=False,product_complete=False)
    v.update(source_checkpoint=subprocess.check_output(['git','rev-parse','HEAD'],cwd=R,text=True).strip(),assistant_modules=updated,compiled_input_hashes=str(E/'source-hashes.json'),module_integration=binding,current_actual_evidence_scope='Host guard same-product replay passed; cold paused Stop missed terminal reconciliation in that replay; current minimal Stop fix rebuilt and causally verified, refreshed actual replay pending')
    v['current_repair']['current_product_host_input_sha256']=hashes['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs']
    v['current_repair']['subsequent_host_delta']='Only paused Stop return now uses existing terminal reconciliation; ResumeRunAsync and its read-only mode guard unchanged; original repair evidence retains its own source binding'
    v['paused_stop_repair']=dict(finding_id='PAUSED-STOP-TERMINAL-RECONCILIATION-1',severity='important',status='open',source_verification=str(E.parent/'verification.json'),actual_red=str(B.parent/'runtime-hostfix/result.json'),independent_verification_pending=True,actual_verified=False)
    encoded=s.encode(v);tmp=P/'VERSION.json.paused-stop-01a11940.tmp';s.write(tmp,encoded);os.replace(tmp,P/'VERSION.json');assert (P/'VERSION.json').read_bytes()==encoded
    write(B/'integration.json',dict(**binding,modules=updated,previous_module_sha256=load(B/'previous/VERSION.json')['assistant_modules'],product_user_changed=[],bgi_and_other_modules_preserved=True,policy_original=root_policy,operation_bytes=session.limit,review_requests_new=0,actual_verified=False))
    print('CURRENT_PAUSED_STOP_MODULES_REFRESHED',updated[0]['sha256'],flush=True)
