from pathlib import Path
import subprocess, json, hashlib, xml.etree.ElementTree as ET
root=Path.cwd(); parent=Path(__file__).parent; out=parent/'migration-final'; out.mkdir(exist_ok=False)
old=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-history-seal-certification-20261004-from-01a106ad/legacy-final'
receipt=json.loads((root/'_workflow/local-wait-admission-gates-20261004/g10-completion/certified-executions/55fcd1ef82ae4a3f97e4d54fb1ce5033/receipt.json').read_text(encoding='utf-8-sig'))
paths=list(receipt['inputs'])+[
    'MultiplayerHoeingAssistant/packages.lock.json',
    'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56RootFactsRefusalTests.cs',
    'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ReopenedMigrationActivationObservationTests.cs']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def facts():
    return [dict(path=p,bytes=len(b:=(root/p).read_bytes()),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),
        bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')) for p in paths]
before=facts(); (out/'source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def run(name,args):
    with (out/(name+'.log')).open('wb') as log:
        code=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=900).returncode
    (out/(name+'-exit.txt')).write_text(str(code),encoding='utf-8'); print(name,code,flush=True); return code
for name,proj in [('build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),
    ('probe-build','Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj')]:
    assert run(name,['dotnet','build',proj,'-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])==0
(out/'product-observation.json').write_text(json.dumps([dict(path=str(p.relative_to(root)),bytes=p.stat().st_size,sha256=sha(p))
    for p in [products/'MultiplayerHoeingAssistant.UnitTest.dll',products/'MultiplayerHoeingAssistant.dll',products/'ControlledWriterProbe.dll']],indent=2),encoding='utf-8')
def test(name,filter):
    return run(name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),
        '/TestCaseFilter:'+filter,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out)])
full=test('full','FullyQualifiedName~TaskCenter')
target=test('target','FullyQualifiedName~LegacyHistoricalSealIntegrityTests|FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~TerminalReleaseSealTests|FullyQualifiedName~TypedParent_|FullyQualifiedName~TypedAdmissionParentTests|FullyQualifiedName~ArbitrationAdmissionServiceTests|FullyQualifiedName~TaskCenterSuccessorPathGateTests|FullyQualifiedName~TaskCenterExternalStartAdmissionTests|FullyQualifiedName~TaskCenterHostRecoveryAdmissionTests|FullyQualifiedName~StartupHandoffHostTests|FullyQualifiedName~FacadeWaitLocallyConsumerTests|FullyQualifiedName~ClaimSurfaceGuardTests|FullyQualifiedName~StartupFlowSchemeStoreTests|FullyQualifiedName~ReopenedMigrationActivationObservationTests|FullyQualifiedName~R56RootFactsRefusalTests')
assert target==0
after=facts(); assert before==after
(out/'source-after.json').write_text(json.dumps(after,indent=2),encoding='utf-8')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(path):
    return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),
        stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(path).findall('.//t:UnitTestResult',ns)}
a=rows(old/'full.trx')|rows(old/'target.trx'); b=rows(out/'full.trx')|rows(out/'target.trx')
comparison=dict(kind='ordinary exact testId comparison, not certification or independent acceptance',baseline_count=len(a),current_count=len(b),
    added=[dict(id=k,**b[k]) for k in sorted(b.keys()-a.keys())],removed=[dict(id=k,**a[k]) for k in sorted(a.keys()-b.keys())],
    changed=[dict(id=k,before=a[k],after=b[k]) for k in sorted(a.keys()&b.keys()) if a[k]['outcome']!=b[k]['outcome']],
    counts={c:sum(v['outcome']==c for v in b.values()) for c in ['Passed','Failed','NotExecuted']},
    failures=[dict(id=k,**v) for k,v in b.items() if v['outcome']=='Failed'])
(out/'impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
observation=dict(kind='ordinary Rebuild/process/TRX/explicit input bytes, not authenticated dependency closure or independent/product acceptance',
    full_exit=full,target_exit=target,sources_unchanged=True,input_count=len(before),
    full_counts={c:sum(v['outcome']==c for v in rows(out/'full.trx').values()) for c in ['Passed','Failed','NotExecuted']},
    target_counts={c:sum(v['outcome']==c for v in rows(out/'target.trx').values()) for c in ['Passed','Failed','NotExecuted']},
    changed_original_inputs=[p for p in receipt['inputs'] if sha(root/p)!=receipt['inputs'][p]],
    production_gate_open=False,goal_complete=False)
(out/'candidate-observation.json').write_text(json.dumps(observation,indent=2),encoding='utf-8')
print(json.dumps(observation['full_counts']),json.dumps(comparison['counts']),flush=True)
