from pathlib import Path
import subprocess,hashlib,json,os,xml.etree.ElementTree as ET
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f';products=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
svc=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';host=r/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
def run(folder,name,args):
 with (folder/(name+'.log')).open('wb') as f:c=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT).returncode
 (folder/(name+'-exit.txt')).write_text(str(c));print(folder.name,name,c,flush=True);return c
def build(folder,name):return run(folder,name,['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo'])
def test(folder,name,flt):return run(folder,name,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~'+flt,'/Logger:trx;LogFileName='+name+'.trx','/ResultsDirectory:'+str(folder)])
def write(p,b):
 tmp=p.with_name(p.name+'.lifecycle-restore.tmp');tmp.write_bytes(b);os.replace(tmp,p)
def early_gate(s):
 a=s.index('    public async Task<AdmissionResult> AdmitRecoveryAsync(');z=s.index('    /// <summary>',a);old=subprocess.check_output(['git','show','HEAD:MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs']).decode();x=old.index('    public async Task<AdmissionResult> AdmitRecoveryAsync(');y=old.index('    /// <summary>',x);return s[:a]+old[x:y]+s[z:]
def source_bypass(s):
 a=s.index('        if (inheritedScope is null)');z=s.index('        var resumeScope = inheritedScope;',a);return s[:a]+'        inheritedScope ??= "bgi:local:" + CurrentBgiEpoch();\n'+s[z:]
def pending_identity(s):
 anchor='            if (tombstones >= TombstoneLimit) break;';assert s.count(anchor)==1;return s.replace(anchor,'            op.WireSubmitKey += "-mutated-pending-identity";\n'+anchor)
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
observations=[]
for name,p,mutate,flt in [('M1-recovery-gate',svc,early_gate,'OriginalLifecycle_RealPaused'),('M2-resume-source',host,source_bypass,'OriginalLifecycle_SourceFault'),('M3-pending-identity',svc,pending_identity,'OriginalLifecycle_SaturatedPendingTransfer')]:
 folder=d/'mutations-r2'/name;folder.mkdir(parents=True,exist_ok=False);original=p.read_bytes();sha=hashlib.sha256(original).hexdigest();nl='\r\n' if original.count(b'\r\n') else '\n';s=original.decode().replace('\r\n','\n');bad=mutate(s).replace('\r\n','\n').replace('\n',nl).encode();assert bad!=original
 assert test(folder,'baseline',flt)==0
 try:
  write(p,bad);assert build(folder,'mutant-build')==0
  c=test(folder,'mutant',flt);assert c!=0
  failed=[dict(id=x.get('testId'),name=x.get('testName'),message=x.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=x.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for x in ET.parse(folder/'mutant.trx').findall('.//t:UnitTestResult',ns) if x.get('outcome')=='Failed'];assert failed
 finally:write(p,original);assert hashlib.sha256(p.read_bytes()).hexdigest()==sha
 assert build(folder,'restored-build')==0;assert test(folder,'restored',flt)==0
 item=dict(id=name,source=str(p.relative_to(r)),original_sha256=sha,mutant_sha256=hashlib.sha256(bad).hexdigest(),restored_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),failed=failed,baseline_exit=0,mutant_build_exit=0,mutant_exit=c,restored_build_exit=0,restored_exit=0,kind='ordinary PFP; not authenticated receipt')
 (folder/'observation.json').write_text(json.dumps(item,ensure_ascii=False,indent=2));observations.append(item)
(d/'mutation-observations-r2.json').write_text(json.dumps(observations,ensure_ascii=False,indent=2))
