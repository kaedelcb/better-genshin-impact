# R5.2 接线设计稿 §24 独立承载：B3 外部启动生命周期补全设计（2026-09-21 抽出；冻结状态见 §24.20-E）

> **来源与编号**：本文件内容原为《R5.2 全入口仲裁接入 · 接线设计稿 v8》`Docs/design/onedragon-r5-2-entry-arbitration-wiring-2026-09-20.md` 的 **§24**，于 2026-09-21 **原样抽出**（**章节号保留 §24.x，条款文本未改动**）。抽出原因：上述设计稿体积超过会诊工具的单文件上限，抽出后 §24 可独立会诊、冻结与实施。
> **交叉引用**：本文件中的 §2／§3／§4.1／§4.2a／§4.2c／§5.1／§6.1／§8／§12／§13.7／§13.10／§14／§17／§18.5／§19.4／§21.10／§22.5／§23.4／§23.8 等编号，均指原设计稿的对应章节。
> **效力**：抽出**不改变**任何条款效力；§24 的冻结 ≠ 生效，未完成 §24.20-D 落地清单与 §24.14 全部夹具前，**不恢复** §23.8 所述生产接线。
## 24. B3 外部启动生命周期补全设计（[2026-09-21 **已冻结**：第 23 轮会诊判「无必改项」，§17.4 收口；生产接线仍关闭]）

> **性质**：本设计只补 **E3/E4/E5 外部启动** 的受理后生命周期、取消分类、JobId 关联与生产组合根验收；**不改变**节点后继仲裁、R4 默认直通路径、`task.single.native=false` 或真实 User 目录合同。**生产接线在本文冻结并通过实现验收前继续关闭。**
>
> **冻结声明（[2026-09-21]）**：本章按 **GPT-5.6 sol / effort=medium** 会诊推进，按 §17.4「有必改项即处置→回归→复会诊」迭代至**第 23 轮判「无必改项」⇒ 本章冻结**（轮次与逐条处置见 §24.20-E，**冻结状态表为唯一来源**）。**冻结 ≠ 生效**：条款的**实现验收**仍按 §23.4／§17 门禁并集执行；**未完成 §24.20-D 落地清单与 §24.14 全部夹具前，不恢复 §23.8 所述生产接线**（`externalStartAdmission` 组合根注入、`_successorAdmissionWired`、`start_bgi(args)` 覆盖均保持关闭）。
>
> **编号说明**：小节号按**追加顺序**保留（§24.13–§24.19 为后续补充合同，§24.9／§24.10 为追加插入项），**不重排**以保证既有交叉引用（§24.1-6、§24.12-6、§24.15 等）继续可解析；**阅读顺序**按下表主题索引。
>
> **效力顺序**：与其他小节冲突时，**以 §24.15（唯一终态事务与恢复状态表）为先**，其次 §24.19（`unknown` 与权威终态）与 §24.20-A（版本口径）；任何小节不得与上述三者冲突。
>
> **主题索引（= 阅读顺序）**：

| # | 主题 | 小节 |
|---|---|---|
| 1 | 外部 Operation 的独立终局出口（非终态/终态受理、分阶段失败语义、容量释放） | §24.1 |
| 2 | 取消分类与责任保留 | §24.2 |
| 3 | JobId 与台账身份规则 | §24.3 |
| 4 | 生产组合根闭环验收要求 | §24.4 |
| 5 | 受理后终态取得责任链 | §24.5 |
| 6 | 结果与责任双维合同 | §24.6 |
| 7 | JobId／证据关联边界 | §24.7 |
| 8 | 操作类型与七项门禁映射 | §24.8 |
| 9 | 确定执行失败的结果合同 | §24.9 |
| 10 | 早期受理与完成观察接口 | §24.10 |
| 11 | 取消阶段矩阵（补充合同） | §24.11 |
| 12 | 待终局处置的持久化与恢复（补充合同） | §24.12 |
| 13 | 执行结果与责任分离（补充合同） | §24.13 |
| 14 | 早期受理接口（补充合同；句柄前窗口／屏障验收） | §24.14 |
| 15 | **唯一终态事务与恢复状态表**（补充合同；权威顺序） | §24.15 |
| 16 | 预观察记录 PreObservationRecord | §24.16 |
| 17 | OperationType 可信持久化 | §24.17 |
| 18 | `_gate` 两段事务 | §24.18 |
| 19 | `unknown` 与权威终态 | §24.19 |
| 20 | 冻结登记：版本口径／长期保守停驻总则／落地清单／会诊记录 | §24.20 |

### 24.1 外部 Operation 的独立终局出口

1. `SendOutcome.Accepted` 增加**远端 `JobId`**（无则 null，不得臆造）。**[纠正·2026-09-21 第五轮 N24-07] 不加 `Terminal` 标记**：终态事实**只**由完成层 `ExternalStartCompletion` 承载（§24.2-2″），不得在发送层再造第三套终态来源。
2. **受理（完成层尚未报终态）**：受理→接管台账落盘→关闭 Submission→外部 Operation 保持 `Accepted/Active`；台账继续为 `AcceptedPendingExecution/Running` 并继续占用。**不得**转 `TerminalCompleted`。
3. **终态（完成层报 `Succeeded`／`Cancelled`／`ExecutionFailed`）且接管一致性验证通过**：受理→接管台账落盘→**同一次权威发布写 `ExecutionResult` ＋ `PendingTerminal`**→台账转 `Terminal`→关闭 Submission（或按已提交关闭事实确认关闭）→外部 Operation `Accepted→TerminalCompleted→TerminalPendingTransfer→Tombstone`；主槽位只在迁移成功后释放。墓碑已满时允许独立终局但保留 `TerminalPendingTransfer`，不突破容量。**[纠正·2026-09-21 冻结轮 B24-04]** 本行**不得**被读成「台账 Terminal 先于 `PendingTerminal`/`ExecutionResult`」——完整顺序以 **§24.15** 为唯一规范（仅**成功**路径的先行步骤在此列出）。**即刻完成**（如 v2 阻塞式 `task.start`）亦走本行：适配器以**已完成的 `CompletionTask`** 表达，不得另立第三套终态载体。
4. **分阶段失败语义**：关闭前失败保留原 Submission；关闭已提交但 Operation 终局失败时保留 `Accepted` 并走独立终局恢复，**不得重建 Submission**；台账 Terminal 已提交后关闭失败时保留原责任并读回验证，不得把已提交事实回滚成未提交。
5. 终局迁移必须复核完整发送身份、已提交关闭事实与无更新责任；**不得**在已持 `_gate` 的结果处理路径直接调用会再次 `_gate.Wait()` 的 `MarkOperationTerminal`，终局化须经无重入的串行入口/锁外调度。
6. `TakeoverTerminalConfirmed` 仅对**持久化操作类型为 E3/E4/E5 外部启动**的 Operation 查 `ExternalStartLedger`：仅当同一 `submissionIdentity+sendSeq` 记录为 `Terminal` 才返回 true；不得用运行快照或“查询未命中”代替，也不得依据“无 RunBinding”推断外部类型。
7. 门面初始化/重启恢复必须按**持久化阶段**处理：仅有已提交关闭事实且无 Submission 时，允许补 Operation 终局；仅扫描到台账 Terminal 但关闭尚未提交时，必须先完成关闭，**不得越过关闭事务直接补终局**。发布结果不明时先读回验证。
8. 连续 32 笔以上已完成外部启动后，主槽位必须随终局与迁移释放；必须新增容量回归夹具证明后续启动仍可获准。

### 24.2 取消分类与责任保留

1. `CommandResult` 增加 `IsTerminal`（仅权威终态返回 true；v2 仅“发送成功”不得置 true）与 `JobId`（ext 队列的 `taskHandle`，无则 null）。
2. **[纠正·2026-09-21 冻结轮 B24-08/B24-09；第四轮 N24-04 改两层；第五轮 N24-07 去 `Terminal` 并冻结唯一映射]** 外部启动结果**按阶段分为两个判别式类型**，不再用一组布尔位，也不让同一类型同时承载「未受理」与「执行终态」：
   - **发送层** `ExternalStartExecution`（适配器 → 门面，**既有类型改造**）：`Kind ∈ {Accepted, Rejected, Unknown}` ＋ `JobId` ＋ `Reason` ＋ **[第七轮 N24-11] `Retryable`（`Rejected` 时必填语义：可否重试）＋ `EvidenceSource`（原始证据词/产生端）**（工厂 `AcceptedWith`／`RejectedWith(reason, retryable, evidenceSource)`／`UnknownWith`；原 `Uncertain` 布尔由 `Kind=Unknown` 取代）。**无 `Terminal` 字段**（[N24-07]）。与 `SendOutcome` **一对一、字段级无损**：`Accepted(JobId)`／`Rejected(reason, retryable, evidenceSource)`／`Unknown(detail)`。
   - **完成层** `ExternalStartCompletion`（**新增**；由 §24.14-1 的 `CompletionTask`／等价权威观察依据承载）：`Kind ∈ {Succeeded, Cancelled, ExecutionFailed, Unknown}` ＋ `RawTerminal`（原始终态词）＋ **[第七轮 N24-12；第十轮 N24-23 改名] `ExecutionErrorCode`（`ExecutionFailed` 必填；其余可为空）** ＋ `JobId` ＋ `EvidenceSource`。**不含 `Rejected`**——受理之后不存在「未受理」，从**类型层**杜绝「先受理、后未受理」的事实反转（[N24-04]）。
   - **同一发送身份只允许产生一次发送层结论**；`EarlyAccepted` 之后不得再出现发送层 `Rejected`／`Unknown`（[N24-07]）。
   - **门面内部** `SendOutcome` **[纠正·2026-09-21 第六轮 N24-07 残留]**：**严格冻结为发送层三态** `{Accepted(JobId), Rejected(reason, retryable, evidenceSource), Unknown(detail)}`，与 `ExternalStartExecution` **一对一**；`Accepted` 增加 `JobId`（**不加 `Terminal`**）；**不含 `Cancelled`**。完成层事实（`Succeeded`／`Cancelled`／`ExecutionFailed`／`Unknown`）**只**经完成结算通道进入门面：新增结算入口 `SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq, ExternalStartCompletion)`，其内部结算结论类型 `CompletionSettlementOutcome ∈ {Succeeded, Cancelled, ExecutionFailed, Unknown}` 映射到 `AdmissionResultKind`（`Succeeded→Accepted`／`Cancelled→Cancelled`／`ExecutionFailed→ExecutionFailed`／`Unknown→Reconciling`／`NeedReconcile`）与 `ExternalStartAdmissionStatus`（§24.9）。`AdmissionResultKind` 增加 `Cancelled`。**[第八轮]** `CompletionSettlementOutcome.Unknown` 的 `Reconciling`/`NeedReconcile` **仅是「对外结论」**，**不改变**已确认受理 Operation 的 `Accepted/Active` 责任状态（§24.19-2 第二种情形）。
   - **适配器对外映射（一对一，不合并语义）**：发送层 `Rejected→Rejected`、`Unknown→NeedReconcile`（对外 `Unknown`）、`Accepted→Accepted`；完成层 `Succeeded→Accepted`、`Cancelled→Cancelled`、`ExecutionFailed→ExecutionFailed`、`Unknown→NeedReconcile`。`ExternalStartAdmissionStatus` 增加 `Cancelled` 与 `ExecutionFailed`（**保留** `Blocked`＝前置门禁阻断、未进入执行结果维）。

**2″. 三层载体的唯一转换表与非法序列（[新增·2026-09-21 第五轮 N24-07]）**

| 早期回复 `ExternalStartReply`（§24.14-1） | 发送层结论 `ExternalStartExecution` | 完成层结果 `ExternalStartCompletion` |
|---|---|---|
| `EarlyAccepted(jobId, CompletionTask)` | **`Accepted(jobId)`**（唯一映射；不得再产出 `Rejected`/`Unknown`） | 由 `CompletionTask` 稍后交付或**已完成**：`Succeeded`／`Cancelled`／`ExecutionFailed`／`Unknown` |
| `Rejected(reason, retryable, evidenceSource)` | `Rejected(reason, retryable, evidenceSource)`（字段无损） | **不产生**（无受理＝无完成层事实） |
| `Unknown(detail)` | `Unknown(detail)`（**只此一次**：同一发送身份**不得**再产生第二个发送层结论） | 本次早期调用**不附带**完成层；观察责任按 §24.14-6 保留。后续查明**曾受理** ⇒ 必须走 `ReconcileSettlement.Accepted(JobId, ExternalStartCompletion?)` 对账通道（**不得**再创建第二个发送层 `Accepted`）；查明**确定未受理** ⇒ 走既有对账拒绝结算分支 |

**非法序列（构造层或实现层必须拒绝）**：①`EarlyAccepted` 后出现发送层 `Rejected`／`Unknown`；②同一发送身份出现**第二次**发送层结论（含「`Unknown` 后补一个 `Accepted`」）；③把发送层 `Accepted` 与完成层 `Unknown` 同时当作终态（只有完成层 `Succeeded/Cancelled/ExecutionFailed` 是权威执行终态，§24.19）；④以「即刻完成」为名另立第三套终态载体（即刻完成必须以**已完成的 `CompletionTask`** 表达）。

**`Rejected` 后的冲突性证据（[新增·2026-09-21 第六轮；第七轮 N24-09 改为路由规则＋持久化责任状态]）**

- 已产生发送层 `Rejected`（确定未受理）后又出现指向**曾受理/终态**的证据时：**不得**按「非法序列」丢弃证据，**不得**走普通 `SettleCompletionAsync`，必须**原样路由至冲突对账**。
- **冲突责任状态（必须持久化，可跨重启）**：`OperationRecord` 新增加法字段 `conflict`（冲突证据集合：双方原始终态词/证据来源/完整发送身份/记录时点）。
- **入口适用（原子迁移，不覆盖既有事实）**：该证据可能落在 `RetryableRejected`／`TerminalRejected`／`TerminalPendingTransfer`／`Tombstone` 任何状态之后——一律在同一权威串行发布内**追加** `conflict` 事实并置**冲突对账待决**标志：
  - **不重建** Submission（`Rejected` 路径已关闭的关系不得复活）；
  - **占用语义按到达时所在区域冻结**（[第八轮 N24-13]，**不修改** §4.1a 容量公式）：冲突到达时记录仍在 `Active`／`TerminalPendingTransfer` ⇒ **继续占主槽位**；记录**已在 `Tombstone`** ⇒ **不虚称重新占有已释放的主槽位**，改为「**墓碑受保护不可裁剪** ＋ 该执行的权威事实置为**冲突未知**并阻断冲突启动（全局 Unknown 占用）」；
  - **禁止重发**（重试资格立即失效，`RetryableRejected` 的窗口到期不得据此转终局中止）；
  - §24.12-3 恢复扫描必须覆盖带 `conflict` 的记录，并在裁决完成前保留唯一恢复依据。
- 裁决入口与**四分支**（[第八轮 N24-13；第九轮 N24-15 拆分首分支]，唯一权威串行发布内原子写入）：`SettleReconciledAsync` 的冲突分支——
  ①**确认曾受理且已有权威终态** ⇒ 按接管证据＋§24.15 唯一顺序完成结算，随后在同一权威发布内**追加 `Resolution=ResolvedAcceptedTerminal` 的审计项 ＋ 把 `OperationRecord.ConflictResolutionAuditId` 指向该项 ＋ 清除活动覆盖层 `pending`**（**历史拒绝仅作审计**，分类读取以匹配的 `ExecutionResult` 为权威——见下「裁决审计的持久化模型」）；
  ②**确认曾受理但尚无权威终态** ⇒ 持久化 `ConflictResolutionState=AcceptedAwaitingTerminal`：**不迁回 `Active`**、**不申请主槽位**、**不清除**墓碑保护与冲突证据，`ResponsibilityState=Pending`，终态到达后再按 §24.15 结算并清冲突；
  ③**确认未受理** ⇒ 保留原拒绝终局，在同一权威发布内完成**四项**（[第二十轮 N24-37]）：①**追加或幂等确认 `ReconciledNotAcceptedEvidence` 证据记录**（重试复用同一 `evidenceId`）＋②**追加或幂等确认引用该 `evidenceId` 的 `ResolvedNotAccepted` 审计项**（重试复用同一 `auditId`）＋③**写入 `OperationRecord.ConflictResolutionAuditId`** ＋④**清除活动覆盖层 `pending`**（解除阻断），`ResponsibilityState=Settled`；**四项缺一＝视为未提交**（读回后以同一 `auditId`／`evidenceId` 重试）；
  ④**仍不可考** ⇒ 继续保留冲突责任与证据（长期保守停驻，§24.20-B），`ResponsibilityState=Pending`。
- **[第九轮 N24-15] 冲突事实必须进入可执行数据流**：带 `conflict`（含 `AcceptedAwaitingTerminal`）的记录必须汇入准入事实面的**执行事实未知/占用**（`ArbitrationFacts.ExecutionFactsUnknown` 或等价占用来源，§14/§17 P51 三源并集），从而**阻断冲突启动**；分类读取必须让**冲突状态覆盖历史 `TerminalRejected` 结果**（不得返回已作废的拒绝结论）。**只有**权威裁决才能清除 `conflict` 并解除占用/阻断。
- **[第十轮 N24-21] `conflict` 是与 `OperationZone` 正交的「对账责任覆盖层」**：原拒绝操作**仍是 `Tombstone`**（**不迁回 `Active`**、**不占主槽位**），冲突责任**单独**处于 `Pending`；受保护墓碑**不计主槽位但禁止按保留策略裁剪**——本条是对 §4.1a 墓碑清理规则的**显式例外**（**不修改** §4.1a 容量公式 `Active + TerminalPendingTransfer ≤ 32`），并同时通过全局 `ExecutionFactsUnknown` 阻断冲突启动。
- **[第十一轮 N24-24；第十二轮 N24-25 改为独立审计载体；第十四轮 N24-27 统一命名] 裁决审计的持久化模型**：**权威审计载体＝租约 v3 新增追加式 `ConflictResolutionAudits[]`**（条目不可变）。该集合**不计入** §4.1a 的 32 主槽位与 256 墓碑容量（避免「永久保护墓碑」造成容量永久耗尽）；其保留期裁剪归 R5.6 迁移/清理统一裁决，**本批不做裁剪**（无界代价如实登记）。**字段命名唯一（不得使用 `OperationRecord.ConflictResolution`／`SupersededResultRef` 等旧称）**：`OperationRecord.ConflictResolutionAuditId` **仅保存 ID 引用**；审计项内嵌 `supersededRejectedResultSnapshot`（被取代的拒绝结果快照）与 `resolutionEvidenceSnapshot`（裁决依据侧证据快照），**不得**指向可能随墓碑删除的 `LastResult`。
  - **最小审计结构（唯一保留定义；[第十五轮] 版本已去重，不得再生第二组同名条款；[第二十一轮 N24-38] `resolutionEvidence*` 为**判别式**且两分支互斥；[第二十二轮 N24-40] **删除顶层 `evidenceSource`**——分支事实来源只存在于分支载荷内，避免证据双写）**：`auditId` ＋ `requestIdentity` ＋ `submissionIdentity` ＋ `sendSeq` ＋ `resolution` ＋ `resolvedAtUtc` ＋ `supersededRejectedResultSnapshot` ＋ **`resolutionEvidenceSnapshot`（仅 `ResolvedAcceptedTerminal` 使用：完整终态证据快照，内含该分支的 `evidenceSource`）** ＋ **`resolutionEvidenceRef`（仅 `ResolvedNotAccepted` 使用：`{ evidenceId }`，**不含**内嵌字段）**。**两字段严格互斥**：与 `resolution` 不匹配、或两字段**同时出现**，均视为损坏（fail-closed）。
    - `supersededRejectedResultSnapshot` 固定字段：`Outcome=Rejected`／`AnsweredSendSeq`／拒绝类别（可重试或终局）／`ReasonCode`／`EvidenceSource`／完整发送身份。
    - `resolutionEvidenceSnapshot` 按裁决类型冻结为**封闭字段表**（[第十六轮 N24-30-R；第十七轮 N24-30-R-R/N24-33] 不得再写「至少含」、**不得自引用**）：
      - `ResolvedAcceptedTerminal` ⇒ 匹配的权威终态结果快照（完整发送身份／终态类型／原始终态词／`ExecutionErrorCode`／JobId／证据来源／`ObservedAtUtc`），**业务字段与匹配的 `ExecutionResult` 逐字段一致**，`ObservedAtUtc` 与 `ExecutionResult.ObservedAtUtc` 全等；
      - `ResolvedNotAccepted` ⇒ **独立不可变证据记录 `ReconciledNotAcceptedEvidence[]`（租约 v3 新增；[第十七轮 N24-30-R-R；第二十轮字段名显式化；第二十一轮 N24-38 引用化；第二十二轮 N24-40 定为**权威观察事件的唯一持久化载体**]）**：含独立 `evidenceId` ＋ 封闭表**全部**字段（`submissionIdentity`／`sendSeq`（＝轮次口径，**不另立 `round` 等第二字段**）／权威未受理事实类型／原始证据词／证据来源／`ObservedAtUtc`）。**该记录即权威观察事件的持久化载体**：四项事务**写入前**校验输入观察事件（来源可信、字段完整、与发送身份匹配）；**提交后**的完整性检查**只**校验 `evidenceId` 唯一性、载荷不变性与身份关联，**不再要求查找第二份观察事件**（因此重启后仍可执行）。审计项**只保存 `resolutionEvidenceRef={evidenceId}`**（**不内嵌**上述字段）；唯一引用＝`evidenceId`（另需与审计项、Operation 的 `submissionIdentity`／`sendSeq` 一致）。
  - **引用完整性校验（可执行判据；墓碑清理前置与分类读取共用；[第二十一轮 N24-38] 判别式两字段互斥）**：`auditId` 非空且在 `ConflictResolutionAudits[]` 内**唯一**；重试**必须复用同一 `auditId`**；**同 ID 同载荷＝幂等**，**同 ID 异载荷＝文件损坏并 fail-closed**；Operation 的引用必须**唯一命中**一个审计项，且 `requestIdentity`／`submissionIdentity`／`sendSeq` 与 Operation 完全一致（**Operation 侧只存 ID，故不比较 Operation 的裁决类型**）；**拒绝快照逐字段核对（两分支均适用，[第十六轮 N24-30-R]）**：`supersededRejectedResultSnapshot` 的 `Outcome`／`AnsweredSendSeq`／拒绝类别（`Retryable`）／`ReasonCode`／`EvidenceSource`／`submissionIdentity`＋`sendSeq`，必须与**事务开始前不可变的拒绝结果**或 `conflict` 内保存的拒绝侧证据**逐字段相等**。**分支兼容矩阵**：
    - `ResolvedAcceptedTerminal` ⇒ 必须存在**同 `submissionIdentity`、同 `sendSeq`** 的权威终态 `ExecutionResult`，且 `resolutionEvidenceSnapshot` 与其**业务字段逐字段一致**、`ObservedAtUtc` 与其 `ObservedAtUtc` 全等；**该分支不得出现 `resolutionEvidenceRef`**；
    - `ResolvedNotAccepted` ⇒ 原拒绝终局仍有效（其身份／`sendSeq`／结果与拒绝快照一致）；**取 `resolutionEvidenceRef.evidenceId` 唯一命中** `ReconciledNotAcceptedEvidence`（该记录**即**权威观察事件载体，[第二十二轮 N24-40]），校验其载荷不变性与 `submissionIdentity`／`sendSeq` 与审计项及 Operation 一致；**该分支不得出现 `resolutionEvidenceSnapshot`**。
    校验不通过（**分支必需载荷缺失**：`ResolvedAcceptedTerminal` 缺 `resolutionEvidenceSnapshot`、或 `ResolvedNotAccepted` 缺 `resolutionEvidenceRef`；引用缺失、多发命中、身份/`sendSeq` 不符、两字段同时出现、拒绝快照字段不等、证据记录载荷/身份不符）⇒ **不得清理墓碑、不得解除阻断**，按损坏保守处置并留痕。

