from pathlib import Path
import sys,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r3'
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954/auto-standard-install-01a10c1c/01a10c87-deee-7381-a61d-fefce264d537'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def write(p,v):
    data=json.dumps(v,ensure_ascii=False,indent=2).encode()
    if p.exists():assert p.read_bytes()==data,'existing final record changed: '+str(p)
    else:s.write(p,data)
with s.Session(root,'final-candidate-identity-and-complete-user-validation') as budget:
    budget.track(base)
    restored=load(base/'ui-r3/restored.json');assert restored['all_original_sha_same'] and restored['protocol_exact_before_value_and_kind']
    archive=Path(restored['test_archive'])
    flow=archive/'flows/wf-edc5b117.flow.json';run=archive/'runs/run-cfb748b14d5d.run.json';doc=load(flow);record=load(run)
    assert len(doc['nodes'])==3 and len({n['nodeId'] for n in doc['nodes']})==3
    assert [n['kind'] for n in doc['nodes']]==['resource.singleTask','resource.singleTask','resource.oneDragonConfig']
    assert doc['nodes'][0]['ref']['taskId']==doc['nodes'][1]['ref']['taskId']=='e00bef32-0c75-41b7-9853-753da8353c5c'
    assert record['state']==6 and record['stopRequested'] and record['currentSubmission'] is None and record['pendingCompletion'] is None
    a=load(base/'ui-r3/readback-before-final-restart.json');b=load(base/'ui-r3/readback-after-final-restart.json')
    preserved=['flows/wf-edc5b117.flow.json','flows/wf-ui-import-01a10cef.flow.json','runs/run-cfb748b14d5d.run.json']
    assert all(a[k]==b[k] for k in preserved)
    for p in [flow,run,archive/'flows/wf-ui-import-01a10cef.flow.json']:
        dst=base/'ui-r3/public-proof'/p.name
        if dst.exists():assert dst.read_bytes()==p.read_bytes()
        else:s.write(dst,p.read_bytes())
    write(base/'ui-r3/verification-facts.json',dict(candidate=str(candidate),windows_session=1,actual_ui=True,marker='actual tool UI observations plus independent file readback',native_single_chooser_eight=True,mixed_node_kinds=[n['kind'] for n in doc['nodes']],unique_node_ids=3,owner_config_and_taskid_bound=True,flow_sha=sha(flow),cancelled_run=record['runId'],future_trigger=record['wait']['nextTriggerAt'],stop_authority=record['stopAuthority'],no_submission_or_completion=True,restart_same_sha={k:a[k]==b[k] for k in preserved},original_data_restored=restored,game_execution=False,os_shutdown=False,complete_game_acceptance=False))
    files=[dict(path=p.relative_to(candidate).as_posix(),bytes=st.st_size,sha256=sha(p)) for p,st in s.files_under(candidate) if p.relative_to(candidate).parts[0] not in ['User','log']]
    assert not any('testhost' in x['path'].lower() or 'ControlledWriterProbe' in x['path'] or 'TestData' in x['path'] for x in files)
    write(base/'final-runtime-manifest.json',files)
    original_files=load(prior/'runtime-facts/runtime-files-with-routes.json')
    new_lookup={v['path'].replace('\\','/'):v for v in files};nonproduct_drift=[];rebuilt_dependencies=[]
    for item in original_files:
        rel=item['path'].replace('\\','/')
        if Path(rel).name.startswith(('BetterGI.','MultiplayerHoeingAssistant.')):continue
        if rel not in new_lookup or item['sha256']!=new_lookup[rel]['sha256']:
            if Path(rel).name.startswith(('Fischless.WindowsInput.','Fischless.HotkeyCapture.','Fischless.GameCapture.')):
                rebuilt_dependencies.append(dict(path=rel,prior_sha=item['sha256'],current_sha=new_lookup[rel]['sha256']))
            else:nonproduct_drift.append(rel)
    assert not nonproduct_drift,nonproduct_drift
    old_inputs={k.replace('\\','/').lower():v for k,v in load(prior/'build-r1/source-before.json').items()}
    dependency_inputs=[]
    for folder in ['Fischless.WindowsInput','Fischless.HotkeyCapture','Fischless.GameCapture']:
        for p in (root/folder).rglob('*'):
            if p.is_file() and p.suffix in ['.cs','.xaml','.csproj','.json','.props','.targets'] and not set(p.relative_to(root/folder).parts)&{'bin','obj','User','.kiro'}:
                rel=p.relative_to(root).as_posix();assert old_inputs[rel.lower()]==sha(p),'dependency source drift: '+rel
                dependency_inputs.append(dict(path=rel,sha256=sha(p)))
    write(base/'rebuilt-dependency-source-binding.json',dict(outputs=rebuilt_dependencies,unchanged_source_inputs=dependency_inputs,reason='normal Rebuild source-project dependencies have new binary/PDB identities; external packages/resources are checked separately; byte equivalence not claimed'))
    source_paths=['BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStopAuthority.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs']
    sources=[dict(path=p,bytes=(root/p).stat().st_size,sha256=sha(root/p)) for p in source_paths]
    write(base/'final-source-manifest.json',sources)
    s.write(base/'final-dotnet-info.txt',subprocess.check_output(['C:/Program Files/dotnet/dotnet.exe','--info']))
    identity=dict(candidate=str(candidate),source_head_at_finalization=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),source_bytes=sources,bgi_sha=sha(candidate/'BetterGI.dll'),assistant_sha=sha(candidate/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),files=len(files),bytes=sum(x['bytes'] for x in files),nonproduct_dependency_resource_drift=nonproduct_drift,rebuilt_project_dependencies=rebuilt_dependencies,independent_repair_review=False,product_accepted=False,source_build_provenance='WIP HEAD plus explicit source hashes; final local commit recorded separately; version labels alone not identity')
    write(base/'FINAL-CANDIDATE.json',identity)
    assistant=load(base/'fence-green/full-summary.json');bgi=load(base/'bgi-sequential/candidate-summary.json')
    known=dict(assistant_failed=assistant['failed'],bgi_sequential_failed=bgi['failed'],original_independent_report='bf73360e2886405bad322e9dbe41983d blocked preserved',review_requests_remaining=0,new_repairs_pending_independent_review=['MIGRATION-RECOVERY-ENTRY-1','NATIVE-SINGLE-UI-ENTRY','FRESH-STOP-FENCE-WIRE'],old_bgi_611_run='ABORTED: original stderr records testhost crash; not full regression',parallel_bgi_cascades=load(base/'single-restored-full/bgi-full-summary.json')['counters'],no_failures_excluded_or_regraded=True)
    write(base/'KNOWN-ISSUES.json',known)
    old_text=(prior/'VALIDATION.md').read_text(encoding='utf-8-sig');matrix=old_text.split('## 逐项验证与通过判据',1)[1].split('## 已有证据与仍欠的判定',1)[0]
    introduction=f'''# 当前完整候选与验证内容

2026-10-06。用户已授权代理完成无需游戏的实际 UI/IPC 验证；游戏内效果、账号切换/树脂/兑换码、真实关机仍由用户后续执行。本文件交付候选和全部原约定功能的验证操作，不宣布产品总目标或独立审查通过。

## 版本身份

候选目录：`{candidate}`。

BGI：`BetterGI.exe`；助手：`Tools\\MultiplayerHoeingAssistant\\MultiplayerHoeingAssistant.exe`。界面仍为 BGI 0.64.2-alpha.2 Dev / 助手 0.2.1，区分版本必须核目录和模块 SHA。

- BGI.dll SHA256：`{identity['bgi_sha']}`。
- MultiplayerHoeingAssistant.dll SHA256：`{identity['assistant_sha']}`。
- 文件/依赖/资源逐 SHA：本目录 `final-runtime-manifest.json`；源码身份 `final-source-manifest.json`；SDK/MSBuild 实际读回 `final-dotnet-info.txt`；构建命令与退出码见 `single-green/`、`single-green-r2/`、`fence-green/`。构建时是 WIP HEAD 加明确源码补丁，最终提交另记，不凭版本号认定等价。
- 本候选包含 7cd6310a 迁移恢复补丁，以及实际验证发现的原生单项作者入口、零停止版本空时间戳兼容修复。迁移执行隔离门、正停止版本缺时间戳/错纪元/错频率的拒绝保持。

## 我已经实际完成的无需游戏验证

1. Session 1 的真实 BGI/助手启动、单机模式、本地 IPC 状态查询与事件订阅；原生八单项在实际资源下拉可选，显示所属配置和修订。
2. 实际 UI 建立两个相同 taskId 的单项出现与一个整龙混排，三个 nodeId 各自独立，绑定真实所属配置与修订；保存、正常退出重启后原 SHA 保持。
3. 未来 08:00 触发运行 `run-cfb748b14d5d` 保存真实停止权威（版本 0、匹配 BGI 纪元），进入等待；显式停止成为 Cancelled，未提交节点、未产生收尾，重启不复活，运行记录原 SHA 保持。这不证明游戏中执行后的停止效果。
4. 原生单项编辑资源从任务中心打开真实 BGI 配置；变更启用态使 SHA 改变，旧引用拒绝，显式更新进入草稿，原取消运行不变。测试资源最后恢复原 SHA，测试草稿放弃，原计划保持。
5. 分享导出、同身份冲突拒绝（前一 r2 同源 UI 证据）、独立身份副本在当前 r3 实际导入；两个计划在重启后保持。
6. 正常迁移 UI 准备/激活/双根回退/重复回退在 r2 已完成；当前 r3 再用实际 SDK 与运行中的 BGI 执行标准安装、激活、回退及重复回退（`live-ipc/r3-migration`），不是假传输或游戏执行。原工作流/资源原字节读回保持。

实际测试采用受控独立 User 及 Codex MSIX AppData 物理命名空间；保存/重启事实仅对应这个已记录环境。原助手 116 文件在其原可见命名空间逐 SHA 恢复，测试数据另存；原协议命令按本轮原值/类型 CAS 恢复。私有原配置及恢复索引不分享、不提交。旧 D 安装、第三方 JS、宏、脚本、截图未作覆盖式迁入或修改。

## 启动与恢复步骤

1. 正常退出其它 BGI/助手，确认没有任务。在正式 Windows 用户环境先备份最新 User、`%APPDATA%\\NexusBGI` 设置、流程和责任记录；本次备份不能覆盖之后的数据。
2. 运行本候选 `BetterGI.exe`。候选独立 User 的 `验收-统一版本-01a10c87` 含八项且全部启用；测试游戏前只启用本次一项，先确认“同时启动原神”关闭，不直接运行整份默认测试配置。
3. 助手目录独立不会隔离正式用户 AppData。启动前确保原启动流程自动执行、定时上线、开机/随 BGI 启动和守护均关闭；保留责任记录。再启动候选助手，界面选择单机执行端并设置 BGI 路径为本候选。已有自动执行配置未关闭时不要直接启动旧配置。
4. 本轮两个任务中心测试计划和取消记录保留在 `ui-r3/restored.json` 指明的测试档案目录，未留到正式旧配置中。需要复用时通过合法流程导入单个自有测试文件，不能整目录覆盖正式记录；本目录 `ui-r3/public-proof` 提供测试副本。
5. 配置组、JS、宏、自定义路线从原合法导入入口准备；第三方脚本一字不改，缺资源按失败记录。普通旧格式用“准备旧数据迁移”→只读预览→原激活入口→原迁移回退；仅在备份副本验证故障，不在正式数据上强造中断。
6. 每次 BGI 启动会注册 BetterGI:// 到本 exe；测试前记当前协议命令，测试结束按所选正式安装恢复。遇错先停止关联任务，保留 run/job/队列/事务/日志原件；未知责任不能直接删除。恢复须核正确原根、修订和归属，经原回退入口，不猜测覆盖安装账。

## 全部原约定功能的操作与判据

下面保留原完整功能清单，不退化为已演示的两个场景。无需游戏部分已有上述真实证据；需要游戏/账号/联机/OS 的行仍未验收。UID 由你测试时自记并对照，我不再索取它来阻断无需游戏的验证。
'''
    ending=f'''\n## 回归结果、已知问题及未验边界

- 原生单项两端红反例各 1 项，修复后各通过；所属配置/taskId 两个反向突变各检出并恢复同源码 SHA。
- 停止空字段实际入口红反例：109 项中 1 个指定失败；修复后 109/109，包括正版本缺时间戳、错纪元和矛盾时间戳拒绝。
- 当前助手全量：{assistant['counters']['total']} 总计 / {assistant['counters']['passed']} 通过 / {assistant['counters']['failed']} 失败 / 2 跳过。原五失败保留；C5 负向分支在空默认目录实际触发，期望 StatusUncertain 而实为 NotReady，旧/新模块同条件都失败（`startup-negative`），未改产品为变绿。
- 当前 BGI 串行完整同条件：旧模块 1129/1113通过/16失败，新模块 1129/{bgi['counters']['passed']}通过/{bgi['counters']['failed']}失败。原生单项能力新断言由红变绿。迁移物理槽在全套中另失败，独立进程旧/新各通过；随机同步点固定反例 `__` 在旧/新都失败（`bgi-focused`）。保留全套差异，不合称无新增失败或全绿。
- 重要纠正：旧 611/607通过/4失败的 stderr 明确记录测试主机崩溃/运行中止，不能称完整 BGI 回归。当前原并行全套1129/906通过/223失败含 App 初始化连锁错误；其原件保留。串行仅控制共享静态状态的交错，不排除任何测试，不取代产品并发验证。
- 完整失败姓名/testId/栈与原级责任见 `KNOWN-ISSUES.json` 及原 TRX。已有 A1/A2固定未修模拟、stuck 20/30秒、OCR顺序、助手旧ranking/坏owner/旧gate差异按原独立报告保留；未覆盖的历史/普通问题不扩入本轮清洁工程。
- 已观察到 BGI 已在同一原生页面时资源打开可能被拒绝；切到另一页再从任务中心打开可成功。此导航问题保留，未伪记修复。
- 独立综合报告 bf73360e2886405bad322e9dbe41983d 原 blocked、98原级义务及原账/预算完整保持。额外实现复核额度为0，本轮没有新请求，也没有新的原生认证 receipt；恢复、原生单项和停止空值修复均为候选，未独立 closed。正式独立修复复核须固定范围/次数授权，不能以本轮测试代替。
- 游戏八任务实际效果、正确账号/UID切换、树脂/兑换码、真正跨天运行/截止、执行中暂停/恢复/插入/跳过、关闭游戏/软件收尾及真实关机、联机/远程多端端到端仍未完成全功能验收。它们保持上表全部操作/判据，不记通过。
- 存储采用用户已授权单次1.5 GiB、累计16 GiB、余量8 GiB；固定两端测试目录重复利用，未删历史必要材料、未重开预算或扩工具。

结果反馈格式：项目、版本目录/模块身份、预期、实际、是否正确账号（自记）、runId/jobId、原日志/截图、通过或未验证。候选与验证内容交付不表示原产品总目标验收完成。\n'''
    text=introduction+matrix+ending
    assert all(k in text for k in ['C01','C02','C04','C06','C07','C08','C09','C10','C11','C17','C20','八类单项','nextDay'])
    s.write(base/'VALIDATION.md',text.encode('utf-8'))
    assert (base/'VALIDATION.md').read_text(encoding='utf-8')==text
    write(base/'completion-material-audit.json',dict(full_original_validation_matrix_preserved=True,actual_ui_and_real_ipc_evidence=True,source_product_manifest=True,sdk_and_dependencies_bound=True,original_user_data_restored=True,all_failures_and_independent_obligations_kept=True,final_local_commit_pending=True,product_total_goal_accepted=False))
    print(json.dumps(dict(files=len(files),runtime_bytes=identity['bytes'],bgi=identity['bgi_sha'],assistant=identity['assistant_sha'],validation=str(base/'VALIDATION.md')),ensure_ascii=False),flush=True)
    budget.check(measure=True)
