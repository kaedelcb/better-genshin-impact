from pathlib import Path
import hashlib,json,os,subprocess,xml.etree.ElementTree as ET
root=Path.cwd();out=root/'_workflow/local-wait-admission-gates-20261004/original-host-history/mutations';out.mkdir(parents=True,exist_ok=True)
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
host='MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs'
terminal='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs'
admission='MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
items=[
 ('M1-current-exit',host,'                var exit = await boundary.AwaitSubmissionExitAsync(run, accepted, budget.Token).ConfigureAwait(false);','                var exit = BoundaryTerminalResult.UncertainWith("mutated exit observation omitted");','FullyQualifiedName~OriginalHost_RunnerRecovery','scenario: "stop-exited"','Expected: Cancelled'),
 ('M2-historical-evidence',terminal,'        if (recovery is not null) return true;','        if (recovery is not null) return false;','FullyQualifiedName~HistoricalObservation_OriginalExitSettles','accepted: False, outcome: False','Assert.False() Failure'),
 ('M3-archive',admission,'=> handoff is null ? [] : handoff.Operations.Concat(handoff.ArchivedOperations.Select(a => a.Operation));','=> handoff is null ? [] : handoff.Operations;','FullyQualifiedName~OriginalHost_RunnerRecovery','scenario: "history-archive-conflict"','Expected: Unavailable'),
 ('M4-reopen-initialization',host,'        if (_admissionWired)\n            await EnsureAdmissionFacadeAsync(budget.Token).ConfigureAwait(false);','        if (false)\n            await EnsureAdmissionFacadeAsync(budget.Token).ConfigureAwait(false);','FullyQualifiedName~OriginalHost_RunnerRecovery','scenario: "history-publish"','历史提交无法唯一关联原发送身份'),
 ('M5-original-payload',terminal,'&& BgiWorkflowExecutionBoundary.OriginalRequestMatches(job, sub.OriginalRequestEvidence)','&& true /* mutated original payload validation */','FullyQualifiedName~HistoricalObservation_WrongOriginalProjectionNeverSettles','field: "RequestFingerprint", value: "bad-payload"','Assert.Null() Failure'),
]
def run(folder,stage,filter):
 folder.mkdir(parents=True,exist_ok=True)
 with (folder/(stage+'-build.log')).open('wb') as f:
  b=subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],stdout=f,stderr=subprocess.STDOUT).returncode
 if b:raise RuntimeError((stage,'build failed',b))
 with (folder/(stage+'-test.log')).open('wb') as f:
  t=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName='+stage+'.trx','/ResultsDirectory:'+str(folder)],stdout=f,stderr=subprocess.STDOUT).returncode
 tree=ET.parse(folder/(stage+'.trx')); ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
 rows=[{'id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns)} for r in tree.findall('.//t:UnitTestResult',ns)]
 return {'build_exit':b,'test_exit':t,'rows':rows}
def replace(p,data):
 temp=p.with_name(p.name+'.original-host-history.tmp');temp.write_bytes(data);os.replace(temp,p)
records=[]
for mid,path,old,new,filter,target,assertion in items:
 p=root/path;original=p.read_bytes();nl='\r\n' if b'\r\n' in original else '\n';old=old.replace('\n',nl).encode();new=new.replace('\n',nl).encode();assert original.count(old)==1,(mid,original.count(old))
 folder=out/mid; baseline=run(folder,'baseline',filter);assert baseline['test_exit']==0 and all(r['outcome']=='Passed' for r in baseline['rows'])
 mutant=original.replace(old,new); negative=None
 try:
  replace(p,mutant);negative=run(folder,'negative',filter)
 finally:
  replace(p,original);assert p.read_bytes()==original
 restored=run(folder,'restored',filter);assert restored['test_exit']==0 and all(r['outcome']=='Passed' for r in restored['rows'])
 failed=[r for r in negative['rows'] if r['outcome']=='Failed' and target.lower() in r['name'].lower() and assertion in r['message']]
 assert negative['test_exit']!=0 and failed,(mid,negative)
 record={'id':mid,'source':path,'source_sha256':hashlib.sha256(original).hexdigest(),'mutant_sha256':hashlib.sha256(mutant).hexdigest(),'restored_sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'patch':{'old':old.decode(),'new':new.decode()},'baseline':baseline,'negative':negative,'restored':restored,'target_failed':failed,'assertion':assertion,'ordinary_observation_not_authenticated_receipt':True}
 (folder/'observation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');records.append(record)
 (out/'observations.json').write_text(json.dumps({'kind':'ordinary PFP/TRX and byte restoration, not authenticated receipt','records':records},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(mid,'P/F/P and original bytes restored',flush=True)