**`ObservedAtUtc` 的唯一语义与两条独立链（[第十七轮 N24-33；第十八轮 N24-36 拆链]）**：`ObservedAtUtc` ＝**该项权威证据首次被可信观察层接收的时点**，**捕获一次后不可改写**。**禁止把两个互斥事实域写成同一条链**：

- **权威执行终态链**：`ExternalStartCompletion.ObservedAtUtc → PendingTerminal.ObservedAtUtc → ExecutionResult.ObservedAtUtc → ResolvedAcceptedTerminal 审计快照.ObservedAtUtc`（四者取值**全等**）。
- **权威未受理链**：不可变观察事件的 `ObservedAtUtc → ReconciledNotAcceptedEvidence.ObservedAtUtc`；审计项**只保存 `evidenceId`**，按该证据记录校验（**不再**声称未受理事实贯穿审计快照的 `ObservedAtUtc`）。

**台账终态副本字段（[第十八轮 N24-36]）**：台账新增 `TerminalObservedAtUtc`（终态副本的观察时点）；**`TerminalAtUtc` 仍定义为台账落盘时点、由落盘时钟生成，二者不得复用**。`MarkTerminal` 签名扩为**接收既有 `observedAtUtc`**：首写保存该值，幂等重试必须**严格比对**既有值与本次传入值（不一致＝损坏/冲突，fail-closed 并留痕）。**本条删除**「台账时间／观察事件二选一」的未决口径。

**`evidenceId` 完整性判据（[第十八轮 N24-35；第十九轮去掉「轮次」重名]）**：`evidenceId` 非空且在 `ReconciledNotAcceptedEvidence[]` 内**唯一**；重试**必须复用同一 `evidenceId`**；**同 ID 同载荷＝幂等**，**同 ID 异载荷＝损坏并 fail-closed**；`ResolvedNotAccepted` 的审计引用必须**唯一命中**一条证据，且 `submissionIdentity`／`sendSeq`（＝该证据的轮次口径，**不另立第二套「轮次」字段**）／证据类型**逐字段相符**；校验不通过 ⇒ 不得清 `pending`、不得解除阻断。
  - **裁决事务（[第十三轮 N24-26] 三项原子变更；[第十七轮 N24-30-R-R] `ResolvedNotAccepted` 分支扩为**四项**，缺一不可）**：同一权威发布内「①**追加或幂等确认审计项**（重试必须复用同一 `auditId`）＋②**写入 `OperationRecord.ConflictResolutionAuditId`** 指向该项 ＋③**清除活动 `conflict.pending` 标志**」；`ResolvedNotAccepted` 分支**另加**②′「**追加或幂等确认 `ReconciledNotAcceptedEvidence` 记录**（重试必须复用同一 `evidenceId`）」——**四项缺一即视为未提交**（读回验证后重试）；**「清除冲突」一律只指清除活动覆盖层 `pending`**，**不删除**审计、证据记录与双方证据。
  - **分类读取**：`ResolvedAcceptedTerminal` 时以**匹配的 `ExecutionResult`** 为权威（历史 `Rejected` **仅作审计**）；`ResolvedNotAccepted` 时原拒绝终局继续有效。
  - **§4.1a 墓碑清理前置（显式例外，[第十四轮 N24-29] 仅限曾进入冲突覆盖层的 Operation）**：**曾进入 `conflict` 覆盖层**的 Operation——`conflict.pending=true` 时**禁止清理**；裁决后仅在 `ConflictResolutionAuditId` 唯一命中且通过上方完整性校验时允许清理。**未曾进入冲突覆盖层的墓碑完全按 §4.1a 原规则清理**（不得被本条扩大为「所有墓碑都需审计引用」）。**§24.12-3 的第四类恢复集合只扫描 `conflict.pending=true`**——已裁决记录不再阻断准入（墓碑仍在 `Tombstone`，不占主槽位）。

**初次回复与后续对账（补充表，[新增·2026-09-21 第六轮；建议项已并入]）**

| 情形 | 首次产生的发送层结论 | 后续取得的证据 | 允许的处置 |
|---|---|---|---|
| 拿到句柄 | `Accepted(JobId)` | 完成层结果（稍后/已完成） | 按 §24.15 完成结算；不得再造发送层结论 |
| 明确未受理 | `Rejected(reason)` | 无 | 按 §4.2c 第二分支关闭；可重试则按预算/窗口 |
| 未受理但证据冲突 | `Rejected(reason)` | 指向曾受理/终态 | **冲突对账**（保留证据、保持责任、不释放、不重发） |
| 结果未知 | `Unknown(detail)` | 对账确认曾受理 | `ReconcileSettlement.Accepted(JobId, ExternalStartCompletion?)`（**不再补发送层结论**） |
| 结果未知 | `Unknown(detail)` | 对账确认确定未受理 | 对账拒绝结算分支（**不再补发送层结论**） |
3. **关闭前取消**：若为**本地取消等待**或**关联不足/持久化失败**，保留未决责任并将 Operation 置 `Reconciling`；若已取得**权威取消终态**且接管/持久化各步成功，则完成关闭并按 §24.1 独立终局。入口均得到 `Cancelled`；不得走确定拒绝关闭、不得重发。若只是调用方停止等待而未观察到远端取消终态，不得伪造原始终态词。**[澄清·2026-09-21 冻结轮 B24-01]** 本款**不含**已形成合法「本地未发送证明」的发送前取消（该情形按 §24.11 第 3′ 行与 §4.2c **第二分支**处置）。**[第八轮 N24-14] 返回同时标注 `ResponsibilityState`（取值以 §24.6-5 唯一映射表为准）**：保留未决责任（`Reconciling`）⇒ `Pending`；权威取消终态且接管/持久化/关闭全部成功 ⇒ `Settled`。
4. **关闭后取消**：不得重建 Submission；Operation 保持已受理事实并写入独立的“待终局处置”记录（仅在已取得权威取消终态时记录原始终态词；仅有本地取消意向时记录 `local_cancel_requested` 并继续观察），入口仍得到 `Cancelled`；权威终态且接管一致时完成独立终局；无权威证据时允许长期保守停驻，但观察责任不得遗弃。**[第八轮 N24-14] `ResponsibilityState`（§24.6-5）**：已受理且待终局处置 ⇒ `Pending`；接管一致并完成独立终局 ⇒ `Settled`。
5. **[纠正·2026-09-21 复会诊／终轮 B24-10]** v2 路径中**观察到明确的取消事实**（响应/回执**原始词** `cancelled`，属证据词而非 `CommandResult.Status` 词位）→ 适配器置 `ExecutionDisposition=Cancelled`＋`IsTerminal=true`，并把原始词写入 `RawTerminal`／`EvidenceSource`（**线路词表不改**：`Status` 仍只用 `success/failed`），再走上述分阶段链；普通 v2 success/发送成功保持**非终态**，不得据此释放占用。
6. `not_found`、等待超时、通道瞬态失败一律 `IsTerminal=false`；不得作为终局证据。ext `Completed/QueueCancelled` 及权威 `failed` 才可置终态，并记录原始终态词。
7. **[新增·2026-09-21 冻结轮；第六轮 N24-07 收窄]** **取消不构成独立的关闭依据**：完成层 `Cancelled` 由 `SettleCompletionAsync` 结算，在提交责任维仍属 §4.2c **第一分支**（受理类关闭）；只有「可证实未发送」才属第二分支；**本地取消意向本身不构成任何关闭依据**（它只停止等待与批次推进）。`SendOutcome` 不设 `Cancelled` 分支（§24.2-2）。

### 24.3 JobId 与台账身份规则

1. `ExternalStartLedgerEntry` 增加加法字段 `jobId`（不改格式 `version`，不改变序列化框架）。
2. JobId 从 `CommandResult → ExternalStartExecution → SendOutcome.Accepted → ArbitrationAdmissionService → ExternalStartLedger` 全链传递；任何层不得丢弃已取得的句柄。
3. 同 `submissionIdentity+sendSeq` 重复接管：两侧均为空=幂等成功；一侧为空、一侧非空=补齐空值；两侧非空且不同=**拒绝并保守待对账**；两侧相同=幂等成功。
4. **[纠正·2026-09-21 第五轮 N24-07；第六轮 N24-08 补三分支]** `ReconcileSettlement.Accepted` 必须扩展携带 `JobId` 与**可选的完成层结果 `ExternalStartCompletion?`**（**不是**发送层 `Terminal` 布尔），使终态回写失败后的显式对账能按**同一发送身份**重试；旧字段调用保持兼容默认值（默认＝未提供完成结果）。三分支处置：
   - `Completion=null` ⇒ 接管落盘后按**普通受理**关闭，Operation 保持 `Accepted/Active`（§24.1-2），`ResponsibilityState=Pending`；
   - `Completion.Kind=Unknown` ⇒ **不得**生成 `PendingTerminal`；保留观察责任，普通受理关闭后保持 `Accepted/Active`，**对调用方返回 `NeedReconcile`（对外 `Unknown`）**（§24.19-2 第二种情形），`ResponsibilityState=Pending`；
   - `Completion.Kind=Succeeded/Cancelled/ExecutionFailed` ⇒ **先**原子写 `ExecutionResult`＋`PendingTerminal`，**再**台账 Terminal、**再**关闭、**最后** Operation 终局（§24.15 唯一顺序）；任一步未完成 ⇒ `Pending`，全部完成 ⇒ `Settled`。
5. 终态回写钩子缺失、抛异常或返回失败时：**关闭前**保持原 Submission 与 `Reconciling`；**关闭后**不得重建 Submission，Operation 保持 `Accepted` 并按 §24.12 写 PendingTerminal，由独立终局补写入口按完整发送身份重试，给出可对账原因。

### 24.9 确定执行失败的结果合同

1. 对外结果新增**确定执行失败**分支（与 `Rejected`、`Unknown` 分离），携带原始终态词/错误码、JobId 与证据来源。
2. 该分支只允许由权威执行终态产生；不得映射为“确定未受理”，也不得触发重发。
3. 失败结果按 §24.1 的非终态/终态顺序持久化：终态证据完整时完成接管与独立终局；证据不足时保留责任待对账。
4. 续用、合并调用与重启后必须返回同一失败事实与责任状态；补失败终态与接管异常后的结果保持夹具。

### 24.10 早期受理与完成观察接口

1. 外部适配层拆为**早期受理通知**与**完成观察**两段，两层各有独立类型、**不得互相代替**：`StartAsync` 在拿到早期回执/句柄后按 §24.14 返回 **`ExternalStartReply`**（`EarlyAccepted(jobId, CompletionTask)`／`Rejected`／`Unknown`）；**完成**结果由句柄 `CompletionTask`／等价权威观察依据承载，以 **`ExternalStartCompletion`**（`Kind ∈ {Succeeded, Cancelled, ExecutionFailed, Unknown}`，**不含 `Rejected`**，§24.2-2）表达；**发送层**结论（既有类型改造）＝ `ExternalStartExecution`（`Kind ∈ {Accepted, Rejected, Unknown}`，§24.2-2）。三者不是同一事实的副本：早期回复只证明「是否已受理」，发送层结论只证明「是否受理/确定未受理」，完成层结果只证明「执行是否已到终态」。
2. 门面只对早期句柄执行受理→接管台账→关闭 Submission，并按当前轮次发布；**不得在门面锁内 await 完成等待**（两段式 `_gate` 见 §24.18）。
3. `CommandExecutor` 在获准后等待完成结果以保持既有“任务完成后再推进批次”的行为；完成事实到达时经当前所有者/恢复扫描执行终局、取消或失败落盘。
4. 断线/换主后，恢复扫描按 §24.12 的三类持久化集合重新绑定 `CompletionTask` 或等价观察依据；无法重新关联的 v2/E4 路径保持关闭。

### 24.4 生产组合根闭环验收要求

以下场景必须由施工方内置、owner 0 点击；至少覆盖：

1. 真实 `MainViewModel → CommandExecutor → TaskCenterHost → Core` 组装下的**连续两次成功启动**，第二次不再被第一次未终局台账阻断。
2. ext `Completed` 后：台账 Terminal、外部 Operation TerminalCompleted、主槽位释放、JobId 可读。
3. ext `QueueCancelled/Completed(cancelled)` 后：入口返回 cancelled，批次停止；**本地取消等待/关联不足**时关闭前断言 Reconciling；**权威取消终态且持久化成功**时断言最终结清；关闭后断言不重建 Submission；只有取得权威终态且接管一致时才断言最终完成独立终局，否则保持观察。
4. v2 发送成功：入口 success，但台账保持未终局；v2 明确 cancelled：入口 cancelled；本地等待取消保留责任，权威取消终态且持久化成功后结清。
5. 终态落盘失败：关闭前保持 Submission/不释放占用；关闭后若 PendingTerminal 已存在则保持 Accepted/PendingTerminal 并由独立补写入口恢复；若 PendingTerminal 首写失败则保持 Accepted、观察责任仍在并重新取证；两种情况均不得重发。
6. 大于 32 笔外部启动完成后的容量与终局释放。
7. 冷启动/旧 v2、授权后纪元变化、切监控模式/宿主关闭交错各自有独立拒绝或待对账断言。

### 24.5 受理后终态取得责任链

1. **早期受理与句柄持久化**必须先于等待完成：拿到 `taskHandle/jobId` 后按完整发送身份写台账；不得等任务执行完才首次落盘。
2. **完成观察责任**归当前所有者；ext 事件快速路径与轮询安全网继续共用终态等待器，断线/换主后由恢复扫描按台账未终结记录重新建立观察。
3. v2/E4 等**没有可关联查询依据**的路径在早期受理与完成观察合同落地前继续关闭；不得以“发送成功”冒充终态。
4. `AcceptedPendingExecution/Running` 台账的解除条件只有**权威终态并完成接管一致性**；确定未受理只用于未决 Submission 的拒绝关闭，不得解除已受理台账。受理与未受理证据冲突时进入冲突对账，不得直接清除占用。

### 24.6 结果与责任双维合同

1. **对外结果维**：`Accepted/Cancelled/ExecutionFailed/Rejected/Unknown` 表达调用者可见事实；**前置门禁结论 `Blocked`（F11／票据压制／占用阻断等，未进入执行结果维）与执行结果并列时须显式区分**，不得混入同一词位；**内部责任维**：`Submission/Operation` 状态独立表达待结算责任。两维不得压成一个枚举。
2. 取消、失败、未知分支均须携带已取得的 `JobId`、原始终态词/错误码、证据来源与完整发送身份；后续持久化异常不得把已观察的取消/失败事实改写成普通 `result_unknown`。**载体落点（加法字段，[第七轮；第八轮 N24-14 补 `ResponsibilityState`；第九轮 N24-19 统一为 `ExecutionErrorCode`]）**：门面返回的 `AdmissionResult` 与适配器可见的 `ExternalStartAdmissionOutcome` 均须新增 `JobId`／`ExecutionDisposition`（`None/Cancelled/ExecutionFailed/Unknown`）／**`ResponsibilityState`（`None`＝不适用／`Pending`＝责任未结清／`Settled`＝已结清，取值以 §24.6-5 唯一映射表为准）**／`RawTerminal`／**`ExecutionErrorCode`**／`EvidenceSource`／`SubmissionIdentity`／`SendSeq`（默认空值/`None`，旧调用兼容），使「结果维 × 责任维」**贯通到调用方**（不得在适配器边界断链）。**名称唯一**：本链一律用 `ExecutionErrorCode`；`ErrorCode` 仅保留既有 BGI 信封语义（`CommandResult`）。
3. **[收窄·2026-09-21 冻结轮 B24-05]** 已取得合法终态者由当前所有者**持续保留结果与结算责任并重试持久化**，不得在无观察责任的情况下遗弃；**持久化或关联条件长期不成立时允许长期保守停驻（§24.20-B），禁止以超时/未命中/重启强制终局**；仅事实不可考者保留 Reconciling 待对账。
4. 续用、合并调用、重启后必须保持同一结果事实与责任状态；补续用、合并调用与重启保持断言。

**5. `ResponsibilityState` 唯一映射表（[新增·2026-09-21 第九轮 N24-17]；§24.2-3／4、§24.3-4、§24.2-2″ 一律引用本表）**

| 情形 | `ResponsibilityState` |
|---|---|
| 前置门禁阻断（F11／票据／占用）／未登记 | `None` |
| 确定未受理并完成关闭（可重试或终局） | `Settled` |
| 本地未发送证明完成关闭（`TerminalRejected`） | `Settled` |
| 普通受理已关闭、完成层尚未报终态 | `Pending` |
| 完成层 `Unknown`（已受理、执行结果未知） | `Pending` |
| 取得权威终态但关闭／台账／终局任一步未完成 | `Pending` |
| 完成层权威终态且结算全部完成（`TerminalCompleted`） | `Settled` |
| 冲突待决（含 `AcceptedAwaitingTerminal`） | `Pending` |
| 冲突裁决确认未受理并清除冲突 | `Settled` |
| 已登记、未占位且仍由既有处理者负责（§24.11 第 2 行） | `Pending` |
| 发送结果未知（`Reconciling`）／关联不足／持久化失败 | `Pending` |
| 确定未受理但关闭尚未提交 | `Pending` |
| 本地取消意向已记录、责任尚未结清 | `Pending` |
| `NotSelected`／本地校验拒绝／「三无」终局中止且已原子落盘 | `Settled` |

> **默认规则（[第十轮 N24-22]）**：**已登记之后**除「**已证明结清**」外**一律不得**返回 `None`；`None` 只用于**未登记**与**前置门禁阻断**（未进入执行与登记流程）。

### 24.7 JobId/证据关联边界

1. “记录合并成功”不等于“远端关联可重建”。JobId 补空必须同时验证固定目标 epoch、线上提交键与证据来源；重复接管不得把既有 `Terminal` 降回 `AcceptedPendingExecution`。
2. 无 JobId 的路径必须逐协议登记替代查询依据（线上提交键/权威快照等）；缺失则保留责任，不宣称接管可重建。
3. 事件、轮询、v2 响应、`already_executed`、热键应答分别登记可证明的事实范围；`not_found`、超时、通道瞬态不进入终态证据。
4. 必须补错 epoch、错轮次、冲突 JobId、迟到活动态不得降级 Terminal 的反例。

### 24.8 操作类型与七项门禁映射

1. 外部启动识别**只按持久化的实际操作类型/来源记录**；`flow:`/`run:` 缺绑定或绑定损坏必须阻断，未知类型不得降级为外部启动。
2. §24 验收通过**不替代** §23.4/§23.8/§17 的门禁并集；上轮七项阻断未在本章逐项解决者继续保留门禁。
3. 组合根验收必须使用真实 `MainViewModel → CommandExecutor → TaskCenterHost → Core` 组装，仅替换外部传输；同时验证默认未注入路径、控制热键、冷启动/旧 v2/重试/取消行为保持。
4. 真实 F11 事实源、运行台账占用合并、授权 epoch 贯穿实际发送、能力复核、在飞跟踪与取消令牌生命周期，未闭合前不得恢复生产接线。

