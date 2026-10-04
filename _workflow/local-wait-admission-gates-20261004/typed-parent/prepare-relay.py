from pathlib import Path
import json,subprocess,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-capacity-identity-20261004-from-01a105f1';d.mkdir(exist_ok=False)
source='01a105f1-d84c-75b3-9b49-58d83c84e1d7';marker='AUTO-CAPACITY-IDENTITY-RELAY-20261004-FROM-01a105f1';rollout=Path('C:/Users/Administrator/.codex/sessions/2026/10/04/rollout-2026-10-04T16-04-58-'+source+'.jsonl')
contexts=[(i+1,x['payload']) for i,line in enumerate(rollout.read_text(encoding='utf-8').splitlines()) if (x:=json.loads(line)).get('type')=='turn_context'];ordinal,c=contexts[-1]
assert c['model']==c['collaboration_mode']['settings']['model']=='gpt-6.1-sol';assert c['effort']==c['collaboration_mode']['settings']['reasoning_effort']=='medium';assert c['cwd']==str(r)
(d/'source-model-observation.json').write_text(json.dumps(dict(source_thread_id=source,rollout=str(rollout),ordinal=ordinal,model=c['model'],effort=c['effort'],settings=c['collaboration_mode']['settings'],actualCwd=str(r)),ensure_ascii=False,indent=2),encoding='utf-8')
original=(r/'_workflow/local-wait-admission-gates-20261004/auto-relay-typed-parent-20261004-from-01a105cd/relay-prompt.txt').read_text(encoding='utf-8-sig');goal=original.splitlines()[0];inherited=original[original.index('继承original-host-history/PLAN.md'):]
intro=f'''先建本批完整Goal（结果＋约束＋完成判据），再开工。标记{marker}，来源{source}；唯一同项目local，cwd E:/Program Files/better-genshin-impact-LCB，沿原工作区，不克隆/换worktree、不恢复旧Goal。仍属原local-wait-admission-gates-20261004共享包，不重开opening、历史、预算、请求或原级义务。
本relay={d.relative_to(r).as_posix()}。按handoff-inherit-model-20261004-v1继承来源实际model=gpt-6.1-sol、thinking=medium，实际rollout证据本relay/source-model-observation.json；复杂独立综合审查仍sol/high，不因主施工medium降强度。
开工先shell核cwd；get_goal查询本人，无Goal仅建一次完整摘要（objective限4000字符，绑定本relay-prompt全文、HANDOFF.md、CURRENT-HANDOFF.md及原完整范围入口），读回active；已有核完整范围不重建。只读核本人当前最新实际rollout turn_context.model/effort及collaboration_mode.settings一致且sol/medium，确认threadId。cwd/完整activeGoal/实际模型成立前不得改代码。保存get_goal完整返回new-goal-readback.json；保存actualCwd/threadId/marker/范围绑定/model/effort/settings/ordinal/rollout为new-handshake-observation.json，立即commentary握手成立或具体阻断。成立后自主继续，不等owner继续。实际读本relay/old-goal-paused-readback.json，不能凭提示词认定来源暂停。
全文读本relay/HANDOFF.md、CURRENT-HANDOFF最新类型化来源段、typed-parent/CANDIDATE.md、typed-parent/final/candidate-observation.json、impact-comparison.json、post-mutation-byte-observation.json、candidate-commit-observation.json、parent-capacity/PLAN.md、PARENT-SOURCE-DESIGN.md和LOCK-CANDIDATE.md。产品候选d50c7e3bb6d49bf780cd7da18009f43a8011f740，分支main-OldTeaBag-B168；文档HEAD动态核。核实际分支/HEAD、暂存/未暂存/新增、在途写者；opening-observation.json为交接现场，保护材料外工作，不制造干净工作区。
原完整范围入口仍auto-relay-typed-parent-20261004-from-01a105cd/relay-prompt.txt、auto-relay-parent-capacity-20261004-from-01a105a5/relay-prompt.txt、auto-relay-original-host-history-20261004-from-01a10568/relay-prompt.txt、auto-relay-g7-original-round-20261004-from-01a10546/relay-prompt.txt、auto-relay-cursor-consumption-20261004-from-01a10502/relay-prompt.txt、auto-relay-server-evidence-20261004-from-01a104a0/relay-prompt.txt。旧提示词只读原完整范围/历史，不执行旧握手或覆盖旧观察；最新候选、唯一下一项、握手目录、模型以本消息和CURRENT-HANDOFF覆盖。
最新候选已实现但未认证/未后审/未验收：CreateRun同受理保存nullable省略的版本1 AdmissionParentSource（Kind/run/workflow/完整Scope/原IntentKey及不可变AdmissionHandoffIdentity的executionId/stepId/triggerKind/mode/扩展字段）；可选execution/step为空不新增业务必填。旧Scope+Handoffs不补造，普通/合并/新记录不能改Scope或来源、删改重复原Handoff。解析返回类型化引用，拒绝错run/workflow、未知版本、缺/重复/变更原绑定、面板与移交并存。生产successor同Lease mutation核当前唯一来源与冻结预期/候选run/Scope，原子绑定ParentSource/ParentRequestIdentity；占位/重驱/重试核原来源，宿主占位后准备/发送前再核。RunStore-only hook不回读Lease/await外端口，移交不另造租约父操作。移交自有占用豁免仍须无全局未决与其他在飞节点。通用非successor组件合同保留，不冒充生产证据。
最终Rebuild exit0；TaskCenter1968 passed/18 failed/2 skip=1988，exit1；同DLL相邻定向398 passed/2 skip=400，exit0；精确合集1978/18/2=1998，对前序1969新增29、删除0、共有变化0，18失败testId集合一致，旧失败不是豁免。声明面再生成前后同SHA且无变量再跑通过。13输入前后字节一致；当前M1普通来源守卫（mutations/M1）、M2版本（model-mutation-r2/M2）、M3占位和M4宿主发送前复核（recheck-mutations-r2）四项P/F/P均指定断言红、恢复源码SHA绑定final输入。去M3后来源失效仍Accepted；去M4后实际受控端口发送1而应0。读全部原TRX/log与前序失败，不只汇总。
来源真实RunStore/LeaseStore/Host/Runner/Panel/受理入口，执行端口受控，不是真IPC/游戏/User验收。首轮1934/28/2退化过程保留；通用组件误加生产Scope约束后恢复原合同，两个successor旧夹具补真实RunStore受理输入、原核心发送/重放断言未改。宿主非拥有者Lease mutation被拒和物理删父/改ID破坏PreObservations引用的失败不记语义红；最终来源权威漂移保持原发送/历史引用，原记录均保留。普通TRX/byte观察不是认证receipt或独立pass。
原异步候选5c46c9a的Host扫描/终局WaitAsync与面板await保持；同步API兼容，对账Task.Run独立调度保留，取消等待不清偿，源与保护数据不改。后续容量/来源修改须重核原锁交错和停止/封印/未决责任。
唯一下一共享依赖转换：基于类型化原父引用补33节点每次实际准入原因码/容量读回、完整原发送身份Tombstone/TerminalPendingTransfer/ArchivedOperations/重启恢复，再核四真实入口不同原来源因果矩阵，连同G4/G7多轮发送/恢复/停止全部原级影响。原33普通Fact存在并在本相邻回归通过，不称“无夹具”；最终Ops计数/普通Fact不能代逐次原因/物理迁区归档/重开。先列各状态和并发故障全表，红反例、集中修复及实际生产链取证，不组件绿/capability=true或_successorAdmissionWired=true冒充交付。仍G2(e)/G4/G4a/G7/G8/G10/⑤⑥全部important/implementation/open；类型化候选没有关闭它们。
仍须修复现有材料配置或使用等价可核查独立只读来源，保留机械阻断，不扩通用工具、不翻owner-policy、不倒签planpass、不伪receipt。最新audit mechanical-review2.log exit2仍原native请求缺receipt.json，旧manifest缺证据列表、policy=false/nativeprepare专属另一Goal无限授权/implgate原planpass限制保持。正式认证和综合独立后审仍必需，沿original-send-round/current-review-config.json及current-native-review-config.json完整源域。原可定位5次不是全部G/control历史已核清；本来源新增请求0，新增审查前核账，8次/有限额外授权不重置。并行只读发现仍r61报告缺失未知，不阻独立修复、不当已消费，不让owner搬运。
以下继承原历史与完整交付要求，最新来源/握手/下一项以上文覆盖；不执行旧握手、不重开原账：
'''
(d/'relay-prompt.txt').write_text(goal+'\n'+intro+inherited,encoding='utf-8')
handoff=f'''# 类型化原父来源候选后的容量／恢复接力

来源{source}，标记{marker}。同原共享包，总Goal未完成，原opening/预算/报告/important implementation open保持，生产门关闭。

产品候选d50c7e3bb6d49bf780cd7da18009f43a8011f740，13源码/测试及本批原始证据明确范围本地提交。实际提交文件集和final源字节读typed-parent/candidate-commit-observation.json。最新HEAD/工作区动态核，材料外R56/csproj/工具/文档/暂存和User/JS/.kiro/旧D盘保留。

已实现类型化不可变首Handoff受理来源、原Scope/绑定普通和合并写者保护、同Lease事务原父绑定、占位/重驱/重试与宿主发送前复核；移交不造新租约父，RunStore-only hook无Lease回读/await。旧缺锚/错run/workflow/未知版本/冲突不补造。可选execution/step为空保持。通用非successor组件合同和异步停止候选保留，详情typed-parent/CANDIDATE.md。

final Rebuild0；全量1968/18/2=1988，exit1；定向398/0/2=400，exit0；精确合集1978/18/2=1998，较旧1969新增29/删除0/共有变化0，18失败身份一致、不豁免。声明面同SHA、13源码/测试前后字节一致；当前4项P/F/P指定断言红且source/restored SHA绑定final输入。原五红例/首轮28失败/编译和注入失败均保留。实际组件/受理链配受控执行端口，不是真IPC/游戏/User；普通TRX/byte非认证receipt/独立pass。

审计mechanical-review2 exit2，原native请求缺receipt.json。policy=false/旧manifest/planpass门及原级义务不改，未扩工具/倒签/伪receipt；认证真实来源、综合后审和完整历史核账仍欠。新增独立请求0，不凭可定位5次自称余额。并行r61报告缺失未知，不当已消费。

唯一下一项：33逐次真实准入原因/容量和完整原发送身份热区/迁区/归档重开，再四真实入口来源因果及G4/G7停止恢复。原33Fact存在并通过本相邻回归，仍欠上述直接证据；全部原功能新产物实际运行/停止/重启/数据保留与可运行版本、认证/综合后审/原级闭合才complete。读本relay-prompt全文及CURRENT-HANDOFF，不缩目标。

交接理由：受理父来源到发送前验证达到聚焦可复核边界，下一步容量/历史迁区需独立枚举状态矩阵；不按时间/工具数强制交接。旧执行者已结束源写入/突变/构建/测试，只编译服务器驻留。先原生暂停本人Goal并读回paused，再唯一同项目local创建；模型继承实际sol/medium，复杂审查sol/high。新cwd/完整activeGoal/实际模型读回前禁写；继承原完整范围，不恢复旧Goal。
'''
(d/'HANDOFF.md').write_text(handoff,encoding='utf-8')
current=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';old=current.read_bytes();entry=f'''## 2026-10-04 类型化原父来源候选（当前优先）

