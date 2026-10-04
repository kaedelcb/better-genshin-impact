from pathlib import Path
import subprocess, json, hashlib
root=Path.cwd(); out=Path(__file__).parent
manifest=json.loads((root/'_workflow/r56-reference-activation-wiring-2026-09-29/manifest.json').read_text(encoding='utf-8-sig'))
scope=[p for p in manifest['sources'] if not p.endswith('.md') and not p.endswith('ClaimSurfaceManifest.txt')]
scope+=['Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56RootFactsRefusalTests.cs',
    'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ReopenedMigrationActivationObservationTests.cs',str(out.relative_to(root))]
def git(*args):
    return subprocess.run(['git','-c','core.longpaths=true',*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
assert git('diff','--cached','--name-only','-z').stdout==b''
opening=git('rev-parse','HEAD').stdout.decode().strip()
status=git('status','--porcelain=v1','--untracked-files=all','-z','--',*scope).stdout
paths=[]
for row in status.split(b'\0'):
    if not row: continue
    assert row[:1] not in (b'R',b'C')
    path=row[3:].decode('utf-8'); assert not any(part in ('bin','obj','User','products') for part in Path(path).parts)
    paths.append(path)
assert paths
before={p:hashlib.sha256((root/p).read_bytes()).hexdigest() for p in paths}
pathspec=out/'migration-checkpoint-paths.nul'
pathspec.write_bytes(b''.join(p.encode('utf-8')+b'\0' for p in paths))
preview=dict(opening=opening,paths=paths,worktree_sha256=before,
    attribution='Inherited original R56 manifest dependencies plus two focused product edits/two new tests and this relay evidence; candidate only, original four failures and new intermittent failure remain open')
(out/'migration-checkpoint-preview.json').write_text(json.dumps(preview,indent=2),encoding='utf-8')
git('add','--pathspec-from-file='+str(pathspec),'--pathspec-file-nul')
commit=git('commit','--only','-m','fix: contain root facts and preserve read-only activation observation candidates',
    '--pathspec-from-file='+str(pathspec),'--pathspec-file-nul')
(out/'migration-checkpoint-commit.log').write_bytes(commit.stdout+commit.stderr)
head=git('rev-parse','HEAD').stdout.decode().strip()
actual=[p.decode('utf-8') for p in git('diff-tree','--no-commit-id','--name-only','-r','-z',head).stdout.split(b'\0') if p]
assert set(actual)==set(paths), (len(actual),len(paths))
assert before=={p:hashlib.sha256((root/p).read_bytes()).hexdigest() for p in paths}
assert git('diff','--cached','--name-only','-z').stdout==b''
(out/'migration-checkpoint-observation.json').write_text(json.dumps(dict(head=head,opening=opening,actual_paths=actual,
    actual_paths_match=True,worktree_bytes_unchanged=True,staging_empty=True,candidate_only=True,goal_complete=False),indent=2),encoding='utf-8')
(out/'status-after-migration-checkpoint.txt').write_bytes(git('status','--porcelain=v1').stdout)
print(json.dumps(dict(head=head,files=len(paths),actual_paths_match=True,staging_empty=True,candidate_only=True)))
