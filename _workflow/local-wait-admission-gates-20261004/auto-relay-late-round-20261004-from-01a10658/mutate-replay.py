from pathlib import Path
import json,subprocess,hashlib,os,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;source=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';original=source.read_bytes();products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';observations=[];ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
sha=lambda b:hashlib.sha256(b).hexdigest()
mutations=[('M1-archived-replay',b'if (archivedMatch.Operation.RequestState == OperationRequestState.TerminalCompleted)',b'if (false && archivedMatch.Operation.RequestState == OperationRequestState.TerminalCompleted)','original settled archived terminal replay'),('M2-original-confirmation',b'|| _hooks.TakeoverTerminalPayloadConfirmed?.Invoke(fact.SubmissionIdentity, fact.SendSeq,\r\n                fact.RawTerminal, fact.ExecutionErrorCode, fact.JobId, fact.TerminalEvidenceSource, observedAt, result.Kind) != true',b'|| false','unconfirmed/conflicting archived replay must remain unresolved')]
def run(out,name,args):
 with (out/(name+'.log')).open('wb') as log:code=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=600).returncode
 (out/(name+'-exit.txt')).write_text(str(code),encoding='utf-8');print(out.name,name,code,flush=True);return code
def build(out,name):return run(out,name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(out,name):return run(out,name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalLateRound_','/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out.resolve())])
def restore():
 tmp=source.with_name(source.name+'.late-round-restore.tmp');tmp.write_bytes(original);os.replace(tmp,source);assert source.read_bytes()==original
for mid,needle,repl,assertion in mutations:
 out=d/'mutations'/mid;out.mkdir(parents=True,exist_ok=False);assert original.count(needle)==1
 o=dict(id=mid,source=str(source.relative_to(r)),original_sha256=sha(original),assertion=assertion,kind='ordinary PFP; not authenticated receipt')
 try:
  assert source.read_bytes()==original
  o['baseline_exit']=test(out,'baseline');assert o['baseline_exit']==0
  mutant=original.replace(needle,repl);source.write_bytes(mutant);o['mutant_sha256']=sha(mutant)
  o['mutant_build_exit']=build(out,'mutant-build');assert o['mutant_build_exit']==0
  o['mutant_exit']=test(out,'mutant');assert o['mutant_exit']==1
  rows=ET.parse(out/'mutant.trx').findall('.//t:UnitTestResult',ns);o['failed']=[dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in rows if x.get('outcome')=='Failed'];assert any(assertion in x['message'] for x in o['failed'])
 finally:
  restore();o['restored_sha256']=sha(source.read_bytes());(out/'observation-before-restored-build.json').write_text(json.dumps(o,ensure_ascii=False,indent=2),encoding='utf-8')
 o['restored_build_exit']=build(out,'restored-build');assert o['restored_build_exit']==0
 o['restored_exit']=test(out,'restored');assert o['restored_exit']==0
 observations.append(o);(out/'observation.json').write_text(json.dumps(o,ensure_ascii=False,indent=2),encoding='utf-8');(d/'mutation-observations.json').write_text(json.dumps(observations,ensure_ascii=False,indent=2),encoding='utf-8')
print('both mutations restored',sha(source.read_bytes()))
