from pathlib import Path
import json,hashlib,subprocess,os,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
svc=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';host=r/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs';records=[]
def build(out,name):
 with (out/(name+'.log')).open('wb') as f: code=subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],stdout=f,stderr=subprocess.STDOUT,timeout=180).returncode
 print(out.name,name,code,flush=True);return code
def test(out,name):
 with (out/(name+'.log')).open('wb') as f:code=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalLateRound_|FullyQualifiedName~OriginalLedgerCausality_','/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out.resolve())],stdout=f,stderr=subprocess.STDOUT,timeout=120).returncode
 print(out.name,name,code,flush=True);return code
def failures(p):return [dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(p).findall('.//t:UnitTestResult',ns) if x.get('outcome')=='Failed']
def projection(b):
 x=b'                                TerminalKind: e.TerminalKind,\r\n                                CandidateId: e.CandidateId, ResourceRef: e.ResourceRef,\r\n                                ActionId: e.ActionId, TargetBgiEpoch: e.TargetBgiEpoch))';assert b.count(x)==1
 return b.replace(x,b'                                TerminalKind: e.TerminalKind))')
def validation(b):
 start=b.index(b'    private static bool ExternalStartLedgerCausalityMatches(');end=b.index(b'    private bool SettledArchivedTerminalReplayMatches(',start)
 return b[:start]+b'    private static bool ExternalStartLedgerCausalityMatches(OperationRecord op, TakeoverLedgerFact fact)\r\n        => op.OperationType == OperationType.ExternalStart && fact.OperationType == OperationType.ExternalStart;\r\n\r\n'+b[end:]
def grouping(b):
 for field in ['CandidateId','ResourceRef','ActionId','TargetBgiEpoch']:
  x=f'                           || !string.Equals(agg.SourceFact.{field}, fact.{field}, StringComparison.Ordinal)\r\n'.encode();assert b.count(x)==1;b=b.replace(x,b'')
 return b
for name,path,mutate,assertion in [('M1-host-projection',host,projection,'HistoricalAcceptanceReceiptsHeld'),('M2-original-match',svc,validation,'original ledger causality late-receipt/candidateId'),('M3-group-conflict',svc,grouping,'original ledger duplicate causality')]:
 out=d/'mutations'/name;out.mkdir(parents=True,exist_ok=False);original=path.read_bytes();sha=lambda b:hashlib.sha256(b).hexdigest();o=dict(id=name,source=str(path.relative_to(r)),original_sha256=sha(original),kind='ordinary PFP, not authenticated receipt',assertion=assertion)
 try:
  assert build(out,'baseline-build')==0;o['baseline_exit']=test(out,'baseline');assert o['baseline_exit']==0
  altered=mutate(original);o['mutant_sha256']=sha(altered);(out/'patch.diff').write_text(__import__('difflib').unified_diff if False else ''.join(__import__('difflib').unified_diff(original.decode().splitlines(True),altered.decode().splitlines(True))),encoding='utf-8');tmp=path.with_suffix(path.suffix+'.causal-temp');tmp.write_bytes(altered);os.replace(tmp,path)
  o['mutant_build_exit']=build(out,'mutant-build');assert o['mutant_build_exit']==0;o['mutant_exit']=test(out,'mutant');o['failed']=failures(out/'mutant.trx');assert o['mutant_exit']!=0 and o['failed']
  if name=='M1-host-projection': assert any('Assert.True() Failure' in x['message'] or '台账' in x['message'] for x in o['failed'])
  else:assert any(assertion in x['message'] for x in o['failed'])
 finally:
  tmp=path.with_suffix(path.suffix+'.causal-temp');tmp.write_bytes(original);os.replace(tmp,path);o['restored_sha256']=sha(path.read_bytes());assert o['restored_sha256']==o['original_sha256'];o['restored_build_exit']=build(out,'restored-build');o['restored_exit']=test(out,'restored');(out/'observation.json').write_text(json.dumps(o,ensure_ascii=False,indent=2),encoding='utf-8');records.append(o);(d/'mutation-observations.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
 assert o['restored_build_exit']==0 and o['restored_exit']==0
print('PFP complete',len(records),flush=True)
