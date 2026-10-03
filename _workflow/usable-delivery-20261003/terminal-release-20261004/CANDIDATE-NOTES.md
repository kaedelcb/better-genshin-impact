# 节点与运行 terminal-release 候选

总产品目标未完成，原控制包未收口。沿 usable-delivery-control-recovery-20261003、原 opening/history/请求身份、两次 blocked 前审及全部原级义务。当前为 owner 交付优先政策下的定向源码候选与普通受控证据，没有综合独立实现后审、当前认证 receipt、实机或生产许可。

## 当前源码

RunStore 在同实例门内从最新耐久记录签发节点/运行两种不可逆封印。节点封印留存完整原提交、原 wire/epoch/key/出现/attempt/发送身份、退出方式/结果及该出现前置；旧提交完整进入 SubmissionHistory。早节点封印不冻结整个 run，后继仍能准入。运行封印固定整个事实投影，普通更新/合并不能造、删、换封印或新增、回退、改写事实；诊断 Note 与正常修订时间可更新。

主体观察落盘并传递真实退出方式/结果；准备和原意图保存原 wire，旧提交归档后仍保护根 wire。效果未知、原身份缺失/冲突、未知收尾历史不允许封印。前置/收尾类型化建 job 前拒绝严格消费原 operation/epoch/key/fingerprint/wire、server_rejected_before_acceptance 与 not_executed，不制造 job 或改成成功。合法未执行拒绝可留在 pending 或不可变历史中清偿。

Host 节点扫描与运行释放先取得对应封印，再在 RunStore 写门外调用仲裁。hook 只读运行证据，不反向获取 RunStore 写门；仲裁回写保存 seal ID，重启补终局及合并镜像沿同证据，最终读回核对该关联。封印发布失败保留 Accepted，仲裁失败凭同封印重试。

历史 LocalWait 的 waitLocally 不删除：后来的同出现完成/跳过证据或明确零发送停止可清偿旧等待。停止已有已退出提交而没有普通节点结果时可按原提交封印；旧 unknown 结果词和实际 succeeded 等原词分别保留，缺对应提交的 unknown 不释放。

## 普通证据与失败保留

初始四红为缺退出/缺 epoch 错放行、效果未知历史错清偿、旧提交不完整留存。扩大首次 21 失败，经封印后受控 rebase、归档/LocalWait 修复降至 11/1，再 408 通过/原 opt-in 2 跳过。旧完成记录不允许改回可恢复态：两个 scope/epoch 恢复夹具改用真实暂停点，保留原继承/拒绝及不重绑断言。只回 job 的测试端口补完整模拟冻结事实；Wait/Rejected 不伪造发送。

第一全量因已知夹具失败结束了本次专属测试进程树，过程身份/原因在 full-final/aborted-observation.json；部分 TRX 1790 行不当完整回归。Stop-Process 的一个子进程先结束导致 not-found，原结果保留，不是终止用户程序。full-final-r2 为 2111 Passed/原六 Failed/2 NotExecuted 共2119，之后 wire、等待清偿、类型化效果和停止节点还有源码/测试增量，不能作为最终版本。

等待清偿红例为 1红/1绿；类型化三段清偿与未知效果保护四红；停止节点 2红/2绿。qualification-final 的三项失败是已合法终态的 RecoverOnStart 返回待恢复空集，断言修订为扫描空加 Load 保持 Failed，保留原封印/状态断言。集中 seal-final 565 Passed/原2跳过。

关键普通 P/F/P：body-exit、run-immutable、node-epoch、history-wire、typed-effect、stop-node。当前版本为 causal-*-r3/record.json；前两组旧版本保留，不能重标当前。指定断言红、真实 Rebuild 和源码逐字节恢复均从各段实际日志/TRX读回。最终全量/身份差集、当前输入/产品读回以 test-observations.json 和 full-comparison.json 的真实结果为准，不提前声明。

脚本锚点失败/部分自有编辑分别保留 implement.py、fix_readback.py、fix_recovery_fixtures.py、fix_wire_history.py、finish_wire_history.py 及相应完成脚本；均先核对当前剩余片段再完成，没有重复整段替换、整文件 Git 恢复或丢弃原失败。改前/后与本聊天精确增量见 before/、current-source-integrity.json、this-chat-only.diff。

## 尚未独立闭合

所有原四 CONTROL MUST、全部原级义务与新增重要候选仍 open。原六迁移失败未修复、未豁免。当前只消费现有工作包，不第三次纯方案审核、不扩 tools 或自造 pass/receipt。Source/Node/run 是同控制包，阶段提交不代替统一认证与综合实现后审。

新静态重要候选（未运行实质反例）：TaskCenterHost.Admission.cs 的 NormalizeVolatile/MergeBackAuthoritativeSubmission 仍只允许/拷贝传统发送字段；真实 SendPreparedAsync 类型化拒绝会写 observedTerminal/serverRejectionEvidence/executionExitConfirmed 与新退出/效果字段，可能拒绝自己产生的清偿事实或保守留 Unknown。需要沿真实仲裁 Sender 的红例核对再修，不能直接宽化白名单、自审判闭合或删除并发改写反例。普通未知保留与错误释放分开。

下一项仍同控制/恢复链：真实 Sender 清偿事实合并反例；等待/暂停/LocalWait 的停止复验撤销；Completing/Unknown 面板停止和原身份只读对账；Source 多已有 run/在飞停止交错。旧 epoch/自动配置缺基线、旧数据/封印契约兼容及实际效果需验收。BGI C17 收尾 result_unknown，退出/模拟成功不代表不可逆效果。

RunStore 同实例串行门与对象所有权不是跨进程排他、防恶意代码或手工文件伪造保证。封印绑定当前序列化事实契约，未主张跨版本迁移验收。保护真实 User/配置/宏/脚本/截图、第三方 JS、.kiro、旧 D:/DOWN 与材料外成果；没有改 BGI 源、不重复旧105测试或旧集成，没有操作用户生产程序。

原全部公版共有功能、C01/C02/C04–C11/C17共11增强及C20、八类原生单项/调度/迁移/管理/分发/新产物实际运行停止重启数据保留仍是同一完整 Goal。只有全部完成才 complete。

最终普通读回：full-final-r3 为2128 Passed/原六 Failed/2 NotExecuted共2136，对prepared full-final新增41、删0、旧outcome变化0、失败testId集合相同；264输入前后/当前字节一致。seal-final 565/原2跳过，六项当前r3 P/F/P及恢复后全量已终态。所有阶段失败保留，原级义务仍open。
