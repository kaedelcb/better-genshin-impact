from pathlib import Path
import sys,json,hashlib,subprocess,uuid
root=Path.cwd();base=Path(__file__).resolve().parent
files=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowMigrationConsumerTests.cs','_workflow/local-wait-admission-gates-20261004/plan.json']
files += [p.relative_to(root).as_posix() for p in base.iterdir() if p.is_file() and p.suffix in ['.md','.py','.json']]
for rel in ['build-r1/build-summary.json','build-r1/source-before.json','build-r1/source-after.json','build-r1/dotnet-info.txt','build-r1/runtime-files.json','regression-r1/summary.json','regression-r2/summary.json','regression-r2/product-readback.json','regression-r2/products-before.json','regression-r2/assistant.trx','regression-r2/bgi.trx','runtime-facts/ui-persistence-observation.json','runtime-facts/supplemental-route-resources.json','runtime-facts/runtime-files-with-routes.json','recovery-fix/source-before.json','recovery-fix/red-r1/summary.json','recovery-fix/green-r3/summary.json','recovery-fix/negative-entry/summary.json','recovery-fix/negative-identity/summary.json','recovery-fix/mutation-entry/before.json','recovery-fix/mutation-entry/restored.json','recovery-fix/mutation-identity/before.json','recovery-fix/mutation-identity/restored.json','recovery-fix/restored-full-r1/summary.json','recovery-fix/restored-full-r1/source-before.json','recovery-fix/restored-full-r1/source-after.json','recovery-fix/restored-full-r1/products.json','recovery-fix/restored-full-r1/assistant.trx']:
    p=base/rel;assert p.is_file();files.append(p.relative_to(root).as_posix())
files=list(dict.fromkeys(files));assert not any('private-user-backup' in p or 'runtime-unified-' in p for p in files)
assert subprocess.check_output(['git','diff','--cached','--name-only'],cwd=root)==b'','unexpected staged material'
before=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip()
tracked=set(subprocess.check_output(['git','ls-files','-z','--',*files],cwd=root).decode().split('\0'))
new_files=[p for p in files if p not in tracked]
if new_files:subprocess.run(['git','-c','core.longpaths=true','add','--intent-to-add','--',*new_files],cwd=root,check=True)
args=['git','-c','core.longpaths=true','commit','--only','-m','fix: recover interrupted migration through bound recovery metadata','--',*files]
result=subprocess.run(args,cwd=root,capture_output=True)
sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as storage
with storage.Session(root,'migration-recovery-checkpoint-readback') as budget:
    attempt=base/('commit-attempt-'+uuid.uuid4().hex[:8])
    storage.write(attempt/'stdout.txt',result.stdout)
    storage.write(attempt/'stderr.txt',result.stderr)
    if result.returncode:print(result.stderr.decode('utf-8',errors='replace'));sys.exit(result.returncode)
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip()
    actual=subprocess.check_output(['git','show','--format=','--name-only','HEAD'],cwd=root).decode().splitlines()
    assert set(actual)==set(files),'commit scope differs'
    staged=subprocess.check_output(['git','diff','--cached','--name-only'],cwd=root);assert not staged
    summary=dict(before_head=before,head=head,files=actual,staged_empty=True,product_accepted=False,independent_repair_review=False,original_report='bf73360e blocked retained',user_scope='user will do subsequent real-machine verification; deliver complete validation content')
    storage.write(base/'commit-result.json',json.dumps(summary,indent=2).encode());print(json.dumps(dict(head=head,files=len(actual),staged_empty=True)),flush=True);budget.check(measure=True)
