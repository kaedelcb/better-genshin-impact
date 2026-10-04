from pathlib import Path
import subprocess, hashlib, json, os, xml.etree.ElementTree as ET
r=Path.cwd(); d=Path(__file__).parent; p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs'
original=p.read_bytes(); text=original.decode('utf-8'); nl='\r\n' if b'\r\n' in original else '\n'
products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'; ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
out=d/'legacy-mutations'; out.mkdir(exist_ok=False); records=[]
def atomic(b):
    tmp=p.with_name(p.name+'.legacy-mutation.tmp'); tmp.write_bytes(b); os.replace(tmp,p)
def run(folder,name,args):
    with (folder/(name+'.log')).open('wb') as f: code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,timeout=600).returncode
    (folder/(name+'-exit.txt')).write_text(str(code)); return code
def build(folder,name): return run(folder,name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(folder,name): return run(folder,name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~LegacyHistoricalSealIntegrityTests','/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(folder.resolve())])
def rows(folder,name): return [dict(id=x.get('testId'),name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for x in ET.parse(folder/(name+'.trx')).findall('.//t:UnitTestResult',ns)]
# Remove only the newly introduced legacy gates. Baseline and restoration use identical bytes.
mutations=[
 ('L1-legacy-association','&& r.RecoveryAssociations.All(a => ValidRecoveryAssociation(r, a))','', 'fault: "orphan"'),
 ('L2-legacy-outcome','|| UniqueOriginalOutcomeSet(r, s, s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity'+nl+'                    ?? r.RecoveryAssociations.FirstOrDefault(a => a.SubmissionKey == s.Key)?.SubmissionIdentity ?? "")','|| true','fault: "duplicate-outcome"'),
 ('L3-terminal-correspondence','            && (!BgiJobTerminalPolling.IsTerminal(submission.ObservedTerminal)'+nl+'                || !BgiJobTerminalPolling.IsTerminal(o.RawTerminal) || o.RawTerminal == submission.ObservedTerminal)'+nl,'','fault: "outcome-terminal"')]
try:
 for ident,old,new,target in mutations:
    assert p.read_bytes()==original and text.count(old)==1,ident
    folder=out/ident; folder.mkdir(); assert build(folder,'baseline-build')==0; assert test(folder,'baseline')==0
    mutant=text.replace(old,new).encode('utf-8'); (folder/'patch.json').write_text(json.dumps(dict(old=old,new=new),indent=2),encoding='utf-8')
    try:
        atomic(mutant); assert build(folder,'mutant-build')==0; mc=test(folder,'mutant')
        failures=[x for x in rows(folder,'mutant') if x['outcome']=='Failed']; assert mc!=0 and any(target in x['name'] and 'Assert.Null() Failure' in x['message'] for x in failures),(ident,failures)
    finally: atomic(original); assert p.read_bytes()==original
    assert build(folder,'restored-build')==0; assert test(folder,'restored')==0
    records.append(dict(id=ident,kind='ordinary P/F/P; not certified receipt',source=str(p.relative_to(r)),source_sha256=hashlib.sha256(original).hexdigest(),mutant_sha256=hashlib.sha256(mutant).hexdigest(),restored_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),baseline=rows(folder,'baseline'),mutant=rows(folder,'mutant'),restored=rows(folder,'restored'),test_exits=[0,mc,0],build_exits=[0,0,0],target=target))
    (d/'legacy-mutation-observations.json').write_text(json.dumps(records,indent=2),encoding='utf-8'); print(ident,'P/F/P restored',flush=True)
finally:
 atomic(original); assert p.read_bytes()==original
 (d/'legacy-restored-source.json').write_text(json.dumps(dict(sha256=hashlib.sha256(p.read_bytes()).hexdigest(),completed=len(records)),indent=2),encoding='utf-8')
