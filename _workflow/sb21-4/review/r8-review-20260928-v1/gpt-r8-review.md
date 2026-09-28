# GPT R8 BO-13 最终复审报告

**模型：** gpt-6-astra  
**强度：** medium  
**模式：** review  
**调用次数：** 1  
**送审快照：** _workflow/sb21-4/review-packet-r8-final-20260928-v1/packet.md  
**工具返回：** reviewed_files 仅含该 packet.md；diff_included=false。该快照由 workflow 工具生成，packet.md 本身含全局 git status、相关未暂存/暂存差异及材料外说明。

## 总体裁决

BO-13 原始 R1 #1–#5 及包内列明的 R3/R4 IMPORTANT 扩展，均可在报告所列有限合同内裁为 CLOSED_AT_IMPORTANT。未发现该范围内新增的 MUST/IMPORTANT 产品缺陷。这是本轮技术裁决，不代表 owner 已接受，也不代表生产验收或 Wave3 残项清零。

仍有两项 SHOULD 文档问题：

1. R7 突变编号仍错序。R5.3 §24.124.3 将 M3/M4 分别描述为“恢复 revision／重启 repark Context”；实际 mutations-r7.json 是 M3＝repark Context，M4＝恢复 revision。应按实验 ID 修正对应关系；“六次实验、五种源码 mutant”的去重结论正确。
2. 当前交接仍混入旧时点表述。handoff“工作区与收口证据”仍写“当前状态仍为 R6 复审前”；R5.3、handoff 又将已有 R8 最终回归和 ClaimSurface 写成待执行。应同步为本次固定证据和最终裁决，防止后续误用。机械守卫通过不能消除这些语义矛盾。

以下仅依据提供的 packet 文本；我没有调用工具、执行命令、修改文件或独立核验 SHA/TRX。

## 原始 R1 项目

| 项目 | 原级裁决 | 支持证据与限定边界 |
|---|---|---|
| R1 #1 前置责任与零发送边界 | CLOSED_AT_IMPORTANT | Stop 专用谓词采用终态白名单：仅 Succeeded/Failed/Cancelled 放行，Intent/Submitted/Unknown 及枚举外值拒绝。IndependentlyRefusesEachUnresolvedPrerequisiteState、UnknownPrerequisiteRemainsUnresolvedForEachPersistedSendFact 比较 run/queue 原字节；已知终态正例及两个 Succeeded 历史 send/job 正例支持兼容行为。HasUnresolvedExternalFact 仍独立检查提交/收尾责任，恢复谓词未因本修复改变。只关闭持久状态分类下的 Stop 合同；不证明 producer 将真实责任正确标为终态，也不声称穷举所有历史字段组合。 |
| R1 #2 决策、身份与 revision 不一致 | CLOSED_AT_IMPORTANT | HasValidParkedDecision 核验 run/workflow/revision、cursor、Deferred submission 及派生 key、Context/binding 配对和规范候选身份；Wait 修订关系为 0 < binding ≤ context ≤ run。非法 revision、旧 Context、错误提交 key、缺失/错误规范身份，以及 StartupHandoff 借用另一 run 的负例有支持；合法 StartupHandoff 正例防止过度拒绝。恢复与同进程 repark 测试证明持久 Interrupted、revision 推进、Context 刷新、旧 binding 和 generation/HWM 保留，并能随后 Stop、新建另一 RunId。Panel 来源仅验证配对快照，不重新鉴权；Hold 允许缺来源/排序证明，但须通过公共 NoSend、cursor/submission 检查且无 binding。不是任意双快照篡改检测或任意流程修订矩阵证明。 |
| R1 #3 请求 runId 与正文错绑 | CLOSED_AT_IMPORTANT | 当前 TaskCenterHost.cs:380–650 已完整展示入口校验及锁内最终复读。最终对象的 body ID、状态、reservation/drive、外部责任、前置责任、decision/binding 校验均先于 LocalWaitQueue.Cancel。FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes 和 R8 M1 支持该关键 guard。字节合同是 Stop 不进一步修改被替换后的 A 文件、B 文件及队列；测试并没有把 A 恢复为替换前内容。关闭限停驻 Stop 路径，不推广为全部动作分支或最终读取后的任意外部写者防护。 |
| R1 #4 Admission 未终局对账 | CLOSED_AT_IMPORTANT | run 成功持久化后，Host 在 _gate 外调用显式终局对账；已经 Cancelled 的再次 Stop 可重试。accepted registration 正例、read timeout、逻辑拒绝、五次写失败、最终确认读失败、损坏/不支持版本和无映射 Hold 用例支持结果传播。允许 run 已 Cancelled 而 admission 暂为 Accepted，此时必须返回可观察的 Unavailable；故障解除后显式 Stop 收敛。永久故障期间不承诺自动成功。 |
| R1 #5 Queue/Run/read/reservation 故障窗口 | CLOSED_AT_IMPORTANT | queue 发布失败、run 发布失败、初读损坏、最终读取错绑/损坏/异常及 Resume reservation 均有展示的行为证据。完整 RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt 明确断言首次 Unavailable、run 原字节及停驻态保留、queue 已 Cancelled；解除故障后再次 Stop 为 Effective、run Cancelled、queue 全文件字节不变。只关闭这些具名故障接缝和确定交错；缺项分支及未展示的独立 queue I/O-read 故障覆盖不能被描述为同等行为验证。 |

