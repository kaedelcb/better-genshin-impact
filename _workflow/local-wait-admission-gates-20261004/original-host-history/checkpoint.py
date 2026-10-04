from pathlib import Path
import subprocess,json,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004';out=base/'original-host-history';relay=base/'auto-relay-original-host-history-20261004-from-01a10568'
source=json.loads((out/'candidate-source-set.json').read_text(encoding='utf-8'))
assert subprocess.check_output(['git','branch','--show-current'],text=True).strip()=='main-OldTeaBag-B168'
for row in source['facts']:assert hashlib.sha256((root/row['path']).read_bytes()).hexdigest()==row['sha256']
with (out/'git-status-pre-commit.txt').open('wb') as f:subprocess.run(['git','status','--porcelain'],stdout=f,check=True)
msg=out/'candidate-commit-message.txt';msg.write_text('fix(task-center): retain historical exit observations and verify archived stop evidence\n\nCandidate only: restore original stop/exit/readback, append bound historical observations without rewriting old facts, reopen safely, and reject archived seal mismatches.\nRebuild exit0; full filter 1936 passed/18 failed/2 skipped, same original failure IDs; targeted expansion 100/100, five PFP mutations restored.\nCertification, independent comprehensive review, all original important/open obligations and real product acceptance remain pending; production gates closed.\n',encoding='utf-8')
files=source['owned']+['_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']+[str(p.relative_to(root)).replace('\\','/') for p in out.rglob('*') if p.is_file() and p.suffix not in ['.tmp','.pyc']]+[str((relay/name).relative_to(root)).replace('\\','/') for name in ['new-goal-readback.json','new-handshake-observation.json','deliveries-discovery.json']]
files=sorted(set(files));assert not any('/User/' in p or '/products/' in p or p.endswith('MigrationSwitchTransaction.cs') or p.endswith('MigrationReferenceActivation.cs') or p.endswith('.csproj') for p in files)
paths=out/'candidate-commit-paths.json';paths.write_text(json.dumps(files,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');files.append(str(paths.relative_to(root)).replace('\\','/'))
tracked=set(subprocess.check_output(['git','ls-files','--',*files],text=True).splitlines());new=[p for p in files if p not in tracked]
if new:subprocess.run(['git','add','--',*new],check=True)
with (out/'candidate-commit.log').open('wb') as f:r=subprocess.run(['git','commit','--only','--file',str(msg),'--',*files],stdout=f,stderr=subprocess.STDOUT)
print((out/'candidate-commit.log').read_text(encoding='utf-8',errors='replace')[-1300:]);assert r.returncode==0
head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head],text=True).splitlines();assert set(actual)==set(files),(set(actual)-set(files),set(files)-set(actual))
for row in source['facts']:assert hashlib.sha256((root/row['path']).read_bytes()).hexdigest()==row['sha256']
(out/'commit-observation.json').write_text(json.dumps({'kind':'local candidate checkpoint, not independent pass/receipt/product acceptance','commit':head,'actual_paths':actual,'explicit_paths_match':True,'source_bytes_match_verified_candidate':True,'production_gate_open':False,'goal_complete':False,'push_release_deploy':False},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
with (out/'git-status-post-commit.txt').open('wb') as f:subprocess.run(['git','status','--porcelain'],stdout=f,check=True)
print('HEAD',head,'files',len(actual))
