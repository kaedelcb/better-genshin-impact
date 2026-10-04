from pathlib import Path
import json,subprocess,hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'; prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'; target=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
def git(*args,check=True):
 p=subprocess.run(['git','-c','core.longpaths=true',*args],cwd=root,capture_output=True); assert not check or p.returncode==0,p.stderr.decode('utf-8',errors='replace'); return p
assert git('branch','--show-current').stdout.decode().strip()=='main-OldTeaBag-B168'; assert git('rev-parse','HEAD').stdout.decode().strip()=='16d93c1e8bbbc0296083f85b282e64626db126ab'
source=json.loads((base/'routing-source-final.json').read_text()); sources=[r['after']['path'].replace('\\','/') for r in source]
for r in source: assert hashlib.sha256((root/r['after']['path']).read_bytes()).hexdigest()==r['after']['sha256']
p=root/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md'; b=p.read_bytes(); p.write_bytes(b.replace('负腿4?原首为'.encode(), '负腿原首为'.encode()))
prefixes=[str(base.relative_to(root)),str(target.relative_to(root))]+[str(p.relative_to(root))for p in prior.glob('historical-routing-*')if p.is_dir()]
owned=set(sources+['_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md'])
for prefix in prefixes:
 for args in [('ls-files','--others','--exclude-standard','-z','--',prefix),('diff','--name-only','-z','--',prefix),('diff','--cached','--name-only','-z','--',prefix)]:
  owned.update(v.decode('utf-8')for v in git(*args).stdout.split(b'\0')if v)
paths=sorted(p.replace('\\','/') for p in owned if '/products/' not in p and not p.endswith('.nul'))
already_staged=set(v for v in git('diff','--cached','--name-only').stdout.decode('utf-8').splitlines() if v)
assert already_staged<=set(paths),already_staged-set(paths)
(base/'routing-commit-paths.json').write_text(json.dumps(paths,ensure_ascii=False,indent=2),encoding='utf-8'); paths.append(str((base/'routing-commit-paths.json').relative_to(root)).replace('\\','/'))
pathspec=base/'routing-commit-paths.nul'; pathspec.write_bytes(b''.join(p.encode('utf-8')+b'\0'for p in paths))
(target/'source-status.txt').write_bytes(git('status','--porcelain=v1').stdout)
git('add','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul')
planned=set(paths); staged=set(git('diff','--cached','--name-only','-z').stdout.decode('utf-8').strip('\0').split('\0')); assert staged<=planned,(staged-planned)
commit=git('commit','--only','-m','WIP: persist original node routing at send intent; keep stop contracts open','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul')
(base/'routing-commit.stdout.txt').write_bytes(commit.stdout); (base/'routing-commit.stderr.txt').write_bytes(commit.stderr)
head=git('rev-parse','HEAD').stdout.decode().strip(); actual=git('diff-tree','--no-commit-id','--name-only','-r','-z','HEAD').stdout.decode().strip('\0').split('\0'); assert set(actual)<=planned; assert set(sources)<=set(actual)
for r in source:assert hashlib.sha256((root/r['after']['path']).read_bytes()).hexdigest()==r['after']['sha256']
(target/'source-head.txt').write_text(head+'\n',encoding='utf-8'); (target/'source-status-postcommit.txt').write_bytes(git('status','--porcelain=v1').stdout)
(base/'routing-commit-observation.json').write_text(json.dumps(dict(head=head,branch='main-OldTeaBag-B168',actual_changed_files=actual,source_test_files=sources,actual_changed_file_count=len(actual),source_bytes_unchanged=True,staged=git('diff','--cached','--name-only').stdout.decode().splitlines(),level='actual local WIP checkpoint, not independent acceptance or delivery'),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(head=head,files=len(actual),source_test_files=len(sources))))
