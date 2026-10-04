import json,hashlib,xml.etree.ElementTree as ET
from pathlib import Path
from collections import Counter
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-owner-wiring-20261005-from-01a107a2'
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-owner-fence-20261004-from-01a10731/core-owner-fence-final-r2/final.trx'
final=base/'host-full-final-r2/results.trx'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def results(p):
    rows=[dict(id=r.attrib['testId'],name=r.attrib['testName'],outcome=r.attrib['outcome']) for r in ET.parse(p).findall('.//t:UnitTestResult',ns)]
    assert len(rows)==len({r['id'] for r in rows})
    return {r['id']:r for r in rows}
old=results(prior); new=results(final)
comparison=dict(before=str(prior.relative_to(root)),after=str(final.relative_to(root)),before_sha256=hashlib.sha256(prior.read_bytes()).hexdigest(),after_sha256=hashlib.sha256(final.read_bytes()).hexdigest(),before_counts=dict(Counter(r['outcome'] for r in old.values())),after_counts=dict(Counter(r['outcome'] for r in new.values())),added=[new[k] for k in sorted(new.keys()-old.keys())],removed=[old[k] for k in sorted(old.keys()-new.keys())],changed=[dict(before=old[k],after=new[k]) for k in sorted(old.keys()&new.keys()) if old[k]!=new[k]])
(base/'host-testid-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
paths=[
 'MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',
 'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',
 'MultiplayerHoeingAssistant/ViewModels/MainViewModel.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterHostTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs']
declared=json.loads((base/'host-full-final-r2/source-before.json').read_text(encoding='utf-8'))
after=json.loads((base/'host-full-final-r2/source-after.json').read_text(encoding='utf-8'))
assert declared==after
observed=[]
for rel in paths:
    b=(root/rel).read_bytes(); sha=hashlib.sha256(b).hexdigest()
    assert next(r['sha256'] for r in after if r['path']==rel)==sha
    observed.append(dict(path=rel,sha256=sha,bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),crlf=b'\r\n' in b))
for r in after: assert hashlib.sha256((root/r['path']).read_bytes()).hexdigest()==r['sha256'],r['path']
claim='Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt'
assert any(r['outcome']=='Passed' and 'DesignDocs_ClaimSurface_MatchesReviewedManifest' in r['name'] for r in new.values())
summary=dict(kind='ordinary fresh declared-source/product/process regression and causal P/F/P, not authenticated receipt, complete dependency closure, independent pass or real product acceptance',source_files=observed,full_counts=comparison['after_counts'],added=len(comparison['added']),removed=len(comparison['removed']),changed=len(comparison['changed']),declared_inputs=len(after),declared_inputs_still_equal=True,claim_manifest_sha256=hashlib.sha256((root/claim).read_bytes()).hexdigest(),claim_guard='Passed',production_authorization=False,independent_review_requests_in_this_thread=0,original_owner_budget='implementation2 used1 remaining_max1 plan0; all historical failures retained',owner_wiring='not implemented: Host RunStore still not BindOwner and recovery qualification remains open',scope_limits=['real original Host owner/10s release/15s late cleanup/TTL/B legitimate recovery still pending','known partial expected mappings, old-format missing evidence and legitimate retired mapping stop/reopen need actual causal coverage','SDK/compiler/MSBuild/task/package/native closure remains unknown; old execution receipts are historical','all 98 prior finding keys/36 unknowns and five new original-level obligations remain open or their original dispositions retained, not closed by this candidate','full real IPC/game/User functionality, stop/restart/data preservation and runnable distribution not accepted'])
(base/'host-source-final-observation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(counts=summary['full_counts'],added=summary['added'],removed=summary['removed'],changed=summary['changed'],declared_inputs=len(after),claim_guard='Passed')))