### 24.11 取消阶段矩阵（补充合同）

| # | 阶段（Operation 是否存在） | 本地取消等待（未观察权威取消终态） | 权威取消终态（已观察） |
|---|---|---|---|
| 1 | **未登记**（无 Operation、无 Submission） | **不创建** Operation/Submission；入口按本地取消结束；**不写**任何远端终态证据 | 不适用（无对象）；若上一轮已受理，按第 3 行处置 |
| 2 | **已登记、未占位** | Operation 保持 `Queued`/既有非终局状态，由既有处理者结束；**不得**据此删除登记事实，也不得在无「三无」复核时终局中止 | **不适用**（未占位＝本操作无**当前轮**发送责任）；若证据属于该操作的**既有发送轮**，**按该轮当前阶段**进第 3／4／5 行（该轮未关闭→第 3 行；已关闭→第 4 行；关闭结果不明→第 5 行）——**不得**对不存在的当前轮 Submission 执行关闭 |
| 3 | **占位后、关闭前（可能已发送）** | Submission/Operation 保持 `Reconciling`；入口 `Cancelled`；记录 `local_cancel_requested` 并继续观察 | 先完成接管与关闭，再按 §24.15 终局事务独立终局；入口 `Cancelled` |
| 3′ | **占位后、关闭前**（已形成合法本地未发送证明） | 按 §4.2c **第二分支**关闭并终局中止（**不写**远端终态词）；入口 `Cancelled` | **[纠正·2026-09-21 复会诊轮 N24-02]** 同一发送身份若出现权威终态 ⇒ **本地未发送证明失效**，转第 3 行（先接管、后关闭、再终局）；若证据属**其他轮次/其他身份** ⇒ 拒绝关联并保守对账；并发到达时按**完整发送身份**判定，不得笼统称「证据更强」 |
| 4 | **关闭后** | 不得重建 Submission；Operation 保持 `Accepted`；记录本地取消意向并继续观察 | 接管一致后按 §24.15 完成独立终局；入口 `Cancelled`，最终责任已结清 |
| 5 | **关闭结果不明** | 读回验证：未提交→按第 3 行；已提交→按第 4 行；仍不可确认→`Reconciling` | 已提交关闭＋接管一致→补终局；仅确认关闭但终局补写失败→`Accepted`＋`PendingTerminal`；未确认→`Reconciling` |

**约束**：本地取消不得冒充远端取消终态；权威取消终态不得被后续持久化异常抹掉；两种取消都必须停止批次推进，但责任结清条件不同。**[纠正·2026-09-21 冻结轮 B24-01/B24-12；第七轮 N24-09 修正载体名]** 「本地未发送证明」须满足 AMD-1-4 的关联验证（发送身份/epoch 全等 ＋ 发送路径已停止 ＋ 持久化关联），否则一律按第 3 行「可能已发送」处置；**完成层 `Cancelled` 不构成第三种关闭依据**（§24.2-7；`SendOutcome` 本身不含 `Cancelled`，§24.2-2）。

**本地未发送证明的终局状态（[新增·2026-09-21 第四轮 N24-06]）**：第 3′ 行与 §24.15「已形成合法本地未发送证明」行完成的关闭，Operation 终局状态为 **`TerminalRejected`**（`OperationResult.Outcome=Rejected`，`EvidenceSource` 标注本地未发送证明来源），**不得**写 `TerminalCompleted`；**不得**生成 `ExecutionResult` 的权威终态，**不得**写 `PendingTerminal`（§24.19-3：`TerminalCompleted` 必须同时存在**权威终态结果**与关闭事实，本地未发送证明不是执行终态）。**[第七轮 N24-09]** 该路径之后若出现指向曾受理/终态的冲突证据，按 §24.2-2″「冲突责任状态」处置（追加 `conflict`、禁止重发、不释放占用）。

### 24.12 待终局处置的持久化与恢复

1. **权威载体**：`OperationRecord` 增加加法字段承载 `PendingTerminal`（原始结果词／**`ExecutionErrorCode`**（[第十轮 N24-23] 统一命名）、JobId、证据来源、完整发送身份、记录时点）；`ExternalStartLedger` 承载同一终态证据的对外可读副本。因这些字段承载责任事实，租约格式 `version` **升至 3**；旧 version 2 消费者必须 fail-closed 拒绝读写，不得静默忽略后回写。序列化框架本身不变。
2. **发布顺序**：先持久化 `OperationRecord.PendingTerminal`，再写台账 Terminal，再关闭/确认关闭，最后转 Operation `TerminalCompleted`。若第一步失败，保持 Reconciling；若后续失败，恢复扫描按已落盘的 PendingTerminal 继续。
3. **恢复扫描集合（[第八轮 N24-13] 增第四类）**：①仅 Submission 未关闭；②未终结台账记录；③台账已 Terminal 但 Operation 尚未终局；④**`conflict.pending=true` 的全部记录（含受保护墓碑）**。恢复必须按完整发送身份关联，不得新建替代 Submission；第四类在裁决完成前**不得**被清理或裁剪。
4. **关闭后补写入口**：独立于 `SettleReconciledAsync`（后者要求 Submission 在册），仅接受已提交关闭事实 + PendingTerminal + 无更新发送责任；失败保持 Accepted/PendingTerminal，不释放主槽位。
5. **证据未持久化且远端不可查询**：保守记为不可考并保留责任；不得承诺重启后返回同一终态事实。清理前不得删除唯一的 PendingTerminal 恢复依据。
6. **首步失败分阶段**：关闭前 PendingTerminal 首写失败=保持 Submission 并在活进程内有界重试，重启后先查同发送身份的已知/可查询证据；关闭后首写失败=Operation 保持 Accepted，由在飞责任继续重试或按 §24.14 的观察依据重新取证，禁止靠不存在的 PendingTerminal 恢复。
7. **原子投影**：PendingTerminal 与 `ExecutionResult` 必须在同一权威串行发布中写入或具有可恢复投影关系。**合法中间态**＝`ExecutionResult` 已终态＋PendingTerminal 已持久化＋Operation 仍 `Accepted`（接管/关闭/终局尚未完成）；只禁止①责任已 `TerminalCompleted` 但 `ExecutionResult` 缺失，②`ExecutionResult` 已终态但既无 PendingTerminal、也无预观察/查询恢复依据。发布返回结果不明时先读回验证；终局事务必须同时验证匹配的结果与关闭事实。

### 24.13 执行结果与责任分离（补充合同）

1. **执行结果字段**：新增 `ExecutionResult`（`succeeded/failed/cancelled/unknown` ＋ `RawTerminal`／**`ExecutionErrorCode`**（[第十轮 N24-23] 统一命名，与 `CommandResult.ErrorCode` 的信封语义分离）／`JobId`／`EvidenceSource`／完整发送身份／**`ObservedAtUtc`**（[第十六轮 N24-30-R] 权威观察时点，供裁决审计快照逐字段对齐）），与 `OperationRequestState` 分列保存。`TerminalCompleted` 只表示责任结清，不表示成功。
2. **门面结论**：`ClassifyCurrentState`、续用与恢复读取 `ExecutionResult`：failed 返回确定执行失败，cancelled 返回取消，unknown 返回待对账；不得把 `TerminalCompleted` 一律返回 Accepted。
3. **合并/续用**：结果合并按完整发送身份；迟到活动态不得降级既有终态结果；冲突结果保守待对账。
4. **失败终态**：执行失败且接管一致时走独立终局；执行事实确定但接管关联不完整时保持责任待对账，不得直接释放。
5. **本地取消意向单独承载**：`local_cancel_requested` 只表达调用方停止等待与批次停止，不写入 `ExecutionResult.cancelled`。后续远端 succeeded/failed/cancelled 仍以远端结果为准。
6. **续用/恢复读取顺序**：若权威 ExecutionResult 已存在，返回该结果并按责任状态对账；若仅有本地取消意向，返回“调用已取消、远端结果待观察”，不得把本地取消当成远端终态。

### 24.14 早期受理接口（补充合同）

1. **启动返回联合类型**：`ExternalStartReply` ∈ `{ EarlyAccepted(jobId, CompletionTask), Rejected(reason, retryable, evidenceSource), Unknown(detail) }`（[第八轮 N24-11] 补齐 `retryable`／`evidenceSource`，以与发送层字段无损对应）；无句柄的早期响应一律 Unknown，不得凭空生成句柄；**与发送层/完成层的唯一映射与非法序列见 §24.2-2″**。
2. **门面锁边界**：占位提交后必须先释放门面 `_gate`，再调用适配层 `StartAsync`/早期网络取证；结果回到权威串行边界后重新校验完整身份、租约代次与关闭前置。
3. **观察器所有权**：`CompletionTask` 的等待器/订阅必须在启动前建立；句柄随 `EarlyAccepted` 交给门面与 `CommandExecutor` 共同引用。接管失败、调用方退出、合并调用均须把观察责任交给当前所有者或恢复扫描，不得随 `using` 提前释放。
4. **[纠正·2026-09-21 第六轮 N24-08]** **交错**：完成先于接管/关闭时，结果**先按完整发送身份暂存**；接管事实持久化后，若暂存结果为**权威终态**，必须**严格执行 §24.15 的唯一顺序**（`ExecutionResult`＋`PendingTerminal`→台账 Terminal→关闭→Operation 终局），**不得先关闭 Submission**；暂存结果为 `Unknown` 时按 §24.3-4 第二分支处置。调用方未拿到句柄即退出时，由恢复扫描按 `PendingTerminal`/台账关联接管观察责任。
5. **屏障验收**：`CompletionTask` 未完成时其他请求必须能取得门面锁；提前完成、接管失败、取消、退出、换主五种交错不得丢失观察责任。
6. **句柄前窗口**：发送前必须先按固定 epoch/线上发送键持久化“预观察记录”；适配器负责在拿到句柄前继续保持观察。若崩溃时仅有预观察记录、远端可查询则恢复观察，不可查询则保持 Unknown；**没有可查询依据的协议路径不得启用**。
7. **验收**：补“拿到句柄前调用方退出/进程崩溃”的退出与恢复夹具；观察责任在句柄落盘前不得随调用方终止。

### 24.15 唯一终态事务与恢复状态表（补充合同）

以下顺序为唯一规范；其他小节与本节冲突时以本节为准：

| 阶段／到达顺序 | 权威终态已到 | 处理顺序 | 失败时状态 |
|---|---|---|---|
| **登记后、未占位** | 否 | **仅在权威串行边界证明「从未发布发送许可 ＋ 无当前处理者 ＋ 无更新责任 ＋ 确定不再处理」时**，才在同一权威事务终局中止 Operation；否则保持 `Queued`/合并既有处理者，不创建 Submission | 保持登记事实并重新读回 |
| **占位后、尚未发送（未形成合法本地未发送证明）** | 否 | 本地取消→保持 Submission/Operation `Reconciling`（记录 `local_cancel_requested`）并继续观察；**不得**伪造未受理 | 见 §24.12-6 |
| **占位后、尚未发送（已形成合法本地未发送证明）** | 否 | 按 §4.2c **第二分支**关闭并终局中止——Operation 终局状态＝**`TerminalRejected`**；**不写**远端终态词、**不生成** `ExecutionResult`／`PendingTerminal`（§24.11「本地未发送证明的终局状态」） | 关闭失败＝保持原责任并读回验证 |
| **占位完成且 PreObservation 已提交、尚无句柄** | 否 | 按既有 PreObservation 继续观察；**不得在发送之后补建 PreObservation** | 发送前 PreObservation 发布失败＝**禁止发送**；崩溃按 PreObservation 恢复 |
| 早期受理、未关闭 | 是 | 写 PendingTerminal/ExecutionResult→台账 Terminal→关闭→Operation 终局 | 首写失败按 §24.12-6；关闭失败保留原责任 |
| 已关闭、终态随后到 | 是 | 写 PendingTerminal/ExecutionResult→台账 Terminal→Operation 终局 | **首写失败＝保持 `Accepted` 并保留观察依据（PreObservation／台账／可查询依据）重试取证**；只有读回确认 `PendingTerminal` 已提交时才记 `Accepted`＋`PendingTerminal` |
| 台账 Terminal 已提交、关闭未完成 | 是 | 读回关闭事务；完成关闭后补 Operation 终局 | 关闭结果不明→Reconciling；已关闭未终局→Accepted |
| 关闭已提交、Operation 终局失败 | 是 | 按完整身份补终局；不重建 Submission | 保持 `Accepted`＋`PendingTerminal`；**若 `PendingTerminal` 首写未提交则保持 `Accepted` 并重新取证** |
| `PendingTerminal`/`ExecutionResult` 发布结果不明 | 可能 | 读回；已提交则继续后续步骤，未提交则重试首写 | 不得当成已终态或已结清 |
| **已拒绝（`TerminalRejected`／`RetryableRejected`／已迁墓碑）后收到冲突证据** | 可能 | **原子追加 `conflict`**（保留双方证据与完整发送身份）；**不得走普通 `SettleCompletionAsync`**；由 `SettleReconciledAsync` 冲突入口按 **§24.2-2″ 四分支**裁决——①曾受理＋**已有**权威终态 ⇒ 先原子写入权威 `ExecutionResult`＋`PendingTerminal`（**[第十五轮 N24-31] 历史拒绝本体不改写**：其「已被取代」关系**只由审计项表达**；事务提交前仍由 `conflict.pending` 覆盖历史拒绝分类）、台账 Terminal→关闭/确认→终局投影，**随后以一次租约原子发布完成三项**「追加或幂等确认 `ResolvedAcceptedTerminal` 审计项 ＋ 写 `ConflictResolutionAuditId` ＋ 清 `conflict.pending`」；②曾受理＋**尚无**终态 ⇒ 只写 `ConflictResolutionState=AcceptedAwaitingTerminal`（**不生成**终态载体），终态到达时**再次进入本冲突入口**；③未受理 ⇒ 保留原拒绝终局，**同一次租约原子发布完成四项**（[第十八轮 N24-34]）「追加或幂等确认 `ReconciledNotAcceptedEvidence` 证据记录 ＋ 追加或幂等确认 `ResolvedNotAccepted` 审计项 ＋ 写 `ConflictResolutionAuditId` ＋ 清 `conflict.pending`」；④不可考 ⇒ 继续保留。**[第十四轮 N24-28]「清冲突」一律只指清 `conflict.pending`**——**不得**删除 `conflict` 证据、审计项、证据记录或拒绝快照 | **[第十八轮 N24-34] 缺失判据按分支**：`ResolvedAcceptedTerminal` ⇒ **三项**任一缺失＝视为未提交；`ResolvedNotAccepted` ⇒ **四项**任一缺失＝视为未提交。读回验证后**以同一 `auditId`／`evidenceId` 重试**；追加失败/发布不明时不得据此释放占用或重发 |

`failed/cancelled` 均属于“已受理后的执行终态”，不得进入确定未受理关闭分支。

### 24.16 预观察记录（PreObservationRecord）

1. **位置与字段**：权威载体为租约文件 version 3 的 `PreObservations[]`；字段至少含 `submissionIdentity`、`sendSeq`、`operationType`、`targetEpoch`、`wireSubmitKey`、协议/查询依据、owner、创建时点、状态。
2. **发布时点**：与发送许可占位同一权威串行发布；未持久化不得发送。取得 JobId 后在同一权威串行边界转为正式外部接管台账并标记 PreObservation 完成。
3. **恢复**：`RecoverAfterRestart` 扫描未完成 PreObservation；按固定 epoch/线上键重查并恢复观察；不可查询时保持 Unknown 且不释放占用，相关协议路径保持关闭。
4. **清理与冲突**：只有正式接管台账或 PendingTerminal 已持久化后才允许清理 PreObservation；冲突/多重 owner 按保守对账拒绝。

### 24.17 OperationType（可信持久化操作类型）

1. `OperationRecord` 增加不可变 `OperationType` 字段，合法值至少含 `FlowRegistration`、`NodeExecution`、`ExternalStart`、`Recovery`、`Handoff`、`Unknown`。
2. 该字段由可信适配器在 Operation 创建时提供，与 Operation 同次原子发布，后续不得改写。
3. 缺失、`Unknown`、与 `ResourceRef`/来源记录冲突时 fail-closed；旧 version 2 记录必须在升级事务中显式迁移或隔离，不得按 `ResourceRef` 猜测成外部启动。

### 24.18 `_gate` 两段事务（补充合同）

1. **第一段（锁内）**：轮次快照、裁决、**占位**、发布唯一发送责任与 `PreObservation`/`Submission`（同次权威原子发布；未持久化不得发送——[纠正·2026-09-21 冻结轮 B24-03，原「占位占位」为笔误]）。
2. **第二段（锁外）**：释放 `_gate` 后调用早期受理/启动/取证；`_inflight` 继续标记本请求，下一轮可处理其他请求但不得重新驱动同一请求。
3. **重入结算**：启动返回后重新取得权威串行权，复核 owner/epoch/revision/完整发送身份；只有匹配当前责任者才可结算。
4. **并发/换主**：换主、关闭、取消、回执同时发生时，以完整身份与租约代次确定唯一结算者；一方完成终局后，另一方只能读回既有结果，不得重复结算。
5. **验收**：补“CompletionTask 未完成时其他请求取得门面锁”、重复结算、双重释放、换主接续夹具。

### 24.19 `unknown` 与权威终态

1. `ExecutionResult` 区分：权威终态 `succeeded/failed/cancelled`；非终态观察 `unknown`。
2. 只有权威终态可生成 `PendingTerminal` 并参与 Operation `TerminalCompleted`。**[纠正·2026-09-21 第七轮 N24-10] `unknown` 的责任状态按「是否已确认受理」二分，不得压成一个状态**：①**发送结果未知**（尚未判定是否受理）⇒ Operation 保持 `Reconciling`；②**已确认受理、完成层为 `Unknown`**（执行结果未知）⇒ 发送受理事实＝`Accepted`、Operation 请求状态＝`Accepted/Active`、**保留观察依据**（`PreObservation`／接管台账／可查询依据）、**不得**生成 `PendingTerminal`，对调用方返回 `NeedReconcile`（对外 `Unknown`）。第二种情形**不得**写成 `Reconciling`（那会与「受理已确认」的事实冲突，也会掩盖投递责任）。
3. `TerminalCompleted` 必须同时存在匹配的权威终态结果与关闭事实；`unknown` 不得借超时、未命中或重启转为终态。

### 24.20 冻结登记：版本口径／长期保守停驻总则／落地清单／会诊记录（[新增·2026-09-21 冻结批]）

**A. 版本口径（唯一定义；与 §24.3-1／§24.12-1 合并阅读，冲突时以本节为准）**

| 载体 | 旧 | 新 | 旧消费者 / 旧记录处置 |
|---|---|---|---|
| 租约文件 `version`（`LogicalOwnerLeaseFile`） | 2 | **3** | **旧（≤2）消费者**遇 3＝`unsupported_version` **响亮拒绝**（不降级解析）；**新代码**遇 ≤2＝**兼容读**（`Operations` 缺类型字段按 §24.17-3 记为 `Unknown` 并 fail-closed），**任何写入一律升为 3**（发布单点升级，修订/代次单调不回退） |
| 外部启动台账 `version`（`ExternalStartLedgerFile`） | 1 | **2** | **旧（1）消费者**遇 2＝版本过高 → **保守待对账（响亮拒绝，不推导空闲）**；**新代码**遇 1＝兼容读，缺 `jobId`/终态副本字段视为「**未取得**」（**不等于**「无责任」），写入一律升为 2 |

- 台账 `jobId` 本身是**加法字段**（§24.3-1）；触发升版的是**承载责任事实**的 `pendingTerminal`／`executionResult` 副本（§24.12-1）。
- **禁止**按 `ResourceRef` 前缀、`RunId` 是否为空、来源记录缺失或运行快照查询结果**猜测**「该 Operation 属外部启动」（§24.1-6／§24.17-3）；类型缺失或未知一律 fail-closed。
- 本节对两处载体的版本决定属**施工方自行判定**（owner 未单独裁决），保留「事后可剔除」入口：若 owner 要求保持台账 version 1，则须改为「台账不承载责任副本、只承载对外可读引用」并重开会诊。

**A′. `version ≤ 2 → v3` 升级事务（[新增·2026-09-21 冻结轮 B24-11；复会诊轮 N24-01 改为**有序判定**并补隔离态结算事务；第十五轮 N24-32 把起点由「v2」扩为「**version ≤ 2**」]）**

租约由 **`version ≤ 2` 升 3**（[第十五轮 N24-32；第十六轮措辞收窄] **v1 与 v2 执行同一套事务**，不因起点版本不同而绕过未决责任判定、隔离结算、类型证明与审计集合初始化；对任何**实际发现**的 v1/v2 文件的 fail-closed **只表示「升级事务完成前禁止业务发送」**——**不得**被实现为永久拒绝迁移，兼容读取后必须进入与 v2 相同的升级/隔离事务）**不是纯格式声明**，按下述**有序判定**处置（**逐条命中即止**，保证分支互斥、实现可唯一选择事务）；**不得**把旧记录直接改写成 v3 `Unknown` 后继续写入（那会把真实责任冻结成不可操作状态）。

