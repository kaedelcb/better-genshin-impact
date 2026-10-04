from pathlib import Path
import subprocess,json,hashlib
root=Path.cwd(); out=root/'_workflow/local-wait-admission-gates-20261004/parent-capacity'; relay=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-typed-parent-20261004-from-01a105cd'
def git(*args):return subprocess.check_output(['git',*args],text=True,encoding='utf-8').strip()
obs=json.loads((out/'final-r2/lock-candidate-observation.json').read_text(encoding='utf-8'))
assert obs['sources_unchanged'] and obs['comparison_summary']['same_failed_ids']
assert obs['full_counts']=={'Passed':1939,'Failed':18,'NotExecuted':2}
for source in obs['sources']:assert hashlib.sha256((root/source['path']).read_bytes()).hexdigest()==source['sha256']
assert git('branch','--show-current')=='main-OldTeaBag-B168'
paths=[r['path'] for r in obs['sources']]+[p.relative_to(root).as_posix() for p in sorted(out.rglob('*')) if p.is_file()]
assert len(paths)==len(set(paths))
staged=git('diff','--cached','--name-only').splitlines(); assert not set(staged)&set(paths)
(relay/'candidate-opening-observation.json').write_text(json.dumps({'branch':git('branch','--show-current'),'head':git('rev-parse','HEAD'),'staged_before':staged,'owned_paths':paths,'inflight':'build/test/mutation complete; no independent review requests added','source_bytes_match_final':True},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(relay/'candidate-status-before.txt').write_text(git('status','--porcelain')+'\n',encoding='utf-8')
msg=relay/'candidate-commit-message.txt';msg.write_text('fix(task-center): await terminal gate and panel stop reconciliation\n\nCandidate only. Preserve original terminal seal/migration core and cancelled-wait responsibility; use asynchronous panel/Host stop path and retain independent reconciliation worker timeout.\nRebuild exit0; TaskCenter 1939 passed/18 original failures/2 skips; exact union1949/18/2, +3 tests, no removed/changed IDs. Two PFP mutations restored. Initial introduced overlapping-stop regression corrected without changing old assertions.\nTyped parent/capacity/real-entry work, authenticated evidence, comprehensive independent review and actual product acceptance remain pending; production gates closed.\n',encoding='utf-8')
subprocess.run(['git','add','--',*paths],check=True)
with (relay/'candidate-commit.log').open('wb') as log: subprocess.run(['git','commit','--only','--file',str(msg),'--',*paths],stdout=log,stderr=subprocess.STDOUT,check=True)
head=git('rev-parse','HEAD'); actual=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines(); assert set(actual)==set(paths)
for source in obs['sources']:assert hashlib.sha256((root/source['path']).read_bytes()).hexdigest()==source['sha256']
(relay/'candidate-commit-observation.json').write_text(json.dumps({'commit':head,'actual_paths':actual,'expected_paths_match':True,'source_bytes_match_final':True,'staged_after':git('diff','--cached','--name-only').splitlines(),'production_gate_open':False,'goal_complete':False},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(relay/'candidate-status-after.txt').write_text(git('status','--porcelain')+'\n',encoding='utf-8')
print(head,'exact owned candidate checkpoint; current source bytes match tested',flush=True)
