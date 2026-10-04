from pathlib import Path
import json,subprocess,xml.etree.ElementTree as ET,hashlib
r=Path.cwd();d=Path(__file__).parent;f=d/'final-r2';products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
before=json.loads((f/'source-after.json').read_text()); assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in before)
with (f/'supplement.log').open('wb') as log:
 code=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~StartupFlowSchemeStoreTests','/Logger:trx;LogFileName=supplement.trx','/ResultsDirectory:'+str(f)],stdout=log,stderr=subprocess.STDOUT,timeout=180).returncode
(f/'supplement-exit.txt').write_text(str(code)); assert code==0
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(p): return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
new={}
for p in [f/'full.trx',f/'target.trx',f/'supplement.trx']:
 for k,v in rows(p).items():
  if k not in new or new[k]['outcome']!='Failed':new[k]=v
old={}; previous=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-legacy-seal-source-20261004-from-01a106d9/migration-final'
for p in [previous/'full.trx',previous/'target.trx',previous/'target-r2.trx']:
 for k,v in rows(p).items():
  if k not in old or old[k]['outcome']!='Failed':old[k]=v
comp=dict(kind='complete current exact testId union incl original namespace-external StartupFlowSchemeStore; earlier limited-selection comparison preserved',baseline_count=len(old),current_count=len(new),counts={c:sum(v['outcome']==c for v in new.values()) for c in ['Passed','Failed','NotExecuted']},added=[dict(id=k,**new[k]) for k in sorted(new.keys()-old.keys())],removed=[dict(id=k,**old[k]) for k in sorted(old.keys()-new.keys())],changed=[dict(id=k,before=old[k],after=new[k]) for k in sorted(old.keys()&new.keys()) if old[k]['outcome']!=new[k]['outcome']],failures=[dict(id=k,**v) for k,v in new.items() if v['outcome']=='Failed'])
assert not comp['removed']; assert len(comp['added'])==4
(f/'impact-comparison-complete.json').write_text(json.dumps(comp,ensure_ascii=False,indent=2),encoding='utf-8')
obs=json.loads((f/'candidate-observation.json').read_text()); obs['supplement_counts']={c:sum(v['outcome']==c for v in rows(f/'supplement.trx').values()) for c in ['Passed','Failed','NotExecuted']};obs['supplement_inputs_unchanged']=all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in before);assert obs['supplement_inputs_unchanged']
(f/'candidate-observation-complete.json').write_text(json.dumps(obs,indent=2),encoding='utf-8'); print(json.dumps(comp['counts']),comp['current_count'],flush=True)
