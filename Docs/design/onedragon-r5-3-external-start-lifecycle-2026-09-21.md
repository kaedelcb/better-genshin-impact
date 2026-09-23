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

- **阻断 6 的加固（§24.18-3 全维度，后经 owner 选择 A 修订）**：`DetectSendResponsibilityAdvanced` 仍按占位时捕获的 `leaseId／ownerEpoch`、请求状态、`submissionIdentity＋sendSeq` 复核责任归属。归属已变化时，普通发送结果保守停驻；若迟到结果是 `Accepted`，旧发送者只可把这条事实按完整发送轮次追加到共享外部台账，不得关闭 Submission 或修改 Operation。当前所有者恢复扫描后按原 `AcceptedAtUtc／EvidenceSource／RunId／JobId／OperationType` 接纳同轮回执并关闭占位；旧轮回执落入 `ConflictPending`，当前轮“未受理”裁决不能解除旧轮冲突。夹具 `ExternalStartSend_OwnershipChangedDuringSend_LateAcceptIsAdoptedByCurrentOwner`、`HistoricalAcceptedReceiptCannotBeClearedByCurrentRoundNotAcceptedAdjudication` 与 `AcceptedReceiptIsSavedWhenOwnershipChangesImmediatelyBeforeClaim`。
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

**B. 「早期层端口形状」历史待裁决记录（对应 §24.21-C-1②；已由本节下方 2026-09-23 owner 裁决取代）**

- §24.14-1 要求适配层 `StartAsync` 返回 `ExternalStartReply`（`EarlyAccepted(jobId, CompletionTask)`／`Rejected`／`Unknown`），且本仓实现对 `EarlyAccepted` **强校验非空句柄**（无句柄构造即抛）。
- 但 **v2（E3 现网阻塞协议）不携带早期句柄**：发送成功时 `JobId` 为空，而 §24.4-4 明文要求「**v2 发送成功：入口 success**，但台账保持未终局」。若把端口返回类型**整体**收敛为 `ExternalStartReply`，该路径只能落 `Unknown`（§24.14-1 末句）⇒ 对外 `result_unknown`，与 §24.4-4 直接冲突（会回归既有 v2 启动语义）。
- 故本批**保留**端口类型 `ExternalStartExecution`（其 `Accepted` 允许空句柄，正是 §24.7-2「无 JobId 路径必须登记替代查询依据」的载体），而**早期层回执类型 `ExternalStartReply` 在 ext 队列通道内部真实承载并经夹具断言**（批次六）。
- **登记为 owner 裁决项**（[验证会诊阻断处置]）：冻结稿 §24.14-1 与本仓已验收的 §24.4-4 在 v2 阻塞协议上**互相冲突**，施工方无权以登记覆盖冻结合同，故本项**未完成**，须 owner 二选一：**(甲)** 修订 §24.14-1（为阻塞式协议增设「无句柄的已受理」变体，或明文声明 v2 结论归完成层）后本批按新合同收敛端口；**(乙)** 维持现状（端口 `ExternalStartExecution`＋`ExternalStartReply` 在 ext 队列通道内部承载），并在 §23.4 门禁并集登记该差异为「已书面接受」。任一选择前，**生产外部启动接线保持关闭**。

**[Owner ruling 2026-09-23；取代本节“待 owner 裁决”状态及旧甲/乙选项，尚未构成生产验收]**：所有参与方升级；生产外部启动只走能返回真实任务编号的 ext 队列通道，不回退无编号的 v2 `task.start`。提交前可证实通道不可用时明确拒绝且零发送；提交已出站后若回执丢失、超时或缺少/仅含空白任务编号，一律 `Unknown`、保留原发送责任、不得自动重发。`already_executed` 保留为独立幂等回执：有真实编号时仅新准入路径可按该编号观察终态；无编号时仍为 `Unknown`，不得把它映射成无句柄 `Accepted`。未接线旧直启路径保留其既有“已执行过”返回文案，但该文案不构成新版生产验收。助手侧已交付该映射、宿主责任与编号观察夹具；BGI 队列协调器不可用时现改为 `queue_unavailable` 明确拒绝，不再回退 v2（组合根生产接线仍未验收）。**BGI `already_executed` 当前回执仍不含 `taskHandle`**，若该结果进入新准入路径会保守待对账；升级后的 BGI 仍须提供权威编号或证明新协议不会返回该形状。R5.8 门禁继续关闭。

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
| 6 | 阻断 | 重取 `_gate` 后**首个动作即台账副作用**（`TakeoverPersist`）——锁外期间责任被推进时，可能出现租约侧与台账侧分裂 | **已修，按 owner 选择 A 扩展**：身份检查仍阻止旧流程关闭/改写；只有迟到 `Accepted` 可由旧流程向共享台账追加逐轮回执。新负责人校验自己的租约凭据后接纳同一发送轮。普通结果仍停驻。夹具见 §24.22-C′；历史轮证据保留冲突，不能由当前轮拒绝裁决清除。 |
| 7 | 重要 | `BuildDispatch` 被移出「捕获异常⇒Unknown」归类 ⇒ 派发快照异常时由外层落 `internal_error`，占位后状态与既有行为不一致（影响所有类型） | **已修**：`BuildDispatch` 保留在原归类内（哨兵夹具不变） |
| 8 | 重要 | 新夹具未覆盖「发送异常后重取锁的配对/可用性」与「责任被推进后迟到受理」两类交错 | **部分已修**：新增旧负责人逐轮追加、当前负责人接纳、旧轮冲突与 claim 前换主夹具；外部启动 Sender 抛异常后后续请求仍可取得门面锁。仍未覆盖：跨进程真实共享台账故障注入及同一身份被终态推进后的裁决入口联动（归冲突批次）。 |
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
- **P50 状态（唯一权威口径；[更正·2026-09-22 批次四十九] 本行已被 §24.61 取代——**结题**）**：
  ①**根因已定位并修复**＝Windows **文件争用家族**（`UnauthorizedAccessException：Access to the path is denied`）
  未纳入有界重试（`RunStore` 原子替换/读路径与 `ArbitrationLeaseStore` 锁路径）；②**诊断入口义务已落实**＝
  `P50_LoadRepro_WholeClass_UnderControlledLoad`（环境变量 opt-in，**不改编源码**，覆盖**整个**重夹具类的四个夹具，
  含直调 33 节点本体）；③修复后同参数 10×16 **连续两轮全绿**、**33 节点用例 `Skip` 已解除**且全量**连续 3 轮
  `951/2/953`**（剩余 2 个 Skip＝两个 opt-in 诊断入口）；④**节点改道门（`_successorAdmissionWired` 生产构造不传）
  继续保留**（与 P50 无关的门禁，属生产接线范围）。容量证据继续由**组件级确定性**夹具承担
  （`Capacity_33NodeCandidates_AllAccepted_WhenEachSettled`／`Capacity_MainSlotsExhausted_33rdCreateRejected`）。
  **残余（如实）**：多进程/实机负载下的稳定性观察归 **R5.8 实机段（owner）**。

**C. B2-γ 第 3 步仍未完成（登记，禁悬空）**
> **当前状态（2026-09-23；本节原编号保留为批次十二历史快照）**：后续证据已取代下方部分“仍未完成”表述。不可变冻结身份／P7 合并写见 §24.32；交错①见 §24.29、②见 §24.33、③见 §24.31／§24.34／§24.36、④见 §24.37／§24.37-C，均按 §24.41-B 记为组件/宿主层覆盖。交错⑤的逐节点释放直接证据见 §24.30（P19② 已交付），但⑤整体仍部分，P19① 仍待按其裁决语义完成；交错⑥仍部分，M1 组件证据见 §24.54、G4a 见 §24.56，生产归属事实与真实入口证据仍欠。P50 已在 §24.61 结题；P8 判据接缝见 §24.62，E3 外部启动适配局部裁决见 §24.65。当前汇总仍为 **4 项覆盖／2 项部分**，见 §24.41-B。
>
> **本会话新增 owner 约束**：S4b 和 S8b 自动动作都要接入新调度器；优先级按交接稿 B.1 比较，热键也必须升级到有任务编号的通道（交接稿 B.2、B.5）。这几项的跨端协议/接线及验收尚未完成。`_successorAdmissionWired` 与生产外部启动门继续关闭；以上组件/宿主层证据不构成生产开门或 R5.8 验收。

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
| **P50** 负载敏感收敛（33 节点夹具） | **[已被 §24.61 取代·历史口径]**（批次四十九已结题：根因＝Windows 文件争用家族未纳入有界重试；修复后 33 节点 Skip 已解除） | §24.28-B（历史）：启用该用例后满负载 5 轮中 1 轮同宿主类夹具红灯（保守方向、无双跑）；诊断入口须覆盖整个用例类——**已落实见 §24.61** |
| **P6** 发送段取消令牌未透传 | **已覆盖（组件/宿主层；[2026-09-21 批次二十三／二十四] §24.38–§24.39）** | 透传按身份取证（组件级 `Dispatch_CarriesCallerTokenByIdentity`／缺省反例／宿主级 `NodeSubmit_SendSegmentSeesCallerToken_NotNone`）＋「取消时责任保持」宿主级端到端（`CancelDuringInFlightSend_ConvergesUnknown_ResponsibilityRetained`：端口侧 `SendCanceledByToken` 为因果证据）。**仍欠（禁悬空）**：子进程级重启、E3/E4/E5／恢复路径透传、`RetryAsync` 无令牌参数。**不得**据此新增统一取消链条 |
| **P8** 失败分类细化 | **[更正·批次五十] 判据接缝已交付（§24.62）**；外部启动路径分类端点仍欠（R5.8） | §24.62（证据载体＋判据＋边界分类＋宿主映射＋**16 夹具**）／§24.21-A（`IsRetryableQueueRejection`）；**当时口径**：本轮仅补队列通道可重试白名单、非 success 一律 Unknown |
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

**B. 仍未覆盖（登记，禁悬空）**（[更正·2026-09-21 批次二十一] 本节 B-1「准备阶段 `RunStore` 更新失败：当前无注入接缝 ⇒ 归 B4 未验收」**已由 §24.36 取代**——该支已交付组件/宿主层夹具并通过；本节其余条目不变）

1. **准备阶段 `RunStore` 更新失败**：按 **§17 P49** 归 **B4**（当前无注入接缝）——**不得**以「发送阶段故障」或
   「占位前校验拒绝」夹具替代（阶段不同、结论不同）。
2. §12.3 交错清单（§24.33-C 续）：仍未做 **④受理接管故障后续取消/重启**、**⑥四类入口 Scope 来源矩阵**；
   以及 P19①、M1/M3 其余、集合②持续观察重绑、P50 诊断套件、P6/P8。
3. **取消令牌支**（`ct.ThrowIfCancellationRequested()` 在门面初始化之前）**本批未覆盖**：其返回的是取消异常而非
   「确定拒绝」，语义与本节四支不同，归 R5.3 取消链批次（P6）一并处理。

### 24.35 落地登记：§16 交错⑥「四类入口 Scope 来源矩阵」（格 A／A′／B；C 为 P28 引用）（[新增·2026-09-21 批次二十]）

**A. 本批落地事实（登记；证据＝夹具名＋全量回归）**

| 格 | 入口 | 夹具 | 断言与**证明边界** |
|---|---|---|---|
| **A** | 面板启动（E1 登记来源） | `TaskCenterSuccessorPathGateTests.NodeSubmit_InheritsRegisteredFlowScope`（1 节点宿主级；[负载] 只用 1 节点以降低重夹具集合负载） | 流程级登记**唯一命中**（识别口径＝无节点身份 ＋ `Intent=start` ＋ `RunBinding=本运行`，与 `TryGetAdmissionScope` 一致）且 Scope 非空、呈 `bgi:` 形状；**后继节点操作逐条与流程级 Scope 逐字一致**（同值）。**边界**：本格单独**不能**区分「继承登记值」与「重读当前值」（纪元未变时二者取值相同）——**该边界已由同批 格 A′ 关闭** |
| **A′** | 面板启动＋**纪元变化**（[会诊加固·判别反例]） | `NodeSubmit_ScopeIsInherited_NotRereadFromCurrentEpoch` | E1 已按纪元 `E0` 登记后，经接缝在**节点准入之前**把当前纪元改为 `E1`：断言运行**不得收口成功**、**端口 `SendCount==0`**、流程级登记 Scope 仍属 `E0`、且**任何节点侧登记都不得携带 `E1`**。含义：若实现退化为「提交时重读当前纪元」，节点会以 `E1` 通过校验并**真的发送** ⇒ 本格变红。⇒ 与格 A 合读即得「**继承登记值、不得按当前纪元重建**」的判别证据 |
| **B** | 启动移交／暂停续行／Interrupted 恢复类**无已登记固定来源** | `NodeSubmit_NoRegisteredScopeSource_RejectedNoSendNoOccupy` | 确定拒绝（`Accepted=false` 且 **`Uncertain=false`**）＋原因含「无已登记仲裁授权」＋**端口 `SendCount==0`**（零发送直接证据）＋**租约文件在册**（证明门面确已初始化＝**真的走到缺来源分支**，而非更早预检被拒）＋**无开放 `Submission`** ＋无任何归属本运行的仲裁操作（`RunBinding`／`Candidate.RunId`／`Candidate.NodeId`／非空 `SubmissionIdentity` 四口径逐一排除＝**零占位**）＋运行快照**逐字段未变**（状态／修订／游标三项／节点结果数／提交键与节点身份／意图／无 `JobId`／无受理发送身份）。落实 AMD-1-5 第三条「缺固定 Scope/绑定＝**不签发、不发送**」——**不退回直通发送、不临时读当前 epoch 补造** |
| **C** | 恢复**有来源**继承 | **P28 既有夹具（引用，不重复登记）** | `TaskCenterHostRecoveryAdmissionTests.ResumeRun_InheritsOriginalScope_WhenEpochUnchanged`（逐字一致）／`ResumeRun_EpochChanged_RejectedNoSilentRebinding`（纪元变化 ⇒ `stale_epoch` 终局拒绝、不静默重绑） |

**仍欠（登记，禁悬空）**：①**首节点绑定**（**§12.3 M1**：E1 关闭后首节点另行取许可、父子关系持久化）；②**启动移交/暂停续行的来源登记**（§13.11 **G4a**）；③入口表**逐项签注**。三者均归 **B4**（§17 P21 组件矩阵），**本批不得据此把 ⑥ 计入已覆盖**。

**B′. 本批会诊（gpt-5.6-sol／medium，一轮＝4 重要）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 55 | 重要 | **格 B 泄漏宿主心跳**：`EnsureAdmissionFacadeAsync` 已启动租约心跳，但用例从未 `ShutdownAsync` 就删临时目录（后台任务可能继续访问目录、读租约亦可能与心跳争锁） | **已修**：改为**先 `await host.ShutdownAsync()`**（`try/finally` 兜底）**再**读快照；新增 `ReadLeaseFileWithRetry`（20×5ms 有界重试） |
| 56 | 重要 | **格 A 不能支撑「继承、不重建」的强表述**（改为提交时重读当前纪元仍会绿；引用的恢复夹具走另一条路径，挡不住本路径回归） | **已修**：新增 **格 A′** 判别反例（`BeforeSuccessorAdmission` 接缝在 E1 登记后、节点准入前改当前纪元 ⇒ 断言零发送＋不得收口成功＋节点侧登记不得携带新纪元）——由「不可区分」升级为**可判别证据**（低成本方案，无需新增生产接缝） |
| 57 | 重要 | **格 B 直接证据不足**：`DoesNotContain(RunBinding==runId)` 不能排除错误 `RunBinding`；未断言无开放 `Submission`；仅查 `Intent` 不能证明状态/游标/结果/提交字段/修订未变 | **已修**：`Submission == null` ＋四口径排除占位（含非空 `SubmissionIdentity`）＋租约文件在册（证明走到缺来源分支）＋运行快照**逐字段**比较（状态/修订/游标/结果数/提交键/意图/无 JobId/无受理发送身份） |
| 58 | 重要 | **文档结构与引用错误**：§16-A 新增 ⑥ 行误为 **5 列**（表头 4 列）；§24.35 引「§17 M1」不存在（M1 在 **§12.3**）；交接稿批次一览止于「十三–十八」 | **已修**：⑥ 行改回 4 列；改「**§12.3 M1**」；交接稿补批次十九、二十 |

**B. 负载观察（如实登记）**：本批第一次全量运行时出现过 **1 次** `NodeSubmit_InheritsRegisteredFlowScope` 红灯（隔离单跑通过），与 **P50 同类**
（`TaskCenterSuccessorPathGateTests` 为 `DisableParallelization` 的宿主级重夹具集合）。处置：格 A 由 2 节点降为 **1 节点**（语义不变），
此后**连续 2 轮全量绿**（858/1/859）；加入格 A′ 后**再连跑 2 轮全绿**（859/1/860）。**P50 类负载敏感性仍未根除**（诊断套件未落实，§24.28-B 口径不变）。

**C. 回归**：全量 **859 通过／1 跳过／860**（连续 2 轮；跳过项＝既有 P50 严格用例，断言未放宽；基线 552 未降）。**生产接线仍关闭**。

### 24.36 落地登记：§17 P49／§16 交错③「准备阶段 `RunStore` 更新失败」（[新增·2026-09-21 批次二十一]）

**A. 本批落地事实（登记；证据＝夹具名＋全量回归）**

1. **新增仅测试接缝**：`RunStore.PublishFaultForTest`（`internal Func<WorkflowRunRecord, Exception?>?`，**生产恒 `null`**）——
   在**原子发布步骤之前**（尚未写临时文件/替换目标）按记录判定注入故障，异常**原样抛出**（不包装）。
   **[会诊纠正]** 初版把注入点放在 `PrepareSubmit` 调用存储层**之前**，被判为「只证明『调用之前异常』、
   未覆盖读取/锁定/原子写/回调已改内存却发布失败」⇒ 已**下移到真实持久化边界** `RunStore.Persist`。
   注入条件由夹具给出＝该记录已带**准备段冻结写**标记（`CurrentSubmission.SendAttempted == true`），
   故确落在「占位后、合并回调已修改内存记录、真实发布失败」的窗口（§12.3 M3③ 所指场景）。
   存在理由：§16 交错③要求「准备阶段 `RunStore` 更新失败 ⇒ 零发送或责任结清」，而该阶段此前**无注入接缝**
   （原登记：§17 P49「当前无注入接缝，不得以发送阶段夹具替代」）。
2. **新增夹具** `TaskCenterSuccessorPathGateTests.NodeSubmit_PrepareStageWriteFault_NoSendResponsibilityRetained`（宿主级，1 节点）：
   注入 `IOException` 后断言——①**注入恰命中一次**（证明确在准备段发布窗口、而非别的路径）；②运行收敛（不悬挂）；
   ③**不得假报成功**且**必须为 `Unknown`**（不得 Failed/Cancelled 等终态）；④**端口 `SendCount == 0`**
   （**零发送**：尚未进入可能发送阶段，§12.3 M3① 阶段边界）；⑤节点结果 `unknown`（**不得**反解为
   「确定未受理/拒绝」——§12.3 M3③：那会诱发换键重跑）；⑥**责任保留且非终局**：节点操作
   `RequestState == Reconciling`＋`LastSendSeq == 1`（＝确系「已占位」）＋`SubmissionIdentity` 非空，
   **未决 `Submission` 在册**且状态 `Reconciling`、身份与节点操作**全等**；⑦**未换键**：租约操作的
   `WireSubmitKey` 等于盘上本笔提交的键、`Attempt == 1`、节点身份仍为 `n-1`。
3. **实现现状说明（如实）**：该夹具（含下移注入点后的版本）**通过**——即当前实现在「准备段发布失败」路径上
   **已**满足「零发送＋保守不可考＋责任保留＋不换键」。本批**未修改任何生产行为**，只补**确定性注入接缝＋验收证据**；
   **该结论不构成本门禁放开的理由**（放开门禁仍须 §23.4 并集 + owner 书面）。
   **范围限定**：本夹具覆盖**活进程内一次驱动**；「重启/恢复后仍不换键/不增发送许可」**不在本节范围**（归 B4 恢复项）。
4. **会诊（一轮：1 阻断／2 重要）逐条处置**：①**阻断**「注入点在存储调用之前 ⇒ 不能证明 `RunStore` 更新失败」⇒ 注入点**下移到 `RunStore.Persist` 的真实发布边界**，并按 `SendAttempted` 条件限定为准备段冻结写；②**重要**「未直接证明不倒置为确定拒绝」⇒ 补 `State == Unknown`、节点操作 `RequestState == Reconciling`、`Submission.State == Reconciling`、提交键/attempt/节点身份仍属原笔；③**重要**「文档状态互相冲突」⇒ §17 P49 由「已交付」改为符合 §17.2 四态词汇的**已验收（组件/宿主层）**、§24.34 标注「无注入接缝／未验收」已被本节取代、交接稿批次一览补至二十一。
5. **回归**：全量 **860 通过／1 跳过／861**（跳过项＝既有 P50，断言未放宽；基线 552 未降）。**生产接线仍关闭**。

**B. 状态收敛**：§16 交错③三支（校验拒绝／发送阶段故障／准备阶段写盘失败）**均有组件/宿主层夹具** ⇒
R5.2 稿 §16 行③与 §16-A 汇总裁**由「部分」升为「已覆盖（组件/宿主层）」**（汇总＝**3 项已覆盖（①②③）／3 项部分（④⑤⑥）**）；
§17 P49 记**已验收（组件/宿主层）**（**不**使用「已交付」自造词汇）。**层级限定**：以上**均为组件/宿主层**证据，
**真实入口层证据属 §23.1**，二者不得互相替代；**重启/恢复后不换键**与**真实磁盘故障分布**均**未验收**。

### 24.37 落地登记：§12.3 交错④「受理接管故障」——接管落盘失败支＋重启支（[新增·2026-09-21 批次二十二]）

**A. 本批落地事实（登记；证据＝夹具名＋全量回归；均**未修改生产行为**）**

| 夹具 | 场景与注入 | 断言 |
|---|---|---|
| `TaskCenterSuccessorPathGateTests.NodeSubmit_TakeoverPersistFailed_UnknownNoResendNoAcceptedFact` | 真实 1 节点流程；`RunStore.PublishFaultForTest` **条件化在接管写专属形态**（[会诊加固] 仅 `Intent=Accepted` 不足以定位接管写 ⇒ 追加要求：`JobId` 非空＋`AcceptedSendIdentity` 非空＋`ObservedTerminal` 为空＋`NodeOutcomes` 为空＝接管发生在终态观察与结果写回之前） | 注入恰一次＋**恰一次发送**（远端已受理）＋**必须 `Unknown`**（不得成功、不得确定拒绝）＋节点结果 `unknown`＋节点操作与未决 `Submission` 均 `Reconciling` 且身份**全等**＋`LastSendSeq==1`＋**盘上无受理事实**（`Intent=Submitted`、`JobId == null`、`AcceptedSendIdentity == null` ⇒ 旧 Runner 对象未把接管事实写成既成事实） |
| `NodeSubmit_TakeoverPersistFailed_HostRecreationKeepsResponsibilityNoResend`（[会诊收窄命名] **host recreation**，非子进程重启） | 同上收敛后新建宿主与端口（同 root，**同进程内**；静态/进程级缓存残留**未排除**） | 前后未决 `Submission` 均非空且 `Reconciling`；节点操作**唯一存在**（`Single`）且 `Reconciling`；操作↔未决责任身份**互证**；`SubmissionIdentity`＋`SendSeq`＋`WireSubmitKey`＋运行记录 `CurrentSubmission.Key`／`Attempt` **前后全等**（→ **未新增发送许可、未换键**）；重建后再次驱动＝**确定拒绝**（`Accepted=false`、`Uncertain=false`）＋原因定位**意图预检**「提交意图缺失或身份不符」＋**零发送**；盘上仍无受理事实 |

**A′. 会诊（一轮：4 重要）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 59 | 重要 | 注入条件仅 `Intent=Accepted` ⇒ **未唯一定位接管写**（结果写回/终态写回也可能带 Accepted） | **已修**：条件加固为「接管写专属形态」（`JobId`＋`AcceptedSendIdentity` 非空、`ObservedTerminal` 空、`NodeOutcomes` 空），并保持「注入恰一次」断言；加固后实测通过 ⇒ 该形态确实只在接管写出现 |
| 60 | 重要 | 宿主重建夹具的核心断言可**空过**（`opBefore` 用 `SingleOrDefault` 且整体包在 `if (opBefore is not null)`），且未证明「未换键」 | **已修**：前后节点操作改 `Single`（缺失即失败）、前后未决 `Submission` 强制非空且 `Reconciling`、补操作↔责任身份互证、`SubmissionIdentity`／`SendSeq`／`WireSubmitKey`／运行 `Key`／`Attempt` **全等**断言 |
| 61 | 重要 | 「重启」与「再驱动在意图预检被拒」的自述**强于实际证据**（同进程新建宿主，未排除静态缓存；未断言再驱动的拒绝口径） | **已修**：夹具**改名并收窄口径**为 **host recreation**（文档与命名一致，明示未排除进程级缓存残留）；补 `Uncertain == false` 与原因「提交意图缺失或身份不符」断言，使「在意图预检被拒」成为**被断言的证据**而非叙述 |
| 62 | 重要 | 交接稿页首批次一览与后置更正**自相矛盾**（页首仍写「二十一在途」） | **已修**：页首一览直接更新为「十三–二十二」（二十一＝`b46486a2c` 已提交；二十二＝本批在途），并**删除**末尾那条后置更正条目（不再依赖读者发现纠错） |

**B. 范围限定（如实，禁悬空）**

1. **取消支未覆盖**：§16 交错④要求「接管故障后**取消**或重启」——本批只覆盖**重启支**；
   **取消支的入口在 E4 控制热键／命令执行器**（本组件层无法真实触达），归**取消链批次（§17 P6/P8/P54）**，
   与 §12.3 M3／G7 的「取消是调用结束方式、不是关闭依据」同口径。
2. **「未决责任本身阻挡再发送」的门面级直接证据仍欠**：本批重启后**再驱动**是在**意图预检**
   （`Intent=Submitted` 非 `IntentRecorded`）处被拒，故「零发送」不能单独归因于门面的未决发送检查；
   该直接证据（含恢复入口再次取许可的路径）归 **B4 恢复项**。
3. **外部启动路径同项夹具**（E6 ext 的接管/关闭失败＋重启）仍欠，归 B4。

**C. 状态与回归**：§16 行④与 §16-A **仍记「部分」**（接管落盘失败支＋重启支已覆盖；取消支与上述两项仍欠）；
**不得**据此把 ④ 计入已覆盖。全量回归 **862 通过／1 跳过／863**（跳过项＝既有 P50 严格用例，断言未放宽；
基线 552 未降）。**生产接线仍关闭**。

### 24.38 落地登记：§17 P6「发送段取消令牌透传」（[新增·2026-09-21 批次二十三]）

**A. 本批落地事实（**本批为生产行为变更**：发送段由此前恒 `CancellationToken.None` 改为**调用方令牌**）**

1. **透传实现**：①`AdmissionRequest` 新增**进程内**字段 `CallerToken`（`[JsonIgnore]`，**不参与任何序列化**；冻结副本一并携带）；
   ②发送分派 `SubmissionDispatch` 新增同名字段（`[JsonIgnore]`），由 `BuildDispatch` **原样**从获选排队项复制；
   ③宿主 `SubmitSuccessorViaAdmissionAsync` 以入参 `ct` 填 `CallerToken`；`DispatchSuccessorViaHostAsync` 以
   **`d.CallerToken`** 调 `SendPreparedAsync`（此前恒传 `CancellationToken.None`）。
   **[会诊重要项处置]** ④**续用重新驱动同样携带**：`ContinueUseAsync` 在「Queued/InRound 且本进程无在途处理者」时
   构造 `redrive` 重新入队——该副本此前**只复制了 `ProcessLocalContext`、漏带 `CallerToken`**（⇒ 该路径发送段会重新落回 `None`）；
   现已在同处补 `CallerToken = request.CallerToken`。⑤**范围限定**：`RetryAsync` 的 API 当前**没有**调用方令牌参数，
   其重试发送仍只能用 `default`（不可取消）——本批**不扩展**该 API（须另行定义合同），如实登记为未覆盖。
2. **语义边界（未放宽）**：取消是**调用结束方式**，**不是关闭依据**——发送段在取消/异常路径上仍按
   「不可考 → 对账清理 → 保留责任」处置；本批**未新增**任何「取消即结清/取消即未受理」路径
   （§13.11 G7 纪律、§12.3 M3③）。
3. **夹具（两级互补）**：
   - **组件级·按身份**（[会诊重要项处置] 仅断言「可取消」可能被内部另建的链接/预算令牌误绿）：
     `ArbitrationAdmissionServiceTests.Dispatch_CarriesCallerTokenByIdentity`——调用方传入**已知** `CancellationTokenSource.Token`，
     断言 Sender 收到的 `SubmissionDispatch.CallerToken` **与传入令牌按底层源相等**（`Assert.Equal`，即**同一枚**），
     且取消后发送段可观察到 `IsCancellationRequested`；
     `Dispatch_WithoutCallerToken_StaysNonCancelable`——缺省时保持**不可取消**，证明门面**不自铸令牌**。
   - **宿主级·生产接线**：`TaskCenterSuccessorPathGateTests.NodeSubmit_SendSegmentSeesCallerToken_NotNone`——真实 1 节点流程中，
     端口断言发送入口令牌 **`CanBeCanceled == true`**（恒 `None` 时必红），且流程仍正常收口 `Succeeded`（透传不改变成功路径）。
