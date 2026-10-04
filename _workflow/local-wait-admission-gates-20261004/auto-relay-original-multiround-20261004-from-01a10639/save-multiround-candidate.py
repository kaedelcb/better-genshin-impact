from pathlib import Path
import hashlib,json,xml.etree.ElementTree as ET,subprocess
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639';f=d/'final-r2'
obs=json.loads((f/'candidate-observation.json').read_text(encoding='utf-8'));comp=json.loads((f/'impact-comparison.json').read_text(encoding='utf-8'));mut=json.loads((d/'mutation-observations.json').read_text(encoding='utf-8'))
assert len(comp['added'])==16 and not comp['removed'] and not comp['changed'] and comp['same_failed_ids']
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
bindings=[]
for m in mut:
 actual=sha(r/m['source']);assert actual==m['original_sha256']==m['restored_sha256'];assert (m['baseline_exit'],m['mutant_exit'],m['restored_exit'])==(0,1,0)
 source=next(x for x in obs['sources'] if x['path'].replace('\\','/')==m['source'].replace('\\','/'));assert source['sha256']==actual
 bindings.append(dict(id=m['id'],source=m['source'],sha256=actual,failed_test_ids=[x['testId'] for x in m['failed']]))
(d/'post-mutation-byte-observation.json').write_text(json.dumps(dict(kind='ordinary SHA/PFP readback; not certification receipt',bindings=bindings,final_sources_match=True),indent=2),encoding='utf-8')
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};interrupted=ET.parse(d/'prior-faults-green2.trx');timing=interrupted.find('t:Times',ns)
raw=[dict(name=x.get('testName'),outcome=x.get('outcome'),duration=x.get('duration')) for x in interrupted.findall('.//t:UnitTestResult',ns)]
(d/'interrupted-run-clarification.json').write_text(json.dumps(dict(kind='readback of original interrupted TRX; partial results only',times=timing.attrib,completed_rows=raw,conclusion='Two raw corruption cases completed Passed at about 51s each. The helper polls for a valid lease even though the injected corrupted lease is intentionally invalid. No product deadlock is established. Full 14-case run was terminated and remains aborted; uncompleted rows are not Passed.'),ensure_ascii=False,indent=2),encoding='utf-8')
text='''# 原Host显式多轮发送候选（原共享包）

沿原opening、全部请求/预算及G2(e)/G4/G4a/G7/G8/G10/⑤⑥原级important implementation open。生产门关闭；本文件不授予认证、独立综合后审或实机验收。

修复两条实际Host链：同门面实例显式RetryAsync沿用首次可信适配器冻结的节点请求上下文和原调用令牌，缓存绑定原LeaseId/OwnerEpoch、完整候选身份/载荷、run/key/游标；原缓存不能被后续请求替换，不序列化，也不从当前流程定义重建。新实例无此上下文仍响亮拒绝。没有增加自动重试、UI入口或改变一次普通提交合同。

首轮拒绝返回给Runner前，另一显式重试可能已受理。Host现在只在同原请求、原父来源、完整前轮nonce/证明/拒绝/预观察、当前许可、真实受理回执及耐久接管关闭共同成立时采用该轮结果；缺失/冲突/关闭失败返回Unknown并保留事实，不能用初轮旧拒绝覆盖第三轮受理。执行端口受控，观察屏障安排显式重试交错；不是实机IPC/User或新增公开重试流程。

16场景涵盖两种原父来源各自合法1→2→3、原nonce/证明/载荷破坏、拒绝历史删除/重复写入被不可变存储拒绝、第三轮可能受理异常、真实受理后接管关闭异常。两种异常经旧Host Shutdown后新Host/new Runner/new stores按原第三轮恢复/真实退出观察/Stop和重复Stop收敛；原前两轮许可及证明逐字相同，原payload/expires保持、发送总数仍3。拒绝历史场景证明普通写者不能删除/重复原记录，不冒充损坏文件Host重试。

最初两个真实红例证明Retry丢原上下文；上下文修复后两个Succeeded→Unknown红例证明旧结果返回交错。编译限定名/插入位置/构造参数错误、端口配置每次重置故障及Error无定位seq的夹具修正均保留，不当产品语义红。`prior-faults-red.trx`中6个invalid_mutation_state属于故障注入方式被存储守卫拒绝，不当产品红。改原始损坏文件的14场景整体中止，原TRX明确其中2项已Passed各约51s，慢在夹具等待有效台账；这不证明产品死锁。其他未完成项未计Passed，记录见interrupted-run-clarification.json。合法迟到受理的完整生产来源链仍须追查，不能把非法Node accepted_receipt形状的Corrupt拒绝当该链已验。

最终有效证据只用final-r2。Rebuild exit0；TaskCenter {full}，exit1；相邻 {target}，exit0；精确合集 {union}，相对前序新增16/删除0/共有变化0，18失败testId集合一致，不豁免。17输入前后字节一致；声明面再生前后同SHA {claim} 且清除变量重跑通过。三个关键P/F/P（原上下文、当前原轮读回、前轮nonce绑定）指定断言红、编译成功、逐项恢复SHA绑定final-r2输入。第一次final包装240s超时、全部原进程随后终态，不算有效回归；原全量TRX证明同条件约4m33s，因此final-r2改600s并完成，原过程未编辑。

机械audit仍exit2缺原native request 9f85a4b85f46400dbff20b97ebec4fda receipt。未改旧manifest/policy/工具/冻结请求，未伪receipt。当前来源新增独立请求0。已读本包4次渠道失败及G10独立blocked源，control-native两份方案请求也已定位；这些并不自动证明所有继承G/control账核清，新增前继续核原账，不宣称剩余额度。

下一共享依赖：合法旧轮迟到受理与已归档原身份责任、全部history/outcome/严格结清中断/封印矩阵；沿实际ExternalStart逐轮台账和Host/BGI生产来源核查，不能篡改Node OperationType去绕过ExternalStart专属守卫。保存现有多轮候选，补齐全账及完整源域认证/统一sol high综合后审并成批原级闭环。全部原约定功能新产物实际运行/停止/重启/数据保留和可运行版本仍欠，不缩总Goal。

保护User/第三方JS/.kiro/旧D盘、材料外R56/csproj/工具/文档/暂存。构建测试与突变已终态；只保存明确本地候选，不push/部署。所有过程原件及source-before、inherited-evidence-read、final-r2原TRX/log、impact-comparison、mutation-observations/post-mutation-byte-observation为权威。
'''.format(full=obs['full_counts'],target=obs['target_counts'],union=comp['counts'],claim=obs['claim_after'])
(d/'MULTIROUND-CANDIDATE.md').write_text(text,encoding='utf-8')
p=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=p.read_bytes();s=b.decode('utf-8');addition='\r\n\r\n## 2026-10-04 原Host显式多轮发送候选（当前优先）\r\n\r\n来源01a10658-486e-7bd0-ba8c-2371bc33a770，沿原共享包。最新完整状态读auto-relay-original-multiround-20261004-from-01a10639/MULTIROUND-CANDIDATE.md及final-r2原执行证据、mutation-observations.json和post-mutation-byte-observation.json；候选commit以candidate-commit-observation.json读回为准，不据旧HEAD推断。实际Goal active及rollout sol/medium握手已保存。\r\n\r\n同实例显式Retry保留首次可信冻结上下文并绑定原所有者；Host旧返回值与新轮受理交错按完整原轮/前轮nonce和耐久接管读回纠正。无自动重试或新增UI入口。16场景通过，三项PFP/源码恢复SHA，最终Rebuild0、TaskCenter1997/18/2、相邻441/0/2、精确合集2007/18/2=2027，对2011新增16/删除0/共有变化0，18失败身份一致、不豁免；17输入不变，声明面同SHA。普通TRX/受控端口/新Host不是认证/独立pass/IPC游戏User验收。\r\n\r\n所有编译/夹具过程、第一次240s全量超时、原始损坏租约整体中止保留。原中止TRX已有两个Passed各约51s，慢因夹具等待有效租约，不能称产品死锁；未完成项不算通过。非法Node迟到accepted_receipt只能证明Corrupt输入保护，不是合法迟到受理完整链。下一共享依赖为合法旧轮迟到受理/归档原身份责任及全部history/outcome/结清中断/封印生产矩阵，继而全账、真实认证和统一sol high综合后审/原级成批闭环。G2(e)/G4/G4a/G7/G8/G10/⑤⑥仍原级important implementation open；总功能实机交付仍未完成、生产门关闭。audit2仍缺原receipt，新增请求0，不翻policy/扩工具/伪receipt/重置预算。保护材料外；提交与必要语义交接按现行原生规则。\r\n'
assert '原Host显式多轮发送候选（当前优先）' not in s;p.write_bytes(b+addition.encode('utf-8'));(d/'current-handoff-edit.json').write_text(json.dumps(dict(before_bytes=len(b),after_bytes=p.stat().st_size,before_sha=hashlib.sha256(b).hexdigest(),after_sha=sha(p)),indent=2),encoding='utf-8')
print('candidate recorded, total current rows',comp['current_count'])
