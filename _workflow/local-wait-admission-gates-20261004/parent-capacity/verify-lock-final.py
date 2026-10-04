from pathlib import Path
import subprocess,json,hashlib,os,xml.etree.ElementTree as ET
root=Path.cwd(); out=root/'_workflow/local-wait-admission-gates-20261004/parent-capacity/final-r2'; out.mkdir(exist_ok=True)
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationAdmissionServiceTests.cs']
def facts():
    return [{'path':p,'bytes':len(b:= (root/p).read_bytes()),'lines':len(b.splitlines()),'sha256':hashlib.sha256(b).hexdigest(),'bom':b.startswith(b'\xef\xbb\xbf'),'crlf':b.count(b'\r\n'),'lf':b.count(b'\n')} for p in paths]
before=facts();products=root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def run(stage,args,env=None):
    with (out/(stage+'.log')).open('wb') as log: code=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,env=env).returncode
    (out/(stage+'-exit.txt')).write_text(str(code)+'\n');return code
build=run('final-build',['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(products),'--nologo']);assert build==0
def test(stage,filter,env=None):
    return run(stage,['dotnet','vstest',str(products/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:'+filter,'/Logger:trx;LogFileName='+stage+'.trx','/ResultsDirectory:'+str(out)],env)
full=test('final-full','FullyQualifiedName~TaskCenter')
manifest=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt';claims=hashlib.sha256(manifest.read_bytes()).hexdigest()
env=os.environ.copy();env['CLAIM_SURFACE_REGENERATE']='1';regen=test('claims-regenerate','FullyQualifiedName~ClaimSurfaceGuardTests',env);assert regen==0
assert hashlib.sha256(manifest.read_bytes()).hexdigest()==claims
target=test('final-targeted','FullyQualifiedName~OriginalHost_TerminalSweep|FullyQualifiedName~OriginalHost_PanelStop|FullyQualifiedName~TerminalWriteback_Cancelled|FullyQualifiedName~ClaimSurfaceGuardTests|FullyQualifiedName~StartupFlowSchemeStoreTests');assert target==0
assert facts()==before
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def rows(p):
    return {r.get('testId'):{'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in ET.parse(p).findall('.//t:UnitTestResult',ns)}
base=root/'_workflow/local-wait-admission-gates-20261004/original-host-history'
old=rows(base/'final-full.trx')|rows(base/'final-targeted-and-compat.trx');new=rows(out/'final-full.trx')|rows(out/'final-targeted.trx')
comparison={'kind':'ordinary exact testId/TRX union, not authenticated receipt','baseline_count':len(old),'current_count':len(new),'added':[{'id':k,**new[k]} for k in sorted(new.keys()-old.keys())],'removed':[{'id':k,**old[k]} for k in sorted(old.keys()-new.keys())],'changed':[{'id':k,'before':old[k],'after':new[k]} for k in sorted(old.keys()&new.keys()) if old[k]['outcome']!=new[k]['outcome']],'same_failed_ids':{k for k,v in old.items() if v['outcome']=='Failed'}=={k for k,v in new.items() if v['outcome']=='Failed'},'counts':{c:sum(r['outcome']==c for r in new.values()) for c in ['Passed','Failed','NotExecuted']}}
(out/'lock-impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
obs={'kind':'ordinary process/TRX/source byte observation, not authenticated receipt or independent pass/product acceptance','sources':before,'build_exit':build,'full_exit':full,'targeted_exit':target,'claim_sha256':claims,'sources_unchanged':True,'full_counts':{c:sum(r['outcome']==c for r in rows(out/'final-full.trx').values()) for c in ['Passed','Failed','NotExecuted']},'comparison_summary':{k:v for k,v in comparison.items() if k not in ['added','removed','changed']},'mutation_evidence':['../lock-mutation-r4/observation.json','../panel-mutation-r2/observation.json'],'original_obligations':'G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important/implementation/open retained','production_gate_open':False,'goal_complete':False,'limitations':['Actual Host/Runner/RunStore/LeaseStore and panel command use controlled execution port and synthetic local accepted parent responsibility for contention; no true IPC/game/User acceptance.','Synchronous APIs remain for compatibility; production panel and terminal writeback callers use asynchronous APIs.','Typed parent source, 33 individual capacity reason readback/tombstone/archive/restart and four entry causal matrix remain pending.','Full TaskCenter still retains 18 failures; exact ID comparison is not a permanent exemption.']}
(out/'lock-candidate-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(obs['full_counts']),json.dumps(comparison['counts']), 'same_failed_ids='+str(comparison['same_failed_ids']),'added='+str(len(comparison['added'])),'removed='+str(len(comparison['removed'])),'changed='+str(len(comparison['changed'])),flush=True)