1. **先判未决责任**：盘上存在 `Submission`／未终结 `Pending`／`Accepted`／`Reconciling`／`Granted`／`Sending` 记录 ⇒ 进入第 4 条**隔离态结算事务**，**不得**就地升版。
2. 无未决责任且 `Operations` 为空（或全部已 `TerminalCompleted`／`Tombstone`）⇒ **原子升版**：同一次发布写 `Version=3`。
3. 无未决责任且有记录：
   - 类型可由**可信创建记录**证明（同次创建写入的请求状态/来源记录/回执来源）⇒ 升级事务内写 `OperationType` 并发布 `Version=3`；
   - 类型不可证明 ⇒ 升版并写 `OperationType=Unknown`；该记录**类型相关判定一律 fail-closed**（外部台账不查、不得独立终局），恢复扫描登记「需人工处置」。
4. **隔离态结算事务（[新增·2026-09-21 复会诊轮 N24-01；终轮 N24-03 收窄适用范围；第十五轮 N24-32 扩至 v1/v2]）**——用于**所有「有未决责任」的 `version ≤ 2` 记录**（**不限于**类型起初即不可证明者：类型证据在事务内判定），**不得**变成永久死锁：
   - **禁止发送**：隔离期间任何占位/发送一律响亮拒绝（`legacy_operation_type_unresolved`）；
   - 结算入口**只**接受按完整发送身份（`submissionIdentity`＋`sendSeq`）＋固定 `targetEpoch`＋`wireSubmitKey`＋合法证据的对账：
     a) 证据足以证明类型 ⇒ 同一事务**只**写 `OperationType` 并升 `Version=3`；**类型证据 ≠ 终态证据**（[第四轮 N24-05]），随后必须**按实际证据事实分流**：已受理未终态→继续观察（`Accepted`／`PendingTerminal` 仅在取得权威终态后写）；取得权威终态→按 §24.15 完成终局；确定未受理→走 §4.2c 第二分支；证据不足→继续隔离；
     b) 取得**确定未受理**证据但类型仍不可证明 ⇒ 同一事务完成 §4.2c **第二分支**关闭、写 `OperationType=Unknown` 并升 `Version=3`；
     c) 证据不足 ⇒ **继续隔离**（长期保守停驻，§24.20-B），并在 §23.4 门禁并集登记**责任方、人工处置入口与恢复依据**；
   - **任何中间状态不得暴露为「可发送」**。

- 升级事务**崩溃/读回**语义：发布返回不明时**先读回**——已提交则继续后续步骤，未提交则重试；**旧进程并存**期间不得双写（旧进程见 v3＝`unsupported_version` 响亮拒绝）。
- **[第十三轮 N24-26；第十八轮 N24-35 扩至两个集合]** 审计与证据集合的升级口径：所有**成功升至 v3** 的事务必须**初始化或保留合法的 `ConflictResolutionAudits[]` 与 `ReconciledNotAcceptedEvidence[]`**（空集合＝合法；**既有 v3 文件缺字段或集合非法＝损坏，fail-closed**）；隔离态结算（第 4 条）若产生冲突裁决，同样必须使用 §24.2-2″ 的原子事务（`ResolvedAcceptedTerminal` **三项**／`ResolvedNotAccepted` **四项**）。
- 台账 `version` 1→2 同理由：**缺 `jobId`/终态副本字段视为「未取得」**，不得据此推断「无责任」；**有未终结记录时不得把台账降级或重建**。

**B. 长期保守停驻总则（跨节；与 §24.2-4／§24.19-3 合并阅读）**

- **[纠正·2026-09-21 冻结轮 B24-06] 两种停驻状态不得混同**：①**未取得权威终态证据**（远端不可查询、通道瞬态、句柄丢失、对账未命中……）→ 停在 `Reconciling`／`Accepted`＋**观察依据**（`PreObservation`／正式接管台账／可查询依据），**不得**产生 `PendingTerminal`（`PendingTerminal` 只由**权威终态**生成，§24.19-2）；②**已取得权威终态、仅关闭或终局结算未完成** → 停在 `Accepted`＋`PendingTerminal`（同样允许长期停驻）。
- 两种停驻均**禁止**以超时、未命中、重启、批次推进或资源压力为由**强制终局**或释放主槽位。
- 「停驻」≠「放弃」：观察责任、恢复扫描与显式对账入口必须持续可用（§24.12-3／§24.12-4／§24.14-6）；清理唯一恢复依据（`PendingTerminal`／`PreObservation`）之前必须先取得**替代权威证据**。
- 停驻可带来可用性代价（主槽位被占），该代价由 §24.1-8 容量夹具与 §24.12-5 保守记不可考共同约束，**不得**以可用性为由放宽为「超时即终局」。

**C. 与既有条款的关系**

- 本章**不减免** §23.4 门禁并集（§18.5 F11 生产事实源、P28/P55/P57、§19.4、§21.10、§22.5、§17 P1–P57 全部原条款）；§24.8-2 已明文，本节重申。
- 本章**取代**草稿阶段的下列隐含口径（属澄清，非新增义务）：①「未知可超时强制终局」→ 见 B；②「台账写入 `version` 1」→ 见 A；③「按 `ResourceRef` 前缀判定外部类型」→ 见 A 第三条。
- 本章与 §4.1/§4.2a/§4.2c/§13.7/§13.10 的**加法关系**：仅新增类型与责任字段，**不改变序列化框架**（锚点 6），不改变 §4.2c 统一关闭接口的两个合法分支。

**D. 落地清单（冻结即登记；每项在实现批内逐条关闭，本批不声称完成）**

| # | 落地项 | 对应条款 | 责任批 |
|---|---|---|---|
| D1 | `CommandResult`：新增 `IsTerminal`／`JobId`／`ExecutionDisposition`（`None/Cancelled/ExecutionFailed/Unknown`）／**`ResponsibilityState`（`None/Pending/Settled`；取代原布尔 `ResponsibilityPending`，[第八轮 N24-14]）**／`RawTerminal`／`ExecutionErrorCode`（**完成层执行错误码**，与既有 `ErrorCode`「BGI 信封错误码」语义分离、不得复用）／`EvidenceSource`；**线路词表不改**（`Status` 仍只用 `success/failed`；v2 的**原始取消词 `cancelled`** 写入 `RawTerminal`，以 `ExecutionDisposition=Cancelled`＋`IsTerminal=true` 承载取消事实）；v2 仅「发送成功」不得置 `IsTerminal=true` | §24.2-1、§24.2-5、§24.6-1／2、§24.9 | B3 实现批 |
| D2 | **两层判别式＋唯一转换表**：发送层 `ExternalStartExecution`＝`Kind ∈ {Accepted,Rejected,Unknown}` ＋ `JobId` ＋ `Reason` ＋ **`Retryable`** ＋ **`EvidenceSource`**（[第九轮 N24-18] 工厂：`AcceptedWith(JobId)`／`RejectedWith(reason, retryable, evidenceSource)`／`UnknownWith(detail)`；非 `Rejected` 时 `Retryable` 不适用；**无 `Terminal`**；同一发送身份只产出一次；`EarlyAccepted` 后不得再产出 `Rejected`/`Unknown`）；完成层新增 `ExternalStartCompletion`＝`Kind ∈ {Succeeded,Cancelled,ExecutionFailed,Unknown}` ＋ `RawTerminal` ＋ **`ExecutionErrorCode`（`ExecutionFailed` 必填，其余可空；[第十一轮 N24-23-R] 全链统一命名）** ＋ `JobId` ＋ `EvidenceSource` ＋ **`ObservedAtUtc`（[第十七轮 N24-33] 权威观察时点；贯穿 `PendingTerminal`／`ExecutionResult`／审计快照三处全等且不可改写）**（**不含 `Rejected`**）；**`SendOutcome` 冻结为发送层 `{Accepted(JobId), Rejected(reason, retryable, evidenceSource), Unknown}`（不含 `Cancelled`）**；新增完成结算入口 `SettleCompletionAsync(requestIdentity, submissionIdentity, sendSeq, ExternalStartCompletion)` 与内部 `CompletionSettlementOutcome`；`AdmissionResultKind.Cancelled`；`ExternalStartAdmissionStatus.Cancelled`／`ExecutionFailed`（保留 `Blocked`，`NeedReconcile`＝对外 `Unknown`）。**错误码唯一映射链**：`ExternalStartCompletion.ExecutionErrorCode → AdmissionResult.ExecutionErrorCode → ExternalStartAdmissionOutcome.ExecutionErrorCode → CommandResult.ExecutionErrorCode`（既有 `CommandResult.ErrorCode` 仅表示 BGI 信封错误码）；**观察时点唯一映射链**：`ExternalStartCompletion.ObservedAtUtc → PendingTerminal.ObservedAtUtc → ExecutionResult.ObservedAtUtc → 审计快照.ObservedAtUtc`（全等、不可改写） | §24.2-2、§24.2-2″、§24.2-7、§24.3-4、§24.9、§24.10-1／3、§24.14-1、§24.6-1／2／3／5 | B3 实现批 |
| D3 | `SendOutcome.Accepted`：新增 `JobId`（**不加 `Terminal`**）；`ReconcileSettlement.Accepted`：新增 `JobId`＋可选 `ExternalStartCompletion?`（旧字段调用保持兼容默认值） | §24.1-1、§24.3-4、§24.2-2″ | B3 实现批 |
| D4 | `ExternalStartLedgerEntry`：`jobId` ＋ 终态副本（升 version 2）＋ **`TerminalObservedAtUtc`（[第十八轮 N24-36] 观察时点副本，与落盘时点 `TerminalAtUtc` 分离）**；台账 `MarkTerminal` 签名**接收既有 `observedAtUtc`**（首写保存、幂等重试严格比对）；`OperationRecord`：`OperationType`／`PendingTerminal`／`ExecutionResult`／本地取消意向 | §24.3-1、§24.12-1、§24.13-1、§24.17-1、§24.2-2″ | B3 实现批 |
| D5 | 租约 `version` 3 ＋ `PreObservations[]`（含发布时点、恢复、清理规则）＋ **`ConflictResolutionAudits[]`（追加式冲突裁决审计，[第十二轮 N24-25] 不计入 32 主槽位／256 墓碑容量，本批不裁剪）** ＋ **`ReconciledNotAcceptedEvidence[]`（[第十七轮 N24-30-R-R] 独立不可变未受理证据记录，含 `evidenceId`；同不计容）** | §24.16、§24.20-A、§24.2-2″ | B3 实现批 |
| D6 | 门面：两段式 `_gate`（锁内占位发布责任 → 锁外启动/取证 → 重新串行校验结算）；无重入终局入口；`TakeoverTerminalConfirmed` 按**持久化操作类型**分派；恢复扫描**四类集合**（①仅 Submission 未关闭／②未终结台账／③台账已 Terminal 但 Operation 未终局／④**`conflict.pending=true` 全部记录，含受保护墓碑**；裁决前禁止裁剪） | §24.18、§24.1-5/6/7、§24.12-3/4、§24.2-2″ | B3 实现批 |
| D7 | 宿主与适配器：早期受理与完成观察拆分、JobId 全链传递、取消阶段矩阵落地、生产组合根夹具（含 >32 笔容量回归） | §24.10／§24.14、§24.3-2、§24.11、§24.4 | B3 实现批 ＋ B4 |
| D8 | 实现批开工时登记 `SendOutcome`／`ReconcileSettlement`／`AdmissionResultKind`／`ArbitrationAdmissionService` 的**定义文件与成员签名**（冻结轮会诊材料仅含四个类型文件，未含上述定义处——属材料范围限制，非缺陷） | §24.20-E（冻结轮「引用锚点」说明） | B3 实现批 |
| D9 | `OperationRecord` 新增加法字段 `conflict`（冲突证据集合＋冲突对账待决标志）、`ConflictResolutionState`（含 `AcceptedAwaitingTerminal`）与 **`ConflictResolutionAuditId` 审计引用**（仅存引用 → 租约 `ConflictResolutionAudits[]`；[第十一/十二/十三轮 N24-24／25／26]「清除冲突」只指清活动 `pending` 标志；审计本体在独立集合、不受墓碑清理影响；**[第十九轮 N24-34-R] 分支化原子裁决事务**：`ResolvedAcceptedTerminal` **三项**；`ResolvedNotAccepted` **四项**（含追加或幂等确认 `ReconciledNotAcceptedEvidence`，重试复用同一 `evidenceId`）——事务规模、引用完整性与逐字段校验一律**以 §24.2-2″ 为准**）**；`AdmissionResult`／`ExternalStartAdmissionOutcome` 新增 `JobId`／`ExecutionDisposition`／**`ResponsibilityState`（`None/Pending/Settled`；取值一律按 §24.6-5 唯一映射表）**／`RawTerminal`／`ExecutionErrorCode`／`EvidenceSource`／`SubmissionIdentity`／`SendSeq`（默认空值/`None`）；冲突记录须汇入准入事实面（`ArbitrationFacts.ExecutionFactsUnknown`／占用），分类读取以冲突状态**覆盖**历史 `TerminalRejected` | §24.2-2″、§24.2-3／4、§24.6-2／5、§24.20-D5 | B3 实现批 |

**E. 会诊记录（gpt-5.6-sol / effort=medium；按 §17.4「有必改项即处置→回归→复会诊」迭代至某轮无必改项）**

| 轮次 | 该轮结论 |
|---|---|
| 冻结轮（2026-09-21） | 12 项必改（4 阻断／8 重要）；「引用不存在锚点」＝**无发现** |
| 复会诊轮 | 6 项必改（含 2 项新发现） |
| 终轮复核 | 6 项必改（含 2 项新发现） |
| 第四轮复核 | 4 项必改（含 2 项新发现） |
| 第五轮复核 | 1 项必改（新发现） |
| 第六轮复核 | 2 项必改（含 1 项新发现） |
| 第七轮复核 | 4 项必改（含 2 项新发现） |
| 第八轮复核 | 4 项必改（含 2 项新发现） |
| 第九轮复核 | 5 项必改（含 3 项新发现） |
| 第十轮复核 | 4 项必改（含 4 项新发现） |
| 第十一轮复核 | 2 项必改（含 2 项新发现） |
| 第十二轮复核 | 1 项必改（新发现） |
| 第十三轮复核 | 1 项必改（新发现） |
| 第十四轮复核 | 4 项必改（含 4 项新发现） |
| 第十五轮复核 | 4 项必改（含 2 项新发现） |
| 第十六轮复核 | 1 项必改（新发现） |
| 第十七轮复核 | 2 项必改（新发现） |
| 第十八轮复核 | 3 项必改（新发现） |
| 第十九轮复核 | 1 项必改（新发现） |
| 第二十轮复核 | 1 项必改（新发现） |
| 第二十一轮复核 | 1 项必改（新发现） |
| 第二十二轮复核 | 2 项必改（新发现） |
| **第二十三轮复核** | **无必改项 ⇒ 冻结**（最小审计结构×兼容矩阵、三项/四项事务、D9、§24.12 恢复扫描逐面一致；仅留 2 条非必改实现建议） |

**逐条处置（全部在文本层完成，无代码改动）**

| 轮 | 编号 | 严重度 | 发现摘要 | 处置位置 |
|---|---|---|---|---|
| 冻结 | B24-01 | 阻断 | §24.2-3／§24.11 取消关闭依据矛盾 | §24.2-3、§24.2-7、§24.11 第 2／3／3′ 行 |
| 冻结 | B24-02 | 阻断 | 「登记后未占位」无条件终局 | §24.15 首行（「三无」四条件）、§24.11 第 2 行 |
| 冻结 | B24-03 | 阻断 | PreObservation 发送时序矛盾 | §24.15、§24.16-2、§24.18-1 |
| 冻结 | B24-04 | 阻断 | 终态事务顺序冲突／首写失败自述矛盾 | §24.1-3、§24.15（失败列） |
| 冻结 | B24-05 | 重要 | 「不得无限停驻」可被读成超时终局 | §24.6-3 |
| 冻结 | B24-06 | 重要 | 停驻状态混同（无证据 vs 有终态） | §24.20-B 两类停驻 |
| 冻结 | B24-07 | 重要 | 章首与 §24.20-E 状态双写 | §24 标题／冻结声明／末尾（状态表唯一来源） |
| 冻结 | B24-08 | 重要 | 对外结果集合与 D2 不一致／`Blocked` 去留 | §24.6-1、§24.20-D2 |
| 冻结 | B24-09 | 重要 | 布尔叠加与两套分类并存 | §24.2-2、§24.10-1 |
| 冻结 | B24-10 | 重要 | `status=cancelled` 与线协议词表 | §24.2-5、§24.20-D1 |
| 冻结 | B24-11 | 重要 | v2→v3 迁移/隔离事务缺失 | §24.20-A′ |
| 冻结 | B24-12 | 重要 | 取消矩阵缺「未登记」行 | §24.11 六行 |
| 复诊 | N24-01 | 阻断 | 隔离态造成合法关闭死锁 | §24.20-A′-4 隔离态结算事务 |
| 复诊 | N24-02 | 阻断 | §24.11 第 2／3′ 行终态列互斥 | §24.11 第 2、3′ 行 |
| 终轮 | N24-03 | 重要 | 隔离事务适用范围歧义 | §24.20-A′-4（适用范围） |
| 终轮 | N24-04 | 阻断 | 三层载体映射／`Rejected` 组合未冻结 | §24.2-2、§24.2-2″ |
| 四轮 | N24-05 | 阻断 | 类型证据被当作终态证据 | §24.20-A′-4a（分流） |
| 四轮 | N24-06 | 重要 | 本地未发送证明的内部终局状态 | §24.11 末段、§24.15 |
| 五轮 | N24-07 | 阻断 | 三层唯一映射缺失／第三套终态来源 | §24.1-1、§24.2-2／2″、§24.3-4、§24.10-1、§24.14-1 |
| 六轮 | N24-08 | 阻断 | §24.14-4 与 §24.15 唯一顺序相反 | §24.14-4、§24.3-4 三分支 |
| 七轮 | N24-09 | 阻断 | 冲突证据的占用／恢复／裁决未闭合 | §24.2-2″、§24.12-3、§24.15 |
| 七轮 | N24-10 | 阻断 | 完成层 `Unknown` 责任状态相反 | §24.19-2 二分、§24.3-4 |
| 七轮 | N24-11 | 重要 | 发送层 `Rejected` 字段不足以一对一 | §24.2-2、§24.2-2″、§24.14-1、D2 |
| 七轮 | N24-12 | 重要 | 完成层缺 `ErrorCode` | §24.2-2、D1（`ExecutionErrorCode`）、D2 |
| 八轮 | N24-13 | 阻断 | `Tombstone` 后冲突的容量／恢复集合／事务 | §24.2-2″（占用语义）、§24.12-3 第四类、§24.15 |
| 八轮 | N24-14 | 阻断 | 责任状态未贯穿到适配器边界 | §24.6-2、§24.2-3／4、D1／D9 |
| 九轮 | N24-15 | 阻断 | 墓碑冲突「确认曾受理但无终态」无合法责任状态 | §24.2-2″（四分支＋`AcceptedAwaitingTerminal`）、§24.12-3 |
| 九轮 | N24-16 | 阻断 | D6 仍写「三类集合」 | §24.20-D6（四类集合） |
| 九轮 | N24-17 | 阻断 | `ResponsibilityState` 无唯一分支矩阵 | §24.6-5 唯一映射表（§24.2-3／4、§24.3-4、§24.2-2″ 引用） |
| 九轮 | N24-18 | 重要 | D2 未同步发送层 `Retryable`/`EvidenceSource` | §24.20-D2（`RejectedWith(reason, retryable, evidenceSource)`） |
| 九轮 | N24-19 | 重要 | `ErrorCode` 出现两套名称 | §24.6-2、D1、D2（统一 `ExecutionErrorCode`＋唯一映射链） |
| 十轮 | N24-20 | 阻断 | §24.15 冲突行仍「三分支」且无反修正事务 | §24.15 冲突行（四分支＋「已被冲突裁决取代」＋再入冲突入口） |
| 十轮 | N24-21 | 阻断 | 墓碑冲突责任与 §4.1a 三区/容量语义冲突 | §24.2-2″（`conflict`＝与区域正交的覆盖层；显式例外，不改容量公式） |
| 十轮 | N24-22 | 阻断 | `ResponsibilityState` 表非穷尽 | §24.6-5 补 5 行＋默认规则（已登记后非结清不得 `None`） |
| 十轮 | N24-23 | 重要 | 生命周期错误码仍混用 `ErrorCode` | §24.2-2、§24.12-1、§24.13-1、D2（统一 `ExecutionErrorCode`） |
| 十一轮 | N24-24 | 阻断 | 「清除冲突」缺持久化审计模型 | §24.2-2″（只清 `pending`＋`ConflictResolution` 审计）、§24.12-3、D9 |
| 十一轮 | N24-23-R | 重要 | D2 完成层仍写 `ErrorCode` | §24.20-D2（统一 `ExecutionErrorCode`＋映射链首端改名） |
| 十二轮 | N24-25 | 阻断 | 冲突裁决审计随墓碑清理而丢失／或永久占容量 | §24.2-2″（独立追加式 `ConflictResolutionAudits[]`、仅存 `auditId`、快照入审计）、§4.1a 清理前置、D5／D9 |
| 十三轮 | N24-26 | 阻断 | 审计引用无唯一原子事务与可执行完整性判据 | §24.2-2″（三项原子变更、`ConflictResolutionAuditId`、最小审计结构、引用校验）、§24.20-A′、D9 |
| 十四轮 | N24-27 | 阻断 | 审计字段三套命名／ID-only 下无法比较裁决类型 | §24.2-2″（命名唯一化＋分支兼容矩阵） |
| 十四轮 | N24-28 | 阻断 | §24.15 冲突行未要求三项事务（高优先级行可绕过） | §24.15 冲突行（三项原子事务＋「清冲突」限定＋失败列） |
| 十四轮 | N24-29 | 阻断 | 墓碑清理前置被写成了对所有墓碑生效 | §24.2-2″（仅限曾进入冲突覆盖层者） |
| 十四轮 | N24-30 | 重要 | 审计未保存裁决依据侧证据快照 | §24.2-2″（`resolutionEvidenceSnapshot`） |
| 十五轮 | N24-27-R | 阻断 | §24.2-2″ 残留第二组同名审计条款 | §24.2-2″（去重，唯一保留定义） |
| 十五轮 | N24-31 | 重要 | §24.15 出现未定义的「拒绝已被取代」写入 | §24.15 冲突行（历史拒绝本体不改写，关系由审计项表达） |
| 十五轮 | N24-32 | 阻断 | 升级起点只写 v2，未覆盖 v1 | §24.20-A′（`version ≤ 2 → v3`；v1 同套事务；实际 v1 文件 fail-closed） |
| 十六轮 | N24-30-R | 重要 | 审计快照字段与逐字段校验不封闭 | §24.2-2″（封闭字段表、拒绝快照逐字段核对、`ExecutionResult.ObservedAtUtc`）、§24.13-1 |
| 十七轮 | N24-30-R-R | 重要 | `ResolvedNotAccepted` 校验自引用 | §24.2-2″（新增 `ReconciledNotAcceptedEvidence[]` 独立证据记录、四项原子事务）、D5 |
| 十七轮 | N24-33 | 重要 | `ObservedAtUtc` 来源与写入时序未冻结 | §24.2-2″（唯一语义与来源）、§24.13-1、D2（唯一映射链） |
| 十八轮 | N24-34 | 阻断 | §24.15 仍以「三项事务」覆盖四项 | §24.15 冲突行（分支③四项＋缺失判据按分支）、§24.20-A′、D9 |
| 十八轮 | N24-35 | 重要 | 新证据集合缺版本与引用完整性合同 | §24.2-2″（`evidenceId` 判据）、§24.20-A′（两集合）、D5／D9 |
| 十八轮 | N24-36 | 重要 | `ObservedAtUtc` 混链、台账存储点未定义 | §24.2-2″（两条独立链）、D4（`TerminalObservedAtUtc`＋`MarkTerminal(observedAtUtc)`） |
| 十九轮 | N24-34-R | 阻断 | D9 仍写「三项原子裁决事务」 | §24.20-D9（分支化三项/四项） |
| 二十轮 | N24-37 | 阻断 | §24.2-2″ 分支③漏第四项 | §24.2-2″ 分支③（四项原子变更）、证据记录字段名显式化 |
| 二十一轮 | N24-38 | 阻断 | `resolutionEvidenceSnapshot` 在未受理分支语义二义 | §24.2-2″（判别式两字段 `resolutionEvidenceSnapshot`／`resolutionEvidenceRef` 互斥＋兼容矩阵） |
| 二十二轮 | N24-39 | 阻断 | 失败条件「快照缺失」与未受理分支冲突 | §24.2-2″（分支必需载荷缺失判据） |
| 二十二轮 | N24-40 | 阻断 | 顶层 `evidenceSource` 证据双写；重启后校验对象未定义 | §24.2-2″（删顶层来源；`ReconciledNotAcceptedEvidence` 定为权威观察事件载体） |

