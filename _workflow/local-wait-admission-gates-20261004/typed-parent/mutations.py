from pathlib import Path
import subprocess,hashlib,json,os,xml.etree.ElementTree as ET
r=Path.cwd();base=r/'_workflow/local-wait-admission-gates-20261004/typed-parent/mutations';base.mkdir(exist_ok=True);products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
cases=[
 ('M1-ordinary-parent-guard','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs','        Require(current.AdmissionSourceScope == next.AdmissionSourceScope);\r\n        Require(current.AdmissionParentSource == next.AdmissionParentSource);\r\n        var originalBindings = next.Handoffs.Select(h => JsonSerializer.Serialize(h)).ToList();\r\n        foreach (var binding in current.Handoffs)\r\n            Require(originalBindings.Remove(JsonSerializer.Serialize(binding)));\r\n        if (current.AdmissionParentSource is { } parent)\r\n            Require(parent.MatchesHandoff(next));','        // mutation: ordinary source protection removed','FullyQualifiedName~OrdinaryWriterCannotRewriteOriginalAdmissionScope','Assert.Throws() Failure'),
 ('M2-original-version','MultiplayerHoeingAssistant/Models/TaskCenter/AdmissionParentSource.cs','return Version == 1 && Kind == AdmissionParentKind.StartupHandoff','return Kind == AdmissionParentKind.StartupHandoff','FullyQualifiedName~ChangedOrMissingOriginalCannotAuthorize','Assert.Null() Failure'),
 ('M3-occupy-parent-recheck','MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','if (op.Candidate?.Namespace == "successor" || op.ParentSource is not null)','if (false)','FullyQualifiedName~TypedParent_OriginalHandoffIsAtomicallyBoundAndRechecked','Assert.NotEqual() Failure'),
 ('M4-host-parent-recheck','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','if (originalParent is null || originalParent != op.ParentSource\r\n            || op.ParentRequestIdentity != originalParent.Value.RequestIdentity\r\n            || d.Candidate.Scope != originalParent.Value.Scope)','if (false)','FullyQualifiedName~OriginalHost_ParentRemoved','Assert.Equal() Failure'),
]
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
records=[]
for id,path,old,new,filter,assertion in cases:
 d=base/id;d.mkdir(exist_ok=False);p=r/path;original=p.read_bytes();a=old.encode();b=new.encode();assert original.count(a)==1
 def run(stage):
  with (d/(stage+'-build.log')).open('wb') as log: c=subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],stdout=log,stderr=subprocess.STDOUT).returncode
  (d/(stage+'-build-exit.txt')).write_text(str(c));assert c==0
  with (d/(stage+'.log')).open('wb') as log: c=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName='+stage+'.trx','/ResultsDirectory:'+str(d)],stdout=log,stderr=subprocess.STDOUT).returncode
  rows=[dict(id=x.get('testId'),name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(d/(stage+'.trx')).findall('.//t:UnitTestResult',ns)]
  return dict(build_exit=0,test_exit=c,rows=rows)
 def replace(data):
  tmp=p.with_name(p.name+'.typed-parent.tmp');tmp.write_bytes(data);os.replace(tmp,p)
 baseline=run('baseline');assert baseline['test_exit']==0
 mutant=original.replace(a,b)
 try: replace(mutant);negative=run('negative')
 finally: replace(original);assert p.read_bytes()==original
 restored=run('restored');assert restored['test_exit']==0
 assert negative['test_exit']==1 and any(x['outcome']=='Failed' and assertion in x['message'] for x in negative['rows']),id
 record=dict(id=id,kind='ordinary PFP/TRX/bytes, not authenticated receipt',path=path,source_sha256=hashlib.sha256(original).hexdigest(),mutant_sha256=hashlib.sha256(mutant).hexdigest(),restored_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),patch=dict(old=old,new=new),baseline=baseline,negative=negative,restored=restored)
 (d/'observation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2));records.append(record);print(id,'P/F/P, SHA restored',flush=True)
(base/'observations.json').write_text(json.dumps(records,ensure_ascii=False,indent=2))