4. **「取消时责任保持」的证据分担（[会诊重要项处置] 收窄口径）**：**边界级**既有夹具
   `BgiWorkflowExecutionBoundaryPortSeamTests.Reconcile_OperationCanceledToSend_ReThrowsAfterReconcile` 只证明
   **取消传播＋对账被调用＋`OperationCanceledException` 不被吞**；它**不**证明宿主仲裁层的责任载体
   （`Submission`／`Operation` 维持 `Reconciling`/`Pending`、未被关闭）。因此：
   **「仲裁责任保持」的宿主级端到端验收仍未完成**（需能受控取消「运行器传给发送段的同一令牌」）⇒ 归 **B4**。
   （实现路径本身的保守性：`OperationCanceledException` 经门面 Sender 异常归类为 `Unknown` → `MarkReconcilingAsync`，
   **未新增**「取消即确定未受理/结清」分支——但该结论**以端到端夹具验收为准**，本批不据此主张已覆盖。）
5. **范围限定**：本批只对**节点后继发送**路径取证（`DispatchSuccessorViaHostAsync`）；
   外部启动（E3/E4/E5）与恢复路径的令牌透传**未逐条取证**（其 `CallerToken` 未设置 ⇒ 保持 `default`＝None，行为与改动前一致，
   **不主张已覆盖**）。
6. **回归**：全量 **865 通过／1 跳过／866**（跳过项＝既有 P50，断言未放宽；基线 552 未降）。
   透传为**生产行为变更**，全量回归无回归；**生产接线仍关闭**。

**B. 相邻项状态（不代做）**：**P8**（通用失败分类细化：网络前可证实未发送 vs 已发送后失败）**仍未闭环**——
本批不改变「非 success 一律 `Unknown`」的既有分类；**P54**（适配器取消传播/执行失败/接管失败三条映射的显式断言）
已有组件级覆盖，**接管失败映射**的显式断言仍欠。
> **[更正·2026-09-22 批次五十]** 上句为**当时口径**：P8 的**判据接缝＋夹具**已交付（见 **§24.62**）——
> 统一提交边界现按证据载体区分「可证实未发送 ⇒ 确定拒绝＋重试窗口」与「已进入线路后失败 ⇒ 保留未决责任」；
> **整行仍未验收**（外部启动路径分类端点归 R5.8／外部启用门）。

### 24.39 落地登记：§17 P6 收束——宿主级「在飞发送期取消 ⇒ `Unknown`＋责任保持」（[新增·2026-09-21 批次二十四]）

**A. 本批落地事实（回归证据；**未改生产行为**）**

1. 夹具 `TaskCenterSuccessorPathGateTests.CancelDuringInFlightSend_ConvergesUnknown_ResponsibilityRetained`（宿主级端到端）：
   端口在**发送入口阻塞在取消令牌上**（在飞发送；端口新增 `BlockUntilCanceled`／`SendStarted`／**`SendCanceledByToken`**／
   **仅清理用** `ReleaseBlockedSend`）⇒ 触发宿主关闭（＝**取消运行器传给发送段的同一令牌**）。
2. 断言链（[会诊加固] **因果证据取自端口**，不用「宿主关闭返回」代替）：`SendStarted` 确认已进入在飞发送 →
   发送**恰一次**且端口观察到**可取消令牌**（§24.38 透传）→ **`SendCanceledByToken` 置位**
   （端口在 `catch (OperationCanceledException) when (ct.IsCancellationRequested)` 中置位 ⇒ **在飞发送确因该令牌被取消而退出**）
   ＋`LastSendToken.IsCancellationRequested`＋随后 `ShutdownAsync` 有界返回 →
   **无重发**（`SendCount` 仍为 1）＋运行收敛 **`Unknown`**（不得成功、不得确定拒绝）＋节点结果 `unknown`
   ＋盘上**无受理事实**（**三字段同口径**：`Intent == Submitted` ＋ `JobId == null` ＋ `AcceptedSendIdentity == null`）
   ＋节点操作与未决 `Submission` 均 **`Reconciling`**，**身份非空且全等**、`LastSendSeq == 1`（**取消未新增发送许可**）、
   `Submission.SendSeq ==` 许可水位、`WireSubmitKey ==` 盘上本笔提交键（未换键）、`Attempt == 1`。
3. **由此关闭的结论**：「发送窗口取消 ⇒ **不可考、责任保留、不得据取消结清/放行**」在**宿主链路**上被验证
   （§13.11 G7「取消是调用结束方式，不是关闭依据」＋§12.3 M3③）；配合 §24.38 的**按身份透传**取证，
   **§17 P6 的两项子目标（透传实现／取消时责任保持）均已覆盖（组件/宿主层）**。
4. **未覆盖（登记，禁悬空）**（[更正·2026-09-21 批次二十五] 原 ①「§16 交错④的『接管故障后取消』组合仍欠」**已由 §24.37-C 取代**）：
   本夹具的取消发生在**受理之前**，**不**证明「接管故障后取消」组合——该组合**已由 §24.37-C 的专用夹具覆盖**
   （原「仍欠」**已作废**；范围/层级残余见 §24.41-C）；②**子进程级重启**与
   **E3/E4/E5／恢复路径的令牌透传**仍按 §24.38 的范围限定未覆盖；③`RetryAsync` 仍无令牌参数（未扩展）。
5. **B′. 本批会诊（一轮：4 重要）与逐条处置**：

   | # | 严重度 | 发现摘要 | 处置 |
   |---|---|---|---|
   | 63 | 重要 | 「`ShutdownAsync` 返回」**不能**作为「取消确实中止在飞发送」的因果证据（宿主关闭自身可能有界收敛运行），且未证明 `Task.Delay` 因该令牌抛 OCE | **已修**：端口新增 `SendCanceledByToken` 信号（在 `catch (OperationCanceledException) when (ct.IsCancellationRequested)` 内置位）作为**因果证据**，夹具先有界等待该信号、再等 `ShutdownAsync`；并断言 `LastSendToken.IsCancellationRequested` |
   | 64 | 重要 | 失败清理存在**无界悬挂**风险（首次关闭超时后 `finally` 仍无超时 `await`） | **已修**：清理路径**有界**（`WaitAsync(10s)`＋catch）；端口新增**仅清理用** `ReleaseBlockedSend` 逃生放行（注释明示**不得**被当作取消成功的证据） |
   | 65 | 重要 | 身份/许可断言不足（`SubmissionIdentity` 两侧同为空也能通过；未断言许可水位） | **已修**：补 `SubmissionIdentity` 非空、`LastSendSeq == 1`、`Submission.SendSeq ==` 许可水位、`WireSubmitKey ==` 盘上本笔提交键、`Attempt == 1` |
   | 66 | 重要 | 「盘上无受理事实」仅查 `JobId`，证明不足 | **已修**：与相邻接管故障夹具同口径，三字段一起断言（`Intent == Submitted`／`JobId == null`／`AcceptedSendIdentity == null`） |

6. **回归**：全量 **866 通过／1 跳过／867**（跳过项＝既有 P50；基线 552 未降）。**生产接线仍关闭**。

**§24.37-C（补记·2026-09-21 批次二十五）：交错④「接管故障后**取消**」组合**

1. 夹具 `TaskCenterSuccessorPathGateTests.NodeSubmit_TakeoverPersistFailedThenCancel_KeepsResponsibilityNoResend`：
   先制造「远端已受理、接管落盘失败」（接管写专属形态注入，与 §24.37-A 同口径）⇒ 运行收敛 `Unknown`、发送恰一次、
   责任保留；**随后取消**（宿主关闭令牌＝取消运行器传给发送段的**同一令牌**）。
2. 断言：取消**不得**释放责任、**不得**新增发送许可、**不得**把不可考改写成确定结论——取消前后
   未决 `Submission` 的**状态与身份**、节点操作的 `Reconciling`／`LastSendSeq`／`WireSubmitKey` **全等**；
   发送仍恰一次；运行仍 `Unknown`（未被改写）；盘上仍**无受理事实**（`Intent=Submitted`、无 `JobId`、无受理发送身份）。
3. ⇒ §16 交错④ 的**三支**（接管落盘失败／随后重启（host recreation）／随后取消（令牌维度））**均有组件/宿主层夹具**。
4. **[更正·2026-09-21 批次二十七] ④在组件/宿主层**已覆盖**（三支齐备）；以下仅为**范围/层级残余**，逐条移交见 §24.41-C**：
   ①**取消的 E4 控制热键／命令执行器入口映射**（本夹具经令牌维度，不经该入口）；②**子进程级重启**（本夹具为同进程新建宿主）；
   ③**外部启动（E3/E4/E5/E6）路径同项夹具**。（本节第 4 项原「仍记部分」的结论**已作废**，当前口径以 R5.2 稿 §16/§16-A 与 §24.41-B 为准。）
5. **[会诊处置·一轮 3 重要]** ①**时序真实性命中**⇒ 补「接管写故障注入**恰一次**」断言；②**断言空过/事实改写漏检**⇒ 补
   `opBefore.RequestState == Reconciling` 与前后状态比较、两侧身份**非空**、两侧「操作↔未决责任」身份互证、
   `LastSendSeq == Submission.SendSeq == 1`、取消前后运行的 `Intent`／`Key`／`Attempt`／节点结果**逐项相等**；
   ③**令牌身份主张收窄**：本夹具的取消**不在飞发送**，故**不**主张「取消发生在发送所用同一枚令牌上」——
   该因果证据由 **§24.39**（在飞发送版，端口侧 `SendCanceledByToken`）提供，并在夹具注释中显式说明分工。
6. 回归：全量 **867 通过／1 跳过／868**（跳过项＝既有 P50；基线 552 未降）。**生产接线仍关闭**。

### 24.40 落地登记：§17 P54「接管失败映射的显式断言」（[新增·2026-09-21 批次二十六]）

**A. 本批落地事实（组件层；**未改生产行为**）**

1. 夹具 `ArbitrationAdmissionServiceTests.SendLayerOutcome_MapsToAdmissionResultAndResponsibility`（Theory 三支）：
   把**发送层报告**与**准入结果层**的映射**逐项显式断言**（此前只有分类/枚举级覆盖，映射本身未被点名）：

   **接管落盘失败支的构造（[会诊阻断处置]）**：`Sender` 仍报 **`Accepted("ext:accepted", null, "job-1")`**，
   仅让接管钩子 **`TakeoverPersist` 失败且不写台账**——初版让 `Sender` 直接回 `Unknown` 会使接管钩子**根本不执行**、
   目标空过（属阻断级构造错误，已改）。操作用 `OperationType.NodeExecution`＋`ResourceRef="node:n-1"`＋`NodeId="n-1"`。

   | 发送层结果 | 准入结果 | 结果维 | 责任维 | 责任载体 | 台账 |
   |---|---|---|---|---|---|
   | `Accepted`（受理） | `Accepted` | **`None`**（本层无权威终态；终态只由完成层承载） | `Pending`（远端作业存续 ⇒ **受理≠结清**） | 节点操作 `Accepted`、`Submission` **已关闭**、`JobId="job-1"` 可读 | **1 条**且逐项核对：`SubmissionIdentity`／`SendSeq`／`JobId`／`CandidateId` 与操作一致，`ActionId` 非空 |
   | `Accepted(job-1)` ＋ **接管钩子失败**（真实接管落盘失败） | `Reconciling` | **`Unknown`**（已受理但接管未落盘 ⇒ **结果不可考**） | **`Pending`**（不得报成功、不得反解为「确定未受理」） | 节点操作 `Reconciling`＋三侧身份对齐（`result`／操作／未决 `Submission` 的 `SubmissionIdentity` 与 `SendSeq` 全等）＋`LastSendSeq==1`（许可已发布、**不得重发**） | **0 条**（接管未落盘 ⇒ **不得写台账**） |
   | `Rejected("boundary_precheck_rejected", false, "host:boundary")` | `TerminalRejected` | `None` | `Settled` | 节点操作 `TerminalRejected`、`Submission` 已关闭 | **0 条**；原因码含 `boundary_precheck_rejected`、证据源含 `host:boundary` |

2. **口径纠正（本批实测暴露）**：**「受理」不等于「责任结清」**——受理后远端作业存续，责任维按 §24.6-5 为 **`Pending`**；
   夹具初版按 `Settled` 断言即被实测纠正（该纠正已写入夹具断言与文档）。
3. **[会诊处置·一轮 1 阻断／3 重要]** ①**阻断（构造错误）**：初版「接管落盘失败支」让 `Sender` 直接返回 `Unknown`，
   接管钩子 `TakeoverPersist` 根本不会执行 ⇒ 目标空过；**已改**为「`Sender` 报 `Accepted(job-1)` ＋ 接管钩子失败且不写台账」。
   ②**断言辨识力（重要）**：补结果维（受理 `None`／接管未落盘 `Unknown` 的**区分**）、台账条目**逐项身份核对**
   （`SubmissionIdentity`／`SendSeq`／`JobId`／`CandidateId`／`ActionId` 非空）、三侧身份与 `SendSeq` 对齐、
   拒绝支的 `ReasonCode`／`EvidenceSource` 点名。③**收敛结论过早（重要）**：本节按修正后的真实构造重新收敛。
   ④**交接稿批次范围（重要）**⇒ 页首一览与计数同步（不依赖后置纠错）。
4. 回归：全量 **870 通过／1 跳过／871**（Theory 三支各计一项；跳过项＝既有 P50；基线 552 未降）。**生产接线仍关闭**。

**B. 状态与残余**

1. **§17 P54 本项收敛**：取消 Task／执行失败分类的映射此前已有覆盖（`CommandExecutorExternalStartAdmissionTests`
   的 `ToExecution`／`ToCompletion` 分阶段映射），**接管失败映射**由本节在**准入结果层**显式断言 ⇒ 本项按组件/宿主层收敛。
2. **仍未覆盖（禁悬空）**：①**外部启动（E3/E4/E5/E6）路径**上的接管失败映射端点（本节为节点后继路径的准入结果层）；
   ②适配器**入参**层（`externalStartAdmission` 委托注入前的形状）与真实入口层证据（§23.1）。均归 **B4/真实入口层**。

### 24.41 B4 收口包：提交点清单・并发屏障・残项移交（[新增·2026-09-21 批次二十七]）

> **性质与状态**：本节是 **B4 的收口交付物**（提交点清单＋并发屏障收口＋残项移交）。
> **收口状态＝已收口（限定范围）**：经**四轮会诊**（第 1 轮 1 阻断／4 重要 ⇒ 第 2 轮 1 阻断／1 重要 ⇒
> 第 3 轮 2 处 ⇒ 第 4 轮**无必改项**），全部处置逐条登记于 §24.41-D。
> **限定范围**：§24.41-A 提交点清单、§24.41-B 已登记的组件/宿主/组合根验收证据、§24.41-C 全部残项的责任·完成证据·门禁移交归档。
> **本结论不代表生产开门、不代表 R5.8 签署、不授权真实 User 目录切换**；所有「已移交·未验收」项目继续受原门禁约束。

**A. 提交点清单（承 §15，逐点状态；**测试接线态 与 生产态必须分开读**）**

| 提交点 | 通道／入口 | **测试接线态** | **生产态** | 证据 | 未闭合 |
|---|---|---|---|---|---|
| S1 `SendPreparedAsync→TaskStart`（节点提交） | 托管节点提交 | 门开时经仲裁装饰器（`CreateRunner` 唯一组装点） | **`_successorAdmissionWired` 恒不传 ⇒ 走既有直通路径** | `TaskCenterSuccessorPathGateTests`（门开/门关双态）＋§24.34/§24.36/§24.37-C/§24.39/§24.40 | 生产节点改道门关闭 |
| S2/S3 `StartGroupCoreAsync`／`StartOneClickCoreAsync`（v2 `task.start`） | E3/E5 | **测试组合根已注入**准入委托并通过 §24.4 组合根夹具族 | **生产组合根未注入 `externalStartAdmission`** | §24.4 夹具族（E3 经面＋发送计数＋闸门双向＋控制热键双向＋33 笔容量＋落盘失败＋冷启动＋关闭交错） | 组合根「阻断零发送／冲突零重发」端到端反例仍欠 |
| S4a `StartViaV2IpcNoKillAsync`（抢占分支） | E3/E5 既有抢占 | 接线态不可达（`allowPreemption:false`） | 既有行为 | 源码审查（§14） | — |
| S4b `StartSpecifiedTaskAsync←RunSpecified` | 策略/触发器收尾 | — | **未接入** | 代码路径核实 | **owner 已要求接入新调度器；实现与端到端验收未交付**（§24.41-C#8） |
| S5 ext 队列薄封装 | ext 通道 | 自身无仲裁检查（依赖上游） | 同 | 上游夹具 | 「通用 `SendCommandAsync` 可直发」待粗化（§24.41-C#11） |
| S7 `ExecuteHotkeyAsync→action.execute_hotkey` | E4 实际发送 | **测试接线态已注入**并验证（控制热键旁路＋E4 双向夹具） | **生产组合根未注入** | §24.27-A 夹具族 | 生产未注入 |
| S8a `ExecuteResumeAsync(cancel:false)` | E2 恢复 | 经 §5.1 专用准入 | **生产已接线（`admissionWired:true`）**——**E2 不属 §24.41-E-2 的关闭范围** | `TaskCenterHostRecoveryAdmissionTests`（含 P28 正/负向） | — |
| S8b `ExecuteResumeWithBusyRetryAsync` | 策略收尾恢复 | — | **未接入** | 代码路径核实 | 与 S4b 同性质（§24.41-C#8） |

**B. 并发屏障收口（§16 六类交错）**

| 交错 | 状态（**组件/宿主层**） | 证据 |
|---|---|---|
| ① 首节点抢先 | 已覆盖 | §24.29 |
| ② 调用者≠获选者 | 已覆盖 | §24.33 |
| ③ 准备阶段故障 | 已覆盖 | §24.31／§24.34／§24.36 |
| ④ 受理接管故障 | 已覆盖 | §24.37／§24.37-C |
| ⑤ 连续超 32 节点 | 部分 | §24.30＋P50 暂停／P19①（§24.41-C#1、#2） |
| ⑥ 四类入口覆盖 | 部分 | §24.35＋M1／G4a／入口表（§24.41-C#3、#4、#12） |

**汇总＝4 项已覆盖（①②③④）／2 项部分（⑤⑥）**（与 R5.2 稿 §16-A 一致）。
**口径优先级声明（消除旧正文的相反结论）**：以下旧句**已被本汇总取代**，不得再作为当前状态引用——
① §24.37-C 第 4 项曾写「故 §16 行④与 §16-A 仍记「部分」」；② §24.39 第 4 项曾写「交错④的『接管故障后取消』组合仍欠」；
③ §24.28-D 的 P6 行曾写「仍欠（归 R5.3 取消链批次）」。三者均在本批次二十七由**统一口径**取代
（④按组件/宿主层分支齐备记已覆盖；范围/层级残余单列于 §24.41-C）。

**C. B4 残项移交（逐条：承接＋完成证据要求＋门禁＋状态）**

| # | 残项 | 承接批次／角色 | 完成证据要求 | 保留门禁 | 状态 |
|---|---|---|---|---|---|
| 1 | **P50 负载敏感诊断套件**（33 节点端到端暂停，须覆盖整个用例类） | 独立诊断批（施工方） | 可单独重复运行的负载复现入口＋根因定位＋该类夹具稳定 | 节点改道门保留；Skip 与严格断言不放宽 | **已交付（组件/宿主层）**（[批次四十九] §24.61：负载复现入口（`BGI_R5_P50_LOAD_REPRO=1`，不改编源码）＋根因＝Windows 文件争用家族未纳入有界重试＋修复后连续两轮 10×16 全绿＋**33 节点 Skip 已解除**、全量连续 3 轮 951/2/953；**残余**＝多进程/实机负载观察归 R5.8） |
| 2 | **P19① 原因码逐次取证**（批次三十二原诊断已按 owner 裁决更新） | R5.8 真实入口与拒绝审计验收（施工方：门面/宿主夹具负责人） | 组件层已验证 `submission_conflict` 释放被拒请求槽位、保留拒绝原因与原未决责任；尚需真实入口端到端核对拒绝映射、审计可查与解除冲突后新许可 | 外部启用门 | **组件层已交付；真实入口未验收**（§24.64） |
| 3 | **首节点绑定（§12.3 M1）**：父子关系与首节点绑定持久化 | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 持久化父子/首节点绑定＋「E1 关闭后首节点另行取许可」夹具 | 节点改道门 | **部分已交付·未验收**（[批次四十四] §24.54：父子绑定由门面自行反查写入＋首/后继节点各自取许可夹具＋自有占用限定豁免＋发送前 F11 复核；**仍欠**：生产归属事实源与真实入口层证据，见 §24.55） |
| 4 | **G4a 启动移交/暂停续行的来源登记** | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 移交受理处落定固定 Scope/绑定来源记录＋后继提交可查得 | 节点改道门 | **已交付（组件/宿主层）**（[批次四十五] §24.56：来源权威下沉运行台账 `admissionSourceScope`（受理同次落盘）＋来源解析判据（面板唯一／歧义拒绝／零条回落且需移交受理事实）＋恢复路径删除当前 epoch 兜底＋端到端「移交启动→首节点继承固定 Scope」；**仍欠**：真实入口/实机证据与移交来源在自有占用豁免中的适用） |
| 5 | **P8 通用失败分类细化** | R5.8 实现闭合序列（施工方：设计＋夹具负责人） | 「网络前可证实未发送 vs 已发送后失败」的可判据接缝＋夹具（现非 success 一律 Unknown） | 外部启用门 | **部分已交付·未验收（组件/宿主层）**（[批次五十] §24.62：证据载体 `BgiNotSentException`（三码枚举）＋判据纯函数＋统一提交边界分类（确定拒绝·开重试窗口 vs 保留未决责任）＋宿主映射＋**16 支夹具**含宿主级对照）；**整行未验收**＝E3/E4/E5 外部启动路径分类端点（R5.8／外部启用门）＋真实入口层证据 |
| 6 | **集合②持续观察重绑** | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 重启后未终结台账记录**重绑观察责任**并可后续结算 | 外部启用门 | **已交付（组件/宿主层）**（[批次四十六] §24.57：观察义务**持久化重绑**（时点／句柄／单调次数）＋写事务内按完整发送身份复核＋句柄冲突与扫描事实矛盾的 fail-closed＋溢出零污染＋「重绑→权威终态→后续结算」夹具；残余＝§24.55 同源的真入口/控制面证据） |
| 7 | **S2/S3 组合根端到端反例**（F11／编号通道冲突零重发／优先级抢占） | R5.8 实现闭合序列（施工方：组合根夹具负责人） | **F11 阻断零发送支已交付**（§24.42）；旧 v2 回退路径下的冲突夹具是历史证据，已被“只走编号队列”合同取代（§24.50-A.1、§24.65）；优先级抢占行为已由 owner 定义，组合根交错待实现 | 外部启用门（生产未注入）；抢占退出确认与身份/级别事实待交付 | **部分已交付·未验收** |
| 8 | **S4b／S8b 未接入提交点**（策略/触发器收尾两处） | R5.8 实现闭合序列 | owner 已要求两条自动动作接入新调度器；S4b 走普通优先级准入，S8b 走恢复专用准入 | 相应入口门禁 | 产品行为已明确；接线与端到端验收未交付 |
| 9 | **外部启动（E3/E4/E5/E6）路径接管失败映射端点；子进程级重启；E4 取消入口映射** | R5.8 真实入口层（§23.1；施工方：入口夹具负责人） | 该路径上的端到端夹具或实机证据 | 外部启用门＋R5.8 门 | 已移交·**未验收** |
| 10 | **§24.36 范围残余**：重启/恢复后不换键、不新增许可；真实磁盘故障分布 | R5.8 实现闭合序列（施工方：恢复项负责人） | 恢复入口再次取许可路径的夹具＋故障分布说明 | 节点改道门 | **部分已交付·未验收（整行未整体验收）**（[批次四十一] §24.52 重启后阻塞半；[批次四十八] §24.60 **子项「恢复入口再次取许可」已交付**＋**故障分布枚举**；**仍欠**＝子进程级重启与真实磁盘故障分布，门禁保留） |
| 11 | **S5 通用 `SendCommandAsync` 直发粗化** | R5.8 安全粗化批（施工方：守卫负责人） | **已交付（组件/宿主层）**（[批次三十四] §24.46 字面量 6 操作类别／9 种拼写→[批次四十三] §24.53 非字面量 10 文件→[批次五十一] §24.63 **间接形态 4 类零容忍（实测 0 处：反射按名／反射按名称筛选／方法组·别名／拼接构造）＋`OpCode` 字面量**直方图**（含只读 3 文件 17 处）＋`OpCode = <表达式>` 4 文件＋逐支正反例**）；**根本限制如实登记**：文本守卫**不证明**「授权先于发送」（由运行期零发送/发送前复核夹具承担）；IL 级间接分析与 Roslyn 化降误报 ⇒ **可选加固**（不构成本行未交付部分） | 外部启用门 | **已交付（组件/宿主层）**（[批次五十一] §24.63） |
| 12 | **⑥ 入口表逐项签注** | R5.8 收口文档＋夹具批（施工方：入口表负责人） | **部分已交付（[批次三十三] §24.45-B）**：**Scope 来源维度 4/4 已签注**（面板＝§24.35 格 A／A′；移交・续行＝格 B fail-closed；恢复＝P28 正/负向）；**首节点绑定维度**：面板启动（测试接线态）**已验**、移交/续行/恢复**未验**（[批次四十四] §24.54；M1 余项见 §24.55）；提交路由：面板/移交＝测试接线态已验、续行/恢复＝生产已接线（**S8b 策略收尾来源未接入**） | **节点改道门＋R5.8 门** | **部分已交付·未验收** |
| 13 | **§24.33 范围残余**：节点后继/外部启动路径「调用者≠获选者」端到端证据 | R5.8 真实入口层（施工方：入口夹具负责人） | 真实入口层绑定证据 | R5.8 门 | 已移交·**未验收** |
| 14 | **§24.38–§24.39 范围残余**：E3/E4/E5 关闭保护交错；恢复路径令牌透传；`RetryAsync` 令牌合同 | R5.8 取消链收束批（施工方：宿主关闭保护与恢复链负责人）；owner 已裁定本期不加单次点击撤销令牌，软件关闭须取消在途发送并查证 | 各入口关闭/回执/未知结果交错取证；恢复路径与 `RetryAsync` 令牌合同按宿主生命周期分别闭合 | **节点改道门／外部启用门／恢复路径既有门禁＋R5.8 门** | 已裁决、部分组件实现·**未验收**（见 §24.65／§24.68） |
| 15 | **§24.40 范围残余**：`RetryableRejected` 责任维显式断言；适配器入参层形状 | R5.8 夹具批（施工方：映射断言负责人） | **`RetryableRejected` 已交付（[批次三十三] §24.45-A）**：四支 Theory 断言 `Kind=RetryableRejected`＋责任维 `Settled`＋**操作状态 `RetryableRejected`**＋**`LastSendSeq==1`（本轮许可已签发并消费；重试须新许可）**＋`Submission` 关闭＋台账 0；**适配器入参层形状仍未验收** | 外部启用门 | **已交付（组件层；[批次三十八] §24.49）**——适配器入参形状矩阵已补齐（`start_group`／`start_oneclick`／`hotkey` manual 与 v2 的**五字段＋三委托非空**）；**不得外推**：不证明发送行为、完成观察协议语义、真实入口/组合根接线、IPC/ext/v2 集成或生产开门 |
| 16 | **owner 侧门禁**：编号协议方向、§23.1 十入口实机、R-8 远程五场景、`task.single.native` 复核、真实 User 目录切换授权、R5.8 签署 | **owner** | 实机证据／真实目录授权／最终签署 | 各自门禁 | 编号协议方向已裁决；实机、授权与签署仍未完成 |
| 17 | **§24.37-B2 未决责任阻挡再次发送的门面级直接证据** | R5.8 实现闭合序列（施工方：门面夹具负责人） | **已交付（[批次三十七] §24.48）**：`UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance`——不可考（`Unknown`）后对同一候选**绕过运行器意图预检**再发起创建 ⇒ 门面以 `Error/submission_conflict` 拒绝＋**零新增发送**＋**许可水位不推进（`LastSendSeq` 仍 1）**＋**未决发送身份/状态不变** | 节点改道门＋R5.8 门 | 已交付（组件层） |
| 18 | **任务优先级与执行中抢占规则**（owner 2026-09-23 已裁决） | R5.8 实现闭合序列 | 最高级锄地和一键锄地可中断任意任务；普通任务按可配置优先级比较，同级后到者打断；先停当前并确认退出，之后才准入/发送新任务 | 新任务调度门 | 行为已明确；权威占用身份/级别/来源、停止回执和抢占交错未交付 |
| 19 | **`submission_conflict` 被拒尝试占主槽位**（批次三十二为修复前诊断；owner 已裁定释放被拒者槽位、保留审计） | R5.8 真实入口与生产门禁验收（施工方：宿主/组合根夹具负责人） | 组件层已覆盖连续拒绝不占满主槽位、原未决身份不变、原因审计保留、冲突解除后新请求获许可；真实入口拒绝映射与生产接线仍须验证 | 外部启用门 | **产品规则已裁决、组件层已交付；真实入口未验收**（§24.64） |

