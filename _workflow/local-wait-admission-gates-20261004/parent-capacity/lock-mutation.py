from pathlib import Path
import subprocess, hashlib, json, os, xml.etree.ElementTree as ET
root=Path.cwd(); out=root/'_workflow/local-wait-admission-gates-20261004/parent-capacity/lock-mutation-r4'; out.mkdir(exist_ok=True)
p=root/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'; original=p.read_bytes()
old=b'var r = await _admission.MarkOperationTerminalAsync(op.RequestIdentity, "runstore-seal:" + seal.Id, _shutdownCts.Token).ConfigureAwait(false);'
new=b'var r = _admission.MarkOperationTerminal(op.RequestIdentity, "runstore-seal:" + seal.Id);'
assert original.count(old)==1
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def replace(b):
    temp=p.with_name(p.name+'.parent-capacity.tmp'); temp.write_bytes(b); os.replace(temp,p)
def run(stage):
    with (out/(stage+'-build.log')).open('wb') as log:
        build=subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'],stdout=log,stderr=subprocess.STDOUT).returncode
    assert build==0
    with (out/(stage+'-test.log')).open('wb') as log:
        code=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalHost_TerminalSweepYieldsWhileFacadeGateIsHeld','/Logger:trx;LogFileName='+stage+'.trx','/ResultsDirectory:'+str(out)],stdout=log,stderr=subprocess.STDOUT).returncode
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows=[{'id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in ET.parse(out/(stage+'.trx')).findall('.//t:UnitTestResult',ns)]
    return {'build_exit':build,'test_exit':code,'rows':rows}
baseline=run('baseline'); assert baseline['test_exit']==0
mutant=original.replace(old,new)
try:
    replace(mutant); negative=run('negative')
finally:
    replace(original); assert p.read_bytes()==original
restored=run('restored'); assert restored['test_exit']==0
assert negative['test_exit']==1 and any(r['outcome']=='Failed' and 'terminal sweep must yield instead of blocking its caller on the facade gate' in r['message'] for r in negative['rows'])
(out/'observation.json').write_text(json.dumps({'kind':'ordinary PFP/TRX/source byte observation, not authenticated receipt','id':'M1-host-synchronous-terminal-sweep','source':p.relative_to(root).as_posix(),'source_sha256':hashlib.sha256(original).hexdigest(),'mutant_sha256':hashlib.sha256(mutant).hexdigest(),'restored_sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'patch':{'old':old.decode(),'new':new.decode()},'baseline':baseline,'negative':negative,'restored':restored},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Host synchronous sweep mutation: P/F/P, original bytes restored',flush=True)
