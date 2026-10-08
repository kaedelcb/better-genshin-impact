"""Bind the reviewed local version to its actual scene and retain pending full acceptance."""
from pathlib import Path
import copy,hashlib,json,os,subprocess,sys
R=Path(__file__).resolve().parents[3];W=Path(__file__).resolve().parent;B=W/'final-one-review'
P=R/'_workflow/runtime-unified-01a10e1b/product';doc=R/'Docs/technical/mistletoe-startup-migration-recovery.md'
sys.path.insert(0,str(R/'tools/mistletoe'))
import storage_limits as s
import native_review as n
sha=lambda data:hashlib.sha256(data).hexdigest()
load=lambda p:json.loads(p.read_bytes())
report=load(B/'review/report.json');capture=load(B/'review/capture-observation.json')
assert report['verdict']==report['source_candidate_verdict']=='pass' and report['complete_product_acceptance']=='pending'
assert capture['all_original_keys_disposed'] and not capture['schema_observations'] and not capture['frozen_drift']
assert capture['used']==1 and capture['remaining']==0
closed={f['id'] for f in report['findings'] if f['severity']=='important' and f['obligation']=='implementation' and f['status']=='closed'}
assert closed=={'PATH-ARRIVAL-MODE-DOWNGRADE-1','PAUSED-STOP-TERMINAL-RECONCILIATION-1'}
actual=load(W/'runtime-paused-stop-resume2/result-supplement.json');assert actual['actual_verified_for_this_scene'] and actual['app_exit_codes']==[0,0,0] and actual['controller_exit']==1
version_path=P/'VERSION.json';version_bytes=version_path.read_bytes();v=load(version_path)
inputs=load(Path(v['compiled_input_hashes']));assert len(inputs)==313 and all(sha((R/k).read_bytes())==h for k,h in inputs.items())
assert all(sha((P/m['path']).read_bytes())==m['sha256'] for m in v['assistant_modules'])
assert sha((P/'BetterGI.dll').read_bytes())==v['bgi_dll_sha256'] and sha((P/'BetterGI.exe').read_bytes())==v['bgi_exe_sha256']
assert (P/v['launcher']).is_file()
doc_bytes=doc.read_bytes();assert sha(doc_bytes)=='35397e7bd7c7fca0e9e523a19ba27fbfc93642bdb46bdb340b5c555878257640'
session=s.Session(R,'owner-final-one-supported-version-registration-01a1198f');session.policy=dict(session.policy,operation_bytes=8*1024*1024)
original_size=s.size;s.size=lambda roots:original_size(list(dict.fromkeys(str(p) for p in roots)))
with session:
    old=session.old_roots[:];session.old_roots=list(dict.fromkeys(old));assert set(old)==set(session.old_roots)
    session.track(B);session.track(version_path);session.track(doc)
    s.write(B/'previous-version-before-final.json',version_bytes);s.write(B/'previous-instructions-before-final.md',doc_bytes)
    old_review=copy.deepcopy(v.get('independent_review'));v.setdefault('independent_review_history',[]).append(old_review)
    old_repair=copy.deepcopy(v['current_repair']);v.setdefault('current_repair_history',[]).append(old_repair)
    new_review=dict(status='completed',request_id=report['request_id'],snapshot_sha256=report['snapshot_hash'],
        child_id=capture['child_id'],model=capture['model'],effort=capture['effort'],
        completed_timestamp=capture['completed_timestamp'],verdict=report['verdict'],source_candidate_verdict=report['source_candidate_verdict'],
        complete_product_acceptance='pending',applies_to_current_candidate=True,applies_to_current_product_source_identity=True,
        report=str(B/'review/report.json'),report_sha256=sha((B/'review/report.json').read_bytes()),capture=str(B/'review/capture-observation.json'),
        original_final_sha256=capture['original_final_sha256'],used=1,remaining=0,automatic_renewal=False,
        marker='OWNER-RESUME-FINAL-ONE-20261008-FROM-01a11963',legacy_receipt_or_permit_claimed=False,production_authorization=False)
    v['independent_review']=new_review;v['original_review_applies_to_current_candidate']=False
    v['last_independently_reviewed_snapshot_sha256']=report['snapshot_hash'];v['last_independently_reviewed_source']=load(B/'snapshot-observation.json')['snapshot']
    v['current_repair'].update(status='closed',actual_runtime_pending=False,actual_verified=True,
        actual_verified_scope='same-BGI cold incompatible Resume refused before mutation; original paused bytes unchanged; explicit Stop settles same original operation',
        independent_verification=str(B/'review/report.json'),independent_verification_pending=False,source_checkpoint=v['source_checkpoint'],
        current_source_sha256={k:inputs[k] for k in old_repair['source_sha256']},historical_source_binding_retained='current_repair_history',product_complete=False)
    v['paused_stop_repair'].update(status='closed',actual_verified=True,independent_verification_pending=False,
        actual_evidence=str(W/'runtime-paused-stop-resume2/result-supplement.json'),closure_report=str(B/'review/report.json'),
        closure_scope='cold Paused original admission settlement; normal actual plus seal-failure/retry PFP; original corrupt/unsupported and game boundaries retained')
    v.setdefault('current_actual_evidence_history',[]).append(dict(evidence=v.get('current_actual_evidence'),scope=v.get('current_actual_evidence_scope')))
    v['current_actual_evidence']=str(W/'runtime-paused-stop-resume2/result-supplement.json')
    v['current_actual_evidence_scope']='Same current five assistant modules and BGI: incompatible cold Resume refuses with byte-identical Paused; explicit Stop yields Cancelled/durable intent/valid seal/original operation TerminalCompleted; app exits0/0/0, Job0, User difference empty, protocol restored. Controller id/Id failure retained as exit1; ordinary supplementary readback, not certified receipt. No game/resource execution.'
    v['actual_scope']=str(W/'runtime-paused-stop-resume2/ACTUAL.md')
    v['current_source_blockers_resolved']=list(dict.fromkeys(v['current_source_blockers_resolved']+sorted(closed)))
    v['current_source_blockers_resolved_scope']='Named implementation findings independently closed only in report scope; historical grades/open and full acceptance unchanged'
    v['complete_product_goal_status']='active; implementation review and local supported-scene acceptance complete; full game/resource/Skip/account/team/game-exit/OS-shutdown effects await voluntary user feedback; not achieved'
    v['kind']='same local BGI+assistant version with full agreed feature implementation review pass and supported non-game actual evidence; full acceptance pending'
    v['product_complete']=False;v['game_and_resource_effects_pending']=True
    v['current_supported_version_ready']=True;v['user_action_now']='No chat/material/model coordination or repeat authorization needed; game effect feedback remains voluntary under existing owner policy'
    v['evidence_checkpoint']=subprocess.check_output(['git','rev-parse','HEAD'],cwd=R,text=True).strip()
    v['final_one_review_registration']=str(B/'VERSION-READBACK.json')
    encoded=s.encode(v);tmp=version_path.with_name('.VERSION.'+session.id+'.tmp')
    assert version_path.read_bytes()==version_bytes and not tmp.exists();s.write(tmp,encoded);os.replace(tmp,version_path);assert load(version_path)==v
    text=doc_bytes.decode('utf-8')
    old_phrase='原两项LocalWait失败和历史blocked保持；本次已授权一次Sol/high整版综合复核尚待完成，完整版本未交付，游戏反馈由用户主动提供。'
    new_phrase='原两项LocalWait失败和历史blocked保持。本次唯一Sol/high整版实现复核已通过，模式降级和暂停Stop两个重要项在报告范围内closed，无新增本版阻断；完整产品验收仍为pending，游戏效果由用户主动反馈，不能将总Goal标complete。当前本地版本可按下述入口使用；[独立报告](../../_workflow/final-ui-01a11220/product-package-01a118e2/final-one-review/review/report.json)和[版本登记](../../_workflow/final-ui-01a11220/product-package-01a118e2/final-one-review/CURRENT-VERSION.md)保留具体边界。'
    assert text.count(old_phrase)==1;text=text.replace(old_phrase,new_phrase)
    old_phrase='原版本综合源码复核 pass 保留原绑定；后来插入漏步已独立闭合，当前模式降级修复尚未独立闭合，不能据本文宣布整版验收完成。'
    new_phrase='原版本复核保留原绑定；本次整版实现复核pass独立关闭模式降级及暂停Stop的对应重要义务。完整游戏效果和产品验收仍未完成，不能据源码pass宣布总Goal完成。'
    assert text.count(old_phrase)==1;text=text.replace(old_phrase,new_phrase)
    old_phrase='独立整版处置仍待本次唯一授权请求。';assert text.count(old_phrase)==1;text=text.replace(old_phrase,'独立整版实现复核已完成，两项重要义务仅在报告范围内闭合。')
    data=text.encode('utf-8');assert len(data)>=len(doc_bytes) and len(text.splitlines())==len(doc_bytes.splitlines())
    tmp=doc.with_name('.'+doc.name+'.'+session.id+'.tmp');assert doc.read_bytes()==doc_bytes and not tmp.exists();s.write(tmp,data);os.replace(tmp,doc);assert doc.read_bytes()==data
    record=dict(kind='ordinary current-version registration, not full acceptance or legacy receipt',product=str(P),launcher=str(P/v['launcher']),instructions=str(doc),
        source_checkpoint=v['source_checkpoint'],compiled_inputs=313,assistant_modules=v['assistant_modules'],bgi_dll=v['bgi_dll_sha256'],bgi_exe=v['bgi_exe_sha256'],
        version_sha256=sha(version_path.read_bytes()),instructions_sha256=sha(data),source_candidate_verdict='pass',complete_product_acceptance='pending',goal_status='active',
        closed_findings=sorted(closed),review=new_review,actual=str(W/'runtime-paused-stop-resume2/result-supplement.json'),
        original_failures_grades_reports_and_allowances_retained=True,current_supported_version_ready=True,product_complete=False,
        remaining='Voluntary game/resource/Skip/account/team/game-exit/OS shutdown effects and full actual acceptance; no additional model requests authorized',
        post_review_changes='Version registration and instructions status only; compiled product inputs and modules unchanged')
    s.write(B/'VERSION-READBACK.json',s.encode(record))
    page='''# 当前本地版本

整版Sol/high实现复核已通过：请求86a523aeb49c4b1f9aa94814ef524e63，原生审查者01a119b7-98f2-7a31-8ac9-e0c297cc34bb；模式降级保护和暂停Stop终局两个重要项在报告范围内closed。原报告、失败、等级和103/39逐项处置保留，本次固定1次已用完，余0，不自动续额。

启动入口：[启动本机槲寄生.cmd](../../../runtime-unified-01a10e1b/product/启动本机槲寄生.cmd)。现有完整BGI＋助手产物复用，助手DLL19291863，BGI DLL0720fe79/EXE42aa9e13，313编译输入绑定不变。启动/迁移/恢复步骤见仓库Docs/technical/mistletoe-startup-migration-recovery.md，独立原始报告见review/report.json，当前版本身份见VERSION-READBACK.json。

当前同产物实际完成不兼容冷恢复拒绝、原Paused字节保持、耐久Stop/Cancelled、有效封印/原操作TerminalCompleted、零节点提交、两轮助手及BGI正常退出0/0/0、Job0、User差集空和协议恢复。载体id/Id字段错误的controller exit1和两个原中断仍保留，没有伪造正常终态或认证收据。

完整产品验收仍为pending，总Goal保持active。八原生及JS/宏的游戏效果、资源运行中的Skip、账号/兑换/队友、game退出及OS关机等按原约定等待用户主动反馈，不催用户；不能据源码pass或本机有限场景称全部效果已验收。特殊小范围界面BUG及无本版现实关联历史按原级open后修，不改成closed。无需搬运聊天、模型或材料，不增加审查/工具工程。
'''
    s.write(B/'CURRENT-VERSION.md',page.encode('utf-8'))
    print(json.dumps(dict(source_candidate_verdict='pass',closed=sorted(closed),version_sha256=record['version_sha256'],goal='active',full_acceptance='pending',product_complete=False,review_remaining=0),ensure_ascii=False),flush=True)