**D. 结束会诊（本轮）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| D1 | 阻断 | §24.41-D 指向不存在的「D′（下一批）」，而 E-1 已宣布「收口完成」——**悬空挂账＋提前产生收口效力** | 已修：本节改记**本轮会诊结论与处置位置**，并明确「**收口状态＝未闭合（待验证轮）**」；E-1 收窄并加前置条件 |
| D2 | 重要 | C 表多项只写「B4／B4 后续」⇒ 循环移交、以移交代替验收；且**遗漏 7 项仍有效残项**（P21 入口表、P49 范围残余、§24.37 未决责任阻挡的门面级证据、§24.33 范围残余、§24.38–39 范围残余、§24.40 范围残余、S5 粗化） | 已修：C 表重写为 **17 行**（**注：B4 收口后 §24.43 又新增 #18「组合根执行占用路径有界返回（阻断式）」，故当前清单为 19 行（#18／#19 之后的实际行数；[更正·批次三十七] 原写 18 行为过时统计）；#18 **不属**本轮四轮会诊范围**），逐条给出承接批次／责任角色（**不再使用「B4／B4 后续」循环承接**，统一为「R5.8 实现闭合序列（施工方：…）」）、完成证据要求、保留门禁、状态（新增 10–15 与 §24.37-B2 行） |
| D3 | 重要 | A 表混淆**测试组合根**与**生产组合根**（S2/S3、S7 的「已注入」与「未注入」并列；S1 未限定门开路径） | 已修：A 表拆为**测试接线态／生产态**两列逐一限定；S8a 注明**E2 不属 E-2 关闭范围** |
| D4 | 重要 | 交错④存在**过时状态残留**（§24.37-C 第 4 项、§24.39 第 4 项、§24.28-D P6 行）与新汇总 4/2 相反 | 已修：B 节增**口径优先级声明**并逐一点名被取代锚点；三处旧句在各自位置已由批次二十五/二十六的更正取代 |
| D5 | 重要 | E-1 无条件使用「完成」，在 D 未闭合时提前产生收口效力 | 已修：E-1 改为「**仅指本批清单、已验收组件/组装证据与完整残项移交完成归档**」＋前置条件「仅在 D 形成『无剩余必改项』结论后生效」 |

**会诊收敛过程（四轮，逐轮留痕）**
1. **第 1 轮**：1 阻断（D 悬空＋提前产生收口效力）／4 重要（C 表循环移交与漏项、A 表混淆测试与生产组合根、B 节旧口径未取代、E-1 无条件「完成」）⇒ 见上表逐条处置。
2. **第 2 轮**：1 阻断（C 表仍循环承接／漏 §24.37-B2 行／状态与门禁错配）／1 重要（B 节锚点错、§24.37-C-4 与 §24.39-4 旧句仍相反）⇒ 处置：C 表统一改「R5.8 实现闭合序列（施工方：…）」并新增 §24.37-B2 行（C#17）、C#8/C#16 状态与 C#12/C#14 门禁修正、两处锚点与旧句原位更正。
3. **第 3 轮**：2 处（C#17 被空行隔断脱离表格；两处仍写「第 3 项」）⇒ 已修（删除空行、改「第 4 项」）。
4. **第 4 轮（验证）**：**无必改项** ⇒ 按限定范围宣布 **B4 收口**。

**E. 效力声明（逐字保留）**

1. **B4 收口**仅指本批**提交点清单、并发屏障收口与完整残项移交已归档**；所有标为「已移交／未验收」的项目
   **继续受各自门禁约束**，**不构成实现验收完成**。**本声明自 §24.41-D 第 4 轮验证会诊判「无必改项」后生效**（限定范围见本节页首状态栏）。
2. **生产接线仍关闭**：`_successorAdmissionWired` 生产恒不传；E3/E4/E5 生产组合根未注入准入委托；
   `start_bgi(args)` 覆盖未落地。（**不含** S8a 的 E2 恢复专用准入——该路径生产 `admissionWired:true`，另按 §5.1 验收。）
3. **R5.8 签署仍待 owner**（§23.4 门禁并集逐项闭合，或 owner 书面「显式移交并保留门禁」）。
4. 回归证据：全量 **870 通过／1 跳过／871**（跳过＝既有 P50 严格用例，断言未放宽；基线 552 未降）。

### 24.42 落地登记：§24.41-C#7「S2/S3 组合根反例·阻断零发送」支（[新增·2026-09-21 批次二十九]）

**A. 本批落地事实（真实组合根；**未改生产行为**）**

1. 夹具 `R5CompositionRootAcceptanceTests.CompositionRoot_F11Active_DeniedZeroSendNoLeaseSideEffect`：
   组装形状＝**真实组合根**（真实 `CommandExecutor` ＋ `externalStartAdmission: host.AdmitExternalStartAsync` ＝测试接线态；
   传输＝进程内管道替身），注入 `TaskCenterAdmissionSeams { F11Active = true }`；**组名中性**（不含 `F11` 字样）。
2. 断言（**因果证据优先**，[会诊加固]）：①`Status != "success"`；②**前提**——传输 `Ready` 且已订阅（防「传输故障伪造零计数」）＋调用前 `arbitration` 目录不存在；
   ③**因果**——结构化责任维 `ResponsibilityState == None`（该拒绝不产生发送责任）＋消息命中 **F11 专属文案**`F11 独立停止闸门激活`（**不得**靠「零发送」或组名回显推断）；
   ④**零发送（双面）**——`ext.task.start == 0` **且** `task.start == 0`（回退面 v2 直发同样零）；
   ⑤**零租约副作用（严格）**——调用后 `arbitration` 目录**仍不得存在**（F11 判定先于租约获取，§7.1-1）；防御分支下若 Handoff 段存在，必须 `Operations`／`PreObservations` 皆空且 `Submission`／`Pending` 皆 null；
   ⑥外部启动台账文件不得存在。
3. **未交付（同批登记）**：**冲突零重发**支——需要替身**可脚本化冲突回执**（`task_running`／`execution_occupied` 形态）
   以驱动 S2/S3 的「首次＋冲突重试」两个静态发送点，本批**未建该替身能力**，故该支仍为「已移交·未验收」。
4. 回归：全量 **871 通过／1 跳过／872**（跳过项＝既有 P50；基线 552 未降）。
   **负载观察（如实登记）**：首轮全量曾现 **1 次** P50 同类红灯——`TaskCenterSuccessorPathGateTests.NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission`
   （隔离复跑通过、随后全量复跑绿）。**断言未放宽、Skip 维持、P50 保持阻断式挂账**（口径同 §24.28-B，不因本轮绿线关闭）。
   本批新增夹具属另一收集（`TaskCenterPipeTransport`）：**未发现与该红灯的直接共享状态，但因果关系未确定**（不同收集不排除全量负载／调度影响；隔离通过、复跑绿不构成排除）。**生产接线仍关闭**。

**B. 状态更新**：§24.41-C#7 由「已移交·未验收」改为「**部分已交付·未验收**（**F11** 阻断零发送支已交付；**冲突零重发支**与**执行占用零发送支**仍欠——后者受阻于 §24.43）；
其余残项不受影响。**不得**据此把「S2/S3 组合根反例」整体计入已完成。

### 24.43 诊断结题：组合根「执行占用」路径的「未返回」＝**等待 ext 队列完成**（非挂起）；并登记**占用门禁适用范围待澄清**（[批次三十·三十一]）

**A. 原始观察（批次三十；保留原样，供追溯）**
1. 造景：真实组合根（真实 `CommandExecutor` ＋ `externalStartAdmission: host.AdmitExternalStartAsync`＝测试接线态 ＋
   进程内管道替身），`statusSnapshotProvider: () => new ControlStatus { TaskRunning = true }`（执行占用）。
2. 观测：`executor.ExecuteAsync(StartGroupCommand(...))` **20s 超时**；预算提至 **75s 仍超时未返回**。
   （超时来自**测试外层 `WaitAsync`**，非 `ExecuteAsync` 自身失败返回；`WaitAsync` **不取消**底层任务。）

**B. 诊断（批次三十一；临时探针，15s 预算，已撤销）**
1. 探针在 15s 未返回时读取替身端口计数：**`ext.task.start = 1`**、`task.start = 0`、`task.status = 0`、
   `action.execute_hotkey = 0`、**`ext.task.queueStatus = 2`**、`task.stop = 0`。
2. **判定**：请求**已经发送且恰好一次**（走 ext 队列），随后**在等待队列/作业推进**
   （`ext.task.queueStatus` 轮询）；替身脚本默认 `queueStatus = "pending"`（永不 `completed`）⇒ 调用**不会返回**。
   **⇒ 现象是「等待完成」，不是「路径挂起」。**

**C. 结论更正（[批次三十一] 更正批次三十的措辞）**
1. **原「疑似生产挂起风险 / 无界等待」表述作废**：根因是**既有 ext 队列的完成等待语义**（真实协议下作业终会推进），
   不是无界阻塞；批次三十的“疑似生产路径缺陷／造景问题待判定”改判为**造景（替身未推进队列）+ 既有等待语义**。
2. **但保留一条待澄清口径**（**新登记、非缺陷断言**）：在「BGI 控制面快照 busy」造景下，
   **E3 外部启动仍发送一次并进入队列等待**——这与 §23.3／§24.4 中「**执行占用 ⇒ 零发送**」的门禁
   **适用范围**不一致：该门禁究竟适用于**节点执行提交**（组件层已有夹具）还是**全部入口（含 E3 外部启动）**？
   **澄清前不得声称 S2/S3 组合根「执行占用零发送」通过**（与批次三十结论一致），
   亦**不得**把本观察当作已确认生产缺陷。

**D. 承接与证据要求**
1. **承接**：R5.8 实现闭合序列（施工方）；**裁定**：占用门禁适用范围的**语义归属**属**设计/owner 级**（本次不代裁）。
2. **完成证据要求（按裁定结果二选一）**：
   ①若判「E3 亦须阻断零发送」⇒ 需实现阻断并使 `ext.task.start == 0`（含 `task.start == 0`）＋未签发许可＋无未决 `Submission`；
   ②若判「E3 允许一次入队＋等待完成」⇒ 须把「执行占用零发送」在 §23.3／§23.9／§24.41 中**限定到正确层级**，
   并为 E3 提供「恰一次入队＋队列推进后正常返回」的夹具（替身需推进 `queueStatus`）。
3. **状态**：§24.41-C#18 随之改写为「**口径澄清项**（待设计/owner 裁定）＋夹具」。

**E. 复现材料（含局限）**
1. 每次观测使用**全新临时根与全新宿主/端口/替身**；前提＝客户端 `StartAsync` 完成、`State == Ready`、已 `SubscribeAsync([])`。
2. 预算记录：20s×1、75s×1（批次三十）；诊断探针 15s×1（批次三十一）。
3. 局限：批次三十两次观测**超时前未记录发送计数**（故当时「占用态零发送」**未取得证据**）；
   批次三十一探针补齐了计数（见 B.1）。两次观测均**未做重复统计**。
4. **工作区**：候选夹具与临时探针**均已撤销**；除**本批文档改动**外**无测试/生产代码残留**（非「工作区全净」）。

**F. 回归影响**：批次三十、三十一**均未新增/修改测试与生产代码**；全量 **871 通过／1 跳过／872**（基线 552 未降）。
**生产接线仍关闭**。

### 24.44 新发现·**疑似缺陷（待设计确认）**：`submission_conflict` 被拒尝试**各留一个主槽位**（[新增·2026-09-21 批次三十二]）

**A. 证据（临时夹具诊断，已撤销；逐次原因码 + 表状态转储）**

1. 造景：组件级门面；`Sender` 恒返回 `Unknown`（保持未决发送责任）；循环发起 **32 次不同的节点创建**
   （`ResourceRef=node:n-i`、各自 `RunBinding/CursorRef` 互异），第 33 次另发起一个创建。
2. **逐次准入结果（33 次调用全部记录）**：
   - 第 1 次：`Reconciling`／`send_unknown`；
   - 第 2–32 次：**`Error`／`submission_conflict`**（31 次连续被拒）；
   - 第 33 次：`Error`／**`operations_capacity_full(active=32,pendingTransfer=0,tombstone=0,earliestCleanable=none)`**。
3. **表状态转储（33 次尝试 → 32 个操作在册）**：
   `n-1: Reconciling/Active/send=1`；**`n-2 … n-32: Queued/Active/send=0`**（31 个）。
   ⇒ 被 `submission_conflict` 拒绝的创建**仍各留下一个 `Queued`＋`Active` 的操作**，占用主槽位。

**B. 判定（疑似缺陷，**待设计确认**）**

1. 在「存在 1 笔未决发送」的前提下，**每次被拒尝试消耗一个主槽位**：约 **31 次**重复触发即可把 32 个主槽位填满，
   随后新的创建被 `operations_capacity_full` 拒绝——**而没有任何执行在跑**（可用性风险）。
2. 与既有文档预期**不一致**：§4.2 / §23.3 要求落败/被拒一方归入
   `{TerminalRejected, RetryableRejected}`（终局化后经 `TerminalPendingTransfer → Tombstone` **释放**主槽位，§4.1a 三区口径）；
   而实测为**既不终局化、也不迁墓碑**的 `Queued` 常驻占槽。
3. **不涉及无双跑**：本路径**未发送**（`send=0`）、无重发；影响面是**容量/可用性**与状态分类一致性。

**C. 承接口径（决议前不代裁）**

1. **裁定**（设计/owner 级）：`submission_conflict` 应归
   ①**可重试拒绝**（`RetryableRejected`：先按 §4.2 关闭/终局化并释放槽位，允许按重试窗口再驱动）；或
   ②**登记保留**（`Queued` 占槽是预期语义，则须给出**容量保护**：如被拒不计容、或强制清理入口与上限保护）。
2. **证据要求（按裁定二选一）**：①终局化 ⇒ 夹具须证「31 次被拒后仍可创建（无 `Active` 残留、`tombstone` 计入）」
   且被拒方原因码/状态满足 §23.3 的落败白名单；②保留占槽 ⇒ 须更新容量公式文档，并提供「被拒尝试不导致不可用」的
   保护证据（例如上限内仍可启动、或提供清理路径）。
3. **状态**：登记 **§24.41-C#19「已移交·未验收（疑似缺陷·待设计确认）」**；**外部启用门保持**。

**D. §17 P19① 的增量（如实）**

1. 本次诊断**已取得「逐次原因码」证据**（见 A.2/A.3 的逐次序列）⇒ P19① 的「**逐次取证**」目标**在证据层面达成**；
   但因其暴露了本节的语义问题，**夹具待裁定后按实际语义重写**再交付（当前**不计为已完成**）。

**E. 工作区与回归**：临时夹具与诊断断言**均已撤销**（该测试文件 blob 与 HEAD 一致）；
本批**未新增/修改测试与生产代码**；全量 **871 通过／1 跳过／872**（基线 552 未降）。**生产接线仍关闭**。

### 24.45 落地登记：§24.41-C#15「`RetryableRejected` 责任维显式断言」＋#12「⑥ 入口表逐项签注」（[新增·2026-09-21 批次三十三]）

**A. #15：`RetryableRejected` 映射显式断言（组件层；未改生产行为）**

1. 夹具扩展：`ArbitrationAdmissionServiceTests.SendLayerOutcome_MapsToAdmissionResultAndResponsibility`
   由三支扩为**四支**（受理／接管落盘失败／副作用前确定拒绝／**可重试拒绝**）。
2. 可重试拒绝支（`SendOutcome.Rejected("queue_full", retryable: true, "host:precheck")`）实测口径（**三处经实测/会诊校准**）：
   `Kind == RetryableRejected`＋**责任维 `Settled`**（**本轮发送责任已确定结清、无未决责任**；**不是**「未产生发送责任」——许可**确已签发并消费**）＋**操作状态 `OperationRequestState.RetryableRejected`**
   （**不是** `TerminalRejected`——初版断言被实测打回）＋**`LastSendSeq == 1`**（占位时**确实签发过**许可并被本次尝试消费；
   **初版按 0 断言亦被实测打回**——「可重试」意味着下次重试须**重新签发新许可（`sendSeq` 递增）**，不得复用本轮，§3.2a／§3.3）
   ＋`Submission` 已关闭＋台账 **0** 条；**精确原因码**（`result.ReasonCode == "queue_full"` 且落盘 `op.LastResult.ReasonCode` 同源、`Retryable == true`）。
3. **[会诊加固] 补「重试须重新签发新许可」的因果断言**：同身份 `RetryAsync` ⇒ **`LastSendSeq` 由 1 递增至 2**、发送计数由 1 增至 2、重试成功路径亦关闭 `Submission`、接管台账 1 条。
   **范围限定**：本支证明的是**门面映射 + 白名单内重试语义**；**不主张**「非白名单即使 `retryable:true` 也会被拒绝」（该反向证据不在本节）。
4. 回归：全量 **872 通过／1 跳过／873**（Theory 多一支；其余不变；跳过项＝既有 P50；基线 552 未降）。

**B. #12：⑥ 入口表逐项签注（三维×四入口；**部分签注**）**

| 入口 | Scope 来源 | 首节点绑定（§12.3 M1） | 提交路由 |
|---|---|---|---|
| **面板启动（E1）** | ✅ **固定登记继承**（§24.35 格 A 逐字一致＋**格 A′** 纪元变化判别反例） | ❌ **未实现**（§24.41-C#3） | ✅ 经仲裁面（S1；门开/门关双态夹具；**生产 `_successorAdmissionWired` 未接线**） |
| **启动移交** | ⚠ **仅负向已验**（无来源 ⇒ fail-closed 零发送，§24.35 格 B）；**正向来源登记未实现**（G4a，§24.41-C#4） | ❌ **未实现** | ⚠ 仅**通用节点提交**证据（S1）；**该入口自身未单独验收** |
| **暂停续行（E2）** | ⚠ **无来源负向已验**（§24.35 格 B）；**正向来源继承未单独取证**（P28 覆盖的是 Interrupted 恢复路径）；**S8b 未接入** | ❌ **未实现** | ✅ 经 §5.1 恢复专用准入（S8a，生产已接线）／⚠ **S8b 直发未接入** |
| **Interrupted 恢复（E2）** | ✅ **有来源继承**（P28 正/负向：逐字一致／纪元变化 ⇒ `stale_epoch` 终局拒绝） | ❌ **未实现** | ✅ S8a（生产已接线） |

**维度汇总（[会诊更正]）**：**四入口均已逐项签注，但 Scope 来源维度并非「4/4 验收通过」**——**正向来源证据**：面板＝格 A／A′（✅）、Interrupted 恢复＝P28 正/负向（✅）；**仅负向证据**：启动移交、暂停续行＝格 B「无来源 ⇒ fail-closed」（⚠ 正向来源登记未实现）；
**首节点绑定 0/4 未验收**（M1 未实现）；**提交路由**面板/移交＝测试接线态已验、续行/恢复＝生产已接线（除 **S8b 策略收尾来源未接入**）。
⇒ §24.41-C#12 记「**部分已交付·未验收**（Scope 来源维度已签注；首节点绑定维度未验收；S8b 来源未接入）」。

**C. 状态与门禁**：§24.41-C#15＝**部分已交付·未验收**（**仅** `RetryableRejected` 门面映射已交付；**适配器入参层形状仍欠**）；#12＝**部分已交付·未验收**（Scope 来源维度：面板/恢复正向已验、移交/续行仅负向；首节点绑定维度未验收；S8b 来源未接入）。
**生产接线仍关闭**（`_successorAdmissionWired` 恒不传、E3/E4/E5 生产组合根未注入）；外部启用门与节点改道门保持。

### 24.46 落地登记：§24.41-C#11「S5 通用直发粗化」——**执行类直发字面量结构守卫**（[新增·2026-09-21 批次三十四]）

**A. 本批落地事实（测试-only；未改生产行为）**

1. **问题（S5 登记的待粗化点）**：既有结构守卫 `SubmissionPointInventoryTests` 只覆盖
   `OpCode = "task.start"` 与 `ExternalOperations.TaskStart,` 两个模式 ⇒ 通用
   `SendCommandAsync(operation, …)` 的**停机／取消／热键执行**等**执行类直发点**没有任何守卫，
   **新增未准入调用方不会被发现**。
2. **本批处置**：同一守卫类新增 `ProductionExecutionSends_MatchRegisteredInventory`，
   把**九种已登记的源码拼写**纳入计数登记（[会诊收窄] **不**概括为「全部执行类直发」）：
   ①`OpCode="task.start"`；②`OpCode="task.stop"`；③`OpCode="action.execute_hotkey"`；
   ④`ExternalOperations.TaskStart,`；⑤`TaskStop,`；⑥`TaskCancel,`；⑦`"ext.task.start"`；⑧`"ext.task.stop"`；⑨`"ext.task.cancel"`（末三项＝**ext 线协议字面量**，防新增硬编码直发）。
3. **登记清单（文件 → [九模式计数]，实测基线）**：
   `Services/CommandExecutor.cs = [6,1,1,0,0,0,0,0,0]`；`Services/BgiExternalClient.cs = [0,0,0,1,1,1,1,1,1]`；
   `Services/TaskCenter/BgiWorkflowExecutionBoundary.cs = [0,0,0,1,0,0,0,0,0]`。
   守卫断言：①**不得出现含未登记执行类直发字面量的文件**；②登记项必须存在且**计数不变**；③文件集合守恒。
4. **能力边界（与既有守卫同口径，如实，勿夸大）**：**文本匹配计数**、非语义调用点识别——
   **会被发现**：新增含上述任一拼写的**文件**、或已登记文件的**计数变化**（含同文件内新增同形语句）；
   **不会被发现**：**变量化／别名／拼装操作名**、拼写不同或**计数守恒**式的等价替换、`PluginCommand` 等其它形态；
   **不证明**「授权先于发送」，也不能替代 §17 P1–P57 与真实入口证据。
5. 回归：全量 **873 通过／1 跳过／874**（新增 1 个 `[Fact]`；跳过项＝既有 P50；基线 552 未降）。

**B. 状态**：§24.41-C#11 记「**部分已交付·未验收**」——**九种已登记源码拼写**已纳入结构守卫（**不等于**「执行类直发已封闭」）；
**仍未覆盖**：**非字面量形态**（变量/别名/拼装操作名直发）、`IpcClient` 侧的**只读**操作（`task.status`／`config.*` 不属执行类、
本守卫**不覆盖**）、以及「守卫不证明授权先于发送」这一根本限制（须由 §17 相关门禁与真实入口证据承担）。

### 24.47 落地登记：P50 诊断入口（可单独、可重复运行）与复现条件（[新增·2026-09-21 批次三十六]）

**A. 本批落地事实（测试-only；未改生产行为；**默认不增加日常回归负载**）**

1. **P50 原要求**（§17 P50）：须建立「**可单独、可重复运行**的负载复现入口」＋诊断日志，用于定位根因；
   此前该入口**未建立**（登记为欠缺项）。
2. **本批交付（[会诊收窄] 只是「隔离重复探针」，**不满足** P50 的负载复现入口要求）**：
   `TaskCenterSuccessorPathGateTests.P50_DiagnosticRepeat_OptIn`（**默认 `Skip`**，不计为通过；启用＝临时移除 `Skip`，可设 `BGI_R5_P50_REPEAT=<n>`，默认 10 轮），重复执行「节点逐节点释放」重夹具；失败时输出**取证快照**
   （`state`／`sends`／`note`／逐操作 `请求状态·许可水位·区域·原因码`）。**未设环境变量时直接返回**
   （等价不执行）⇒ **不改变任何既有夹具断言、不增加日常负载**。
3. **实测（本批）**：`BGI_R5_P50_REPEAT=10` 与 `=20` 两次运行**均全绿**（各约 3–5 秒）⇒
   **该异常在「隔离 + 重复」条件下不复现**。
4. **复现条件的观察（[会诊收窄] 相关性，非充分因果）**：本会话**两次全量并发运行**各观察到 **1 次**同负载敏感夹具
   （`TaskCenterHeavyE2E` 收集内 `NodeOperation_TerminalizedBeforeRunEnds_OnNextNodeAdmission`）红灯，而探针
   **隔离重复 10/20 轮均绿** ⇒ 现有证据**提示**该现象与**全量并发负载高度相关**；**触发条件与根因仍未确定**
   （样本仅 20 轮，且不能排除低概率/时序/机器状态相关的单夹具缺陷）。

**B. 状态与残余（P50 仍未关闭）**

1. **已满足（有限）**：存在**可单独运行、可重复**的**隔离探针**，失败时给出取证快照。
2. **仍欠（P50 核心要求）**：①**「负载下重复」的入口**——须随**全量并发负载**运行（或在负载注入下运行）
   并**覆盖目标夹具/整个用例类**；本探针**两者皆无** ⇒ **不得**据此认为 P50 的「诊断入口」要求已满足；
   ②**根因定位**（仍无诊断日志级证据）；③生产侧负载敏感性未消除；④33 节点端到端用例**维持 Skip**（断言未放宽）；
   ⑤**取证充分性**仍欠（探针当前只输出最终状态/发送计数/逐操作快照，缺原始 lease/日志/时点/负载参数）。
   ⇒ **§17 P50 本体表述（「诊断入口未落实」）应更正为「隔离探针已建立（§24.47），负载下入口仍未建立」**。
3. **门禁**：节点改道门保持；**P50 保持阻断式挂账**（不因本批绿线关闭）。

**C. 回归**：全量 **873 通过／2 跳过／875**（跳过项＝既有 P50 的 33 节点端到端用例 ＋ 本批新增的**默认 Skip 诊断探针**；基线 552 未降）。
**生产接线仍关闭**。

### 24.48 落地登记：§24.41-C#17「未决责任阻挡再次发送」的门面级直接证据（[新增·2026-09-21 批次三十七]）

**A. 本批落地事实（组件层；未改生产行为）**

1. 夹具 `ArbitrationAdmissionServiceTests.UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance`。
2. 构造与断言：
   - 第 1 笔（节点执行、`ResourceRef=node:n-1`）发送结果为 **`Unknown`** ⇒ 操作停在 **`Reconciling`**、
     `LastSendSeq == 1`、`SubmissionIdentity` 非空、未决 `Submission` 状态 `Reconciling` 且身份与操作**全等**；
   - **绕过运行器意图预检**（测试直接进门面）对**同一候选**再发起创建 ⇒ 门面以 **`Error/submission_conflict`** 拒绝，
     且 **零新增发送**（`sends` 仍为 1）、**许可水位不推进**（原操作 `LastSendSeq` 仍为 1）、
     **未决发送身份与状态不变**（`SubmissionIdentity`／`SendSeq`／`SubmissionState.Reconciling` 全部保持）。
3. **语义边界（与 §24.44 的分工）**：本夹具钉住的是「**未决责任必须阻挡再次发送**」这一**预期语义**；
   「**被拒尝试是否应占主槽位**」（容量/可用性口径）曾是另一独立问题，owner 已裁定释放被拒请求自己的槽位并保留审计；组件实现见 §24.64，真实入口仍未验收。
4. 回归：全量 **874 通过／2 跳过／876**（新增 1 个 `[Fact]`；跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 状态**：§24.41-C#17 记「**已交付（组件层）**」；其余残项不受影响。

### 24.49 落地登记：§24.41-C#15 收口——适配器入参形状矩阵补齐（[新增·2026-09-21 批次三十八]）

**A. 本批落地事实（测试-only，仅**加强既有夹具断言**；未改生产行为）**

1. 按会诊要求把「**适配器 → 准入委托**」边界的请求形状矩阵补齐（四个入口／来源）：
   - `start_group`（E3）：**五字段**（`Namespace`／`WorkflowId`／`ResourceRef`／`TriggerOccurrenceId` 占位符／`SourceDetail`）＋
     **三类委托非空**（`ExecuteAsync`／`CompletionProvider`／`CompletionObserver`，后者由既有观察语义夹具覆盖）；
   - `start_oneclick`（E3）：五字段（`onedragon:` 段）＋**三类委托非空**（本批新增）；
   - `hotkey_execute`（E4）**manual** 来源：五字段＋三类委托非空（本批新增委托断言）；
   - `hotkey_execute`（E4）**v2** 来源：**补齐** `WorkflowId`／`ResourceRef`／`SourceDetail` ＋ 三类委托非空（此前仅断言两项）。
2. 断言只作用于**请求形状/静态映射**，**不**调用 `ExecuteAsync`（不在本项内扩张完成委托的终态语义验证）。
3. 回归：全量 **874 通过／2 跳过／876**（无新增用例，仅加强既有断言；跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 状态登记（按会诊给出并满足前置后的口径）**

> **§24.41-C#15 已交付（组件层）**：组件级夹具覆盖 E3 `start_group`／`start_oneclick` 与 E4 `hotkey_execute`（manual/v2）
> 在**适配器 → 准入委托**边界构造的 `ExternalStartAdmissionRequest` 字段映射，以及
> `ExecuteAsync`／`CompletionProvider`／`CompletionObserver` 的**非空形状**。

> **不得外推**：仅证明**组件层请求形状与静态映射**；**不证明**执行委托的真实发送行为、完成观察的协议语义、
> 真实入口/组合根接线、IPC/ext/v2 集成、恢复重绑、**生产接线**或**生产开门**。

### 24.50 落地登记：§24.41-C#7「S2/S3 组合根反例·冲突零重发」支（[新增·2026-09-22 批次三十九]）

**A. 本批落地事实（组合根级；未改生产行为）**

1. 当时的 v2 冲突夹具（现因 owner 新合同改为 `R5CompositionRootAcceptanceTests.CompositionRoot_V2ConflictSetup_NoFallbackSend`；下列计数为**历史结果**，不是现行验收）：
   **v2 `task.start` 脚本化业务冲突**（替身 `AcceptV2TaskStart=false` ⇒ `task_already_running`）时，
   **接线态**（真实组合根 + `externalStartAdmission` 注入）必须**不得触发既有「冲突重试」的第二次发送**。
