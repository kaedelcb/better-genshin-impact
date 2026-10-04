from pathlib import Path
import json,hashlib,subprocess,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;f=d/'final';o=json.loads((f/'candidate-observation.json').read_text(encoding='utf-8'));c=json.loads((f/'impact-comparison.json').read_text(encoding='utf-8'));m=json.loads((d/'mutation-observations.json').read_text(encoding='utf-8'))
assert o['build_exit']==0 and o['target_exit']==0 and o['sources_unchanged'] and o['claim_before']==o['claim_after'];assert not c['removed'] and not c['changed'] and c['same_failed_ids'] and len(c['added'])==8;assert len(m)==3 and all(x['baseline_exit']==0 and x['mutant_exit']==1 and x['restored_exit']==0 and x['original_sha256']==x['restored_sha256'] for x in m)
post=[dict(path=x['path'],expected=x['sha256'],actual=hashlib.sha256((r/x['path']).read_bytes()).hexdigest()) for x in o['sources']];assert all(x['expected']==x['actual'] for x in post);(d/'post-mutation-byte-observation.json').write_text(json.dumps(post,indent=2),encoding='utf-8')
text=f'''# 原ExternalStart台账因果候选

当前聊天01a10699-4d55-7cd0-9a8f-97660078ce7f，来源01a1067e，仍原共享包/opening/历史/预算/原级责任。本Goal active与实际rollout sol/medium握手已保存，来源原生paused读回。总产品交付未完成、生产门关闭；本来源新增独立请求0。

红例causal-red.trx：四热区/归档组合均在late-receipt/candidateId错误非空断言失败；恢复把错候选原台账当旧轮受理并写冲突责任（归档可迁回）。这是已证错误事实消费，不泛化误释放。最初修改脚本在写前因两处同needle校验失败，源码未写；原过程保留，不当产品红。

修复两产品源码：Host从ExternalStartLedgerEntry原件投影CandidateId/ResourceRef/ActionId/TargetBgiEpoch；迟到Sender直建事实同样投影；门面acceptedReceipt在状态处理之前同原冻结操作逐字段比对，历史受理/终态/裁决和归档迁回同mutation再核，归档已结清幂等分支同原字段核。扫描同identity/seq的重复因果字段冲突整组拒绝。原job/逐轮identity/seq/来源/时间/终态/拒绝/审计/确认保持，不从定义重建、不改OperationType绕路。nullable参数仅加法；非acceptedReceipt通用组件合同保留。三处既有直接台账夹具补真实四字段投影，旧核心断言未改。

19定向场景=原11+本8。迟到受理前热区/归档×终态后热区/归档×late-receipt/late-terminal/settled-replay/new-host×四字段错非空/缺失，actual临时原台账改写、finally恢复；128矩阵文件实读。每阶段同身份组四字段冲突、两种顺序均整组拒绝；当前轮未完成/已完成×原Host/新Host也核四字段，错误事实时handoff逐字不变、零重发，合法回读延续。原第一轮走真实Host发送/完成观察和台账，第二轮受控SenderOverride拒绝/适配器未受理；归档时钟/新Host及临时台账篡改是模拟，非真实IPC/游戏/User或子进程重启验收。

三项普通P/F/P：删除Host投影、跳过原操作比对、删除组冲突比对。均Rebuild0、指定断言红、finally原子恢复SHA、恢复19绿，记录原失败testId/栈/patch。M1指定断言是合法原轮受理的HistoricalAcceptanceReceiptsHeld==1（投影丢失返回冲突）；M2为original ledger causality late-receipt/candidateId，M3为original ledger duplicate causality。源恢复SHA绑定final18输入。

最终Rebuild0；TaskCenter{o['full_counts']}，exit{o['full_exit']}；相邻{o['target_counts']}，exit{o['target_exit']}；精确合集{c['counts']}共{c['current_count']}，对{c['baseline_count']}新增8/删除0/共有变化0，18失败testId集合相同，不豁免。18源字节保持，声明面同SHA {o['claim_after']}。原TRX/log实际解析存inherited-evidence-read，普通过程不代认证/独立pass/实机验收。mechanical-review exit2仍9f85原receipt缺失，旧manifest缺列表/policy=false/原planpass约束保留，不翻policy/倒签/伪receipt/扩通用工具。

下一共享依赖：完整多history/outcome、严格原轮结清中断、已封印/归档Stop幂等与活跃原任务到退出矩阵。现有OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure只有history-exited/settle/publish/multiple/archive/archive-conflict有限模拟；history-multiple两节点只把最后未知主体转历史，不能单凭名称算所有历史/结果组合覆盖。当前真实Host停止链与RunStoreTerminalRelease校验已读，须先完整因果矩阵及红例再定向修复，不用组装/Load/能力true替生产事实。核原payload/job/epoch/key/run/node/occ/loop/attempt/task/config/version/operation/nonce、各history/outcome唯一index/hash、缺失/重复/冲突、严格结清失败和恢复/封印幂等、原数据保留；后续原全账/认证真实来源/统一sol-high综合后审/全部原级闭环与所有约定功能新产物实际运行/停止/重启/数据保留及可运行版本仍欠。

原G2(e)/G4/G4a/G7/G8/G10/⑤⑥important implementation open。9f85四失败+G10独立blocked原源和control-native d25e7c/f82d49两plan receipt原源已读；G/control/R56完整历史预算未核清，不能自称余额/归零，新增前核账，8次/有限追加保持。认证及完整后审入口仍original-send-round/current-review-config.json/current-native-review-config.json全源域。bundle36169fbf7baed38338f2ac7a1cb2681ba4a236ae81e9a282025c7efc63c5bc8c核通过；deliveries发现r61报告缺失未知，未集成，不阻安全修复。

全部User/JS/.kiro/旧D盘/材料外R56/csproj/工具/文档/暂存保护，无push部署。所有写入/恢复/构建/测试/audit须终态后明确范围本地候选提交；本文件与候选commit观察作为当前证据，不改旧receipt或opening。
'''
(d/'CANDIDATE.md').write_text(text,encoding='utf-8')
p=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=p.read_bytes();(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),crlf=b.count(b'\r\n'),lf=b.count(b'\n')),indent=2),encoding='utf-8');add=f'''\n\n## 2026-10-04 原ExternalStart台账四字段因果候选（当前优先）\n\n来源01a10699-4d55-7cd0-9a8f-97660078ce7f，仍原共享包。完整状态读auto-relay-external-original-causality-20261004-from-01a1067e/CANDIDATE.md、final原TRX/log/128矩阵文件/精确差集、mutation-observations/post-mutation-byte-observation与candidate-commit-observation。原台账四字段错非空红例证明错误事实消费；已加Host/Sender原件投影、原操作复核与同identity/seq组冲突拒绝，未用当前定义补造。19场景（新增8）/三项PFP恢复SHA；Rebuild0、TaskCenter{o['full_counts']}，相邻{o['target_counts']}，精确合集{c['counts']}共{c['current_count']}，比{c['baseline_count']}新增8/删0/变化0，18失败身份同，不豁免；18输入/声明面同SHA。普通TRX/受控端口/新Host实例非认证/独立pass/IPC游戏User验收。\n\n下一共享依赖为完整多history/outcome/严格结清中断/封印与归档停止幂等/active→退出生产矩阵，随后原全账/认证及sol-high统一综合后审/原级闭环和全部约定功能新产物实际运行/停止/重启/数据保留/可运行版本。当前audit2仍原receipt缺失，不翻policy/倒签/扩工具/伪receipt；新增独立请求0，完整预算仍待逐项核，r61报告缺失未知。全部原级义务open、生产门关闭、总Goal未完成，保护全部材料外与User，明确范围本地候选提交不代交付。\n'''
if b.count(b'\r\n')>len(b.splitlines())//2:add=add.replace('\n','\r\n')
assert '来源01a10699-4d55'.encode() not in b;p.write_bytes(b+add.encode());print(o['full_counts'],o['target_counts'],c['counts'],flush=True)
