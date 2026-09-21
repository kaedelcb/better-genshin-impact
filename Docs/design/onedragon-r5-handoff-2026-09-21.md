# 槲寄生调度器 R5 交接稿（2026-09-21）

## 当前权威状态

- 分支：`main-OldTeaBag-B168`
- 最新提交：**见 `git log -1`**（本稿随批次更新，不逐批回写哈希以免过期）；**当前全量回归 849 通过／1 跳过／850**（跳过＝P50 严格用例，断言未放宽；基线 552 未降）
- **已提交批次一览（R5.3 §24）**：`107a5731e`（§24 冻结稿抽出＋会诊至无必改项）→ `2b54d1e21`（批次一）→ `dc4db77b2`（二）→ `603a53be2`（三）→ `cfa36856b`（四）→ `54d590f25`（五）→ `e1bab1786`（六·P38）→ `c77160220`（七·两段式 `_gate`）→ `4ef69ee23`（八·恢复扫描集合②/③＋端口形状转 owner 裁决）→ `f0206511d`（九·组合根夹具一批）→ `95d12ccbc`（十·二批）→ `e54c2b7a8`（十一·三批）→ `ea973255b`（十二·B2-γ 第 3 步首批）；证据归并见 R5.2 稿 §25 与 §24.28
- §24 B3 外部启动生命周期设计**已抽出为独立冻结稿**（[`onedragon-r5-3-external-start-lifecycle-2026-09-21.md`](onedragon-r5-3-external-start-lifecycle-2026-09-21.md)，章节号仍为 §24.x；原设计稿 §24 处保留指针）。**工作区无未提交改动**（未跟踪的 `.bak`／`.stale`／`TestResults` 等不属改动）。
- **阶段状态**：R5.3 组件/组装层证据已归并（R5.2 稿 §25）；**未签署**：R5.8「无双跑」验收单（owner 级，须 §23.4 门禁并集逐项闭合或 owner 书面「显式移交并保留门禁」）。
- 工作区未跟踪文件：既有 `.bak`／`.stale`／`TestResults` 等，**禁止删除、移动或提交**

## owner 已决定

- 选择 **A：B3 架构整改纳入 R5，现在做**。
- 不强制旧用户切换；生产外部启动入口在新设计实现并通过验收前保持关闭。

## 已完成

- R5.0–R5.5：组件层完成。
- R5.6：事务组件层完成；迁移演练入口与路径隔离续修已完成。
- R5.7：旧补丁退出收口（静态清单 + 文本守卫）。
- R5.8：迁移演练隔离、路径身份、严格 BGP 进程枚举、宿主零演练产物快照等批次已收口。
- 最新全量回归：**775 通过 / 1 跳过 / 776**（跳过项为既有 P50 严格用例，未放宽）。

## 未完成，按顺序

1. ~~冻结 §24 B3 外部启动生命周期设计~~ **✅ 已完成（2026-09-21，第 23 轮会诊「无必改项」⇒ 已冻结；冻结 ≠ 生效）**。原要求逐条已落地（处置记录见该文件 §24.20-E）：
   - 租约格式 version：新增责任字段需升 version 3，旧消费者 fail-closed；
   - 唯一终态事务/恢复状态表；
   - 预观察记录（句柄产生前崩溃恢复）；
   - “Operation 已登记、尚未占位”的取消竞态；
   - `_gate` 两段事务：锁内占位发布责任 → 锁外启动/取证 → 重新串行校验结算；
   - 持久化可信 `OperationType`，未知类型 fail-closed；
   - `unknown` 与权威终态分离；
   - 取消/Unknown 允许长期保守停驻，不得用超时强制终局。
2. 实现 B3 生命周期改造：`CommandExecutor`、`ArbitrationAdmissionService`、`TaskCenterHost.Admission`、`ExternalStartLedger`、`ExternalStartAdmission`、`CommandResult` 及相关模型。
3. 补生产组合根闭环夹具，恢复 `MainViewModel → CommandExecutor → TaskCenterHost → Core` 生产接线；默认门在验收前保持关闭。
4. B2-γ 第 3 步接线复核：宿主节点分派 + 装饰器接线 + 端到端夹具。
5. B4：提交点清单 + 并发屏障收口 + 全量回归 + 结束会诊。
6. R5.3 / R5.5 / R5.8 最终收口与「无双跑」验收单签署。

