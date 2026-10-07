"""Freeze the completed C06/parameter runtime boundary, preserving prior failed evidence."""
from pathlib import Path
import json,hashlib,os,subprocess,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product';RUNTIME=BASE/'own-runtime/eighth-popout';DATA=BASE/'own-runtime/assistant-data'
ROLLOUT=Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T09-40-46-01a11405-2f49-7ae2-b0a7-73e423bd691c.jsonl')
OUT=BASE/'own-preview-params-checkpoint'
HANDOFF='''# C06正常预览/双窗起点与参数真实保存检查点

OWN-ROOT-PREVIEW-PARAMS-20261007-FROM-01a11405，来源01a11405-2f49-7ae2-b0a7-73e423bd691c，前继OWN-ROOT-IDENTITY-UI-20261007-FROM-01a113cc。完整产品未完成。main-OldTeaBag-B168，同项目local原工作区。最后HEAD以本轮精确Git提交读回为准。源当前实际模型/档位以model.json和最新turn_context为证，本轮用户设置已从xhigh改为ultra，接班按来源当前实际值继承；原完整Goal不缩范围。

采用release-first-v2、complete-usable-v1、storage-v1及自动本地提交。原opening/历史/重要2项/会诊98+extra2/2余额0与LocalWait等旧失败保持，本轮会诊0，无独立综合pass。用户撤销了侧聊全部A/B协调要求，停止相关确认、核对和安排；后续只继续本产品目标，不复活它们、不据资料存在启动额外写者。

C01同名身份实机：六轮同产物两窗wf-own-control-01a112a0与wf-1e7a758a可见稳定ID/完整修订提示，选择/草稿同步。两条真实run-24a842e93a7f、run-148851cd7070分别正确流程Succeeded、branchYes+结束、0提交/收尾；助手正常退出/重启及原文件SHA保持。检查点093dc8b83，own-runtime/sixth-identity，原恢复中断材料不改。

C06第一段af0eb384a：顶条普通预览、两窗共享起点入口、active/同预览修订/有效起点/在途的启动可用状态，脏/无效/冲突草稿及Host修订/候选守卫保持。flow-preview原red3Fail，green86Pass，negative3Fail，恢复86Pass，八Job0，源SHA恢复。七轮实际普通预览可选第二起点，但新建弹窗把Preview改为Editing；未实际启动，三软件正常exit0/Job0/User全同，原缺口/UI源完整保存。七轮admission原模型字段xhigh不是当前实际值证明，原件保持；八轮actual-model按原生turn_context读取实际ultra。

C06后修c446ac706：SelectFlow选择与当前Preview相同身份时保持模式；显式Edit和其他身份切换维持行为。真实弹窗用例原红1Fail(null Preview)。第一次popout因预算扫描obj瞬时DLL不存在中断，Job cleanup0/预约4ce7507ea37b440781cec83e63d5aa49 failed/-128194292原样保留，不合成成功。popout-r2 red1Fail/green86Pass+旧渲染用例ToolTip元数据1Fail，四Job0/预约50a634861370482cb519cb1d7aa39e97 failed，未改断言/排除测试。用户暂停时popout-r3尚未发布预约、无子Job或突变；旧自有锁27736/nonce9608原件及恢复证明在flow-preview/paused-recovery，账本未改。复工r3同源green87Pass/0Fail/0Skip，negative1Fail/0Pass/0Skip，原子恢复，restored87Pass/0Fail/0Skip/源漂移空，六Job0/父exit0/预约7d5d98a55b1545e2aa38c115a4fc704e retained；原一次渲染异常保留未复现，不宣称历史无失败。

同完整product仍_workflow/runtime-unified-01a10e1b/product；refresh-preview助手4b463a后已被refresh-popout五模块精确刷新为DLL4308bb5e134804ad6cc9460cbee7b9eea617bee734477c3006dc1be91738e063。旧模块保全，BGI DLL0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7、EXE42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795不变，9500User刷新时全同。新材料经Session/process_runner，helpers仅去重内存中的相同路径记录、集合/基线/额度/账本保持；不是OS配额，也没有工具bundle热改。

八轮真实BGI27144/助手27244、正常重启33004，Session1/E:product、自有NEXUSBGI_DATA_ROOT=own-runtime/assistant-data。主窗正常预览wf-1e7a758a/ceace011，选择2.control.end n-c747b01d后新建真弹窗，两窗ID/预览/所选起点保持；独立窗启动run-c0537b3fb48f成功，只出现第二结束节点、sequenceIndex1，0BGI提交/0收尾，SHA58498d62c0867d4cc6865abedc50f1f01b59ce68a6d46dd6fef704eab850ca1a。资源流程没有误启动。

参数实机：选择wf-242498c6并选中21:35 n-090b94d1，经正式“编辑真实资源”打开自有BGI垫底占位。通过真实控件把MinResinToKeep 0→1，Tab失焦保存；读回只有该字段改变，TaskOrder/TaskDefinitions保持。资源SHA从11D4A510…变88A314683AA12669ACD075DF4603B03AFCB6CBFB1BD4552143C4C831494733DF。真实更新引用修订并保存flow为b5a34892d6a6d5f6d21e9642828831dcab8481c55ccdaeaa7e4fe1fc0a86709b，JSON唯一语义差异nodes[1].ref.revision。助手正常重启后界面实际加载b5a34892，run/flow SHA同，磁盘参数仍1。BGI本轮未冷重启，下一次自有BGI启动须实际确认参数1；不能把助手重启当BGI重启证据。

八轮两助手/BGI exit0/0/0、Job active0、协议恢复、父终态、无自有应用/突变/构建。product_User仅预期OneDragon/垫底占位.json改变，未删除；不能称本轮User全同。其余7流程和9旧run逐SHA同，新增上述1纯本机run，合计8flow/10run。参数1和已保存引用保留在自有验收产物；不要直接恢复旧备份/改真实User去掩盖变化。实际原始UI来源own-runtime/eighth-popout/private/native-ui-source.jsonl及SHA0b0a66863b11d97d78f1d51d9352b713ed2ae516a21701da9f10eeec21c87e39，源码/组件/实机/整版分层。

唯一下一项：同product/data根重新启动时确认BGI参数1和真实资源引用，然后集中正常旧数据准备→标准安装→激活→可恢复回退/数据保留及剩余开发侧矩阵，必要综合复核与完整版本/启动步骤。定向正常迁移入口LegacyMigrationCandidateService→StandardMigrationConsumer/WorkflowMigrationConsumer→Host和BGI config.standardMigration，不把迁移演练/桩或目录候选当正式验收；原源目录只读、固定来源/输出/绑定/重复任务/顺序/过滤/once，原件不移动。相关现有材料外两Migration源码按当前SHA，不以HEAD替代工作区。用户游戏/UID/账号/兑换码/树脂实际动作/关机/队友等待其主动反馈，不代做、不据此停其它开发。

总计划页首、complete-usable-v1、ui-design-intake及DELIVERY-COVERAGE/FIRST-POLICY、原plan.json保持权威。UI唯一E:/Program Files/mistletoe-ui-design只读/v4跨列聚类，非旧v6；车道是路径，到达节点就执行，重复由路径/循环决定，非OR/AND配额。单流程/整表互导、C01/C02/C04-C11/C17/C20、公版、八原生、组/JS/宏全部保留。致命BUG=功能不能用，小范围BUG原级后修；完整含全功能正式UI，局部验收不是整版。

禁止移动真实User/data_guard prepare/restore或原件改名，第三方JS一字不改。所有助手启动显式同own-runtime/assistant-data根，不默认根/不迁旧exe配置；真实D:用户BGI/助手Session2不能操作或杀。启动前现场重核Session/入口/连接，未知局部阻断。原MSIX116/常规27/旧档/失败保留。storage-v1 operation1610612736/retained18790481920/min_free8589934592及原17.5GiB剩余验收复核授权保持；Session/Job纳管，不换目录/渠道/工作树绕限额，不删历史/唯一成果。bundle drift/r61缺报告/旧全量/ProductionCtor/2LocalWait/bf733 blocked98与extra2/2余额0保持。没有独立综合pass，稳定整版材料齐备再提出必要固定次数/范围复核，不自行超额或重置，也不以此停独立产品工作。

Computer Use完整skill/guidance/confirmations/api及node_repl @oai/sky；唯一实际返回窗口、每动作新观察，不混PS UIA。失去绑定/几何时同工具刷新/返回窗口Raise，关联模态索引不可用可用新截图小模态坐标；实际聚合卡会每刷新重建索引，不能盲重试旧索引。坐标须核最新截图尺度，滚动条拖动与节点拖动区分；physical Escape停当轮，不reset绕过。普通click不改时间。编码/大小/hash/diff-stat保护，Rebuild DeployToBgiTools=false，精确git commit --only，无push/合并/发布。材料外csproj、两Migration源、旧账本/文档/_workflow保护。

交接理由：已完成可验证C06和参数保存批次，下一独立目标正常迁移与全功能收口宜接班；不是时间/工具数强制切换。按handoff-auto-create/handoff-inherit-model，安全终态/精确提交后先原生暂停本线程并读回paused，再创建恰好一个同项目local接班，继承来源最新实际model/effort并核新rollout和完整Goal active；原完整目标仍未完成，旧线程停写，不让用户搬运，不双写。
'''
with s.Session(ROOT,'own-root-01a11405-preview-parameter-checkpoint') as budget:
    budget.old_roots=list(dict.fromkeys(budget.old_roots));budget.track(OUT)
    def write(name,value):s.write(OUT/name,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    raw=json.loads((RUNTIME/'result.json').read_text(encoding='utf-8'));terminal=json.loads((RUNTIME/'apps-tree-terminal.json').read_text(encoding='utf-8'))
    assert raw['exit_code']==0 and terminal['active_processes']==0 and raw['protocol_restored']
    assert raw['product_user_changed']==['OneDragon/垫底占位.json'] and not raw['product_user_removed']
    refresh=json.loads((BASE/'own-runtime/refresh-popout/result.json').read_text(encoding='utf-8'))
    assert all(sha((PRODUCT/x['path']).read_bytes())==x['sha256'] for x in refresh['updated'])
    hashes=json.loads((BASE/'flow-preview/popout-r3/restored/source-hashes.json').read_text(encoding='utf-8'))
    drift=[n for n,h in hashes.items() if not (ROOT/n).is_file() or sha((ROOT/n).read_bytes())!=h];assert not drift,drift
    for st in ['green','negative','restored']:
        result=json.loads((BASE/f'flow-preview/popout-r3/{st}/result.json').read_text(encoding='utf-8'))
        for k in ['build','test']:assert json.loads((BASE/f'flow-preview/popout-r3/{st}/{k}-tree-terminal.json').read_text(encoding='utf-8'))['active_processes']==0
    name='垫底占位.json';before=(RUNTIME/'private/parameter-before'/name).read_bytes();after=(PRODUCT/'User/OneDragon'/name).read_bytes()
    a=json.loads(before);b=json.loads(after);assert a['MinResinToKeep']==0 and b['MinResinToKeep']==1
    assert [n for n in set(a)|set(b) if a.get(n)!=b.get(n)]==['MinResinToKeep']
    s.write(OUT/'private/parameter-after'/name,after)
    before_flows={f.name:sha(f.read_bytes()) for f in (RUNTIME/'private/before/flows').glob('*.json')}
    after_flows={f.name:sha(f.read_bytes()) for f in (DATA/'flows').glob('*.json')}
    changed=[n for n in before_flows if before_flows[n]!=after_flows.get(n)];assert changed==['wf-242498c6.flow.json']
    flow=json.loads((DATA/'flows/wf-242498c6.flow.json').read_text(encoding='utf-8'))
    old=json.loads((RUNTIME/'private/before/flows/wf-242498c6.flow.json').read_text(encoding='utf-8'))
    assert flow['nodes'][1]['ref']['revision']==sha(after).upper()
    check=json.loads(json.dumps(flow));check['nodes'][1]['ref']['revision']=old['nodes'][1]['ref']['revision'];assert check==old
    for folder in ['flows','runs']:
        for file in (DATA/folder).glob('*.json'):s.write(OUT/'private'/folder/file.name,file.read_bytes())
    before_runs={f.name:sha(f.read_bytes()) for f in (RUNTIME/'private/before/runs').glob('*.json')}
    after_runs={f.name:sha(f.read_bytes()) for f in (DATA/'runs').glob('*.json')}
    assert all(after_runs.get(n)==h for n,h in before_runs.items())
    assert set(after_runs)-set(before_runs)=={'run-c0537b3fb48f.run.json'}
    run=json.loads((DATA/'runs/run-c0537b3fb48f.run.json').read_text(encoding='utf-8'))
    assert run['workflowId']=='wf-1e7a758a' and run['state']==4
    assert [n['nodeId'] for n in run['nodeOutcomes']]==['n-c747b01d'] and not run['submissionHistory'] and not run['completionHistory']
    turn=[json.loads(line) for line in ROLLOUT.read_bytes().splitlines() if json.loads(line).get('type')=='turn_context'][-1]
    ctx=turn['payload'];settings=ctx['collaboration_mode']['settings'];assert ctx['model']==settings['model'] and ctx['effort']==settings['reasoning_effort']
    model=dict(source_thread=os.environ['CODEX_THREAD_ID'],rollout=str(ROLLOUT),timestamp=turn['timestamp'],cwd=ctx['cwd'],model=ctx['model'],effort=ctx['effort'],settings_model=settings['model'],settings_effort=settings['reasoning_effort'])
    write('model.json',model)
    s.write(OUT/'private/git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    write('checkpoint.json',dict(marker='OWN-ROOT-PREVIEW-PARAMS-20261007-FROM-01a11405',head_before_checkpoint=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),source_drift=drift,model=model,product=str(PRODUCT),assistant_data=str(DATA),assistant_sha=refresh['updated'][0]['sha256'],runtime=raw,terminal=terminal,parameter=dict(file='OneDragon/垫底占位.json',before=0,after=1,changed_fields=['MinResinToKeep'],revision=sha(after)),flow=dict(workflowId=flow['workflowId'],revision=after_flows['wf-242498c6.flow.json'],changed_path='nodes[1].ref.revision',nodeId='n-090b94d1'),specified_start=dict(runId=run['runId'],workflowId=run['workflowId'],revision=run['workflowRevision'],nodeId='n-c747b01d',state=4,submissions=0,completion=0,sha256=after_runs['run-c0537b3fb48f.run.json']),other_flows_byte_identical=len(before_flows)-1,old_runs_byte_identical=len(before_runs),flows_total=len(after_flows),runs_total=len(after_runs),new_consultations=0,independent_review=False,product_complete=False))
    s.write(OUT/'HANDOFF.md',HANDOFF.encode('utf-8'))
    print('CHECKPOINT_OK',model['model'],model['effort'],len(after_flows),len(after_runs),flush=True)
