from pathlib import Path
import subprocess, hashlib, os, json, xml.etree.ElementTree as ET
root=Path.cwd(); out=Path(__file__).parent
source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs'
original=source.read_bytes(); sha=lambda b:hashlib.sha256(b).hexdigest()
old=b'ReadActivationStatusGuarded(record.Path, out var status, out var detail, requireQuiescence: _rootAuthority is null)'
new=b'ReadActivationStatusGuarded(record.Path, out var status, out var detail)'
assert original.count(old)==1
products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def run(name,args):
    with (out/(name+'.log')).open('wb') as f:
        code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT,timeout=600).returncode
    (out/(name+'-exit.txt')).write_text(str(code),encoding='utf-8'); print(name,code,flush=True); return code
def build(name):
    return run(name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj',
        '-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(name):
    return run(name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),
        '/TestCaseFilter:FullyQualifiedName~ReopenedMigrationActivationObservationTests',
        '/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out)])
def atomic(data):
    tmp=source.with_name(source.name+'.activation-restore-tmp'); tmp.write_bytes(data); os.replace(tmp,source)
observation=dict(kind='ordinary P/F/P, not authenticated receipt or independent acceptance',
    source=str(source.relative_to(root)),original_sha256=sha(original),patch='restore quiet requirement at modern read-only repeated activation')
try:
    assert test('activation-pfp-baseline')==0
    atomic(original.replace(old,new)); observation['mutant_sha256']=sha(source.read_bytes())
    assert build('activation-pfp-mutant-build')==0
    assert test('activation-pfp-mutant')!=0
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    failures=[dict(test_id=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),
        stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns))
        for x in ET.parse(out/'activation-pfp-mutant.trx').findall('.//t:UnitTestResult',ns) if x.get('outcome')=='Failed']
    assert len(failures)==1 and 'drift: "clean"' in failures[0]['name'] and 'quiescence_released_during_callback' in failures[0]['message']
    observation['target_failures']=failures
finally:
    atomic(original); observation['restored_sha256']=sha(source.read_bytes()); assert observation['restored_sha256']==observation['original_sha256']
    (out/'activation-mutation-observation.json').write_text(json.dumps(observation,indent=2),encoding='utf-8')
assert build('activation-pfp-restored-build')==0
assert test('activation-pfp-restored')==0
observation['passed_failed_passed']=True
(out/'activation-mutation-observation.json').write_text(json.dumps(observation,indent=2),encoding='utf-8')
