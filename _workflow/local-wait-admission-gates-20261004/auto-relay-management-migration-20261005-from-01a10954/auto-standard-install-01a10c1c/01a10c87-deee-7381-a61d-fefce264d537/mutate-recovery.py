from pathlib import Path
import sys,json,hashlib,os,subprocess
root=Path.cwd();base=Path(__file__).resolve().parent;sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
kind=sys.argv[1]
rel='MultiplayerHoeingAssistant/Services/TaskCenter/'+('TaskCenterHost.cs' if kind=='entry' else 'WorkflowStore.cs')
p=root/rel;original=p.read_bytes();digest=hashlib.sha256(original).hexdigest();nl=b'\r\n' if b'\r\n' in original else b'\n'
if kind=='entry':
    needle=b'                    var recovery = _workflows.LoadMigrationRecovery(workflowId);'
    replacement=b'                    _ = _workflows.LoadSnapshot(workflowId);'+nl+needle
else:
    needle=b'if (hasTransaction && (current.ExtensionData![WorkflowMigrationConsumer.TransactionField].ValueKind'
    replacement=b'if (false && (current.ExtensionData![WorkflowMigrationConsumer.TransactionField].ValueKind'
assert original.count(needle)==1;mutant=original.replace(needle,replacement)
ev=base/'recovery-fix'/('mutation-'+kind)
with storage.Session(root,'recovery-mutation-'+kind+'-before') as budget:
    budget.track(ev);budget.check(location=ev);ev.mkdir(exist_ok=False)
    storage.write(ev/'original.bin',original);storage.write(ev/'before.json',json.dumps(dict(target=rel,sha256=digest,mutant_sha256=hashlib.sha256(mutant).hexdigest()),indent=2).encode())
try:
    tmp=p.with_name(p.name+'.recovery-mutant.tmp');tmp.write_bytes(mutant);os.replace(tmp,p)
    code=subprocess.run([sys.executable,'-B',str(base/'check-recovery.py'),'negative-'+kind],cwd=root).returncode
    print('mutant_exit',code,flush=True)
finally:
    tmp=p.with_name(p.name+'.recovery-restored.tmp');tmp.write_bytes(original);os.replace(tmp,p)
    assert hashlib.sha256(p.read_bytes()).hexdigest()==digest
    with storage.Session(root,'recovery-mutation-'+kind+'-restore') as budget:
        budget.track(ev);storage.write(ev/'restored.json',json.dumps(dict(target=rel,sha256=digest,same_sha=True),indent=2).encode());budget.check(measure=True)
    print('restored',kind,digest,flush=True)
