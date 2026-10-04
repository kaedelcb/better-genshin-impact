from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();d=Path(__file__).parent;pf=d/'checkpoint-paths.nul'
assert subprocess.check_output(['git','rev-parse','HEAD']).decode().strip()=='eb50b8716594258d4bc66ed19fd789737db7838f'
assert not subprocess.check_output(['git','diff','--cached','--name-only']).strip()
paths=[x.decode('utf-8') for x in pf.read_bytes().split(b'\0') if x]
paths+= [str(p.relative_to(r)).replace('\\','/') for p in d.iterdir() if p.is_file() and p.name!='checkpoint-paths.nul']
paths=sorted(set(paths));pf.write_bytes(b'\0'.join(x.encode('utf-8') for x in paths)+b'\0')
# The previous failed index operation left HEAD and staging untouched. Enable long path support for these invocations only.
with (d/'checkpoint-add-recovery.log').open('wb') as f: add=subprocess.run(['git','-c','core.longpaths=true','add','-f','--pathspec-from-file='+str(pf),'--pathspec-file-nul'],stdout=f,stderr=subprocess.STDOUT)
assert add.returncode==0
with (d/'checkpoint-commit.log').open('wb') as f: commit=subprocess.run(['git','-c','core.longpaths=true','commit','--only','-m','fix: validate legacy historical run seals and capture execution provenance','--pathspec-from-file='+str(pf),'--pathspec-file-nul'],stdout=f,stderr=subprocess.STDOUT)
assert commit.returncode==0
head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip();actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head]).decode('utf-8').splitlines()
owned={'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LegacyHistoricalSealIntegrityTests.cs'}
assert owned<=set(actual)<=set(paths)
assert not subprocess.check_output(['git','diff','--cached','--name-only']).strip()
source=hashlib.sha256((r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs').read_bytes()).hexdigest();assert source=='1bd71a8ef2902425be038fe630030709b27e989801dbc346d9c3b6a14576e084'
(d/'candidate-commit-observation.json').write_text(json.dumps(dict(commit=head,actual_paths=actual,actual_changed_files=len(actual),product_source_files=1,new_test_source_files=1,source_sha256=source,staged_after='',long_path_recovery='invocation-local core.longpaths=true; no persistent configuration change',git_blob_byte_equivalence_claimed=False,goal_complete=False,production_gate_open=False),ensure_ascii=False,indent=2),encoding='utf-8')
(d/'git-status-after-checkpoint.txt').write_bytes(subprocess.check_output(['git','status','--porcelain']))
print(head,len(actual),'explicit files; 1 product source + 1 new test, remaining evidence/metadata')
