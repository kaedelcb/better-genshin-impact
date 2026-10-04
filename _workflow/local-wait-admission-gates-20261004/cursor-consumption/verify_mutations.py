from pathlib import Path
import hashlib, json, subprocess, xml.etree.ElementTree as ET
root=Path(__file__).resolve().parents[3]; out=Path(__file__).resolve().parent/'mutation-round2'
out.mkdir(exist_ok=False)
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
sha=lambda b:hashlib.sha256(b).hexdigest()
store='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs'
guard='MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs'
service='MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs'
cases=[
 ('M1-cursor-hold',guard,'Require(SameCursor(current, next));','Require(true);','FullyQualifiedName~CursorPermit_PreparedResponsibilityBlocksAllCursorWriters','No exception was thrown'),
 ('M2-durable-once',store,'s.SendPermit != permit','s.SendPermit?.Nonce != permit.Nonce','FullyQualifiedName~CursorPermit_ReopenedCredentialCannotSendTwice','Assert.True() Failure'),
 ('M3-stable-occurrence',service,'&& string.Equals(other.CursorRef, cursorRefToCheck, StringComparison.Ordinal)','&& string.Equals(other.CursorRef, cursorRefToCheck, StringComparison.Ordinal)\n                    && other.CursorRevision == request.CursorRevision','FullyQualifiedName~Cursor_MixedRevisionFormats','Strings differ'),
 ('M4-authorized-only',guard,'Require(nextPermit == authorizedPermit &&','Require(true || nextPermit == authorizedPermit &&','FullyQualifiedName~CursorPermit_OrdinaryWritersCannotFabricateConsumption','No exception was thrown'),
 ('M5-readback',store,'var readback = Load(run.RunId);','var readback = published;','FullyQualifiedName~CursorPermit_DurableConsumptionFailureNeverCallsPort','Assert.True() Failure')]
subjects={p:(root/p).read_bytes() for p in {c[1] for c in cases}}
before=out/'mutation-before';before.mkdir(exist_ok=False)
for p,b in subjects.items():
 d=before/p;d.parent.mkdir(parents=True,exist_ok=True);d.write_bytes(b)
def publish(p,b):
 t=(root/p).with_suffix('.mutation.tmp');t.write_bytes(b);t.replace(root/p)
def build(folder):
 with (folder/'build.log').open('wb') as log:
  return subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],cwd=root,stdout=log,stderr=subprocess.STDOUT).returncode
def test(folder,filter):
 with (folder/'test.log').open('wb') as log:
  return subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName=results.trx','/ResultsDirectory:'+str(folder)],cwd=root,stdout=log,stderr=subprocess.STDOUT).returncode
def facts(folder):
 tree=ET.parse(folder/'results.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
 return [{'test_id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'), 'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in tree.findall('.//t:UnitTestResult',ns)]
records=[];baseline=None;restored=None
try:
 baseline=out/'mutation-baseline';baseline.mkdir(exist_ok=False)
 assert build(baseline)==0
 assert test(baseline,'FullyQualifiedName~CursorPermit|FullyQualifiedName~Cursor_MixedRevisionFormats|FullyQualifiedName~Cursor_ArchivedMixedRevision')==0
 print('baseline passed',flush=True)
 for name,p,old,new,filter,expected in cases:
  folder=out/name;folder.mkdir(exist_ok=False)
  newline='\r\n' if b'\r\n' in subjects[p] else '\n'
  a=old.replace('\n',newline).encode();b=new.replace('\n',newline).encode()
  assert subjects[p].count(a)==1,name
  mutant=subjects[p].replace(a,b);publish(p,mutant)
  try:
   be=build(folder);assert be==0,name
   te=test(folder,filter);results=facts(folder)
   failures=[r for r in results if r['outcome']=='Failed' and expected in r['message'] and 'Assert.' in r['stack'] or r['outcome']=='Failed' and expected in r['message'] and 'Tests.' in r['stack']]
   assert te!=0 and failures,(name,results)
   records.append({'id':name,'source':p,'original_sha256':sha(subjects[p]),'mutant_sha256':sha(mutant),'patch':{'old':old,'new':new},'filter':filter,'build_exit':be,'test_exit':te,'intended_failures':failures})
   print(name+': intended assertion failed',flush=True)
  finally:
   publish(p,subjects[p]);assert (root/p).read_bytes()==subjects[p]
finally:
 for p,b in subjects.items():publish(p,b)
 restored=out/'mutation-restored';restored.mkdir(exist_ok=False)
 be=build(restored)
 te=test(restored,'FullyQualifiedName~CursorPermit|FullyQualifiedName~Cursor_MixedRevisionFormats|FullyQualifiedName~Cursor_ArchivedMixedRevision') if be==0 else None
 (out/'mutation-observation.json').write_text(json.dumps({'kind':'ordinary process/TRX/byte observations, not authenticated receipt or independent pass','baseline_results':facts(baseline) if (baseline/'results.trx').exists() else [],'mutations':records,'restored_build_exit':be,'restored_test_exit':te,'restored_source_hashes':{p:sha((root/p).read_bytes()) for p in subjects},'restored_bytes_equal':all((root/p).read_bytes()==b for p,b in subjects.items())},ensure_ascii=False,indent=2),encoding='utf-8')
 print('restored build='+str(be)+' test='+str(te),flush=True)
 assert be==0 and te==0