**非必改建议（实现期执行，不计入必改项）**：①为「权威未受理事实类型／原始证据词／证据来源」确定唯一代码字段名与**封闭枚举**（不得退化为自由字符串比较）；②为 `ResolvedNotAccepted` 提供**统一审计联接读取器**（禁止调用方只读审计项而不解析 `resolutionEvidenceRef`）；③`CommandResult.ErrorCode` 保持信封语义、生命周期错误码一律 `ExecutionErrorCode`；④`ExternalStartExecution` 判别式化属进程内破坏性改造，实施时一次性审计构造点／属性访问／模式匹配调用点，并保留旧工厂重载以避免改动解构与记录相等性断言的调用方。

> **登记纪律**：各轮发现均在**文本**层面处置完毕（无代码改动）；**后轮处置优先**（例：N24-04 的「发送层 `Terminal=false`」已被 N24-07 的「发送层去掉 `Terminal`」取代）；**未出现「无必改项」轮次前不得宣布冻结完成**（§17.4，轮次不设上限）。

**冻结状态（唯一来源）**

| 状态项 | 当前值 |
|---|---|
| 已完成会诊轮次 | 冻结轮（12）／复（6）／终（6）／四（4）／五（1）／六（2）／七（4）／八（4）／九（5）／十（4）／十一（2）／十二（1）／十三（1）／十四（4）／十五（4）／十六（1）／十七（2）／十八（3）／十九（1）／二十（1）／二十一（1）／二十二（2）／**二十三（无必改项）** |
| 是否已出现「无必改项」轮次 | **是**（第二十三轮：判「无必改项」，§17.4 收口） |
| 处置优先级说明 | 同一编号在多轮重复出现时**后轮优先**（例：N24-04 的「发送层 `Terminal=false`」已被 N24-07 的「发送层去掉 `Terminal`」取代） |
| 本章状态 | **已冻结**（第二十三轮「无必改项」；设计冻结 = 合同冻结，**不代表实现完成**） |
| 生产接线 | **关闭**（不依赖冻结与否：实现与 §24.14 夹具完成并通过验收前不恢复，§23.8） |

**冻结状态**：见 §24.20-E 末尾「冻结状态」表（唯一来源；冻结 ≠ 生效、≠ 生产开门）。无论冻结与否，实现与 §24.14 夹具完成并通过验收前，**不恢复** §23.8 所述生产接线。

### 24.21 落地登记：Batch B 收尾之三（P38 早期受理／完成观察拆分）与会诊处置（[新增·2026-09-21]）

**A. 本批落地事实（登记；证据＝提交记录＋全量回归）**

| 落地项 | 实现位置 | 条款 |
|---|---|---|
| 早期受理段／完成观察段拆分（通道不可用＝未发送 ⇒ 回退既有路径；先订阅后动作；提交一次） | `CommandExecutor.TryStartViaQueueEarlyAsync` | §24.10-1／§24.14-1／§24.14-3（红线7） |
| 早期层回执 `ExternalStartReply.EarlyAccepted(jobId, CompletionTask)` 构造与断言 | 同上（`Reply` 字段）＋夹具 | §24.14-1 |
| 完成观察（门面锁外；事件快速路径＋5s 轮询安全网；等待器由观察段 `finally` 释放） | `CommandExecutor.ObserveQueueTerminalAsync` | §24.10-2／§24.14-3／§24.18-2 |
| 观察结论 → 完成层结果（仅事件/轮询权威终态构造终态载体；`not_found`／超预算／瞬态／本地取消 ⇒ `Unknown`，**不写** `PendingTerminal`） | `CommandExecutor.MapQueueObservationToCompletion` | §24.2-2／§24.7-3／§24.19-2／§24.20-B |
| 旧词表投影（未接线直启路径文案/错误码/探针逐字保留） | `CommandExecutor.MapQueueEarlyToLegacyResultAsync` | §24.8-3（不回归既有直启路径） |
| `CompletionObserver` 返回 `null`＝本通道不承载完成事实；宿主**仅在确有完成事实**时结算 | `ExternalStartAdmissionRequest.CompletionObserver`／`TaskCenterHost.AdmitExternalStartAsync` | §24.3-4（第一分支）／§24.6-5 |
| 观察记录携带**接收时点**、`RawTerminal` 保留线路原词 | `CommandExecutor.QueueTerminalObservation` | §24.2-2″（两条独立链） |
| 队列拒绝的**可重试封闭白名单**（仅无损拒绝类；其余默认终局拒绝） | `CommandExecutor.IsRetryableQueueRejection` | §24.2-2″／§24.11 第 3′ 行 |
| 未知分支 JobId 回退（`acceptanceJobId` → 既有结果 → 台账句柄读回）；受理句柄与完成层句柄**非空冲突** ⇒ `terminal_job_id_conflict` 保守停驻 | `ArbitrationAdmissionService.SettleCompletionAsync` | §24.3-3／§24.6-2／D9 |
| `ToCompletion` 非终态 ⇒ **`null`**（原 `Unknown` 会把「v2 发送成功」改判为对外 `result_unknown`，与 §24.4-4 明文冲突） | `CommandExecutor.ToCompletion` | §24.3-4／§24.4-4／§24.6-5 |
| 完成观察消费条件＝「**有完整发送身份＋确有完成事实**」（不再限于 `Accepted`）：接管/关闭失败（`Reconciling`）时权威终态仍按唯一顺序结算 | `TaskCenterHost.AdmitExternalStartAsync` | §24.14-4／§24.15 |

**B. 本批会诊（gpt-5.6-sol／effort=medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 1 | 阻断 | `ToCompletion` 对普通 v2 成功返回 `Unknown` ⇒ 宿主结算成 `NeedReconcile`（对外 `result_unknown`），与 §24.4-4「v2 发送成功：入口 success」冲突 | **已修**（非终态 ⇒ `null`；夹具改为断言 `null`） |
| 2 | 阻断 | ext 受理后**热观察任务**在接管/关闭失败（门面返回 `Reconciling`）时无人消费 ⇒ 终态可能只留在被遗弃的任务里 | **部分已修**：宿主消费条件改为「有完整发送身份＋确有完成事实」（覆盖 `Reconciling` 暂存结算）；**残余**＝接管失败后由恢复扫描接续（见 §24.21-C-2） |
| 3 | 阻断 | 24h／`not_found`／瞬态后不再观察，仅「不写终态」不足以保证「停驻≠放弃」 | **部分认领**：不写终态已满足（§24.20-B 禁止超时强制终局）；**持续观察/重绑**归 §24.12-3 集合②（未终结台账）→ 见 §24.21-C-2 |
| 4 | 阻断 | 早期层接口未成为**端口边界**（端口仍返回 `ExternalStartExecution`）；`already_executed` 直接制造无句柄 `Accepted` | **部分认领**：端口形状改造与两段式 `_gate` **同批**执行（见 §24.21-C-1）；`already_executed` 判定**保留**＝§24.11 第 3′ 行（同一发送身份已出现权威终态 ⇒ 本地未发送证明失效 ⇒ 先接管、后关闭、再终局）＋§24.7-2（无句柄路径以线上提交键登记替代查询依据）；**事后可剔除入口**：若 owner 要求严格按 §24.14-1 字面改为 `Unknown`，改动面＝`MapQueueEarlyToAdmission` 单分支＋一个夹具 |
| 5 | 阻断 | 启动/早期网络取证仍发生在门面 `_gate` 内（`DrainRoundAsync` → `ProcessRoundAsync` → `ProcessWinnerAsync` → `hooks.Sender`） | **认领为未完成项**（D6 残项）：两段式 `_gate` 为**下一批首项**（见 §24.21-C-1）；当前生产接线**关闭**，故无运行影响 |
| 6 | 重要 | `submit.Success=false` 一律视为「确定未受理且可重试」 | **已修**（封闭白名单，默认终局拒绝） |
| 7 | 重要 | `ObservedAtUtc` 在映射阶段重取；`completed＋Cancelled=true` 被改写成 `cancelled` | **已修**（接收时点随观察记录传递；原词不改写，取消事实由 `Kind` 承载） |
| 8 | 重要 | 本地取消链未落地（宿主传 `CancellationToken.None`，热观察不可取消） | **认领挂账**：归 R5.3 取消链（§24.11 全矩阵）批次；本批与拆分前**行为一致**（无回归），观察段已支持 `ct` |
| 9 | 重要 | 未知分支依赖调用方传 `acceptanceJobId`；多来源非空句柄冲突被静默择一 | **已修**（台账读回回退＋`terminal_job_id_conflict` 保守停驻） |
| 10 | 重要 | §24.14-5／§24.18-5 交错夹具缺失 | **部分已修**（新增「完成观察等待期间其他请求可取得门面锁」）；其余归下一批与 §24.4 组合根夹具（见 §24.21-C） |
| 11 | 建议 | 保持 E3/E4/E5 生产接线关闭 | **遵循**（本批未开任何生产接线） |

**B′. 验证会诊（第二轮，同一批次；gpt-5.6-sol／medium）与处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 12 | 阻断 | 处置 2 未闭合：`SettleCompletionAsync` 返回 `not_accepted` 时宿主「保留原拒绝」会**静默丢掉**同一发送身份上新的权威终态（§24.2-2″「已拒绝后收到冲突证据」） | **已修**：新增 `TaskCenterHost.RegisterObservedTerminalConflictAsync`——`not_accepted` ＋**权威终态**（非 `Unknown`、带观察时点/原词/完整发送身份）时原子追加冲突证据并置冲突待决（返回 `NeedReconcile`＋责任 `Pending`＋禁止重发；既有拒绝本体不改写，仅由 `Superseded*` 审计表达取代关系）；证据不完整/登记被拒/异常时保留门面原结论。夹具：`ExternalStart_RejectedThenAuthoritativeTerminal_RegistersConflictEvidence` |
| 13 | 重要 | 权威终态映射仍可 `ObservedAtUtc ?? UtcNow` 补时点（等于把映射时刻伪装成证据接收时刻） | **已修**：`MapQueueObservationToCompletion` 对权威终态**缺观察时点/默认值 fail-closed**（返回 `Unknown`，不写终态载体）；夹具补「缺时点 ⇒ `Unknown`」断言，其余夹具一律显式提供接收时点 |

**C. 未完成项登记（带批次归属，禁止悬空）**

1. **D6 两段式 `_gate`（§24.14-2／§24.18-2）**：**已落地**（§24.22-A：外部启动的启动/取证在门面锁外执行＋锁外并发不改变本笔结算，两枚夹具）；**仍余**：①**流程登记/节点执行**的锁外窗口——实测放开后 `TaskCenterSuccessorPathGateTests` 两条既有夹具转红（E1 启动发送窗口内提交的节点操作被并发轮次按占用冲突拒绝 ⇒ 节点操作停留 `Queued`、`sends=0`、运行收敛 `Unknown`），故须与「**在飞父操作（已发布发送责任、尚未结算）的占用/合并判定**」（§3.1／§4.2a／§24.1-5/7）一并调整，归 **B2-γ 批次**；②端口形状按 §24.14-1 收敛为 `ExternalStartReply`（`EarlyAccepted／Rejected／Unknown`）＋完成观察（＝`CompletionObserver` 的正式化）；③恢复扫描**四类集合**（含**集合②未终结台账记录**）接线，使「无权威证据时长期保守停驻」具备**持续观察/重绑入口**（§24.12-3／§24.14-6；对应 §24.21-B 阻断 2 残余与阻断 3）。
2. **同批夹具**（§24.14-5／§24.18-5／§24.4-4）：`CompletionTask` 未完成时其他请求取得门面锁（**本批已补**）、终态先于接管/关闭的唯一顺序、接管失败后终态由当前所有者/恢复路径结算、本地取消与调用方退出后的观察责任移交、换主接续、重复结算与等待器单次释放、**真实回退 core 的 v2 普通 success 保持 `Accepted/Pending`**（需 §24.4 组合根的真实传输接缝）。
3. **归 R5.3 取消链批次**：§24.11 全阶段矩阵的**本地取消**实现（入口取消 + 责任 `Pending` + 后台观察继续的分离语义）与 `ct` 贯通。
4. **[更新·2026-09-21 批次八]** 本清单第 1 条（D6 残项）现状：**②端口形状**已按证据处置（§24.23-B：保留 `ExternalStartExecution`＋事后可剔除入口）；**③恢复扫描集合②/③**已落地（§24.23-A），集合①（未决 `Submission.Submitting`→`Reconciling`）与集合④（`conflict.pending` 不得被通用恢复改写）由既有 `RecoverAfterRestart` 承载并有既有夹具；**仅①流程登记/节点执行的锁外窗口**仍待 B2-γ（须与在飞父操作占用判定一并调整）。

5. **[更正·2026-09-21 批次八·第二轮验证会诊]** 上条措辞收窄（避免与 §24.23-C 矛盾）：**集合②/③的「扫描识别＋责任保留」已落地**（§24.23-A）；其中**持续观察重绑/重新取证未完成**，由 §24.4 组合根批次（真实传输接缝到位后）承接（§24.23-C#13）；**端口形状**为 **owner 裁决项·未完成**（§24.23-B）。
6. **[口径统一·2026-09-21]** 上条（第 4 条）中「②端口形状**已按证据处置**」与「③恢复扫描集合②/③**已落地**」两句表述**以第 5 条为准**：端口形状＝**owner 裁决项（未完成）**；集合②/③＝**扫描识别＋责任保留已落地、持续观察重绑未完成**（§24.4 组合根批次承接）。

### 24.22 落地登记：Batch B 收尾之四（两段式 `_gate`·外部启动锁外窗口）（[新增·2026-09-21]）

**A. 落地事实**

| 落地项 | 实现位置 | 条款 |
|---|---|---|
| 占位/发布发送责任与 `PreObservation`（锁内）之后，**释放 `_gate`** 再调用适配层启动/早期取证（锁外），返回后**重新取得权威串行权**再结算 | `ArbitrationAdmissionService.ProcessWinnerAsync`（`releaseGateDuringSend` 分支） | §24.18-2／§24.14-2（与 §24.10-2「完成等待不在门面锁内」同源） |
| **锁外窗口按操作类型分派**：仅 `OperationType.ExternalStart` 走锁外；流程登记/节点执行保持既有单段串行（逐字不变）。类型来源＝**已持久化**的 `OperationRecord.OperationType`（不得用请求字段决定生命周期分支） | 同上（读 `occupy.File` 中本操作记录的类型，§24.17-2／§24.8-1） | §24.14（外部启动生命周期）／§24.8-3（不回归既有路径）／§24.17-2 |
**C′. 第二轮（窄范围验证）处置补充**

- **阻断 6 的加固（§24.18-3 全维度）**：`DetectSendResponsibilityAdvanced` 扩展为逐项核对「①当前租约 `leaseId／ownerEpoch` 仍等于**占位基线**（锁内以值快照捕获，不信对象引用）；②同名操作的**请求状态**未被他人推进；③`submissionIdentity＋sendSeq` 在操作与 Submission 两侧仍是占位那一轮」。任一不成立 ⇒ 保守停驻且**零台账写入**。新增夹具 `ExternalStartSend_OwnershipChangedDuringSend_LateAcceptStopsBeforeLedgerWrite`（换主但发送身份未变 ⇒ `owner_or_lease_changed_during_send`＋责任 `Pending`＋台账 `Entries` 为空）与 `ExternalStartSend_ResponsibilityAdvancedDuringSend_LateAcceptStopsBeforeLedgerWrite`（换主恢复把本笔转 `Reconciling` ⇒ `send_responsibility_advanced_during_send`）。
- **残余（如实登记，非本批可闭合）**：本复核（读租约）与随后 `TakeoverPersist`（**独立文件**）之间不存在跨文件原子边界，跨进程处理者仍可能在此缝隙内推进；该缝隙按 §24.12 恢复集合②/③（未终结台账、台账已终态而 Operation 未终局）与冲突裁决处置。若后续要求更强的跨文件原子协议，需 owner 级裁决（本批不擅自引入新的事务机制）。

### 24.23 落地登记：恢复扫描集合②/③ 与「早期层端口形状」处置（[Batch B 收尾之五·新增 2026-09-21]）

**A. 落地事实（恢复扫描 = §24.12-3 集合②/③；D6 残项）**

| 落地项 | 实现位置 | 条款 |
|---|---|---|
| **台账扫描钩子**：按完整发送身份返回「是否已终态」；不可读/未配置＝**不可确认**（保守停驻，不得当作「无记录」） | `AdmissionHooks.TakeoverLedgerScan`／`TakeoverLedgerScan`／`TakeoverLedgerFact` | §24.12-3／§24.16-3 |
| **集合②未终结台账**：**保留观察责任**——不改状态、不写终态载体、不释放占用、不重发（无可用查询依据时长期保守停驻） | `ArbitrationAdmissionService.RecoverExternalStartObservationsAsync` | §24.12-3①／§24.16-3／§24.20-B |
| **集合③台账已终态、Operation 未终局**：用**已持久化 `PendingTerminal`** 补终局（`Kind` 为唯一类别判据；`Unknown`／载荷缺失**不驱动**） | 同上 ＋ `CompletionFromPendingTerminal` | §24.12-3③／§24.15／§24.19-2 |
| 其余不一致（台账终态缺 `PendingTerminal`／租约侧已终态而台账未终结／台账孤儿）**只计数登记**，不改动任何责任 | 同上（报告 `ExternalStartRecoveryReport`） | §24.12-6／§24.2-2″（禁止静默释放或补造事实） |
| **冲突待决记录一律跳过**（`conflict.pending=true`）——即使台账已终态、本地已有 `PendingTerminal`，也**不走普通完成结算**（避免绕过裁决审计并误释放占用） | 同上（报告项 `ConflictPendingSkipped`） | §24.12-3④／§24.15 冲突行／§24.2-2″ |
| **补终局不得补造载荷**：`PendingTerminal` 缺原始终态词/观察时点/证据来源，或 `Failed` 缺执行错误码 ⇒ **不驱动结算**（只登记） | `CompletionFromPendingTerminal` | §24.2-2／§24.6-2／§24.12-6 |
| 报告成功判据＝**责任已结清**（`ResponsibilityState.Settled`），不得仅凭结果维（`Cancelled`／`ExecutionFailed`）记成功 | 同上 | §24.6-5／§24.15 |
| 宿主接线：门面组装完成（心跳先行）后执行一次恢复对齐；**异常只记日志、绝不影响启动**；锁内不得 `await`（锁外执行） | `TaskCenterHost.EnsureAdmissionFacadeAsync`／`CompleteAdmissionInitAsync` | §24.12-3／§24.14-6 |
| 夹具（+3） | 门面级 `RecoverObservations_UnterminatedLedger_KeepsResponsibilityOnly`／`RecoverObservations_LedgerTerminalButOperationNotFinal_TerminalizesFromPendingTerminal`；宿主级 `Recovery_UnterminatedLedger_KeepsObservationResponsibility` | §24.12-3（集合②/③ 的行为证据） |

