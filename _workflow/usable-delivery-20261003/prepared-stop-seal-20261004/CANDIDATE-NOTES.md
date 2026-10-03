# 同 prepared Stop 零调用清偿候选

沿用原控制包/两次 blocked 前审/opening/history/原级发现，不新增前审、不宣布闭合、不开生产。已采用交付优先、工作包合批、自动本地提交、自动交接政策。

7 个源码/测试文件：模型新增独立 localNoSendProof；PreparedSubmit 捕获原 run / epoch / wire / key / 出现 / attempt / 指纹 / 有效期 / StopAuthority 及消费ID，核验载荷指纹、消费状态和端口尚未进入。RunStore 同实例门内从耐久记录核对 Stop 和全部原身份，没有 job / acceptedSendIdentity / rawTerminal / exit / 远端拒绝的矛盾事实，才发布本地证明并将 Intent 标 Rejected。SendAttempted 保持 true，rawTerminal 仍 null，executionExitConfirmed 仍 false。普通 Update/合并不可伪造新证明，已有证明不能删除、替换或被同身份受理事实推翻。Runner 停止链不查询已清偿本地提交，恢复资格单独消费该证明。

普通受控证据：red-r2 为 1 红/5 绿，红点是旧实现仍 Unknown。green-r2 72/72，green-r3 121/121。runner-final 编译漏传 prerequisites/terminal 参数失败；runner-final-r2 15 绿/1 红，因真实严格合同缺配置修订；已补齐夹具前提，保留全部行为断言。impact-final 的真实 Runner 用例通过，验证 Cancelled / 原发送身份 / 零端口调用 / 零原键查询 / 不推进 next / 无伪造 rawTerminal / 重启保持。最终扩大/全量结果由 test-observations.json 读回，不由此处提前声称。

早期补丁锚点错误：add_red 第一次预期 CRLF，但实际文件 LF，在写前中止；因此 red/ 首次 Rebuild 未包含新反例，不能称红例验证。implement_nosend 第一次模型字段同名锚点有两处，写前中止；新增服务文件当时缺模型字段，green/ 编译失败。第二次脚本将字段放到第一处同名 outcome，随后立即仅移到 WorkflowSubmission，并还原 outcome 多余空行。当前精确差集和原行换行在 current-source-integrity.json / this-chat-only.diff；失败原件保留。

两项普通 P/F/P 已形成：durable-no-call 删除实际证明落盘，指定断言 Object≠Null；no-call-immutable 删除原证明不可替换守卫，指定 Assert.Throws 无异常；每项 subject 逐字节恢复。随后新增真实 Runner 测试及还原模型空行，早两项 subject 未变但测试/模型输入有增量，保留普通来源版本限度。第三项 runner-no-lookup 的当前记录以 causal-runner-no-lookup-observation.json 为准，不预先称完成。

剩余共享链：RunStore 不可逆终局封印 / 完整原身份、三段退出及真实效果；Host 所有节点和运行释放入口锁外消费/回写/失败重试；等待、暂停、LocalWait；Completing/Unknown 面板停止/原身份只读对账；Source 多 run / 在飞停止交错；原功能/迁移六失败/兼容/分发/实机。

新静态重要候选：NodeOutcomeIsTerminal 仅核对 raw 词与 send identity，不核验完整退出和封印；非节点 TakeoverTerminalConfirmed 仅看 State。Sweep 在下一次节点准入前需及时释放节点，不能用一枚冻结全 run 的封印阻止后继。RecordIntent 替换上一笔提交后完整退出身份可能只剩 NodeOutcomes，应在同协议设计节点资格留存与运行封印，并先红例。这个方向尚未实现/验证，不据此裁定唯一设计或闭合 CONTROL-RELEASE-ATOMICITY-1。

局限：本地证明是同 prepared 控制流及 RunStore 受控写入的证据；不证明跨进程排他、防恶意代码或外部手工文件伪造。RunStore 实例门与串行对象所有权继续是明确边界。没有 BGI 源增量、没有实际产品运行验收、没有综合独立实现后审；所有原级义务仍 open。

全量 full-final 为当前源输入普通证据：2087 Passed / 原六 Failed / 2 NotExecuted，共2095；对上一段 final-full-r2 的精确 testId：新增17、删0、旧outcome变化0、失败集合相同。其中15项为本段新增，2项是上段最后所属快照反例（此前全量未包含），不归本段新增。全量前/后261输入无漂移。扩大 impact-final 552 Passed / 2 opt-in跳过，共554；所有失败身份保留。full-final 在第三项突变之前执行，恢复后必须核对源码和程序集是否等价，不能静默把不同产物重绑为该次全量。

最终安全读回：第三项 runner-no-lookup P/F/P已终态，原/恢复/current SHA相同；261全量输入及两个程序集与full-final逐字节相同，无需重复未变源码全量。首个summary脚本将PowerShell单对象JSON当列表拼接，写前TypeError；按原返回dict/list读取后成功，没有改因果原件。
