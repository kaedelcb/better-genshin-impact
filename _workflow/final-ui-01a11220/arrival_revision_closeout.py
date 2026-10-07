"""Bind actual results and candidate instructions; preserve exhausted review grant and historical pass."""
from pathlib import Path
import hashlib, json, os, subprocess, sys
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808'
PRODUCT = ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA = ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
OLD = ROOT/'_workflow/final-ui-01a11220/own-runtime/cold-insert-01a11808'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda p: json.loads(p.read_bytes())
with s.Session(ROOT,'arrival-revision-candidate-actual-closeout-01a11808') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(BASE);budget.track(PRODUCT/'VERSION.json');budget.track(PRODUCT/'版本与启动说明.md')
    guide=ROOT/'Docs/technical/mistletoe-startup-migration-recovery.md';budget.track(guide)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    actual=load(BASE/'actual/actual-arrival.json');result=load(BASE/'actual/result.json')
    assert actual['actual_node_order']==['gate',actual['inserted_node_id'],'end'] and actual['terminal_state']=='Succeeded'
    assert result['exit_code']==0 and not result['product_user_changed'] and not result['real_user_changed']
    assert load(BASE/'actual/apps-tree-terminal.json')['active_processes']==0
    inputs=load(BASE/'restored/source-hashes.json');assert all(sha((ROOT/n).read_bytes())==h for n,h in inputs.items())
    version=load(PRODUCT/'VERSION.json');assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in version['assistant_modules'])
    assert not version['original_review_applies_to_current_candidate']
    for name in ['run-8386f7c03944.run.json','run-6e5697b907bd.run.json','run-f4b7a4d95adc.run.json']:
        assert (DATA/'runs'/name).read_bytes()==(BASE/'actual/private/before/runs'/name).read_bytes()
    write(BASE/'delivery/preserved-original-runs.json',dict(original_run_ids=['run-8386f7c03944','run-6e5697b907bd','run-f4b7a4d95adc'],byte_unchanged=True))
    version['current_repair'].update(actual_verified=True,actual=actual,independent_verification='pending; original grant exhausted',status='implemented_with_evidence_pending_independent_verification')
    version.update(current_actual_evidence=str(BASE/'actual/actual-arrival.json'),actual_scope=str(BASE/'ACTUAL.md'),product_complete=False,
        complete_product_goal_status='active; not achieved; fixed additional review grant exhausted',user_action_now='only owner decision on one fixed additional repair verification; do not request game feedback now')
    version['kind']='same full product with source/test/finite actual arrival repair; independent repair verification and full manual acceptance pending'
    s.write(BASE/'delivery/before/VERSION.json',(PRODUCT/'VERSION.json').read_bytes())
    s.write(PRODUCT/'VERSION.json',json.dumps(version,ensure_ascii=False,indent=2).encode(),mode='wb')
    write(BASE/'delivery/VERSION.json',version)
    actual_text=f'''# 同一产物的有限实际修复重演

源码候选检查点8cab51bfa，助手DLL {next(m['sha256'] for m in version['assistant_modules'] if m['path'].endswith('.dll'))}；BGI DLL/EXE保持原身份，实际Session1、显式NEXUSBGI_DATA_ROOT为既定独立助手根，两个EXE以各自目录启动。完整编译输入313项与restored/source-hashes.json逐SHA一致，原独立Sol/high pass仅绑定旧源码，新六行修复未独立复核，余额0。

原失败run-8386f7c03944保留：新路径gate→新增节点→end已保存并登记重载，旧产物实际却仅gate/end。原跨BGI纪元拒绝run-6e5697b907bd和冷恢复取消run-f4b7a4d95adc字节也保持，不重跑或修改原记录。原同BGI冷恢复在旧源码产物上实际读回nextDay次日同刻、原时刻/策略/epoch与零发送保持；本修复仅触及节点时间等待之后的到达边界，未改根触发等待或冷恢复准入，不将它外推为同时重启BGI/助手成功。

修复重演：正式UI启动新的{actual['run_id']}，原修订332dd1c8、gate等待05:30；三步添加新固定判断{actual['inserted_node_id']}，正式检查器连接gate.yes→新增节点、新增节点.yes→end，no未选支线保持。05:23:42保存修订81fd132c，05:24:11登记边界重载；等待期间原修订/时刻/零节点保持。05:30到达后真实结果为{actual['actual_node_order']}各一次，结果branchYes、branchYes、succeeded，新修订{actual['revision']}与落盘flow字节SHA一致，未选支线零到达、资源提交零，Succeeded终态。

正常退出助手及BGI，apps出口[0,0]、Job树0，协议恢复；product/User9501和真实Debug/User9522前后SHA集合无差异，未移真实目录、未改第三方JS、未启动游戏。实际run/flow、原生UI源、argv/进程来源和数据hash保留在actual/。

这证明本机判断/结束流程的插入→保存→节点边界重载→实际到达已成立，不代替八原生/组/JS/宏游戏效果、资源执行Skip、账号/兑换码/准备/队友或关机验收，也不代替新增源码独立复核。完整Goal未complete，原级发现/历史/报告/失败/预算原样保持。
'''
    s.write(BASE/'ACTUAL.md',actual_text.encode('utf-8'),mode='wb')
    readme=PRODUCT/'版本与启动说明.md';before=readme.read_bytes();s.write(BASE/'delivery/before/版本与启动说明.md',before)
    prefix='''# 2026-10-08 同套 BetterGI 与槲寄生产物

本套已加入8cab51bfa的等待后到达边界修复，新产物正式UI已实际完成插入节点、保存新修订、边界重载及新增节点到达。源码反例/回归/有效突变与恢复、原失败和新实际来源由VERSION.json绑定。原Sol/high源码pass只对应原版本，本次新增六行修复仍待独立复核；固定grant已2/2、余额0，整版Goal和完整实机验收保持未完成。

'''
    text=before.decode('utf-8');old_intro=text.index('双击同目录')
    updated=(prefix+text[old_intro:]).encode('utf-8');s.write(readme,updated,mode='wb')
    s.write(BASE/'delivery/README.md',updated)
    guide_before=guide.read_bytes();s.write(BASE/'delivery/before/mistletoe-startup-migration-recovery.md',guide_before)
    text=guide_before.decode('utf-8')
    start=text.index('本文对应正式 WPF 调度列表。');end=text.index('\n\n## 启动同一套',start)
    intro='本文对应正式 WPF 调度列表和当前同套BGI＋助手。8cab51bfa修复了等待期间保存的新路径晚于节点求值生效的问题；当前已完成源码回归、有效突变与精确恢复，以及正式UI插入/保存/边界重载/实际到达的有限重演。此前两根因及原Sol/high源码pass保持原证据，新增修复尚未独立复核，审查grant余额0；完整实机验收和整版Goal仍未完成。游戏反馈继续等待用户主动提供，不要求现在测试。'
    text=text[:start]+intro+text[end:]
    old_sentence='综合实现复核没有通过结论，不能据本文宣布整版验收完成。'
    assert text.count(old_sentence)==1
    text=text.replace(old_sentence,'原版本综合源码复核为pass，但新增到达边界修复尚未独立复核，不能据本文宣布整版验收完成。')
    anchor='正常退出后，重启仍使用同一份产物和同一个助手数据目录。'
    assert text.count(anchor)==1
    text=text.replace(anchor,'本轮已实际验证：BGI保持运行，仅正常退出并冷启助手，对原固定时刻实例显式恢复后按nextDay等待到次日同刻，再停止，零节点/提交。若同时重启BGI，上轮原记录恢复被拒绝的来源仍保留，尚未证明该场景可恢复；保留原运行和错误反馈，不手改记录或把另一份新启动记作原运行恢复。\n\n'+anchor)
    s.write(guide,text.encode('utf-8'),mode='wb')
    assert len(guide.read_bytes())>=len(guide_before) and b'\r\n' not in guide.read_bytes()
    refs=[BASE/'REPAIR.md',BASE/'ACTUAL.md',BASE/'source-patch.json',BASE/'red/result.json',BASE/'green/result.json',BASE/'negative/result.json',BASE/'negative-source-restored.json',BASE/'restored/result.json',BASE/'restored/source-hashes.json',BASE/'restored/products.json',BASE/'actual/refresh.json',BASE/'actual/actual-arrival.json',BASE/'actual/result.json',BASE/'actual/private/native-ui-source.jsonl',OLD/'ACTUAL.md',OLD/'result.json',OLD/'private/native-ui-source.jsonl']
    for stage in ['red','green','negative','restored']:
        for phase in ['build','test']:refs.append(BASE/stage/(phase+'-tree-terminal.json'))
    write(BASE/'delivery/evidence-index.json',[dict(path=str(p),sha256=sha(p.read_bytes()),bytes=p.stat().st_size) for p in refs])
    checkpoint=dict(kind='owner checkpoint after concrete source/test/actual repair; no added review authorized',finding_id='PATH-ARRIVAL-REVISION-INSERT-1',severity='important',
        status='implemented_with_evidence_pending_independent_verification',source_checkpoint=version['source_checkpoint'],actual=actual,red=dict(passed=4,failed=2),restored=dict(passed=309,original_failures=2,new_cases_passed=6),
        mutation=dict(failed=2,exact_restore=True),review_used=2,review_remaining=0,proposed_fixed_additional_requests=1,proposed_scope='Sol/high read-only repair verification of this arrival revision defect and affected path/stop/deadline guards; current same candidate and original relevant obligations, no renewed whole-history/tool/SDK work',
        original_reports_and_grades_unchanged=True,all_original_runs_preserved=True,product_complete=False,goal_complete=False,
        pending_manual=['eight native and group/JS/macro game effects','resource SkipCurrent','account/redeem/preparation/team/co-op effects','game/combined exit and OS shutdown'],automatic_review_renewal=False,new_reviews=0,
        handoff_decision='continue current chat at owner checkpoint; no new independent implementation objective/context risk and no writers in flight; do not resume source paused Goal')
    write(BASE/'delivery/OWNER-CHECKPOINT.json',checkpoint)
    print('CANDIDATE SOURCE/ACTUAL/INSTRUCTIONS BOUND; fixed grant remains exhausted; no current independent pass or whole-product completion claimed',flush=True)