## 会诊与执行纪律

- 会诊模型：**GPT-5.6 sol，effort=medium**；限流再降级 deepseek-flash（只读）。
- 实现类改动：会诊 → 处置 → 回归 → 验证会诊 → 无必改项后再提交。
- 文档类改动：攒 3–5 项一次会诊；不追求零发现，但不能自相矛盾、不能引用不存在锚点、效力状态要正确。
- 构建/测试：`dotnet test ... -p:DeployToBgiTools=false`。
- 提交：`git commit --only -m "<msg>" -- <files>`；禁止 `git add -A`／`git add .`。

## 新会话开场建议

## 续办进度（2026-09-21 批次）

- **§24 设计稿已原样抽出**为独立文件 [`onedragon-r5-3-external-start-lifecycle-2026-09-21.md`](onedragon-r5-3-external-start-lifecycle-2026-09-21.md)（**章节号保留 §24.x**；原设计稿 §24 处保留指针）。抽出原因：原设计稿体积超过会诊工具单文件上限。
- **§24 已冻结**（GPT-5.6 sol / medium，按 §17.4 迭代至**第 23 轮「无必改项」**）：冻结轮 12 项必改、复会诊 6、终轮 6、第四轮 4、第五轮 1、第六轮 2、第七轮 4、第八轮 4、第九轮 5、第十轮 4、第十一轮 2、第十二轮 1、第十三轮 1、第十四轮 4、第十五轮 4、第十六轮 1、第十七轮 2、第十八轮 3、第十九轮 1、第二十轮 1、第二十一轮 1、第二十二轮 2——**全部逐条文本处置**，逐条处置表与**唯一冻结状态表**见该文件 §24.20-E；冻结 ≠ 生效。
- **落地批次一已完成（代码）**：`ExternalStartExecution` 判别式化＋完成层 `ExternalStartCompletion`／早期层 `ExternalStartReply`；`ExternalStartAdmissionStatus` 增 `Cancelled`/`ExecutionFailed`；`AdmissionResult`／`ExternalStartAdmissionOutcome`／`CommandResult` 增结果维×责任维加法字段（线路词表不变）；**租约 version 3**（`OperationType`／`PendingTerminal`／`ExecutionResult`／`PreObservations[]`／`ConflictResolutionAudits[]`／`ReconciledNotAcceptedEvidence[]`，≤2 兼容读＋旧代未决责任 fail-closed，v3 审计双向绑定与逐字段校验）；**台账 version 2**（`jobId`／`terminalObservedAtUtc`／终态副本）；门面**占位同次发布写 `PreObservationRecord`**；宿主发送层判别式无损映射＋接管路由改按 `OperationType`。实现批次会诊 5 轮（10→6→5→4→2→1），**末轮无必改项＝可提交**；全量回归 **775/1/776**（基线未降）。
- **落地批次二已完成（代码，Batch B 第一步）**：完成层结算入口 `SettleCompletionAsync`（§24.3-4 三分支＋§24.15 唯一顺序「接管只落记录不关闭 → `ExecutionResult`＋`PendingTerminal` → 台账 Terminal → 关闭 → Operation 终局」）；`AdmissionHooks` 新增 `TakeoverTerminalPersist`／`TakeoverTerminalPayloadConfirmed`／`TakeoverJobIdRead`（三态 `LedgerHandleProbe`）并接入真实台账；统一逐字段判据（`CompletionMatchesExecutionResult`／`CompletionMatchesPendingTerminal`／`TerminalFactsConsistent`）；三处读回验证；TOCTOU 防释放（`terminal_job_id_backfill_required`／`terminal_ledger_unreadable`）；类型 fail-closed（仅 `ExternalStart`，`Unknown` 拒绝）＋`MarkOperationTerminal` 外部启动禁止旁路；`ExternalStartLedger.RecordAccepted` 允许对既有 Terminal 记录**仅做句柄合并**（不改状态/终态副本）。会诊 10 轮（10→6→4→3→1→2→1→1→1→0），**末轮无必改项＝可提交**；夹具 +15；全量回归 **790/1/791**（基线未降）。
- **落地批次三已完成（代码，Batch B 续：冲突登记／裁决／全链守卫）**：`RegisterConflictEvidenceAsync`（结构化冲突证据＋幂等＋类型/当前轮拒绝校验）、`AdjudicateConflictAsync`（受理终态＝方向声明→§24.15 终态链→三项原子发布；未受理＝四项原子发布；确定性 `auditId`/`evidenceId`；全载荷幂等；墓碑区域冻结；合并项镜像）、`NotAcceptedObservation`＋封闭事实类型白名单＋`NotAcceptedObservationVerifier`（未配置 fail-closed）；全链守卫（准入 `facts_unknown`、重试/窗口/普通对账/分类读取/恢复/裁剪/容量）。会诊 6 轮（12→4→3→3→3→0），**末轮无必改项＝可提交**；夹具 +14；全量回归 **803/1/804**（基线未降）。
- **落地批次四已完成（代码，Batch B 收尾之一：操作类型 fail-closed 全链）**：`Create` 拒绝 `Unknown`/未定义枚举（`operation_type_required`，零登记）；`ContinueUseAsync`／`RetryAsync`／`ValidateAndOccupy` 以**持久化类型**为准拒绝（`legacy_operation_type_unresolved`，责任 `Pending`＋完整发送关联、不改写状态、不重发）；优先级纪律＝本记录冲突 ＞ 合并目标冲突 ＞ 类型隔离；夹具迁移（节点场景显式 `NodeExecution` ＋ `ResourceRef = node:{nodeId}`、`R58` 合成候选 `ExternalStart`）。会诊 7 轮（3→2→2→1→1→1→0），**末轮无必改项＝可提交**；夹具 +5；全量回归 **807/1/808**（基线未降）。
- **落地批次五已完成（代码，Batch B 收尾之二：完成观察接线＋v2 取消等价）**：端口新增 `CompletionProvider`；宿主受理后取完成结果交 `SettleCompletionAsync`（§24.15 唯一顺序）并回传结算后的结果维/责任维，结算异常保留已观察事实；适配器 `ToExecution`（`success｜IsTerminal｜IsObservedCancel` ⇒ 发送层已受理）与 `ToCompletion`（取消⇒终态 `Cancelled`；非终态⇒`Unknown`；权威失败⇒`ExecutionFailed`；**信封 `ErrorCode` 不作证据**）。会诊 3 轮（3→1→0），**末轮无必改项＝可提交**；夹具 +5；全量回归 **812/1/813**。
- **落地批次六已完成（代码，Batch B 收尾之三／P38：ext 队列通道「早期受理＋完成观察」拆分）**：早期段（通道不可用＝**未发送**⇒回退既有路径／先订阅后动作／提交一次／构造 §24.14-1 早期层回执 `ExternalStartReply.EarlyAccepted`）与观察段（**门面锁外**等待：事件快速路径＋5s 轮询安全网；等待器由观察段 `finally` 释放）拆分；观察结论只在事件/轮询**权威终态**时构造终态载体，`not_found`／24h 兜底／通道瞬态／本地取消一律 `Unknown`（不写 `PendingTerminal`）；未接线直启路径的旧词表投影**逐字保留**（文案/错误码/探针/返回时机）。**会诊**：GPT-5.6 sol medium 一轮（5 阻断／3 重要／1 建议）⇒ 已修：`ToCompletion` 非终态改回 **`null`**（原 `Unknown` 使 §24.4-4「v2 发送成功：入口 success」被改判为对外 `result_unknown`）、宿主完成观察消费条件改为「**有完整发送身份＋确有完成事实**」（接管/关闭失败 `Reconciling` 时权威终态仍按 §24.15 唯一顺序结算）、`CompletionObserver` 返回 `null` 语义（不得写成 `Unknown`）、队列拒绝**可重试封闭白名单**、观察记录**接收时点**与 `RawTerminal` **原词不改写**、未知分支 JobId 回退与**句柄冲突保守停驻**；夹具 +9（含「完成观察等待期间其他请求可取得门面锁」）；全量回归 **821 通过／1 跳过／822**。**认领未完成项**（明细见 §24.21-C）：**下一批首项＝两段式 `_gate`（D6 残项：持锁调用 Sender 违反 §24.14-2／§24.18-2）**＋端口形状收敛为 `ExternalStartReply`＋恢复扫描四类集合（含未终结台账）接线；本地取消链归 R5.3 取消链批次。
- **同批第二轮验证会诊（gpt-5.6-sol／medium）1 阻断＋1 重要，均已处置**：①`SettleCompletionAsync` 返回 `not_accepted` 时**不得静默丢权威终态** ⇒ 宿主新增 `RegisterObservedTerminalConflictAsync`（原子追加冲突证据＋置冲突待决、责任 `Pending`、禁止重发；既有拒绝本体不改写，仅由 `Superseded*` 表达取代关系）；②权威终态**缺观察时点 ⇒ fail-closed**（不再以映射时刻冒充证据接收时刻）。夹具 +11；全量回归 **822 通过／1 跳过／823**。
- **落地批次七已完成（代码，两段式 `_gate`·外部启动锁外窗口）**：门面 `ProcessWinnerAsync` 拆两段——第一段（锁内）＝轮次快照／裁决／**占位**／同次发布发送责任与 `PreObservation`；第二段＝**释放 `_gate`** 后调用外部启动适配层的启动／早期受理，返回后重新取得权威串行权再结算（§24.18-2／§24.14-2）。锁外窗口**按持久化操作类型分派**（仅 `OperationRecord.OperationType == ExternalStart`，§24.17-2／§24.8-1）：**实测**无条件放开会使 `TaskCenterSuccessorPathGateTests` 两条既有夹具转红（E1 启动发送窗口内提交的节点操作被并发轮次按占用冲突拒绝 ⇒ 停留 `Queued`、`sends=0`、运行 `Unknown`），故流程登记／节点执行的放开须与「在飞父操作占用／合并判定」一并调整＝**B2-γ 批次**（登记 §24.21-C-1①、§24.22-B）。**会诊一轮逐条处置**：重取锁后**首个副作用前**复核本笔发送责任归属（`DetectSendResponsibilityAdvanced`：被推进 ⇒ 保守停驻、**零台账写入**、不关闭、不释放占用）、`BuildDispatch` 异常归类复原、非终态「曾受理」冲突词表口径登记归冲突批次。夹具 +4；全量回归 **826 通过／1 跳过／827**。**仍余**：端口形状收敛为 `ExternalStartReply`、恢复扫描集合②（未终结台账）。
- **批次七补记（第二轮流验证处置）**：锁外返回后的复核扩展为**占位基线**全维度（`leaseId`/`ownerEpoch`＋操作请求状态＋`submissionIdentity＋sendSeq` 值快照；`DetectSendResponsibilityAdvanced`，仅锁外窗口生效）——换主/恢复推进任一命中即**保守停驻且零台账写入**；新增夹具「换主但发送身份未变仍停驻」。夹具共 +5；全量回归 **827 通过／1 跳过／828**。**如实登记残余**：读租约与写台账（独立文件）之间无跨文件原子边界（归 §24.12 恢复集合②/③与冲突裁决）；非终态「曾受理」冲突词表口径归冲突批次（§24.22-C′）。
- **落地批次八已完成（代码，恢复扫描集合②/③＋早期层端口形状处置）**：门面新增 `RecoverExternalStartObservationsAsync`＋台账扫描钩子 `TakeoverLedgerScan`（不可读/未配置＝不可确认 ⇒ 保守停驻）：**集合②未终结台账**只保留观察责任（不改状态、不写终态载体、不释放、不重发）；**集合③台账已终态而 Operation 未终局**用**已持久化 `PendingTerminal`** 补终局（`Kind` 唯一判据、`Unknown`/载荷缺失不驱动）；其余不一致只计数登记（`ExternalStartRecoveryReport`）。宿主在门面组装后（心跳先行、锁外）执行一次对齐，异常只记日志。**早期层端口形状**：因 `EarlyAccepted` 强校验非空句柄与 §24.4-4「v2 发送成功＝入口 success」冲突，保留 `ExternalStartExecution` 端口类型（§24.23-B 留事后可剔除入口）。夹具 +3；全量回归 **830 通过／1 跳过／831**。
- **批次八补记（会诊处置＋owner 裁决项）**：封堵恢复扫描的**冲突待决旁路**（`conflict.pending` 一律跳过）、**补终局不得补造载荷**（缺原词/观察时点/证据来源或 `Failed` 缺错误码 ⇒ 不驱动）、报告成功判据改为**责任已结清**、宿主异常改 `TryLog`＋`ct` 传播、`TakeoverLedgerFact` 增 `JobId`；夹具合计 +5；全量回归 **832 通过／1 跳过／833**。**新增 owner 裁决项（未完成）**：**早期层端口形状**——§24.14-1（`EarlyAccepted` 须带非空句柄、无句柄一律 `Unknown`）与已验收的 §24.4-4（v2 发送成功＝入口 success）在 v2 阻塞协议上冲突，需 owner 二选一（修订 §24.14-1／书面接受现状）；裁决前生产接线保持关闭（§24.23-B）。**仍未完成**：集合②的**持续观察重绑**（归 §24.4 组合根批次）。
- **落地批次九已完成（代码，§24.4 生产组合根闭环夹具·第一批）**：**真实组装、仅替换外部传输**——真实 `CommandExecutor`（E3 接线）→ 真实 `TaskCenterHost`（真实门面＋接管台账＋真实 `BgiExternalClient`）→ 真实 Core；新增**仅测试**接缝 `IpcClient`／`BgiExternalClient.PipeNameOverrideForTest`（生产恒 `null`）＋进程内管道替身 `BgiInstancePipeDouble`（生产同构帧格式、v2/ext 协议子集、`ext.event` 推送）。已覆盖 §24.4-1（连续两次成功，第二次不被第一次未终结台账阻断；`ext.task.start` 恰两次）／§24.4-2（ext `Completed` ⇒ 台账 Terminal＋JobId 可读＋Operation `TerminalCompleted`＋主槽位释放）／§24.4-3（`task.queueCancelled` ⇒ 入口 failed+cancelled、责任 `Settled`、不重建 Submission）／§24.4-4（v2 成功 ⇒ 入口 success、`IsTerminal=false`、责任 `Pending`、台账保持未终局）。**组合根验收直接产出并已修缺陷**：v2「回核心结果」路径丢弃结果维/责任维（D9 断链）⇒ 现按准入结论补齐（线路字段逐字不变）。**未覆盖（归 §24.4 第二批，登记 §24.25-D）**：终态落盘失败交错、>32 笔容量与终局释放、冷启动/纪元变化/切模式/宿主关闭交错、组合根层「默认未注入路径＋控制热键」断言。夹具 +3；全量回归 **835 通过／1 跳过／836**。**生产接线仍关闭**。
- **批次九补记（组合根验收会诊处置）**：①替身**拒绝推送未订阅事件**（实现 `ext.event.subscribe`＋记录订阅集；夹具镜像生产 `SubscribeAsync([])` 启动步骤并断言订阅发生）；②**D9 字段贯通补全**（`RawTerminal`／`ExecutionErrorCode` 贯通；发送身份按 D1/D9 分工留在门面结果与适配器 outcome）；③§24.4-2 收紧为「终局操作**迁出 `Active` 计容区**且快照无 Active 操作」；④静态管道名接缝**还原初始化前原值**。全量回归 **835 通过／1 跳过／836**。
- **落地批次十已完成（代码，§24.4 生产组合根闭环夹具·第二批）**：组合根夹具扩充至 **8 枚**：>32 笔容量与终局释放（33 轮闭环、无 Active 计容操作、台账 33 条 Terminal）、**默认未注入路径**（生产门仍关闭的可观测证据：零仲裁操作/零台账）、**终态落盘失败 → 集合②保留责任 → 造景台账终态 → 集合③补终局**（全程零重发）、冷启动零发送、**宿主关闭交错**（不假成功/不重发/责任 Pending）。**直接产出并已修缺口**：`SettleCompletionAsync` 结算无法执行（租约不可用/Operations 缺失）时原返回责任 `None`（＝不适用）⇒ 现按 §24.6-5 回显 `Pending`＋完整发送身份。夹具 +5；全量回归 **840 通过／1 跳过／841**。**仍未覆盖（登记 §24.26-C）**：授权后纪元变化、切监控模式、组合根层控制热键双向断言、§24.4-3「关闭前 Reconciling」细分、owner 实机项。
- **批次十补记（会诊处置）**：①**责任/结果双维分流**——`SettleCompletionAsync` 在结算无法执行时按可证明事实分流（完整发送身份 ⇒ `Pending`＋回显身份；否则维持 `None`），且已观察的取消/失败**保留结果维与原词/错误码/证据来源**（不再降为不可考）；②>32 容量断言收紧为 `Active` 与 `TerminalPendingTransfer` **双双为 0**；③关闭交错夹具先断言「已受理（台账 `AcceptedPendingExecution`＋Submission 已关闭＋Operation `Accepted`）」再释放租约；④冷启动出口精确断言 `result_unknown`。全量回归 **840 通过／1 跳过／841**。
- **落地批次十一已完成（代码，§24.4 组合根夹具·第三批）**：组合根夹具增至 **10 枚**——新增「**切监控模式（切换闸门）双向**」（闸门激活 ⇒ 拒绝且零发送；解除 ⇒ 恢复）与「**控制热键双向**」（接线态经统一仲裁面并执行核心 IPC；未接线＝生产默认 ⇒ 直发 IPC 且零仲裁操作）。**替身同构改进**：管道替身由单连接改为**多实例并发**（与生产同语义），修掉 ext 常驻连接与 v2 新连接互斥导致的虚假连接超时。**承接登记（§24.27-C）**：授权后纪元变化的组合根端到端模拟未做（门面/后继路径级 `stale_epoch` 已有夹具，本批不冒充）；§24.4-3「关闭前 Reconciling」的本地等待取消细分归 R5.3 取消链批次；owner 实机项按 §23.4 保留。夹具 +2；全量回归 **842 通过／1 跳过／843**。**生产接线仍关闭**。
- **批次十一补记（会诊处置）**：①切模式夹具改**状态级因果证据**（登记 `LastSendSeq==0`＋回 `Queued` 可再驱动＋零发送＋闸门确实激活）；②替身 **Dispose 收敛**（先取消/关连接 → 等全部服务任务退出 → 再释放同步原语）；③事件订阅**按连接**维护（只推给订阅方，v2 IPC 连接不再被注入 `ext.event`）；④热键「未接线」证据改**身份集合逐项比对**；⑤**登记–回退**：控制面门禁压成 `result_unknown` 的 refinement（需与分类语义/宿主映射/既有夹具同批变更，本批不回退已验证语义）。全量回归 **842 通过／1 跳过／843**。
- **落地批次十二已完成（代码，B2-γ 第 3 步·首批：装饰器契约夹具＋P50 复现取证）**：①**装饰器契约夹具 +3**（关闭 §12.2 三项夹具盲区）：参数与令牌原样透传（同一请求实例＋同一令牌、不回落 inner）；故障/取消透明传播——**加固后**用「预构造故障/取消任务＋`Assert.Same` 原样返回同一实例」与「同步抛错必须在同步调用点逸出原异常」锁定（排除「包一层 async/await 也能过」的假通过）；能力值复读（不得构造期缓存）。②**P50 复现取证**：取消 33 节点宿主级用例 Skip 后，定向单跑通过，但**满负载全量 5 轮中 1 轮红灯**（同宿主类负载敏感夹具 `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission` 收敛失败，保守方向、无双跑）⇒ 负载敏感性**真实且可复现**；据此**恢复 Skip**、P50 维持**阻断式挂账**（诊断入口须覆盖整个用例类；生产节点改道门继续保留）。③**§12.3 交错⑤保留**（缺逐节点终局/迁出计容区的直接证据）。**第 3 步仍未完成（§24.28-C）**：不可变冻结身份、六类交错强制版、M1/M3、`_successorAdmissionWired` 生产不传。夹具 +3；全量回归 **845 通过／1 跳过／846**。
- **下一步**：**B2-γ 第 3 步续**（§24.28-C：不可变冻结身份；六类交错的强制版夹具——首节点抢先需新增**门面 `_gate` 外**的观察点；M1 首节点授权衔接；M3 准备阶段边界与本地未发送证据）→ 生产接线恢复评估（默认门关闭；须 §23.4 门禁并集与 owner 书面确认）→ B4（提交点清单＋并发屏障＋全量回归＋结束会诊）→ R5.3/5.5/5.8 收口与「无双跑」验收单成稿（含 owner 裁决项：§24.23-B 早期层端口形状；承接项：§24.27-C 纪元变化组合根模拟、§24.21-C-3 取消链、P50 观察项）。
- 生产外部启动接线（E3/E4/E5）**仍关闭**；`.bak`／`.stale`／`TestResults` 等未跟踪文件未动。