完整顺序可以确认：

请求 ID → 初读及正文 ID → 进入 Host gate → 最终复读及正文 ID/状态 → reservation/drive → 外部责任 → 前置责任 → Wait/Hold 与绑定检查 → queue 精确取消 → run 持久化 → 退出 gate → 通知 → admission 对账

其中队列墓碑已经发布而 run 写入失败，不是零副作用失败。返回 Unavailable 并明确提示部分提交、允许重试，符合展示的有限合同。LocalWaitQueueStore.Cancel 先比较载荷，再对已有墓碑直接返回 AlreadyCancelled，因此同一匹配墓碑的重试不会调用 Persist；载荷已变则拒绝。

## R3/R4 IMPORTANT 扩展

| 扩展 | 原级裁决 | 当前对应证据 |
|---|---|---|
| R3 #1 同会话可观察重试、sibling 隔离 | CLOSED_AT_IMPORTANT | ReadTimeoutIsVisibleAndSameSessionStopRetriesToTerminal、LogicalAdmissionRejectionRemainsVisibleAndCanRetry、WriteExhaustionProcessesSiblingAndRetryDoesNotRewriteTerminalSibling。一个登记失败及 logger 抛错不阻断另一登记；重试保留已终局 sibling 的 revision/time；重复 Stop 保留完整 lease bytes。 |
| R3 #2 独立输入及失败分支证据缺口 | CLOSED_AT_IMPORTANT | 独立前置状态输入、最终读取错绑/损坏/异常、reservation-held 字节断言，加上本轮补齐的完整顺序及 run-publish-failure 测试，覆盖包内登记的缺证项。历史突变不重记为 R8 实验。 |
| R4 overlap／终局写幂等性 | CLOSED_AT_IMPORTANT | overlap 测试让超时 worker 与 retry 同时到达写边界，并等待两者 reconciliation 完成，再检查单次 lease revision、对应 UpdatedRevision、另一 run operation 序列化字节和重复 Stop 全 lease bytes。限同 Host、同 admission service 的确定交错。 |
| R4 identity／revision／恢复矩阵 | CLOSED_AT_IMPORTANT | 对应 R1 #2 的非法 revision、合法旧 binding、来源身份正反例及两类 repark 生命周期；不扩展为任意修订、任意外部 writer 或跨进程恢复证明。 |

Admission 的重启用例实际执行的是重建 Host 后显式 Stop；不能据此声称启动扫描已自动完成终局对账。MarkOperationTerminal 展示了服务互斥及最新 lease 内再次检查 Accepted；MutateCore 展示了 revision 更新和发布，但 WithLock 实现未展开，因此不将注释中的跨进程锁声明升级为本轮跨进程验证。

## R8 当前源码反向突变

- M1：testId 14b12c92-31c4-d84d-5129-de01fac6b561 在删除最终 body-ID guard 后，于测试第 977 行出现 Expected Unavailable / Actual Effective，命中目标状态断言。mutant 未执行到后续字节断言；字节保持由 baseline/restored 用例支持。
- M2：testId c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72 在重写已有墓碑后，于第 1179 行最终集合字节断言失败，直接识别了重试改写。两项均报告 baseline/build 成功、mutant build 成功而目标测试失败、恢复 SHA 等于原 SHA 且恢复测试成功。这是包内执行记录，不是我的独立复跑。

## 验证与范围边界

最终验证帧报告 BO-13 49/49、LocalWait 240/240、助手全量 1558 passed／2 skipped／0 failed。首次文档守卫失败被保留并解释。构建发生在最终文档措辞修正前，产品/测试源码 SHA 与当前一致，不能称为“文档修正后重新构建”。ClaimSurface regen/no-env 的稳定 SHA 支持清单一致性；testId 差集支持名称和结果集合比较，不证明测试正文未变。

本结论不证明跨文件原子事务、断电耐久、新 OS 进程、任意外部写者、真实 admission 服务或生产实机行为。材料外变更不随本审查获得验收或合并授权。BO-6/7 保持未启动及原 MUST/IMPORTANT，BO-8/9 保持原级冻结；BO-4、BO-10/12 不重开，所有生产门继续关闭。
