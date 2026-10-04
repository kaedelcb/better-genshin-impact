from pathlib import Path
import json,hashlib,subprocess,xml.etree.ElementTree as ET
r=Path.cwd();d=Path(__file__).parent;o=json.loads((d/'final/candidate-observation.json').read_text(encoding='utf-8'));c=json.loads((d/'final/impact-comparison.json').read_text(encoding='utf-8'));m=json.loads((d/'mutation-observations.json').read_text(encoding='utf-8'));ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
assert not c['removed'] and not c['changed'] and c['same_failed_ids'] and len(c['added'])==11
assert all(hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'] for x in o['sources'])
assert all(x['baseline_exit']==0 and x['mutant_build_exit']==0 and x['mutant_exit']==1 and x['restored_exit']==0 and x['restored_sha256']==x['original_sha256']==hashlib.sha256((r/x['source']).read_bytes()).hexdigest() for x in m)
(d/'post-mutation-byte-observation.json').write_text(json.dumps(dict(kind='ordinary byte/PFP readback; not certification',bindings=[dict(id=x['id'],source=x['source'],sha256=x['restored_sha256'],failed_test_ids=[t['testId'] for t in x['failed']]) for x in m],final_sources_match=True),indent=2),encoding='utf-8')
text=f'''# 合法迟到原轮与归档重放候选

本聊天01a1067e-7395-7cd3-b38d-540ae7670782，仍原共享包/历史/预算/原级重要实现open，总Goal未完成、生产门关闭。实际Goal active及rollout sol/medium已读回，来源Goal paused。

修复真实 ExternalStart 恢复扫描：已裁决并结清后归档的原轮终态只核验原执行结果、原identity/seq/job/终态类别/原词/错误码/来源/时间、原第二轮拒绝、原裁决审计与实际台账终态确认，再幂等接受；归档原件不迁回热区、不重开冲突、不补造旧认领。普通当前轮已结清归档同样核原完整结果及原受理结清事实，缺失/不一致保持未确认。

11场景覆盖迟到回执前热区/归档×结清后热区/归档、新Host重读/零新增发送，错job/终态/来源/时间、缺终态/台账未确认保持原件，以及普通当前轮归档重放。原第一轮发送/迟到Accepted/完成观察走实际Host生产分派与ExternalStartLedger；第二轮拒绝用受控SenderOverride，第一轮未受理是受控权威适配器观察，时钟归档及新Host实例是临时数据模拟。不能代全部ExternalStart原适配器多轮、真实BGI IPC/游戏/User/真实子进程重启。

有效红例host-late-red2：两个归档终态重放在指定断言失败，两个热区通过。host-late-red初轮四个unsupported_dispatch_shape_b2a来自新Host缺原进程上下文，不当产品修复红、不改变该保守合同。插入脚本换行匹配、类型名及源码比较辅助名编译错误原过程保留；测试最终恢复原LF/无BOM，产品CRLF/无BOM。6个故障场景使用扫描事实/确认钩子改变外端观察，真实台账与归档原件保持；不伪造合法节点回执。

两个关键P/F/P：去归档幂等分支重新出现合法归档重放失败；去原台账确认使unconfirmed指定断言失败。均Rebuild成功、指定失败、finally原子恢复、恢复Rebuild及11场景通过，SHA绑定final输入。非认证收据。

最终Rebuild0，TaskCenter{o['full_counts']}，exit1；相邻{o['target_counts']}，exit0；精确合集{c['counts']}共{c['current_count']}，对2027新增11/删除0/共有结果变化0/18失败testId集合一致，不豁免。18输入前后同字节，声明面再生前后同SHA {o['claim_after']}，无变量相邻复跑通过。全部原TRX/log/失败/突变保留。

机械audit exit2仍原9f85请求缺receipt；不改旧manifest/policy/冻结请求/通用工具、不伪receipt。正式认证/综合sol-high后审仍欠，新增请求0。原5笔可定位来源及control-native两份plan源已实际读取，历史G/control/R56全账仍需逐项核对，不宣称余量归零。r61报告缺失仍未知、不当已消费。

下一共享依赖定位见NEXT-CAUSAL-SOURCE.md：ExternalStart台账恢复投影缺TargetBgiEpoch/CandidateId/ResourceRef/ActionId的原操作因果复核需实际反例，不自称已误释放或已排除；其后全部多history/outcome/严格结清中断/封印矩阵、完整真实来源认证/独立综合后审/原级闭环与原全部约定功能新产物实际运行/停止/重启/数据保留和可运行版本。不能以本11场景缩总Goal或关闭G责任。

保护User/第三方JS/.kiro/旧D盘/材料外R56/csproj/工具/文档/暂存，无push部署。当前源码、突变恢复、Rebuild/测试/audit均终态；本地提交仅候选。
'''
(d/'CANDIDATE.md').write_text(text,encoding='utf-8')
p=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=p.read_bytes();(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')),indent=2),encoding='utf-8')
addition='''

## 2026-10-04 原ExternalStart迟到原轮与归档重放候选（当前优先）

来源01a1067e-7395-7cd3-b38d-540ae7670782，仍原共享包。完整状态见auto-relay-late-round-20261004-from-01a10658/CANDIDATE.md及final原TRX/log/精确差集、mutation-observations及post-mutation-byte-observation，提交身份读candidate-commit-observation。合法原历史受理终态归档重放按原完整终态/台账确认/拒绝/裁决审计幂等复核，原档不迁回、不重开/重发。11场景、2项PFP恢复SHA；Rebuild0、TaskCenter2008/18/2、精确合集2018/18/2=2038，对2027新增11/删除0/变化0，18失败身份相同，不豁免；18输入同字节，声明面同SHA。普通TRX/受控第二轮Sender/新Host实例不是认证、独立pass、IPC游戏User验收。

初轮unsupported形状是新Host缺原进程上下文夹具问题，不当修复红、不放宽原合同；有效红为后轮结清归档重放指定断言。原编译/过程失败均保留。audit2仍缺原receipt，不翻policy/倒签/扩工具/伪receipt，新增独立请求0。下一共享依赖是NEXT-CAUSAL-SOURCE.md定位的原ExternalStartLedger epoch/candidate/resource/action因果投影与全部history/outcome/严格结清中断/封印/原全账/认证/统一sol-high综合后审。原G2(e)/G4/G4a/G7/G8/G10/⑤⑥保持important implementation open；总功能实机与可运行版本交付仍未完成、生产门关闭。保护全部材料外与User，候选提交不替交付。
'''
if b.count(b'\r\n')>len(b.splitlines())//2:addition=addition.replace('\n','\r\n')
assert '来源01a1067e-7395'.encode('utf-8') not in b;p.write_bytes(b+addition.encode('utf-8'))
print(o['full_counts'],o['target_counts'],c['counts'],len(c['added']))
