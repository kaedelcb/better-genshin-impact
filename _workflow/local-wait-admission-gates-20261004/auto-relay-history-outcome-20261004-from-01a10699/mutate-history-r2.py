from pathlib import Path
import subprocess,hashlib,json,os,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;out=d/'mutations-r2';out.mkdir(exist_ok=False);p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';original=p.read_bytes();text=original.decode('utf-8');products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};records=[]
def atomic(b):
 tmp=p.with_name(p.name+'.history-mutation-restore.tmp');tmp.write_bytes(b);os.replace(tmp,p)
def run(folder,name,args):
 with (folder/(name+'.log')).open('wb') as f:code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,timeout=600).returncode
 (folder/(name+'-exit.txt')).write_text(str(code));return code
def build(folder,name):return run(folder,name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(folder,name,flt):return run(folder,name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+flt,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(folder.resolve())])
def rows(folder,name):return [dict(id=x.get('testId'),name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(folder/(name+'.trx')).findall('.//t:UnitTestResult',ns)]
start=text.index('    internal static bool UniqueOriginalOutcomeSet(');end=text.index('    private static bool OutcomeResultMatches',start)
host='FullyQualifiedName~OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure';direct='FullyQualifiedName~HistoricalObservation_DirectBoundSealsRejectConflictingOutcomeSet'
mutations=[('M1-original-outcome-set',text[start:end],'    internal static bool UniqueOriginalOutcomeSet(WorkflowRunRecord run, WorkflowSubmission submission, string sendIdentity) => true;\r\n\r\n',host,'history outcome uniqueness must reject'),
 ('M2-node-seal', '        if (!UniqueOriginalOutcomeSet(r, s, sendIdentity)) return null;\r\n','',direct,'Assert.Null() Failure'),
 ('M3-run-seal', '''        && Submissions(r).All(s => BodySettled(r, s)
            && ((s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity) is not { Length: > 0 } identity
                || UniqueOriginalOutcomeSet(r, s, identity)))'''.replace('\n','\r\n'),'        && Submissions(r).All(s => BodySettled(r, s))',direct,'Assert.Null() Failure'),
 ('M4-business-result', '&& (!BgiJobTerminalPolling.IsTerminal(o.RawTerminal) || OutcomeResultMatches(o, o.RawTerminal, run.StopRequested))','&& true',direct,'Assert.Null() Failure')]
try:
 for ident,old,new,flt,expected in mutations:
  assert p.read_bytes()==original;assert text.count(old)==1,ident
  folder=out/ident;folder.mkdir();assert build(folder,'baseline-build')==0;assert test(folder,'baseline',flt)==0
  mutant=text.replace(old,new).encode('utf-8');(folder/'patch.json').write_text(json.dumps(dict(old=old,new=new),ensure_ascii=False,indent=2),encoding='utf-8')
  try:
   atomic(mutant);assert build(folder,'mutant-build')==0
   mc=test(folder,'mutant',flt); failures=[x for x in rows(folder,'mutant') if x['outcome']=='Failed'];assert mc!=0 and any(expected in x['message'] for x in failures),(ident,failures)
  finally:atomic(original);assert p.read_bytes()==original
  assert build(folder,'restored-build')==0;assert test(folder,'restored',flt)==0
  record=dict(id=ident,kind='ordinary P/F/P not certified receipt',source=str(p.relative_to(r)),original_sha256=hashlib.sha256(original).hexdigest(),mutant_sha256=hashlib.sha256(mutant).hexdigest(),restored_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),baseline=rows(folder,'baseline'),mutant=rows(folder,'mutant'),restored=rows(folder,'restored'),expected=expected,build_exits=[0,0,0],test_exits=[0,mc,0]);records.append(record);(d/'mutation-observations-r2.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8');print(ident,'P/F/P',len(failures),'byte restored',flush=True)
finally:
 atomic(original);assert p.read_bytes()==original
 (d/'post-mutation-byte-observation-r2.json').write_text(json.dumps(dict(source=str(p.relative_to(r)),sha256=hashlib.sha256(p.read_bytes()).hexdigest(),matches_original=True,completed=len(records)),indent=2),encoding='utf-8')
