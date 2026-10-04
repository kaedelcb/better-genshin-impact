from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent;thread='01a1061f-79a1-7b62-a8d2-bc46334403e3';p=Path('C:/Users/Administrator/.codex/sessions/2026/10/04/rollout-2026-10-04T16-54-48-'+thread+'.jsonl');items=[json.loads(x) for x in p.read_text(encoding='utf-8').splitlines()];ordinal,c=[(i+1,x['payload']) for i,x in enumerate(items) if x.get('type')=='turn_context'][-1]
assert c['model']==c['collaboration_mode']['settings']['model']=='gpt-6.1-sol';assert c['effort']==c['collaboration_mode']['settings']['reasoning_effort']=='medium';assert c['cwd']==str(r)
read=json.loads((d/'new-goal-readback.json').read_text(encoding='utf-8-sig'));goal=read['goal'];handshake=json.loads((d/'new-handshake-observation.json').read_text(encoding='utf-8-sig'));assert goal['threadId']==handshake['threadId']==thread and goal['status']=='active';assert handshake['actualCwd']==str(r);assert handshake['marker']=='AUTO-CAPACITY-IDENTITY-RELAY-20261004-FROM-01a105f1';assert handshake['model']==c['model'] and handshake['effort']==c['effort']
assert all(x in goal['objective'] for x in ['relay-prompt.txt','HANDOFF.md','CURRENT-HANDOFF.md','33','G4','G7','C20','数据保留','独立综合后审','生产门'])
def strings(x):
 if isinstance(x,str):yield x
 elif isinstance(x,list):
  for y in x:yield from strings(y)
 elif isinstance(x,dict):
  for y in x.values():yield from strings(y)
observations=[];decoder=json.JSONDecoder()
for i,x in enumerate(items):
 payload=x.get('payload',{})
 if x.get('type')!='response_item' or payload.get('type') not in ['custom_tool_call_output','function_call_output']:continue
 for output in strings(payload.get('output')):
  pos=0
  while (pos:=output.find('{"goal"',pos))>=0:
   try:value,_=decoder.raw_decode(output[pos:])
   except json.JSONDecodeError:pos+=1;continue
   native=value.get('goal')
   if isinstance(native,dict) and native.get('threadId')==thread and native.get('status')=='active' and native.get('objective')==goal['objective']:
    observations.append(dict(ordinal=i+1,call_id=payload.get('call_id'),nativeGoal=native))
   pos+=1
assert observations,'No matching native Goal output in successor rollout'
delegations=[(i+1,x['payload']['output']) for i,x in enumerate(items) if x.get('type')=='response_item' and x.get('payload',{}).get('type')=='function_call_output' and x['payload'].get('namespace')=='codex_app' and x['payload'].get('name')=='create_thread']
assert len(delegations)==1
delegationOrdinal,delegation=delegations[0]
assert '<source_thread_id>01a105f1-d84c-75b3-9b49-58d83c84e1d7</source_thread_id>' in delegation
actualInput=delegation.split('<input>',1)[1].split('</input>',1)[0]
expectedInput=(d/'relay-prompt.txt').read_text(encoding='utf-8')
assert actualInput.strip().replace('\r\n','\n')==expectedInput.strip().replace('\r\n','\n'),'Delegated initial prompt differs from complete saved prompt'
assert handshake['marker'] in actualInput
obs=dict(source_thread_id='01a105f1-d84c-75b3-9b49-58d83c84e1d7',successor_thread_id=thread,host_id='local',marker=handshake['marker'],actual_cwd=c['cwd'],actual_model=c['model'],actual_effort=c['effort'],settings_model=c['collaboration_mode']['settings']['model'],settings_effort=c['collaboration_mode']['settings']['reasoning_effort'],rollout=str(p),context_ordinal=ordinal,nativeGoalObservations=observations,delegation_ordinal=delegationOrdinal,complete_initial_prompt_matches_saved_text=True,marker_received_in_native_delegation=True,completeScopeBindingVerified=True,newGoalStatus='active',sourceGoalStatusFromNativeSavedReadback=json.loads((d/'old-goal-paused-readback.json').read_text(encoding='utf-8'))['goal']['status'],newGoalFileSHA=hashlib.sha256((d/'new-goal-readback.json').read_bytes()).hexdigest(),handshakeFileSHA=hashlib.sha256((d/'new-handshake-observation.json').read_bytes()).hexdigest(),oldSourceWritingStopped=True,productGoalComplete=False,productionGateOpen=False)
assert obs['sourceGoalStatusFromNativeSavedReadback']=='paused';(d/'successor-handshake-verification.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8');print('Successor native active Goal, marker, cwd and actual sol/medium verified')
