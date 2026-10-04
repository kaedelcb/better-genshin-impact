from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=Path(__file__).parent
scope=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs',str(d.relative_to(r))]
def git(*args):return subprocess.run(['git','-c','core.longpaths=true',*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
assert git('diff','--cached','--name-only','-z').stdout==b''
before=git('rev-parse','HEAD').stdout.decode().strip(); status=git('status','--porcelain=v1','--untracked-files=all','-z','--',*scope).stdout
paths=[]
for row in status.split(b'\0'):
 if not row:continue
 assert row[:1] not in (b'R',b'C'); p=row[3:].decode();assert not any(x in ('bin','obj','User','products') for x in Path(p).parts);paths.append(p)
sha={p:hashlib.sha256((r/p).read_bytes()).hexdigest() for p in paths}; spec=d/'candidate-paths.nul';spec.write_bytes(b''.join(p.encode()+b'\0' for p in paths))
(d/'candidate-commit-preview.json').write_text(json.dumps(dict(opening=before,paths=paths,sha256=sha,candidate_only=True,attribution='two focused Host products and additive deterministic tests; relay evidence includes source parent handshake confirmation; original failures and comprehensive obligations remain open'),indent=2),encoding='utf-8')
git('add','--pathspec-from-file='+str(spec),'--pathspec-file-nul')
c=git('commit','--only','-m','fix: track terminal writeback through host shutdown without blocking reruns','--pathspec-from-file='+str(spec),'--pathspec-file-nul');(d/'candidate-commit.log').write_bytes(c.stdout+c.stderr)
head=git('rev-parse','HEAD').stdout.decode().strip();actual=[p.decode() for p in git('diff-tree','--no-commit-id','--name-only','-r','-z',head).stdout.split(b'\0') if p];assert set(actual)==set(paths);assert sha=={p:hashlib.sha256((r/p).read_bytes()).hexdigest() for p in paths};assert git('diff','--cached','--name-only','-z').stdout==b''
(d/'candidate-commit-observation.json').write_text(json.dumps(dict(head=head,opening=before,actual_paths=actual,actual_paths_match=True,source_bytes_unchanged=True,staging_empty=True,candidate_only=True,goal_complete=False),indent=2),encoding='utf-8');(d/'status-after-candidate.txt').write_bytes(git('status','--porcelain=v1').stdout);print(json.dumps(dict(head=head,files=len(actual),staging_empty=True,candidate_only=True)))
