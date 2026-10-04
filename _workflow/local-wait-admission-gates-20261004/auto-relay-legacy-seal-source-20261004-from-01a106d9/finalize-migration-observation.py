from pathlib import Path
import json, hashlib, xml.etree.ElementTree as ET
root=Path.cwd(); parent=Path(__file__).parent; out=parent/'migration-final'
before=json.loads((out/'source-before.json').read_text(encoding='utf-8'))
after=[]
for entry in before:
    p=root/entry['path']; b=p.read_bytes()
    after.append(dict(path=entry['path'],bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),
        bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')))
assert before==after
(out/'source-after.json').write_text(json.dumps(after,indent=2),encoding='utf-8')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(path):
    return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),
        message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns))
        for x in ET.parse(path).findall('.//t:UnitTestResult',ns)}
def counts(data):
    return {kind:sum(x['outcome']==kind for x in data.values()) for kind in ['Passed','Failed','NotExecuted']}
original=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-history-seal-certification-20261004-from-01a106ad/legacy-final'
baseline=rows(original/'full.trx')|rows(original/'target.trx')
executions={name:rows(path) for name,path in [('full',out/'full.trx'),('target',out/'target.trx'),
    ('target-r2',out/'target-r2.trx'),('multiround-repeat',parent/'multiround-current-repeat.trx')]}
all_rows={}
for name,data in executions.items():
    for test_id,row in data.items(): all_rows.setdefault(test_id,[]).append(dict(execution=name,**row))
rank={'Failed':2,'NotExecuted':1,'Passed':0}
worst={key:max(values,key=lambda x:rank[x['outcome']]) for key,values in all_rows.items()}
comparison=dict(kind='conservative exact testId union; any failure retained, repeat never erases earlier failure',
    baseline_count=len(baseline),current_count=len(worst),counts=counts(worst),
    added=[dict(id=k,**worst[k]) for k in sorted(worst.keys()-baseline.keys())],
    removed=[dict(id=k,**baseline[k]) for k in sorted(baseline.keys()-worst.keys())],
    changed=[dict(id=k,before=baseline[k],after=worst[k]) for k in sorted(worst.keys()&baseline.keys()) if baseline[k]['outcome']!=worst[k]['outcome']],
    failures=[dict(id=k,executions=all_rows[k]) for k,v in worst.items() if v['outcome']=='Failed'])
(out/'impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
inherited=json.loads((root/'_workflow/local-wait-admission-gates-20261004/g10-completion/certified-executions/55fcd1ef82ae4a3f97e4d54fb1ce5033/receipt.json').read_text(encoding='utf-8-sig'))['inputs']
observation=dict(kind='ordinary complete TRX/source/product bytes; not authenticated provenance, independent pass or product acceptance',
    sources_unchanged=True,input_count=len(before),execution_counts={name:counts(data) for name,data in executions.items()},
    full_exit=1,target_exit=1,target_repeat_exit=0,multiround_repeat_exit=0,
    changed_original_inputs=[entry['path'] for entry in before if entry['path'] in inherited and entry['sha256']!=inherited[entry['path']]],
    claim_sha256=hashlib.sha256((root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt').read_bytes()).hexdigest(),
    new_observed_failure='OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest(handoff true, valid): original target failed seal assertion; full/same-filter repeat/16-case isolated passed; cause unknown and open',
    production_gate_open=False,goal_complete=False,new_independent_requests=0)
(out/'candidate-observation.json').write_text(json.dumps(observation,indent=2),encoding='utf-8')
print(json.dumps(dict(counts=observation['execution_counts'],conservative_counts=comparison['counts'],
    added=len(comparison['added']),removed=len(comparison['removed']),changed=len(comparison['changed']),input_count=len(before),
    original_inputs_changed=observation['changed_original_inputs'])))
