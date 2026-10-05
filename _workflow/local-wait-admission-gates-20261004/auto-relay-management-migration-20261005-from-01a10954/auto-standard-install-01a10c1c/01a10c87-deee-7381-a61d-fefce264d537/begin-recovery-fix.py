from pathlib import Path
import sys,json,hashlib
root=Path.cwd();base=Path(__file__).resolve().parent;sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowMigrationConsumerTests.cs']
with storage.Session(root,'bounded-migration-recovery-repair-opening') as budget:
    ev=base/'recovery-fix';budget.track(ev);budget.check(location=ev);ev.mkdir(exist_ok=False)
    before={}
    for rel in paths:
        data=(root/rel).read_bytes();storage.write(ev/(Path(rel).name+'.before.bin'),data)
        before[rel]=dict(bytes=len(data),lines=len(data.splitlines()),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=data.count(b'\r\n'),lf=data.count(b'\n'))
    storage.write(ev/'source-before.json',json.dumps(before,indent=2).encode())
    plan=root/'_workflow/local-wait-admission-gates-20261004/plan.json';old=plan.read_bytes();storage.write(ev/'plan-before.json',old)
    record=dict(policy='mistletoe-release-first-20261005-v2',finding='MIGRATION-RECOVERY-ENTRY-1',severity='important',status='open; directed repair, not independently closed',admission='正常迁移中断→原回退入口被执行LoadSnapshot隔离门挡住；用户无法恢复原流程/BGI标准根。独立bf73360e report保持blocked。本版功能最小修复仅恢复描述读取及原Host调用，执行/编辑隔离不变。红反例/身份拒绝/双根回归/PFP/新助手产物后转验证内容交付；实机由用户自己执行。',scope=paths,matrix=['reference update published before commit: execution/edit remain quarantined, original Host rollback restores baseline','activated but not committed: same recovery and repeat rollback','unknown/corrupt/wrong transaction/root/target: reject, preserve both roots','later edit: existing transaction ownership guards reject overwrite','second BGI rollback unconfirmed: report unavailable, never Effective'],review_budget='extra implementation2 used2 remaining0; no further request without finite authorization',protected='all live User/JS/AppData/.kiro/other staged and material-out untouched',next='red actual partial transaction fixture -> narrow metadata/Host repair -> Rebuild, targeted and full affected regression/PFP -> source-bound candidate and user validation instructions; independent repair followup pending budget')
    obj=json.loads(old.decode('utf-8-sig'));assert 'delivery_20261005_migration_recovery_fix' not in obj
    end=old.rfind(b'}');insert=json.dumps({'delivery_20261005_migration_recovery_fix':record},ensure_ascii=False,indent=2)[1:-1].strip().encode()
    new=old[:end].rstrip()+b',\r\n  '+insert+b'\r\n'+old[end:];json.loads(new.decode('utf-8-sig'));storage.write(plan,new,mode='wb')
    print(json.dumps(dict(source_files=len(before),plan_before=len(old),plan_after=len(new))),flush=True);budget.check(measure=True)
