from pathlib import Path
import hashlib,json,os
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
metadata=json.loads((base/'round-test-before.json').read_text(encoding='utf-8')); assert metadata['crlf'] is False
p=root/metadata['path']; b=p.read_bytes(); (base/'round-test-before-format-restore.cs.txt').write_bytes(b)
fixed=b.replace(b'\r\n',b'\n'); assert b.count(b'\r\n')==36 and fixed.count(b'\r')==0
tmp=p.with_name(p.name+'.format-restore.tmp'); tmp.write_bytes(fixed); os.replace(tmp,p); assert p.read_bytes()==fixed
(base/'round-format-restore.json').write_text(json.dumps(dict(path=metadata['path'],before_sha256=hashlib.sha256(b).hexdigest(),after_sha256=hashlib.sha256(fixed).hexdigest(),crlf_lines_removed=36,original_crlf=metadata['crlf'],after_crlf=False,atomic_same_directory=True,semantic_diff=False),indent=2),encoding='utf-8')
print('original LF restored in new test block; r1 evidence retained, current bytes need r3 PFP and r2 full')
