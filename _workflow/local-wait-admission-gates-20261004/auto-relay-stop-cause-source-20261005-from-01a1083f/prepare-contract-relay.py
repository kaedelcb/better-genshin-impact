from pathlib import Path
import json,hashlib,subprocess,os
root=Path.cwd();base=Path(__file__).parent;relay=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
relay.mkdir(exist_ok=False)
summary=(base/'STOP-CAUSE-CANDIDATE.md').read_text(encoding='utf-8')
(relay/'HANDOFF.md').write_text('# 原历史节点模式、停止合同与完整来源接续\n\n来源01a1086d-4ff9-77a0-8650-a0e0c8503834；标记AUTO-STOP-CONTRACT-SOURCE-20261005-FROM-01a1086d。\n\n'+summary,encoding='utf-8')
handoff=root/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=handoff.read_bytes();nl='\r\n' if b'\r\n' in b else '\n'
(base/'current-handoff-preappend.json').write_text(json.dumps(dict(sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),newline=repr(nl)),indent=2),encoding='utf-8')
handoff.write_bytes(b+('\n\n## 2026-10-05 节点原发送关系候选与历史停止合同接续（当前优先）\n\n'+summary).replace('\n',nl).encode('utf-8'))
rollouts=list(Path('C:/Users/Administrator/.codex/sessions/2026/10/05').glob('*'+os.environ['CODEX_THREAD_ID']+'.jsonl'));assert len(rollouts)==1
events=[json.loads(x) for x in rollouts[0].read_text(encoding='utf-8').splitlines()];contexts=[e['payload'] for e in events if e['type']=='turn_context'];c=contexts[-1];settings=c.get('collaboration_mode',{}).get('settings',{})
assert c['model']=='gpt-6.1-sol' and c.get('effort',c.get('reasoning_effort'))=='medium'
assert settings.get('model')==c['model'] and settings.get('reasoning_effort')=='medium'
(relay/'source-model-observation.json').write_text(json.dumps(dict(threadId=os.environ['CODEX_THREAD_ID'],rollout=str(rollouts[0]),ordinal=len(contexts),model=c['model'],effort=c.get('effort'),settings=settings,turn_id=c.get('turn_id')),ensure_ascii=False,indent=2),encoding='utf-8')
msg='''/goal 完成槲寄生原计划全部约定功能最终交付：保全原能力/fence/Host资格关闭/原映射/节点关系全部有效候选，在原层集中统一历史节点模式、老格式原关系与停止资格合同、完整停止因果及来源依赖认证，余最多1次Sol/high综合实现复核与全部原级闭合，全部新产物实际运行/停止/重启/数据保留/效果耐久及可运行版本；保护User/材料外，证据前生产门关闭，不伪成功。

先建本批完整Goal（结果＋约束＋完成判据），再开工。唯一AUTO-STOP-CONTRACT-SOURCE-20261005-FROM-01a1086d，来源01a1086d-4ff9-77a0-8650-a0e0c8503834，本relay=_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d。同项目local/cwd E:/Program Files/better-genshin-impact-LCB；不克隆/worktree、不重开原包/opening/历史/责任/预算、不恢复旧Goal。

按handoff-inherit-model-20261004-v1继承来源最新实际turn_context gpt-6.1-sol/medium、settings一致（source-model-observation原件），独立后审仍Sol/high。先核shell actualcwd/get_goal；本人无Goal只建立一次objective<=4000完整摘要，绑定本relay-prompt全文/HANDOFF/CURRENT-HANDOFF最新及前继全部范围；读回active。已有核完整范围不重建。核本人实际最新rollout model/effort/settings/threadId，保存原生完整new-goal-readback.json和new-handshake-observation.json（actualCwd/threadId/marker/fullScopeBinding/model/effort/settings/ordinal/rollout）；立即commentary报告握手。读old-goal-paused-readback确认来源paused；cwd/本人完整activeGoal/实际模型成立前禁止产品写入。未知不猜、不重复接班。握手成立后自主连续施工，不等owner继续。

完整读本HANDOFF、CURRENT-HANDOFF最新段，_workflow/local-wait-admission-gates-20261004/auto-relay-stop-cause-source-20261005-from-01a1083f/STOP-CAUSE-CANDIDATE.md、STOP-CAUSE-REPAIR.md、node-final-observation/node-source-final/node-testid-comparison/node-commit-observation、node-source-final-r3完整原TRX/log/277输入前后及产物观察、node-pfp-r3-observation两组全部原三腿/原子恢复；原r1/r2失败原件保留。继承该前relay-prompt全文/HANDOFF及所有前权威材料，含Host BindOwner/原能力/fence/关闭/原映射完整性与非终局收尾、unknown-owner-contract-candidate-r1两oracle。旧“本聊天/当前/下一项/Host未接线”仅历史，以最新候选为准，不撤回已BindOwner候选、不重执行历史时间性状态。原报告/原失败/原级责任逐字保留、不改绑。

'''+summary+'''

唯一下一共享转换：在原层集中证明旧格式缺全部锚、仅部分原账丢失的历史节点模式/原关系，不能把当前_successorAdmissionWired开关当历史权威。两原坏租约Cancelled断言与原owner资格/fence冲突必须统一合同及独立综合裁决，原断言不改为绿，不开无owner发布旁路。补stop/permit/history/terminal双合法次序、真实端口及原完整10s/15s/TTL余矩阵；不能借后继能力、不能永久Pending削功能、不能未知假NoMapping。随后完整真实来源SDK/MSBuild/task/compiler/package/native capture-before-build依赖闭包与认证，再用余最多1次Sol/high全源域综合实现复核、全部原级闭合及原全部功能真实交付。原98finding/36unknown及五新增/CORRUPT-STOP-OWNER-CONTRACT-1原级保持；原综合报告026c0ee0及其本层5finding/39unknown/逐项原处置全继承。原CLI9/native兼容22/历史失败、owner implementation2已用1余1/plan0不重置；本来源独立请求0。不开小函数/纯前审，不借其他Goal无限政策；预算用尽仍must/important未闭合保持原级阻断，提供具体固定次数/范围检查点，不降级/拆包/滚动续期。

总Goal范围：公版共有功能、C01/C02/C04-C11/C17/C20、八类原生单项、priority/fixed/flexible、legacyFiltered/空weekdays/once水位/迁移、跨天截止、管理互导/跳转、C17真实耐久、迁移激活回退、旧失败/兼容与正式分发。权威原总计划、2026-09-17比较/兼容审计、_workflow/usable-delivery-20261003/DELIVERY-COVERAGE不削功能。缺功能/误执行/数据风险/错误释放/未知停止/假成功不得延期；普通BUG只据owner政策原级明确影响登记。仅全部约定功能新产物正确WindowsSession实际运行/停止/重启/数据保留与真实效果耐久读回、可运行版本/启动步骤、完整来源依赖认证、独立综合复核及全部原级义务闭合方complete；受控端口/组件/旧D安装/强杀/日志/机制绿不代IPC游戏User验收。证据前生产门关闭。

采用现行AGENTS、delivery-first、自动本地提交、模型继承；读bgi-project-development导航、mistletoe-independent-review/work-package-review、完整审查工序/设施/tools README，核bundle36169fbf7baed38338f2ac7a1cb2681ba4a236ae81e9a282025c7efc63c5bc8c。读并行成果md/json并运行deliveries.py只读发现，自主核待消费成果，不让owner搬材料；r61 missing未知/未消费，不阻独立安全修复。沿原manifest/opening账本机械audit/verify或如实登记适用阻断及等价核验，不扩工具/翻policy/伪receipt，不倒签前审/重开budget；原9f85缺receipt/sourcepolicyfalse/旧07125及55fcd历史不改。状态/并发/故障/写者矩阵→真实红因果→集中必要修复→串行-t:Rebuild -p:DeployToBgiTools=false及原规定回归→关键PFP finally同目录原子恢复同SHA→声明面→完整真实来源/依赖认证→余1统一Sol/high全源域综合复核/原级闭合→全部实机可用交付。固定源码只读Agent无净收益可记录理由，正式后审保独立来源/强度。

写前后hash/bytes/lines/BOM/换行并核diff，无大幅缩水。安全终态归属明确主动git commit --only明确路径检查点，本地候选与正式验收分开；longpaths只命令级，保护其他暂存和材料外。使用专属fresh产物，不复制历史产物/整树复制，不清盘/reset/push/合并/部署发布/杀用户进程；全部User/配置/宏/脚本/截图、第三方JS一字不改、.kiro/旧D盘/必要JSON/原报告失败保全；只管理本次明确创建且核创建身份的隔离测试树，强杀不当验收。

必要接续先安全终态/明确本地提交/保存Git/责任，再原生暂停本人未完成Goal并读回paused，核最新实际model/effort/settings继承，只创建唯一同项目local完整初始/goal接班并wait核cwd/本人完整activeGoal/实际模型；未知保留身份不重复，不让owner搬提示词、协调模型或反复授权。不创建临时Goal、不恢复旧Goal、不新增hooks/心跳/定时器/后台merge；旧暂停Goal缓存不自动恢复施工。当前本relay交接观察元数据可能未提交，保全并如实核归属；源码候选以node-commit-observation/source-head及实际Git为准。
'''
(relay/'relay-prompt.txt').write_text(msg,encoding='utf-8')
print(json.dumps(dict(relay=relay.relative_to(root).as_posix(),prompt_chars=len(msg),model=c['model'],effort=c.get('effort')),ensure_ascii=False))