> 继续 BGI 槲寄生调度器 R5（接管与切换）。先读本交接稿与已冻结的 §24 独立稿（[`onedragon-r5-3-external-start-lifecycle-2026-09-21.md`](onedragon-r5-3-external-start-lifecycle-2026-09-21.md)，含 §24.20-D 落地清单与 §24.20-E 冻结状态）；owner 已选 A，B3 架构整改纳入 R5，§24 已冻结、落地批次一（类型与持久化格式代）已提交。**下一步＝Batch B**（`SettleCompletionAsync` 完成结算三分支／冲突裁决三项四项原子事务／第四类恢复集合／§24.6-5 逐分支责任映射／v2 `cancelled` 等价转换／Create 对 `Unknown` 硬拒绝），随后 §24.4 生产组合根闭环夹具、B2-γ 第 3 步、B4、R5.1–R5.8 收口与「无双跑」验收单签署。会诊 GPT-5.6 sol medium；构建/测试加 `-p:DeployToBgiTools=false`；提交只用 `git commit --only`。不要向我索取「继续」确认。
- **文档批已完成（R5.3 §24 证据归并＋§17 口径同步）**：R5.2 稿新增 **§25「R5.3 §24 批次证据增量（组件/组装层；供 §23 R5.8 验收单引用）」**（逐批提交号／登记章节／组合根夹具 10 枚／全量 845-1-846／**不改签署状态、不闭合门禁并集**）；§24.28-D 给出与 §17 启用前置清单的**逐项归并视图**（P38 已关闭；P19② 仍欠；P17 归 B4；P50 维持阻断式 Skip＋本轮复现证据；P6/P8 仍欠；P54 部分已补）；§24.21-C-6 统一「端口形状／集合②③」口径（避免同稿两处矛盾）。纯文档批。
- **落地批次十三已完成（代码，B2-γ 第 3 步·次批：§12.3 交错①「首节点抢先」强制版）**：新增**仅测试**观察点 `TaskCenterAdmissionSeams.BeforeSuccessorAdmission`（**节点准入入口、取得门面锁之前**；生产恒 `null`；只发信号＋只读快照）——满足 §17 P17「观察点必须在**锁外**」（既有 `AdmissionBarriers` 全在 `_gate` 内，锁内等待会自死锁）；夹具 `NodeAdmission_BeforeGateObservation_E1StillOpen_NoChildPermitYet` 强制制造「E1 未关闭时首节点到达」并断言：**父责任仍在**（E1 Submission 仍开、父操作未终局）＋**子许可 0**＋**子发送 0**，放行后流程正常收口（`Succeeded`＋恰 2 次节点发送）＝不自拒／不重复占位／不循环等待。§17 P17 的**组件屏障部分由此关闭**（真实入口证据仍属 §23.1 owner 侧）；交错②③④⑤⑥仍未做（⑤不得据绿线关闭）。夹具 +1；全量回归 **846 通过／1 跳过／847**。
- **批次十三补记（会诊处置）**：①父操作断言加固（预置终局态使未命中即失败＋父操作唯一命中＋`SubmissionIdentity` 与开放 Submission 全等＋非终局集合）；②接缝 `BeforeSuccessorAdmission` 改为**无参** `Func<Task>`（不暴露可变生产对象）；③夹具防悬挂（断言段 `try/finally` 无条件放行 E1＋外层等待探针收敛）与租约快照有界重试；④清理重复 `/// <summary>` 并让 33 节点夹具注释显式由 §24.29 取代。全量回归 **846 通过／1 跳过／847**。
- **落地批次十四已完成（代码，§12.3 交错⑤「逐节点释放直接证据」）**：新增夹具 `NodeSubmit_EachSendObservesPreviousNodeReleased`——6 节点流程中在第 k 次（k≥2）节点发送入口只读租约快照逐次取证（5 次）。**会诊加固后**判据＝前节点 **`TerminalCompleted` ＋ `Zone == Tombstone`（真正迁出计容区；`TerminalPendingTransfer` 仍计容故不得接受）＋ `SubmissionIdentity` 非空＋唯一命中**，并同一时点断言**当前节点 `Zone == Active`**；新增 `OnBeforeSendWithPayload` 以**实际 payload 的 `configName`** 关联「第 k 次发送 ↔ 具体节点」（不得按序号推断）。**加固后实测通过 ⇒ §17 P19② 组件/宿主层关闭**（更强证据）；P19①「原因码逐次取证」仍未关闭。§12.3 交错清单现为：①组件层已关闭（§24.29）、⑤已补直接证据（§24.30）；②③④⑥与不可变冻结身份／M1/M3／P50 诊断／P6/P8 仍未做。夹具 +1；全量回归 **847 通过／1 跳过／848**。
- **落地批次十五已完成（代码，§12.3 交错③「准备/发送阶段故障」·发送阶段支）**：端口级注入 `RoutingFakePort.ThrowOnSend`（**记录本次发送尝试之后**抛 `IOException`）；夹具 `NodeSubmit_SendStageFailure_StaysReconcilingNoResend`：**会诊加固后**断言运行收敛且**恰为 `Unknown`**、节点结果 **`unknown`**、只一次发送尝试且 **`LastSendSeq==1`**、节点 Operation **`Reconciling`**、**未决 `Submission` 仍在册且身份全等**——「不可考/责任保留」落在责任载体上（§12.3 M3）。**其余两支承接**（§24.31-B）：准备阶段 `RunStore` 更新失败按 §17 **P49**（归 B4）；**占位前校验拒绝尚无独立夹具**（与交错⑥同批）。§12.3 清单：①组件层关闭、③发送阶段支已补、⑤已补；仍未做②③其余两支④⑥＋P19①＋不可变冻结身份＋M1/M3 其余＋P50 诊断＋P6/P8。夹具 +1；全量回归 **848 通过／1 跳过／849**。
- **文档批已完成（§16／§17 台账状态同步＋控制键夹具补强）**：①**新增夹具** `CompositionRoot_ControlKeyHotkeys_BypassAdmissionEntirely`——**控制键**（`CancelTaskHotkey`／`BgiEnabledHotkey`／`SuspendHotkey`）在**接线态下仍直通**：`action.execute_hotkey` 每次恰一次、`task.status` 零次、**零仲裁操作**（满足 §16 原行的「发送一次／状态查询零次／准入零次」）；②**R5.2 稿 §16-A 升级为真章节标题**并同步汇总统计（0 已覆盖／6 部分 → **1 已覆盖／5 部分**），其中「控制热键不经准入」改判**已覆盖**并点名上述夹具（与普通热键 E4 双向夹具 `CompositionRoot_ControlHotkey_WiredGoesThroughAdmission_UnwiredDoesNot` 区分，不得互相替代）；③**新增 §17-A 状态同步**：P17＝已覆盖（组件层，§24.29）、P19②＝已覆盖（§24.30）、P23＝已覆盖（本节）、P50＝不变（阻断式 Skip）；④交接稿「当前权威状态」改为哈希无关表述并修正夹具数目表述。夹具 +1；全量回归 **849 通过／1 跳过／850**。