来源{source}，产品候选d50c7e3bb6d49bf780cd7da18009f43a8011f740；13源码/测试和原过程明确范围本地提交，未push/发布/部署。版本1类型化原Handoff受理来源、Scope/原绑定写者保护、同Lease事务ParentSource/ParentRequestIdentity、占位/重驱/重试与宿主发送前复核已实现候选。旧缺锚/错run/workflow/未知版本/歧义/跨类别冲突拒绝，可选execution/step不新设必填；通用组件合同及异步停止保持。详情typed-parent/CANDIDATE.md与candidate-commit-observation.json。

最终Rebuild0；全量1968/18/2=1988 exit1；同DLL相邻398/0/2=400 exit0；精确合集1978/18/2=1998，对旧1969新增29、删除0、共有变化0，18失败testId相同，不豁免。声明面同SHA、13源输入前后字节一致，当前四项P/F/P指定断言红、SHA恢复绑定final。普通组件/受控端口/TRX/字节观察，非认证receipt/独立综合实现pass/IPC游戏User验收。原28失败退化及编译/非拥有者/破坏历史引用的注入失败保留，不冒充语义红。

最新audit mechanical-review2 exit2原native请求缺receipt；policy=false/旧manifest/planpass机械阻断、全部原级G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important implementation open保持，新增独立请求0，全历史未核清不自称余额，生产门关闭，总交付未完成。

唯一下一共享依赖：类型化来源后的33节点逐次实际准入原因/容量/完整原发送身份Tombstone/TerminalPendingTransfer/ArchivedOperations/重启恢复，再四真实入口来源因果及原G4/G7全部链。原33普通Fact存在并通过相邻回归，仍欠直接逐次/归档/入口证据。最新握手/完整Goal/实际模型要求读auto-relay-capacity-identity-20261004-from-01a105f1/relay-prompt.txt和HANDOFF.md；旧握手不执行。本次自然转换边界按原生暂停和实际模型继承自动接班；旧Goal/新Goal状态须读回，不凭本段自述。User/JS/.kiro/旧D盘与材料外成果保护。

'''
(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(old),sha256=hashlib.sha256(old).hexdigest())),encoding='utf-8');current.write_bytes(entry.replace('\n','\r\n').encode('utf-8')+old)
print(d.relative_to(r).as_posix(), 'prompt chars',len(goal+intro+inherited),flush=True)
