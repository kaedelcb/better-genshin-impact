from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;phase=sys.argv[1];product=root/'_workflow/runtime-unified-01a10e1b/product'
source=root/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'full-product-readback-'+phase) as budget:
    budget.track(base)
    original=json.loads((base/'private/source-original-hashes.json').read_text(encoding='utf-8-sig'))
    protected={p:h for p,h in original.items() if Path(p).is_relative_to(source)}
    assert all(sha(Path(p))==h for p,h in protected.items())
    recovery=json.loads((base/'private/ui-recovery.json').read_text(encoding='utf-8-sig'));saved=Path(recovery['preserved']).resolve()
    assert {p.relative_to(saved).as_posix():sha(p) for p,st in s.files_under(saved)}==recovery['before']
    records=[]
    for src in sorted((source/'OneDragon').glob('*.json')):
        dst=product/'User/OneDragon'/src.name;doc=json.loads(dst.read_text(encoding='utf-8-sig'))
        s.write(base/'private'/phase/'OneDragon'/src.name,dst.read_bytes())
        records.append(dict(name=src.name,sha256=sha(dst),tasks=len(doc['TaskDefinitions']),order=doc['TaskOrder'],enabled_count=sum(bool(v) for v in doc['TaskEnabledList'].values())))
    live=saved.parent/'NexusBGI';flows=[]
    for p in sorted((live/'flows').glob('*.flow.json')):
        doc=json.loads(p.read_text(encoding='utf-8-sig'));s.write(base/'private'/phase/'flows'/p.name,p.read_bytes())
        flows.append(dict(file=p.name,sha256=sha(p),name=doc.get('name'),nodes=len(doc['nodes']),activation=doc.get('activation',{})))
    # Sensitive flow activation fields remain private; public summary only counts and SHA.
    s.write(base/'private'/phase/'flows.json',json.dumps(flows,ensure_ascii=False).encode())
    s.write(base/(phase+'.json'),json.dumps(dict(original_user_files_unchanged=len(protected),original_assistant_files_unchanged=len(recovery['before']),standard_configs=records,total_tasks=sum(x['tasks'] for x in records),assistant_flow_count=len(flows),game_started=False),ensure_ascii=False,indent=2).encode())
    print(phase,'configs',len(records),'tasks',sum(x['tasks'] for x in records),'original_files_unchanged',len(protected),flush=True)
