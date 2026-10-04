from pathlib import Path
import subprocess,json,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004';out=base/'original-send-round';sources=json.loads((out/'candidate-source-set.json').read_text(encoding='utf-8'))['owned']
relay=base/'auto-relay-g7-original-round-20261004-from-01a10546'
files=sources+['_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']+[str(p.relative_to(root)).replace('\\','/') for p in out.rglob('*') if p.is_file() and p.suffix not in ['.tmp','.pyc']]+[str((relay/name).relative_to(root)).replace('\\','/') for name in ['new-goal-readback.json','new-handshake-observation.json','new-opening-status.txt']]
files=sorted(set(files));assert all((root/p).is_file() for p in files)
assert not any('/User/' in p or '/products/' in p or p.endswith('R56ReferenceActivationWiringTests.cs') or p.endswith('MigrationSwitchTransaction.cs') for p in files)
msg=out/'candidate-commit-message.txt';msg.write_text('fix(task-center): bind recovery to original send rounds and retain no-byte retries\n\nCandidate only: preserve original nonce/payload and rejected rounds; validate current/history recovery and strict facade settlement.\nRebuild exit0; 1906 passed/18 failed/2 skipped unique TaskCenter plus compatibility union, same 18 failure IDs; nine P/F/P mutations restored.\nAll original important/open obligations, certification, independent comprehensive review and real product acceptance remain pending. Production gates closed.\n',encoding='utf-8')
files.append(str(msg.relative_to(root)).replace('\\','/'))
(out/'candidate-commit-paths.json').write_text(json.dumps(files,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');files.append(str((out/'candidate-commit-paths.json').relative_to(root)).replace('\\','/'))
tracked=set(subprocess.check_output(['git','ls-files','--',*files],text=True).splitlines());new=[p for p in files if p not in tracked]
if new:subprocess.run(['git','add','--',*new],check=True)
with (out/'candidate-commit.log').open('wb') as log:r=subprocess.run(['git','commit','--only','--file',str(msg),'--',*files],stdout=log,stderr=subprocess.STDOUT)
print('candidate commit exit',r.returncode,'explicit files',len(files))
print((out/'candidate-commit.log').read_text(encoding='utf-8',errors='replace')[-1600:])
if r.returncode!=0:raise SystemExit(r.returncode)
head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head],text=True).splitlines();assert set(actual)==set(files),(set(actual)-set(files),set(files)-set(actual))
facts=json.loads((out/'candidate-source-set.json').read_text(encoding='utf-8'))['facts']
assert all(hashlib.sha256((root/row['path']).read_bytes()).hexdigest()==row['sha256'] for row in facts)
record={'kind':'local candidate checkpoint, not independent pass/receipt/product acceptance','commit':head,'actual_paths':actual,'explicit_paths_match':True,'working_source_bytes_match_verified_candidate':True,'all_original_obligations_retained':True,'push_release_deploy':False}
(out/'commit-observation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
with (out/'git-status-post-candidate-commit.txt').open('wb') as f:subprocess.run(['git','status','--porcelain'],stdout=f,check=True)
print('HEAD',head)