**B. 「早期层端口形状」＝待 owner 裁决项（对应 §24.21-C-1②；本批**不**视为已处置完成）**

- §24.14-1 要求适配层 `StartAsync` 返回 `ExternalStartReply`（`EarlyAccepted(jobId, CompletionTask)`／`Rejected`／`Unknown`），且本仓实现对 `EarlyAccepted` **强校验非空句柄**（无句柄构造即抛）。
- 但 **v2（E3 现网阻塞协议）不携带早期句柄**：发送成功时 `JobId` 为空，而 §24.4-4 明文要求「**v2 发送成功：入口 success**，但台账保持未终局」。若把端口返回类型**整体**收敛为 `ExternalStartReply`，该路径只能落 `Unknown`（§24.14-1 末句）⇒ 对外 `result_unknown`，与 §24.4-4 直接冲突（会回归既有 v2 启动语义）。
- 故本批**保留**端口类型 `ExternalStartExecution`（其 `Accepted` 允许空句柄，正是 §24.7-2「无 JobId 路径必须登记替代查询依据」的载体），而**早期层回执类型 `ExternalStartReply` 在 ext 队列通道内部真实承载并经夹具断言**（批次六）。
- **登记为 owner 裁决项**（[验证会诊阻断处置]）：冻结稿 §24.14-1 与本仓已验收的 §24.4-4 在 v2 阻塞协议上**互相冲突**，施工方无权以登记覆盖冻结合同，故本项**未完成**，须 owner 二选一：**(甲)** 修订 §24.14-1（为阻塞式协议增设「无句柄的已受理」变体，或明文声明 v2 结论归完成层）后本批按新合同收敛端口；**(乙)** 维持现状（端口 `ExternalStartExecution`＋`ExternalStartReply` 在 ext 队列通道内部承载），并在 §23.4 门禁并集登记该差异为「已书面接受」。任一选择前，**生产外部启动接线保持关闭**。

**C. 本批会诊（批次八：gpt-5.6-sol／medium，一轮＋第二轮验证）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 10 | 阻断 | 恢复扫描未排除 `conflict.pending` ⇒ 冲突待决记录若同时具备台账 Terminal 与 `PendingTerminal`，会被普通结算绕过裁决审计 | **已修**：分类阶段遇 `ConflictPending` 一律跳过并计数（`ConflictPendingSkipped`）；夹具 `RecoverObservations_ConflictPendingRecord_IsSkippedNotSettled` |
| 11 | 阻断 | 端口形状未按 §24.14-1 收敛，登记不能替代冻结合同 | **认领为 owner 裁决项**（见 §24.23-B：§24.14-1 与 §24.4-4 冲突，施工方无权自行覆盖）；本项**未完成**，生产接线保持关闭 |
| 12 | 重要 | `CompletionFromPendingTerminal` 为缺失载荷补造事实（空证据来源补字符串、`Failed` 缺错误码补 `"unknown"`） | **已修**：字段不完整即返回 `null`（不驱动结算）；夹具 `RecoverObservations_IncompletePendingPayload_DoesNotTerminalize` |
| 13 | 重要 | 集合②只有计数，未提供**持续观察重绑**；报告成功判据未看责任是否结清 | **部分处置**：`TakeoverLedgerFact` 增加 `JobId`（供重绑/重新取证使用）；报告成功判据改为**责任已结清**（`ResponsibilityState.Settled`）。**持续观察重绑（重新查询/重订阅）仍归 §24.4 组合根批次**（真实传输接缝到位后接线）；本批**只主张「识别＋保留责任」**，登记见 §24.21-C-1③（更新条目） |
| 14 | 重要 | 宿主恢复异常路径未用 `TryLog`（日志委托抛异常会逸出并使初始化失败）；`ct` 未按约定传播 | **已修**：改用 `TryLog` ＋ `ct.ThrowIfCancellationRequested()` |
| 15 | 重要 | 夹具未覆盖冲突待决旁路与载荷不完整路径 | **已修**：+2 夹具（见 #10／#12） |
| 16 | 阻断 | **第二轮验证**：`SettleCompletionAsync` 自身对 `ConflictPending` 记录放行（设计原为裁决路径而设）⇒ 恢复扫描「检查后置冲突」的竞态仍可绕过裁决审计 | **已修（本轮）**：`SettleCompletionAsync` 新增 `adjudicationAuthorized`（默认 `false`）——冲突待决记录**只**允许裁决入口在**先声明方向**后放行（`AdjudicateConflictAsync` 内部传 `true`），其余调用者一律 `conflict_requires_adjudication` 保守停驻 |
| 17 | 重要 | **第二轮验证**：载荷不完整路径未登记（`continue` 静默消失）；`ExecutionFailedWith` 仍留 `"unknown"` 合成回退 | **已修（本轮）**：新增报告项 `IncompletePendingPayload`（＋`ToString` 输出）并删除合成回退；夹具断言 `IncompletePendingPayload==1` |
| 18 | 重要 | **第二轮验证**：`ct` 仅在调用前检查、未传入恢复过程 | **已修（本轮）**：门面 `RecoverExternalStartObservationsAsync(CancellationToken ct = default)` 在分类前与逐笔结算间检查；宿主透传 `ct` |
| 19 | 重要 | **第二轮验证**：文档收窄不一致（§24.23-A／§24.21-C-1④ 仍称「集合②/③已落地」） | **已修（本轮）**：两处均收窄为「**扫描识别＋责任保留已落地；持续观察重绑未完成**（§24.4 组合根批次承接）」；另修正 §24.22 与 §24.23 的章节错位（批次七的 B/C 独立为 §24.24） |
### 24.24 批次七补记：实测反例与会诊处置（[新增·2026-09-21]；内容归属 §24.22，因登记次序错位而独立成节）

> **[更正·2026-09-21]** §24.22-A 表的两行（「锁外并发安全」与「夹具：外部启动发送窗口…」）此前误落在 §24.23-C 表尾；本轮已移回本节下表。

| 落地项（§24.22-A 补） | 实现位置 | 条款 |
|---|---|---|
| 锁外并发安全：锁外期间其他请求可完成准入；返回后按既有写入时校验（`MutateHandoffLatest` 的 owner/epoch/revision）与逐回调发送身份校验结算——**换主/接管时旧身份写入一律响亮拒绝** | 同上 ＋ 既有 `Checks` | §24.18-3／§24.18-4（证据＝既有 `OwnershipChanged_OldFlowWrite_LeaseStaleGeneration`、`StateAdvancedExternally_OldRoundDoesNotOverwrite`） |
| 夹具：外部启动发送窗口内另一笔请求可完成准入（旧实现超时失败）＋边界对照（流程登记仍串行） | `ArbitrationAdmissionServiceTests.ExternalStartSend_ReleasesGate_OtherRequestAdmittedDuringSend`／`FlowRegistrationSend_KeepsGate_SerializedUntilSendReturns` | §24.18-5（部分） |

**A. 实测反例（登记为流程登记/节点执行放开的前置条件）**：把锁外窗口无条件放开后，`TaskCenterSuccessorPathGateTests` 的
`AcceptedReceipt_PersistedBeforeSubmissionClose` 与 `ConcurrentRunWriteDuringSend_MergeRefused_ConvergesUnknown` 转红——
E1 流程启动的发送窗口内提交的节点操作被并发轮次按占用冲突拒绝（节点操作停留 `Queued`、`sends=0`、运行收敛 `Unknown`）。
结论：该两类的锁外窗口**不能**单独放开，须与「在飞父操作占用/合并判定」一并调整（见 §24.21-C-1①）。


**B. 本批会诊（批次七：gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 5 | 阻断 | 锁外窗口按**请求字段** `AdmissionRequest.OperationType` 分派 ⇒ 续用/重驱动（请求不回填类型）会走错分支，伪造请求更可令持久化为流程登记/节点执行的操作进入锁外窗口 | **已修**：改读**已持久化的 `OperationRecord.OperationType`**（§24.17-2／§24.8-1） |
| 6 | 阻断 | 重取 `_gate` 后**首个动作即台账副作用**（`TakeoverPersist`）——锁外期间本笔发送身份若已被他人推进（换主恢复/对账判「确定未受理」/关闭/轮次改写），会出现「租约侧拒绝、台账侧受理」的分裂，且旧身份会覆盖新所有者名下的责任 | **已修**：新增 `DetectSendResponsibilityAdvanced`（**仅锁外窗口生效**）——结算与任何副作用**之前**复核「本笔 `submissionIdentity＋sendSeq` 仍是占位那一轮且 Submission 在册」，不成立即**保守停驻**（`NeedReconcile`／责任 `Pending`／禁止重发／保留占用；不写台账、不关闭）。夹具：`ExternalStartSend_ResponsibilityAdvancedDuringSend_LateAcceptStopsBeforeLedgerWrite` |
| 7 | 重要 | `BuildDispatch` 被移出「捕获异常⇒Unknown」归类 ⇒ 派发快照异常时由外层落 `internal_error`，占位后状态与既有行为不一致（影响所有类型） | **已修**：`BuildDispatch` 保留在原归类内（哨兵夹具不变） |
| 8 | 重要 | 新夹具未覆盖「发送异常后重取锁的配对/可用性」与「责任被推进后迟到受理」两类交错 | **部分已修**：+2 夹具（责任推进 ⇒ 停驻且**零台账写入**；外部启动 Sender 抛异常 ⇒ `Reconciling` 且**后续请求仍能取得门面锁**）。仍未覆盖：同一身份被**终态**证据推进后的裁决入口联动（归冲突批次） |
| 9 | 建议（登记） | 非终态「曾受理」证据与「确定未受理」之间的**冲突词表口径**（`ConflictEvidenceRecord.RawTerminal` 目前只表达终态词） | **登记挂账**：归冲突裁决批次（不得为凑语义伪造终态词，§24.2-2″） |
### 24.25 落地登记：§24.4 生产组合根闭环夹具（第一批：外部启动 ext 闭环）（[新增·2026-09-21]）

**A. 组装与传输替换（真实组装、仅替换外部传输）**

- **组装**＝真实 `CommandExecutor`（E3 接线）→ 真实 `TaskCenterHost`（真实仲裁门面＋真实接管台账＋真实 `BgiExternalClient`）→ 真实 Core；`MainViewModel` 的真实构造路径由既有 `R410ProductionWiringTests` 覆盖（本批不重复构造，登记为组合根的 `MainViewModel` 侧证据）。
- **传输替换**＝新增**仅测试**接缝 `IpcClient.PipeNameOverrideForTest`／`BgiExternalClient.PipeNameOverrideForTest`（**生产恒 `null`**；夹具在 `InitializeAsync/DisposeAsync` 成对设置/还原，并以 `[CollectionDefinition(..., DisableParallelization = true)]` 保证静态覆盖不与其它测试并行）+ 进程内管道替身 `BgiInstancePipeDouble`：帧格式与生产一致（`[4 字节长度][1 字节 type=1][v2 信封 JSON]`），实现 v2／ext 协议子集（`ping`／`task.status`／`task.start`／`ext.hello`／`ext.task.start`／`ext.task.queueStatus`）与 `ext.event` 事件推送。

**B. 已覆盖场景（`R5CompositionRootAcceptanceTests`，三枚夹具；仅替换传输、不替换任何判定/责任逻辑）**

| 场景 | 断言 |
|---|---|
| §24.4-1 连续两次成功启动 | 两次均 `success`；第二次不被第一次未终结台账阻断；`ext.task.start` 恰两次（无重发） |
| §24.4-2 ext `Completed` 闭环 | 台账 `Terminal`＋`JobId` 可读；外部 Operation `TerminalCompleted`；主槽位（Submission）释放；`ExecutionResult.Kind=Succeeded`、`RawTerminal="completed"` |
| §24.4-3 权威取消终态 | `task.queueCancelled` ⇒ 入口 `failed`＋`cancelled`（结果维 `Cancelled`）、责任 `Settled`；台账 Terminal／Operation 终局／**不重建** Submission |
| §24.4-4 v2 发送成功 | ext 不可用（对端老 BGI）⇒ 回退 v2；入口 `success`、`IsTerminal=false`、责任 `Pending`；台账保持 `AcceptedPendingExecution`、无 `ExecutionResult`／`PendingTerminal` |

**C. 本批发现并修复的实现缺口（组合根验收的直接产出）**

- **D9 贯通缺口**：v2 路径「获准且核心已执行 ⇒ 回核心结果」原样返回核心 `CommandResult`，**丢失结果维/责任维**（`ResponsibilityState=None`）。已修：在**线路字段逐字保留**的前提下，按准入结论补齐 `ResponsibilityState`／`JobId`／`EvidenceSource`／`ExecutionDisposition`（核心已给更强事实时不改写），使「普通受理 ⇒ 责任 `Pending`」贯通到调用方（§24.6-2／§24.6-5／D9）。

**D. 仍未覆盖（归 §24.4 第二批；禁止悬空）**

1. §24.4-5 终态落盘失败交错（关闭前 `PendingTerminal` 首写失败／关闭后补写失败）与 §24.4-3 的「关闭前 `Reconciling` 断言」细分；
2. §24.4-6 **大于 32 笔**外部启动完成后的容量与终局释放（在替身上跑 33+ 轮闭环）；
3. §24.4-7 冷启动／授权纪元变化／切监控模式／宿主关闭交错；
4. §24.8-3 的「默认未注入路径（生产门仍关闭）＋控制热键」在**组合根层面**的显式断言（组件级默认门关闭已有既有夹具覆盖）。

**E. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 20 | 重要 | 替身可推送**未订阅**事件 ⇒ 事件链存在假通过风险（掩盖生产漏订阅/订阅集不含终态事件） | **已修**：替身实现 `ext.event.subscribe`（记录订阅集；空数组＝全部）并**拒绝未订阅事件的推送**；夹具镜像生产启动步骤（`MainViewModel.BgiExternal`：Ready 且未激活时 `SubscribeAsync([])`）并断言订阅已发生 |
| 21 | 重要 | D9 贯通仍不完整：`RawTerminal`／`ExecutionErrorCode` 未贯通到适配器可见结果 | **已修**：`MapAdmissionOutcome` 各分支与「核心回执补齐」路径均投影 `RawTerminal`／`ExecutionErrorCode`。**口径说明**：`SubmissionIdentity`／`SendSeq` 属 `AdmissionResult`／`ExternalStartAdmissionOutcome` 合同（D9），**不在** D1 的 `CommandResult` 字段清单内——`CommandResult` 层不重复承载，避免两套权威发送身份 |
| 22 | 重要 | §24.4-2「主槽位释放」断言不足（Submission 关闭 ≠ 计容释放） | **已修**：断言终局操作**已迁出 `Active` 计容区**（`Zone != Active`）且当次快照中**不存在 `Active` 操作**（§24.1-3） |
| 23 | 建议 | 静态接缝未还原原值（无条件置 null） | **已修**：保存并还原初始化前的原值 |

### 24.26 落地登记：§24.4 生产组合根闭环夹具（第二批）（[新增·2026-09-21]）

**A. 已覆盖场景（`R5CompositionRootAcceptanceTests` 扩充至 8 枚夹具；仍为「真实组装、仅替换外部传输」）**

| 场景 | 断言 |
|---|---|
| §24.4-6 **大于 32 笔** | 33 轮闭环全部 `success`；`ext.task.start` 恰 33 次（无重发）；结束时**无任何 `Active` 计容操作**；台账 33 条均为 `Terminal` |
| §24.8-3 **默认未注入路径（生产门仍关闭）** | `CommandExecutor` 不注入准入委托（＝当前生产默认）⇒ 走既有直启 ext 队列通道（旧词表「队列通道」文案保留）、**不产生任何仲裁操作、不写外部启动台账**（门关闭的可观测证据） |
| §24.4-5 **终态落盘失败** | 台账置只读 ⇒「台账 Terminal」步失败：入口**不得假成功**、责任 `Pending`、**零重发**；`PendingTerminal` 已持久化（§24.12-6 在飞责任保留） |
| §24.12-3② **未终结台账**（承上） | 恢复写权限后重启：属集合② ⇒ **只保留观察责任**（不得据此终局、不得重发） |
| §24.12-3③ **台账已终态、Operation 未终局**（承上） | 恢复终态副本后再重启 ⇒ 恢复扫描按已持久化 `PendingTerminal`**补终局**（台账 Terminal＋Operation `TerminalCompleted`，全程零重发） |
| §24.4-7 **冷启动** | ext 不可用＋IPC 不可达 ⇒ 接线态**禁止裸拉起、零发送**；核心给 `cold_start_required`、适配器按保守口径映射为对外不可考，责任 `Pending`（§14 细化前不得升格为确定拒绝） |
| §24.4-7 **宿主关闭交错** | 受理后宿主关闭（租约释放）再送达权威终态 ⇒ **不得假成功、不得重发**；责任 `Pending`、台账保持未终结（由恢复/当前所有者承接） |

**B. 本批发现并修复的实现缺口**

- **§24.6-5 责任维缺口（组合根关闭交错直接产出）**：`SettleCompletionAsync` 在「租约不可用／Operations 记录缺失」等**结算无法执行**的路径上返回 `ResponsibilityState=None`（语义＝不适用），会让调用方把「责任未结清」误读为「无责任」。已修：这两条路径在**携带完整发送身份**时回显 `ResponsibilityState.Pending`＋`SubmissionIdentity`＋`SendSeq`＋`ExecutionDisposition=Unknown`（§24.6-5：发送结果未知／关联不足／持久化失败 ⇒ `Pending`）。

**C. 仍未覆盖（登记；归后续批次或 owner 实机）**

1. §24.4-7 其余交错：**授权后纪元变化**（含旧 epoch 拒绝/待对账）、**切监控模式**；
2. §24.8-3 的**控制热键**在组合根层面的接线/未接线双向断言（组件级已有夹具覆盖）；
3. §24.4-3 的「关闭前 `Reconciling` 断言」细分（本地等待取消 vs 权威取消）；
4. owner 实机项（十入口点、后台触发器、R-8 远程五场景、`task.single.native` 复核、真实 User 目录切换）——按 §23.4 门禁并集保留。

**D. 本批会诊（第二轮，gpt-5.6-sol／medium）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 24 | 阻断 | `SettleCompletionAsync` 的「结算无法执行」返回把**未登记**与**已登记但无法结算**混同（无条件 `Pending`），且会把已观察的取消/失败降级为不可考 | **已修（按可证明事实分流）**：请求身份＋完整发送身份（`submissionIdentity`＋`sendSeq≥1`）齐备 ⇒ 责任 `Pending` 并回显发送身份；**不齐备 ⇒ 维持 `None`**（不得把未知请求误标为有责任）。同时以 `DispositionOf` **保留完成层事实**（`Cancelled`／`ExecutionFailed`／`Succeeded`／`Unknown`＋原始终态词＋执行错误码＋证据来源），不再一律降为 `Unknown`（§24.6-2／§24.6-5） |
| 25 | 重要 | >32 容量夹具只断言「无 `Active`」，未覆盖**同样计容**的 `TerminalPendingTransfer` | **已修**：断言 `Active` 与 `TerminalPendingTransfer` **双双为 0**（容量公式 `Active + TerminalPendingTransfer ≤ 32`，§24.1-3／§24.1-8） |
| 26 | 重要 | 关闭交错夹具仅等「替身收到 `ext.task.start`」即释放租约，可能实际覆盖「尚未受理即释放」等其它分支 | **已修**：关闭前等待并断言「台账 `AcceptedPendingExecution`＋Submission 已关闭＋Operation `Accepted`」，确保交错发生在**受理链完成之后**（§24.4-7／§24.15） |
| 27 | 重要 | 冷启动错误码断言过宽（`cold_start_required` 与 `result_unknown` 二选一） | **已修**：本夹具观察**适配器出口**，精确断言 `result_unknown`（不得泄漏核心层错误码）；并在夹具注释说明 `cold_start_required` 只由**接线态核心**产生，未接线冷启动走既有裸拉起语义（另有组件级夹具） |

### 24.27 落地登记：§24.4 生产组合根闭环夹具（第三批：切模式闸门与控制热键）（[新增·2026-09-21]）