2. **构造纠正（诊断证据，避免空过）**：首版直接脚本化 v2 冲突即运行 ⇒ **在 90s 预算内未返回**，诊断计数显示
   **`task.start = 0`、`ext.task.start = 1`**——说明**接线态 E3 优先走 ext 队列**（v2 脚本未被触达），
   超时是**等待 ext 队列完成**（同 §24.43 的结论，不是挂起）。⇒ 要覆盖 v2 冲突，必须**先让 ext 不可用**
   （替身 `AcceptHello=false` ⇒ 客户端降级 `Legacy` ⇒ 回退既有 `core()`＝v2 `task.start`）。
3. 断言（已修构造后实测通过）：`task.start` **恰一次**（**冲突零重发**）、`ext.task.start == 0`、
   适配器出口**不得报成功**、仲裁面**已登记**该操作且**本轮许可已签发**（`LastSendSeq == 1`，责任保持）。
4. 回归：全量 **875 通过／2 跳过／877**（跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 状态**：§24.41-C#7 记「**已交付（组合根/组件层）**」——**F11 阻断零发送支**（§24.42）与**冲突零重发支**（本节）
两半齐备。**限定**：冲突支经「**ext 不可用 ⇒ 回退 v2**」路径取证；**ext 可用时的 v2 冲突脚本不被触达**（该分支不存在，已由诊断证据说明）。

### 24.51 落地登记：**文档—夹具名一致性守卫**（[新增·2026-09-22 批次四十]）＋首轮检出的一处真实悬空引用

**A. 本批落地事实（测试-only；未改生产行为）**

1. **新增守卫** `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/DocsFixtureReferenceGuardTests.cs`：
   从两份 R5 设计稿（§24 独立稿、R5.2 稿）中抽取**夹具/测试方法名形状**的标识符
   （保守两形状：①测试类名点方法名的引用；②下划线连接的方法名），逐一到**测试源码全文**中核对存在性；
   并含**抽取器最小反例**（防「提取不到任何名字」式假绿）与**抽取量下界**断言（>100）。
2. **能力边界（如实）**：只核对**名字是否存在**；**不**校验该夹具是否真的覆盖文档所述断言、**不**校验章节锚点/行号，
   也不替代会诊（会诊仍查语义与效力）。抽取规则保守 ⇒ **拼写变体可能漏报**。
3. **首轮检出（真实缺陷）**：R5.2 稿 R5.3.1-① 曾点名一个**测试源码中不存在**的夹具
   （「占用未确认 ⇒ 零启动＋零许可＋`Queued`」那条）——**已更正**为真实证据：
   组件级 `Occupied_NeedPreemptConfirm_WinnerStaysQueued`；「解除后新请求可获准」＝纯函数级
   `ArbitrationOrderingTests.NeedPreemptConfirm_WhenOccupied_And_AllowRequestExecution_WhenFree`。
4. **回归**：全量 **877 通过／2 跳过／879**（新增 2 个用例：守卫 1 ＋ 抽取器反例 1；跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 价值与后续**：本守卫把「文档点名不存在的夹具」这类缺陷**机械化检出**（此前只能靠逐轮会诊人工比对，
本会话已多次出现同类问题）。**建议**在 §7 收口与 R5.8 签署前把本守卫纳入常规回归（已在默认集合内）。

### 24.52 落地登记：§24.36 范围残余之「重启后未决责任仍阻挡、不新增许可、不换键」（[新增·2026-09-22 批次四十一]）

**A. 本批落地事实（组件级模拟宿主重建；未改生产行为）**

1. 夹具 `ArbitrationAdmissionServiceTests.RestartAfterUnknown_SameCandidateStillBlocked_NoNewPermitNoKeyChange`：
   - ① 门面 A 使某候选进入**不可考**（`Unknown`）⇒ 操作 `Reconciling`、许可水位 1、未决 `Submission` 在册且身份与操作全等；
   - ② **模拟重启**：在**同一租约目录**上经**接管观察**新建门面 B（**不同所有者** ＝ `pid:testB`、**新实例**；并断言接管后 **`LeaseId` 与 `OwnerEpoch` 确实更替**）；
   - ③ 门面 B 对**同一候选**再发起创建 ⇒ 仍以 `Error/submission_conflict` 拒绝，且 **B 侧零发送**、
     原许可水位（`LastSendSeq==1`）、**非空 `WireSubmitKey`（不换键）**、发送身份（`SubmissionIdentity`／`SendSeq`）**均不变**、
     未决状态仍 `Reconciling`；并断言 ④`r1`／`r2` **请求身份不同而候选号相同**、⑤**B 侧未新增预观察**、
     ⑥若 `r2` 已登记则 **`LastSendSeq==0` 且无发送身份**、⑦第二次被拒**不得改变 B 的所有权代次**（不得借拒绝重新接管/续期）。
2. **范围限定（如实）**：本节是**组件级**「宿主重建」（同进程新建门面 + 接管），
   **不是**子进程级重启；**恢复入口再次取许可**那条路径**仍未覆盖**（归 §24.41-C#10 余项／B4）。
3. 回归：全量 **878 通过／2 跳过／880**（跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 状态**：§24.41-C#10 记「**部分已交付·未验收**」——**重启后阻塞半**已交付（本节；证据含所有权代次更替、许可/键/身份不变、零新增预观察）；
**恢复入口再取许可**与**子进程级重启**仍欠。

### 24.53 落地登记：§24.41-C#11 余项「S5 **非字面量** 直发粗化」（[新增·2026-09-22 批次四十三]）

**A. 本批落地事实（测试-only；未改生产行为）**

1. **问题**：既有守卫只登记「**字面量拼写**」（九种），而 `xx.SendCommandAsync(<标识符>, …)` 这类
   **操作名非字面量**（来自变量/参数）的直发形态**不在登记范围** ⇒ 可绕过。
2. **本批处置**：`SubmissionPointInventoryTests` 新增 `ProductionNonLiteralSends_MatchRegisteredInventory`——
   以保守正则（`\.\s*SendCommandAsync\(\s*(?!ExternalOperations\.)(?!new\b)([A-Za-z_][A-Za-z0-9_\.]*)`）
   登记**非字面量直发点**（文件 → 次数；**实测现状＝10 个文件各 1 处**）：
   端口转发层 `IBgiExecutionPort`（入口透传，操作名由调用方给出）＋两个适配器
   （`BgiWorkflowPrerequisiteAdapter`／`ResourceCatalogService`）＋`BgiWorkflowExecutionBoundary`／
   `BgiWorkflowTerminalExecutor`／`CommandExecutor`／`RemoteConfigEditService`／三个 `MainViewModel*` 文件。
   守卫断言：①**无未登记文件**；②登记项**计数不变**；③文件集合守恒。
3. **能力边界（如实）**：仍为**文本匹配**；**不覆盖**「反射调用」「别名变量间接调用」「字符串拼接后传参」等形态；
   **不证明**授权先于发送，也不替代 §17 原条款与真实入口证据。
4. 回归：全量 **879 通过／2 跳过／881**（跳过项＝既有 33 节点用例＋P50 默认 Skip 探针；基线 552 未降）。

**B. 状态**：§24.41-C#11 记「**部分已交付·未验收**」——**字面量（九种）＋非字面量（10 文件）双登记**已交付；
**仍欠**：反射/别名/拼接形态与「授权先于发送」的语义级守卫（后者不属本项范围）。

### 24.54 落地登记：§12.3 M1①②③⑤「父登记/首节点绑定 ＋ 自有驱动占用的**限定**豁免」（[新增·2026-09-22 批次四十四]）

**A. 本批落地事实（生产代码＋夹具；生产外部启动接线与节点改道门仍关闭）**

1. **问题（§12.3 M1）**：E1 流程登记只授权「登记该 run 的驱动」，其 `Accepted` **不构成任何节点受理证据**；
   而该 run 的**节点子提交**在自有驱动在飞（BGI 侧 `TaskRunning=true`）时会被「执行占用」整体拒绝 ⇒
   后继节点**永远拿不到自己的发送许可**（实测表现为节点停留 `Queued`、`sends=0`、运行 `Unknown`）。
   同时**父子/首节点绑定未持久化**，「豁免只能覆盖可证明的父授权子提交」缺少可判据载体。
2. **父子绑定持久化（M1⑤）**：租约 v3 `OperationRecord` 新增加法字段 `parentRequestIdentity`；
   由**门面在创建事务内自行反查**同一 `runBinding` 的流程登记父操作写入（`ResolveParentRequestIdentity`：
   **唯一命中**才绑定；无命中/歧义/父类型非 `FlowRegistration`/来源形不符/父 workflow ≠ 本笔 workflow
   ⇒ **不绑定、不补造**）。**不采信调用方自报**（`AdmissionRequest` 无该字段；`ContinueUse`/`Retry` 只读已持久化值）。
3. **自有驱动占用的限定豁免（M1③）**：`ArbitrationFacts` 新增 `OwnInFlightRunBindings`（**宿主注入的归属事实**），
   `IsOwnParentOccupationExempt` 全条件成立才豁免：①归集**恰为本笔 runBinding 一项**；②按**持久化记录**判定
   `OperationType == NodeExecution` 且候选带 `NodeId`；③父登记按严格判据唯一命中、状态 ∈ {`Accepted`（已接管关闭）、
   `TerminalCompleted`}、`SubmissionIdentity` 非空且 `LastSendSeq > 0`；④**同 run 无其他在飞节点责任**
   （`Queued/InRound/Granted/Sending/Accepted/Reconciling`）；⑤全局未决发送槽为空；⑥父子绑定与 workflow 一致。
   **归属来源（保守口径）**：BGI 控制面快照只给「是否有任务在跑」这一布尔值，`_drives` 包含关系**不能证明**
   占用确属本候选 run ⇒ **生产恒不填归属＝一律不豁免（fail-closed）**；夹具经接缝 `OwnInFlightRunBindingsProvider`
   注入，且**外部启动台账占用存在时一律不给归属**。
4. **轮次/锁内一致性与分流**：`ProjectGuardFacts` 把**真实 F11 闸门**与**盘上任一冲突待决**（⇒ `facts_unknown`）
   投影进本轮事实；`Decide` 前再经 `RecheckRoundGuard` 做一次锁内复核并**重判**；**分流只允许发生在「纯占用
   ＋本轮身份唯一 ＋子裁决恰为 `NeedPreemptConfirm`」**时（`HandleNeedPreemptConfirmAsync` 结清其余候选、
   剩余子集按归一事实重判）——避免「同轮混入无关候选即整体误拒」与「拆散同候选号冲突组」两种反例。
5. **发送前 F11 复核（占位后、未发送）**：节点/流程/外部启动路径（`ProcessWinnerAsync`）与**恢复专用边界**
   （`AdmitRecoveryAsync`）在调用 Sender **之前**复用 `BlockSendIfF11Async`——命中即以「占位后、网络前的
   **本地未发送证明**」（§4.2c）原子关闭该笔 Submission，操作记 `TerminalRejected`＋`f11_active`＋
   `EvidenceSource=local_not_sent_pre_send`＋`AnsweredSendSeq=本笔轮次`，返回 `F11Blocked`／责任维 `Settled`
   ＋完整发送关联；**关闭失败 ⇒ `NeedReconcile` 且同样不发送**（fail-closed）。
6. **外部启动类型的调用位置决定（§24.17 加固）**：E3/E4/E5 入口 `OperationType` **写死** `ExternalStart`
   （不再透传入参字段），避免「已执行外部副作用、接管按错误持久化类型分派」。
7. **夹具**（＋9 项，全量 **905 通过／2 跳过／907**，0 失败；基线 552 未降）：组件矩阵
   `Ownership_ExecutionOccupied_OnlyVerifiedOwnParentChildExempted`（11 支：`exempt`＋10 支 fail-closed，含
   `own_multiple_runs`／`parent_unresolved`／`parent_wrong_type`／`parent_source_shape_invalid`／
   `parent_workflow_mismatch`／`other_node_in_flight`／`own_withdrawn_before_occupy`）＋
   `Ownership_ExecutionOccupied_MixedRound_ExemptNodeStillAdmitted`（混轮按候选分流）＋
   `Ownership_ExecutionOccupied_SameCandidateIdPeer_PreventsSplitAndSending`（整组身份冲突不被拆散）＋
   `Ownership_ExecutionOccupied_BlockedSubDecisionNotPreempt_NoSplitNoSend`（不拆分回退）＋
   `Ownership_ExecutionOccupied_WithUnknownOrF11_MixedRoundNotSplit`＋
   `Ownership_ExecutionOccupied_RealF11GateFlipsAfterEnqueue_RoundStillF11Blocked`＋
   `Ownership_ExecutionOccupied_DiskConflictPending_RoundStaysFactsUnknown`＋
   `Ownership_ExecutionOccupied_F11AndDiskConflict_F11WinsWholeRound`＋
   `Ownership_ExecutionOccupied_F11FlipsDuringOccupy_NoSendAndLocalNotSentClose`＋
   `RecoveryAdmission_F11FlipsDuringOccupy_NoSendAndLocalNotSentClose`＋
   `ParentBinding_MissingFieldInExistingV4_ReadsValidAndNotBackfilled`；宿主层
   `NodeSubmit_AfterE1Closed_OwnDriveOccupied_PerNodePermitAndParentBinding`（E1 关闭后首/后继节点**各自取许可**、
   父子绑定落盘、许可互不相同）＋`NodeSubmit_OwnDriveOccupied_LedgerOccupationOverridesSeamOwnership_NoExemption`
   ＋`ExternalStart_SelfReportedOtherType_PersistsExternalStart`。
8. **会诊**：实现批**十轮**（1 阻断/4 重要 → 3 重要 → 3 重要 → 2 高+1 重要 → 1 高+2 重要 → 1 高+2 重要 →
   1 高+1 中 → **第十轮「无必改项」**），逐条处置全部落入代码/夹具/残余登记；**第十轮判定：无必改项**。

**B. 状态**：§24.41-C#3「首节点绑定（§12.3 M1）」由「已移交·未验收」改为
「**部分已交付·未验收**」——持久化父子绑定 ＋「E1 关闭后首节点另行取许可」夹具**已交付（组件/宿主层）**；
**仍欠**（见 §24.55）：生产归属事实源（控制面需暴露带来源的占用事实）与真实入口层证据。
§24.41-C#12「⑥ 入口表」随本项目**首节点绑定维度**由 0/4 升为**部分**（面板启动路径已验，测试接线态）。
**生产外部启动接线与节点改道门仍关闭**；本批**不代表 R5 收口、不代表生产开门**。

### 24.55 残余登记：批次四十四移交项（[新增·2026-09-22]）

| # | 残余 | 承接 | 完成证据要求 | 保留门禁 | 状态 |
|---|---|---|---|---|---|
| 1 | **生产归属事实源缺失**：控制面快照只有布尔 `TaskRunning`，无法证明占用归属本候选 run ⇒ 生产侧自有占用豁免**恒不生效**（节点子提交在自有驱动在飞时仍被占用阻断） | R5.8 真实入口层／控制面（发送权威侧） | 控制面暴露**带来源**的占用事实（runBinding/jobId/来源）＋宿主归属由该事实推导＋端到端夹具 | 节点改道门＋外部启用门 | 已移交·**未验收** |
| 2 | **外部活信号与真实发送之间的严格原子性**：`BlockSendIfF11Async` 之后、真实发送动作之前仍非原子（本地布尔再读无法闭合） | 设计／控制面批（发送权威侧） | 控制面提供「F11 清零才入队」原子操作＋本地占位携带其关联身份＋失败按确定未发送关闭 | 节点改道门 | 已移交·**未验收**（本批已消除「占位后发送前」路径的确定性反例） |
| 3 | **去重镜像在占用路径的状态语义**：`MirrorMergedAsync(winnerAccepted:false)` 使镜像落 `NotSelected`＋`merged_duplicate`（终局），而本笔调用方拿到 `NeedPreemptConfirm` ⇒ 即时结果与续用分类不一致、镜像失去交接后继续资格（**既有语义，非本批引入**） | 设计批（合并/镜像语义） | 重新定义镜像交接期状态与 `ContinueUse` 分类（含对既有 dedupe/镜像验收结论的影响面说明） | 节点改道门 | 已移交·**未验收** |

### 24.56 落地登记：G4a「启动移交来源登记」——来源权威下沉运行台账（[新增·2026-09-22 批次四十五]）

**A. 本批落地事实（生产代码＋夹具；生产外部启动接线与节点改道门仍关闭）**

1. **问题（§13.11 G4a／AMD-1-5 第 2 类来源）**：启动移交受理的运行**不经面板 E1**（无 `flow:` 流程登记操作），
   其固定 Scope/绑定来源此前**无处落定** ⇒ 后继节点提交在改道门开启后按「无已登记仲裁授权」响亮拒绝（格 B）。
2. **来源权威＝运行台账受理登记事实（会诊后重构）**：`WorkflowRunRecord` 新增加法字段
   `admissionSourceScope`，由 `RunStore.CreateRun(..., admissionSourceScope:)` 在**受理提交点（锁内、与运行创建
   同一次落盘）**写入 `bgi:local:{受理时 epoch}` ——**不存在**「已受理但来源未登记」的跨存储窗口；
   `epoch` 不可用 ⇒ 字段留空（该 run **无固定来源** ⇒ 后继准入不签发、不发送；受理本身仍按 R4.9 成立）。
   **会诊否决的早期形态**：曾尝试在租约 `Operations` 内另造一条 `Handoff` 来源记录——被判定引入跨存储窗口、
   空发送身份的终局错配、主槽位长期占用，且其形状无法与调用方伪造的普通操作区分 ＝ **已撤除**。
3. **来源解析判据（宿主唯一点）**：`TaskCenterHost.ResolveAdmissionParent(sources, run, runId, workflowId)`：
   ①租约侧**面板来源**（`FlowRegistration` ＋ workflow 逐字相等）**恰一条**且 Scope 规范 ⇒ 只认它；
   ②**多条 ⇒ 来源歧义直接拒绝**（禁止回落运行台账）；③**零条** ⇒ 回落运行台账，且要求该 run **确有启动移交
   受理事实**（`Handoffs` 含 `start`/`armTrigger` 绑定）＋`WorkflowId` 逐字相等＋Scope 规范
   （`IsCanonicalAdmissionScope`：`bgi:local:{非空完整 epoch}`）⇒ 返回合成来源身份 `run-source:{runId}`。
4. **来源固定、只比较不重写**：`RunStore.CreateRun` 对非空 Scope 做**形状校验**（畸形 ⇒ 响亮抛出、不落权威字段、
   不产生运行记录）；注册后任何路径**不得**重读当前 epoch 重建 Scope（判别反例见 A.6）。
5. **恢复路径 P28 残余收口**：`SubmitResumeViaAdmissionAsync` **删除"无来源时按当前 epoch 构造"的兜底**——
   来源解析提前到**门面初始化之前**（只读租约实例；不建目录、不取租约、不跑恢复扫描、不启心跳），
   解析为 null ⇒ 响亮拒绝（零租约副作用）。**行为变更登记**：G4a 之前受理的历史遗留运行（既无面板来源、
   也无移交登记 Scope）**不再可恢复**，需显式处置（不得补造）。
6. **夹具**（＋9 项，全量 **917 通过／2 跳过／919**，0 失败；基线 552 未降）：
   宿主级 `StartHandoff_PersistsFixedAdmissionSourceScope_AndSuccessorResolvesIt`（受理后运行记录带固定 Scope、
   反查返回 `run-source:{runId}`＋该 Scope、重放不重选）／`StartHandoff_WithoutEpoch_LeavesNoSourceScope_SuccessorFailsClosed`／
   **端到端** `NodeSubmit_AfterHandoffStart_InheritsRecordedFixedScope`（经移交受理的 run 无面板来源操作，
   节点提交**继承运行记录固定 Scope 逐字一致**、整体成功、发送恰一次）／
   `ResolveAdmissionParent_Matrix`（7 支：面板唯一／面板 Scope 畸形／面板歧义＋运行来源并存 ⇒ 拒／
   零条＋移交受理事实 ⇒ 回落且完整 epoch 不截断／零条但无移交受理事实 ⇒ 拒／畸形 Scope ⇒ 拒／workflow 不匹配 ⇒ 拒）／
   恢复反例 `ResumeRun_NoRegisteredSource_RejectedNoLeaseSideEffect`（拒绝＋**零租约副作用**：租约文件不存在、
   零发送、零 Operation、原运行不动）／`RunStoreTests.CreateRun_AdmissionSourceScope_WhitespaceNormalizedMalformedRejected`
   （写入边界：空白⇒null、完整 epoch 原样、畸形⇒抛出且不建记录）；既有恢复夹具预置运行改为携 start 移交绑定＋固定来源 Scope。
7. **会诊**：**四轮**（1 轮 3 阻断＋3 重要 → 2 轮 2 阻断＋1 重要 → 3 轮 1 阻断＋3 重要 → **第四轮「无必改项」**），
   逐条处置全部落入代码/夹具；**第四轮判定：无必改项**。

**B. 状态**：§24.41-C#4「G4a 启动移交/暂停续行的来源登记」由「已移交·未验收」改为
「**已交付（组件/宿主层）**」：移交受理处落定固定来源 ＋ 后继提交可查得（端到端夹具）＋缺来源 fail-closed。
**仍欠**（§24.55 同源）：真实入口层/实机证据、以及移交来源在**自有占用限定豁免**中的适用（现按 fail-closed）。
**生产外部启动接线与节点改道门仍关闭**；本批**不代表 R5 收口、不代表生产开门**。

### 24.57 落地登记：C 表 #6「集合②持续观察**重绑**」——观察义务持久化与后续结算（[新增·2026-09-22 批次四十六]）

**A. 本批落地事实（生产代码＋夹具；生产外部启动接线与节点改道门仍关闭）**

1. **问题（§24.12-3 集合②／§24.14-6）**：重启/接管后的恢复扫描此前对「外部启动台账**未终结**且本笔仍负发送
   责任」只**计数**（`ObservationKept`）——「停驻≠放弃」缺少**落盘载体**：其他处理者/后续轮次无法从持久化
   事实看出「这笔仍在持续观察中」，也没有可续扫的依据。
2. **观察义务持久化（重绑）**：`OperationRecord` 增三个加法字段 `observationReboundAtUtc`／`observationJobId`／
   `observationRebindCount`；`RecoverExternalStartObservationsAsync` 在**同一权威串行边界**内对每一笔
   集合②目标写回：**重绑时点（本轮一次捕获）＋次数单调 +1 ＋ 台账给出的句柄（可用时）**——
   **不改责任状态、不写终态载体、不释放占用、不重发**。
3. **写事务内按完整发送身份复核**（[会诊处置]）：只凭 `RequestIdentity + Zone` 定位会把**旧轮次的句柄写到已推进的
   新责任**上；现要求写事务内逐笔复核 `OperationType == ExternalStart`＋`Zone == Active`＋`!ConflictPending`＋
   无 `PendingTerminal`／`ExecutionResult`＋责任状态 ∈ {Accepted, Granted, Sending, Reconciling}＋
   `SubmissionIdentity`／`LastSendSeq` **与扫描事实逐字相等**；任一不满足 ⇒ **整笔跳过**（`observation_rebind_state_advanced`）。
4. **扫描事实规范化与 fail-closed**：事实先按**完整发送身份分组**——句柄忽略空值**稳定合并**（与事实顺序无关）；
   同组**两个不同非空句柄**或**终态标志矛盾**（`true`/`false` 并存）⇒ **整组不处理**（既不重绑也不补终局），
   经 `ScanFactConflicts` 计数报告。
5. **零部分写入与溢出保护**：校验（身份／状态／句柄合并与冲突／计数合法性）**全部先行**，通过后**一次性写入**
   三个字段；`ObservationRebindCount` 非法或达 `int.MaxValue` ⇒ 保守失败（`observation_rebind_count_overflow`，
   不回绕、不静默重置、**不写任何字段**）。
6. **报告合同**：`ExternalStartRecoveryReport` 追加 `ObservationRebound`（本轮**实际落盘**笔数）、
   `ObservationRebindFailure`（未全部落盘的原因：状态已推进／句柄冲突／计数溢出／接缝异常）、
   `ScanFactConflicts`（自相矛盾组数）；三者均纳入 `AnythingReported` 与 `ToString()`。
7. **可后续结算（C 表 #6 的完成证据）**：重绑**不吞责任**——随后台账报**权威终态**时仍按集合③用已持久化的
   `PendingTerminal` 走 `SettleCompletionAsync` 唯一顺序补终局（操作 `TerminalCompleted`、未决发送关闭、
   主槽位经迁移释放），且重绑载体作为**历史**保留（不回退、不清洗）。
8. **夹具接缝**：新增 `AdmissionHooks.BeforeObservationRebindPersist`（**夹具接缝；生产装配不设置**）用于
   **确定性**复现「分类快照之后、写事务之前责任被推进」的交错；接缝异常只**放弃本轮重绑**并如实登记
   （`observation_rebind_seam_exception`），**集合③补终局照常继续**（同轮互不连坐）。
9. **夹具**（＋7 项，全量 **929 通过／2 跳过／931**，0 失败；基线 552 未降）：
   `RecoverObservations_UnterminatedLedger_RebindsDurableObservationResponsibility`（重绑落盘＋责任/身份不变＋
   连续两轮次数单调）／`RecoverObservations_RebindTargetAdvanced_SkipsWithoutOverwriting`（快照后被推进 ⇒ 整笔跳过、
   零污染）／`RecoverObservations_JobIdConflict_DoesNotOverwrite_EqualJobIdIdempotent`（同句柄幂等／异句柄不覆盖）／
   `RecoverObservations_DuplicateScanFacts_NormalizedOrderIndependently`（4 支：顺序无关合并／两不同句柄冲突／
   终态标志矛盾）／`RecoverObservations_RebindCountOverflow_NoFieldPollution`（溢出零污染）／
   `RecoverObservations_RebindSeamThrows_ReportedAndSettlementStillRuns`／
   `RecoverObservations_ScanConflictPlusSeamThrow_SeamReasonNotMasked`（原因码不被遮蔽）／
   `RecoverObservations_MixedBatch_RebindsAndSettlesIndependently`（一笔重绑＋一笔补终局各自计数）／
   `RecoverObservations_ReboundThenAuthoritativeTerminal_SettlesLater`（**重绑→权威终态→后续结算**）；
   宿主级既有 `Recovery_UnterminatedLedger_KeepsObservationResponsibility` 追加三个重绑字段断言。
10. **会诊**：**四轮**（1 轮 4 重要 → 2 轮 3 重要＋1 一般 → 3 轮 1 必改 → **第四轮「无必改项」**），逐条处置
    全部落入代码/夹具；**第四轮判定：无必改项**。

**B. 状态**：§24.41-C#6「集合②持续观察重绑」由「已移交·未验收」改为「**已交付（组件/宿主层）**」。
**仍欠**（§24.55 同源）：真实入口层/实机证据与**控制面**侧的持续观察接缝（本轮交付的是本地仲裁面的重绑载体与
结算闭环）。**生产外部启动接线与节点改道门仍关闭**；本批**不代表 R5 收口、不代表生产开门**。

### 24.58 失败模式覆盖映射表（[§17.4-A 第 2／6 条] 已登记清单 → 现状证据；[新增·2026-09-22 批次四十七]）

> **用途**：§17.4-A 第 2 条要求「生产构造开门」类实现**反例先行**，且「失败模式清单覆盖面**不得小于**已登记清单」。
> 本表把**四份已登记清单**（`§23.8` 7 行／`§24.41-C` 19 行／`§24.55` 3 行／`§24.67` 4 行）逐行映射到**处置状态＋指针类型＋证据指针**，
> 并由机械守卫 `FailureModeCoverageGuardTests.FailureModes_AllMappedWithEvidence` 强制：**逐来源固定行数**、
> **行号恰为 1..N**（重复/跳号/未解析数据行即失败）、**每行恰一条映射**、**指针类型合法**、
> **指针非占位/非自引用**、**§ 锚点真实存在**、**夹具名必须定义在其类体内**、**点名的夹具数不得少于 3**（防「全用 § 指针」假绿）。
> **`指针类型` 语义**：`完成证据`＝本行已交付且给出可核验证据；**`部分证据`＝本行仍有未完成部分**（存在已交付证据，
> 但**不得**读作已验收）；`残项登记`＝已按 §24.55 格式登记、未验收；`owner 裁决`＝待 owner 书面裁决。
> **效力**：本表**不**替代任何门禁、**不**把「部分证据／残项登记／门禁保留／已移交」行记为已验收。

