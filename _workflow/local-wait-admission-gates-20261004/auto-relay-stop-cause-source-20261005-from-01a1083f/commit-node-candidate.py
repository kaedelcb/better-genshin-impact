import json,subprocess,hashlib
from pathlib import Path
root=Path.cwd();base=Path(__file__).parent;relay=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
def git(*args):return subprocess.check_output(['git','-c','core.longpaths=true',*args],encoding='utf-8',text=True)
source=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']
expected=json.loads((base/'node-source-final.json').read_text());assert all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in expected)
assert not git('diff','--cached','--name-only').strip();assert set(git('diff','--name-only','--','MultiplayerHoeingAssistant','Test').splitlines())==set(source)
tracked=list(source)+['_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']
exclude={'HANDOFF.md','relay-prompt.txt','old-goal-paused-readback.json','source-goal-active-readback.json','source-head.txt','source-model-observation.json','source-status.txt','creation-observation.json','current-handoff-before.json','handoff-confirmation.json','new-model-independent-readback.json'}
for p in base.rglob('*'):
    if not p.is_file() or p.suffix.lower() not in {'.json','.txt','.md','.py','.log','.trx'}:continue
    rel=p.relative_to(base)
    if any(x in {'products','input-snapshot','TestResults','controlled-writer-processes'} for x in rel.parts):continue
    if len(rel.parts)==1 and p.name in exclude:continue
    tracked.append(p.relative_to(root).as_posix())
tracked.extend(p.relative_to(root).as_posix() for p in relay.glob('*') if p.is_file())
tracked=sorted(set(tracked));pre=dict(branch=git('branch','--show-current').strip(),head=git('rev-parse','HEAD').strip(),explicit_files=tracked,source_hashes=expected,level='local WIP candidate checkpoint; every original obligation and production gate remains')
(base/'node-commit-precheck.json').write_text(json.dumps(pre,ensure_ascii=False,indent=2),encoding='utf-8');tracked.append((base/'node-commit-precheck.json').relative_to(root).as_posix())
pathspec=base/'node-checkpoint-explicit.pathspec';pathspec.write_bytes(b'\0'.join(x.encode() for x in tracked)+b'\0')
for name,args in [('add',['add','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul']),('commit',['commit','--only','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul','-m','fix(mistletoe): checkpoint original node mapping stop candidate (WIP)'])]:
    p=subprocess.run(['git','-c','core.longpaths=true',*args],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,encoding='utf-8',text=True)
    (base/('node-explicit-'+name+'.log')).write_text(p.stdout,encoding='utf-8');print(name,p.returncode,flush=True);assert p.returncode==0,p.stdout[:1000]
head=git('rev-parse','HEAD').strip();committed=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines();assert set(committed)<=set(tracked) and all(x in committed for x in source)
assert all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in expected)
post=dict(head=head,branch=git('branch','--show-current').strip(),actual_changed_files=committed,source_test_files=source,product_files=source[:1],test_files=source[1:],actual_changed_file_count=len(committed),source_bytes_unchanged=True,staged=git('diff','--cached','--name-only').splitlines(),level='actual local WIP checkpoint; not independent acceptance or product delivery')
(base/'node-commit-observation.json').write_text(json.dumps(post,ensure_ascii=False,indent=2),encoding='utf-8')
(relay/'source-head.txt').write_text(head+'\n',encoding='utf-8');(relay/'source-status.txt').write_text(git('status','--porcelain'),encoding='utf-8')
print(json.dumps(dict(head=head,actual_changed_file_count=len(committed),source_test_files=2,source_bytes_unchanged=True,staged=post['staged']),ensure_ascii=False))
