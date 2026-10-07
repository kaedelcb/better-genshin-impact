"""Preserve completed migration/runtime facts and refresh only owned assistant modules."""
from pathlib import Path
import hashlib, json, os, subprocess, sys
ROOT=Path(__file__).resolve().parents[2]; BASE=Path(__file__).resolve().parent
RUNTIME=BASE/'own-runtime'; PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA=RUNTIME/'assistant-data'; OUT=BASE/'own-migration-viewport-checkpoint'
REFRESH=RUNTIME/'refresh-viewport'; CARRIER=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
with s.Session(ROOT,'own-root-01a114a3-migration-viewport-checkpoint-refresh') as budget:
    previous_roots=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(previous_roots));assert set(budget.old_roots)==set(previous_roots)
    budget.track(BASE);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User')
    for directory in [OUT,REFRESH]:
        if directory.exists(): assert not any(directory.iterdir()), 'existing checkpoint output is not empty'
        else: directory.mkdir()
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    stages={}
    for name in ['red','green','negative','restored']:
        folder=BASE/'viewport-scroll/r2'/name
        result=json.loads((folder/'result.json').read_text(encoding='utf-8'))
        terminals=[json.loads((folder/(phase+'-tree-terminal.json')).read_text(encoding='utf-8')) for phase in ['build','test']]
        assert all(t['active_processes']==0 for t in terminals)
        stages[name]=dict(build=result['build'],passed=len(result['Passed']),failed=len(result['Failed']),skipped=len(result['NotExecuted']),jobs=terminals,source_drift=result['source_drift'])
    assert stages['green']['failed']==stages['restored']['failed']==0
    source_path=BASE/'viewport-scroll/r2/restored/source-hashes.json'
    sources=json.loads(source_path.read_text(encoding='utf-8'))
    drift=[name for name,digest in sources.items() if sha((ROOT/name).read_bytes())!=digest];assert not drift
    runtimes={}
    for name in ['ninth-cold-migration','tenth-migration-activation','eleventh-migration-rollback']:
        folder=RUNTIME/name;result=json.loads((folder/'result.json').read_text(encoding='utf-8'));terminal=json.loads((folder/'apps-tree-terminal.json').read_text(encoding='utf-8'))
        assert result['exit_code']==0 and terminal['active_processes']==0
        assert sha((folder/'private/native-ui-source.jsonl').read_bytes())==result['raw_ui_source_sha256']
        runtimes[name]=dict(result=result,terminal=terminal)
    first=json.loads((RUNTIME/'ninth-cold-migration/private/product-user-before.json').read_text(encoding='utf-8'))
    last=json.loads((RUNTIME/'eleventh-migration-rollback/private/product-user-after.json').read_text(encoding='utf-8'))
    original_changed=[name for name,digest in first.items() if last.get(name)!=digest];assert not original_changed
    old_runs={file.name:sha(file.read_bytes()) for file in (RUNTIME/'ninth-cold-migration/private/before/runs').glob('*.run.json')}
    assert all(sha((DATA/'runs'/name).read_bytes())==digest for name,digest in old_runs.items())
    run=json.loads((DATA/'runs/run-6a7ca3c9313e.run.json').read_text(encoding='utf-8'))
    assert run['state']==6 and run['stopRequested'] and not run['submissionHistory'] and not run['completionHistory']
    input_before=json.loads((RUNTIME/'tenth-migration-activation/private/legacy-source-before.json').read_text(encoding='utf-8'))
    input_after=json.loads((RUNTIME/'eleventh-migration-rollback/private/legacy-source-after.json').read_text(encoding='utf-8'));assert input_before==input_after
    workflow='wf-migr-81ecebc20d078250'
    activated=(RUNTIME/'tenth-migration-activation/private/after/flows'/(workflow+'.flow.json')).read_bytes()
    reverted=(DATA/'flows'/(workflow+'.flow.json')).read_bytes()
    candidate=(RUNTIME/'tenth-migration-activation/private/migration-after/legacy-migration-candidates/f18f48d2f243ac64/candidate-20261007-124737/flows/工作日计划.flow.json')
    doc=json.loads(reverted);assert doc['activation']['status']=='candidate-ready' and doc['watermark']['once']
    journal=json.loads((PRODUCT/'User/migration-installations/std-76fe1cc10c52725404a6219702b4fc51.json').read_text(encoding='utf-8'))
    assert journal['State']=='rolledBack' and all(not (PRODUCT/'User/OneDragon'/(f['ConfigName']+'.json')).exists() for f in journal['Files'])
    old_identity=json.loads((RUNTIME/'refresh-popout/result.json').read_text(encoding='utf-8'))
    assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in old_identity['updated'])
    before_user={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')}
    updated=[]
    for module in old_identity['updated']:
        target=PRODUCT/module['path'];content=(CARRIER/target.name).read_bytes()
        s.write(REFRESH/'before'/target.name,target.read_bytes())
        temporary=target.with_name(target.name+'.own-viewport.tmp');assert not temporary.exists()
        s.write(temporary,content);os.replace(temporary,target);assert target.read_bytes()==content
        updated.append(dict(path=module['path'],sha256=sha(content)))
    after_user={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')};assert before_user==after_user
    assert sha((PRODUCT/'BetterGI.dll').read_bytes())==old_identity['bgi_sha256'] and sha((PRODUCT/'BetterGI.exe').read_bytes())==old_identity['bgi_exe_sha256']
    write(REFRESH/'result.json',dict(updated=updated,bgi_sha256=old_identity['bgi_sha256'],bgi_exe_sha256=old_identity['bgi_exe_sha256'],source_hashes=str(source_path),product_user_changed=[],user_files=len(after_user),review_requests_new=0,independent_review=False,actual_ui=False,product_complete=False))
    summary=dict(marker='OWN-ROOT-PREVIEW-PARAMS-20261007-FROM-01a11405',runtimes=runtimes,stages=stages,original_product_user_files_unchanged=len(first),original_changed=original_changed,old_runs_byte_identical=len(old_runs),legacy_input_files_unchanged=len(input_before),migration=dict(workflow_id=workflow,active_sha256=sha(activated),reverted_sha256=sha(reverted),installation_state=journal['State'],source_index_reused=True,once=True,filtered_nodes=sum(bool(n.get('legacyFiltered')) for n in doc['nodes'])),wait_run=dict(run_id=run['runId'],state=run['state'],stop_requested=True,submissions=0,completion=0),source_drift=drift,refresh=str(REFRESH/'result.json'),independent_review=False,product_complete=False)
    write(OUT/'checkpoint.json',summary)
    s.write(OUT/'git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    text='''# 正常迁移与策略视口检查点（候选，整版未完成）

本线程完整Goal active；同项目main-OldTeaBag-B168，实际gpt-6.1-sol/ultra。采用release-first-v2、complete-usable-v1、storage-v1。原opening、原级open、98+extra2/2余额0及旧失败保持；新增会诊0。子代理仅机械索引既有资料，无写入或独立审查结论。

九轮同产物冷启动：BGI原生“垫底占位”参数1、助手加载b5a34892修订；资源/流程SHA同交接，两程序exit0、Job0，User逐SHA同。

十轮使用仓库6份正常旧格式夹具及global-schedule的精确副本，经正式UI生成4候选。工作日计划wf-migr-81ecebc20d078250真实安装两标准配置并激活：09500fb7→73dc26b2，activate-4561eadee44d43f895128eed4645c471。原生BGI显示两个领取邮件，原顺序/独立ID、过滤节点/once保留；查看未改字节，未启动游戏/账号/兑换码/收尾动作。此为自有正常样本的真实软件/IPC验收，不是实际用户目录迁移。

十一轮冷启动仍active/installed、两标准文件字节同；正式回退恢复精确09500fb7候选，标准安装rolledBack。再准备显示已复用，4流程及源索引SHA不变。7源输入、原9500个product/User文件、原10run均保持；仅本次新增安装账保留，原件未移动。完整原始UI/安装绑定/事务/前后SHA均在own-runtime对应阶段，见checkpoint.json。

剩余跳过/重载验证使用纯本机wf-own-control-01a112a0，UI把判断节点排至22:00并保存9dc676f1。run-6a7ca3c9313e进入等待，因完整策略表单被挤出视口未继续跳过/重载，已实际停止Cancelled/state6/stopRequested=true/0提交/0收尾。该自有流程目前仍为22:00验证修订，原44ac05e9字节在十一轮before；后续通过UI取消定时并保存，不直接覆盖备份。

已定位正式策略表单可达性缺口：UpdateViewport使用外层滚动后的屏幕top，重绑/调整窗宽会把时间轴扩大同样的滚动量。原真实WPF反例offset150为374.82→524.82，offset0通过。四行修复补回父ScrollViewer的像素偏移；目标红1/通过1、相关绿89、逆向旧逻辑红1/通过1、恢复89、四Rebuild0/八Job0/源漂移0。新夹具验证下方表单可进入视口；组件不是实际软件验收。首次因obj临时_wpftmp文件消失中断的失败预约和cleanup0保留；r2仅允许生成目录最多5次完整重扫，集合/额度/账本不改，实际重试列表为空，工具bundle未改。

同产物五助手模块已精确刷新，旧模块保存在own-runtime/refresh-viewport/before；BGI DLL/EXE及9501个当前User文件逐SHA同。唯一下一项：用刷新后的同product和显式assistant-data根，复验完整策略表单滚动/参数保存，完成纯本机重载/跳过/停止及其余开发侧矩阵，再稳定整版必要复核及版本/启动恢复步骤。用户游戏测试仍待主动反馈；不移动真实User、不改第三方JS、不代做游戏/关机，不复活撤销A/B协调。当前无综合pass，整版Goal未完成。
'''
    s.write(OUT/'CHECKPOINT.md',text.encode())
    print(json.dumps(dict(original_user_unchanged=len(first),old_runs_unchanged=len(old_runs),migration_reverted=True,causal_restored=stages['restored'],assistant_sha=updated[0]['sha256']),ensure_ascii=False),flush=True)
