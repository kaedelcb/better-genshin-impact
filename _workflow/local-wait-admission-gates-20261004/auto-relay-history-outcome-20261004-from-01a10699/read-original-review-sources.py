from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent;rows=[]
paths=[]
native=r/'_workflow/local-wait-admission-gates-20261004/native-review/requests/9f85a4b85f46400dbff20b97ebec4fda'
paths += list(native.glob('dispatch-failure-*.json'))+[native/'request.json',native/'cli-events.jsonl',native/'cli-stderr.log']
g10=r/'_workflow/local-wait-admission-gates-20261004/g10-completion'
paths += [g10/'independent-audit-original.md',g10/'independent-audit-original-event.json',g10/'independent-audit-source.json']
for ident in ['d25e7c4789e14b13be9e99edc06c60cc','f82d494fb39b44368bc79fe68b58411b']:
 folder=r/'_workflow/usable-delivery-20261003/control-native/requests'/ident
 paths += [folder/n for n in ['request.json','report.json','receipt.json','prior.json','raw-final.txt']]
for p in paths:
 b=p.read_bytes();obj=dict(path=str(p.relative_to(r)),bytes=len(b),sha256=hashlib.sha256(b).hexdigest())
 if p.suffix=='.json':
  value=json.loads(b.decode('utf-8-sig'));obj['keys']=list(value) if isinstance(value,dict) else None
  if isinstance(value,dict):
   obj['identity_fields']={k:value[k] for k in ['request_id','stage','outcome','attempt','channel','verdict','snapshot_hash','permission_scope'] if k in value}
   if 'findings' in value: obj['findings']=[{k:x[k] for k in ['id','severity','obligation','status'] if k in x} for x in value['findings']]
   if 'prior_dispositions' in value:obj['prior_dispositions']=value['prior_dispositions']
   if p.name=='receipt.json':
    folder=p.parent
    for name,key in [('report.json','report_sha256'),('request.json','request_sha256'),('raw-final.txt','raw_final_sha256')]:
     if key in value:obj[key+'_matches_actual']=hashlib.sha256((folder/name).read_bytes()).hexdigest()==value[key]
    obj['observed_native_identity']=value.get('native_identity')
 rows.append(obj)
source=json.loads((g10/'independent-audit-source.json').read_text(encoding='utf-8-sig'))
rows.append(dict(g10_report_matches_source=source['report_sha256']==hashlib.sha256((g10/'independent-audit-original.md').read_bytes()).hexdigest(),contexts=source['contexts'],thread_id=source['thread_id']))
(d/'original-review-source-read.json').write_text(json.dumps(dict(kind='original-source readback, not new request or complete historical budget reconciliation',known_local_attempts=4,known_local_independent_source_audits=1,control_native_plan_requests_read=2,full_G_control_R56_budget_reconciliation_complete=False,rows=rows),ensure_ascii=False,indent=2),encoding='utf-8')
print('original sources read',len(paths),'known local 4 failed+1 independent; history not reset; complete budget remains unresolved',flush=True)
