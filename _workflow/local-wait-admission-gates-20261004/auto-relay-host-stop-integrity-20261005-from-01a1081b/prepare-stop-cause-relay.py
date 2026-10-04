import json,hashlib,subprocess
from pathlib import Path
root=Path.cwd()
base=Path(__file__).parent
final=json.loads((base/'stop-final-observation.json').read_text(encoding='utf-8'))
assert final['counts']=={'Passed':2136,'Failed':2,'NotExecuted':2}
nextdir=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f'
nextdir.mkdir(exist_ok=False)
marker='AUTO-STOP-CAUSE-SOURCE-20261005-FROM-01a1083f'
current='_workflow/local-wait-admission-gates-20261004/auto-relay-host-stop-integrity-20261005-from-01a1081b'
nextrel=nextdir.relative_to(root).as_posix()
handoff=f'''# 原停止因果与完整来源接续

来源 01a1083f-deee-7042-8414-d5a4723ac658，标记 {marker}，本relay={nextrel}。仍原共享包/opening/全部98finding与36unknown/五新增及 CORRUPT-STOP-OWNER-CONTRACT-1 原级责任，不重开、不降级、不恢复旧Goal。施工继承来源实际 Sol/medium，独立综合后审仍 Sol/high；来源模型原件将在本目录 source-model-observation.json，暂停以原生 old-goal-paused-readback.json 为准。

最新源候选、提交身份及工作区以 {current}/stop-commit-observation.json 与本目录 source-head/status 为准，旧HEAD仅历史。原资格候选300460f2及此前所有有效候选保全，Host已BindOwner；不得照旧提示撤销实际接线。

本聊天新增3源码/测试（2产品、1测试）：Host终局对账在封印前/循环/最终逐项核冻结原映射身份、类型、发送轮次、流程，不以剩余登记掩盖同run丢失项；老格式缺新字段但已有Wait绑定不把原账丢失当NoMapping；合法墓碑/归档仍按原关系消费，不重写原run/operation。驱动正常观察捕获非终局结果时不启动后来显式Stop的终局事务，避免13次/预期5次的共享收尾竞态。原Root能力、跨进程fence、合法B恢复、共享10s关闭、F11不可执行诊断、原映射不可变全部保留。

关键普通因果：partial-mapping-red-r5真实Start→Resume两原登记，移除一项及其预观察后租约仍Valid，Expected Unavailable/Actual Effective行为红；r1-r4夹具结构/引用错误原件保留，不算有效反例。parked-observer-red-r1 Expected0/Actual1。legacy-retirement-red-r1两老格式同会话/重开Effective假成功已红。合法退休首fixture错用未取得能力的新store被拒 lease_stale_generation，改用Host实际store；四正例只证明合法形状/重开消费，不冒24h自然清理验收。

unknown-owner-contract-candidate-r1新增两安全合同oracle：corrupt/unsupported下Unavailable且run/queue原字节完全不变；恢复实际原存储后同一合法owner重试Effective，原run真实Cancelled/StopRequested且原登记TerminalCompleted。两原UnreadableAdmissionState的Cancelled冲突断言仍逐字保留，原级open；不自授合同改写，不开无owner发布旁路。这两个候选不是独立裁决；后续必须将冲突和本原层证据一起送统一综合裁决。

最终stop-integrity-final-r3：助手/测试/Probe各串行Rebuild0，run1，2136Passed/2Failed/2原NotExecuted=2140。对资格2129新增11/删除0/共有name-outcome变化0；两原Failed仍ExpectedCancelled/ActualLocalWaitParking，声明面Passed。277输入执行前后及r3三PFP最后恢复后同字节，stop-testid-comparison/stop-final-observation/stop-source-final与原TRX/log/products观察保存。r3三项ordinary P/F/P：partial-original-mapping、legacy-missing-original、parked-observer-terminal-race，指定行为断言红后原子恢复同SHA且恢复绿。r1/r2及前序全部原件保留，LF误转CRLF已真实恢复原LF，增加安全oracle后用r3重做，不改绑旧证据。

current-live-module-observation只有本次唯一隔离testhost的129已加载模块路径/hash、创建与父子身份，是post-build观察，不代SDK/MSBuild/task/compiler/package/native完整capture-before-build闭包。当前普通TRX/PFP不是认证receipt、独立pass、实际IPC游戏User或可运行分发。原07125/55fcd保持历史；sourcepolicyfalse/9f85缺receipt与旧机械约束保留。原manifest evidence空的audit仍机械blocked，不改工具或自造证据。并行r61报告missing仍未知/未消费，bundle36169fbf...核实。

唯一下一共享转换：把原停止资格合同、节点原发送/缺全部锚的老格式关系与stop/permit/history/terminal双合法次序集中在原层证明；完整10s/15s/TTL与真实端口余矩阵，原未知责任不能假NoMapping、不能借后继能力、不能用永久Pending削功能。随后全真实来源和完整SDK/MSBuild/task/compiler/package/native依赖认证，再用余最多1次Sol/high全源域综合实现复核及全部原级闭合，全部原约定功能新产物实际运行/停止/重启/数据保留/效果耐久和可运行交付。当前本聊天独立请求0；owner implementation2已用1、余最多1/plan0，旧CLI9/native兼容22及历史错误不重置，不借另一Goal无限政策，不小函数/纯前审。

完整范围绑定前relay-prompt全文、本候选/修复/最终原件、本HANDOFF与CURRENT-HANDOFF最新段以及原总计划、2026-09-17比較/兼容审计和DELIVERY-COVERAGE。原Report026c0ee0 Sol/high blocked、98finding/36unknown原文及五新增均继承。原报告字面引用差异登记保留，不改报告；全源码导航不是白名单，真实User/凭据/生成输出/历史副本排除，不扩工具、不整树复制。

本原映射与非终局收尾候选的红例/回归/突变恢复形成可复核安全边界；转剩余停止因果/节点关系及来源认证按任务语义接续，不按时间/工具数。当前操作均终态后才提交、保存、原生暂停来源并创建唯一同项目local接班。总Goal未完成、生产门关闭；旧Goalpaused/新完整activeGoal/cwd/实际模型成立前新执行者不写产品。保护User/第三方JS/.kiro/旧D盘/材料外/所有必要JSON和原报告失败，不push/部署发布/清盘/reset/杀用户程序。
'''
prompt=f'''/goal 完成槲寄生原计划全部约定功能最终交付：保全原能力、跨进程fence、Host资格/关闭与原映射完整性全部候选，原层统一停止资格合同、老格式和节点原发送关系及完整停止因果；集中全部共享正确性、真实来源完整依赖认证、余最多1次Sol/high综合实现复核和原级闭合，全部新产物实际运行/停止/重启/数据保留/效果耐久与可运行版本；保护User和材料外，证据成立前生产门关闭，不伪成功。

先建本批完整Goal（结果＋约束＋完成判据三要素），再开工。唯一标记{marker}；来源01a1083f-deee-7042-8414-d5a4723ac658；本relay={nextrel}。同项目local/cwd E:/Program Files/better-genshin-impact-LCB，不克隆/worktree、不重开原opening/历史/全部原级责任/预算，不恢复旧Goal。

按handoff-inherit-model-20261004-v1继承来源最近实际turn_context gpt-6.1-sol/medium、settings一致（本目录source-model-observation原件），独立后审仍Sol/high。先shell核actualcwd；get_goal查本人，无Goal只建立一次objective<=4000字符完整摘要，绑定本relay-prompt全文、本HANDOFF及CURRENT-HANDOFF最新段全部总范围，读回active；已有核完整范围不重建。核本人实际最新rollout model/effort/settings/threadId，保存原生完整new-goal-readback.json与new-handshake-observation.json（actualCwd/threadId/marker/完整范围绑定/model/effort/settings/ordinal/rollout），立即commentary报告握手。实际读old-goal-paused-readback确认来源paused；cwd/完整activeGoal/实际模型成立前禁止产品写入。未知不猜、不重复创建接班。握手后自主连续施工，不等owner继续。

完整读本HANDOFF、CURRENT-HANDOFF最新段、{current}/STOP-INTEGRITY-REPAIR.md、STOP-INTEGRITY-CANDIDATE.md、stop-final-observation/stop-source-final/stop-testid-comparison/stop-commit-observation、stop-integrity-final-r3完整原TRX/log/277输入前后/产物、r3三PFP全部原三腿/原子恢复及unknown-owner-contract-candidate-r1真实两oracle。沿{current}/relay-prompt.txt及HANDOFF完整继承所有前权威材料；其中旧“本聊天/当前/下一项/Host未接线”只是历史，以最新候选与本段为准，不重新执行旧时间性状态、不撤回已BindOwner候选、不重建原包。所有原报告/原失败/重要责任逐字和原级保留，原证据不改绑。

{handoff}

总Goal完整范围：公版共有功能、C01/C02/C04-C11/C17/C20、八类原生单项、priority/fixed/flexible、legacyFiltered/空weekdays/once水位/迁移、跨天截止、管理互导/跳转、C17真实耐久、迁移激活回退、旧失败/兼容和正式分发，以原总计划、2026-09-17比较/兼容审计、_workflow/usable-delivery-20261003/DELIVERY-COVERAGE为权威，不削功能。缺功能/误执行/数据风险/错误释放/未知停止/假成功不得延期；普通BUG只按owner政策原级明确影响登记。仅全部约定功能新产物正确WindowsSession实际运行/停止/重启/数据保留与真实效果耐久读回、可运行版本/启动步骤、完整来源依赖认证、独立综合实现复核及全部原级义务闭合方complete。受控端口/组件/旧D安装/强杀/日志/机制绿不代IPC游戏User验收；证据前生产门关闭。

采用现行AGENTS、delivery-first、自动本地提交、模型继承；读bgi-project-development导航、mistletoe-independent-review/work-package-review、完整审查工序/设施/tools README，核bundle36169fbf7baed38338f2ac7a1cb2681ba4a236ae81e9a282025c7efc63c5bc8c。读并行成果md/json并运行deliveries.py只读发现，自主核待消费成果，不让owner搬材料；r61 missing保持未知，不阻独立安全修复。沿原manifest/opening/账本执行机械audit/verify或如实登记适用阻断和等价人工核验，不扩工具/翻policy/伪receipt，不倒签前审/重开budget。完整状态/并发/故障/写者矩阵→真实红因果→集中必要修复→串行-t:Rebuild -p:DeployToBgiTools=false及影响/原规定回归→关键PFP finally同目录原子恢复同SHA→声明面→完整实际来源依赖认证→余1统一Sol/high全源域综合实现复核/原级闭环→全部实机可用交付。固定源码只读Agent无净收益可记录理由；正式独立综合保来源及强度。剩余额度用尽仍must/important未闭合时保持原级阻断并给具体有限范围检查点，不降级/拆包/滚动续期。

写前后识别hash/bytes/lines/BOM/换行并复核diff。安全终态归属明确主动git commit --only路径检查点，本地候选与最终验收区分；longpaths只命令级，保护其他暂存/材料外。使用专属fresh产物，不复制历史产物、不整树复制、不清盘、不杀用户进程、不push/合并/部署发布。全部User/配置/宏/脚本/截图、第三方JS一字不改、.kiro/旧D盘/必要JSON/原报告失败保全；只管理本次明确创建且核创建身份的隔离测试树，不把强杀当验收。

必要接续先安全终态/明确本地提交/保存当前Git和原级责任，再原生查询暂停本人未完成Goal并读回paused，核最近实际model/effort/settings继承，恰好创建同项目local一个完整初始/goal接班，wait核cwd/完整activeGoal/实际模型；未知保留身份不重复。无临时Goal、不恢复旧Goal、不新增hooks/心跳/定时器/后台merge，不让owner搬提示词、协调模型或反复授权。旧暂停Goal不自动恢复施工。
'''
(nextdir/'HANDOFF.md').write_text(handoff,encoding='utf-8')
(nextdir/'relay-prompt.txt').write_text(prompt,encoding='utf-8')
(nextdir/'source-head.txt').write_text(subprocess.check_output(['git','rev-parse','HEAD'],text=True),encoding='utf-8')
status=subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain'],text=True,encoding='utf-8')
(nextdir/'source-status.txt').write_text(status,encoding='utf-8')
print(nextdir.relative_to(root).as_posix(), 'prepared exact one continuation prompt; not dispatched')
