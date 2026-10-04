from pathlib import Path
import json,hashlib
r=Path.cwd(); d=Path(__file__).parent
roots=['_workflow/local-wait-admission-gates-20261004/native-review/requests','_workflow/usable-delivery-20261003/control-native/requests','_workflow/r56-reference-activation-wiring-2026-09-29/review-process/requests','_workflow/r56-reference-activation-wiring-2026-09-29/native-recovery-20261001/native/requests','_workflow/r56-reference-activation-wiring-2026-09-29/native-recovery-20261001/native-compatibility-v9/requests']
families=[]
for root in roots:
    rows=[]
    for p in sorted((r/root).glob('*/request.json')):
        b=p.read_bytes(); j=json.loads(b.decode('utf-8-sig')); report=p.with_name('report.json'); receipt=p.with_name('receipt.json')
        row=dict(path=str(p.relative_to(r)),sha256=hashlib.sha256(b).hexdigest(),request_id=j.get('request_id'),stage=j.get('stage'),report_exists=report.is_file(),receipt_exists=receipt.is_file())
        for name,q in [('report',report),('receipt',receipt)]:
            if q.is_file():
                qb=q.read_bytes(); qj=json.loads(qb.decode('utf-8-sig')); row[name]=dict(sha256=hashlib.sha256(qb).hexdigest(),verdict=qj.get('verdict'),snapshot_hash=qj.get('snapshot_hash'))
        rows.append(row)
    families.append(dict(root=root,canonical_request_files=len(rows),requests=rows))
regs=[]
for p in sorted((r/'.git/mistletoe-review').glob('*/registration.json')):
    b=p.read_bytes(); j=json.loads(b.decode('utf-8-sig')); regs.append(dict(path=str(p.relative_to(r)),sha256=hashlib.sha256(b).hexdigest(),batch=j.get('batch'),owner_root=j.get('owner_root'),history=j.get('history'),history_reconciliation=j.get('history_reconciliation')))
obs=dict(kind='read-only original request inventory, not a budget grant or completed reconciliation',families=families,registrations=regs,local_known_charged_dispatches=5,local_note='four charged failures for one original request plus G10 independent audit; original G/control/R56 ownership and historical per-batch budgets still must be reconciled before dispatch',new_requests=0,full_budget_reconciliation_complete=False)
(d/'original-request-ledger-inventory.json').write_text(json.dumps(obs,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps([dict(root=f['root'],canonical_requests=f['canonical_request_files']) for f in families]))
