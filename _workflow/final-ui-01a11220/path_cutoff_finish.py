from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2]
WORK=Path(__file__).resolve().parent/'path-cutoff-01a1176e'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
REVIEW=ROOT/'_workflow/local-wait-admission-gates-20261004/delivery-review-20261007/review-2'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
load=lambda p:json.loads(p.read_bytes())
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
report=load(REVIEW/'report.json');capture=load(REVIEW/'capture-observation.json')
assert report['verdict']==report['source_candidate_verdict']=='pass'
assert report['manual_acceptance_pending'] is True and capture['all_original_keys_disposed'] and not capture['schema_observations'] and not capture['frozen_drift']
assert {f['id'] for f in report['findings'] if f['status']=='resolved_with_evidence'}=={'PATH-STRUCTURAL-LOOP-ENTRY-1','LOOP-CUTOFF-TIMED-NODE-1'}
sources=load(WORK/'restored/source-hashes.json');assert all(sha(ROOT/name)==digest for name,digest in sources.items())
version=load(PRODUCT/'VERSION.json');assert all(sha(PRODUCT/row['path'])==row['sha256'] for row in version['assistant_modules'])
assert sha(PRODUCT/'BetterGI.dll')==version['bgi_dll_sha256'] and sha(PRODUCT/'BetterGI.exe')==version['bgi_exe_sha256']
user_after=load(WORK/'product-actual/private/product-user-after.json')
actual_user={f.relative_to(PRODUCT/'User').as_posix():sha(f) for f,_ in s.files_under(PRODUCT/'User')}
assert actual_user==user_after
version['kind']='current same-product source-reviewed version; whole-product runtime acceptance pending'
version['independent_review'].update(status='completed',verdict='pass',source_candidate_verdict='pass',manual_acceptance_pending=True,report=str(REVIEW/'report.json'),report_sha256=sha(REVIEW/'report.json'),capture=str(REVIEW/'capture-observation.json'),child_id=capture['child_id'],completed_timestamp=capture['completed_timestamp'])
version.update(product_complete=False,complete_product_goal_status='active; not achieved',current_source_blockers_resolved=['PATH-STRUCTURAL-LOOP-ENTRY-1','LOOP-CUTOFF-TIMED-NODE-1'],original_failures_and_grades_retained=True,user_action_now='none; await voluntary manual feedback')
readme=(PRODUCT/'版本与启动说明.md').read_text(encoding='utf-8')
old='当前最后一次独立集中复核正在进行，完整版本尚未宣布验收完成。此目录使用修复后的助手模块，源码/实际来源由同目录 VERSION.json 绑定。原候选索引和旧报告保持原样。'
new='最后一次 gpt-6.1-sol/high 独立源码与实现证据复核已通过，两项本版执行风险按原重要级闭合。此目录使用修复后的助手模块，源码/实际来源与原始报告由同目录 VERSION.json 绑定。完整实机验收仍待主动反馈，整版 Goal 保持未完成；原候选索引、失败和旧 blocked 报告保持原样。'
assert readme.count(old)==1;readme=readme.replace(old,new)
guide=ROOT/'Docs/technical/mistletoe-startup-migration-recovery.md';guide_bytes=guide.read_bytes()
old='第1次综合复核原报告保持 blocked，本次修复待剩余一次集中复核；完整版本尚未宣布交付。'.encode()
new='第1次综合复核原报告保持 blocked；第2次 gpt-6.1-sol/high 已对本次两项重要修复给出源码/实现证据 pass，完整实机验收仍 pending，整版 Goal 未完成。'.encode()
assert guide_bytes.count(old)==1;guide_updated=guide_bytes.replace(old,new)
refs=[REVIEW/'report.json',REVIEW/'original-final.txt',REVIEW/'native-rollout.jsonl',REVIEW/'capture-observation.json',WORK/'REPAIR.md',WORK/'ACTUAL.md',WORK/'AUDIT.md',WORK/'restored/source-hashes.json',WORK/'restored/products.json',WORK/'restored/result.json',WORK/'restored/path-cutoff.trx',WORK/'product-refresh/result.json',WORK/'product-actual/result.json',WORK/'product-actual/actual-behavior.json',WORK/'product-actual/fixtures-withdrawn.json',WORK/'product-actual/private/native-ui-source.jsonl']
index=[dict(path=str(p),sha256=sha(p),bytes=p.stat().st_size) for p in refs]
closure=dict(kind='source repair and owned support closeout; complete product acceptance pending',source_checkpoint=version['source_checkpoint'],material_checkpoint=version['evidence_checkpoint'],source_candidate_verdict='pass',independent_review=capture,regression=dict(build=0,passed=303,original_failed=2,other=0,new_cases_passed=11),mutations=dict(entry_failed=5,cutoff_failed=5,exact_source_restoration=True),owned_actual=load(WORK/'product-actual/actual-behavior.json'),data_preservation=dict(user_files=len(actual_user),changed=[],removed=[]),ordinary_issues=['cold cancelled feedback Unavailable','drag time precision','collection collapse interaction'],pending_manual=['eight native and group/JS/macro game effects','resource-execution SkipCurrent','account/redeem/preparation/team/co-op effects','game/combined exit and OS shutdown'],further_source_work_required_by_current_report=False,full_product_goal_complete=False,legacy_receipt_or_permit_claimed=False,review_used=2,review_remaining=0,original_report_grades_openings_failures_preserved=True,unrelated_materials_not_committed=True,next='await voluntary actual game/resource/effect feedback; no new certification/tool/loop project',user_action_now='none')
with s.Session(ROOT,'same-product-final-source-closeout-01a1176e') as budget:
    old_roots=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old_roots));assert set(old_roots)==set(budget.old_roots)
    budget.track(WORK/'delivery');budget.track(PRODUCT/'VERSION.json');budget.track(PRODUCT/'版本与启动说明.md')
    s.write(PRODUCT/'VERSION.json',json.dumps(version,ensure_ascii=False,indent=2).encode(),mode='wb')
    s.write(PRODUCT/'版本与启动说明.md',readme.encode(),mode='wb')
    s.write(WORK/'delivery/VERSION.json',json.dumps(version,ensure_ascii=False,indent=2).encode())
    s.write(WORK/'delivery/README.md',readme.encode())
    s.write(WORK/'delivery/启动本机槲寄生.cmd',(PRODUCT/'启动本机槲寄生.cmd').read_bytes())
    s.write(WORK/'delivery/evidence-index.json',json.dumps(index,ensure_ascii=False,indent=2).encode())
    s.write(WORK/'delivery/CLOSEOUT.json',json.dumps(closure,ensure_ascii=False,indent=2).encode())
    assert guide.read_bytes()==guide_bytes;guide.write_bytes(guide_updated);assert guide.read_bytes()==guide_updated
    assert load(PRODUCT/'VERSION.json')==version
    print('current source pass and original pending manual boundary recorded; same product ready, total Goal not completed',flush=True)
