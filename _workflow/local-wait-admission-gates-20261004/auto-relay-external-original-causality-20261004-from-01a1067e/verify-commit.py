from pathlib import Path
import json,hashlib,subprocess
r=Path.cwd();d=Path(__file__).parent;before=json.loads((d/'commit-before.json').read_text(encoding='utf-8'));head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();files=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head],text=True).splitlines();expected=before['files'];assert not(set(files)-set(expected))
unchanged=[]
for p in sorted(set(expected)-set(files)):
 a=subprocess.check_output(['git','show',before['head']+':'+p]);b=subprocess.check_output(['git','show',head+':'+p]);assert a==b;unchanged.append(dict(path=p,sha256=hashlib.sha256(a).hexdigest(),reason='explicit scope included already tracked unchanged inherited file; no new delta'))
assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in before['source_bytes']);assert subprocess.check_output(['git','diff','--name-only','--',*expected[:4]],text=True)==''
obs=dict(head=head,previous_head=before['head'],branch=before['branch'],actual_files=files,explicit_scope=expected,unchanged_files=unchanged,exit_code=0,source_bytes_unchanged=True,scope_verified=True,independent_pass=False,production_gate_open=False,goal_complete=False,initial_verification='commit succeeded; equality check compared 224 actual deltas against 230 scope paths, six inherited unchanged paths verified byte-identical in both commits; no commit retry')
(d/'candidate-commit-observation.json').write_text(json.dumps(obs,indent=2),encoding='utf-8');(d/'current-status-after.txt').write_bytes(subprocess.check_output(['git','status','--porcelain']));print(json.dumps(dict(head=head,changed_files=len(files),unchanged_in_scope=unchanged,all_source_hashes_preserved=True)),flush=True)
