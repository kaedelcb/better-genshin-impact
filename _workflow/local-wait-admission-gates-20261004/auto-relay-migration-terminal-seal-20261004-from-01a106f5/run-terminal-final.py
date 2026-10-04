from pathlib import Path
import json,hashlib,subprocess,xml.etree.ElementTree as ET
root=Path.cwd(); parent=Path(__file__).parent; out=parent/'final-r2'; out.mkdir(exist_ok=False)
previous=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-legacy-seal-source-20261004-from-01a106d9/migration-final'
paths=[r['path'] for r in json.loads((previous/'source-after.json').read_text(encoding='utf-8-sig'))]
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def facts():
 return [dict(path=p,bytes=len(b:=(root/p).read_bytes()),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')) for p in paths]
before=facts(); (out/'source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
def run(name,args):
 with (out/(name+'.log')).open('wb') as log: code=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=900).returncode
 (out/(name+'-exit.txt')).write_text(str(code)); print(name,code,flush=True); return code
for name,project in [('build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),('probe-build','Test/R56ControlledWriterProbe/R56ControlledWriterProbe.csproj')]:
 assert run(name,['dotnet','build',project,'-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])==0
for name,filter in [('full','FullyQualifiedName~TaskCenter'),('target','FullyQualifiedName~TaskCenterSuccessorPathGateTests|FullyQualifiedName~TaskCenterHostAdmissionTests|FullyQualifiedName~TaskCenterHostRecoveryAdmissionTests|FullyQualifiedName~LocalWaitFinalizationContractTests|FullyQualifiedName~TerminalReleaseSealTests|FullyQualifiedName~LegacyHistoricalSealIntegrityTests|FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~ArbitrationAdmissionServiceTests|FullyQualifiedName~StartupHandoffHostTests|FullyQualifiedName~ClaimSurfaceGuardTests')]:
 run(name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out)])
after=facts(); assert before==after; (out/'source-after.json').write_text(json.dumps(after,indent=2),encoding='utf-8')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(p):
 return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
def union(ps):
 result={}
 for p in ps:
  for k,v in rows(p).items():
   if k not in result or result[k]['outcome']!='Failed': result[k]=v
 return result
old=union([previous/n for n in ['full.trx','target.trx','target-r2.trx']])
new=union([out/'full.trx',out/'target.trx'])
comparison=dict(kind='current exact testId union; any failure retained across same-version repeats; original prior failures kept separately',baseline_count=len(old),current_count=len(new),counts={c:sum(v['outcome']==c for v in new.values()) for c in ['Passed','Failed','NotExecuted']},added=[dict(id=k,**new[k]) for k in sorted(new.keys()-old.keys())],removed=[dict(id=k,**old[k]) for k in sorted(old.keys()-new.keys())],changed=[dict(id=k,before=old[k],after=new[k]) for k in sorted(old.keys()&new.keys()) if old[k]['outcome']!=new[k]['outcome']],failures=[dict(id=k,**v) for k,v in new.items() if v['outcome']=='Failed'])
(out/'impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
oldfacts={r['path']:r for r in json.loads((previous/'source-after.json').read_text(encoding='utf-8-sig'))}
obs=dict(kind='ordinary sources/products/process/TRX, not authenticated receipt, independent pass or product acceptance',inputs=len(before),sources_unchanged=True,changed_inputs=[r['path'] for r in before if r['sha256']!=oldfacts[r['path']]['sha256']],full_counts={c:sum(v['outcome']==c for v in rows(out/'full.trx').values()) for c in ['Passed','Failed','NotExecuted']},target_counts={c:sum(v['outcome']==c for v in rows(out/'target.trx').values()) for c in ['Passed','Failed','NotExecuted']},products=[dict(path=str(p.relative_to(root)),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [products/'MultiplayerHoeingAssistant.dll',products/'MultiplayerHoeingAssistant.UnitTest.dll',products/'ControlledWriterProbe.dll']],production_gate_open=False,goal_complete=False)
(out/'candidate-observation.json').write_text(json.dumps(obs,indent=2),encoding='utf-8'); print(json.dumps(obs['full_counts']),json.dumps(comparison['counts']),flush=True)