**A. 已覆盖（组合根夹具增至 10 枚）**

| 场景 | 断言 |
|---|---|
| §24.4-7 **切监控模式（切换闸门）双向** | 置 `Diag.SwitchGateActive` ⇒ 新启动被**拒绝且零发送**；解除闸门 ⇒ 启动恢复正常（切换闸门属控制面写入：不续命、不需所有权，§6.1） |
| §24.8-3 **控制热键双向（组合根层）** | **接线态** ⇒ 热键经统一仲裁面（产生 `hotkey:{名}` 仲裁操作，核心仍发 `action.execute_hotkey` IPC）；**未接线（＝生产默认）** ⇒ 热键直发 IPC 且**不产生任何仲裁操作**（既有语义逐字不变） |

**B. 替身同构改进（本批发现）**：管道替身原为**单连接**，导致「ext 客户端常驻连接」与「v2 IPC 新连接」互斥——热键组合根夹具实测出现**虚假的连接超时**（`IPC 快捷键失败: 连接命名管道超时`）。已改为**多实例并发**（`MaxAllowedServerInstances`，每连接独立服务、事件推送面向全部活动连接），与生产「同一管道名多实例」语义一致；此后 v2/ext 双通道在组合根夹具内可并存。

**C. 仍未覆盖（登记，禁悬空）**

1. §24.4-7 **授权后纪元变化**：门面/后继路径级已有 `stale_epoch` 夹具覆盖（见 §24.7-4 要求）；**组合根层的端到端纪元变更模拟**未做（需更深替身或 owner 实机），本批不以近似断言冒充；
2. §24.4-3「关闭前 `Reconciling`」的**本地等待取消**细分：归 §24.21-C-3 **R5.3 取消链批次**（与 `ct` 贯通同批）——当前组合根层已覆盖「权威取消终态」与「结算写入失败 ⇒ 保守停驻」两侧；
3. owner 实机项（十入口点、后台触发器、R-8 远程五场景、`task.single.native` 复核、真实 User 目录切换）按 §23.4 门禁并集保留。

**D. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 28 | 重要 | 切模式夹具只断言「非成功＋零发送」，可能因**其它门禁/瞬态失败**而假通过 | **已修**：改为**状态级因果证据**——被阻断候选只**登记**（`LastSendSeq==0`）、回到 `Queued` 可再驱动（非 `InRound` 悬挂）、零新增发送，并断言前提「闸门确实激活」（`Diag.SwitchGateActive`） |
| 33 | 重要（装饰器盲区加固） | 故障/取消夹具只断言最终异常类型 ⇒ 把同步异常包成 faulted `Task`、或捕获取消后重抛新 `OperationCanceledException` 的实现仍可通过 | **已修**：预构造**故障/取消任务实体**并 `Assert.Same` 断言**原样返回同一任务实例**；同步抛错改为在**同步调用点**断言原异常类型（不得包成异步故障） |
| 34 | 重要（P50 与交错⑤） | ①恢复执行 33 节点用例不足以关闭 §12.3 交错⑤（只证明最终态，缺「逐节点按自身发送身份终局并迁出计容区」的直接证据）；②不得据「3 轮通过」把 P50 降级 | **已修/已登记**：交错⑤**保留**在 §24.28-C（附理由：缺逐节点时点/Zone 直接证据，§16 交错⑤／§17 P19）；P50 **维持阻断式 Skip**（本批实测：启用后满负载 5 轮中 1 轮同宿主类夹具红灯 ⇒ 负载敏感性可复现），并明确诊断入口须覆盖**整个**用例类的负载敏感夹具 |
| 29 | 重要 | 替身多实例改造后**服务任务未被跟踪/等待**，`DisposeAsync` 存在「Release 已释放信号量 / 用已释放 CTS」竞态 | **已修**：跟踪全部服务任务；Dispose 先取消并关闭连接，**等待任务收敛**后再释放 `_writeLock`／`_cts` |
| 30 | 重要 | 事件订阅与广播**非按连接**维护 ⇒ 可能向未订阅的 v2 IPC 连接注入 `ext.event` | **已修**：订阅状态改为**按连接**（`_subscriptions`／`_subscribedAllConnections`），推送只发往**订阅了该事件**的连接；v2 连接不再收到事件 |
| 31 | 重要 | 热键「未接线零新增仲裁操作」只比数量，存在「新增一笔又裁掉一笔」的假通过风险 | **已修**：改为**操作身份集合逐项比对**（调用前后 `RequestIdentity` 序列相等），并断言不存在第二个 `hotkey:组合根热键` 操作 |
| 32 | 登记（本批尝试后回退） | 控制面门禁（`switch_gate_active`／`residue_reconcile_pending`）在适配器出口被压成 `result_unknown`；把 §24.6-1 的「前置门禁阻断」词汇扩展到控制面门禁需**同步修改分类语义**（本批实测：改动会改变既有已验证夹具的操作状态期望——`Queued` vs `InRound`） | **回退并登记为后续 refinement**：本批保持既有分类语义不变（不回归已验证行为），组合根夹具改用**状态级因果证据**；后续若引入通用 `Blocked` 结果维，须与 §24.6-1 条款、`AdmissionResultKind`、宿主映射与既有夹具**同批**变更 |

### 24.28 落地登记：B2-γ 第 3 步·首批（装饰器契约夹具＋P50 处置）（[新增·2026-09-21]）

**A. 装饰器契约夹具（关闭 §12.2「第 3 步其他强制项·夹具盲区」的三项盲区）**

| 盲区 | 夹具与断言 |
|---|---|
| 参数与令牌**原样透传** | `SubmitAsync_PassesRequestInstanceAndTokenVerbatim`：断言交给准入委托的是**同一 `WorkflowSubmitRequest` 实例**与**同一 `CancellationToken`**（不得重建请求、不得换令牌），且不回落 inner |
| 故障/取消**透明传播** | `SubmitAsync_PropagatesSyncThrowFaultedTaskAndCancelledTask`：同步抛错／故障 Task／取消 Task 三种形态**不得**被本层吞掉或改写（否则上层会把不可考误当确定未受理），且都不回落 inner |
| 能力值**复读**（不得缓存） | `Capabilities_AreReReadPerAccess_NotCachedAtConstruction`：运行期改变 inner 能力实况后，装饰器属性必须反映新值 |

**B. §12.3 交错⑤（连续超 32 节点）与 P50 处置**

- 原 `NodeSubmit_33NodeFlow_NoCapacityExhaustion`（宿主级端到端、断言严格）此前因 **P50**（满负载偶发第 N 节点收敛 `Reconciling→Unknown`）而 **Skip「暂停执行」**，且**复现入口未落实**。
- **本批实测（复现证据，[取代先前「3 轮未复现」的尝试结论]）**：取消 Skip 后，定向单跑通过；但**满负载全量套件 5 轮中出现 1 轮红灯**——同宿主类的负载敏感夹具 `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission` 收敛失败（保守方向、无双跑）。⇒ **负载敏感性真实存在且可复现**，启用该用例会以约 20% 概率污染基线。按「基线必须稳定 ＋ 断言不得放宽」纪律：**恢复 Skip**（断言保持严格、未放宽），P50 继续为**阻断式挂账**。
- **P50 状态（唯一权威口径；与 R5.2 稿 §17 P50 一致，本次为加强而非放宽）**：①**根因未定位**；②**诊断入口义务未落实**（须建成可单独、可重复运行的负载复现入口，且应覆盖**整个** `TaskCenterSuccessorPathGateTests` 类的负载敏感夹具，而非仅本用例）；③**生产侧负载敏感性未消除**；④**节点改道门（`_successorAdmissionWired` 生产构造不传）继续保留**。容量证据继续由**组件级确定性**夹具承担（`Capacity_33NodeCandidates_AllAccepted_WhenEachSettled`／`Capacity_MainSlotsExhausted_33rdCreateRejected`）。

**C. B2-γ 第 3 步仍未完成（登记，禁悬空）**

1. §12.2 第 3 项**不可变冻结身份**（`PreparedSubmit` 持可变 `Run/Submission` 引用 ⇒ 发送与对账须用同一份冻结身份、节点取固定流程快照并核对 occurrence/iteration/attempt/提交键/游标修订、Sender 与 Runner 走字段合并或版本守卫）；
2. §12.3 六类交错中**尚未强制/尚未直接取证**者：**⑤连续超 32 节点**——恢复执行的 33 节点夹具只证明「最终：33 次发送成功＋33 个节点操作已登记＋未耗尽容量」，**未**直接断言「第 2—33 次准入/发送时**前一节点已按自身发送身份终局并迁出计容区**」（§16 交错⑤／§17 P19 的「宿主链路逐节点释放直接证据」仍欠；最终通过也可能被其它提前释放路径满足）⇒ **该交错保留在本清单**（不得据本轮绿线关闭）；①首节点抢先（E1 未关闭时节点到达——需门面 `_gate` **外**的观察点，现有 `AdmissionBarriers` 全在锁内故会自死锁）、②调用者≠获选者（payload 完全属于获选者）、③准备阶段故障三态、④受理接管故障后续取消/重启、⑥入口覆盖（面板/移交/续行/恢复四类 Scope 来源）；
3. §12.3 M1 的**首节点授权衔接**（E1 授权对象＝宿主驱动登记；节点唯一性服从 §3.2a）与 M3（占位后「活进程准备失败」阶段边界 + 本地未发送证据关联）；
4. `_successorAdmissionWired` **生产构造仍不传**（第 3 步路径未启用）；启用须待上述项与本批承接项一并收口，且经 owner 书面确认。

**D. 与 R5.2 稿 §17「启用前置清单」的归并视图（避免两处口径漂移）**

| §17 条目 | 本轮（R5.3 §24 批次）状态 | 证据／交叉引用 |
|---|---|---|
| **P38** ext 队列通道拆分「早期受理通知」与「完成等待」 | **已关闭（实现＋夹具）** | §24.21-A／提交 `e1bab1786`（早期段/观察段拆分、`ExternalStartReply.EarlyAccepted`、旧词表逐字保留、门面锁外观察）＋夹具 5 枚；**「外部启用门」列要求不变**：生产接线仍关闭，开门由 R5.8 验收决定 |
| **P19②** 宿主链路**逐节点释放直接断言** | **仍欠（本轮再确认）** | §24.28-C-2⑤：恢复执行的 33 节点用例只证「最终 33 次发送/33 操作/未耗尽容量」，**不构成**逐节点时点/Zone 直接证据（§16 交错⑤）；该交错保留不关闭 |
| **P17** 首节点抢先（强制版，需门面锁**前**观察点） | **仍欠（归 B4 组件屏障）** | §24.28-C-2①；本轮登记理由：现有 `AdmissionBarriers` 全在 `_gate` 内，锁内等待节点占位会自死锁 |
| **P50** 负载敏感收敛（33 节点夹具） | **仍欠（阻断式 Skip 维持）**；本轮获得**复现证据** | §24.28-B：启用该用例后满负载 5 轮中 1 轮同宿主类夹具红灯（保守方向、无双跑）⇒ 维持 Skip；诊断入口须覆盖整个用例类 |
| **P6** 发送段取消令牌未透传 | **仍欠（归 R5.3 取消链批次）** | §24.21-C-3；本批只做端口/门面层 `ct` 贯通（§24.26-B），**不**声称发送段取消链已闭环 |
| **P8** 失败分类细化 | **仍欠**（本轮仅补队列通道**可重试白名单**，不等价于通用分类） | §24.21-A（`IsRetryableQueueRejection`）；通用「网络前可证实未发送 vs 已发送后失败」分类未闭环，非 success 仍一律 Unknown |
| **P54** 适配器取消传播与执行失败分类显式验收 | **部分已补** | `CommandExecutorExternalStartAdmissionTests`（`ToExecution`/`ToCompletion` 分阶段映射、取消/失败/未知三态）＋本轮 §24.26 的 `MapAdmissionOutcome` 字段贯通；**接管失败**映射的显式断言仍归 B4 |
| **本轮新增登记（不在 §17 表内）** | ①**端口形状**＝owner 裁决项（§24.23-B）；②**控制面门禁 `Blocked` 词汇扩展**＝refinement（§24.27-D#32）；③**集合②持续观察重绑**归 §24.4 组合根批次（§24.23-C#13）；④**授权后纪元变化的组合根端到端模拟**未做（§24.27-C-1）；⑤**跨文件 TOCTOU 残余**（§24.22-C′） | 见对应章节；均**未主张已完成** |
### 24.29 落地登记：B2-γ 第 3 步·次批（§12.3 交错①「首节点抢先」强制版）（[新增·2026-09-21]）

**A. 落地事实**

| 项 | 实现／证据 | 条款 |
|---|---|---|
| **仅测试观察点：节点准入入口、取得门面锁之前**（**生产恒 `null`＝空操作**；只发信号＋只读快照，**不得改变任何状态**） | `TaskCenterAdmissionSeams.BeforeSuccessorAdmission`；在 `SubmitSuccessorViaAdmissionAsync` 中于 `EnsureAdmissionFacadeAsync`（＝取租约/门面锁）**之前**调用 | §17 P17／§12.3 交错①（观察点必须在**锁外**——`AdmissionBarriers` 全在锁内，锁内等待会自死锁） |
| E1 侧强制阻塞点（夹具侧）：首轮「Accepted 后、台账前」阻塞 | `ProbeNodeSubmitRoutingAsync(..., holdFirstAccept, beforeSuccessorAdmission)` | §12.3 交错① |
| 夹具 `NodeAdmission_BeforeGateObservation_E1StillOpen_NoChildPermitYet` | 节点到达准入入口时断言：**E1 的 Submission 仍开**（父责任仍在）＋父操作未终局＋**子许可 0**＋**子发送 0**（`RoutingFakePort` 只统计节点发送）；放行后流程正常收口（`Succeeded`＋恰 2 次节点发送）＝**不自拒、不重复占位、不循环等待** | §12.3 交错① |

**B. §24.28-C-2 交错清单更新**

- **①首节点抢先：强制版夹具已建 ⇒ 组件层关闭**（真实入口证据仍属 §23.1，owner 侧，未闭合）。
- **②调用者≠获选者、③准备阶段故障三态、④接管故障后续取消/重启、⑤逐节点释放直接证据、⑥四类入口 Scope 来源**：**仍未做**；其中 **⑤ 不得据绿线关闭**（§24.28-C-2⑤：33 节点用例只证最终态）。
- **②调用者≠获选者、③准备阶段故障三态、④接管故障后续取消/重启、⑤逐节点释放直接证据、⑥四类入口 Scope 来源**：**仍未做**；其中 **⑤ 不得据绿线关闭**（§24.28-C-2⑤：33 节点用例只证最终态）。

**C. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 35 | 重要 | 夹具对「父操作未终局」的断言可能假通过（`parentState` 预置为非终局；未要求父操作唯一命中；未证明当前开放的 `Submission` 属于该父操作；仅排除 `TerminalCompleted`） | **已修**：预置值改为**终局态**（未命中即必须失败）；要求**父操作唯一命中**＋**其 `SubmissionIdentity` 与当前开放 Submission 全等**；状态断言改为**非终局集合**（`Queued/InRound/Granted/Sending/Accepted/Reconciling`） |
| 36 | 重要 | 接缝接收可变的 `WorkflowSubmitRequest` ⇒ 「不得改变状态」只靠注释，留下误用面 | **已修**：接缝签名改为**无参** `Func<Task>`（不向测试接缝暴露任何生产对象） |
| 37 | 重要 | 夹具失败时可能**永久悬挂 E1**（`holdE1` 仅在断言全通过后释放；无外层兜底）＋观察回调无重试连读租约 | **已修**：断言段 `try/finally` **无条件**放行 E1；外层 `finally` 再次放行并**等待探针任务收敛**（吞异常以免遮蔽真实断言）；租约快照改为**有界重试**（20×5ms）并断言「必须读到一致快照」 |
| 38 | 建议 | 新夹具后残留重复 `/// <summary>`；33 节点夹具注释仍称「交错①强制版仍欠」，与 §24.29-B 矛盾 | **已修**：删除重复标记；33 节点夹具注释改为**显式取代**（交错① 强制版已由 §24.29 承接，本夹具只保留交错⑤容量证据） |
### 24.30 落地登记：§12.3 交错⑤「逐节点释放直接证据」（[新增·2026-09-21 批次十四]）

**A. 落地事实**

| 项 | 实现／证据 | 条款 |
|---|---|---|
| **逐节点释放直接取证夹具** `NodeSubmit_EachSendObservesPreviousNodeReleased`（**会诊加固后**；[更正·2026-09-21] 节点数经 §24.32 批次十七**降载 6→4**，下列数字为**实际值**） | **4 节点**流程（原 6 节点，为降低 `DisableParallelization` 集合内负载而缩减，逐次取证语义不变）；发送入口用**实际 payload 的 `configName` 把「第 k 次发送」关联到具体节点**（**不得按发送序号推断**）；同一时点断言：①前节点 Operation **唯一命中**（记录数必须为 1）且 `RequestState == TerminalCompleted`、`Zone == Tombstone`（**真正迁出计容区**——`TerminalPendingTransfer` 仍计容，故不得接受）、`SubmissionIdentity` 非空；②**当前节点 Operation 已在 `Zone == Active`**（本轮占位）。共 **3 次逐次取证**（第 2..4 次发送），任一违规即失败并给出 state/zone/记录数诊断。**实测通过** ⇒ `TerminalPendingTransfer → Tombstone` 的迁出在该时点确实已完成 | §4.1a 三区容量口径／§12.3 交错⑤／§16 交错⑤／§17 P19② |
| 取证纪律 | 有界重试（20×5ms）吸收满负载下租约文件锁瞬时争用；**不依赖**被 P50 暂停的 33 节点用例（该用例仅作最终态证据） | §16 交错⑤／P50 |

**B. §17 P19② 状态更新**：**宿主链路逐节点释放的直接断言已补**（组件/宿主层，**4 节点逐次取证**（[更正·2026-09-21] 批次十七 6→4 降载；第 2..4 次共 3 次观察）＋既有 2 节点 G8 夹具）——原「仍欠」表述由本节取代。**仍未关闭**：P19①「原因码逐次取证」（需在准入结果层逐次记录 `operations_capacity_full` 等拒绝原因码，当前只以「33 个不同节点操作全部登记成功且身份唯一」间接约束）。

**C. §12.3 交错清单（更新后）**：①首节点抢先＝**组件层已关闭**（§24.29）；**⑤逐节点释放＝组件/宿主层已补直接证据**（本节）；仍未做：②调用者≠获选者、③准备阶段故障三态、④接管故障后续取消/重启、⑥四类入口 Scope 来源，以及 P19①、不可变冻结身份、M1/M3、P50 诊断套件、P6/P8。

**D. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 39 | 阻断 | 原判据「`TerminalCompleted && Zone != Active`」**弱于** §16 交错⑤：`TerminalPendingTransfer` 同样 `Zone != Active` 却仍计容；且未断言当前节点处于 `Active` | **已修并实测通过**：前节点判据改为 **`Zone == Tombstone`**（真正迁出计容区）＋**当前节点 `Zone == Active`** 同时点断言 ⇒ 强于原判据且成立 |
| 40 | 重要 | 发送序号未与具体节点/发送身份关联（按 `index` 推断 `n-{index}`、按 `NodeId` `FirstOrDefault` 取旧记录） | **已修**：新增 `RoutingFakePort.OnBeforeSendWithPayload`（1 起序号＋**实际 payload JSON**），夹具从 payload 的 `configName` 反查当前节点；前节点/当前节点均要求**唯一命中**且前节点 `SubmissionIdentity` 非空（缺一即取证失败） |
| 41 | 重要 | 加固前把 P19② 记为「已关闭」属**越界登记** | **处置**：本轮先加固夹具；**加固后实测通过**，故 P19② 在**组件/宿主层**关闭（理由＝更强的直接证据，而非放宽）。P19①「原因码逐次取证」**仍未关闭** |
### 24.31 落地登记：§12.3 交错③「准备/发送阶段故障」·发送阶段支（[新增·2026-09-21 批次十五]）

**A. 落地事实**

| 项 | 实现／证据 | 条款 |
|---|---|---|
| 端口级**发送阶段故障注入** `RoutingFakePort.ThrowOnSend`（**记录本次发送尝试之后**抛 `IOException`＝「已进入可能发送阶段后失败」） | 夹具侧注入（`ProbeNodeSubmitRoutingAsync(..., configurePort)`） | §12.3 M3（阶段边界） |
| 夹具 `NodeSubmit_SendStageFailure_StaysReconcilingNoResend`（**会诊加固后**） | 断言：运行**收敛**（不悬挂）且 **恰为 `WorkflowRunState.Unknown`**（不得 `Succeeded`/`Failed`/`Cancelled`）、节点结果为 **`unknown`**；**只允许一次发送尝试**（`SendCount == 1`，不得换通道重发）且 `LastSendSeq == 1`（无更新发送许可）；该节点 Operation 为 **`Reconciling`**；**未决 `Submission` 仍在册**（身份与该 Operation 的 `SubmissionIdentity` 全等、状态 `Reconciling`）——「不可考/责任保留」由此落在**责任载体**上而非仅 Operation 枚举 | §12.3 交错③／M3／§4.0 `Submission.Reconciling` 责任语义／§3.2a（Unknown 不产生下一轮）／§24.20-B |

