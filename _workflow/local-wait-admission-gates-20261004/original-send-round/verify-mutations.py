from pathlib import Path
import hashlib,json,subprocess,xml.etree.ElementTree as ET
root=Path.cwd();out=root/'_workflow/local-wait-admission-gates-20261004/original-send-round/mutations';products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
sha=lambda b:hashlib.sha256(b).hexdigest()
# This script is prepared now; it starts only after all prior build/test sessions reach terminal.
host='MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
boundary='MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs'
guard='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs'
wrapper='MultiplayerHoeingAssistant/Services/TaskCenter/ArbitrationWorkflowExecutionBoundary.cs'
cases=[
 ('M1-archive',host,'var matches = handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation))\n            .Where(o => o.OperationType == OperationType.NodeExecution','var matches = handoff.Operations\n            .Where(o => o.OperationType == OperationType.NodeExecution','FullyQualifiedName~OriginalRound_ModernConsumedAnchorResolvesHotOrArchived','Assert.NotNull'),
 ('M2-readback',boundary,'var readback = _runs.Load(run.RunId!);\n                if (readback?.CurrentSubmission','var readback = mergedRecord;\n                if (readback?.CurrentSubmission','FullyQualifiedName~OriginalRound_ReconcileReadbackFailure','Assert.True'),
 ('M3-retained-proof',guard,'Require(nextRounds[index] == priorRounds[index]);','Require(true);','FullyQualifiedName~OriginalRound_LegitimateNoBytesRetry','No exception was thrown'),
 ('M4-renewal','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs','&& NextSendRound(permit.OriginalSendIdentity, nextIdentity);','&& false;','FullyQualifiedName~OriginalRound_LegitimateNoBytesRetry','Assert.Null'),
 ('M5-historical-exit',boundary,'|| !hit.ExecutionExitConfirmed || hit.ExecutionExitDisposition != submission.ExecutionExitDisposition','|| hit.ExecutionExitDisposition != submission.ExecutionExitDisposition','FullyQualifiedName~OriginalRound_HistoricalActiveOrConflictingExit','Assert.Null'),
 ('M6-runner-route',wrapper,'=> _reconcileViaAdmission is { } reconcile ? reconcile(run, submission, ct) : _inner.ReconcileSubmissionAsync(run, submission, ct);','=> _inner.ReconcileSubmissionAsync(run, submission, ct);','FullyQualifiedName~OriginalRound_ThreeParameterRunnerReconcile','Strings differ'),
 ('M7-original-identity',boundary,'|| originalPermit.OriginalSendIdentity != acceptedSendIdentity','|| false','FullyQualifiedName~OriginalRound_ReconcileCannotReplaceFrozenPermitIdentity','Assert.True'),
 ('M8-association-readback','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','readback.RecoveryAssociations.Count(a => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(association)) == 1','readback.RecoveryAssociations.Any(a => a.SubmissionIdentity == association.SubmissionIdentity)','FullyQualifiedName~OriginalRound_RecoveryAssociationReadback','Assert.Null'),
 ('M9-lease-rounds','MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs','if (op.RejectedSendRounds is { } rejectedRounds','if (false && op.RejectedSendRounds is { } rejectedRounds','FullyQualifiedName~OriginalRound_LeaseRead','Values differ')]
subjects={p:(root/p).read_bytes() for p in {c[1] for c in cases}}
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(folder):
 return [{'id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in ET.parse(folder/'results.trx').findall('.//t:UnitTestResult',ns)]
def build(folder):
 with (folder/'build.log').open('wb') as log:return subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],cwd=root,stdout=log,stderr=subprocess.STDOUT).returncode
def test(folder,filter):
 with (folder/'test.log').open('wb') as log:return subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName=results.trx','/ResultsDirectory:'+str(folder)],cwd=root,stdout=log,stderr=subprocess.STDOUT).returncode
def publish(p,b):
 temp=(root/p).with_suffix('.original-round-mutation.tmp');temp.write_bytes(b);temp.replace(root/p)
if __name__=='__main__':
 out.mkdir(exist_ok=False);(out/'before').mkdir()
 for p,b in subjects.items():
  dest=out/'before'/p;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(b)
 records=[];failure=None
 try:
  for name,p,old,new,filter,expected in cases:
   case=out/name;case.mkdir();baseline=case/'baseline';baseline.mkdir()
   assert build(baseline)==0,name
   assert test(baseline,filter)==0,name
   newline='\r\n' if b'\r\n' in subjects[p] else '\n'
   oldb=old.replace('\n',newline).encode();newb=new.replace('\n',newline).encode()
   assert subjects[p].count(oldb)==1,(name,subjects[p].count(oldb))
   mutant=subjects[p].replace(oldb,newb);publish(p,mutant)
   mutantFolder=case/'mutant';mutantFolder.mkdir()
   try:
    be=build(mutantFolder);assert be==0,name
    te=test(mutantFolder,filter);results=rows(mutantFolder)
    intended=[r for r in results if r['outcome']=='Failed' and expected in r['message'] and 'Tests.' in r['stack']]
    assert te!=0 and intended,(name,results)
   finally:
    publish(p,subjects[p]);assert (root/p).read_bytes()==subjects[p]
   restored=case/'restored';restored.mkdir()
   re=build(restored);rt=test(restored,filter) if re==0 else None
   assert re==0 and rt==0,(name,re,rt)
   records.append({'id':name,'source':p,'source_sha256':sha(subjects[p]),'mutant_sha256':sha(mutant),'restored_sha256':sha((root/p).read_bytes()),'patch':{'old':old,'new':new},'filter':filter,'baseline':rows(baseline),'mutant_build_exit':be,'mutant_test_exit':te,'intended_failures':intended,'restored_build_exit':re,'restored_test_exit':rt,'restored':rows(restored)})
   print(name+': P/F/P with intended assertion and byte restoration',flush=True)
 except BaseException as ex:
  failure=repr(ex);raise
 finally:
  for p,b in subjects.items():publish(p,b)
  (out/'observations.json').write_text(json.dumps({'kind':'ordinary process/TRX/byte evidence, not authenticated receipt or independent pass','records':records,'failure':failure,'all_subject_bytes_restored':all((root/p).read_bytes()==b for p,b in subjects.items()),'final_sources':{p:sha((root/p).read_bytes()) for p in subjects}},ensure_ascii=False,indent=2),encoding='utf-8')
