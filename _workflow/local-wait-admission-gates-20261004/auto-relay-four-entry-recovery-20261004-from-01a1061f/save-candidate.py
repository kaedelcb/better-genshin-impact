from pathlib import Path
import json,hashlib,subprocess,xml.etree.ElementTree as ET
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f';enc='utf-8'
obs=json.loads((d/'final/candidate-observation.json').read_text(encoding=enc));mut=json.loads((d/'mutation-observations-r2.json').read_text(encoding=enc));facts=[]
for item in obs['sources']:
 p=r/item['path'];b=p.read_bytes();sha=hashlib.sha256(b).hexdigest();assert sha==item['sha256'];facts.append(dict(path=item['path'],bytes=len(b),lines=len(b.splitlines()),sha256=sha))
for m in mut:assert m['restored_sha256']==next(x['sha256'] for x in facts if x['path'].replace('/','\\')==m['source'])
(d/'post-mutation-byte-observation.json').write_text(json.dumps(dict(kind='current final input matches restored mutation subjects',sources=facts,mutations=[dict(id=m['id'],source=m['source'],restored=m['restored_sha256']) for m in mut]),indent=2),encoding=enc)
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};tests=[dict(id=x.get('testId'),name=x.get('testName'),outcome=x.get('outcome')) for x in ET.parse(d/'final/capacity.trx').findall('.//t:UnitTestResult',ns) if 'OriginalLifecycle_' in x.get('testName','')];assert len(tests)==10 and all(x['outcome']=='Passed' for x in tests)
(d/'lifecycle-matrix-observation.json').write_text(json.dumps(dict(kind='continuation state/concurrency/fault matrix; original manifest/opening preserved',tests=tests,rows=[dict(id='lifecycle-original-source',dimension='state',tests=[x['id'] for x in tests if 'RealPaused' in x['name']],mutation='M1-recovery-gate'),dict(id='lifecycle-unclosed-recovery-overlap',dimension='concurrency',tests=[x['id'] for x in tests if 'RealPaused' in x['name']],mutation='M1-recovery-gate'),dict(id='lifecycle-source-fault',dimension='fault',tests=[x['id'] for x in tests if 'SourceFault' in x['name']],mutation='M2-resume-source'),dict(id='lifecycle-pending-original',dimension='state',tests=[x['id'] for x in tests if 'Saturated' in x['name']],mutation='M3-pending-identity')],production_gate_open=False,original_obligations='important/implementation/open retained',independent_pass=False),indent=2),encoding=enc)
cp=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=cp.read_bytes();nl='\r\n' if b.count(b'\r\n') else '\n';(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')),indent=2),encoding=enc)
prefix='''## 2026-10-04 真实Paused／新Host Interrupted恢复与待迁移原身份候选（当前优先）

当前施工聊天01a10639-2e7a-7392-9c6c-749342ecb428，本完整Goal真实active，实际sol/medium；来源已paused/idle。候选提交身份读auto-relay-four-entry-recovery-20261004-from-01a1061f/candidate-commit-observation.json。仍原共享包/opening/预算及全部原级important implementation open，总交付未完成、生产门关闭。

真实Paused续行反例发现恢复Sender启动Runner后过早释放门面串行边界，使下一节点submission_conflict拒绝；AdmitRecoveryAsync现维持_gate至接管/关闭完成，文件事务锁不跨Sender await。新增10场景：面板/移交原父来源×真实Paused续行/新Host Interrupted显式恢复及来源失效拒绝；强制恢复Accepted后台账前屏障证明Runner已推进但无下一节点登记/发送；32实际原发送节点在临时模拟32 TerminalPendingTransfer+256未到期墓碑满区拒绝新登记，新Host初始化恢复后完整原Operation序列化不变。Interrupted崩溃状态模拟，恢复扫描真实；Pausing跨新Host按原R4合同先成Interrupted。不是子进程重启/IPC游戏User验收。

最终Rebuild0；TaskCenter1981/18/2=2001，exit1；相邻425/0/2=427，exit0；精确合集1991/18/2=2011，相对前序2001新增10/删除0/共有变化0/18失败身份一致，不豁免。声明面同SHA，13输入前后一致。mutations-r2三项P/F/P指定断言/SHA恢复：恢复过早释放、来源检查绕过、待迁移原键破坏。初轮M2屏障悬挂自有testhost精确终止并finally恢复，不计有效突变；扫描前手工篡改移交被不可变守卫拒绝，不冒充扫描后Resume源故障绿证据。原过程均保留。完整读LIFECYCLE-CANDIDATE.md、final读回/impact-comparison及原TRX/log、mutation-observations-r2、post-mutation-byte-observation，不只本文。

唯一下一共享转换：实际宿主合法多sendSeq重试、所有前轮原nonce/未发送或拒绝证据、旧轮迟到受理冲突、关联发布后中断/严格结清失败/多history/outcome/封印与Stop/重开原责任，稳定共享链后统一认证和sol/high独立综合后审。已有OriginalHost_RunnerRecoveryUsesOriginalRoundAndStrictFacadeClosure在本回归通过，但非所有G7链闭合。新增独立请求0；原可定位5次不是全部预算已核清。audit exit2仍原native请求缺receipt，旧manifest/policy/planpass机械约束保留，不翻policy/倒签/扩工具/伪receipt。r61报告缺失未知，不当已消费。

全功能新产物实际运行/停止/重启/数据保留及可运行版本仍为总Goal判据；材料外/User/JS/.kiro/旧D盘保护，无push/发布/部署。下方此前当前段仅作历史，新的握手/下一项以本段及后续relay为准。

'''
cp.write_bytes(prefix.replace('\n',nl).encode(enc)+b);assert cp.stat().st_size>len(b)
print('byte and exact test/mutation records saved',flush=True)
