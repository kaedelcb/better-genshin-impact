from pathlib import Path
import json,hashlib,os,subprocess
r=Path.cwd();old=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1';d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f';d.mkdir(exist_ok=False)
thread=os.environ['CODEX_THREAD_ID'];assert thread=='01a1061f-79a1-7b62-a8d2-bc46334403e3';marker='AUTO-FOUR-ENTRY-RECOVERY-RELAY-20261004-FROM-01a1061f'
rollout=next((Path('C:/Users/Administrator/.codex/sessions/2026/10/04')).glob('*'+thread+'.jsonl'));contexts=[(i,json.loads(line)['payload']) for i,line in enumerate(rollout.read_text(encoding='utf-8').splitlines(),1) if json.loads(line).get('type')=='turn_context'];ordinal,ctx=contexts[-1];assert ctx['model']=='gpt-6.1-sol' and ctx['effort']=='medium';settings=ctx['collaboration_mode']['settings'];assert settings['model']==ctx['model'] and settings['reasoning_effort']==ctx['effort']
(d/'source-model-observation.json').write_text(json.dumps(dict(threadId=thread,rollout=str(rollout),ordinal=ordinal,model=ctx['model'],effort=ctx['effort'],settings=settings),ensure_ascii=False,indent=2))
commit=json.loads((old/'candidate-commit-observation.json').read_text())['commit']
handoff=f'''# 四真实入口与新Host恢复接力

来源{thread}，标记{marker}，原共享包不重开opening/预算/请求/原级义务。总Goal未完成，生产门关闭。本候选{commit}只两个源码/测试（内部标量观察器与三项夹具）及本批原过程；明确范围本地提交，未认证/未后审/未验收。

完整细节读前目录CAPACITY-CANDIDATE.md、final/candidate-observation.json、impact-comparison.json、panel-capacity-original.json、handoff-capacity-original.json、post-mutation-byte-observation.json、candidate-commit-observation.json及两项mutation观察与全部原TRX/log。

面板/移交各33次实际准入均accepted、唯一原request/identity与类型化原ParentSource不变；实际占位发布后读回主槽位最大2/1，前序节点逐次TerminalCompleted/Tombstone/封印。实际拥有者临时租约模拟满25h，产品RecoverAfterRestart/MigrateAndClean真实归档面板34/移交33条；全原Operation序列化内容保留，新的LeaseStore/RunStore读回合法，归档旧ContinueUse保守stale_operation_identity、发送仍33。重开只新存储实例与原门面恢复，**不是新Host或真进程重启**，不补称实机。内部观察器只复制标量，异常不改准入结果，生产null。

Rebuild0；TaskCenter1971/18/2=1991、exit1；相邻401/0/2=403、exit0；精确合集1981/18/2=2001，对前序1998新增3/删除0/共有变化0，18失败ID一致、不豁免。声明面同SHA，13输入前后字节一致；M1去终局迁墓碑使移交返回数32而应33（面板31），M2归档改原WireSubmitKey使原记录字符串对照红，两项P/F/P指定断言/SHA恢复。初轮未初始化租约读回、可选ExecutionResult、非拥有者写入和错误SubmissionRecord字段编译失败保留，不当产品语义红。普通TRX/字节/受控端口非认证receipt/独立pass/IPC游戏User验收。

唯一下一项：完整原身份在TerminalPendingTransfer饱和保留及真实新Host初始化/恢复→四真实入口（面板start、移交start、Paused续行、Interrupted恢复）不同原来源因果全表。原RecoveryAdmissionTests的Paused/Interrupted/HandoffResume用FakeBoundary/runnerFactory，不能冒充实际生产successor链；原普通33 Fact存在。已定位真实生产ProbeNodeSubmitRoutingAsync可扩展暂停/退出/重开驱动接缝，或新夹具组装实际Host/Runner/RunStore/LeaseStore/BgiWorkflowExecutionBoundary配受控端口。保留原StopAuthority、许可/nonce/旧sendSeq/游标逻辑消费、历史/Outcome/封印和父引用；Pause/Stop/Shutdown不能凭RPC/不存在/超时清偿，来源欠缺拒绝/无发送。补完原G4/G7多轮迟到冲突/恢复停止及所有原级影响后共享链统一认证/独立综合后审；全约定功能实机仍欠。

最新audit exit2仍native请求9f85a4b85f46400dbff20b97ebec4fda缺receipt，旧manifest缺列表/policy=false/planpass机械约束保留。正式认证/独立后审配置仍original-send-round/current-review-config.json与current-native-review-config.json，不翻policy/倒签/扩工具/伪receipt；稳定共享链时修复既有材料配置或等价独立可核查只读来源，保留机械阻断。新增独立请求0；原可定位5次不是全G/control已核清，新增前核账，不宣称余额/重置。G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important/implementation/open保持。并行发现已跑，r61报告缺失未知，不当已消费。

全部源码写入/突变恢复/Rebuild/测试/audit终态，只有编译服务器驻留。源父聊天实时idle/completed。保护User/JS/.kiro/旧D盘、材料外R56/csproj/工具/文档/暂存，无push/发布/部署。接力理由：容量与物理归档存储转换形成聚焦可复核边界，下一步实际新Host/四入口生命周期需要独立状态枚举；不按时间/工具数切换。旧Goal暂停和新Goal active须原生读回。
'''
(d/'HANDOFF.md').write_text(handoff,encoding='utf-8')
original=(old/'relay-prompt.txt').read_text(encoding='utf-8');first,body=original.split('\n',1)
prefix=f'''
先建本批完整Goal（结果＋约束＋完成判据），再开工。最新标记{marker}，来源{thread}，唯一同项目local，cwd E:/Program Files/better-genshin-impact-LCB；本relay={d.relative_to(r).as_posix()}。仍原共享包，不重开opening/历史/预算/请求/原级义务，不恢复旧Goal。
最新握手/Goal/唯一下一项以本段和本relay/HANDOFF.md为准，下方继承提示词所有旧握手仅作历史不执行。按handoff-inherit-model-20261004-v1继承实际gpt-6.1-sol/medium，证据本relay/source-model-observation.json，复杂独立综合审查仍sol/high。
先shell核cwd，get_goal查本人，无Goal仅建一次完整摘要绑定本relay-prompt全文/HANDOFF/CURRENT-HANDOFF/下方原范围；读回active。只读核本人最新rollout实际turn_context.model/effort与collaboration_mode.settings一致且sol/medium，确认threadId；cwd/完整activeGoal/实际模型成立前禁改代码。保存get_goal完整返回本relay/new-goal-readback.json；保存actualCwd/threadId/marker/范围绑定/model/effort/settings/ordinal/rollout为new-handshake-observation.json并立即commentary握手结果。实际读本relay/old-goal-paused-readback.json，不凭文字认定来源paused；成立后自主继续，不等owner继续。
全文读本relay/HANDOFF.md、CURRENT-HANDOFF最新段、前relay CAPACITY-CANDIDATE.md及final原始读回/精确TRX比较/突变/原日志。当前产品候选{commit}；分支main-OldTeaBag-B168，HEAD动态核，保护材料外，不制造干净工作区。原类型化候选d50c7e3和异步5c46c9a行为保持。
最新33直接证据/普通TRX层级与剩余转换详见本HANDOFF：下一共享依赖是TerminalPendingTransfer饱和原身份保留、真实新Host恢复/停止与四真实入口不同父来源完整因果矩阵，连同G4/G7多轮迟到冲突、封印、停止恢复全部原级义务。新存储实例读回不是新Host/真进程重启；原RecoveryAdmissionTests的FakeBoundary不能当生产successor证明。先完整状态/并发/故障矩阵与红例再集中修复，稳定共享链后真实来源认证/sol high独立综合后审/原级成批闭环，所有约定功能实机及可运行版本仍为总Goal判据。
本来源新增独立请求0，历史/预算不重置。当前audit2缺原receipt；不翻policy/倒签/伪receipt/扩通用工具，修复既有材料配置或等价可核查独立只读来源并保留机械阻断；认证与正式综合后审仍必需，普通TRX不可替。执行原始oldGoal/范围/全部功能/保护/自动提交/必要交接规则，复用g10 products，构建测试串行。
以下继承全文的原完整范围和历史保持，旧最新/握手/唯一下一项只由本段覆盖，不执行旧握手，不缩总目标：
'''
(d/'relay-prompt.txt').write_text(first+'\n'+prefix+body,encoding='utf-8')
current=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=current.read_bytes();(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),lines=len(b.splitlines()))));nl='\r\n' if b'\r\n' in b else '\n'
heading=f'''## 2026-10-04 33次准入及原身份实际归档候选（当前优先）

来源{thread}，候选{commit}。面板/移交各33实际准入accepted、逐次占位后容量最大2/1；原父来源/发送身份/封印不变，产品迁移算法实际归档34/33完整操作，新的存储实例读回及旧身份续用保守拒绝/零新增发送。不是新Host/真进程重启或IPC/游戏/User验收。详情auto-relay-capacity-identity-20261004-from-01a105f1/CAPACITY-CANDIDATE.md和final原始逐次读回。

Rebuild0；全量1971/18/2=1991、exit1；相邻401/0/2=403、exit0；精确合集1981/18/2=2001，对前序新增3/删除0/共有变化0，18失败ID相同、不豁免。声明面同SHA、13输入前后字节一致；两个指定PFP（去迁墓碑/归档改原WireSubmitKey）红后SHA恢复。普通证据非认证receipt/独立pass。audit2仍原native receipt缺失；全部原级open/预算/生产门和总交付保持，新增请求0。

唯一下一项：TerminalPendingTransfer饱和原身份保留、真实新Host初始化/恢复/停止与四真实入口不同原来源因果全表，再原G4/G7链及统一认证/综合后审。原Paused/Interrupted组件恢复夹具用FakeBoundary，不冒充生产successor；本候选只新存储实例重开。最新完整Goal/握手/模型/下一项读auto-relay-four-entry-recovery-20261004-from-01a1061f/HANDOFF.md和relay-prompt.txt。User/JS/.kiro/旧D盘与材料外保护，未部署/发布/push。总目标未完成；旧/新Goal状态须原生查询，不凭正文自述。

'''
current.write_bytes(heading.replace('\n',nl).encode('utf-8')+b);assert current.read_bytes().endswith(b)
print(str(d),len((d/'relay-prompt.txt').read_bytes()),flush=True)
