import json,subprocess,hashlib
from pathlib import Path
root=Path.cwd(); base=Path(__file__).parent
relay=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
def git(*args): return subprocess.check_output(['git','-c','core.longpaths=true',*args],text=True,encoding='utf-8')
source=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs']
expected=json.loads((base/'stop-source-final.json').read_text(encoding='utf-8'))
assert all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in expected)
assert not git('diff','--cached','--name-only').strip(),'other staged work present'
actual=git('diff','--name-only','--','MultiplayerHoeingAssistant','Test').splitlines()
assert set(actual)==set(source),actual
files=source+['_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md']
for folder in [base,relay]:
    for p in folder.rglob('*'):
        if p.is_file() and p.suffix.lower() in {'.json','.txt','.md','.py','.log','.trx'} and not any(part in {'products','input-snapshot','TestResults','controlled-writer-processes'} for part in p.relative_to(folder).parts):
            files.append(p.relative_to(root).as_posix())
files=sorted(set(files))
pre=dict(branch=git('branch','--show-current').strip(),head=git('rev-parse','HEAD').strip(),explicit_files=files,level='WIP candidate checkpoint, all original unresolved findings and final delivery gates retained')
(base/'stop-commit-precheck.json').write_text(json.dumps(pre,ensure_ascii=False,indent=2),encoding='utf-8')
files.append((base/'stop-commit-precheck.json').relative_to(root).as_posix())
pathspec=base/'checkpoint-explicit.pathspec'
pathspec.write_bytes(b'\0'.join(path.encode('utf-8') for path in files)+b'\0')
add=subprocess.run(['git','-c','core.longpaths=true','add','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul'],cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8')
(base/'stop-explicit-add-output.txt').write_text(add.stdout,encoding='utf-8')
assert add.returncode==0,'explicit-path add failed; original output preserved'
p=subprocess.run(['git','-c','core.longpaths=true','commit','--only','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul','-m','fix(mistletoe): checkpoint original mapping and stop integrity candidate (WIP)'],cwd=root,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,encoding='utf-8')
(base/'stop-commit-output.txt').write_text(p.stdout,encoding='utf-8')
print('commit exit',p.returncode)
assert p.returncode==0,'explicit-path commit failed; original output preserved'
head=git('rev-parse','HEAD').strip()
committed=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines()
assert set(committed)<=set(files)
assert all(path in committed for path in source)
assert all(hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'] for r in expected),'Git checkpoint changed source bytes'
post=dict(head=head,branch=git('branch','--show-current').strip(),actual_changed_files=committed,source_test_files=source,product_files=source[:2],test_files=source[2:],actual_changed_file_count=len(committed),source_bytes_unchanged=True,staged=git('diff','--cached','--name-only').splitlines(),worktree_status=git('status','--porcelain'),level='actual local WIP checkpoint; not independent acceptance or product delivery')
(base/'stop-commit-observation.json').write_text(json.dumps(post,ensure_ascii=False,indent=2),encoding='utf-8')
(relay/'source-head.txt').write_text(head+'\n',encoding='utf-8')
(relay/'source-status.txt').write_text(post['worktree_status'],encoding='utf-8')
print(json.dumps(dict(head=head,actual_changed_file_count=len(committed),source_test_files=3,source_bytes_unchanged=True,staged=post['staged']),ensure_ascii=False))
