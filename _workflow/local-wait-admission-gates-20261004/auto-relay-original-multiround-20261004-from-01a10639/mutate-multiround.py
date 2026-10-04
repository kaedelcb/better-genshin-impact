from pathlib import Path
import subprocess,json,hashlib,os,xml.etree.ElementTree as ET
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639/mutations';d.mkdir(exist_ok=False)
products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
host=r/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs';svc=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';permit=r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs'
hostbytes=host.read_bytes();start=hostbytes.index(b'        // An explicit same-request retry');end=hostbytes.index(b'        if (result.Kind == AdmissionResultKind.Accepted)',start)
items=[('M1-original-context',svc,b'retryRequest.ProcessLocalContext = original.ProcessLocalContext;',b'retryRequest.ProcessLocalContext = null;'),('M2-current-round-readback',host,hostbytes[start:end],b''),('M3-prior-nonce-binding',permit,b'&& PreviousRoundsComplete(run, submission, permit)',b'&& true /* mutation: prior original nonce checks removed */')]
records=[]
def run(out,name,args):
 with (out/(name+'.log')).open('wb') as f: code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,timeout=90).returncode
 (out/(name+'-exit.txt')).write_text(str(code),encoding='utf-8');return code
def build(out,name):return run(out,name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(out,name):return run(out,name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalMultiRound_','/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out)])
for ident,p,old,new in items:
 out=d/ident;out.mkdir();b=p.read_bytes();assert b.count(old)==1
 sha=lambda x:hashlib.sha256(x).hexdigest();record=dict(id=ident,source=str(p.relative_to(r)),original_sha256=sha(b),kind='ordinary PFP, not authenticated receipt');restored=False
 try:
  assert build(out,'baseline-build')==0;record['baseline_exit']=test(out,'baseline');assert record['baseline_exit']==0
  mutant=b.replace(old,new);record['mutant_sha256']=sha(mutant);p.write_bytes(mutant)
  record['mutant_build_exit']=build(out,'mutant-build');assert record['mutant_build_exit']==0
  record['mutant_exit']=test(out,'mutant');assert record['mutant_exit']==1
  rows=ET.parse(out/'mutant.trx').findall('.//t:UnitTestResult',ns)
  record['failed']=[dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in rows if x.get('outcome')=='Failed']
  assert record['failed']
  (out/'patch-before.bin').write_bytes(old);(out/'patch-after.bin').write_bytes(new)
 finally:
  temp=p.with_name(p.name+'.multiround-restore');temp.write_bytes(b);os.replace(temp,p);record['restored_sha256']=sha(p.read_bytes());assert p.read_bytes()==b
  record['restored_build_exit']=build(out,'restored-build');assert record['restored_build_exit']==0
  record['restored_exit']=test(out,'restored');assert record['restored_exit']==0
  (out/'observation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8');records.append(record)
  (d.parent/'mutation-observations.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8');print(ident,record.get('baseline_exit'),record.get('mutant_exit'),record.get('restored_exit'),flush=True)
