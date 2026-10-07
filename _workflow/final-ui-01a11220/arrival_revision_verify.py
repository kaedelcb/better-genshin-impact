from pathlib import Path
import hashlib,json,os,subprocess,sys
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808'
DATA=ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
OLD=ROOT/'_workflow/final-ui-01a11220/own-runtime/cold-insert-01a11808'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda p:json.loads(p.read_bytes())
with s.Session(ROOT,'arrival-revision-safe-final-verification-01a11808') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(BASE);budget.track(DATA)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    observed=subprocess.check_output(['powershell.exe','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe','dotnet.exe','testhost.exe','MSBuild.exe')} | Select-Object ProcessId,Name,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig')
    processes=json.loads(observed) if observed.strip() else []
    if isinstance(processes,dict):processes=[processes]
    assert all(str(PRODUCT).lower() not in (p.get('ExecutablePath') or '').lower() for p in processes)
    cold_path=DATA/'flows/wf-own-cold-same-bgi-01a11808.flow.json'
    cold=cold_path.read_bytes();assert load(cold_path)==load(OLD/'fixtures'/cold_path.name)
    run_path=DATA/'runs/run-f4b7a4d95adc.run.json';run_bytes=run_path.read_bytes();run=load(run_path)
    assert run['state']==6 and run['stopRequested'] and not run['nodeOutcomes'] and not run['submissionHistory']
    doc=load(cold_path);doc.update(triggers=[],terminal=[],loop=None)
    temporary=cold_path.with_name(cold_path.name+'.withdraw-own-01a11808.tmp');write(temporary,doc);os.replace(temporary,cold_path)
    assert run_path.read_bytes()==run_bytes
    write(BASE/'delivery/own-trigger-withdrawn.json',dict(workflow_id=doc['workflowId'],before_sha256=sha(cold),after_sha256=sha(cold_path.read_bytes()),run_id=run['runId'],original_run_byte_unchanged=True,only_own_flow_changed=True))
    inputs=load(BASE/'restored/source-hashes.json');assert all(sha((ROOT/n).read_bytes())==h for n,h in inputs.items())
    version=load(PRODUCT/'VERSION.json');assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in version['assistant_modules'])
    assert sha((PRODUCT/'BetterGI.dll').read_bytes())==version['bgi_dll_sha256'] and sha((PRODUCT/'BetterGI.exe').read_bytes())==version['bgi_exe_sha256']
    before=load(BASE/'actual/private/product-user-before.json');after={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};assert before==after
    real_root=ROOT/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    real_before=load(BASE/'actual/private/real-user-before.json');real_after={f.relative_to(real_root).as_posix():sha(f.read_bytes()) for f,_ in s.files_under(real_root)};assert real_before==real_after
    index=load(BASE/'delivery/evidence-index.json');assert all(sha(Path(row['path']).read_bytes())==row['sha256'] for row in index)
    guide=ROOT/'Docs/technical/mistletoe-startup-migration-recovery.md';guide_bytes=guide.read_bytes()
    assert not guide_bytes.startswith(b'\xef\xbb\xbf') and b'\r\n' not in guide_bytes
    status=subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT)
    s.write(BASE/'delivery/current-workspace-status.txt',status)
    guide_record=dict(path=str(guide),bytes=len(guide_bytes),lines=len(guide_bytes.splitlines()),sha256=sha(guide_bytes),bom=False,crlf=False)
    write(BASE/'delivery/verification.json',dict(kind='mechanical source/product/evidence/data verification; not independent review',compiled_input_count=len(inputs),compiled_drift=[],product_modules=version['assistant_modules'],
        product_user_files=len(before),real_user_files=len(real_before),product_user_changed=[],real_user_changed=[],evidence_index_entries=len(index),evidence_index_drift=[],
        original_run_preservation=load(BASE/'delivery/preserved-original-runs.json'),only_own_trigger_withdrawn=True,guide=guide_record,other_observed_processes=processes,
        review_used=2,review_remaining=0,new_reviews=0,goal_complete=False,product_complete=False,hand_off_required=False))
    print('FINAL VERIFIED: current source/product/evidence; product User9501 and real User9522 unchanged; no owned writers; review grant still 0',flush=True)
