# Prepared Stop 本地零调用清偿：接续现场

总目标未完成，原控制包未收口。沿用 usable-delivery-control-recovery-20261003 / 两次 blocked 前审 / 原四 CONTROL MUST / 全部原级义务 / 原 opening、history、请求计数。没有综合实现后审、发布或新产品实际运行/停止/重启/数据保留验收。采用交付优先、工作包合批、自动本地提交、自动交接政策。

来源聊天 01a1033d-dff3-74f2-950e-792c3a82ce34，接收 AUTO-STOP-SEAL-RELAY-20261004-FROM-01a1031a。本聊天 Goal 由原生 create_goal 一次建立并 get_goal 读回 active，原握手在 durable-observation-20261004/auto-relay-20261004/new-goal-readback.json；交接后的真实状态以本目录 auto-relay 的原生读回为准。

## 本段候选

同 prepared 已消费而未进入本端口时，核验原冻结载荷/epoch/wire/key/fingerprint/有效期/出现/attempt/StopAuthority，在 RunStore 同门内从耐久 Stop 与无矛盾受理事实发布类型化 localNoSendProof。保持 SendAttempted=true，不伪造 rawTerminal 或 exit；与 ServerRejectionEvidence 分开。只有该专门入口可新增证明，普通 Update/合并不能伪造；原证明不可删除/替换或同身份追加矛盾受理。Runner 停止链不再查询已清偿本地提交，恢复按证明清偿。

7 个源码/测试＋2个候选导航登记的改前字节、大小/hash/BOM/换行和本段精确增量分别在 before/、current-source-integrity.json、this-chat-only.diff。导航 continuation_sources / write_paths 不改原 sources/opening，也不当当前认证。阶段提交范围与最新 HEAD 以 commit-scope.json / COMMIT-RESULT-20261004.json 为准；后者在安全操作及提交后真实生成。

普通证据、指定失败及局限见 CANDIDATE-NOTES.md、test-observations.json、causal-observations.json、causal-runner-no-lookup-observation.json、impact-final/、full-final/、causal-*/restored/。只能依据实际已生成结果报告，缺路径不猜成功。历史全量/失败不重绑；本段没有 BGI 源码增量，不重复 BGI 构建或旧105测试。原六迁移失败未修/未豁免。

## 唯一下一项：同控制/恢复链的封印与释放

先补红反例并集中实现 RunStore 不可逆 terminal-release 封印：完整原 epoch/wire/出现/提交/StopAuthority，主体、前置和收尾的终态/退出及实际效果资格；未知、缺失、矛盾一律不释放。CompletionHistory、迟到准备/发送/恢复和全部事实新增/回退/改写必须受保护。Host 所有节点和运行释放入口消费对应封印，RunStore 门外调用仲裁回写，失败凭同封印重试，最终读回关联同封印。

静态新重要候选保持 open：TaskCenterHost.Admission.cs 的 NodeOutcomeIsTerminal 当前仅结果原词/send identity；TakeoverTerminalConfirmed 非节点仅 State；SweepTerminalNodeOperations 下一节点准入之前及时释放旧节点，不能冻结整个 run 来阻止后继。RecordIntent 替换旧提交时完整退出身份可能只剩 NodeOutcomes。须追全链，考虑耐久节点资格/旧提交留存与最终 run 封印的共同协议；此方向未实现/未验证，不把建议当已冻结唯一设计。MarkOperationTerminal 约192/2696、hook约390–428仅导航。仲裁内部 hook 不应反向取 RunStore 写门，避免锁序倒转。

随后仍按同控制链：等待/暂停/LocalWait 停止复验撤销；Completing/Unknown 面板停止和原身份只读对账；Source 多已有 run / 在飞停止交错。旧自动配置缺基线、换 epoch 实际兼容待验收；BGI 收尾 result_unknown、C17实际效果未验收。原级 MUST/important 不归普通BUG延期。

整包稳定后统一当前真实来源认证、规定全量/必要因果、独立综合实现后审和原级闭环，gpt-6.1-sol 默认medium、复杂/高风险high。不要第三次纯方案前审、重开包/账/Goal、自造pass/receipt或扩工具。原计划全部功能、公版恢复、迁移六失败/兼容/正式分发/实机仍属完整总目标，所有权威材料和保护沿上段完整交接及原总计划。

RunStore 实例门、控制流和串行对象所有权不证明跨进程排他、防恶意代码或外部文件伪造。普通证据不代替认证/独立后审/实际产品。

## 交接判断

同 prepared Stop 清偿形成聚焦、可复核的状态转换边界；下一步须同时设计节点及时释放、旧提交退出身份留存、完整 run 封印和 Host 锁序。当前上下文包含多版反例/夹具前提/突变与恢复、之前的多层历史引用，切分能让下一执行者按当前字节集中推理整条封印协议。不是时间/工具数强制交接，不将总目标标 complete。

所有测试/突变恢复与提交须真实终态后，才原生暂停本 Goal 并读回 paused、创建唯一同项目 local 接班；正确cwd/自身完整 Goal active 握手确认后旧聊天停工。具体状态来自 auto-relay 原生结果，本文不自证 paused/active。材料外成果、原账、User/配置/宏/截图、JS/.kiro/旧D:/DOWN保持。

最终安全读回：3项普通P/F/P已终态、subject逐字节恢复；全量/扩大使用的261源码输入与当前全部相同，两个程序集与全量执行时SHA256逐字节相同，因此不为交接重复全量。full-final 2087/原六/2，impact-final 552/2；全部原级义务仍open。读回细节test-observations.json。
