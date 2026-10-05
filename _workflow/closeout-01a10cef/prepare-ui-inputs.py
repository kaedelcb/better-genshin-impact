from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
b=Path(__file__).resolve().parent
old=root/'_workflow/runtime-unified-01a10c87/candidate-r1/User'
with s.Session(root,'non-game-ui-controlled-inputs'):
    d=json.loads((b/'ui-flow-export.json').read_text());d['workflowId']='wf-ui-import-01a10cef';d['name']='验收-导入副本-01a10cef'
    s.write(b/'ui-flow-import-distinct.json',json.dumps(d,ensure_ascii=False,indent=2).encode())
    rows=[]
    for p in [old/'config.json',old/'OneDragon/验收-统一版本-01a10c87.json']:
        dst=b/'legacy-user'/p.relative_to(old);data=p.read_bytes();s.write(dst,data);rows.append(dict(source=str(p),target=str(dst),sha256=hashlib.sha256(data).hexdigest()))
    s.write(b/'ui-legacy-source.json',json.dumps(rows,ensure_ascii=False,indent=2).encode())
