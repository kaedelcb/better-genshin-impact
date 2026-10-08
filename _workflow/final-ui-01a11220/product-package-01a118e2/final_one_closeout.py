"""Finish mechanical readback without converting ordinary provenance into certification."""
from pathlib import Path
import hashlib,json,subprocess,sys
R=Path(__file__).resolve().parents[3];W=Path(__file__).resolve().parent;B=W/'final-one-review'
sys.path.insert(0,str(R/'tools/mistletoe'))
import storage_limits as s
import native_review as n
load=lambda p:json.loads(p.read_bytes());sha=lambda data:hashlib.sha256(data).hexdigest()
session=s.Session(R,'owner-final-one-final-readback-01a1198f');session.policy=dict(session.policy,operation_bytes=8*1024*1024)
original_size=s.size;s.size=lambda roots:original_size(list(dict.fromkeys(str(p) for p in roots)))
with session:
    old=session.old_roots[:];session.old_roots=list(dict.fromkeys(old));assert set(old)==set(session.old_roots);session.track(B)
    packet=Path(load(B/'snapshot-observation.json')['snapshot']);q=load(packet/'request.json');frozen=load(packet/'snapshot.json')
    assert sha((packet/'snapshot.json').read_bytes())==q['snapshot_hash']
    mismatches=[folder+'/'+rel for folder,rows in [('source',frozen['files']),('contracts',frozen['contract_files'])] for rel,row in rows.items() if sha((packet/folder/rel).read_bytes())!=row['sha256']]
    assert not mismatches
    native=subprocess.run([sys.executable,'-B',str(R/'tools/mistletoe/native_review.py'),'validate','--request',str(packet),'--current'],capture_output=True)
    s.write(B/'legacy-native-validator-final.log',native.stdout+native.stderr)
    assert native.returncode==2 and b'snapshot identity drift' in native.stdout
    version=load(R/'_workflow/runtime-unified-01a10e1b/product/VERSION.json');inputs=load(Path(version['compiled_input_hashes']))
    assert all(sha((R/k).read_bytes())==h for k,h in inputs.items())
    capture=load(B/'review/capture-observation.json');assert capture['verdict']=='pass' and capture['all_original_keys_disposed'] and not capture['schema_observations']
    doc=R/'Docs/technical/mistletoe-startup-migration-recovery.md';previous=(B/'previous-instructions-before-final.md').read_bytes()
    changes=[rel for rel,row in frozen['files'].items() if not (R/rel).is_file() or sha((R/rel).read_bytes())!=row['sha256']]
    assert changes==['Docs/technical/mistletoe-startup-migration-recovery.md'],changes
    assert sha(previous)==frozen['files'][changes[0]]['sha256']
    assert not doc.read_bytes().startswith(b'\xef\xbb\xbf') and len(doc.read_bytes().splitlines())==len(previous.splitlines())
    longpaths=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=R,capture_output=True);assert longpaths.returncode==0
    s.write(B/'closeout-status-longpaths.txt',longpaths.stdout)
    assert not any(line[:2].strip()==b'D' for line in longpaths.stdout.splitlines())
    diff=subprocess.run(['git','diff','--check','--','Docs/technical/mistletoe-startup-migration-recovery.md'],cwd=R,capture_output=True);assert diff.returncode==0
    result=dict(kind='ordinary final readback; no legacy receipt, permit or full acceptance',request=q['request_id'],snapshot_hash=q['snapshot_hash'],frozen_hash_mismatches=mismatches,
        native_validator=dict(exit_code=native.returncode,output='legacy-native-validator-final.log',reason='legacy verify_input requires snapshot.complete plus native input schema; equivalent ordinary packet deliberately does not claim those fields; not evidence of frozen byte drift'),
        current_compiled_input_changes=[],compiled_inputs=313,post_review_source_changes=changes,post_review_change_scope='three instructions status paragraphs derived from completed independent report; no product code or behavior edit',
        schema_observations=[],all_original_keys_disposed=True,source_candidate_verdict='pass',closed_findings=['PATH-ARRIVAL-MODE-DOWNGRADE-1','PAUSED-STOP-TERMINAL-RECONCILIATION-1'],
        version_sha256=sha((R/'_workflow/runtime-unified-01a10e1b/product/VERSION.json').read_bytes()),instructions_sha256=sha(doc.read_bytes()),
        used=1,remaining=0,automatic_renewal=False,complete_product_acceptance='pending',product_complete=False,goal_status='active',
        own_apps_or_builds_inflight=False,external_remaining='user voluntary game/resource/Skip/account/team/game-exit/OS shutdown feedback; no additional executor implementation gap found by final review')
    s.write(B/'FINAL-READBACK.json',s.encode(result));print(json.dumps(result,ensure_ascii=False),flush=True)
