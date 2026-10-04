import json,hashlib,collections,xml.etree.ElementTree as ET
from pathlib import Path
root=Path.cwd()
base=Path(__file__).parent
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def load(p): return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def rows(p):
    return {r.attrib['testId']:dict(id=r.attrib['testId'],name=r.attrib['testName'],outcome=r.attrib['outcome'],duration=r.attrib.get('duration'),error=r.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for r in ET.parse(p).findall('.//t:UnitTestResult',ns)}
def save(name,data): (base/name).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
out=base/'stop-integrity-final-r3'
currentPath=out/'results.trx'
priorPath=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-host-writer-qualified-20261005-from-01a107e1/qualified-final-candidate-r5/results.trx'
current=rows(currentPath); prior=rows(priorPath)
counts=dict(collections.Counter(r['outcome'] for r in current.values()))
added=[current[k] for k in sorted(current.keys()-prior.keys())]
deleted=[prior[k] for k in sorted(prior.keys()-current.keys())]
changed=[dict(before=prior[k],after=current[k]) for k in prior.keys()&current.keys() if (prior[k]['name'],prior[k]['outcome'])!=(current[k]['name'],current[k]['outcome'])]
save('stop-testid-comparison.json',dict(level='ordinary original TRX comparison, not authenticated receipt',baseline=priorPath.relative_to(root).as_posix(),current=currentPath.relative_to(root).as_posix(),baseline_sha256=sha(priorPath),current_sha256=sha(currentPath),counts=counts,added=added,deleted=deleted,shared_changes=changed))
assert len(added)==11 and not deleted and not changed,(len(added),deleted,changed)
assert counts=={'Passed':2136,'Failed':2,'NotExecuted':2},counts
assert all(r['outcome']=='Passed' for r in added)
assert all('UnreadableAdmissionStateIsNotTreatedAsNoMapping' in r['name'] and 'Expected: Cancelled' in r['error'] for r in current.values() if r['outcome']=='Failed')
assert load(out/'source-before.json')==load(out/'source-after.json')
after=load(out/'source-after.json')
assert all(sha(root/r['path'])==r['sha256'] for r in after),'post-execution input drift'
pfpPath=base/'host-pfp-partial-original-mapping-r3-legacy-missing-original-r3-parked-observer-terminal-race-r3-observation.json'
pfps=load(pfpPath)['records']
for mutation in sorted({r['mutation'] for r in pfps}):
    group={r['leg']:r for r in pfps if r['mutation']==mutation}
    assert set(group)=={'baseline','negative','source-restoration','restored'}
    for leg in ['baseline','restored']:
        assert group[leg]['exit']==0 and all(r['outcome']=='Passed' for r in group[leg]['results'])
        assert load(root/group[leg]['directory']/'source-before.json')==after
    assert group['negative']['exit']!=0 and any(r['outcome']=='Failed' for r in group['negative']['results'])
    assert group['baseline']['source_sha256']==group['restored']['source_sha256']==group['source-restoration']['restored_sha256']==sha(root/group['restored']['source'])
selected=[r for r in after if r['path'] in ['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs']]
save('stop-source-final.json',selected)
proofs=[r for r in current.values() if any(part in r['name'] for part in ['DesignDocs_ClaimSurface','UnknownOwnerStop_Preserves','TerminalStop_PartialOriginalMappingLoss','TerminalStop_LegacyParkedRun','TerminalStop_LegitimateTombstone','ParkedDriveCompletion_DoesNot','HostOwner_ActualShutdownBudgetSuccessorRecoveryRejectsLateOriginalWriter','WriteExhaustionProcessesSibling'])]
assert any('DesignDocs_ClaimSurface' in r['name'] and r['outcome']=='Passed' for r in proofs)
save('stop-final-observation.json',dict(level='ordinary source/process/product/TRX/PFP; not authenticated receipt, full dependency closure, independent pass or real product delivery',declared_inputs=len(after),execution_inputs_unchanged=True,after_all_mutations_restored=True,source_after_all_mutations=after,counts=counts,added=len(added),deleted=0,shared_name_outcome_changes=0,critical_current_proofs=proofs,pfp_report=pfpPath.relative_to(root).as_posix(),pfp_sha256=sha(pfpPath),findings='all original obligations and CORRUPT-STOP-OWNER-CONTRACT-1 remain original-level open',independent_requests_this_chat=0,remaining_implementation_requests=1,production_authorization=False))
print(json.dumps(dict(counts=counts,added=len(added),deleted=0,shared_changes=0,declared_inputs=len(after),current_pfpmutations=3),ensure_ascii=False))
