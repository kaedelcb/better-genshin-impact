# 槲寄生调度器 R5 交接稿（2026-09-21）

## 当前权威状态

- 分支：`main-OldTeaBag-B168`
- 最新提交：`2b54d1e21`（R5.3 §24 落地批次一：类型与持久化格式代；回归 775/1/776）；上一笔 `107a5731e`（§24 冻结稿抽出＋会诊至无必改项）
- §24 B3 外部启动生命周期设计**已抽出为独立冻结稿**（[`onedragon-r5-3-external-start-lifecycle-2026-09-21.md`](onedragon-r5-3-external-start-lifecycle-2026-09-21.md)，章节号仍为 §24.x；原设计稿 §24 处保留指针）。**工作区无未提交草稿**。
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
- **下一步**：**§24.4 生产组合根闭环夹具**（真实 `MainViewModel→CommandExecutor→TaskCenterHost→Core` 组装＋连续两次成功、F11 后批次退出、冷启动/旧 v2、>32 笔容量、切模式/关闭交错）并恢复生产接线（默认门关闭）→ B2-γ 第 3 步（含「在飞父操作占用判定」与流程登记／节点执行的锁外窗口）→ B4 → R5.3/5.5/5.8 收口。
- 生产外部启动接线（E3/E4/E5）**仍关闭**；`.bak`／`.stale`／`TestResults` 等未跟踪文件未动。

> 继续 BGI 槲寄生调度器 R5（接管与切换）。先读本交接稿与已冻结的 §24 独立稿（[`onedragon-r5-3-external-start-lifecycle-2026-09-21.md`](onedragon-r5-3-external-start-lifecycle-2026-09-21.md)，含 §24.20-D 落地清单与 §24.20-E 冻结状态）；owner 已选 A，B3 架构整改纳入 R5，§24 已冻结、落地批次一（类型与持久化格式代）已提交。**下一步＝Batch B**（`SettleCompletionAsync` 完成结算三分支／冲突裁决三项四项原子事务／第四类恢复集合／§24.6-5 逐分支责任映射／v2 `cancelled` 等价转换／Create 对 `Unknown` 硬拒绝），随后 §24.4 生产组合根闭环夹具、B2-γ 第 3 步、B4、R5.1–R5.8 收口与「无双跑」验收单签署。会诊 GPT-5.6 sol medium；构建/测试加 `-p:DeployToBgiTools=false`；提交只用 `git commit --only`。不要向我索取「继续」确认。
