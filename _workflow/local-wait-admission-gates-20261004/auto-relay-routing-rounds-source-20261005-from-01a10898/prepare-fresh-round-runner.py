from pathlib import Path
import json,hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
original=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f/run-stop-check.py'
b=original.read_bytes(); script=b.decode('utf-8-sig')
script=script.replace('import sys, json, hashlib, subprocess, time','import sys, json, hashlib, subprocess, time, shutil')
script=script.replace('out = base / sys.argv[1]\nout.mkdir(exist_ok=False)', '''evidence = base / sys.argv[1]
out = Path('C:/Users/Administrator/.codex/tmp/bgi-routing-rounds-01a108b6') / sys.argv[1]
out.parent.mkdir(parents=True, exist_ok=True)
out.mkdir(exist_ok=False)
evidence.mkdir(exist_ok=False)''')
# Original script on this workspace has CRLF; normalize only the new owned helper.
if 'evidence = base' not in script:
 script=b.decode('utf-8-sig').replace('\r\n','\n')
 script=script.replace('import sys, json, hashlib, subprocess, time','import sys, json, hashlib, subprocess, time, shutil')
 script=script.replace('out = base / sys.argv[1]\nout.mkdir(exist_ok=False)', '''evidence = base / sys.argv[1]
out = Path('C:/Users/Administrator/.codex/tmp/bgi-routing-rounds-01a108b6') / sys.argv[1]
out.parent.mkdir(parents=True, exist_ok=True)
out.mkdir(exist_ok=False)
evidence.mkdir(exist_ok=False)''')
assert 'evidence = base' in script
script=script.replace('raise SystemExit(code)', '''save('external-products.json', dict(absolute_output=str(out), evidence_index=str(evidence), source_root=str(root), product_copy=False, purpose='fresh isolated build/test products; protected User untouched'))
for item in out.iterdir():
    if item.is_file(): shutil.copyfile(item, evidence/item.name)
raise SystemExit(code)''')
(base/'run-round-check.py').write_text(script,encoding='utf-8')
(base/'fresh-runner-observation.json').write_text(json.dumps(dict(original_helper=str(original.relative_to(root)),original_sha256=hashlib.sha256(b).hexdigest(),fresh_output_parent='C:/Users/Administrator/.codex/tmp/bgi-routing-rounds-01a108b6',source_root=str(root),reason='preserve all evidence; E drive low capacity; no existing directory cleanup, volume or mount operation'),indent=2),encoding='utf-8')
print('created owned helper for fresh C products and byte-exact raw evidence indexes')
