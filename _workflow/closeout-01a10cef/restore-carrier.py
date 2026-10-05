from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
b=Path(__file__).resolve().parent
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954/auto-standard-install-01a10c1c/01a10c87-deee-7381-a61d-fefce264d537'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'joint-carrier-original-identity-restoration'):
    changed=json.loads((b/'joint-products-before.json').read_text());assert all(sha(Path(r['path']))==r['sha256'] for r in changed)
    s.write(b/'joint-products-after-test.json',json.dumps(changed,indent=2).encode())
    original=json.loads((prior/'regression-r2/products-before.json').read_text())
    repaired=[]
    for row in original:
        p=Path(row['path'])
        if sha(p)!=row['sha256']:
            source=Path(row['source']);assert sha(source)==row['sha256'];s.write(p,source.read_bytes(),mode='wb');repaired.append(str(p))
    assert all(sha(Path(r['path']))==r['sha256'] for r in original)
    s.write(b/'joint-original-carrier-restored.json',json.dumps(dict(all_original_28_same_sha=True,restored=repaired,latest_test_modules_bound_separately=True),indent=2).encode())
