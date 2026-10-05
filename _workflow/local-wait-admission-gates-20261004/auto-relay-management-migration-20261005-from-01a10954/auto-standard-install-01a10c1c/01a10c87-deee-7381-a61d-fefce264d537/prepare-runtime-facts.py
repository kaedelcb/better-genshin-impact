from pathlib import Path
import sys,os,json,hashlib,stat,shutil
root=Path.cwd();base=Path(__file__).resolve().parent
sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
candidate=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
source=Path('D:/DOWN/BetterGI_v0.64.2+lcb.22.8-NexusBGI-fix2/BetterGI/GameTask/AutoHoeing/Assets')
target=candidate/'GameTask/AutoHoeing/Assets'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
with storage.Session(root,'unified-runtime-resource-and-recovery-preparation') as budget:
    budget.track(base/'runtime-facts');budget.track(target);budget.track(base/'private-user-backup')
    budget.check(location=candidate);ev=base/'runtime-facts';ev.mkdir(exist_ok=False)
    assert not target.exists(),'refuse overwriting runtime resource root'
    resources=[]
    for p,s in storage.files_under(source):
        rel=p.relative_to(source);data=p.read_bytes();storage.write(target/rel,data)
        resources.append(dict(path=rel.as_posix(),bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),source=str(p)))
    for row in resources:assert sha(Path(row['source']))==row['sha256']==sha(target/row['path'])
    storage.write(ev/'supplemental-route-resources.json',json.dumps(resources,indent=2).encode())
    # Local recovery material contains private configuration; never sent to review.
    appdata=Path(os.environ['APPDATA'])/'NexusBGI';backup=base/'private-user-backup'
    rows=[]
    for p,s in storage.files_under(appdata):
        rel=p.relative_to(appdata);data=p.read_bytes();q=backup/rel;storage.write(q,data)
        rows.append(dict(path=rel.as_posix(),bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),mtime_ns=s.st_mtime_ns,source=str(p)))
    for row in rows:assert sha(Path(row['source']))==row['sha256']==sha(backup/row['path'])
    storage.write(backup/'backup-index.json',json.dumps(rows,indent=2).encode())
    config=candidate/'User/OneDragon/验收-统一版本-01a10c87.json';document=json.loads(config.read_text(encoding='utf-8-sig'))
    names=list(document['TaskDefinitions'].values());assert len(names)==8 and len(document['TaskOrder'])==8
    storage.write(ev/'ui-persistence-observation.json',json.dumps(dict(first_pid=17560,second_pid=20084,windows_session=1,executable=str(candidate/'BetterGI.exe'),actual_ui_created=True,actual_normal_exit_and_restart=True,configuration=str(config),before_restart_sha256='511a2cfa5e8c61f7da63801a15cb68164530b1d48f1930928115ac5b5324c57e',after_restart_sha256=sha(config),task_names=names,task_order=document['TaskOrder'],game_execution=False,assistant_launched=False,shutdown_performed=False,route_resources_added=len(resources),route_bytes=sum(r['bytes'] for r in resources),private_backup_files=len(rows),private_backup_verified=True,old_appdata_modified=False,game_account_and_consumption_scope='pending explicit user response'),ensure_ascii=False,indent=2).encode('utf-8'))
    manifest=[dict(path=p.relative_to(candidate).as_posix(),bytes=s.st_size,sha256=sha(p)) for p,s in storage.files_under(candidate) if p.relative_to(candidate).parts[0] not in ['User','log']]
    storage.write(ev/'runtime-files-with-routes.json',json.dumps(manifest,indent=2).encode())
    plan=root/'_workflow/local-wait-admission-gates-20261004/plan.json';old=plan.read_bytes();storage.write(ev/'plan-before.json',old)
    record=dict(policy='mistletoe-release-first-20261005-v2',storage_policy='mistletoe-storage-limits-20261005-v1',thread_id='01a10c87-deee-7381-a61d-fefce264d537',goal='active native readback',admission='统一完整运行候选及完整验收：先前载体缺运行资源，用户无法使用原计划功能；当前源码对应BGI+Tools助手完整构建，受影响全量回归、固定资源与恢复材料、最后一次综合实现复核，随后同一产物全部实机功能。不是新包或预算。',current_must_do='九个当前失败的本版关联独立裁决；全部功能/正确账号任务/停止重启/数据责任保持/正常迁移与真实C17耐久验收；版本交付',deferred='普通无本版现实影响历史/工具原级open保留；不扩认证生态或退役功能',next='稳定统一候选最后Sol/high综合复核；用户账号/消耗/关机范围待答，仅关联实机操作暂停',build_evidence=(base/'build-r1').relative_to(root).as_posix(),regression_evidence=(base/'regression-r2').relative_to(root).as_posix(),runtime_evidence=ev.relative_to(root).as_posix(),independent_requests_this_chat=0,remaining_implementation_review=1,original_opening_and_budget_preserved=True)
    obj=json.loads(old.decode('utf-8-sig'));assert 'delivery_20261005_unified_runtime' not in obj
    insert=json.dumps({'delivery_20261005_unified_runtime':record},ensure_ascii=False,indent=2)[1:-1].strip().encode('utf-8')
    end=old.rfind(b'}');new=old[:end].rstrip()+b',\r\n  '+insert+b'\r\n'+old[end:]
    assert len(new)>len(old);json.loads(new.decode('utf-8-sig'));storage.write(plan,new,mode='wb')
    storage.write(ev/'plan-change-observation.json',json.dumps(dict(before_bytes=len(old),after_bytes=len(new),before_sha256=hashlib.sha256(old).hexdigest(),after_sha256=sha(plan)),indent=2).encode())
    print(json.dumps(dict(resources=len(resources),resource_bytes=sum(x['bytes'] for x in resources),private_backup_files=len(rows),runtime_files=len(manifest),plan_before_bytes=len(old),plan_after_bytes=len(new))),flush=True)
    budget.check(measure=True)
