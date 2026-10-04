from pathlib import Path
import json,hashlib,subprocess
r=Path.cwd();old=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-late-round-20261004-from-01a10658';d=old.parent/'auto-relay-external-original-causality-20261004-from-01a1067e';d.mkdir(exist_ok=False)
f=next(Path('C:/Users/Administrator/.codex/sessions/2026/10/04').glob('*01a1067e*'));contexts=[json.loads(x)['payload'] for x in f.read_text(encoding='utf-8').splitlines() if json.loads(x).get('type')=='turn_context'];p=contexts[-1];settings=p.get('collaboration_mode',{}).get('settings',{});assert p['model']=='gpt-6.1-sol' and p.get('effort')=='medium' and settings.get('model')==p['model'] and settings.get('reasoning_effort')==p['effort']
obs=dict(threadId='01a1067e-7395-7cd3-b38d-540ae7670782',rollout=str(f),ordinal=len(contexts),model=p['model'],effort=p['effort'],settings=dict(model=settings['model'],reasoning_effort=settings['reasoning_effort']))
(d/'source-model-observation.json').write_text(json.dumps(obs,indent=2),encoding='utf-8')
v=json.loads((old/'final/candidate-observation.json').read_text(encoding='utf-8'));assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in v['sources'])
marker='AUTO-EXTERNAL-ORIGINAL-CAUSALITY-RELAY-20261004-FROM-01a1067e'
handoff='''# 原ExternalStart台账因果字段与共享链认证接力

标记AUTO-EXTERNAL-ORIGINAL-CAUSALITY-RELAY-20261004-FROM-01a1067e，来源01a1067e-7395-7cd3-b38d-540ae7670782，仍原共享包、opening/请求/预算/原级义务不重置。原总交付未完成，生产门关闭。继承本人当前实际sol/medium；复杂统一独立审查仍sol/high。

本轮产品候选8597fed905875eb29f6b95fb7347f136695ddfed，最新HEAD动态核。1产品源码+1测试+当前交接以及执行证据共122文件精确读回；不是122产品源码。前relay（auto-relay-late-round-20261004-from-01a10658）CANDIDATE.md、NEXT-CAUSAL-SOURCE.md、final/candidate-observation.json与impact-comparison、原TRX/log、mutation-observations/post-mutation-byte-observation、candidate-commit-observation必须实际读。候选修复ExternalStart已裁决结清归档原终态的原身份/完整终态/拒绝快照/审计/台账确认幂等复核；不迁回已结清归档，不重开/重发，缺证据仍未确认。11场景含迟到前热区/归档×完成后热区/归档、错误job/终态/来源/时间/缺终态/未确认、普通当前轮归档及新Host零发送。原第一轮受理回执与完成观察走真实Host生产链，第二轮拒绝为受控SenderOverride，未受理为受控适配器观察；时钟归档/新Host是临时数据模拟，不代IPC游戏User验收。

有效红host-late-red2两归档重放指定断言失败，两热区通过；最初host-late-red四项unsupported新Host缺原上下文是夹具问题，不能当产品红或放宽该原合同。编译辅助名/类型/脚本换行匹配失败过程保留。两项PFP删除归档分支和台账确认均指定断言红/编译成功/finally原子恢复SHA/恢复11绿。最终Rebuild0，TaskCenter2008/18/2=2028，相邻481/0/2=483；精确合集2018/18/2=2038，对2027新增11/删除0/共有变化0/18失败ID相同、不豁免；18输入字节一致、声明面同SHA。普通TRX不是认证收据/独立pass/产品验收。

唯一下一共享依赖：TaskCenterHost.Admission.cs TakeoverLedgerScan从原ExternalStartLedgerEntry投影TakeoverLedgerFact时没有CandidateId/ResourceRef/ActionId/TargetBgiEpoch。先真实原台账与原操作的全字段因果矩阵及红反例，核不同/缺epoch/candidate/resource/action、原job/逐轮identity/seq/预观察、热区/归档/新Host/未终态/严格结清交错；现定位不等于已证明错误释放，其他层可能拒绝，必须沿真实链核查。不改Node OperationType绕专属守卫，不由当前定义重建原事实，不伪造receipt。保留d50c7e3/5c46c9a/e668f8d/27d5be3/9751966c/8597fed9候选行为。其后完整多history/outcome/严格结清中断/封印生产矩阵、原全账/真实来源认证/统一sol-high综合后审/原级成批闭环，全部原总功能实际运行/停止/重启/数据保留和可运行版本仍Goal判据。

认证/正式后审仍original-send-round/current-review-config.json与current-native-review-config.json全源域（包括BGI接纳/查询/注册表/BgiExternalClient/Directory.Build.targets/csproj）。audit2仍9f85缺receipt，旧manifest/policy=false/原planpass限制保留，不翻policy/倒签/扩通用工具/伪receipt。本来源新增独立请求0；实际读9f85的3原生失败+1CLI及G10真实blocked源，control-native d25e7c/f82d49两份plan request/report/receipt也读，历史G/control/R56全账尚未逐项核清，不自称余额或归零；新增前核全账，8次/有限追加不重置。r61报告缺失仍未知。

全部源码写入/突变恢复/Rebuild/测试/audit已终态，未push部署；保护所有User/JS/.kiro/旧D盘与材料外R56/csproj/工具/文档/暂存。仅明确本聊天源码/测试/证据本地候选commit --only。当前HEAD/工作区与后续文档提交以opening-observation/实际Git为准，不能制造干净工作区。

交接理由：合法迟到轮次与已结清归档重放形成已验证转换；下一原外部台账epoch/candidate/resource/action因果投影需独立追查实际生产来源，不泛化已通过11场景为全部G7。非时间/工具数量触发。旧Goal状态必须读old-goal-paused-readback；新聊天先核cwd/建立或核自身完整Goal读active/实际rollout模型档位，再写源码，不恢复旧Goal、不等owner继续。
'''
(d/'HANDOFF.md').write_text(handoff,encoding='utf-8')
top='''/goal 完成槲寄生原计划全部约定功能最终交付：闭合类型化唯一父来源、33节点逐次容量和原身份墓碑、四真实入口、G4/G7发送消费恢复历史归档停止重启及全部原级义务，取得认证真实来源、独立综合后审和所有约定功能新产物实际运行/停止/重启/数据保留与可运行版本；保护User和材料外成果，证据成立前生产门关闭，不伪成功。

先建本批完整Goal（结果＋约束＋完成判据），再开工。最新标记AUTO-EXTERNAL-ORIGINAL-CAUSALITY-RELAY-20261004-FROM-01a1067e，来源01a1067e-7395-7cd3-b38d-540ae7670782；唯一同项目local，cwd E:/Program Files/better-genshin-impact-LCB；本relay=_workflow/local-wait-admission-gates-20261004/auto-relay-external-original-causality-20261004-from-01a1067e。仍原共享包，不重开opening/历史/预算/请求/原级义务，不恢复旧Goal。最新握手/唯一下一项只以上本段及本HANDOFF、CURRENT-HANDOFF最新候选为准，下方全部旧握手仅历史不执行。
按handoff-inherit-model-20261004-v1继承实际gpt-6.1-sol/medium，本relay/source-model-observation为实际rollout证据；复杂统一综合独立审查sol/high不降强度。先shell核cwd，get_goal本人，无Goal仅建一次完整摘要（objective≤4000字符，绑定本relay-prompt全文/HANDOFF/CURRENT-HANDOFF/继承原范围），读回active；已有核完整范围不重建。只读核本人当前rollout最新turn_context.model/effort与collaboration_mode.settings一致且sol/medium及threadId，cwd/完整activeGoal/实际模型成立前禁改代码。保存get_goal完整返回本relay/new-goal-readback.json；保存actualCwd/threadId/marker/范围绑定/model/effort/settings/ordinal/rollout为new-handshake-observation.json，立即commentary握手。实际读本relay/old-goal-paused-readback.json，不凭文字认定来源paused；成立后自主继续，不等owner继续。
全文读本HANDOFF、CURRENT-HANDOFF最新段、前relay（auto-relay-late-round-20261004-from-01a10658）CANDIDATE.md、NEXT-CAUSAL-SOURCE.md、final/candidate-observation.json/impact-comparison与全部原TRX/log、mutation-observations/post-mutation-byte-observation、candidate-commit-observation及继承原材料。产品候选8597fed905875eb29f6b95fb7347f136695ddfed，分支main-OldTeaBag-B168，HEAD动态核；保护材料外、不制造干净工作区，源码/恢复/构建/测试/audit均终态。
本候选原ExternalStart已裁决结清归档原终态按原完整identity/seq/job/终态/来源/时间/拒绝/审计/真实台账确认幂等复核，不迁回已结清归档、不重开/重发；11场景/2项PFP恢复SHA、Rebuild0、TaskCenter2008/18/2，相邻481/0/2，精确合集2018/18/2=2038，对2027新增11/删0/共有变化0/18失败ID同、不豁免，18输入同字节/声明面同SHA。有效红host-late-red2两归档断言失败；首轮unsupported形状是新Host缺原上下文夹具问题，不放宽原合同。原第一轮迟到回执/完成是Host真实生产路径，第二轮拒绝受控SenderOverride，其他未受理/归档/新Host是临时模拟；普通TRX不代认证/独立pass/IPC游戏User验收。原全部失败过程保持。
唯一下一共享依赖：实际Host TakeoverLedgerScan未投影原ExternalStartLedgerEntry的CandidateId/ResourceRef/ActionId/TargetBgiEpoch，先完整原台账/原操作全字段因果矩阵与红反例再集中定向修复，核不同/缺epoch/candidate/resource/action/原job/每轮identity/seq/预观察、热区/归档/新Host/未完成/严格结清交错。现定位不是已证误释放或已排除；沿实际链核，不能修改Node OperationType绕专属守卫、不能由当前定义重建原事实。保留原全部候选d50c7e3/5c46c9a/e668f8d/27d5be3/9751966c/8597fed9。继而完整多history/outcome/严格结清中断/封印生产矩阵、全账、真实认证、统一sol-high综合后审/原级成批闭环。原payload/job/epoch/key/run/node/occ/loop/attempt/task/config/version/operation/nonce逐项因果，禁止Load/组装/capability=true代证据。
本来源新增独立请求0；9f85的3原生路由失败+1CLI不支持+G10独立blocked源实际读，control-native d25e7c/f82d49两份plan源也读，尚未逐项核清全部G/control/R56账，新增前核账、8次/有限追加不重置、不自称余额归零。audit2仍原receipt缺失；旧manifest缺列表/policy=false/原planpass机械限制保留，不翻policy/倒签/伪receipt/扩通用工具。修复现有材料配置或等价可核查独立来源，正式认证及综合后审沿original-send-round/current-review-config.json和current-native-review-config.json全部源域。
采用现行AGENTS、delivery-first、完整工作包合批审查、自动本地提交/交接模型继承；读bgi-project-development、mistletoe-independent-review与references/work-package-review.md、Docs/design/mistletoe-review-process.md、mistletoe-workflow-facilities.md、automatic-local-commit-policy-20261004.md及tools/mistletoe/README.md，核bundle36169fbf7baed38338f2ac7a1cb2681ba4a236ae81e9a282025c7efc63c5bc8c；读并行索引md/json跑deliveries.py只读发现，r61报告缺失未知，不当已集成不让owner搬运。反例/源码/矩阵→集中修复→串行Rebuild DeployToBgiTools=false/影响回归/关键PFP finally恢复SHA/声明面→完整源域认证/独立综合后审/原级闭环；不第三轮纯前审、不重置旧账、不削质量门。
保护全部User/配置宏脚本截图、第三方JS只读/.kiro/旧D盘与材料外R56/csproj/工具/文档/暂存；不清盘/杀用户程序/整树复制/reset。复用g10 products，源码/构建/测试串行，自动git commit --only明确本聊天范围本地候选，不push部署。总Goal原全部约定功能新产物实际运行/停止/重启/数据保留、可运行版本、认证独立综合后审与原级闭合才complete。以下原完整范围全文仅历史覆盖，不执行旧握手、不缩范围：

'''
(d/'relay-prompt.txt').write_text(top+(old/'relay-prompt.txt').read_text(encoding='utf-8-sig'),encoding='utf-8')
status=subprocess.check_output(['git','status','--porcelain'],text=True,encoding='utf-8');(d/'opening-status.txt').write_text(status,encoding='utf-8')
(d/'opening-observation.json').write_text(json.dumps(dict(branch=subprocess.check_output(['git','branch','--show-current'],text=True).strip(),head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),status=status,candidate='8597fed905875eb29f6b95fb7347f136695ddfed',goal_complete=False,production_gate_open=False),ensure_ascii=False,indent=2),encoding='utf-8')
print(str(d),len((d/'relay-prompt.txt').read_text(encoding='utf-8')))
