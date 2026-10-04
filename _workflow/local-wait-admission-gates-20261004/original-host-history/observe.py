from pathlib import Path
import hashlib,json,subprocess,xml.etree.ElementTree as ET
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004';out=base/'original-host-history'
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def trx(path):
 tree=ET.parse(path)
 return {r.get('testId'):{'test_id':r.get('testId'),'name':r.get('testName'),'outcome':r.get('outcome'),'message':r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),'stack':r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)} for r in tree.findall('.//t:UnitTestResult',ns)}
def union(paths):
 result={}
 for path in paths:
  for key,row in trx(base/path).items():
   if key in result:assert result[key]['name']==row['name'] and result[key]['outcome']==row['outcome'],(key,path)
   result[key]=row
 return result
oldpaths=['original-send-round/final-full.trx','original-send-round/final-targeted-and-compat.trx']
newpaths=['original-host-history/final-full.trx','original-host-history/final-targeted-and-compat.trx']
old=union(oldpaths);current=union(newpaths)
comparison={'kind':'exact testId union comparison, ordinary TRX observation','baseline_paths':oldpaths,'current_paths':newpaths,'baseline_count':len(old),'current_count':len(current),'added':[current[i] for i in sorted(current.keys()-old.keys())],'removed':[old[i] for i in sorted(old.keys()-current.keys())],'changed':[{'before':old[i],'after':current[i]} for i in sorted(old.keys()&current.keys()) if old[i]['outcome']!=current[i]['outcome']],'counts':{k:sum(r['outcome']==k for r in current.values()) for k in ['Passed','Failed','NotExecuted']},'same_failed_test_ids':{i for i,r in old.items() if r['outcome']=='Failed'}=={i for i,r in current.items() if r['outcome']=='Failed'}}
(out/'impact-comparison.json').write_text(json.dumps(comparison,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
sources=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowExecutionBoundary.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/HistoricalExecutionObservationTests.cs']
facts=[]
for path in sources:
 b=(root/path).read_bytes();facts.append({'path':path,'bytes':len(b),'lines':len(b.splitlines()),'sha256':hashlib.sha256(b).hexdigest(),'bom':b.startswith(b'\xef\xbb\xbf'),'crlf_lines':b.count(b'\r\n'),'lf_bytes':b.count(b'\n')})
(out/'candidate-source-set.json').write_text(json.dumps({'owned':sources,'facts':facts},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
mutations=json.loads((out/'mutations-r2/observations.json').read_text(encoding='utf-8'));assert len(mutations['records'])==5
for m in mutations['records']:assert m['source_sha256']==m['restored_sha256']==hashlib.sha256((root/m['source']).read_bytes()).hexdigest()
inherited=[]
for p in (base/'original-send-round').rglob('*'):
 if p.is_file() and p.suffix in ['.log','.trx','.json','.md']:
  b=p.read_bytes();row={'path':str(p.relative_to(base)).replace('\\','/'),'bytes':len(b),'sha256':hashlib.sha256(b).hexdigest()}
  if p.suffix=='.trx': row['counts']={k:sum(r['outcome']==k for r in trx(p).values()) for k in ['Passed','Failed','NotExecuted']}
  elif p.suffix in ['.log','.md']:b.decode('utf-8-sig',errors='replace')
  else:json.loads(b.decode('utf-8-sig'))
  inherited.append(row)
(out/'inherited-evidence-read.json').write_text(json.dumps(inherited,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
obs={'kind':'ordinary process/TRX/source-byte observation, not authenticated receipt, independent pass or product acceptance','thread_id':'01a105a5-74ae-7820-8451-15ae698d8afd','head_before_checkpoint':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'candidate_sources':facts,'final_build_exit':int((out/'final-build-exit.txt').read_text(encoding='utf-8-sig')),'final_full_test_exit':int((out/'final-full-test-exit.txt').read_text(encoding='utf-8-sig')),'final_targeted_exit':int((out/'final-targeted-and-compat-exit.txt').read_text(encoding='utf-8-sig')),'counts':comparison['counts'],'new_tests':len(comparison['added']),'removed':len(comparison['removed']),'changed':len(comparison['changed']),'same_failed_ids':comparison['same_failed_test_ids'],'mutations':'original-host-history/mutations-r2/observations.json','mutations_count':5,'source_bytes_restored':True,'independent_requests_added':0,'original_obligations':'G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important/implementation/open retained','production_gate_open':False,'goal_complete':False,'product':{'path':str((base/'g10-completion/products/MultiplayerHoeingAssistant.UnitTest.dll').relative_to(root)).replace('\\','/'),'sha256':hashlib.sha256((base/'g10-completion/products/MultiplayerHoeingAssistant.UnitTest.dll').read_bytes()).hexdigest()},'limitations':['Real Host/Runner/RunStore/LeaseStore with controlled execution port; no real IPC/game/User acceptance. Physical archive fixture moves complete settled Operations under the actual lease mutation boundary with aged timestamp; it is not 24h real waiting.','Original history/outcome serialized facts retained; unknown/unanchored facts never reconstructed. All originally required multi-round conflict/late acceptance and typed parent/capacity/real-entry/synchronous gate dependency work remains to be fully integrated and independently reviewed.','Publication/settlement exception, reopen initialization and archived false-success defects have red/fix evidence. Initial fixture compile errors and invalid non-owner lease red attempt retained; they are not semantic red evidence.','Original full TaskCenter failures retained by exact ID; no permanent failure exemption. Original manifest evidence audit blocked, policy=false/native planpass constraints retained; no fake receipt or independent pass.']}
introduced=[r for r in comparison['added'] if '.HistoricalExecutionObservationTests.' in r['name'] or '.OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure(' in r['name']]
obs.pop('new_tests');obs['new_to_current_union']=len(comparison['added']);obs['introduced_tests']=len(introduced);obs['additional_existing_scope_tests']=len(comparison['added'])-len(introduced)
obs['full_trx_counts']={k:sum(r['outcome']==k for r in trx(out/'final-full.trx').values()) for k in ['Passed','Failed','NotExecuted']}
obs['targeted_trx_counts']={k:sum(r['outcome']==k for r in trx(out/'final-targeted-and-compat.trx').values()) for k in ['Passed','Failed','NotExecuted']}
(out/'candidate-observation.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps({'counts':comparison['counts'],'new_to_union':len(comparison['added']),'introduced_tests':len(introduced),'additional_existing_scope_tests':len(comparison['added'])-len(introduced),'removed':len(comparison['removed']),'changed':len(comparison['changed']),'same_failed_ids':comparison['same_failed_test_ids']},ensure_ascii=False))
