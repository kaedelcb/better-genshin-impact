import json,subprocess,hashlib
from pathlib import Path
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-owner-wiring-20261005-from-01a107a2'
def git(*args): return subprocess.check_output(['git','-c','core.longpaths=true',*args],cwd=root).decode('utf-8').strip()
assert git('branch','--show-current')=='main-OldTeaBag-B168'
assert not git('diff','--cached','--name-only'), 'Other staged work must be preserved, not included'
source=json.loads((base/'host-source-final-observation.json').read_text(encoding='utf-8'))['source_files']
for r in source: assert hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256']
paths=[r['path'] for r in source]
extensions={'.md','.json','.jsonl','.trx','.log','.py','.txt','.pathspec'}
paths.extend(str(p.relative_to(root)).replace('\\','/') for p in base.rglob('*') if p.is_file() and 'products' not in p.relative_to(base).parts and p.suffix in extensions and p.name not in {'host-checkpoint-paths.json','host-checkpoint.pathspec','host-checkpoint-preview.json','host-commit-observation.json','host-checkpoint-commit.log'})
paths=sorted(set(paths))
pathspec=base/'host-checkpoint.pathspec'
pathspec.write_bytes(b'\0'.join(p.encode('utf-8') for p in paths)+b'\0')
(base/'host-checkpoint-paths.json').write_text(json.dumps(paths,ensure_ascii=False,indent=2),encoding='utf-8')
preview=dict(before_head=git('rev-parse','HEAD'),source_files=len(source),listed_paths=len(paths),outside_staged='empty before checkpoint',scope='9 source/test paths plus this relay own textual process/input/TRX/mutation/metadata evidence; products excluded; historical original relay files remain unchanged')
(base/'host-checkpoint-preview.json').write_text(json.dumps(preview,ensure_ascii=False,indent=2),encoding='utf-8')
paths.extend(str(p.relative_to(root)).replace('\\','/') for p in [pathspec,base/'host-checkpoint-paths.json',base/'host-checkpoint-preview.json'])
pathspec.write_bytes(b'\0'.join(p.encode('utf-8') for p in sorted(set(paths)))+b'\0')
subprocess.check_call(['git','-c','core.longpaths=true','add','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul'],cwd=root,stdout=subprocess.DEVNULL)
staged=set(git('diff','--cached','--name-only').splitlines())
assert staged<=set(paths)
with (base/'host-checkpoint-commit.log').open('w',encoding='utf-8') as log:
    subprocess.check_call(['git','-c','core.longpaths=true','commit','--only','-m','fix: track complete host shutdown and retain missing admission responsibility','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul'],cwd=root,stdout=log,stderr=subprocess.STDOUT)
head=git('rev-parse','HEAD'); actual=set(git('show','--format=','--name-only',head).splitlines())
assert actual==staged,(actual^staged)
assert not git('diff','--cached','--name-only')
for r in source: assert hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256']
observation=dict(head=head,paths=sorted(actual),source_file_count=sum(p in actual for p in [r['path'] for r in source]),total_changed_files=len(actual),staged_after=[],kind='local candidate checkpoint, no independent pass/production or complete delivery',source_bytes_equal=True)
(base/'host-commit-observation.json').write_text(json.dumps(observation,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in observation.items() if k!='paths'}))
