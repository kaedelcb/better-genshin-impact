from pathlib import Path
import subprocess,hashlib,json,os,xml.etree.ElementTree as ET
root=Path.cwd(); parent=Path(__file__).parent; products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
cases=[('M3-execution-registration-held','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',b'            lock (_gate)\r\n            {\r\n                _drives.Remove(entry.WorkflowId);\r\n                _reservedWorkflows.Remove(entry.WorkflowId);\r\n            }\r\n            try\r\n            {\r\n                await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);\r\n            }\r\n            finally\r\n            {\r\n                lock (_gate) _driveCompletions.Remove(entry);',b'            try\r\n            {\r\n                await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);\r\n            }\r\n            finally\r\n            {\r\n                lock (_gate)\r\n                {\r\n                    _drives.Remove(entry.WorkflowId);\r\n                    _reservedWorkflows.Remove(entry.WorkflowId);\r\n                    _driveCompletions.Remove(entry);\r\n                }','completed Runner must release execution registration while terminal writeback is tracked')]
records=[]
def run(out,name,args):
 with (out/(name+'.log')).open('wb') as log: code=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=900).returncode
 (out/(name+'-exit.txt')).write_text(str(code)); print(out.name,name,code,flush=True); return code
def test(out,name):
 env=os.environ.copy(); env['BGI_TERMINAL_LIFECYCLE_EVIDENCE_DIR']=str(out)
 with (out/(name+'.log')).open('wb') as log: code=subprocess.run(['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalHost_ShutdownWaitsFor','/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(out)],env=env,stdout=log,stderr=subprocess.STDOUT,timeout=60).returncode
 (out/(name+'-exit.txt')).write_text(str(code)); print(out.name,name,code,flush=True); return code
build=['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo']
for name,rel,needle,replacement,assertion in cases:
 out=parent/name; out.mkdir(exist_ok=False); p=root/rel; original=p.read_bytes(); assert original.count(needle)==1
 assert test(out,'baseline')==0
 try:
  mutant=original.replace(needle,replacement); temp=p.with_name(p.name+'.terminal-mutation.tmp'); temp.write_bytes(mutant); os.replace(temp,p)
  assert run(out,'mutant-build',build)==0
  code=test(out,'mutant'); assert code!=0
  rows=ET.parse(out/'mutant.trx').findall('.//t:UnitTestResult',ns)
  failed=[dict(testId=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns)) for x in rows if x.get('outcome')=='Failed']
  assert len(failed)==2 and all(assertion in x['message'] for x in failed),failed
 finally:
  temp=p.with_name(p.name+'.terminal-restore.tmp'); temp.write_bytes(original); os.replace(temp,p); assert p.read_bytes()==original
 assert run(out,'restored-build',build)==0
 assert test(out,'restored')==0
 records.append(dict(id=name,path=rel,original_sha256=hashlib.sha256(original).hexdigest(),mutant_sha256=hashlib.sha256(mutant).hexdigest(),restored_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),failed=failed,kind='ordinary PFP, not authenticated receipt'))
 (parent/'terminal-mutation-observations-m3.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
print('all mutations restored',flush=True)
