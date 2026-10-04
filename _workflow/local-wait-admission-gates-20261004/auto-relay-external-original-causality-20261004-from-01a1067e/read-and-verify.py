from pathlib import Path
import subprocess,json,hashlib,os,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;prev=d.parent/'auto-relay-late-round-20261004-from-01a10658';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=[]
for f in sorted(prev.rglob('*')):
 if not f.is_file() or f.suffix not in ('.json','.trx','.log','.md'):continue
 b=f.read_bytes();o=dict(path=str(f.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if f.suffix=='.trx':
  rows=ET.fromstring(b).findall('.//t:UnitTestResult',ns);o['counts']={c:sum(x.get('outcome')==c for x in rows) for c in ['Passed','Failed','NotExecuted']};o['failures']=[dict(id=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in rows if x.get('outcome')=='Failed']
 elif f.suffix=='.json':
  try:o['json_type']=type(json.loads(b.decode('utf-8-sig'))).__name__
  except (UnicodeError,ValueError) as e:o['parse_error']=str(e)
 else:
  lines=b.decode('utf-8',errors='replace').splitlines();o['tail']=lines[-5:];o['errors']=[x for x in lines if 'error ' in x or '失败!' in x][-8:]
 records.append(o)
(d/'inherited-evidence-read.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8');print('read inherited',len(records),flush=True)
out=d/'final';out.mkdir(exist_ok=False);products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';base=prev/'final'
paths=[x['path'] for x in json.loads((base/'candidate-observation.json').read_text(encoding='utf-8'))['sources']]
def facts():return [dict(path=p,bytes=len(b:=(r/p).read_bytes()),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')) for p in paths]
before=facts();(out/'source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
def run(name,args,env=None):
 with (out/(name+'.log')).open('wb') as f:code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,env=env,timeout=600).returncode
 (out/(name+'-exit.txt')).write_text(str(code),encoding='utf-8');print(name,code,flush=True);return code
assert run('build',['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])==0
def test(name,flt,env=None):return run(name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+flt,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out.resolve())],env)
env=os.environ.copy();env['BGI_CAUSAL_MATRIX_DIR']=str(out/'matrix');assert test('causal-matrix','FullyQualifiedName~OriginalLateRound_|FullyQualifiedName~OriginalLedgerCausality_',env)==0
full=test('full','FullyQualifiedName~TaskCenter')
claims=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();cb=sha(claims);env=os.environ.copy();env['CLAIM_SURFACE_REGENERATE']='1';assert test('claims-regenerate','FullyQualifiedName~ClaimSurfaceGuardTests',env)==0
target=test('target','FullyQualifiedName~TypedParent_|FullyQualifiedName~TypedAdmissionParentTests|FullyQualifiedName~ArbitrationAdmissionServiceTests|FullyQualifiedName~TaskCenterSuccessorPathGateTests|FullyQualifiedName~TaskCenterExternalStartAdmissionTests|FullyQualifiedName~TaskCenterHostRecoveryAdmissionTests|FullyQualifiedName~StartupHandoffHostTests|FullyQualifiedName~FacadeWaitLocallyConsumerTests|FullyQualifiedName~ClaimSurfaceGuardTests|FullyQualifiedName~StartupFlowSchemeStoreTests');assert target==0;assert facts()==before
def rows(p):return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
a=rows(base/'full.trx')|rows(base/'target.trx');b=rows(out/'full.trx')|rows(out/'target.trx');comp=dict(kind='ordinary exact testId union, not certified',baseline_count=len(a),current_count=len(b),added=[dict(id=k,**b[k]) for k in sorted(b.keys()-a.keys())],removed=[dict(id=k,**a[k]) for k in sorted(a.keys()-b.keys())],changed=[dict(id=k,before=a[k],after=b[k]) for k in sorted(a.keys()&b.keys()) if a[k]['outcome']!=b[k]['outcome']],same_failed_ids={k for k in a if a[k]['outcome']=='Failed'}=={k for k in b if b[k]['outcome']=='Failed'},counts={c:sum(v['outcome']==c for v in b.values()) for c in ['Passed','Failed','NotExecuted']},failures=[dict(id=k,**v) for k,v in b.items() if v['outcome']=='Failed'])
(out/'impact-comparison.json').write_text(json.dumps(comp,ensure_ascii=False,indent=2),encoding='utf-8');obs=dict(kind='ordinary process/TRX/source byte observation; not certification/independent pass/IPC game User acceptance',sources=before,build_exit=0,full_exit=full,target_exit=target,full_counts={c:sum(v['outcome']==c for v in rows(out/'full.trx').values()) for c in ['Passed','Failed','NotExecuted']},target_counts={c:sum(v['outcome']==c for v in rows(out/'target.trx').values()) for c in ['Passed','Failed','NotExecuted']},claim_before=cb,claim_after=sha(claims),sources_unchanged=True,matrix_files=len(list((out/'matrix').glob('*.json'))),production_gate_open=False,goal_complete=False);(out/'candidate-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(obs['full_counts']),json.dumps(comp['counts']),comp['same_failed_ids'],obs['matrix_files'],flush=True)
