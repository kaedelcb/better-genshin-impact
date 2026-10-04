import json,hashlib,collections,xml.etree.ElementTree as ET
from pathlib import Path
root=Path.cwd();base=Path(__file__).parent;ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def rows(p):
    tree=ET.parse(p);result=[dict(id=r.get('testId'),name=r.get('testName'),outcome=r.get('outcome'),duration=r.get('duration'),error=r.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for r in tree.findall('.//t:UnitTestResult',ns)]
    assert len({r['id'] for r in result})==len(result)
    return {r['id']:r for r in result}
out=base/'node-source-final-r2';currentPath=out/'results.trx';priorPath=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-stop-integrity-20261005-from-01a1081b/stop-integrity-final-r3/results.trx'
cur=rows(currentPath);prior=rows(priorPath);counts=dict(collections.Counter(r['outcome'] for r in cur.values()))
added=[cur[k] for k in sorted(cur.keys()-prior.keys())];deleted=[prior[k] for k in sorted(prior.keys()-cur.keys())];changed=[dict(before=prior[k],after=cur[k]) for k in prior.keys()&cur.keys() if (prior[k]['name'],prior[k]['outcome'])!=(cur[k]['name'],cur[k]['outcome'])]
comparison=dict(level='ordinary actual original TRX comparison; not authenticated receipt',baseline=priorPath.relative_to(root).as_posix(),current=currentPath.relative_to(root).as_posix(),baseline_sha256=sha(priorPath),current_sha256=sha(currentPath),counts=counts,added=added,deleted=deleted,shared_changes=changed)
(base/'node-testid-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
assert len(added)==4 and not deleted and not changed,(len(added),deleted,changed)
assert counts=={'Passed':2140,'Failed':2,'NotExecuted':2},counts
assert all(r['outcome']=='Passed' for r in added)
assert all('UnreadableAdmissionStateIsNotTreatedAsNoMapping' in r['name'] and 'Expected: Cancelled' in r['error'] for r in cur.values() if r['outcome']=='Failed')
after=load(out/'source-after.json');assert load(out/'source-before.json')==after
assert all(sha(root/r['path'])==r['sha256'] for r in after)
pfps=load(base/'node-pfp-r2-observation.json')['records']
for name in {r['mutation'] for r in pfps}:
    group={r['leg']:r for r in pfps if r['mutation']==name};assert set(group)=={'baseline','negative','source-restoration','restored'}
    for leg in ['baseline','restored']:
        assert group[leg]['exit']==0 and all(r['outcome']=='Passed' for r in group[leg]['results'])
        assert load(root/group[leg]['directory']/'source-before.json')==after
    assert group['negative']['exit']==1 and any(r['outcome']=='Failed' and 'Expected: Unavailable' in r['error'] and 'Actual:   Effective' in r['error'] for r in group['negative']['results'])
    assert group['baseline']['source_sha256']==group['restored']['source_sha256']==group['source-restoration']['restored_sha256']==sha(root/group['restored']['source'])
selected=[r for r in after if r['path'] in ['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']]
(base/'node-source-final.json').write_text(json.dumps(selected,indent=2),encoding='utf-8')
proofs=[r for r in cur.values() if any(p in r['name'] for p in ['ClaimSurface','OriginalTerminalStop_MissingOrChanged','LegacyTerminalStop_AllNewAnchors','PreparedStop_RealRunner','DeliveryFence_StopAfterPrepare','HostOwner_ActualShutdownBudget','OriginalHost_RunnerRecovery'])]
assert any('ClaimSurface' in r['name'] and r['outcome']=='Passed' for r in proofs)
obs=dict(level='ordinary source/process/product/TRX/PFP, not authenticated receipt/full dependency closure/independent pass/real product delivery',declared_inputs=len(after),execution_inputs_unchanged=True,after_all_mutations_restored=True,source_after_all_mutations=after,counts=counts,added=4,deleted=0,shared_name_outcome_changes=0,critical_proofs=proofs,independent_requests_this_chat=0,remaining_implementation_requests=1,production_authorization=False,remaining='Original stop eligibility contract conflict, historical node-admission mode of legacy records with every anchor absent, complete stop/permit/history/terminal interleaving + real port and full budget/TTL matrix, full source dependency certification, comprehensive review and all original functionality real runtime delivery remain open.')
(base/'node-final-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(counts=counts,added=4,deleted=0,shared_changes=0,declared_inputs=len(after),current_pfp=2)))
