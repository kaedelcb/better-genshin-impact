from pathlib import Path
import hashlib, json, xml.etree.ElementTree as ET, subprocess
root=Path(__file__).resolve().parents[3];out=Path(__file__).resolve().parent
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def results(p):
 t=ET.parse(root/p)
 return {r.get('testId'):{'id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in t.findall('.//t:UnitTestResult',ns)}
def summary(p):
 rs=results(p)
 return {'path':p,'sha256':sha(root/p),'counts':{k:sum(r['outcome']==k for r in rs.values()) for k in ['Passed','Failed','NotExecuted']},'failed':[r for r in rs.values() if r['outcome']=='Failed']}
base='_workflow/local-wait-admission-gates-20261004/'
baselines=[base+'server-original-evidence/reconcile-restored-taskcenter.trx',base+'server-original-evidence/reconcile-startup-compat.trx']
prior={}
for p in baselines:
 for i,r in results(p).items():
  assert i not in prior,(p,i)
  prior[i]=r
current_path=base+'cursor-consumption/final2-full.trx';current=results(current_path)
current_compat=base+'cursor-consumption/final2-startup-compat.trx'
for i,r in results(current_compat).items():
 if i in current:
  assert current[i]['name']==r['name'] and current[i]['outcome']==r['outcome'],i
  continue # Same IDs also match the full filter through the TaskCenterHandoffFields suffix; retain both TRX summaries below.
 current[i]=r
subjects=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs','MultiplayerHoeingAssistant/Services/TaskCenter/LocalNoSendEvidence.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreSendPermit.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationAdmissionServiceTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/BgiWorkflowExecutionBoundaryPortSeamTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/DurableJobObservationTests.cs']
comparison={'kind':'ordinary exact testId comparison; not authentication or failure exemption','baseline_paths':baselines,'baseline_count':len(prior),'current_paths':[current_path,current_compat],'current_count':len(current),'added':[current[i] for i in sorted(current.keys()-prior.keys())],'removed':[prior[i] for i in sorted(prior.keys()-current.keys())],'changed':[{'before':prior[i],'after':current[i]} for i in sorted(prior.keys()&current.keys()) if prior[i]['outcome']!=current[i]['outcome']],'failed_id_set_equal':{i for i,r in prior.items() if r['outcome']=='Failed'}=={i for i,r in current.items() if r['outcome']=='Failed'}}
(out/'impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2),encoding='utf-8')
paths=[base+'cursor-consumption/'+p for p in ['red.trx','mixed-red.trx','current.trx','current2.trx','current3.trx','current4.trx','full.trx','final-targeted.trx','final2-full.trx','final2-startup-compat.trx']]
mutation=json.loads((out/'mutation-round2/mutation-observation.json').read_text(encoding='utf-8'))
observation={'kind':'ordinary source/process/TRX observations, not authenticated receipt/independent implementation pass/product acceptance','thread_id':'01a10546-0e2d-7903-85d1-5db3cfd6e7ad','head_observed':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'sources_observed_after_safe_terminal':[{'path':p,'length':(root/p).stat().st_size,'sha256':sha(root/p)} for p in subjects],'tests':[summary(p) for p in paths],'mutation_source_bytes_restored':mutation['restored_bytes_equal'],'mutations':mutation['mutations'],'mutation_scope_note':'Five mutation subjects unchanged since round2; final changes afterward were unrelated backup-path normalization, creation proof guard and the original-payload observation fixture/legacy test. No claim of authenticated full-input PFP yet.','final_build_log':base+'cursor-consumption/final2-build.log','final_build_exit':0,'final_full_test_exit':1,'final_product':{'path':base+'g10-completion/products/MultiplayerHoeingAssistant.UnitTest.dll','sha256':sha(root/(base+'g10-completion/products/MultiplayerHoeingAssistant.UnitTest.dll'))},'impact_comparison':comparison,'independent_requests_added':0,'all_original_obligations':'important/implementation/open retained','g4g7_closed':False,'production_gate_open':False,'goal_complete':False}
(out/'candidate-observation.json').write_text(json.dumps(observation,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'current':summary(current_path)['counts'],'added':len(comparison['added']),'removed':len(comparison['removed']),'changed':len(comparison['changed']),'failed_id_set_equal':comparison['failed_id_set_equal']},ensure_ascii=False))
