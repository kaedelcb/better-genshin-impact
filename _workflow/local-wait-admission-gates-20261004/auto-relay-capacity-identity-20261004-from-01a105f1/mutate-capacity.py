from pathlib import Path
import subprocess,json,hashlib,os,xml.etree.ElementTree as ET
r=Path.cwd();base=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1';products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products';p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';original=p.read_bytes();text=original.decode('utf-8');nl='\r\n' if b'\r\n' in original else '\n';text=text.replace('\r\n','\n');sha=lambda b:hashlib.sha256(b).hexdigest()
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(path):return [dict(test_id=x.get('testId'),name=x.get('testName'),outcome=x.get('outcome'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(path).findall('.//t:UnitTestResult',ns)]
def replace_bytes(b):
 tmp=p.with_name(p.name+'.capacity-restore.tmp');tmp.write_bytes(b);os.replace(tmp,p)
mutations=[
 ('M1-no-terminal-slot-migration', '            op.Zone = OperationZone.Tombstone;\n            op.UpdatedAtUtc = now; // 迁入起算 24h 最短保留', '            continue; // controlled negative: terminal operations retain main slots\n            op.UpdatedAtUtc = now; // 迁入起算 24h 最短保留', 'Expected: 33', 'Actual:   32'),
 ('M2-archive-wire-identity', '            foreach (var op in archivable)\n                file.Handoff.ArchivedOperations.Add(new ArchivedOperationRecord { Operation = op, ArchivedAtUtc = now });', '            foreach (var op in archivable)\n            {\n                if (op.OperationType == OperationType.NodeExecution) op.WireSubmitKey += "-wrong-original";\n                file.Handoff.ArchivedOperations.Add(new ArchivedOperationRecord { Operation = op, ArchivedAtUtc = now });\n            }', 'Assert.Equal()', 'Strings differ')]
for name,old,new,failure1,failure2 in mutations:
 assert text.count(old)==1,name
 d=base/name;d.mkdir(exist_ok=False);(d/'original-source.json').write_text(json.dumps(dict(source=str(p.relative_to(r)),sha256=sha(original),bytes=len(original))))
 def run(label,args):
  with (d/(label+'.log')).open('wb') as f:code=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT).returncode
  (d/(label+'-exit.txt')).write_text(str(code));print(name,label,code,flush=True);return code
 def build(label):return run(label,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
 def test(label):return run(label,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~Capacity_33ActualAdmissions','/Logger:trx;LogFileName='+label+'.trx','/ResultsDirectory:'+str(d)])
 baseline=test('baseline');assert baseline==0
 mutant_build=None;mutant=None;restored_build=None;restored=None
 try:
  replace_bytes(text.replace(old,new).replace('\n',nl).encode('utf-8'))
  mutant_build=build('mutant-build');assert mutant_build==0
  mutant=test('mutant')
 finally:
  replace_bytes(original);assert p.read_bytes()==original
  restored_build=build('restored-build');assert restored_build==0
  restored=test('restored')
 negative=rows(d/'mutant.trx')
 obs=dict(id=name,source=str(p.relative_to(r)),original_sha256=sha(original),restored_sha256=sha(p.read_bytes()),baseline_exit=baseline,mutant_build_exit=mutant_build,mutant_exit=mutant,restored_build_exit=restored_build,restored_exit=restored,baseline=rows(d/'baseline.trx'),negative=negative,restored=rows(d/'restored.trx'),specified_failure_verified=any(x['outcome']=='Failed' and failure1 in x['message'] and failure2 in x['message'] for x in negative),kind='ordinary P/F/P and byte observation, not authenticated mutation receipt')
 (d/'observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2));assert mutant!=0 and restored==0 and obs['specified_failure_verified']