**B. 交错③ 其余两支的承接（登记，禁悬空）**

1. **准备阶段 `RunStore` 更新失败**：需按 §17 **P49** 建「故障注入夹具」（归 B4）；当前无注入接缝，故本批未做——**不得**以「发送阶段故障」夹具替代（两者阶段不同）。
2. **占位前的校验拒绝**（意图/身份/游标/冻结）：`SubmitSuccessorViaAdmissionAsync` 的本地预检在**任何租约副作用之前**返回 `Rejected`（代码顺序可证）；**尚无独立夹具**直接断言「拒绝＋零发送＋零仲裁操作」——登记为后续夹具项（与 §12.3 交错⑥「入口覆盖」同批处理更经济）。

**C. §12.3 交错清单（更新后）**：①首节点抢先＝组件层已关闭（§24.29）；**③发送阶段支＝已补直接证据**（本节）；⑤逐节点释放＝已补直接证据（§24.30）；仍未做：②调用者≠获选者、③其余两支（P49＋校验拒绝夹具）、④接管故障后续取消/重启、⑥四类入口 Scope 来源，以及 P19①、不可变冻结身份、M1/M3 其余、P50 诊断套件、P6/P8。

**D. 本批会诊（gpt-5.6-sol／medium，一轮）与处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 42 | 重要 | 「不可考/责任保留」证据不足：仅断言 Operation `Reconciling`＋运行非 `Succeeded`，若运行错误收敛为 `Failed/Cancelled` 或未决 `Submission` 被错误移除仍会通过 | **已修并实测通过**：补断言 **`State == Unknown`**、**节点结果 `unknown`**、**未决 `Submission` 仍在册且身份与该 Operation `SubmissionIdentity` 全等、状态 `Reconciling`**、**`LastSendSeq == 1`**（无更新发送许可）——责任保留落在**责任载体**上 |
### 24.32 落地登记：P7／§12.2 第 3 项「不可变冻结身份·字段合并」（[新增·2026-09-21 批次十七]）

**A. 落地事实**

| 项 | 实现／证据 | 条款 |
|---|---|---|
| **RunStore 级字段合并** `UpdateMerging(runId, applyOwnedFields, out latest)` | 存储闸门内**重新加载盘上最新记录** → 只应用调用方声明的**自有字段** → 以**最新修订**原子发布；记录不存在返回 `false`（无副作用），损坏记录仍拒绝覆盖。对比：旧对象整写 `Update` 在修订漂移时**响亮冲突**（既有护栏不变） | §17 **P7**（单一承接者）／§12.2 第 3 项「字段合并或版本守卫」 |
| **执行边界全面改用合并写**（两处写回点） | `BgiWorkflowExecutionBoundary.PrepareSubmit`（冻结字段：`Epoch`／`ExpiresAtUtc`／`Fingerprint`／`SendAttempted`／`Intent`）与 `ReconcileAfterUncertainSendAsync`（受理事实：`Intent=Accepted`＋`JobId`）改为合并写；**并把合并后的修订/时间戳回写调用方对象**（否则调用方随后用旧修订写会响亮冲突——实测门关直通路径曾因此变红） | §12.2 第 3 项／R4.8 BatchB 合同不回退 |
| **发送身份冻结身份扩展**（`ReconcileIdentity` 增 `Attempt`） | 发送前与对账命中均按「`Epoch`／`Key`／`WireRunId`／`NodeId`／`LoopIteration`／**`Attempt`**」全等核对；**身份不一致＝不落盘**（对账命中返回 `null` ⇒ 外层收敛 `Unknown`；发送前则**可证实未发送地拒绝**） | §12.2 第 3 项「核对 occurrence/iteration/**attempt**/提交键/游标修订」（游标修订仍由门面层按 `CursorRevision` 核对） |
| 夹具 | `RunStoreTests.UpdateMerging_PreservesConcurrentNonOwnedChange_WhileUpdateConflicts`（非自有字段保留＋旧对象整写冲突对照＋缺失记录无副作用）；`BgiWorkflowExecutionBoundaryPortSeamTests.Reconcile_ConcurrentRecordAdvance_*` **更新为两情形**：①并发只改非自有字段 ⇒ **合并落盘**（受理事实在盘上 **且** 并发改动保留）；②并发推进 `Attempt` ⇒ **不绑定**（保守 `Unknown`、盘上无该笔受理事实、零重发） | §17 P49 之外的本批证据；同时作为 P7 的**并发保留夹具** |

**B. 已更新的既有夹具语义（如实登记）**：原 `Reconcile_ConcurrentRecordAdvance_WriteBackRejected_ConservativeUnknown`（旧语义＝「任何并发推进 ⇒ 写回被修订守卫拒绝」）**已按 P7 更新**：非自有字段的并发推进现在**合并保留**（不再整笔失败）；**发送身份被推进**仍**严格保守**（不绑定）。⇒ 该夹具由「一律拒绝」升级为「按自有/非自有 + 身份是否仍属本笔分流」，**断言未放宽**（新增了 identity 分流的反例）✔。

**C. 负载敏感性观察（如实登记）**：本批在**全量套件**中曾出现 1 次 `NodeSubmit_EachSendObservesPreviousNodeReleased` 红灯（隔离运行通过）——与 **P50** 同源（同属 `TaskCenterHeavyE2E` 非并行收集的宿主级重夹具，机器级负载敏感）。本批已把该夹具的节点数由 **6 降为 4**（保持逐次取证语义：3 次观察）以降低本收集内负载；随后**连续 2 轮全量回归绿（850/1/851）**。**残余**：P50 类负载敏感性仍未根除（诊断套件未落实，见 §24.28-B）。

**A′. 最终实现口径（会诊后修正；以上表为意图，下列为**已落地**的精确语义）**

1. **接口名与拒绝语义**：`RunStore.UpdateMergingIf(runId, Func<WorkflowRunRecord,bool> applyOwnedFields, out latest)`。
   回调**返回 `false`＝前置条件不成立 ⇒ 零发布、零修订推进**（`latest` 为盘上原样，供调用方读回判定）；
   记录不存在 ⇒ `false`；**坏记录一律响亮冲突**：反序列化为 `null`／`RunId` 缺失／`RunId` 与请求不一致 ⇒ `RunRecordConflictException`（拒绝覆盖、原件保留）。
2. **两处写回改用合并写＋全量 rebase**：`PrepareSubmit`（冻结字段）与 `ReconcileAfterUncertainSendAsync`（受理事实）先在同一锁内**核对身份与合法前态**，通过后才写自有字段；成功后调用 `RunStore.RebaseOnto(run, latest)` 把**盘上最新字段整体同步回调用方对象**（含嵌套引用；反射浅复制，替代 STJ `Populate` 的不可用）。
   **禁止**「只回写修订号」——那会**洗白旧对象**，使其后续 `Update` 通过修订检查并把并发改动整写覆盖（会诊阻断项）。
3. **冻结身份字段集**：`Epoch`／`Key`／`WireRunId`／`NodeId`／**`Occurrence`**／`LoopIteration`／**`Attempt`**。
   发送前核对「`WireRunId`＋节点/出现次数/轮次/attempt/提交键」且**合法前态必须为 `IntentRecorded`**（否则可证实未发送地拒绝且**零发布**）；对账命中同样核对全字段集，不一致 ⇒ **零发布**并返回 `null`（外层收敛 `Unknown`），且**回滚调用方内存视图**（不得留下假 `Accepted/JobId`）。
4. **夹具补强**：`RunStoreTests` 增「rebase 后旧对象整写**不再覆盖**并发改动」与「前置不成立 ⇒ **零修订推进**」两项断言；`BgiWorkflowExecutionBoundaryPortSeamTests` 的两情形夹具保持（①非自有字段并发 ⇒ 合并落盘且保留改动；②`Attempt` 并发推进 ⇒ 不绑定、保守 `Unknown`、盘上无该笔受理事实、零重发）。

**D. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 43 | 阻断 | 「只把合并后的修订/时间戳回写旧对象」＝**洗白**：旧对象随后整写会通过修订检查并覆盖并发改动 | **已修并实测通过**：改为 `RunStore.RebaseOnto`（盘上最新字段**整体同步**回旧对象，含嵌套引用）；新增夹具断言「rebase 后整写保留并发 `Note`」 |
| 44 | 阻断 | `PrepareSubmit` 在**核对身份之前**就在合并回调里改写并发布了盘上 `CurrentSubmission`，且未校验合法前态（可把 `Accepted` 回退成 `Submitted`） | **已修**：身份（含 `WireRunId`/`Occurrence`/`Attempt`/`Key`）与**合法前态 `IntentRecorded`** 全部在**同一锁内先核对**，不成立即 `false` ⇒ **零发布、零修订推进**、调用方内存不污染 |
| 45 | 阻断 | 对账拒绝合并时**仍推进修订**且**留下内存假受理**（未回滚 `Intent/JobId`） | **已修**：`UpdateMergingIf(...)==false` 分支**回滚**调用方 `Intent/JobId` 并返回 `null`；`UpdateMergingIf` 的不发布分支**不推进修订**（新增断言） |
| 46 | 重要 | 冻结身份缺 `Occurrence`；`PrepareSubmit` 未核对盘上 `WireRunId`；文档与实现对不齐 | **已修**：`ReconcileIdentity` 增 `Occurrence` 并在发送前/对账两处核对；`PrepareSubmit` 同时核对 `WireRunId`；本节 A′ 已把实现字段集逐项写明 |
| 47 | 重要 | `UpdateMerging` 未维持「损坏记录响亮冲突」：JSON `null` 当作不存在、`{}`/空 `RunId` 抛 `ArgumentException`、盘内 `RunId` 与请求不一致未拒绝 | **已修**：上述三种情形统一抛 `RunRecordConflictException`（不当作不存在、不落到别的目标路径） |

### 24.33 落地登记：B2-γ 第 3 步·第三批——§16②「调用者≠获选者」组件级夹具（[新增·2026-09-21 批次十八]）

**A. 本批落地事实（登记；证据＝夹具名＋全量回归）**

1. **新增夹具** `ArbitrationAdmissionServiceTests.CallerContextNotSelected_DispatchCarriesWinnerIdentityOnly`：
   A（低优先级＝**落选**）与 B（高优先级＝**获选**）并发入队于**同一轮次**，断言门面交给 Sender 的
   `SubmissionDispatch` **完全属于获选者 B**——① 请求身份与**完整发送身份**（`submissionIdentity`／`sendSeq`）
   等于锁内原子发布的 B 占位事实；② **候选八段身份**（`Scope`／`Namespace`／`WorkflowId`／`TriggerOccurrenceId`／
   `RunId`／`NodeId`／`Occurrence`／`LoopIteration`／`Attempt`）＋`PayloadFingerprint`＋`ResourceRef`＋`Intent`＋
   `ActionId`＋`Priority` **逐字段绑定 B 的期望值**（A/B 逐段取不同值，故「从 A 或执行上下文选择性拼入」任一字段即红）；
   ③ `StableIdentity`／`CandidateId` 等于 B 的**确定性派生**；④ **进程内不可变上下文**（§13.10 A1/A2）原样属于 B、
   不得泄漏 A 的对象；⑤ 派发携带**内部冻结副本**（`NotSame` 调用方对象）。
2. **落选面落盘证据**：A 的 `Operation` 停在 `NotSelected` 且 `LastSendSeq == 0`（**未发布发送许可**）、
   载荷指纹仍为 A 自身；B 为 `Accepted`＋`LastSendSeq == 1`；本轮**发送恰一次**；`Submission` 已关闭；
   **台账只留 B 一笔**（`SubmissionIdentity` 与派发对象全等）。
3. **反例非空洞的设计（[会诊处置·第 1＋2 轮]）**：轮次放行条件**只有**「两笔入队收齐」（**有界**等待，
   与执行上下文无关——谁启动的 drain 处理本轮都不影响结论）；夹具**不再断言任何执行上下文流行性**：
   §12.2 B1 的判据是**发送归属**（身份来自获选者的锁内占位快照），而非 `AsyncLocal` 是否跨越 await 继续传播
   ——生产即使在某处抑制上下文流动，本用例仍成立（初版「放行＝处理 drain 的 ambient 属于 A」已被判为
   **把待证前提写进放行条件＋把上下文传播变成测试合同**，故删除）。落选调用者 A 的请求对象与其
   `ProcessLocalContext` 全程存活，任何「按调用方请求／执行上下文／最新 Operation 拼装身份」的实现都会在
   逐字段断言上变红。
4. **回归**：全量 **851 通过／1 跳过／852**（跳过项＝既有 P50，断言未放宽；基线 552 未降），
   定向连跑 5 次全绿（无调度敏感）。**生产接线仍关闭**（`_successorAdmissionWired` 生产构造恒不传；
   E3/E4/E5 组合根准入委托仍未注入）。

**B. 本批会诊（gpt-5.6-sol／medium，一轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 48 | 阻断 | `AsyncLocal` 闸门把**待证明前提写进放行条件**（放行 = 处理 drain 的 ambient 属于 A ⇒ `ambientAtSend` 恒真），并把「上下文必然传播」变成测试硬要求（生产若改 `SuppressFlow`，身份仍正确但用例会超时变红）——与 B1 合同方向相悖 | **已修**：删除 ambient 闸门；放行条件改为**仅**「两笔入队收齐」（非 ambient 接缝）；标记改为**同步写入** drain 自身上下文（不依赖自然传播、不参与放行）。定向连跑 5 次全绿 |
| 49 | 阻断 | 「全部获选者身份」覆盖不足：`CandidateId`／`ActionId`／`TargetEpoch` 仅与落盘 `Submission` **自洽比较**（两处同错仍可通过）、`Intent` 未断言、八段中多数维度 A/B 同值、未断言落选者未发布发送许可 | **已修**：A/B 逐段取不同值并**逐字段绑定期望值**（八段＋`PayloadFingerprint`／`ResourceRef`／`Intent`／`ActionId`／`Priority`／`StableIdentity`／`CandidateId`＝B 的确定性派生）；新增「A 的 `LastSendSeq==0` 且载荷指纹仍为 A」「台账单笔且身份与派发全等」「`Submission` 已关闭」断言 |
| 50 | 重要 | 总计划 §3.3 R5 行「**R5.0–R5.7 已完成**」**效力拔高**：未限定证据层级，与 §7「生产接线仍关闭、P50 等门禁未闭、未签署」冲突 | **已修**：该行改为「**R5.0–R5.7 组件/设计层按各自范围收口（≠ 生产启用、≠ 验收签署；R5 整体仍未完成）**」，并同步 §7 汇总行的 R5.3 口径为「组件/组装层持续收口＋残余登记（逐批见 §7 行）」 |

**B′. 验证会诊（第 2 轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 51 | 阻断 | `ambientAtSend` 断言仍要求 `AsyncLocal` **跨越 await 自然传播**（写入点与断言点之间存在未完成 await）——生产若在该边界使用抑制流动，身份逻辑即使正确、用例仍失败 | **已修**：**删除**上下文标记与 `ambientAtSend` 断言（及相应机制），不再把「上下文流行性」设为合同；判定核心＝身份归属断言本身 |
| 52 | 阻断 | 「完整发送身份绑定 B」仍留**自洽比较**缺口（`SubmissionIdentity` 只与 `winner`／盘上互相比较，三处同时串错仍可通过）；且 A/B 的 `Scope`／`Namespace` 实为**同值**，与「逐段取不同值」表述不符 | **已修**：新增 `Assert.Equal("sub:{B.RequestIdentity}:1", d.SubmissionIdentity)`（按 B 的请求身份与 `sendSeq` **直接派生**）；A/B 的 `Scope`（`bgi:inst-a:ep1`／`bgi:inst-b:ep1`，epoch 均 `ep1`）与 `Namespace`（`v2`／`manual`）改为不同值并加 `NotEqual` 反向断言 |
| 53 | 重要 | 屏障等待**无界**：外层 `WaitAsync` 超时只停止等待、不取消仍挂在屏障上的提交任务，可能在测试结束后继续访问已清理的临时目录 | **已修**：屏障等待改为**有界** `allEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(5))`；超时/异常按门面既有语义**响亮完成本轮**（不遗留悬挂提交任务） |
| 54 | 重要 | 交接稿「当前权威状态」自相矛盾：页首 849／「已完成」段 775／新增批次 851 并存，且声明「工作区无未提交改动」而材料为在途 diff | **已修**：页首与「已完成」段计数统一为 **851 通过／1 跳过／852**（并注明本会话起点 775）；批次一览补「十三–十八」；工作区表述改为「仅本批有意提交的改动（在途批次标注），提交后回到无未提交改动」 |

**C. 仍未覆盖（登记，禁悬空）**

1. **节点后继／外部启动路径的同类端到端断言**（本轮为**组件层**证据：`ArbitrationAdmissionService` 门面派发对象）。
   真实入口层（E1/E4/E5/E6 的 Host 装饰器→Sender 实际载荷归属）归 **§23.1 真实入口证据**，两者**不得互相替代**（§8）。
2. §12.3 交错清单（§24.30-C／§24.31-C 口径延续）：仍未做 **③其余两支**（准备阶段 `RunStore` 更新失败＝§17 P49 归 B4；
   占位前校验拒绝夹具）、**④受理接管故障后续取消/重启**、**⑥四类入口 Scope 来源**；以及 P19①、M1/M3 其余、
   P50 诊断套件、P6/P8。**不可变冻结身份**已于批次十七关闭（§24.32）。

### 24.34 落地登记：§12.3 交错③「准备阶段故障」·占位前校验拒绝（[新增·2026-09-21 批次十九]）

**A. 本批落地事实（登记；证据＝夹具名＋全量回归）**

1. **新增夹具** `TaskCenterSuccessorPathGateTests.SuccessorSubmit_PreOccupyRejection_DeterministicNoSendNoLeaseSideEffect`
   （Theory **五支**，宿主层，直接驱动内部准入方法 `SubmitSuccessorViaAdmissionAsync`；发送侧注入计数端口
   `RoutingFakePort`，任何一次实际发送都会被计入）：

   | 支 | 构造 | 命中的预检 | 期望原因含 |
   |---|---|---|---|
   | `submission-null` | `run.CurrentSubmission = null`（**提交缺失**） | 意图先行（§13.10 A1） | 「提交意图缺失或身份不符」 |
   | `intent-state-invalid` | 存在提交但 `Intent = Submitted`（**意图状态不合法**，非 `IntentRecorded`） | 同上（与上行构造互不相同） | 同上 |
   | `identity-mismatch` | 提交请求的出现身份（`n-OTHER`）与已落盘意图（`n-1`）不符 | 同上（可证实未发送） | 同上 |
   | `freeze-fail` | 传入无法完成深拷贝冻结的节点（`FreezeNode` 反序列化为空 ⇒ 抛错并被归类为**可证实未发送**） | 节点冻结（前置条件合法） | 「冻结失败」 |
   | `f11` | `F11Active = true`（§7.1-1：F11 判定**先于**租约获取） | F11 独立闸门（前置条件合法） | 「F11」 |

2. **逐支共同断言（§16 交错③「校验拒绝 ⇒ 零发送」的可执行版）**：
   `result.Accepted == false` 且 **`result.Uncertain == false`**（可证实未发送 ⇒ **确定拒绝**，
   **不得**报成「待对账」）＋**注入端口 `SendCount == 0`**（「零发送」的**直接**证据，不靠「目录未创建」
   间接推断）＋**`arbitration` 目录未被创建**（⇒ `EnsureAdmissionFacadeAsync` 未执行 ⇒ 门面未占位 →
   **零租约初始化、零占位、零仲裁写入**；事实快照读取本身不计入该表述）＋拒绝原因指向该分支的预检项。
3. **同批已存在证据（不重复登记）**：游标缺失/不一致负向夹具
   `SuccessorSubmit_CursorMissingOrMismatched_RejectedWithoutLeaseSideEffect`（G4）与发送阶段故障夹具（§24.31）。
4. **回归**：全量 **856 通过／1 跳过／857**（Theory 五支各计一项；跳过项＝既有 P50，断言未放宽；基线 552 未降）。
   **生产接线仍关闭**。
5. **验收效力边界（[纠正·2026-09-21 结束会诊]）**：本节只关闭 §12.3 交错③的**「校验拒绝」支**（连同 §24.31 的
   「发送阶段故障」支）；交错③**整体仍记「部分」**——「准备阶段 `RunStore` 更新失败」按 §17 P49 归 B4 且
   **未验收**，按 §16 收口判据**不得计入「已覆盖」**（§16／§16-A 已同步为 2 项已覆盖／4 项部分）。

**B. 仍未覆盖（登记，禁悬空）**

1. **准备阶段 `RunStore` 更新失败**：按 **§17 P49** 归 **B4**（当前无注入接缝）——**不得**以「发送阶段故障」或
   「占位前校验拒绝」夹具替代（阶段不同、结论不同）。
2. §12.3 交错清单（§24.33-C 续）：仍未做 **④受理接管故障后续取消/重启**、**⑥四类入口 Scope 来源矩阵**；
   以及 P19①、M1/M3 其余、集合②持续观察重绑、P50 诊断套件、P6/P8。
3. **取消令牌支**（`ct.ThrowIfCancellationRequested()` 在门面初始化之前）**本批未覆盖**：其返回的是取消异常而非
   「确定拒绝」，语义与本节四支不同，归 R5.3 取消链批次（P6）一并处理。
