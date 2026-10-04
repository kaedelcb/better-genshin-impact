from pathlib import Path
import json,subprocess,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004';relay=base/'auto-relay-parent-capacity-20261004-from-01a105a5';out=base/'original-host-history'
goal=json.loads((relay/'old-goal-paused-readback.json').read_text(encoding='utf-8'));assert goal['goal']['status']=='paused' and goal['goal']['threadId']=='01a105a5-74ae-7820-8451-15ae698d8afd'
sources=json.loads((out/'candidate-source-set.json').read_text(encoding='utf-8'))['facts'];assert all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in sources)
files=[str(p.relative_to(root)).replace('\\','/') for p in relay.iterdir() if p.is_file()]+[str((out/name).relative_to(root)).replace('\\','/') for name in ['commit-observation.json','candidate-commit.log','git-status-post-commit.txt']]
new=set(files)-set(subprocess.check_output(['git','ls-files','--',*files],text=True).splitlines())
if new:subprocess.run(['git','add','--',*sorted(new)],check=True)
with (relay/'handoff-commit.log').open('wb') as f:result=subprocess.run(['git','commit','--only','-m','docs: preserve historical stop candidate and parent-capacity relay','--',*files],stdout=f,stderr=subprocess.STDOUT)
assert result.returncode==0;head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head],text=True).splitlines();assert set(actual)==set(files)
with (relay/'opening-status.txt').open('wb') as f:subprocess.run(['git','status','--porcelain'],stdout=f,check=True)
opening={'actualCwd':str(root),'branch':subprocess.check_output(['git','branch','--show-current'],text=True).strip(),'head':head,'product_candidate_commit':'b9d234d23c7fe80294b983684297e21ee00c639c','metadata_commit_paths':actual,'source_bytes_match_verified_candidate':all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in sources),'old_goal_status_from_native_readback':'paused','goal_complete':False,'production_gate_open':False,'no_build_test_mutation_in_flight':True,'status_file':'opening-status.txt','process_observation':'after-safe-terminal-processes.json'}
(relay/'opening-observation.json').write_text(json.dumps(opening,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(opening,ensure_ascii=False))
