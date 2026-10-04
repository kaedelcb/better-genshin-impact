from pathlib import Path
import json,xml.etree.ElementTree as ET
d=Path(__file__).parent;ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(p):return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
base=d.parent/'auto-relay-external-original-causality-20261004-from-01a1067e'/'final'
records=[]
for name in ['final','final-r2','final-r3']:
 folder=d/name
 if not (folder/'full.trx').exists() or not (folder/'target.trx').exists():continue
 f=rows(folder/'full.trx');t=rows(folder/'target.trx');b=rows(base/'full.trx')
 records.append(dict(execution=name,counts={v:sum(x['outcome']==v for x in f.values()) for v in ['Passed','Failed','NotExecuted']},full_failed=[dict(id=k,**v) for k,v in f.items() if v['outcome']=='Failed'],added_full=sorted(f.keys()-b.keys()),removed_full=sorted(b.keys()-f.keys()),changed_full=[dict(id=k,before=b[k],after=f[k]) for k in f.keys()&b.keys() if f[k]['outcome']!=b[k]['outcome']],same_full_failed_ids={k for k,v in f.items() if v['outcome']=='Failed'}=={k for k,v in b.items() if v['outcome']=='Failed'},repeat_outcome_differences=[dict(id=k,full=f[k],target=t[k]) for k in f.keys()&t.keys() if f[k]['outcome']!=t[k]['outcome']]))
(d/'per-execution-comparison.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
for v in records:print(v['execution'],v['counts'],'same full failures',v['same_full_failed_ids'],'repeated changes',len(v['repeat_outcome_differences']),flush=True)
