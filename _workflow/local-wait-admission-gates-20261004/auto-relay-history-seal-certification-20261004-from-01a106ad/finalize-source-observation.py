from pathlib import Path
import sys,json,hashlib,xml.etree.ElementTree as ET
r=Path.cwd(); d=Path(__file__).parent; sys.path.insert(0,str(r/'tools/mistletoe'))
import execution_evidence as e
from review_support import Blocked
rel='_workflow/local-wait-admission-gates-20261004/g10-completion/certified-executions/55fcd1ef82ae4a3f97e4d54fb1ce5033/receipt.json'
receipt=json.loads((r/rel).read_text(encoding='utf-8')); e.validate(r,rel)
try:
    e.validate(r,rel,purpose='current_regression')
    green_error=None
except Blocked as ex: green_error=str(ex)
assert green_error and receipt['exit_codes']['run']==1
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(p): return {x.get('testId'):dict(name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns)}
ordinary=rows(d/'legacy-final/full.trx'); current=rows((r/rel).parent/'full.trx')
assert ordinary.keys()==current.keys() and all(ordinary[k]['outcome']==current[k]['outcome'] for k in current)
p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs'; before=(d/'RunStoreTerminalRelease.before.cs').read_bytes(); after=p.read_bytes()
def facts(b): return dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n'))
source=facts(after); assert receipt['inputs']['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs']==source['sha256']
obs=dict(kind='authenticated complete process/input/product/result provenance; failed regression, not green certification or independent/product acceptance',receipt=rel,receipt_sha256=hashlib.sha256((r/rel).read_bytes()).hexdigest(),provenance_validation='passed',green_validation='rejected',green_error=green_error,build_exit=receipt['exit_codes']['build'],run_exit=receipt['exit_codes']['run'],input_count=len(receipt['inputs']),product_count=len(receipt['products']),same_test_ids_and_results_as_ordinary_execution=True,counts={c:sum(v['outcome']==c for v in current.values()) for c in ['Passed','Failed','NotExecuted']},failures=[dict(id=k,**v) for k,v in current.items() if v['outcome']=='Failed'],source_before=facts(before),source_after=source,limitations=['Source inputs are explicit current C#/XAML/projects/targets; package lock and SDK/MSBuild imports are not separately in authenticated inputs. Do not claim exhaustive dependency certification.','Full regression has original six failures; no current green proof or implementation obligation closure.','No actual game/User/IPC product acceptance; ordinary P/F/P remains unauthenticated by execution_evidence.'],production_gate_open=False,goal_complete=False,new_independent_requests=0)
(d/'authenticated-source-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:obs[k] for k in ['provenance_validation','green_validation','input_count','product_count','counts','same_test_ids_and_results_as_ordinary_execution']}))
