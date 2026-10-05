from pathlib import Path
import sys,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'candidate-delivery-material-requirement-readback'):
    identity=load(base/'FINAL-CANDIDATE.json');candidate=Path(identity['candidate']);commit=load(base/'FINAL-COMMIT.json')
    head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip();assert head==commit['head']
    sources=load(base/'final-source-manifest.json');assert all(sha(root/x['path'])==x['sha256'] for x in sources)
    files=load(base/'final-runtime-manifest.json');assert all(sha(candidate/x['path'])==x['sha256'] for x in files)
    dependencies=load(base/'rebuilt-dependency-source-binding.json');assert all(sha(root/x['path'])==x['sha256'] for x in dependencies['unchanged_source_inputs'])
    assert sha(candidate/'BetterGI.dll')==identity['bgi_sha'] and sha(candidate/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll')==identity['assistant_sha']
    assert load(base/'fence-green/module.json')['sha256']==identity['assistant_sha']
    assert load(base/'ui-r3/restored.json')['all_original_sha_same']
    actual=load(base/'live-ipc/r3-migration/result.json');assert actual['actual_module_sha'].lower()==identity['assistant_sha'] and all(actual[k]['Ok'] for k in ['prepared','activated','rollback','repeat'])
    facts=load(base/'ui-r3/verification-facts.json');assert all(facts['restart_same_sha'].values()) and facts['no_submission_or_completion']
    text=(base/'VALIDATION.md').read_text(encoding='utf-8')
    original=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954/auto-standard-install-01a10c1c/01a10c87-deee-7381-a61d-fefce264d537/VALIDATION.md'
    rows=[line for line in original.read_text(encoding='utf-8-sig').splitlines() if line.startswith('|')]
    assert rows and all(line in text for line in rows),'original validation row missing'
    assert all(k in text for k in ['C01','C02','C04','C06','C07','C08','C09','C10','C11','C17','C20','nextDay','停止','重启','恢复','UID','兑换码','真实关机','独立','0','未验证'])
    assert not subprocess.check_output(['git','diff','--cached','--name-only'])
    assert not subprocess.check_output(['git','diff','--name-only','--',*[x['path'] for x in sources]])
    result=dict(head=head,scope='candidate identity + complete validation content; not product total acceptance',all_runtime_files_current_sha=True,all_source_hashes_current=True,rebuilt_project_source_inputs_bound=True,same_sdk_real_migration=True,actual_ui_mixed_authoring_wait_cancel_restart=True,user_data_restored=True,complete_original_function_matrix=True,final_local_checkpoint_read_back=True,independent_repair_review=False,product_total_accepted=False,known_failures_preserved=True,material_out_changes_not_reset=True)
    s.write(base/'completion-audit.json',json.dumps(result,ensure_ascii=False,indent=2).encode())
    print(json.dumps(result,ensure_ascii=False),flush=True)