| 来源 | # | 失败模式（简） | 当前处置 / 状态 | 指针类型 | 证据指针（§ 锚点 或 夹具） |
|---|---|---|---|---|---|
| §23.8 | 1 | 受理后台账无生产终态回写链 ⇒ 永久占位 | 已交付（组件/宿主层）：完成结算唯一顺序＋宿主运行终态回写 | 完成证据 | §24.15／§24.21 |
| §23.8 | 2 | F11 取消被压成 `result_unknown` | 已交付（组件/宿主层）：取消事实**端到端保留**（结果维 `Cancelled`＋原始终态词）并在结算路径结清；**未结清分支**由未决观察夹具承载 | 完成证据 | `TaskCenterExternalStartAdmissionTests.ExternalStart_CompletionCancelled_SettlesAndReportsCancelled` |
| §23.8 | 3 | 授权 `TargetEpoch` 未绑定实际发送 | **部分**：受理台账按**授权纪元**落盘（`TargetBgiEpoch`）且发送身份与授权快照绑定；**未交付**＝「准入后 BGI 重启窗口」的真实端到端取证 | 部分证据 | `TaskCenterExternalStartAdmissionTests.ExternalStart_Accepted_ExecutesOnce_RecordsLedger_ClosesSubmission` |
| §23.8 | 4 | 生产 F11 事实源为空、占用未合并运行台账 | **门禁保留**：生产 F11 源未接线；占用归属事实源缺失 | 残项登记 | §24.55 |
| §23.8 | 5 | 冷启动／旧 v2／抢占回归 | **部分**：冷启动与旧 v2 组合根夹具已交付；抢占规则已由 owner 明确，任务级事实、抢占交接和端到端证据未闭合 | 部分证据 | §24.25／§24.27／§24.68 |
| §23.8 | 6 | 外部入口未纳入宿主生命周期、发送回调恒 `None` | **部分**：owner 已选择软件关闭取消并核查；宿主令牌已接入部分发送链，E3/E4/E5 全路径交错证据仍未闭合 | 部分证据 | §24.68 |
| §23.8 | 7 | 缺生产组合根闭环夹具 | 已交付（**测试接线态**，生产注入仍关闭）：12 枚组合根夹具 | 完成证据 | §24.42 |
| §24.41-C | 1 | P50 负载敏感诊断套件 | **已交付（组件/宿主层）**：负载复现入口＋根因（文件争用家族未纳入有界重试）＋修复＋**33 节点 Skip 已解除**（全量连续 3 轮 951/2/953）；**残余**＝多进程/实机负载观察（R5.8） | 部分证据 | §24.61 |
| §24.41-C | 2 | P19① 原因码逐次取证 | **组件层部分已交付**；owner 已裁定 `submission_conflict` 被拒请求释放自身槽位并保留审计，真实入口仍未验收 | 部分证据 | §24.44／§24.64 |
| §24.41-C | 3 | 首节点绑定（§12.3 M1） | 部分已交付·未验收（绑定持久化＋首/后继各自取许可已交付） | 部分证据 | `TaskCenterSuccessorPathGateTests.NodeSubmit_AfterE1Closed_OwnDriveOccupied_PerNodePermitAndParentBinding` |
| §24.41-C | 4 | G4a 启动移交来源登记 | **已交付（组件/宿主层）** | 完成证据 | `TaskCenterSuccessorPathGateTests.NodeSubmit_AfterHandoffStart_InheritsRecordedFixedScope` |
| §24.41-C | 5 | P8 通用失败分类细化 | **部分已交付·未验收**：判据接缝＋夹具已交付（§24.62）；外部启动路径分类端点归 R5.8 | 部分证据 | §24.62／§24.31 |
| §24.41-C | 6 | 集合② 持续观察重绑 | **已交付（组件/宿主层）** | 完成证据 | `ArbitrationAdmissionServiceTests.RecoverObservations_ReboundThenAuthoritativeTerminal_SettlesLater` |
| §24.41-C | 7 | S2/S3 组合根端到端反例（F11／编号通道冲突零重发／抢占） | **部分**：F11 零发送已交付；旧 v2 回退冲突证据已被新合同取代，当前须验证编号通道下拒绝零重发；优先级行为已裁决但抢占接线未闭合 | 部分证据 | §24.42／§24.50／§24.65／§24.68 |
| §24.41-C | 8 | S4b／S8b 未接入提交点 | **产品行为已明确**：owner 要求两条自动动作接入新调度器；接线与端到端夹具未交付，旧直发路径尚无强制门禁 | 残项登记 | §13.11／§24.68 |
| §24.41-C | 9 | 外部启动路径接管失败映射端点／子进程重启／E4 取消入口映射 | 已移交·未验收（真实入口层） | 残项登记 | §23.1／§24.37 |
| §24.41-C | 10 | 恢复入口再次取许可、真实磁盘故障分布 | **部分已交付（整行未整体验收）**：重启后阻塞半＋**子项「恢复入口再次取许可」**已交付；**仍欠**＝子进程级重启与真实磁盘故障分布（门禁保留） | 部分证据 | §24.52／§24.60 |
| §24.41-C | 11 | S5 通用直发粗化 | **已交付（组件/宿主层）**：五层登记（字面量 6 操作类别／9 拼写＋非字面量 10 文件＋**间接形态 4 类零容忍**＋**`OpCode` 字面量直方图**（含只读，真·单遍扫描＋**完整 C# 标识符左边界**（`\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}` 全类别有反例）＋token 间隔（跨行/块注释/行注释/制表符）＋字符串边界/转义感知的整段 RHS 精确分类）＋`OpCode = <表达式>` 4 文件）＋**逐支正反例 94 支**；根本限制（不证明授权先于发送）／自改自证／误报面／等号后跨行·未闭合字符串保守失败／**预处理指令/序列化别名/Unicode 转义拼写静默漏扫（已登记）**／IL 级间接分析均如实登记（可选加固） | 完成证据 | §24.46／§24.53／§24.63 |
| §24.41-C | 12 | ⑥ 入口表逐项签注 | 部分已交付：**Scope 来源维度 4/4 已签注（≠ 4/4 验收通过）**；首节点绑定维度部分 | 部分证据 | §24.45／§24.54 |
| §24.41-C | 13 | §24.33 范围残余（真实入口层绑定证据） | 已移交·未验收 | 残项登记 | §24.33 |
| §24.41-C | 14 | §24.38–39 范围残余（E3/E4/E5 与恢复路径令牌） | **部分**：owner 已选择关闭保护；调用方令牌接口不扩展，但宿主关闭取消与核查、恢复路径和重试令牌交错尚未全覆盖 | 部分证据 | §23.4／§24.38／§24.68 |
| §24.41-C | 15 | §24.40 范围残余（`RetryableRejected` 责任维／适配器入参形状） | 已交付（组件层） | 完成证据 | §24.45／§24.49 |
| §24.41-C | 16 | owner 侧门禁（编号协议方向／实机／R-8／`task.single.native`／User 切换／R5.8 签署） | **端口方向已裁决**为只用编号队列、热键先升级；实机、R-8、`task.single.native`、真实 User 目录切换授权与 R5.8 签署仍未完成 | 残项登记 | §23.4／§24.68 |
| §24.41-C | 17 | §24.37-B2 未决责任阻挡再次发送的门面级证据 | 已交付（组件层） | 完成证据 | `ArbitrationAdmissionServiceTests.UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance` |
| §24.41-C | 18 | 执行占用／任务优先级抢占规则 | **产品行为已明确、实现未闭合**：最高级锄地及可配置普通任务按优先级抢占；停止与退出确认前不得发送新任务，权威占用身份/来源/优先级仍缺 | 残项登记 | §24.43／§24.68 |
| §24.41-C | 19 | `submission_conflict` 被拒尝试占主槽位 | **组件层已交付**：owner 已裁定释放被拒请求槽位并留审计；真实入口与生产开门仍未验收 | 部分证据 | §24.44／§24.64 |
| §24.55 | 1 | 生产归属事实源缺失（自有占用豁免恒不生效） | 已移交·未验收（R5.8／控制面） | 残项登记 | §24.54 |
| §24.55 | 2 | 外部活信号与真实发送的严格原子性 | 已移交·未验收（设计／控制面批） | 残项登记 | §24.54 |
| §24.55 | 3 | 去重镜像在占用路径的状态语义 | 已移交·未验收（设计批） | 残项登记 | §24.22 |
| §24.67 | 1 | 接管受理与未受理拒绝并发结算 | 跨服务发送责任认领尚未原子化，存在接受事实被负向关闭覆盖的风险 | 残项登记 | §24.68 |
| §24.67 | 2 | 到期清理后的历史引用与游标重放 | 清理可能留下审计/预观察悬空引用并丢失历史重放防护 | 残项登记 | §24.68 |
| §24.67 | 3 | 抢占确认与继续入口 | 普通续用/重试/占位已 fail-closed；可信确认、证据绑定和确认后继续未交付 | 残项登记 | §24.68 |
| §24.67 | 4 | 裁决、恢复与外部台账读写一致性 | claim 失败恢复、证据版本绑定、恢复补终态及台账读写 fail-closed 尚待审查 | 残项登记 | §24.68 |

### 24.59 落地登记：§17.4-A 执行力落地——失败模式覆盖守卫 ＋ owner 待决单表（[新增·2026-09-22 批次四十七]）

**A. 本批落地事实（测试＋文档；无生产行为变更）**

1. **§17.4-A 第 2 条（反例先行／清单覆盖面）落地**：新增 **§24.58 失败模式覆盖映射表**——把四份已登记清单
   （`§23.8` 7 行／`§24.41-C` 19 行／`§24.55` 3 行／`§24.67` 4 行 ＝ **33 行**）逐行映射到**当前处置状态与证据指针**，
   「门禁保留」行如实标注、不记为已验收。
2. **机械守卫**：`FailureModeCoverageGuardTests.FailureModes_AllMappedWithEvidence` 强制——①**逐来源固定行数**
   （`§23.8`＝7／`§24.41-C`＝19／`§24.55`＝3／`§24.67`＝4）且行号**恰为 1..N**（重复/跳号/未解析数据行即失败）；
   ②映射表**每行恰一条**、**来源集合恰等于**四个已登记来源、解析出的数据行数**恰为 33**（清单外来源与畸形行即失败）；
   ③指针必须给出**类型**（`完成证据`／`部分证据`／`残项登记`／`owner 裁决`）与非占位、**任一锚点非自引用**的指针；
   ④`§` 引用按**严格整串**校验（章节须存在、分节后缀仅 `-C`、`§X-C#N` 须命中该来源表第 N 行；无法解析的 § token 即失败）；
   ⑤夹具名必须**定义在其类体内**（逐文件、单遍词法剥离注释/字符串后按**方法声明**形状匹配）。**能力边界**：只证明
   「清单不漏项、结构未漂移、指针非空且锚点/夹具真实存在」；**不**评价证据强度、不替代会诊。
3. **§17.4-A 第 4 条（owner 待决单表）落地**：交接稿「owner 待决」升级为**单表一次交付**——列＝
   待决事项／甲／乙／影响面／**施工方推荐**／不裁决的后果／**建议默认值＋失效期（待 owner 同意）**；
   5 项推荐分别为 **甲（#18）／甲（#19）／乙收窄版（#14，含宿主生命周期令牌须另行取证）／乙（#8，含
   「无强制门禁」如实声明）／乙（§24.23-B）**；默认值一律标注「**须经 owner 同意方可生效**」，
   **失效期统一为「owner 同意后 7 天」**（#5 为 R5.8 签署前置，故不得以「R5.8 签署前」作期限）；
   未同意时一律**保持未验收＋门禁保留**（不产生自动放行）。同时列出 **B′ 与裁决无关、可继续推进的施工项**
   （§24.58／P50／§24.33／P8／#10／#11）。
4. **§17.4-A 第 1 条（声明面守卫）现状核对**：`ClaimSurfaceGuardTests` 已在册且**逐行纳入新增与移除判定**
   （本批因 owner 表与优先序列变更**重生成清单**，故**本批不援引「措辞批」豁免**，按语义变更走完整纪律）。
5. 回归：全量 **949 通过／2 跳过／951**（新增守卫 1 项＋对抗夹具 19 支；跳过＝既有 33 节点用例＋默认 Skip 的
   P50 隔离探针；基线 552 未降）。
