from pathlib import Path
import subprocess,json,hashlib,os,xml.etree.ElementTree as ET
r=Path.cwd();base=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f/final';d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639/final-r2';d.mkdir(exist_ok=False)
products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
paths=[x['path'] for x in json.loads((base/'candidate-observation.json').read_text(encoding='utf-8'))['sources']]+['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs','MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def facts():return [dict(path=p,bytes=len(b:=(r/p).read_bytes()),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')) for p in paths]
before=facts();(d/'source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
def run(name,args,env=None):
 with (d/(name+'.log')).open('wb') as f:code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,env=env,timeout=600).returncode
 (d/(name+'-exit.txt')).write_text(str(code),encoding='utf-8');print(name,code,flush=True);return code
assert run('build',['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])==0
def test(name,flt,env=None):return run(name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+flt,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(d)],env)
env=os.environ.copy();env['BGI_MULTIROUND_EVIDENCE_DIR']=str(d)
assert test('multiround','FullyQualifiedName~OriginalMultiRound_',env)==0
full=test('full','FullyQualifiedName~TaskCenter')
claims=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt';cb=sha(claims);env=os.environ.copy();env['CLAIM_SURFACE_REGENERATE']='1'
assert test('claims-regenerate','FullyQualifiedName~ClaimSurfaceGuardTests',env)==0
target=test('target','FullyQualifiedName~TypedParent_|FullyQualifiedName~TypedAdmissionParentTests|FullyQualifiedName~ArbitrationAdmissionServiceTests|FullyQualifiedName~TaskCenterSuccessorPathGateTests|FullyQualifiedName~TaskCenterHostRecoveryAdmissionTests|FullyQualifiedName~StartupHandoffHostTests|FullyQualifiedName~FacadeWaitLocallyConsumerTests|FullyQualifiedName~ClaimSurfaceGuardTests|FullyQualifiedName~StartupFlowSchemeStoreTests');assert target==0
assert facts()==before
def rows(p):return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
a=rows(base/'full.trx')|rows(base/'target.trx');b=rows(d/'full.trx')|rows(d/'target.trx')
comp=dict(kind='ordinary exact testId/TRX union, not authenticated receipt',baseline_count=len(a),current_count=len(b),added=[dict(id=k,**b[k]) for k in sorted(b.keys()-a.keys())],removed=[dict(id=k,**a[k]) for k in sorted(a.keys()-b.keys())],changed=[dict(id=k,before=a[k],after=b[k]) for k in sorted(a.keys()&b.keys()) if a[k]['outcome']!=b[k]['outcome']],same_failed_ids={k for k in a if a[k]['outcome']=='Failed'}=={k for k in b if b[k]['outcome']=='Failed'},counts={c:sum(v['outcome']==c for v in b.values()) for c in ['Passed','Failed','NotExecuted']},failures=[dict(id=k,**v) for k,v in b.items() if v['outcome']=='Failed'])
(d/'impact-comparison.json').write_text(json.dumps(comp,ensure_ascii=False,indent=2),encoding='utf-8')
obs=dict(kind='ordinary process/TRX/source bytes; not authenticated receipt/independent pass/IPC game User acceptance',sources=before,build_exit=0,full_exit=full,target_exit=target,full_counts={c:sum(v['outcome']==c for v in rows(d/'full.trx').values()) for c in ['Passed','Failed','NotExecuted']},target_counts={c:sum(v['outcome']==c for v in rows(d/'target.trx').values()) for c in ['Passed','Failed','NotExecuted']},claim_before=cb,claim_after=sha(claims),sources_unchanged=True,production_gate_open=False,goal_complete=False)
(d/'candidate-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(obs['full_counts']),json.dumps(comp['counts']),comp['same_failed_ids'],flush=True)
