from pathlib import Path
import json,hashlib,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;prev=d.parent/'auto-relay-external-original-causality-20261004-from-01a1067e';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};records=[]
for f in sorted(prev.rglob('*')):
 if not f.is_file() or f.suffix not in ('.json','.trx','.log','.md','.txt'):continue
 b=f.read_bytes();o=dict(path=str(f.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if f.suffix=='.trx':
  rows=ET.fromstring(b).findall('.//t:UnitTestResult',ns);o['counts']={c:sum(x.get('outcome')==c for x in rows) for c in ['Passed','Failed','NotExecuted']};o['failures']=[dict(id=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in rows if x.get('outcome')=='Failed']
 elif f.suffix=='.json':
  try:obj=json.loads(b.decode('utf-8-sig'));o['json_type']=type(obj).__name__;o['keys']=list(obj)[:25] if isinstance(obj,dict) else None
  except (UnicodeError,ValueError) as e:o['parse_error']=str(e)
 else:
  lines=b.decode('utf-8-sig',errors='replace').splitlines();o['tail']=lines[-5:];o['errors']=[x for x in lines if 'error ' in x or '失败!' in x][-8:]
 records.append(o)
(d/'inherited-evidence-read.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8');print('read inherited',len(records),'matrix',sum('/matrix/' in x['path'].replace('\\','/') for x in records),flush=True)
# Reuse command/identity comparison structure only; all executions and source bindings are new.
source=(prev/'read-and-verify.py').read_text(encoding='utf-8');start=source.index("out=d/'final';")
final='from pathlib import Path\nimport subprocess,json,hashlib,os,xml.etree.ElementTree as ET\nr=Path.cwd();d=Path(__file__).parent;prev=d.parent/\'auto-relay-external-original-causality-20261004-from-01a1067e\';ns={\'t\':\'http://microsoft.com/schemas/VisualStudio/TeamTest/2010\'}\n'+source[start:]
needle="paths=[x['path'] for x in json.loads((base/'candidate-observation.json').read_text(encoding='utf-8'))['sources']]"
assert needle in final
final=final.replace(needle,needle+"\npaths+=['Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/HistoricalExecutionObservationTests.cs','MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj']")
final=final.replace("env=os.environ.copy();env['BGI_CAUSAL_MATRIX_DIR']=str(out/'matrix');assert test('causal-matrix','FullyQualifiedName~OriginalLateRound_|FullyQualifiedName~OriginalLedgerCausality_',env)==0", "assert test('history-matrix','FullyQualifiedName~OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure|FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~TerminalReleaseSealTests')==0")
final=final.replace("test('target','FullyQualifiedName~TypedParent_", "test('target','FullyQualifiedName~HistoricalExecutionObservationTests|FullyQualifiedName~TerminalReleaseSealTests|FullyQualifiedName~TypedParent_")
(d/'run-final.py').write_text(final,encoding='utf-8')