6. **[首轮会诊处置]** 本轮会诊首轮给出 **2 阻断／7 重要／2 建议**，已**逐条处置**：覆盖守卫改为逐来源固定行数＋
   行号恰 1..N＋映射来源全集＋未解析数据行即失败；指针增加**类型**（`完成证据`／`部分证据`／`残项登记`／
   `owner 裁决`）、**任一元素自引用**即拒绝、**完整锚点**（`§24.41-C#N` 须校验章节与表行）校验、夹具改为
   **方法声明**（剥离注释/字符串＋花括号类体）校验；owner #3／#4／#6 依据与失效期修正；交接稿权威状态同步。
   **第二轮复核另指出**：覆盖守卫需拒绝「清单外来源／畸形映射行」（已补）、`指针类型` 与「部分」状态冲突
   （新增 `部分证据` 类型并逐行重标）、过期失效期口径（已统一为「同意后 7 天」）、A 表第一行过期内容（已同步）——
   均在本批内处置；**第三至六轮复核**又依次指出：非法锚点尾部/缩进与加粗的畸形映射行/词法剥离顺序与跨文件配平/
   包装式自引用/映射行额外列吞并/非 `-C` 锚点未验行号/插值洞嵌套花括号——**全部逐条处置**（严格两段式锚点
   tokenizer、逐文件单文件花括号配平、单遍词法状态机＋插值洞递归与 `holeDepth`、按解析锚点判自引用、
   `[^|]` 列形状、`§X#N` 表行校验），并新增对抗夹具 19 支（词法剥离 9 支＋插值嵌套 1 支＋映射列形状 8 支等）。
   **第六轮判定「无必改项」**；其唯一**建议级**（Markdown 包装变体探测）已**采纳**（`[*_`]{0,4}` 探测＋2 支夹具），
   属**收窄假绿通道**的加固、不改变任何声明/门禁语义（§17.4-A 第 3 条）。

**B. 状态**：§17.4-A 六条中 **第 1／2／4 条**已有**可执行载体**（声明面守卫／失败模式覆盖守卫／owner 单表）；
第 3 条（分级处置）、第 5 条（冻结后新发现走残项登记）、第 6 条（安全敏感面冻结须交枚举）为**纪律条款**，
在后续批次的会诊处置与冻结稿评审中执行并留痕。**生产外部启动接线与节点改道门仍关闭**。

### 24.60 落地登记：§24.41-C#10 余项「**恢复入口再次取许可**」与故障分布说明（[新增·2026-09-22 批次四十八]）

**A. 本批落地事实（测试-only；无生产行为变更）**

1. **组件级新夹具** `ArbitrationAdmissionServiceTests.RecoveryEntry_ReacquiresOwnPermit_AfterUnresolvedSubmissionClosed`：
   ①同一 `runBinding` 尚存**未决 `Submission`** 时，恢复准入被 `submission_conflict` **确定拒绝**（因未发布发送许可而
   终局中止）——**零新增发送**、恢复操作 `LastSendSeq == 0`、且**原未决发送的身份原样不变**（既未复用也未动过）；
   ②该未决发送经**权威对账确定未受理**（`ReconcileSettlement.NotAccepted`）关闭后，恢复**再次发起**必须
   **新签发本轮许可**：`LastSendSeq == 1`、`submissionIdentity == sub:{rid}:1`（与旧身份不同）、Sender **恰一次**、
   结算后无未决发送。即「**再次取许可＝重新签发**」，不是沿用旧身份、也不是靠旧许可继续。
2. **宿主级既有夹具补断言**（`TaskCenterHostRecoveryAdmissionTests` 的恢复全贯支）：恢复操作 `LastSendSeq == 1`
   且其发送身份与**启动操作**那笔**互不相同**——证明恢复入口**另行取得**自己的许可，而非继承启动许可。
3. **故障分布枚举（C#10 证据要求的第二半；[首／二轮会诊重要项处置] 按窗口逐行标注覆盖类型）**：
   **三类**含义 = `注入式夹具已覆盖`（由**夹具注入**模拟该窗口，不等于真实进程崩溃）／`设计已定义`（仅有规范文本，
   **不**据此记「已覆盖」）／`真实故障未覆盖`（门禁保留）。**本表只覆盖「外部启动/恢复」链的已知窗口**，不外推为全系统。

   | # | 崩溃/故障窗口 | 覆盖类型 | 证据 |
   |---|---|---|---|
   | 1 | **轮次快照后、裁决前**崩溃（登记已在册） | 注入式夹具已覆盖 | `Recovery_RegisteredBeforeRoundCrash_TerminalAbort_NoSend`（§24.21；屏障 `AfterRoundSnapshot`） |
   | 2 | **占位（许可发布）后、实际发送前**崩溃 | 注入式夹具已覆盖 | `Recovery_PendingSubmissionAtRestart_ConservativeReconcile`（§24.21；屏障 `AfterOccupyBeforeSend`，发送计数 0） |
   | 3 | 远端**已受理**、接管台账/关闭尚未完成时抛异常 | 注入式夹具已覆盖（**抛异常语义，非进程崩溃**） | `SenderAcceptedThenCloseThrows_ConvergesUnknown_NotRejected`（§24.17／§24.37 范围） |
   | 4 | 节点运行记录**接管写落盘失败**（`RunStore.PublishFaultForTest` 条件化接管写） | 注入式夹具已覆盖 | §24.37 |
   | 5 | **外部启动台账**接管落盘失败/受理未完成（不得报「已受理」） | 注入式夹具已覆盖 | `SettleCompletion_NullWhenAcceptanceFails_DoesNotReportPlainAcceptance`（§24.15 批次二） |
   | 6 | `PendingTerminal`／`ExecutionResult` 已写、**台账 Terminal 之前** | 注入式夹具已覆盖 | §24.23-A（集合②/③ 分类与责任保留） |
   | 7 | 台账 Terminal 已写、**Operation 终局之前** | 注入式夹具已覆盖 | `RecoverObservations_LedgerTerminalButOperationNotFinal_TerminalizesFromPendingTerminal`（§24.23-A） |
   | 8 | 台账未终结时的**持续观察重绑**（重启后） | 注入式夹具已覆盖 | §24.57（重绑载体＋后续结算） |
   | 9 | **终局结算期间台账句柄迟到**（回填前不释放槽位） | 注入式夹具已覆盖 | `SettleCompletion_LedgerJobIdArrivesLate_DoesNotReleaseSlotUntilBackfilled`（§24.15 批次二） |
   | 10 | **`PreObservation` 已写、远端句柄尚未取得/不可读**时结算 | 注入式夹具已覆盖（句柄不可读 ⇒ 保守不释放） | `SettleCompletion_JobIdReadHookMissing_FailsClosedAsUnreadable`（§24.15 批次二） |
   | 11 | **外部启动锁外发送期间换主/责任被并发推进** | 组件级注入式夹具已覆盖；宿主完成观察持有原所有者凭据 | `ExternalStartSend_OwnershipChangedDuringSend_LateAcceptIsAdoptedByCurrentOwner`／`HistoricalAcceptedReceiptCannotBeClearedByCurrentRoundNotAcceptedAdjudication`／`AcceptedReceiptIsSavedWhenOwnershipChangesImmediatelyBeforeClaim`（§24.22-C′） |
   | 12 | 普通流程写入的锁外换主（对照口径） | 注入式夹具已覆盖 | `OwnershipChanged_OldFlowWrite_LeaseStaleGeneration`（§24.24） |
   | 13 | 重启/宿主重建后**同候选再次发送**被阻塞 | 注入式夹具已覆盖 | §24.52 |
   | 14 | **恢复入口再次取许可**（未决许可不得复用＋结算后重新签发） | 注入式夹具已覆盖 | §24.60（本批组件＋宿主级夹具） |
   | 15 | 旧格式代 **v1** 记录读取与接管升级 | 注入式夹具已覆盖（**仅 v1**；**v2 未决记录的读取/升级证据不足**） | `LeaseV1_BackwardRead_TakeoverWritesUpgradeToV4`（§24.20-A） |
   | 16 | 旧代/缺字段记录的**隔离态结算事务**本身 | 设计已定义（**无独立用例**，不记「已覆盖」） | §24.20-A′ |
   | 17 | 远端已受理后**进程真实崩溃**（无关闭、无台账终态；重启后对账） | 真实故障未覆盖（现有证据为注入式抛异常） | 门禁保留（§24.41-C#9） |
   | 18 | **子进程级真实重启**（新进程、新租约代次全链） | 真实故障未覆盖 | 门禁保留（§24.41-C#9／#10） |
   | 19 | **真实磁盘故障分布**（IO 抖动、半写盘、磁盘满） | 真实故障未覆盖（仅注入式故障） | 门禁保留（§24.41-C#10） |

4. 回归：全量 **950 通过／2 跳过／952**（新增 1 支夹具；跳过＝既有 33 节点用例＋默认 Skip 的 P50 隔离探针；
   基线 552 未降）。

**B. 状态（[首轮会诊重要项处置] 不改写整行效力）**：§24.41-C#10 **整行仍记「部分已交付·未验收（未整体验收）」**——
本批交付的是其**子项「恢复入口再次取许可」**（组件＋宿主级夹具）与**故障分布枚举**；**仍欠**＝
**子进程级重启**与**真实磁盘故障分布**（门禁保留，属 R5.8 真实入口层／环境批）。
`§24.58` 该行据此记 `部分证据`（不得读作整项已验收）。**生产外部启动接线与节点改道门仍关闭**。

### 24.61 P50 结题：负载敏感红灯根因＝Windows 文件争用家族未纳入有界重试；33 节点用例 Skip 已解除（[批次四十九]）

**A. 根因取证（可重复的负载复现入口）**

1. **入口（本批新增）**：`TaskCenterSuccessorPathGateTests.P50_LoadRepro_WholeClass_UnderControlledLoad`——
   ①**可控负载**（`BGI_R5_P50_LOAD_CPUS`，默认＝处理器数，忙等线程制造 CPU 争用）；②**覆盖整个重夹具类**：
   逐轮**直接调用**类内四个重夹具，其中包含受 P50 影响原本 `Skip` 的 **33 节点端到端**方法本体（直调绕过
   Skip 属性、仍按其**严格断言**执行）；③`BGI_R5_P50_LOAD_ROUNDS`（默认 3）；④失败时汇总各夹具异常消息
   （各夹具内部已带 `Diag(...)` 运行态与逐操作快照）。
   **启用（不改编源码）**：`P50LoadReproFactAttribute` 在**发现阶段**按 `BGI_R5_P50_LOAD_REPRO=1` 决定 Skip ⇒
   `$env:BGI_R5_P50_LOAD_REPRO=1; dotnet test --filter P50_LoadRepro` **可单独、可重复**运行；**默认仍是 Skip**
   （不计为通过、不增加日常负载）。旧探针 `P50_DiagnosticRepeat_OptIn` 保留为更小的隔离重复入口。
2. **复现（修复前）**：`10 轮 × 16 负载线程` 命中 **5/10 轮红灯**，**全部**在 33 节点夹具；取证（原文摘录）：
   `state=Unknown … sends=25 note=… 驱动异常（UnauthorizedAccessException），按未决外部事实标 Unknown收敛。`
   `logs=[[任务中心] 运行驱动异常（UnauthorizedAccessException）：Access to the path is denied.]`；
   另一轮为 `后继提交准备阶段异常（run-…）：UnauthorizedAccessException：Access to the path is denied.`
   **结论（性质）**：这不是「断言/时序假失败」，而是**真实缺陷**——Windows 上文件争用既可能抛 `IOException`，
   也可能抛 `UnauthorizedAccessException`（目标被其他句柄占用/正被原子替换时形如 "Access to the path is denied"），
   而运行记录的**原子替换/读取**与租约锁路径当时**只对 `IOException` 重试** ⇒ 争用下硬失败，运行被收敛
   `Unknown`/`Interrupted`（保守方向、无双跑）。

**B. 修复（生产行为加固；断言未放宽、未用 Skip 隐藏）**

1. `RunStore` 新增 `IsFileContention`（`IOException` ∪ `UnauthorizedAccessException`）与 `WithContentionRetry`
   （默认 80×15ms ≈ 1.2s，**预算耗尽仍原样抛出**）；应用于：①读路径（`Load`／`List`／`UnknownFiles`／
   `HandoffLedgerQuery` 与发布前修订核对）；②**原子替换发布**（`File.Move(tmp, file, overwrite: true)`）。
2. `ArbitrationLeaseStore.WithLockContentionRetry` 同步把 `UnauthorizedAccessException` 纳入该族有界重试
   （与既有 `IOException` 同预算）。
   **[第四轮会诊阻断项处置·扩展]** 同一口径另修三处：①租约**读取路径**弃用 `Directory.Exists`／`File.Exists`
   探测（拒绝访问会被静默折成 `Absent`＝「可获取」）⇒ 改「直接访问 + 异常分类」，争用耗尽一律 `Corrupt`
   （fail-closed、零改写）；②`ReadCore` 不再把争用 `IOException` 吞成 `Corrupt`（改为**同预算有界重试**）；
   ③争用族判定**统一取自 `RunStore.IsFileContention`**（排除 `FileNotFound`／`DirectoryNotFound`，
   避免在「不存在」上白烧预算），`WithLock` 建目录亦纳入重试。
   **[第五轮会诊处置·扩展]** ④**写侧重试边界收窄**：有界重试**只包围取锁句柄**，持锁后
   `ReadCore`／`Publish`（`WriteAllBytes`＋`Move`）／`QuarantineResidues`（枚举＋建目录＋逐件迁移）
   **各自逐点重试** ⇒ 争用时**不再重放业务回调**；⑤**残件探测失败 fail-closed**（不可枚举 ⇒
   `UncertainResidue=true`，不再折成「无残件」）；⑥**无锁快照读后复核锁文件**（以 `Open` 成败分类），
   已出现则丢弃快照改走锁内读取。
3. **纪律**：不修改任何断言、不放宽任何等待预算；改动是「把**同一类瞬时争用**纳入既有有界重试」，耗尽后
   仍是响亮失败（保持保守方向）。

**C. 结题证据**

1. **修复后同参数复现入口**：`10 轮 × 16 负载线程` **连续两轮全绿**（修复前同参数 5/10 轮红灯）。
2. **解除 33 节点用例 `Skip`**：全量回归 **连续 3 轮 `951 通过／2 跳过／953`（0 失败）**——剩余 2 个跳过是
   两个 **opt-in 诊断入口**（P50 隔离探针＋负载复现入口本身），**均为默认 Skip、不计为通过**；
   基线由 950/2/952 提升为 **951/2/953**（33 节点用例**已实际执行**）。
   **第三轮会诊处置后复验**：全量 **`953 通过／2 跳过／955`（0 失败，26s）**；负载复现入口 **`10 轮 × 16`
   实跑 18s 全绿**（计数未变——第三轮只**加固既有两支夹具**，未新增用例）。
   **第四轮（验证会诊）处置后复验（最新口径）**：全量 **`955 通过／2 跳过／957`（0 失败，33s）**
   ——较 953/2/955 的 **+2** ＝ 本批新增两支夹具（机制级预算夹具＋租约侧「不可读 ⇒ `Corrupt` 而非 `Absent`」
   夹具），**断言未放宽、无测试被移走或排除**；耗时 26s→33s 系**逐点预算耗尽断言**（每点 ≈1.2s×有界预算）
   与租约侧真实共享冲突取证所致（代价已如实登记；负载复现入口 `10×16` 仍全绿）。
   **第五轮（验证会诊）处置后复验（最新口径）**：全量 **`956 通过／2 跳过／958`（0 失败，32s）**
   （**+1** ＝ 租约侧**零副作用**夹具）；租约**写侧语义变更**（重试只包围取锁句柄、持锁后逐点重试）后
   **复跑负载复现入口 `10×16` 仍全绿**。逐轮口径（951→952→953→955→956）为**时间序递增**，
   **以最新一条与本稿 E.10 为权威**。
3. **范围如实**：本结题证据为**本机多线程 + 受控 CPU 负载**（非多进程/实机负载）；
   多进程与实机负载下的稳定性观察仍归 **R5.8 实机段（owner）**。

**D. 状态**：§24.41-C#1（P50 负载敏感诊断套件）由「已移交·未验收」改为
「**已交付（组件/宿主层）**」——可单独重复运行的**负载复现入口** ＋ **根因定位与修复** ＋ **该类夹具稳定
（含 33 节点用例启用后连续 3 轮全量绿）**；`§24.58` 该行因「实机/多进程负载未观察」仍记 **`部分证据`**。
**D′. 最新证据口径（[第六轮会诊处置后]）**：全量 **`956 通过／2 跳过／958`（0 失败，32s）**、负载复现入口
`10×16` 全绿（含租约写侧语义变更后的复跑）；本稿 §24.41-C#1 表格行、交接稿页首/状态表/A 表内的
「951/2/953」「953/2/955」「955/2/957」均为**该步当时口径**（按时间序递增，非并列权威），
**以本条与本稿 E.11 为最新**。**效力边界不变**：P50 结题**不等于**
生产接线开门、**不代表** R5.8 验收单已签署、**不含**真实 User 目录切换授权；多进程/实机负载观察仍归
R5.8 实机段（owner）。

**E. 首轮会诊处置（[批次四十九]，阻断 1／重要 3／建议 1 逐条落地）**

1. **阻断·覆盖不全**：`RunStore` 的文件访问点**全部**纳入争用重试——除已改的读枚举/发布前核对/原子替换外，
   补齐 `Load`、`UpdateMergingIf` 的盘上读取、`Persist` 的**备份 `File.Copy`** 与**临时文件写入**；
   并新增**确定性注入夹具** `RunStoreTests.FileContentionFamily_TransientDeniedRetries_PermanentDeniedSurfacesLoudly`
   （`FileOperationFaultForTest` 按操作标签注入）：发布/读（`read-load`／`read-list`／`read-merging`）/
   持久化（`read-persist-check`／`backup`／`write-tmp`）**瞬时拒绝后成功**；④**预算耗尽后权限故障必须响亮**。
2. **重要·权限语义**（不得以「延迟后原样抛出」掩盖语义变化）：`List()` 与 `UnknownFiles` 对
   **预算耗尽后的 `UnauthorizedAccessException` 改为响亮上抛**（`catch (UnauthorizedAccessException) { throw; }`）——
   不再把它当成「内容损坏」而**静默丢弃/误报坏记录**（那会隐藏活动运行，属双跑/误判风险）；`QueryHandoffLedger`
   仍以既有 `Incomplete`（**不可确认、不得当未命中**）承接并显式登记原因（保守方向）。
3. **重要·入口加固**：①**就绪屏障**（负载线程先报就绪，主线程 `Wait(30s)` 后统一开跑）⇒ 证明首个夹具执行时
   负载已在跑；②`BGI_R5_P50_LOAD_CPUS=0` **直接拒绝**（防「零负载假绿」）；③夹具 **180s 超时后立即停止本轮与
   后续轮次**（超时任务无法强制中止，禁止叠加执行破坏串行前提）；④负载线程异常入队并在收尾**统一断言为空**
   （不再静默吞）。
4. **建议·持锁重试尾延迟**（已采纳为**如实登记的代价**）：`Persist` 在 `_gate` 内最坏 ≈1.2s/步（读 + 替换两步
   同次可叠加 ⇒ ≈2.4s 量级）；**语义上无死锁**、`Action` 泛型包装正确。登记为**已知代价**；
   「耗尽/持锁延迟诊断计数」列为后续可选加固（不阻塞本批结题）。
5. **证据更新**：修复+覆盖补齐后，负载复现入口 **10×16 再次全绿**；全量回归**连续两轮 `952 通过／2 跳过／954`**
   （新增注入夹具 1 支；33 节点用例已启用）。
6. **第二轮会诊处置（阻断 1／重要 3）**：
   ①**探测/枚举/建目录/清理全部纳入**——用「**读取**」取代 `File.Exists` 探测（`Load`／`UpdateMergingIf`／
   `Persist` 的修订核对与备份）：**不存在** ⇒ 合法「无记录」（跳过核对/备份）；**拒绝访问/争用** ⇒ 有界重试后
   **原样抛出**（不再被静默当作「不存在」）；目录枚举改 `EnumerateRunFilesOrEmpty`（目录不存在＝空集，
   拒绝访问＝**响 亮上抛**）；`Directory.CreateDirectory`（runs 与备份目录）纳入重试；临时文件清理改为
   **尽力而为**（残留 `.tmp` 不被权威读取路径消费，且清理异常不得遮蔽主流程结果）。
   ②**争用家族预算耗尽后统一响亮**：`List()`／`UnknownFiles` 的 catch 改为「`JsonException` ⇒ 隔离为坏记录；
   `IOException`／`UnauthorizedAccessException` ⇒ **抛出**」；`QueryHandoffLedger` 对同族以既有 `Incomplete`
   （不可确认、不等于未命中）承接。
   ③**负载入口生命周期**：就绪等待与 `start.Set()` 移入 `try` ⇒ 就绪超时也走 `finally` 取消负载线程（消除泄漏）；
   并把「超时后**停止继续调度**（已超时任务无法强制中止）」的表述**收窄如实**。
   ④**文档一致性**：交接稿「最新全量回归」行与页首/状态表统一为 **952/2/954**；§24.28-D 的 P50 行标注
   「已被 §24.61 取代·历史口径」。
7. **第二轮会诊续处置（收窄争用族 + 诊断计数，实测同步）**：
   ①**「不存在」不是争用**：`FileNotFoundException`／`DirectoryNotFoundException` 虽是 `IOException` 子类，
   但等多久也不会出现 ⇒ 从重试族**排除**（`IsFileContention` 收窄为「`UnauthorizedAccessException` ∪
   （`IOException` 且非两类 NotFound））；新增确定性夹具 `RunStoreTests.IsFileContention_NarrowFamily_ExcludesNotFound`。
   **实测代价与收益**：未收窄前，夹具收尾的后台写在**已删除临时根**上触发 NotFound ⇒ 白烧 80×15ms/次
   （单夹具观测 **237 次重试／3 次耗尽**）⇒ 该类 **152s**、全量 **8m29s**；收窄后该类 **8.3s**、全量回到 **26s**
   （负载复现入口 10×16 仍全绿 ⇒ **真实争用**（拒绝访问/共享冲突）覆盖不受影响）。
   ②**争用诊断计数**（采纳第二轮建议）：`RunStore.ContentionRetryAttempts`／`ContentionExhausted`／
   `LastContentionException` 作为**只读观测**（不参与任何判定），供负载诊断入口与回归排查复用。
   ③证据更新：全量**连续两轮 `953 通过／2 跳过／955`**（新增判定夹具 1 支）。
8. **第三轮会诊处置（阻断 1／重要 2／建议 2，逐条落地）**：
   ①**阻断·`Load` 仍在用 `File.Exists` 探测**：会把「拒绝访问」静默折成「不存在」（＝**误判无记录**）⇒
   与 `UpdateMergingIf`／`Persist` 同口径改走 `TryReadAllTextOrNull(file, "read-load")`：不存在 ⇒ `null`
   （合法「无记录」）；争用族 ⇒ 有界重试后**原样抛出**。
   ②**重要·`QueryHandoffLedger` 的目录枚举争用会逸出**：枚举段包入 `catch (IsFileContention) ⇒` 既有
   **`Incomplete`（不可确认，**不等于**未命中）**，`UnauthorizedAccessException` 不再逸出到对账调用方；
   枚举助手新增 `enumerate` 注入标签（`List`／`UnknownFiles` 仍**响亮上抛**，与权限语义一致）。
   ③**重要·夹具未断言注入点确实命中**（「标签写错/接线未到」会导致**空过假绿**）：注入回调内**逐标签计数**，
   收尾 `AssertTagHits` 逐标签断言（发布恰 3／各读与持久化步 ≥2／耗尽路径 ≥80／枚举 ≥160）；并补
   `read-load` 与 `enumerate` 两个分支，其中**枚举争用耗尽**断言「`List()` 响亮抛出」＋
   「`QueryHandoffLedger` **连续两次**均 `Incomplete`」（幂等不可确认）。
   ④**建议·真实 Windows 形态**（已采纳）：判定夹具补**共享冲突**（`ERROR_SHARING_VIOLATION`＝32 ⇒
   HRESULT `0x80070020`）与**字节区间锁冲突**（`ERROR_LOCK_VIOLATION`＝33 ⇒ `0x80070021`）两支；
   并补**`IOException` 预算耗尽后原样抛出**断言（`Assert.Same` 证明**同一实例**，且盘上仍是上次成功发布的
   载荷 ⇒ 失败**未半写**）。
   ⑤**建议·诊断状态封装**（第二轮已采纳，本轮复核确认）：私有字段＋只读属性（`Interlocked`／`Volatile` 读）。
   ⑥**证据复验**：全量 **`953 通过／2 跳过／955`（0 失败，26s）**；负载复现入口 **`10×16` 实跑 18s 全绿**。
9. **第四轮（验证会诊）处置（阻断 2／重要 3／建议 3，逐条落地）**：
   ①**阻断·`ArbitrationLeaseStore` 仍在用 `Exists` 探测**：`Read()` 的 `Directory.Exists`／
   `File.Exists(_lockPath)` 与 `ReadCore()` 的 `File.Exists(_leasePath)` 在**拒绝访问**时**静默返回 false**
   ⇒ 把「不可读」折成「目录不存在／无锁文件／无正式文件」⇒ 判 **`Absent`**；而 `Absent` 正是 `TryAcquire`
   获取成功的依据（`TryAcquire` 仅在 `Corrupt`/`Unsupported` 时 `Reject`）⇒ **误判「无归属」＝双跑风险**。
   **已修**（与 `RunStore` 同口径）：改为**直接访问 + 异常分类**——锁文件以 `FileMode.Open` 打开
   （**绝不新建**，保持 §6.4「只读不写盘」）；`FileNotFound`／`DirectoryNotFound` ⇒ `Absent`（合法「无正式文件」，
   残件留痕为**诊断性**、失败不改状态）；争用族 ⇒ 有界重试；**预算耗尽 ⇒ `Corrupt`（fail-closed），绝不 `Absent`**。
   ②**阻断·租约正文读取把争用 `IOException` 吞成 `Corrupt`**：`ReadCore` 旧实现的 `catch (IOException)`
   会把**瞬时共享冲突**误报为「文件损坏」（外层重试看不到异常 ⇒ 无法重试）。**已修**：正文读取纳入
   **同预算同口径**有界重试；预算耗尽按**明确 fail-closed 合同**归 `Corrupt`（调用方一律拒绝变更/接管、
   原件保留留痕、**零改写**），`UnauthorizedAccessException` 形态同样如此。
   ③**重要·`WithLock` 的 `Directory.CreateDirectory(_configDir)` 在重试边界之外**：已纳入同一有界重试。
   ④**重要·夹具证据缺口**：**已补**——(a) 新增注入标签 `create-runs-dir`／`create-backup-dir`／`cleanup-tmp`
   且**全部标签都计数**（否则跨标签断言会空过：实测首版「备份确实执行」断言即因只计装机标签而**假红**，
   已改为全标签计数）；(b) 预算断言改为**逐点精确**（`before + 80`，**恰好**烧完一轮），并覆盖
   `read-load`／`read-merging`／`read-persist-check`／`backup`／`write-tmp`／`publish`／`read-list`／`read-unknown`；
   (c) 补两条**定向反证**——陈旧修订仍抛 `RunRecordConflictException`（⇒ 盘上读取**未被吞成「无记录」**）、
   备份文件**确实新增**（⇒ 未被吞成「跳过备份」）；(d) 新增**机制级**夹具
   `WithContentionRetry_BudgetEatsExactlyAttempts_NonContentionNeverRetried`（瞬时×N ⇒ 调用 N+1；耗尽 ⇒
   **恰好 attempts 次**且**同一实例**；非争用异常**一次也不重试**）。
   ⑤**重要·`QueryHandoffLedger` 调用方合同**：**已审计**（无改动）——生产唯一调用方
   `TaskCenterHost.LedgerGate` 对 `Incomplete` **既有显式分支**：拒绝新受理（`LedgerIncomplete`，文案
   「读不到 ≠ 未受理」）；既有夹具 `LedgerIncomplete_Rejected_NewIntentBlocked` 覆盖 ⇒ 枚举/单文件争用归
   `Incomplete` **不改变调用方语义**（`Miss` 才放行；`Incomplete` 不放行 ⇒ 方向**更保守**）。
   ⑥**建议·清理语义**（采纳为如实声明）：`RunStore.Persist`／`ArbitrationLeaseStore.Publish` 的**发布后**
   残件清理为**尽力而为**——本节「预算耗尽一律响亮」的**唯一例外**（残件不进入权威读取路径；租约侧残留 `.tmp`
   另有 `UncertainResidue` 约束＋`QuarantineResidues` 留痕 ⇒ 保守方向、不构成「静默放行」）。
   ⑦**建议·测试接缝**（复核确认）：`RunStore.FileOperationFaultForTest`／`PublishFaultForTest`、租约侧无接缝；
   生产路径**恒 `null`**（`internal` 可写性属既有接缝形态，未见生产赋值）。
   ⑧**证据**：全量 **`955 通过／2 跳过／957`（0 失败，33s）**；负载复现入口 **`10×16` 仍全绿**。
   **范围如实**：租约侧新夹具为**真实 Windows 共享冲突**（测试持 `FileShare.None`）取证；ACL 型
   `UnauthorizedAccessException` 由判定/注入路径覆盖，**未**在本机做真实 ACL 变更取证。
10. **第五轮（验证会诊）处置（阻断 1／重要 3／建议 3，逐条落地）**：
   ①**阻断·残件探测失败被折成「无残件」（fail-open）**：`ReadAbsentWithResidueProbe` 原先在枚举抛
   `IOException`／`UnauthorizedAccessException` 时清空明细并以 `UncertainResidue=false` 返回；而 `TryAcquire`
   **只在 `UncertainResidue=true` 时**拒 `residue_uncertain` ⇒ **未能排除崩窗残件却仍可获取新租约**
   （绕过 `QuarantineResidues`，未决动作可能被忽略）。**已修**：不可枚举 ⇒ **按存在未决残件保守处理**
   （`UncertainResidue=true` ＋ 明细「残件目录不可枚举」），须隔离/对账后方可获取（fail-closed）。
   ②**重要·「无锁文件 ⇒ 无写者」窗口**：`Open` 抛 `FileNotFound` 后，首个写者可能已建锁并发布 ⇒ 无锁快照
   可能陈旧。**已修**：无锁快照读后**复核锁文件是否已出现**（复核**不得**用 `Exists`：以 `Open` 成败分类），
   已出现 ⇒ **丢弃快照**改走锁内读取（宁可重读，不返回可能陈旧的快照）。
   ③**重要·写侧重试包围整段事务**：原 `WithLockContentionRetry` 包住「读取→业务回调→发布」⇒ 发布/枚举抛
   争用时会让 `mutate` 回调、身份生成、残件迁移**重复执行**（本批把 `UnauthorizedAccessException` 纳入争用族
   又扩大了该范围）。**已修**：有界重试**只包围取锁句柄**；持锁后**逐点**重试
   （`ReadCore`／`Publish` 的 `WriteAllBytes`＋`Move`／`QuarantineResidues` 的枚举＋建目录＋逐件迁移）
   ⇒ **业务回调不再因争用被重复调用**；预算耗尽仍**原样抛出**（响亮失败，不静默）。
   ④**重要·宿主把 `Corrupt` 当「确无映射」静默通过**：节点终局扫描与终局回写两处，`File=null` ⇒ 空集合
   ⇒ 静默放弃本轮（不错误放行，但会**无痕延迟**收口）。**已修**：显式分支——扫描处**留诊断并放弃本轮**
   （`TryLog`）；回写处按「**尚未收敛**」在有界 spin 内重试。两者均严格保守且**留痕**。
   ⑤**建议·租约夹具证据**（已采纳）：新增**零副作用**夹具 `Read_MissingDirectoryOrLock_CreatesNothing`
   （目录/锁文件都不得被新建 ⇒ 新 `FileMode.Open` 分类的回归守卫）；共享冲突夹具增**耗时下界**弱证据
   （≥800ms ⇒ 确有有界重试，而非「直接当失败」）。
   ⑥**建议·夹具脆弱性**（已采纳）：`RunStore` 备份反证不再依赖「文件总数 +1」（隐含「一修订一文件」实现
   细节），改断言「该修订号备份文件存在 ＋ **内容＝发布前盘上修订**」（按记录反序列化比对，避开序列化器
   对非 ASCII 的转义）。
   ⑦**建议·接缝**（如实登记）：租约侧**无**注入接缝 ⇒ 其「预算耗尽」证据为**耗时下界＋状态断言**（弱于
   `RunStore` 侧逐点精确预算）；如需强证据须新增**可计数接缝**（登记为**可选加固**，不阻塞本批结题）。
   ⑧**证据**：全量 **`956 通过／2 跳过／958`（0 失败，32s）**；负载复现入口 **`10×16` 在租约写侧语义变更后
   复跑仍全绿**。**效力边界不变**：本批**仍不代表**生产接线开门／R5.8 签署／真实 User 目录切换授权。
11. **第六轮（验证会诊）处置（重要 1／建议 2，逐条落地）**：
   ①**重要·终局回写的**嵌套预算放大**（可用性回归）**：终局回写循环按「次」计（500 次），而 `Read()` 在
   持续争用下自身最坏消耗 ≈1.2s 预算 ⇒ 最坏 `500×1.2s ≈ 10 分钟`，与注释暗示的「约 10 秒有界窗口」不符。
   **已修**：循环外加**墙钟预算**（总 >15s 即 `break`），并在墙钟耗尽时走既有「放弃本轮回写」分支
   （**留日志**：受理管线未在有界窗口内收敛，保守留待对账）；`Corrupt` 分支的 `continue` 现受该墙钟约束。
   ②**建议·注释与实践漂移**（已采纳）：`WithLockContentionRetry` 的摘要原称「整段『读取→判定→更新→发布』
   重试安全」——该表述在第五轮把重试边界收窄到「取锁句柄」后**已作废**；已改为显式警示（**勿**把事务重新
   包回重试边界，否则业务回调/身份生成/残件迁移会被重复执行）。
   ③**建议·租约侧预算证据强度**（如实登记，无改动）：共享冲突夹具的 `≥800ms` 耗时下界只证「发生过明显等待」，
   非「恰好 80 次」；已在 E.10⑦ 与本条如实声明，精确证据需**新增可计数接缝**（登记为可选加固）。
   ④**证据**：全量 **`956 通过／2 跳过／958`（0 失败，32s）**——本轮只改**宿主回写的墙钟约束**与注释，
   **未新增/未移除**夹具，计数不变；断言未放宽。
   ⑤**建议·诊断口径细分**（**书面不采纳·登记为可选加固**）：第七轮建议把「墙钟/次数耗尽」与「受理管线未收敛」
   区分为不同日志原因（如 `settle_budget_exhausted:last_read=Corrupt`）。**行为正确性不受影响**（两者都走
   「保守放弃本轮＋留日志」同一分支，且后续触发/恢复扫描会再对账）；采纳将**改动已通过验证的宿主分支**
   而需再起一轮验证会诊，性价比低于其收益 ⇒ 本轮**书面不采纳**并登记为**可选加固**（**事后可剔除入口**：
   后续批次若因其他原因再改 `TaskCenterHost.Admission`，一并采纳本项）。**第七轮（验证会诊）结论：
   无必改项（阻断 0／重要 0／建议 1）**。

### 24.62 落地登记：**P8 通用失败分类细化**——「网络前可证实未发送 vs 已进入线路后失败」判据接缝（[新增·2026-09-22 批次五十]）

**A. 落地事实（判据接缝＝**证据载体**；只认证据，不认消息文本/异常类型泛化）**

| 项 | 实现／证据 | 条款 |
|---|---|---|
| **证据载体** `BgiNotSentException`（`internal sealed class`，继承 `InvalidOperationException`；证据码**枚举全表**＝`channel_not_ready`／`pipe_not_connected`／`local_request_rejected`） | 新增 `Services/TaskCenter/BgiSendEvidence.cs`；`IsKnownEvidenceCode`／`AllEvidenceCodes` 供「新增证据码必须登记」守卫 | §17.4-A ⑥（安全敏感面须交枚举） |
| **生产抛点**（本进程在**任何字节写入线路之前**失败的事实） | `BgiExternalClient.SendCommandAsync`：①通道未 `Ready`／无管道 ⇒ `channel_not_ready`／`pipe_not_connected`（原 `InvalidOperationException` **文案逐字保留**）；②重复请求 ID（写入前本地拒绝）⇒ `local_request_rejected`。**继承既有异常类型** ⇒ 既有 `catch (InvalidOperationException)` 降级路径**逐字不变** | §3.2a（无损拒绝类）／兼容纪律 |
| **判据纯函数** `BgiWorkflowExecutionBoundary.IsProvenNotSent`（＋`NotSentEvidence`） | **只认证据载体**：`IOException`／`TimeoutException`／普通 `InvalidOperationException`／`OperationCanceledException` 一律 false | P8 核心判据 |
| **边界分类**（`SendPreparedAsync` 异常支） | **先对账**（命中即证明证据有误 ⇒ 按命中回执，绝不硬判未发送）→ 未命中再分类：证据载体 ⇒ `RejectedWithRetryWindow`（**确定拒绝 ＋ 重试窗口**）；其余 ⇒ `UnknownWith`（**保留未决责任**，现状不变） | §12.3 M3（阶段边界） |
| **结果维扩展** `BoundarySubmitResult.Retryable`（默认 `false`；新静态 `RejectedWithRetryWindow` 置 true） | 仅「确定拒绝＋开重试窗口」路径置 true ⇒ 既有终局确定拒绝语义**不受影响** | §3.2a |
| **宿主映射（发送方向）** | `DispatchSuccessorViaHostAsync`：`SendOutcome.Rejected(..., retryable: sent.Retryable, ...)` ⇒ 门面按 `RetryableRejected` 落盘（责任 `Settled`、`Submission` 关闭、重试窗口派生、`LastSendSeq` 不变） | §3.3／§24.45 |
| **宿主映射（结果方向·[会诊重要项处置]）** | `MapAdmissionResultToBoundary`：`RetryableRejected` **单独分流**为 `RejectedWithRetryWindow`（保留可重试性）；`TerminalRejected`／`NotSelected`／`F11Blocked`／`NeedPreemptConfirm` 保持**终局**（`Retryable=false`） | §3.3 |
| **夹具 ＋16**（§17.4-A ② 反例先行） | ①判据 Theory 7 支（三证据码 true；**同文案普通异常**／`IOException`／`TimeoutException`／OCE false）；②证据码**枚举＋构造期拒绝未知码/null/大小写变形**＋**判据层白名单独立取证**（测试接缝绕过构造期校验后仍不得判「可证实未发送」）＋**父类型兼容** 1 支；③边界 Theory 3 支（三证据码 ⇒ `Rejected`＋`Retryable`＋**对账仍执行**＋发送恰一次）；④**反例** Theory 2 支（`IOException`／`TimeoutException` ⇒ `Uncertain`＋`Retryable=false`）；⑤**真实传输入口守卫** 1 支（未 `Start`／`State=Down`／无管道 ⇒ 证据码 `channel_not_ready`；**不写任何全局静态接缝** ⇒ 与并行测试无污染；**范围如实**：只覆盖入口守卫，`pipe_not_connected` 分支由判据层覆盖，真实管道断开取证见 F.3）；⑥**结果方向回映射** 1 支（`RetryableRejected` ⇒ `Retryable=true`；四个终局 Kind ⇒ `Retryable=false`）；⑦**宿主级端到端对照** 1 支 `NodeSubmit_ProvenNotSent_RejectedWithRetryWindow_NotUnknown`（运行 `Failed`／节点 `rejected`／操作 `RetryableRejected`＋重试窗口已派生＋`LastSendSeq==1`／`Submission` **已关闭**／运行记录无受理事实／发送**恰一次**），与既有 `NodeSubmit_SendStageFailure_StaysReconcilingNoResend`（`IOException` ⇒ 运行 `Unknown`＋未决 `Submission`）构成**对照对** | P8／§12.3 M3 |

**B. 范围如实（本批**未**覆盖，禁悬空）**

1. **E3/E4/E5 外部启动路径**（`CommandExecutor` 队列/v2 适配层）：「通道不可用＝未发送」在该路径已有**自有**形态
   （`QueueStartEarlyKind.ChannelUnavailable` ⇒ 回退既有路径、不落回已提交结论），**未**改用本证据载体 ⇒
   该路径上的分类端点归 **R5.8 实现闭合序列（外部启用门）**；§24.41-C#5 因此记「**部分已交付·未验收**」。
2. **本地校验类确定拒绝**（F11／身份/游标/契约预检）保持 `retryable:false`（**终局**）：本批只对**传输层可证实未发送**
   开重试窗口——§3.2a 的「只有无损拒绝类**才**开重试窗口」是**必要条件**，不要求所有无损拒绝都可重试
   （本地校验类重试不可修复，开窗口只会白跑）。
3. **未改序列化框架**：证据码是**进程内事实**，不进任何落盘/线路字段（锚点 6 不破）。
4. **生产外部启动接线仍关闭**（`_successorAdmissionWired` 生产构造恒不传；E3/E4/E5 组合根注入未开）；
   本批**不代表**生产开门。

**C. 状态**：§24.41-C#5（P8）由「已移交·未验收」改为「**部分已交付·未验收（组件/宿主层）**」；
§24.58 对应行随之更新为「部分证据」。

**D. 回归**：全量 **971 通过／2 跳过／973（0 失败，33s）**（＋15 夹具；断言未放宽、无测试被移走；
基线 552 未降；BGI 侧既有 14 例失败指纹不变，与本批无关）。**生产接线仍关闭**。

**E. 本批会诊（gpt-5.6-sol／medium；第 1 轮 0 阻断／4 重要／4 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 53 | 重要 | `MapAdmissionResultToBoundary` 把 `RetryableRejected` 与终局拒绝**一起**映射 ⇒ 回映射**丢失可重试性**（发送方向正确、结果方向断链） | **已修**：`RetryableRejected` **单独分流**为 `RejectedWithRetryWindow`；新增结果方向回映射夹具（`RetryableRejected` ⇒ true；四个终局 Kind ⇒ false） |
| 54 | 重要 | 证据码白名单**不参与判定**：构造函数不校验、判据只看异常类型 ⇒ 任意码（含「写后失败」误用）都会打开重试窗口 | **已修·双层防线**：①构造期**拒绝未登记码**（fail-fast）；②`IsProvenNotSent` **同时要求 `IsKnownEvidenceCode`**；夹具补「未登记码构造即拒」反例；`AllEvidenceCodes` 改**只读包装**（建议项一并处置） |
| 55 | 重要 | 「可重试窗口」与运行记录之间存在**持久化中间矛盾**：`PrepareSubmit` 已落 `SendAttempted=true`/`Intent=Submitted`，而门面侧已是 `RetryableRejected`/`Settled` ⇒ 两次发布之间崩溃会出现两侧不一致；且 Runner 未据 `Retryable` 签发新提交身份 | **已登记残项**（本批不改判定；见 **F.1／F.2**）：性质＝**过度保守（不双跑）**——`Intent=Submitted` 会让重试**被保守挡住**，不会导致远端重复执行；本稿与交接稿**不再**声称「重试链已完整可用」 |
| 56 | 重要 | 「真实传输未连接」夹具实为**入口守卫**（未 `Start`、`State=Down`、`_pipe=null`），**未**触达管道、也**未**覆盖 `pipe_not_connected`／序列化／写锁／写后超时等阶段 | **已修（措辞如实＋登记）**：夹具与文档改称「**真实传输入口守卫**」；`pipe_not_connected` 由判据层 Theory 覆盖；**真实管道断开与各阶段分类的真实传输取证**登记 **F.3**（R5.8 实机/真实入口层） |
| 57 | 建议 | `AllEvidenceCodes` 为数组（引用只读、内容可改） | **已采纳**：改 `IReadOnlyList<string>` ＋ `Array.AsReadOnly` |
| 58 | 建议 | §24.62 证据锚点写成 `SubmitSuccessorSender`，实际成员为 `DispatchSuccessorViaHostAsync` | **已采纳**：锚点更正 |
| 59 | 建议 | 交接稿标题「截至批次四十九」与正文（已含批次五十）不一致 | **已采纳**：标题同步为「截至批次五十」 |
| 60 | 建议 | 既有精确类型断言（`Assert.Throws<InvalidOperationException>`）可能因父类型不变而**不**回归，但材料内无法全局核对 | **已核（机械证据）**：本批改动后**助手侧全量 972/2/974 绿**（0 失败）⇒ 仓内不存在会被本改动打断的精确类型断言；后续新增断言仍须按此复核 |

**F. 残项登记（§17.4-A ⑤：冻结后新发现先登记，由 owner 三选一；**不自动扩批次**）**

1. **门面结算 ↔ 运行记录之间的崩溃窗**（发现 55）：拒结事实（门面：`RetryableRejected`／`Settled`／`Submission` 关闭）
   与运行记录（`SendAttempted=true`／`Intent=Submitted`）在**两次发布之间**崩溃时不一致。
   **性质**：**过度保守、不双跑**（恢复路径按未决处置）。**完成证据要求**：崩溃窗夹具（两发布之间中断）×
   「恢复后不重发且不误判成功」；**承接**＝R5.8 实现闭合序列（施工方：恢复项负责人）；**门禁**＝节点改道门保留。
2. **Runner 未据 `Retryable` 签发新提交身份**（发现 55）：重试窗口已在**门面侧**成立并持久化，
   但 Runner 的 `attempt` 仍固定为 1、未驱动 `RetryAsync` ⇒ **重试链未闭环**（窗口只能由显式恢复/后续批次消费）。
   **完成证据要求**：`RetryableRejected` ⇒ 窗口内经 `RetryAsync` **新许可**重驱动的端到端夹具（含 `attempt` 推进与
   新发送身份）；**承接**＝R5.8 实现闭合序列；**门禁**＝节点改道门保留。
3. **真实传输阶段分类取证**（发现 56）：`pipe_not_connected`／写锁等待取消／payload 序列化失败／
   `WriteAsync`·`FlushAsync` 抛错／写后超时 的**真实传输**分类（当前代码审查结论：写/刷异常与写后超时仍为
   `Unknown`＝保守方向）。**完成证据要求**：真实管道断开（如进程内管道替身停止服务）夹具或实机证据；
   **承接**＝R5.8 真实入口层／实机段；**门禁**＝外部启用门。

**G. 处置后回归（最新）**：全量 **972 通过／2 跳过／974（0 失败）**（＋1 夹具＝结果方向回映射；
「构造期拒绝未知码」断言并入既有枚举夹具，不新增用例；断言未放宽、无测试被移走）。**生产接线仍关闭**。

**H. 第 2 轮（验证会诊：0 阻断／2 重要／2 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 61 | 重要 | **第二层白名单校验无独立回归证据**：未登记码在构造期即被拒 ⇒ 即使把 `IsProvenNotSent` 退化为「只看异常类型」，现有断言仍全绿 | **已修**：新增**仅测试接缝** `BgiNotSentException.CreateBypassingWhitelistForTest`（`RuntimeHelpers.GetUninitializedObject` ＋反射写只读属性后备字段，生产路径不可达并在注释中写明），夹具断言「绕过构造期校验后，未登记码/`null`/大小写变形 ⇒ **false**」＋**正向对照**（同一接缝用已登记码 ⇒ true，排除「对象未被构造」导致的假绿）；并补构造期 `null`／大小写变形断言 |
| 62 | 重要 | 真实传输夹具**改写全局静态** `PipeNameOverrideForTest`，而该类未禁并行 ⇒ 并行构造的客户端可能继承随机假管道名（非确定性） | **已修**：夹具**不再触碰全局接缝**——`BgiExternalClient` 仅在 `Start/StartAsync` 后连接，本夹具**不启动**客户端 ⇒ `State=Down` 恒成立、确定性且零全局副作用（比「加禁并行 collection」更彻底） |
| 63 | 建议 | §24.62 夹具数量登记为「＋17」，实际 xUnit 用例为 **16** | **已采纳**：改为「＋16」并逐项列明（7＋1＋3＋2＋1＋1＋1） |
| 64 | 建议 | §24.62 残留两处失真：①边界分类表仍写 `RejectedNotSent`（现名 `RejectedWithRetryWindow`）；②发现 60 仍写「973/2/975」 | **已采纳**：两处统一为现行符号与 **972/2/974** 口径 |

**I. 第 3 轮（验证会诊：0 阻断／2 重要／1 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 65 | 重要 | `NotSentEvidence` 与自身合同不一致：对「绕过构造期校验的未登记码」仍返回非空证据码（而 `IsProvenNotSent` 已判 false）⇒ 后续调用方可能把非空码误当证明 | **已修**：`NotSentEvidence` 改为**与判据同口径**（仅 `IsProvenNotSent(ex)` 成立时返回证据码，否则 `null`）；夹具补「未登记码 ⇒ `NotSentEvidence == null`」断言＋已登记码正向对照 |
| 66 | 重要 | §24.62 之外仍有「15 支夹具」残留（§24.28-D 表行、§24.41-C#5 表行）⇒ 第 2 轮建议 63 未完整处置 | **已修**：两处统一为 **16**；`D. 回归` 行的「＋15／971/2/973」为**该步当时快照**，由本条与 G/H 的 972/2/974 口径覆盖（不构成并列权威） |
| 67 | 建议 | 测试接缝 `CreateBypassingWhitelistForTest` 的可见性/可达性与「正向对照不假绿」经核验成立；未初始化异常对象在当前用途不读取 `Message`，无实际问题 | **已核**（无改动）：接缝仅 `internal`＋夹具调用点；正向对照已断言「已登记码 ⇒ true」 |

**J. 第 5 轮（验证会诊）结论与建议处置**

- **结论：无必改项（阻断 0／重要 0／建议 1）**——交接稿并列权威冲突已消除（`+15／971/2/973` 明确为**中间快照**，
  唯一最新口径＝**16 支夹具／972/2/974**）；`NotSentEvidence` 各分支均有直接断言；A–I 自洽；无新增悬空项。
- **建议处置（第 68 项·书面不采纳·登记为可选加固）**：建议把判据 Theory 中「三已登记码」的断言由
  `NotSentEvidence(ex) is not null` 收紧为 `Assert.Equal(evidenceCode, NotSentEvidence(ex))`。
  **理由**：安全边界（三码判定与「未登记码/null/大小写变形 ⇒ 两者皆假」）**已被直接断言覆盖**；收紧为精确相等属
  **表达强化**，采纳将改动**已通过验证**的夹具而需再起一轮验证会诊，性价比低于收益 ⇒ 本轮**书面不采纳**并登记为
  **可选加固**（**事后可剔除入口**：后续若因其它原因再动该夹具，一并采纳本项）。

### 24.63 落地登记：§24.41-C#11 余项「S5 **间接形态** 粗化 ＋ `OpCode` 字面量（含只读）登记」（[新增·2026-09-22 批次五十一]）

**A. 本批落地事实（测试-only；未改生产行为；**全仓实测**）**

| 形态 | 实测现状 | 守卫语义 |
|---|---|---|
| ①**反射按名取方法**（`GetMethod`／`GetRuntimeMethod` 的**普通/逐字字符串或 `nameof`**（含**裸名与限定名**）＋**空白排版** `GetMethod (`） | **0 处** | **零容忍**：出现即失败（要求先登记 §24.41-C#11 并评估可否改回直发） |
| ②**方法组／别名使用**（`.SendCommandAsync` **后不跟 `(`**（空白容错，防把「带空格调用」误报）＋**无点前缀**的 `SendCommandAsync` 使用：`Register(SendCommandAsync)`／`= SendCommandAsync;`） | **0 处** | 同①（[会诊 1／2 轮处置] 上一版只覆盖 `= 对象.SendCommandAsync;`，后又漏无点前缀与空白回溯误报） |
| ③**拼接·插值构造操作名**（`"ext." + …`／`$"task.{…}"`／`string`·`String` ＋**空白容错**的 `.Concat("config."…)`） | **0 处** | 同①（防「换新拼写绕过操作名词表登记」） |
| ④**全量 `OpCode` 字符串字面量（含**只读**操作）** | **3 文件 17 处**，登记为**直方图（文件→值→次数）**：`CommandExecutor.cs` （`task.start×6`／其余 7 值各 ×1）、`IpcClient.cs`（`ping×1`）、`MainViewModel.cs`（`config.list×1`／`task.status×2`） | **计数守恒式替换亦失败**（[会诊阻断项] 上一版「计数＋去重集合」无法发现「一处 `task.start` 改成 `config.list`」）；正则放宽为**任意内容**字符串字面量 ⇒ 非白名单字符集的新值同样被捕获 |
| ⑤**`OpCode = <表达式>` 赋值**（比较/常量/参数传入） | **4 文件各 1 处**（`v2OpCode`／`opCode`／`v2OpCode`／`operation`） | **单独登记**（[会诊重要项] 上一版误称其由「非字面量直发」守卫覆盖——该守卫只覆盖 `SendCommandAsync(<标识符>, …)`） |
| ⑥**`OpCode =` 赋值总量守恒（[第 6／9／10 轮] 真·单遍 ＋ 跨行保守）** | 每文件 `all == 字面量支 ＋ 表达式支`（实测**全部为 0 差**）；扫描＝**一条正则一次遍历**（`OpCodeAssignmentPattern`：**等号前允许跨行**、**等号后只允许空格/制表符** ⇒ 跨行给值不被捕获为已分类；`rhs` 可空 ⇒ 未识别形态亦计入总量） | [会诊第 2 轮阻断项／第 6·9·10 轮阻断·重要项] **兜住两支都未识别的形态**（`(x)`／`$"…"`／`(string)x`／`operation + suffix`／`operation()`／`new X()`／**等号后跨行 RHS**）**且等号前跨行不得漏扫**（漏扫＝总量 0＝假绿）⇒ **漏报即失败** |
| ⑦**反射按「名称」筛选取方法**（`GetMethods()` ＋ `.Name == "…"`／`.Name.Equals("…")`；`GetMethod(s)("sendcommandasync", …)` **忽略大小写**） | **0 处** | [会诊第 3 轮重要项] 与①同为零容忍（`GetMethods()`＋按名筛选是反射的另一种常见形态） |

**B. 夹具**：`SubmissionPointInventoryTests.ProductionIndirectSendForms_AreAbsent`（**四类零容忍**：反射按名／反射按名称筛选／方法组·别名／拼接构造）＋
`ProductionOpCodeLiterals_MatchRegisteredHistogram`（**直方图＋文件集合守恒**＋`OpCode = <表达式>` 单独登记）＋
**逐支正反例** `IndirectSendFormPatterns_HaveExpectedSamples`（**43 支**＝反射按名 8＋方法组 15＋按名称筛选 15＋拼接 5）／
`OpCodePatterns_CaptureLiteralsAndExpressions`（6 支）／`OpCodeConservation_RejectsUnclassifiedForms`（**23 支**）／
`OpCodeScan_CountsLegalTokenSeparators_Exactly`（**22 支**，**精确计数**：等号前跨行/注释/制表符间隔必须 `Total==1`；
含**左边界**正例 `x.OpCode`／`@OpCode`／`this.OpCode`／`global::X.OpCode` 与**逐类别后缀反例**（ASCII／`\p{L}`／`Nd`／`Nl`／`Mn`／`Mc`／`Pc`／`Cf`）
——**全部 Theory 用例 94 支**（＋2 结构守卫 `[Fact]` ⇒ 本批新增 **96** 个测试）；[会诊第 1／3 轮阻断·重要项] 防「正则被改成
永不匹配 ⇒ 生产 0 命中 ⇒ 假绿」与「未识别形态静默通过」（守恒判据自测）。

**C. 能力边界与根本限制（如实·不得夸大；§17.4-A ⑥）**

1. **五层守卫都是文本匹配**（非语义/非 IL 识别），**精确划定**：
   - **已覆盖**：`SendCommandAsync` 首参数为**简单标识符/成员表达式**（§24.53 登记 10 文件）；`OpCode` 的**字符串字面量**
     （字符串**边界感知**：普通/逐字字符串内的 `,`／`;`／`}` 不截断；**含转义引号**的普通/逐字字面量亦可分类）
     与**简单标识符表达式**；上述**四类**间接形态；`OpCode`／操作名的**完整字面量**。
   - **已覆盖的 token 间隔**（[第 11 轮]）：`OpCode` 与 `=` 之间的**跨行空白／块注释／行注释**均被扫描
     （`OpCode(?:[\s]|/\*…\*/|//…)*=`）；**精确计数**由 `OpCodeScan_CountsLegalTokenSeparators_Exactly` 锁定
     （`Total==1`），避免「总量 0 ⇒ 0==0」假绿。
     **未覆盖（保守失败，非假绿）**：`OpCode =` **后换行给值**（等号后跨行 RHS）⇒ 未分类 ⇒ 守恒失败；
     **未闭合**字符串（`OpCode = "abc`）⇒ 同样保守失败。二者均**只会拒绝**，不会静默放行。
   - **未覆盖（静默漏扫·如实登记·[第 11／14 轮会诊重要项]）**：①`OpCode` 与 `=` 之间插入**预处理指令**（`#if` 等）；
     ②**序列化别名形态**（给既有属性加 `[JsonPropertyName("OpCode")]` 后经另一名称赋值／构造器参数／字典键
     生成同名线路字段）；③**Unicode 转义拼写的标识符**（`\u004F\u0070Code = "y"` 词法上即 `OpCode`，原始文本中
     无连续 `OpCode` ⇒ 总量 0）。三者**不会被本守卫发现** ⇒ 需评审关注（或 Roslyn 化之后一并覆盖）。
   - **未覆盖（登记为可选加固）**：**前缀变量化**（`$"{prefix}.task.start"`）、复杂表达式/别名链、
     IL 级间接调用（`MethodInfo.Invoke` 经变量、`dynamic`、`Delegate.CreateDelegate`、`Reflection.Emit`）
     ⇒ 需 IL 分析工具，**不构成本行「未交付」部分**（本行完成证据要求＝已登记的形态面）。
   - **原始文本扫描面（误报）**：不剥注释/字符串 ⇒ 生产注释里写出上述被禁写法会**误报并失败**（保守方向，
     改回正常注释即可）；长期若要降低误报应改用 Roslyn 语法树（登记为可选加固）。
   - **「自改自证」缺口（如实登记）**：登记表与生产源码同在仓库内 ⇒ 守卫只强制「生产文本与同文件快照一致」，
     **不证明**快照变更已获 §24.41-C#11 的**独立批准**（后者依赖代码评审对 §24.63 的核验）。
   - **直方图粒度的固有边界（如实登记）**：[会诊第 2 轮建议] 登记为「**文件→值→次数**」，**非位置级**——
     **同文件内成对互换**（一处 `task.start→config.list` ＋ 另一处 `config.list→task.start`）直方图不变，
     本守卫**不能发现**（需位置级/语法树比对 ⇒ 归入 Roslyn 化可选加固）。
   - **`reflection-name-filter` 的保守误报面（如实登记）**：[会诊第 4 轮建议] 该支使用**忽略大小写**匹配 ⇒
     任意对象上的 `.Name == "sendcommandasync"`、以及**未带** `BindingFlags.IgnoreCase` 的
     `GetMethod("sendcommandasync", …)` 都会命中（保守方向：误报而非漏报）；且不强制 `.Name` 前必须出现
     `GetMethods()`。**显式点名的剩余反射入口（未覆盖，归可选加固）**：`GetMethod(methodName, …)` 的**名称变量化**、
     `string.Equals(x.Name, "SendCommandAsync", …)`、`GetMember`／`GetProperty` 取得委托或 `MethodInfo` 的链路。
2. **「守卫不证明授权先于发送」是根本限制**：结构守卫只证明**直发面未扩大**；**授权先于发送**由**运行期证据**
   承担——F11 独立闸门零发送（§24.42）、发送前 F11 复核（§24.54）、执行占用零发送适用范围（§24.41-C#18）、
   门面「锁内占位先于发送」（§24.18-2）等既有夹具。二者**不得互相代替**。
3. **计数口径澄清**：§24.46 的「9 种源码拼写」＝**6 个操作类别**（v2 `task.start`／`task.stop`／
   `action.execute_hotkey` ＋ ext `TaskStart`／`TaskStop`／`TaskCancel`）**＋ 3 个 ext 线协议字面量**
   （`"ext.task.start"`／`"ext.task.stop"`／`"ext.task.cancel"`）；§24.63-D 的「字面量 6 模式」指**操作类别**。

**D. 状态**：§24.41-C#11 由「部分已交付·未验收」改为「**已交付（组件/宿主层）**」——**五层登记齐备**
（字面量 6 操作类别／9 种源码拼写；非字面量 10 文件；**间接形态 4 类零容忍**（反射按名／反射按名称筛选／方法组·别名／拼接构造）；`OpCode` 字面量直方图含只读 3 文件 17 处；
`OpCode = <表达式>` 4 文件）；**保留门禁**＝外部启用门；**残余**（IL 级间接分析、Roslyn 化降误报）为**可选加固**。

**E. 回归**：全量 **1068 通过／2 跳过／1070（0 失败）**（本批 ＋96 个测试＝2 结构守卫 `[Fact]`＋**43** 间接形态正反例
（反射按名 8／方法组 15／按名称筛选 15／拼接 5）＋6 `OpCode` 两分支正反例＋23 守恒正反例＋22 精确计数正反例；含十五轮会诊处置强化；断言未放宽、无测试被移走；
基线 552 未降）。
**生产接线仍关闭**。

**F. 本批会诊（gpt-5.6-sol／medium；第 1 轮 4 阻断／7 重要／3 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 69 | 阻断 | `OpCode` 只比较「计数＋去重集合」⇒ **计数守恒式替换**（一处 `task.start` 改 `config.list`）可通过 | **已修**：登记改为**文件→值→次数直方图**（`task.start×6` 等逐一断言） |
| 70 | 阻断 | 正则只认白名单字符集 ⇒ `OpCode = "Task.Start"`／`@"…"` 等**新写法完全不可见** | **已修**：正则放宽为**任意内容**字符串字面量（含逐字字符串），新值一并被捕获并因未登记失败 |
| 71 | 阻断 | 三类零容忍**无正反例** ⇒ 正则被改成永不匹配时**空洞通过** | **已修**：新增 15 支正反例 Theory（含「完整字面量」与「前缀变量化边界」两类反向样例） |
| 72 | 阻断 | 「反射按名」漏掉**限定成员** `nameof(IpcClient.SendCommandAsync)`（属文档已声称覆盖的形态） | **已修**：限定名可选（裸名与限定名都命中）＋正反例 |
| 73 | 重要 | 「方法组／别名」只覆盖一种赋值语法，类别名与「已交付」结论过宽 | **已修**：改为**广义口径**——任何 `.SendCommandAsync` 后**不跟 `(`** 的用法（赋值/实参/强转/括号包裹），实测仍 0 处 |
| 74 | 重要 | 拼接支漏 `string . Concat`（空白排版差异）且漏报范围未精确登记 | **已修**：`string|String` ＋空白容错；其它漏报（前缀变量化等）已在 C.1 **逐条**登记 |
| 75 | 重要 | 「`OpCode = <表达式>` 由非字面量直发守卫承接」**不成立**（该守卫只覆盖首参数形态） | **已修**：新增**单独登记**（4 文件各 1 处：`v2OpCode`／`opCode`／`v2OpCode`／`operation`）＋计数断言＋正反例 |
| 76 | 重要 | 「禁止只改数字消警」只是提示、无机械约束（**自改自证**） | **已登记**（C.1 第四条）：如实声明守卫只强制「生产文本与同文件快照一致」，**不证明**快照变更已获独立批准（依赖评审） |
| 77 | 重要 | 原始文本扫描**不剥注释/字符串** ⇒ 生产注释写出被禁写法会误报（文档只写「文本匹配」） | **已登记**（C.1 第三条）：明确误报面与保守方向，并登记 Roslyn 化降误报为可选加固 |
| 78 | 重要 | 文档把「变量化不覆盖」与既有非字面量守卫混为一谈 | **已修**：C.1 改为**覆盖/未覆盖逐条对照**（已覆盖＝首参数简单标识符/成员表达式；未覆盖＝前缀变量化、复杂表达式、别名链、动态/IL） |
| 79 | 重要 | 交接稿标题仍「截至批次五十」；B′ 待办仍列 #11 | **已修**：标题同步为批次五十一；B′ 从待办移除并标注「已交付（组件/宿主层）」 |
| 80 | 建议 | §24.63-D 的「6 模式」与实现数组（9 种拼写）易混 | **已采纳**：明确为「**6 个操作类别**／9 种源码拼写」（C.3） |
| 81 | 建议 | 正则宜预编译并具名集中 | **已采纳**：三支零容忍＋`OpCode` 两支均为**具名静态 `Regex`（`RegexOptions.Compiled`）**，便于逐支正反例 |
| 82 | 建议 | 长期应改用 Roslyn 语法树以降误报 | **已登记为可选加固**（C.1 第三条） |

**G. 第 2 轮（验证会诊：1 阻断／4 重要／3 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 83 | 阻断 | 两支 `OpCode` 正则**并未覆盖全部** `OpCode =` 赋值：`OpCode = (x)`／`= $"…"`／`= (string)x` 两支皆不命中 ⇒ **漏报即通过**，且与文档「覆盖全部赋值形态」矛盾 | **已修**：新增 **`OpCode =` 总量守恒断言**（每文件 `all == 字面量支 ＋ 表达式支`；实测全部 0 差）⇒ 出现未识别形态**必须**先扩展口径，不得静默通过；文档 A⑥ 与「覆盖范围」同步 |
| 84 | 重要 | 方法组正则 `\.\s*SendCommandAsync\s*(?!\()` 的 `\s*` 可**回溯到零** ⇒ 把**带空格的正常调用** `client.SendCommandAsync (op, …)` **误报**为方法组使用 | **已修**：改为 `(?!\s*\()`（前瞻内消化空白）＋新增「带空格调用」不应命中断言 |
| 85 | 重要 | 方法组只认**点前缀** ⇒ 漏 **无点前缀**的委托传递（`Register(SendCommandAsync)`／`= SendCommandAsync;`） | **已修**：并入 `(?<![A-Za-z0-9_\.])SendCommandAsync(?!\s*\()`（实测 0 处）＋两条正例断言 |
| 86 | 重要 | 反射支漏**空白排版**与**逐字字符串**：`t.GetMethod ("SendCommandAsync")`／`GetMethod(@"SendCommandAsync")`／`GetRuntimeMethod (nameof(...), types)` | **已修**：统一为 `(GetMethod|GetRuntimeMethod)\s*\(\s*(?:普通/逐字字符串 \| nameof(...))`＋三条正例断言 |
| 87 | 重要 | 21 支正反例未覆盖上述关键边界 ⇒ 不足以证明修复目标成立 | **已修**：反例扩至 **28 支**（新增空白排版、逐字字符串、无点前缀、带空格调用、带空格声明等 7 支） |
| 88 | 建议 | 直方图为「文件→值→次数」**非位置级**：同文件成对互换不可发现 | **已登记**（C.1 第四条）：明确粒度边界，位置级/语法树比对归 Roslyn 化可选加固 |
| 89 | 建议 | 交接稿优先序列称「四层守卫」而 §24.63／B′ 称「五层登记」，口径不统一 | **已修**：交接稿统一为「**五层登记**」并同步最新回归计数 |

**H. 第 3 轮（验证会诊：0 阻断／2 重要／2 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 90 | 重要 | 反射零容忍支漏 **`GetMethods()` ＋ 按名称筛选**（`.Name == "SendCommandAsync"`）与 **`IgnoreCase` 取单方法** | **已修**：新增第 4 类零容忍 `reflection-name-filter`（`.Name ==`／`.Name.Equals`／`GetMethod(s)(\"sendcommandasync\"…)`，**忽略大小写**；实测 0 处）＋6 支正反例 |
| 91 | 重要 | **守恒判据缺乏直接自测**（三类未识别形态无样例）且代码注释仍称「两支覆盖全部形态」（与本轮结论矛盾）；§24.63-B 仍写「15 支」 | **已修**：抽**唯一实现** `OpCodeAssignmentCounts` ＋ 新增 `OpCodeConservation_RejectsUnclassifiedForms`（6 支；`= (x)`／`= $"…"`／`= (string)x` ⇒ **守恒失败**）；注释改为「两支互斥；**未覆盖形态由总量守恒拒绝**」；B 节计数更正为 **28 支** |
| 92 | 建议 | 方法组支的**保守误报面**（XML/普通注释、字符串、`nameof`）宜入样例，避免被误读为语义识别 | **已采纳**：补 3 支「应命中」样例（注释／字符串／`nameof`）并保留 §24.63-C 的误报面声明 |
| 93 | 建议 | 点分支既有形态（`client?.SendCommandAsync`／`this.SendCommandAsync`／跨行 `.SendCommandAsync`）无回归样例 | **已采纳**：补 3 支「应命中」样例 |

**I. 第 4 轮（验证会诊：0 阻断／2 重要／2 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 94 | 重要 | 守恒判据**未真正做到唯一实现**：守卫仍单独算一次 `nonLiteral` 并在断言中使用该值（丢弃 `OpCodeAssignmentCounts` 返回的 `nonLiteralCount`）⇒ 自测与生产断言可能口径漂移 | **已修**：循环内**全部**计数（含 `foundNonLiteral` 与守恒断言）统一取自 `OpCodeAssignmentCounts`，删除重复 `Regex.Matches` |
| 95 | 重要 | 用例计数与代码不符：间接样例实为 **34 支**（8＋15＋6＋5），全部 Theory **46 支**，本批新增 **48** 个测试；文档／交接稿残留 28／40 与「三类零容忍」旧称 | **已修**：§24.63-A/B/E 与交接稿统一为「**四类零容忍**／间接样例 **34** 支／Theory **46** 支／本批新增 **48** 个测试」；代码注释的「三支」改「四类」 |
| 96 | 建议 | `reflection-name-filter` 的忽视大小写带来**保守误报面**（任意 `.Name == "sendcommandasync"`、无 `IgnoreCase` 的 `GetMethod("sendcommandasync"…)`）应登记 | **已采纳**：C.1 新增该误报面声明 |
| 97 | 建议 | 其余反射入口宜**点名**而非笼统归入能力边界 | **已采纳**：C.1 点名 `GetMethod(methodName, …)` 名称变量化、`string.Equals(x.Name, "SendCommandAsync")`、`GetMember`／`GetProperty` 链路（归可选加固） |

**J. 第 5 轮（验证会诊：0 阻断／2 重要／1 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 98 | 重要 | 字面量支仍被**扫描两次**（守卫循环先 `OpCodeStringLiteralPattern.Matches` 建直方图，再调 `OpCodeAssignmentCounts` 重扫）⇒ 与「全部计数唯一实现」注释不符 | **已修**：新增**单遍唯一实现** `OpCodeScan`（一次遍历得**直方图＋字面量＋表达式＋总计数**），守卫循环与 `OpCodeAssignmentCounts` 均只经它，删除全部重复 `Regex.Matches` |
| 99 | 重要 | 「四类／34／46／48」未在全量口径位置统一：§24.41-C#11 行、§24.58 行、§24.63-D、交接稿优先序列仍写「三类」；§24.63 夹具摘要只列 ①②③ 漏 ①′（按名称筛选）且失败消息只列三项 | **已修**：四处「三类→**四类**」；夹具摘要补 ①′；失败消息补「反射按名称筛选」；优先序列支数改 **46** |
| 100 | 建议 | `reflection-name-filter` 仍漏**反向/等价比较**写法（`"SendCommandAsync" == x.Name`／`x.Name is "…"`／`"…".Equals(x.Name)`） | **已采纳（扩展而非登记）**：三支写法并入第 4 类零容忍正则（实测 0 处）＋4 支正反例（含 `"DoOther" == x.Name` 反例） |

**K. 第 6 轮（验证会诊：0 阻断／3 重要／0 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 101 | 重要 | `OpCodeScan` 虽为唯一入口，但内部仍**三次** `Regex.Matches`（字面量／表达式／总量）⇒ 「一次遍历」的注释与目标不成立 | **已修（真·单遍）**：改为**一条正则** `OpCodeAssignmentPattern`（`rhs` 可空 ⇒ 未识别形态亦入总量；`literal`／`nonLiteral` 同遍分类）**一次 `Matches`**；`OpCodeScan` 同时产出直方图／字面量／表达式／总量，守卫与自测共用 |
| 102 | 重要 | 当前汇总口径仍有两处「逐支正反例 46 支」残留（§24.58 行、交接稿优先序列） | **已修**：两处改为 **52 支**（间接样例 40＋`OpCode` 两分支 6＋守恒 6） |
| 103 | 重要 | `"SendCommandAsync"\.Equals(` 分支**过宽**（参数非 `.Name` 也会命中，如 `"…".Equals(otherValue)`） | **已修**：约束参数为成员 `.Name`；补 **2 支反例**（`Equals(otherValue)`／`Equals("literal")` ⇒ 不命中），间接样例总数 38→**40** |

**L. 第 7 轮（验证会诊：1 阻断／1 重要／0 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 104 | 阻断 | 单遍正则**把复杂 RHS 的前缀误分类**（`OpCode = operation + suffix`／`= operation()`／`= "task.start" + suffix`／`= new Something()` ⇒ 被当作已分类）⇒ 守恒假绿、仍留计数守恒式绕过 | **已修**：`OpCodeAssignmentPattern` 只负责**捕获整段 RHS**（到行尾/`,`/`;`/`}` 前），分类改由**两个精确锚定正则**（`^…$`：整段恰为字面量／恰为简单标识符）完成；**其余一律未分类 ⇒ 守恒失败**；补 **4 支**复杂 RHS 反例＋1 支对象初始化器尾逗号正例 |
| 105 | 重要 | 反向 `.Equals` 分支仍可误报非 `.Name` 参数（`.Equals(x.NameSuffix)`／`.Equals(x.Name.Trim())`／`.Equals(x.Name, comparer)`） | **已修**：要求**完整成员** `.Name` 且**紧跟右括号**；补 3 支反例 |
| 106 | 建议 | （本轮无建议项） | — |

**M. 第 8 轮（验证会诊：0 阻断／1 重要／2 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 107 | 重要 | RHS 捕获 `[^\r\n,;}]` **不识别字符串边界** ⇒ `OpCode = "a,b"`／`"a;b"`／`@"a}b"` 会被截断而未分类（**保守误报**，非假绿），与「任意内容字符串字面量／整段 RHS」的声明不符 | **已修**：RHS 捕获改为**字符串边界感知**（普通字符串含转义／逐字字符串 `""` 转义内的 `,;}` 不截断）＋4 支样例（`"a,b"`／`"a;b"`／`@"a}b"`／同一行多属性）；**跨行 RHS 仍保守失败**（已在 C.1 登记） |
| 108 | 建议 | C.1 仍写「上述三类间接形态」 | **已采纳**：改「**四类**」，并同步「字符串边界感知」措辞与「跨行 RHS 保守失败」边界 |
| 109 | 建议 | 缺「同一行多属性」直接回归样例 | **已采纳**：补 `OpCode = "task.start", Payload = x` 正例 |

**N. 第 9 轮（验证会诊：0 阻断／1 重要／3 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 110 | 重要 | 登记的「跨行 RHS 保守失败」**不成立**：等号后的 `\s*` 会吞掉换行 ⇒ `OpCode =\n operation` 被正常分类并守恒通过，文档与实现效力不符 | **已修**：等号后改为**只允许空格/制表符**（`OpCode[ \t]*=[ \t]*`）⇒ 跨行给值不再被捕获为已分类形态（未分类 ⇒ 守恒失败），并补 2 支跨行反例（标识符／字面量） |
| 111 | 建议 | 分类器不能分类**含转义引号**的合法字面量（`"a\",b"`／`@"a""}b"`）⇒ 保守失败 | **已采纳（扩展）**：分类拆为**普通（含转义）／逐字（`""` 转义，值归一）**两支＋2 支正例 |
| 112 | 建议 | 「字符串字面量已覆盖」的表述略宽于实际能力 | **已采纳**：C.1 明确「含转义引号亦可分类；**未闭合**字符串与**跨行 RHS** 保守失败（只会拒绝、不会静默放行）」 |
| 113 | 建议 | 计数口径核对（43／64／66 → 现应为 43／68／70） | **已采纳**：A/B/E 与交接稿统一为 **43／68／70**（本批新增 70 个测试、全量 1042/2/1044） |

**O. 第 10 轮（验证会诊：1 阻断／0 重要／4 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 114 | 阻断 | 第 9 轮把**等号前**空白也收窄为 `[ \t]*` ⇒ **合法写法** `OpCode` 换行 `= "…"` **完全不被扫描**（总量 0 ⇒ 守恒反而成立）＝**假绿**，且新字面量不进直方图 | **已修**：改为 `OpCode\s*=[ \t]*…`（**等号前允许跨行**、等号后仅空格/制表符）；补 2 支正例（等号前跨行 ＋ 字面量／标识符） |
| 115 | 建议 | 跨行反例与同行各形态核对（制表符缩进等） | **已核**：`OpCode =\n value` ⇒ 总量 1／分类 0 ⇒ 守恒失败；同行普通/逐字/标识符/多属性均正常分类 |
| 116 | 建议 | 值归一语义核对（普通保持源码原样；逐字 `""`→`"`） | **已核**：现有 17 个登记值不含转义 ⇒ 直方图预期不变（本批回归亦未变化） |
| 117 | 建议 | 计数口径核对 | **已核**：**该轮处置前快照＝43／68／70**；第 10 轮新增 2 支后为 **43／70／72**，第 11 轮再新增（2 注释间隔＋8 精确计数）后为 **43／80／82**（最新口径见 B/E 与交接稿） |
| 118 | 建议 | 历史快照标注与效力边界核对 | **已核**：旧计数均有后续「口径更正」；生产接线关闭／R5.8 未签署未被拔高 |

**P. 第 11 轮（验证会诊：2 阻断／1 重要／1 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 119 | 阻断 | 第 10 轮新增的两支「等号前跨行」正例**只断言守恒** ⇒ 若正则再次漏扫（总量 0）仍会 `0==0` **假绿** | **已修**：新增 `OpCodeScan_CountsLegalTokenSeparators_Exactly`（**8 支精确计数**：等号前跨行／块注释／行注释／同行形态 ⇒ `Total==1` 且分类恰 1 支；等号后跨行／复杂 RHS ⇒ (1,0,0)） |
| 120 | 阻断 | `OpCode` 与 `=` 之间为**注释**（`OpCode/*c*/= "x"`）时**完全漏扫** ⇒ 总量 0 ⇒ 假绿 | **已修**：token 间隔放宽为 `(?:[\s]|/\*…\*/|//…)*`（跨行 ＋ 块/行注释）＋2 支样例（块注释／行注释） |
| 121 | 重要 | **预处理指令**插入 `OpCode` 与 `=` 之间、以及**序列化别名**（`[JsonPropertyName]`／构造器参数／字典键）形态 ⇒ 本守卫无法发现 | **如实登记**（C.1 新增「未覆盖（静默漏扫）」两条，含需评审关注与 Roslyn 化承接）；**不**声称已覆盖 |
| 122 | 建议 | §24.63-O 第 117 项仍写「43／68／70」 | **已修**：标注为**该轮处置前快照**并给出后续 43／70／72 → **43／80／82** 的演进口径 |

**Q. 第 12 轮（验证会诊：0 阻断／1 重要／0 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 123 | 重要 | `OpCode` 匹配缺**左边界** ⇒ `x.NotOpCode = "y"`／`SomeOpCode = "y"` 被误计为 `OpCode` 赋值（放宽 token 间隔后新引入的保守误报面） | **已修**：加 `(?<![A-Za-z0-9_])` 左边界（仍匹配成员赋值 `x.OpCode = …` 与独立 `OpCode = …`）；精确计数样例增至 **11 支**（新增 `x.OpCode` 正例与 `x.NotOpCode`／`SomeOpCode` 反例） |

**R. 第 13 轮（验证会诊：0 阻断／2 重要／0 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 124 | 重要 | 左边界 `[A-Za-z0-9_]` 只覆盖 ASCII ⇒ **Unicode 后缀同名标识符**（`变量OpCode = "y"`）仍被误计（新的保守误报） | **已修**：左边界扩为**完整 C# 标识符字符集** `(?<![\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}])`；补 `变量OpCode` 反例 ⇒ `(0,0,0)` |
| 125 | 重要 | 精确计数样例缺**逐字标识符**与其它限定成员形态（`@OpCode`／`this.OpCode`／`global::X.OpCode`）⇒ 若后续把 `@` 误纳入禁止前驱会静默漏扫 | **已修**：三支均补为**正例** ⇒ `(1,1,0)`；精确计数样例合计 **15 支** |

**S. 第 14 轮（验证会诊：0 阻断／2 重要／3 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 126 | 重要 | 左边界漏 `\p{Cf}` ⇒ `变量<U+200C>OpCode` 仍误计（与「完整 C# 标识符字符集」声明不符） | **已修**：左边界补 `\p{Cf}`；补 Cf 后缀反例（`变量\u200COpCode` ⇒ `(0,0,0)`） |
| 127 | 重要 | **Unicode 转义拼写标识符**（`\u004F\u0070Code = "y"`）静默漏扫且未登记 | **已登记**（C.1 第三类静默漏扫，与预处理指令/序列化别名并列；明确**不**声称覆盖全部合法拼写） |
| 128 | 建议 | 精确计数样例宜按**字符类别**逐类反例（Nd／Mn／Mc／Pc／Cf）＋补制表符间隔样例 | **已采纳**：补 5 支逐类别后缀反例＋1 支 `OpCode\t=\t"y"` 正例 ⇒ 精确计数样例 **21 支** |

**T. 第 15 轮（验证会诊：0 阻断／1 重要／0 建议）与逐条处置**

| # | 严重度 | 发现摘要 | 处置 |
|---|---|---|---|
| 129 | 重要 | 逐类别反例漏 **`\p{Nl}`**（letter number）⇒ 若单独删除该类，21 支样例仍全绿，「完整标识符字符集」未被逐类证明 | **已修**：补 `A\u2160OpCode = "y"`（罗马数字一，`Nl`）⇒ `(0,0,0)`；精确计数样例 **22 支**（ASCII／`\p{L}`／`Nd`／`Nl`／`Mn`／`Mc`／`Pc`／`Cf` 全类别覆盖） |

**U. 第 16 轮（验证会诊）处置状态与**事后补验入口**（如实登记）**

1. **通道状态**：本轮拟对「第 15 轮处置后的最终状态」做第 16 轮验证会诊，但**会诊通道不可用**——
   `gpt-5.6-sol` 连续 3 次返回**账号级限流**；按交接稿纪律降级 **deepseek-flash 只读**时，子代理
   **三次均未收到任务文本**（返回「没有收到具体任务内容」）⇒ 本轮**未取得会诊结论**（**不**声称已会诊）。
2. **机械自检（本轮实际执行；替代不了会诊，如实列明）**：①左边界字符类实际含
   `\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}` **全七类**（源码文本核对）；②`\p{Nl}` 后缀反例
   `A\u2160OpCode = "y"` 在源码中**恰 1 处**；③定向夹具 **104 通过／0 失败**；④全量回归
   **1068 通过／2 跳过／1070（0 失败，33s）**。
3. **本轮改动性质（收窄 §17.4-A ① 的适用范围并留痕）**：仅**新增一支负例断言**＋同步文档计数
   （**未改生产代码、未改守卫判定逻辑**）⇒ 判定面事实不变，但**文档声明面已变**（故**不**援引措辞豁免）。
4. **事后补验入口（可剔除）**：下一批次的**首轮会诊**须**补验**本批最终状态（重点：22 支精确计数的
   逐类别覆盖、三类静默漏扫登记、43／94／96 计数自洽）；若补验提出阻断/重要项，按 §17.4-A ③ 照常处置，
   并在此处登记处置结果（本项为**流程性挂账**，非产品缺陷）。

### 24.64 落地登记：`submission_conflict` 被拒请求释放主槽位（2026-09-23；owner 裁决 #2）

1. **取代旧待决口径**：§24.41-C#19、§24.44 与交接稿 B 表 #2 的「被拒尝试留 `Queued/Active`」是**修复前事实**。owner 已选「被拒请求释放自己的名额、保留拒绝原因；原未决任务继续保留且绝不自动重发」。本项只处理**未签发本轮发送许可**的 `submission_conflict` 分支；不改变原未决 `Submission`、发送身份或许可水位。
2. **实现**：`ArbitrationAdmissionService.ProcessWinnerAsync` 在占位锁内复核返回 `submission_conflict` 后，不再把本笔 `InRound` 回退为 `Queued/Active`；调用既有 `TerminatePrecheckAsync`，以 `TerminalRejected`＋`LastPrecheckResult.ReasonCode=submission_conflict` 持久化并迁出主槽位。门面与外部入口将其作为**确定拒绝**返回，不映射成结果未知；后来者若重试，须**创建新请求**，不得复用原未决发送许可。持久化失败不报告未落盘的结论。
3. **反例先行与回归**：新增 `SubmissionConflict_RepeatedRejections_ReleaseOwnSlots_KeepOriginalResponsibility`，改动前在首个被拒请求上实测 `Queued`（红）；改后连续 **32** 次拒绝均有终局状态、可查原因、零新许可、非 `Active`，原 `Submission` 身份与序号保持，权威对账关闭原责任后新请求获准且只新增一次发送。既有 `Capacity_MainSlotsExhausted_33rdCreateRejected` 的负例造景改为**32 个已受理未终结作业**真实占槽；不能再把 31 个冲突拒绝当作应占槽事实。该负例与恢复再次取许可、未知责任零重发三项定向夹具通过。
4. **验证边界**：助手侧全量本轮 **1068 通过／2 跳过／1 失败／1071**；唯一失败为既有 `R410ProductionWiringTests.C5_StartupChain_HandoffDelegate_BindingAndNegativePaths` 枚举真实 `%APPDATA%/NexusBGI/runs` 时被本轮沙箱拒绝。显式排除此**单个环境受阻夹具**后，同轮其余 **1068 通过／2 跳过／1070（0 失败）**；这**不能**冒充全量绿。生产节点改道与外部启动接线仍关闭；R5.8 未签署。§24.41-C#19 状态更新为**组件层已交付、真实入口与生产开门未验收**。墓碑总量上限仍按既有 256/24h 容量合同，不把本批 32 次夹具外推为无限次拒绝永不背压。

### 24.65 owner 新裁决：宿主关闭取消链与带编号队列必需（2026-09-23；#3／#5）

1. **#3 范围**：E3/E4/E5 本期不提供单次点击撤销；宿主退出须取消在飞发送，已出站而结果不明须保留责任、禁止自动重发。`TaskCenterHost.SubmitExternalStartViaAdmissionAsync` 将 `_shutdownCts` 与入口令牌链接给门面 `CallerToken`；`DispatchExternalStartViaHostAsync` 将该令牌交给 `ExternalStartContext.ExecuteAsync`；E3 队列适配器再交给 `BgiExternalClient.SubmitTaskStartAsync(cancellationToken)`。宿主夹具 `ExternalStart_HostShutdownDuringSend_CancelsSender_KeepsUnknownResponsibility` 先因在飞发送无法取消而红，改后绿，核对一次发送、保守待对账、原 `Submission` 保留、无虚假受理台账；全套并行时发现关停与结算交错可分别回 `NeedReconcile` 或 `Reconciling`，两者均不结清责任，夹具按此真实状态表断言。**限定**：E4 热键尚无带编号通道，发送前／回执后与真实 ext 传输交错未验，恢复与 `RetryAsync` 另列。
2. **#5 范围**：owner 要求所有参与方升级，新生产入口只走带任务编号的 ext 队列。E3 接线态的 `start_group`／`start_oneclick` 在 ext 对象缺失、非 Ready 或缺 `CapabilityTaskQueue` 时，现返回确定未发送的 `Rejected/task_queue_unavailable`，不落回 v2；发送已出站后断线／缺句柄仍是 Unknown、保留责任。`WiredStart_NoQueueChannel_RejectsBeforeAnyV2Fallback` 两支先红后绿；原组合根三支 v2 回退造景按新合同**改断言而未排除**，均检查两通道零发送及适配器明确失败。§24.4-4「v2 成功＝入口 success」保留为**旧合同历史证据**，不再作为新生产接线的成功判据。
3. **热键新前置**：owner 另选 E4 热键也升级到有编号通道；当前 BGI/助手尚无热键的 ext 队列提交协议，旧 E4 新调度接线仍关闭，不能声称满足本条。需先建 BGI 协议、助手映射与编号回执／完成观察／取消／抢占证据。新旧合同的生产开门仍由 §23.4／§23.9 并集门禁和 R5.8 约束，不能凭本节局部夹具打开。
4. **本批回归**：声明面清单按 `CLAIM_SURFACE_REGENERATE=1` 单独再生成，关闭该变量后声明／夹具引用／失败模式守卫 **22/22**；助手侧全量 **1071 通过／2 跳过／1 失败／1074**，唯一失败仍是既有 C5 用例枚举真实 `%APPDATA%/NexusBGI/runs` 被沙箱拒绝。显式排除这一个环境受阻用例后 **1071 通过／2 跳过／1073（0 失败）**。首次并行全套时新关停夹具暴露 `NeedReconcile`／`Reconciling` 两种合法关停结算交错，已按「两者均保留原 `Submission`」修正断言并复跑全套；不把环境排除结果称作全量绿。

### 24.66 本批复核补强：观察取消边界、续用责任维与无回退（2026-09-23）

1. **宿主关闭与观察取消分离**：早期编号回执后，终态观察任务的取消源固定为 `ExternalStartAdmissionRequest.HostLifetimeToken`；未提供宿主令牌时使用不可取消令牌，绝不回退到发送令牌。完成观察调用方取消时只取消本次等待（`WaitAsync(ct)`），底层终态取证继续运行；宿主关闭才取消观察任务。对 owner 裁决 #3 的边界夹具覆盖了发送令牌取消、观察等待取消、宿主关闭和缺失宿主令牌四种交错。
2. **续用待对账回执**：同一 `RequestIdentity` 续用已处于 `Reconciling` 的操作时，返回 `Unknown`＋责任 `Pending`，并回显原 `SubmissionIdentity` 与 `SendSeq`；不新增发送。`UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance` 对续用回执与原责任均作断言。
3. **BGI 队列关闭态**：`BgiTaskCoordinator.Submit` 在 `_disposed` 检查点、队列项构造／事件／注册之前返回 `Unavailable`；`ExternalInterfaceCommandPlane` 将其映射为 `queue_unavailable`，不转入 v2 `task.start`。新增真实已 Dispose 协调器测试断言无执行、无事件；响应映射测试单独断言零回退错误码。完整 dispatcher 的关闭态组合路径尚未有独立集成夹具；静态复核确认分支无 v2 调用，生产组合根仍关闭。
4. **会诊与定向验证**：GPT Astra 高档跨模块复核指出并推动修复两项中等问题：观察不再随发送阶段取消，以及同身份 `Reconciling` 回执不再丢责任字段；随后 gpt-6-sol 中档聚焦复核确认取消边界无具体正确性发现，BGI／冲突路径复核无其他具体问题。BGI 复核建议补完整 dispatcher 集成夹具；当前已有协调器关闭态测试与 dispatcher 映射测试，组合路径覆盖仍记为开门前验证缺口。
5. **本次全量回归**：助手 **1085 通过／2 跳过／1 失败／1088**；唯一失败 `ClaimSurfaceGuardTests.DesignDocs_ClaimSurface_MatchesReviewedManifest`，因本批声明面有变化、清单尚待会诊后重新生成。BGI **938 通过／14 失败／952**；既有对照为 **936 通过／14 失败／950**，本批新增两项通过、失败数未增加。助手通过数高于 552 基线，但两套全量回归均不得记作全绿。生产开门及真实 User 门禁保持关闭，R5.8 未签署。

### 24.67 本轮安全复核与待闭合交错（2026-09-23）

1. **会诊模型规则**：本会话普通技术复核使用 GPT-6-SOL 中档；最难分析及安全敏感审查使用 GPT-6-Astra 中档；owner 明确指定时遵从指定。SOL 中档发现接管台账与“未受理”关闭竞态、墓碑清理后审计/预观察仍反向引用已删除操作、待交接确认可被普通重驱动三项重要问题；Astra 中档确认三项成立，并指出还需核对恢复和裁决中间态。
2. **本轮已落地的安全阻断**：`ContinueUseAsync`／`RetryAsync` 对 `PreemptConfirmPending` 返回 `NeedPreemptConfirm`；`ValidateAndOccupy` 在跨进程原子占位事务内再次拒绝该标记，且本轮新发送不再顺手清标记。`PreemptConfirmPending_FreeOccupancyDoesNotAuthorizeContinueOrRetry` 覆盖“占用变空但没有确认”交错，验证续用、重试仍零发送。定向五项测试 **5/5 通过**（包括之前的完成/拒绝并发回归）。这实现 fail-closed 阻断；当前尚无可信的确认后继续入口，故不能称安全交接闭环已完成。
3. **未闭合的重要交错**：①完成路径先写外部接管台账、后写租约终态时，另一个服务仍可能把同轮改成 `RetryableRejected`；现有并发测试没有断言外部台账及后续重试。需先确定并落地发送级持久化认领与所有负向结算原子排斥。②`MigrateAndClean` 到期删除 Operation，却保留追加式审计和完成预观察；Store 读校验仍要求它们反向命中 Operation。真实清理后可能下一读 `Corrupt`；同时需保护历史游标消费、防止清理后换新请求重放。③Astra 另指出裁决 claim 发布失败读回、claim 与证据集合绑定、恢复补终态、台账写侧/读侧 fail-closed 尚待逐项验证。
4. **效力边界**：本条不表示以上残项已修复，也不打开生产接线或真实 User 门禁；R5.8「无双跑」验收单仍未签署。当前定向助手组件测试 **5/5**；声明面守卫重新生成后单项 **1/1**；串行全量助手 **1119 通过／2 跳过／1121（0 失败）**，BGI **938 通过／14 失败／952**（14 项与既有基线一致）。全量结果满足助手侧 552/552 下限，但 BGI 套件并非全绿；未完成的安全语义与生产门禁不能据此标为闭合。

**当前未闭合失败模式（§24.58 登记）**：

| # | 失败模式 | 当前状态 |
|---|---|---|
| 1 | 外部受理接管台账与并发未受理拒绝互相覆盖，导致已接受事实仍允许重试 | 未闭合；须在发送身份/序号级原子认领，并让所有正负结算路径互斥 |
| 2 | 到期清理删除 Operation 后，审计/预观察/游标历史仍依赖该 Operation，导致读损坏或重放 | 未闭合；须保留独立历史凭证并验证清理后读取、分页和重放拒绝 |
| 3 | `PreemptConfirmPending` 无可信确认入口；普通续用、重试和占位已 fail-closed | 安全阻断已交付，确认后继续及证据绑定尚未交付 |
| 4 | 裁决 claim 发布失败/证据版本绑定、恢复补终态、台账读写校验未形成全路径 fail-closed 证据 | 未闭合；逐项复核仍需完成 |

### 24.68 §24.67 残项范围补记（2026-09-23）

本节为 §24.58 提供逐项的承接、完成证据、门禁和状态。`PreemptConfirmPending` 普通续用、重试、占位路径已拒绝发送，但这项局部阻断不等于完整交接闭环。

| # | 责任方/承接 | 完成判据 | 门禁 | 当前状态 |
|---|---|---|---|---|
| 1 | 助手仲裁租约/发送结算；施工方实现与并发夹具 | 发送身份、SendSeq 的持久认领与 `Accepted` 事实原子可恢复；所有 NotAccepted、拒绝、冲突和关闭路径不能覆盖已认领事实；竞态后恢复扫描不得重发 | 外部启动门、R5.8 门 | 未闭合；现有测试未断言外部台账与后续重试的组合结果 |
| 2 | 助手租约存储与历史游标；施工方实现独立历史凭证/归档和清理夹具 | 到期清理后租约可读；审计/预观察引用有独立可验证来源；旧游标可分页或明确过期；同一历史身份不能借清理窗口重放 | 持久化读写门、R5.8 门 | 未闭合；不得只删反向引用校验或把历史游标当作无记录 |
| 3 | 抢占确认入口与仲裁服务；施工方定义一次性、证据绑定确认事务 | 只有绑定被抢占任务身份及退出证据的确认可继续；重放/换身份/状态变化均拒绝；确认前普通续用、重试、占位保持零发送；确认后只为新到任务签发正确许可 | 新任务调度门、R5.8 门 | 局部 fail-closed 已交付；可信确认、证据绑定及确认后继续未交付 |
| 4 | 裁决恢复服务与外部启动台账；施工方逐路径验证 | claim 发布/持久化失败必须可检测且不放行；claim 绑定证据集合修订；恢复能补终态但不补造证据；台账读写、发布与损坏/权限错误均 fail-closed | 仲裁恢复门、外部启动门、R5.8 门 | 未闭合；需按方法和失败点补反例并跑恢复交错 |

Owner 已裁决的优先级规则、S4b/S8b 接线要求、关闭保护、编号队列和冲突拒绝槽位处理不再列作待裁决；对应代码/协议/端到端验收仍未全部完成。生产接线及真实 User 门禁保持关闭，R5.8「无双跑」验收单未签署。

### 24.69 逐轮迟到回执恢复加固（2026-09-23）

Owner 选择 A 后，补齐如下持久化与恢复路径：

1. `TakeoverPersist` 对 `ExternalStart` 按台账 entry 的规范 `submissionIdentity＋sendSeq` 反查请求；必须匹配同一 ExternalStart 操作及 candidate/resource/action/epoch，并由同轮 AcceptanceClaim、开放 Submission 或 ExecutionResult 之一证明回执来源。历史 claim 不再借用 Operation 当前轮身份；接管恢复测试验证 round 1 claim 写回仍为 round 1。
2. 新增 `RecordLateAcceptedReceipt`。重复观察同一轮时保留首次观察时间/来源，只单调补齐缺失 RunId/JobId；不同非空句柄仍拒绝。宿主写后读回校验规范台账记录。租约和外部台账都在发布前校验候选文件，非法候选拒绝且原文件与修订号不变。
3. 同轮 `accepted_receipt` 在匹配的 `ResolvedAcceptedTerminal` 审计持久化后可以保留为历史证据并解除 `ConflictPending`；没有匹配审计时写侧拒绝清 pending。旧轮回执仍保持冲突待决，当前轮“未受理”不能替它结案。
4. 若 `ExecutionResult＋PendingTerminal` 已持久化而台账仍 `AcceptedPendingExecution`，恢复会用原载荷续写台账终态；最终结算对分类时的两个完整快照逐字段复核，避免新载荷被旧扫描结果终局化。

**验证**：助手完整单元测试 1132 通过／2 跳过／0 失败（1134 总数）；本节新增的旧轮 claim、同轮回执审计、外部台账候选拒写、受理回执幂等、未终态台账续跑及载荷变化交错测试均通过。BGI 完整单元测试本次因测试主机崩溃中止（577 通过、4 失败，581 后中止）；按 `BgiTaskCoordinatorTests` 过滤运行仍在测试发现后崩溃，故该侧本轮没有有效测试结论。生产接线及 R5.8 门禁不因此开放。

**仍需 Owner 明确一项业务处置**：旧轮 `Accepted` 与新轮确定拒绝并存时，当前实现按 A 保存并停在 `ConflictPending`，没有可结清的旧轮裁决入口。具体选择及推荐已在本会话询问；收到答复前维持停驻、不得自动重发，也不得将此项计为闭环。

### 24.70 Owner 选择 A：旧发送轮迟到终态由当前所有者结清（2026-09-23）

本节取代 §24.69 的“仍需 Owner 明确”状态。Owner 选择 A 的含义是：**旧发送者只追加证据；当前所有者核实完整证据后结清**。这不是允许旧发送者越权修改 Operation。

1. 宿主旧发送者仅在旧轮迟到 `Accepted` 回执已按完整 `submissionIdentity＋sendSeq` 写入并读回成功、且结算证明 owner generation 已过期时，才可把随后观察到的终态追加到共享外部台账。旧发送者不改租约文件、不关闭当前 Submission、不置 Operation 终态、不释放主槽位；终态 kind、原词、错误码、JobId、来源和观察时点必须一并保存并逐字段读回核实。无法确认写入时返回待对账，责任保持 Pending，禁止重发。
2. 当前 owner 的恢复扫描在租约内重新核验旧轮 Accepted 回执、旧轮终态台账载荷及读取钩子确认结果；还必须确认该操作是 ExternalStart、旧轮身份严格匹配、当前轮已有同轮确定拒绝且拒绝仍是 LastResult、当前轮 Submission 已关闭、现有 ExecutionResult/PendingTerminal/AcceptanceClaim 无冲突，并且不存在需要另行裁决的活动合并镜像。任何条件缺失或冲突都保持待核查，不自动结案。
3. 若当前 owner 尚未开始新一轮、完整终态对应当前发送轮，恢复扫描只在 operation 仍为 `Accepted/Reconciling`、无冲突、未拒绝、两终态载体均缺失，且终态台账逐字段读回确认后，才把该台账事实转换成 `ExternalStartCompletion` 并交给**原 `SettleCompletionAsync` 事务**。正常受理接管、`ExecutionResult＋PendingTerminal`、台账确认、Submission 关闭及 Operation 终局顺序仍由原事务负责；旧发送者从不调用该结算路径。若已进入拒绝、冲突或轮次推进状态，不走此窄分支。
4. 若当前 owner 已开始较新轮，且较新轮有同轮拒绝事实，所有条件成立时，当前 owner 在一个租约写事务中追加 `ResolvedHistoricalAcceptedTerminal` 审计（保存旧轮终态快照和当前轮拒绝快照），把旧轮载荷写入 `ExecutionResult＋PendingTerminal`，标记 `TerminalCompleted` 并清除该历史冲突。旧轮身份留在结果与审计中；之后由已有 `MigrateAndClean` 迁入 `Tombstone` 时释放主槽位。恢复重复扫描幂等，不追加第二份审计，也不重新发送。
5. 租约写侧拒绝缺少逐轮状态标记的历史 Accepted 回执；已结清标记须同时有对应终态快照、两轮身份及审计，否则候选文件被拒。失败、取消与成功共用终态类型校验；失败还必须带错误码。

**验收证据**：`HistoricalAcceptedReceiptCannotBeClearedByCurrentRoundNotAcceptedAdjudication` 覆盖“先阻止当前轮拒绝清旧冲突→旧轮终态到达→按两轮快照结清→重复恢复幂等→重试不发送→写侧不得擦掉历史跟踪状态”；`StaleOwner_AppendsLateAcceptedTerminalToOriginalRound_WithoutFinalizingOperation` 覆盖宿主换主交错：旧发送者仅更新原轮台账；随后新的宿主取得租约并恢复同一轮终态，操作结清且发送计数仍为 1。两个定向用例 **2/2**；准入、租约、外部启动宿主等相关类 **258/258**；助手全量 **1133 通过／2 跳过／0 失败（1135 总数）**。两个 Skip 是受控负载诊断入口。BGI 全量三次分别 **938/14、939/13、938/14（总数均 952）**，FsCheck 生成型用例失败计数有波动；`BgiTaskCoordinatorTests` 定向 **19/19**。Astra 中档只读会诊本次未启动成功（GPT 执行器返回 exit code 1），因此不记会诊结论。

**范围边界**：此项只关闭迟到旧轮受理及其权威终态的责任恢复；不签署 R5.8「无双跑」验收单、不打开生产接线或真实 User 门禁，也不替代其他待闭合交错与全量 BGI 回归。

### 24.71 状态快照可用性与新鲜度 fail-closed（2026-09-23）

1. **BGI 状态来源**：`task.status` 与只读状态响应现在带 `bgiEpoch.processId/startTicksUtc`，来源为 `JobRegistry.CurrentEpoch`。助手公共解析器仅当 `running` 是布尔值且 epoch 的 PID/启动 ticks 合法时将状态标记为可用；`stateRevision` 可解析但仅 ext 路径要求存在。
2. **轮询及 ext 缓存**：v2／observer 成功响应记录本地观测时点。ext 路径每轮状态轮询调用 `RefreshStatusSnapshotAsync`，在同一锁下读取 JSON 与接收时点；快照必须有 `stateRevision`、年龄在 0–20 秒内且 epoch 等于当前 SDK `ServerEpoch`。外部连接离开 `Ready` 会清掉 JSON 和时点；本轮状态采集异常使上一轮 `LatestLocalStatus` 的 `TaskStatusAvailable=false` 且清观测时点，避免旧的 idle 展示值延续为准入证据。
3. **准入影响**：`ControlStatus` 新增状态可用性、BGI epoch、观测时间及 `HasFreshTaskStatus`。生产 `CurrentArbitrationFacts` 在快照不可用/过期、宿主 epoch 缺失或不匹配、外部台账未知时报告 `ExecutionFactsUnknown`，阻止将默认 `TaskRunning=false` 当成空闲。start/resume 移交等待新鲜快照；存在宿主 epoch 时必须与快照一致。`armTrigger` 仍不受快照限制。宿主 epoch 缺失时保持既有登记行为，但不产生 `AdmissionSourceScope`，后继执行仍 fail-closed。
4. **反例与验证**：助手生产路径夹具覆盖 unavailable、stale、wrong epoch 的 `NeedReconcile/facts_unknown`＋零发送，以及 matching fresh idle 的正向恰一次发送；解析夹具覆盖合法 busy/idle 与缺失/畸形 `running`/epoch。修正一处旧夹具误用 `9:900`（与 `RoutingFakePort.Epoch` 不同）后，相关助手类 **216/216 通过**、另有 **2 个受控 P50 用例 Skip**（总数 218）；助手全量 **1155/2/1157（0 失败）**。BGI `TaskStatus_IncludesCurrentProcessEpoch` 与 `BgiTaskCoordinatorTests` **20/20**；BGI 全量 **939 通过／14 失败／953**，§24.70 同配置三次对照失败数为 14／13／14，本次失败数未超既有波动范围、总数因新增 1 个 BGI 状态纪元测试增加 1；该套件仍不全绿。
5. **会诊记录**：GPT-6-Astra／medium 对本批 diff 的只读审查尝试 1 次，执行器返回 `Codex exit code 1`，未取得报告。此错误不属于约定的超时、瞬时网络或 5xx 类，未重试；不得记为会诊通过。
6. **未闭合边界与门禁**：状态快照仍不是 BGI 原子执行快照；查询后到发送前仍可能变化。当前无权威执行实例身份/任务类别优先级，也无绑定具体被抢占任务的停止后退出确认。因此本批不实现抢占，不证明“busy 零发送”覆盖所有入口，不开放 E3/E4/E5 或节点生产门。真实 User 门禁保持关闭，R5.8「无双跑」验收单未签署。