"""Synchronize current source-only repair status without rebinding old binaries."""
from pathlib import Path
import copy, hashlib, json, os, subprocess, sys

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/arrival-mode-downgrade-01a11897'
OUT = BASE / 'metadata-checkpoint'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
REVIEW = ROOT / '_workflow/final-ui-01a11220/arrival-revision-01a11808/fixed-review-1/review'
INTERRUPTED = ROOT / '_workflow/final-ui-01a11220/own-runtime/startup-supplement-01a11897/interrupted-terminal.json'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s

sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda p: json.loads(p.read_bytes())
version_path = PRODUCT / 'VERSION.json'; readme_path = PRODUCT / '版本与启动说明.md'
with s.Session(ROOT, 'arrival-mode-source-only-version-status-01a11897') as budget:
    old_roots = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old_roots)); assert set(old_roots) == set(budget.old_roots)
    for path in [OUT, version_path, readme_path]: budget.track(path)
    OUT.mkdir(exist_ok=False)
    before = {p: p.read_bytes() for p in [version_path, readme_path]}
    assert all(not b.startswith(b'\xef\xbb\xbf') and b'\r\n' not in b for b in before.values())
    version = load(version_path); old = copy.deepcopy(version)
    evidence = load(BASE / 'verification.json'); capture = load(REVIEW / 'capture-observation.json'); report = load(REVIEW / 'report.json')
    assert capture['verdict'] == report['verdict'] == 'blocked'
    assert capture['all_original_keys_disposed'] and not capture['schema_observations'] and not capture['frozen_drift']
    assert capture['model'] == 'gpt-6.1-sol' and capture['effort'] == 'high' and capture['used'] == 1 and capture['remaining'] == 0
    insertion = next(x for x in report['findings'] if x['id'] == 'PATH-ARRIVAL-REVISION-INSERT-1')
    mode = next(x for x in report['findings'] if x['id'] == 'PATH-ARRIVAL-MODE-DOWNGRADE-1')
    assert insertion['status'] == 'closed' and mode['status'] == 'open' and mode['severity'] == 'important'
    assert all(sha((ROOT / p).read_bytes()) == h for p, h in evidence['source_sha256'].items())
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in version['assistant_modules'])
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == version['bgi_dll_sha256']
    assert sha((PRODUCT / 'BetterGI.exe').read_bytes()) == version['bgi_exe_sha256']
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    assert head == 'e55e3272c3b56a5d1b3ba3d71f1fd8f20ddb371c'
    for path, data in before.items(): s.write(OUT / 'before' / path.name, data)
    version['kind'] = 'existing full candidate binaries; insertion finding independently closed; mode downgrade repair is source-only and pending independent verification'
    version['historical_independent_review'] = copy.deepcopy(version['independent_review'])
    version['independent_review'] = dict(request_id=capture['request_id'], model=capture['model'], effort=capture['effort'], status='completed', used=1, remaining=0,
        old_fixed_grant_used=2, old_fixed_grant_remaining=0, verdict=report['verdict'], source_candidate_verdict=report['source_candidate_verdict'],
        report=str(REVIEW / 'report.json'), report_sha256=sha((REVIEW / 'report.json').read_bytes()), capture=str(REVIEW / 'capture-observation.json'),
        child_id=capture['child_id'], completed_timestamp=capture['completed_timestamp'], snapshot_sha256=capture['snapshot_sha256'],
        applies_to_current_candidate=False, applies_to_current_product_source_identity=True, verdict_scope='frozen source before e55 mode repair; original report unchanged; ordinary provenance only',
        subsequent_source_change='PATH-ARRIVAL-MODE-DOWNGRADE-1', manual_acceptance_pending=True, product_complete=False)
    version['last_independently_reviewed_source'] = 'E:/CodexReviewSnapshots/terminal-candidate-20261004/' + capture['request_id']
    version['last_independently_reviewed_snapshot_sha256'] = capture['snapshot_sha256']
    original_repair = copy.deepcopy(version['current_repair'])
    original_repair['independent_verification'] = 'original insertion finding closed by native review 8c795c4e92274393b1d148a722787014; limited to that finding and evidence'
    original_repair['status'] = insertion['status']
    original_repair['closure_report'] = str(REVIEW / 'report.json')
    original_repair['actual']['independent_verification_pending'] = False
    version['arrival_revision_repair'] = original_repair
    version['current_repair'] = dict(id=mode['id'], severity=mode['severity'], source_implemented=True, product_implemented=False, source_checkpoint=head,
        status=evidence['status'], source_sha256=evidence['source_sha256'], independent_verification='pending; fixed-one grant exhausted; no additional authorization or request',
        targeted_evidence=str(BASE / 'verification.json'), owner_checkpoint=str(BASE / 'OWNER-CHECKPOINT.json'), actual_verified=False, new_product_refresh=False,
        actual_runtime_pending=True, original_blocked_report_preserved=True, product_complete=False)
    if insertion['id'] not in version['current_source_blockers_resolved']: version['current_source_blockers_resolved'].append(insertion['id'])
    version['current_source_blockers_resolved_scope'] = 'only listed original findings at their independently reviewed sources; mode repair and complete product acceptance remain pending'
    version['complete_product_goal_status'] = 'active at this metadata checkpoint; not achieved; source-only mode repair awaiting bounded independent verification'
    version['user_action_now'] = 'original fixed-one review has completed; new direct fixed-one authorization for mode-repair verification remains pending; do not request game feedback'
    interrupted = load(INTERRUPTED)
    assert interrupted['own_job_active_processes'] == 0 and interrupted['own_startup_config_exactly_restored'] and interrupted['own_assistant_config_exactly_restored']
    version['interrupted_supplements'] = [dict(kind='startup-supplement-01a11897', state='user_interrupted_and_restored; not acceptance', evidence=str(INTERRUPTED), sha256=sha(INTERRUPTED.read_bytes()), controller_exit=1, active_processes=0, source_modified=False, actual_verified=False)]
    version['product_complete'] = False
    unchanged = ['source_checkpoint', 'evidence_checkpoint', 'compiled_input_hashes', 'assistant_modules', 'bgi_dll_sha256', 'bgi_exe_sha256', 'actual_scope', 'current_actual_evidence', 'additional_non_game_actual', 'assistant_data_root', 'original_review_applies_to_current_candidate']
    assert all(version[k] == old[k] for k in unchanged)
    readme = before[readme_path].decode('utf-8')
    paragraph = '本套已加入8cab51bfa的等待后到达边界修复，新产物正式UI已实际完成插入节点、保存新修订、边界重载及新增节点到达。源码反例/回归/有效突变与恢复、原失败和新实际来源由VERSION.json绑定。原Sol/high源码pass只对应原版本，本次新增六行修复仍待独立复核；固定grant已2/2、余额0，整版Goal和完整实机验收保持未完成。'
    assert readme.count(paragraph) == 1
    current = '本套物理产物仍绑定8cab51bfa的等待后到达边界修复，助手DLL为b56d5bb4；原插入漏步已由唯一追加Sol/high原生复核闭合。该次报告整体为blocked，因为发现等待中把路径改为顺序链可能误执行新资源（PATH-ARRIVAL-MODE-DOWNGRADE-1）。此问题已在源码检查点e55e3272c修复，反向突变有效，恢复后回归313通过、原2项LocalWait失败保持；该源码候选尚未再次独立复核，产品模块没有刷新，也没有新候选实际入口验收。旧grant2/2和新增grant1/1均已用完，整版Goal仍未完成。'
    readme = readme.replace(paragraph, current)
    readme += '\n当前产品的已知关联风险：等待中的路径流程不可删除全部路径节点后改成无路径顺序链继续旧运行。应先通过正式停止入口确认旧运行已终态，再按修改后定义显式创建新运行；未知或未确认退出仍保持原保护。源码候选中的模式拒绝尚未进入本目录模块，不能把候选测试当成产品已修复。\n\n本聊天启动中心补验被用户实体Escape中断，控制器exit1、自有Job进程0，原自有启动与助手配置精确恢复；未执行启动流程，不记验收成功。此前startup-center-01a11808的有限历史证据仍保留，与本次中断分开。\n'
    updates = {version_path: json.dumps(version, ensure_ascii=False, indent=2).encode('utf-8') + b'\n', readme_path: readme.encode('utf-8')}
    for path, data in updates.items():
        assert path.read_bytes() == before[path]
        temporary = path.with_name(path.name + '.metadata-own-01a11897.tmp')
        s.write(temporary, data); os.replace(temporary, path); assert path.read_bytes() == data
    check = load(version_path); assert all(check[k] == old[k] for k in unchanged)
    assert all(sha((PRODUCT / m['path']).read_bytes()) == m['sha256'] for m in check['assistant_modules'])
    records = [dict(path=str(path), before_bytes=len(before[path]), after_bytes=len(updates[path]), before_sha256=sha(before[path]), after_sha256=sha(updates[path]), format='UTF-8 without BOM, LF') for path in updates]
    s.write(OUT / 'verification.json', json.dumps(dict(kind='documentation and metadata correction only; no new review or build', source_candidate_checkpoint=head, files=records, product_identity_fields_preserved=unchanged, product_modules_unchanged=True, raw_report_unchanged=True, original_insertion_finding_closed=True, new_mode_finding_pending_independent_verification=True, startup_supplement_not_accepted=True, used=1, remaining=0, new_dispatches=0, product_complete=False), ensure_ascii=False, indent=2).encode('utf-8'))
    print('VERSION METADATA UPDATED; binary/source identities remain separate; no new review/build/UI; full product pending', flush=True)
