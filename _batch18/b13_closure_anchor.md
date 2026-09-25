# 批次 13（D5）收口证据锚点（应批次 18 会诊 F3「重要」处置要求编制）

> 用途：使「D5 已由批次 13 收口」在批次 18 会诊/登记中具备**就地可核对**的证据锚点，不再仅以引用存在。
> 来源：R5.3 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md` §24.108（第 2907 行起，提交 `212ba3470` 时点工作区版本）、
> R5 交接稿 `Docs/design/onedragon-r5-handoff-2026-09-21.md` 页首批次 13 行、提交 `7336902d8` 元数据。
> 编制：sess_7687f914，2026-09-26。**本文件为节选＋撮要，权威版本仍以 R5.3 §24.108 提交态为准。**

## 1. 提交元数据（git log -1 7336902d8）

- 提交：`7336902d82459202ecd07f07ed0b0f0f315db43b`，2026-09-24 16:04:18 +0800
- 标题：`feat(r5): D5 批次完成类型拆分（CompleteSucceeded/CompleteWithUnresolved，纯函数未接线）`
- 正文要点（原文）：删除无载荷 `Complete`，拆为 `CompleteSucceeded`（唯一可触发 RunSpecified 收尾）与
  `CompleteWithUnresolved(BatchUnresolvedItem[])`（不携带成功/收尾许可，errorCode 原样保留）；空批次早退归
  `CompleteSucceeded`（空集合全成功为真）；全部确认分支按本拍确认投影逐项分两类；**修复首轮会诊必改**：
  未决判据先应用本拍 ConfirmTerminal 再分类；夹具：两条原 `_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed`
  改新语义（旧实现 13 红/64 绿/77），新增同拍反例与同拍对照及全成功/未启动/取消/空批次/幂等夹具；
  R5.3 新增 §24.108；声明面 +5 行；定向 77/77（batch13_green_core4.trx）；助手全量 1303/2/0/1305；
  IPC 事故审计 37/37；生产入口门与 R5.8 仍关闭，未接任何生产消费点。

## 2. R5.3 §24.108 关键原文节选

- **「本批做了什么」**：只实现 owner 2026-09-24 裁决的 D5＝推荐项 A（类型拆分）……本批**未接任何生产入口**：
  `BatchReconcileDecider` 在全仓库生产源码中**没有任何消费点**……不改变任何正在运行的行为。
- **类型拆分表**：`CompleteSucceeded()`＝全部期望项均可证成功（逐项 `Started ∧ TerminalWasCancelled != true ∧
  TerminalErrorCode == null`），**唯一**获准触发"完成后动作"（RunSpecified 收尾）；`CompleteWithUnresolved(...)`
  ＝全部期望项已到终态但至少一项不是可证成功，**不携带**任何"成功/可收尾"许可。
- **「旧的无载荷 `sealed record Complete()` 已删除（不再存在同名类型），因此**不存在**"沿用旧语义的默认路径"」**。
- **两处指定分支归类裁定表**（逐条含依据）：空批次早退（原第 166–170 行，现 191–199）→ `CompleteSucceeded`
  （vacuous truth；2026-09-13 空批次误触发事故兜底在调用方 `Started` 守卫，不在决策器）；「全部确认→完成」分支
  （原第 282–289 行，现 311–366）→ 按逐项投影分两类（此前同一拍无条件追加无载荷 `Complete()`）。
  **行号口径注（会诊第 2 轮 G1 处置）**：本锚点引文的「现 191–199／311–366」沿用 §24.108 提交态的
  **含前置注释行**口径（191 行起为 `[D5] 空批次归类` 注释块、311 行为 `// 3) 全部确认 → 完成` 节注释）；
  若按「仅语句行」口径则为 **195–199／312–366**——两组数字均系 HEAD `c41a2902e` 同一文件的准确行号，
  已逐行核对一致，无时点差异（`BatchReconcilePlan.cs` 自 7336902d8 后未被任何提交改动）。
- **本拍确认投影＝首轮会诊必改（第 1 项）**：未决判据必须按"本拍动作已应用后"的逐项状态分类……只读旧字段会把
  本拍才确认的 `lost_job`/`result_unknown`/`task_failed`/重试耗尽的 `preempt_timeout` 误判为"可证成功"，从而在
  同一拍产出 `CompleteSucceeded`。实现（第 316–335 行）先建 `pendingConfirm` 字典再逐项判未决；未决判据
  a=Started false（业务拒绝）/b=Cancelled（F11）/c=ErrorCode 非空（原样承载）。
- **调用方审计表**：`BatchReconcileAction`/`BatchReconcileDecider` 生产消费点＝**零**（`git grep` 只命中定义文件；
  文本扫描口径，不含反射/动态调用）；`TaskCenterHost.*` 无符号引用；无 UI 文案 switch 需迁移；
  真实收尾链路 `MainViewModel.OnlineBatch.cs` 依赖 `batch.CoordinatedSucceeded`，服务端
  `CoordinatedBatchState.cs` 只在全队 `succeeded` 时置 `completed` ⇒「有未确认也收尾」生产上当前不可达。

## 3. R5 交接稿页首批次 13 行（登记口径）

> **2026-09-24 批次 13：D5 批次完成类型拆分（纯函数＋夹具，生产零消费点，未接线）**：把 `BatchReconcileDecider`
> 原来无载荷的 `Complete` 拆成 `CompleteSucceeded`（全部项可证成功，唯一获准触发"完成后动作"/RunSpecified 收尾者）
> 与 `CompleteWithUnresolved`（……不携带任何成功/可收尾许可）；旧无载荷 `Complete` 已删除。两处指定分支已逐条裁定
> ……必改（首轮会诊）：未决判据必须先应用本拍 `ConfirmTerminal` 再分类……反例先行：两条原
> `_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed` 夹具改新语义后在旧实现下 **13 红／64 绿／77**
> （`batch13_red_core.trx`）；实现后定向三类 **77/77**（`batch13_green_core4.trx`）；助手全量 **1303 通过／2 跳过／
> 0 失败／1305**（`batch13_assistant_full3.trx`），基线为批次 9 的 1296／2，失败差集为空。……
> `gpt-6-sol`／medium 两轮各 1 次成功：首轮 1 必改／2 重要／1 建议全部处置，第二轮判**无必改、无未登记重要项**，
> 可据此通过 D5 批内验收。**残余**：决策器未接线、空批次 `Started` 守卫仅静态读码、真实收尾行为与服务端完成
> 条件只由文本审计陈述；D1–D4 未开工；生产入口门、真实 User 门与 R5.8 签署仍关闭。详见 R5.3 §24.108。

## 4. 会诊闭环裁定行（批次 18 会诊 F3 所要的就地锚点）

- 第 1 轮（gpt-6-sol/medium）：1 必改（本拍确认投影）／2 重要／1 建议——**全部处置**（必改已落代码并留注解）。
- 第 2 轮（gpt-6-sol/medium）：**无必改、无未登记重要项**，可据此通过 D5 批内验收（R2 口径收口）。
- 注：批次 13 原始 TRX/会诊报告本体在 `_batch13/` 与工具侧留档（未跟踪），本锚点撮要自 R5.3/交接稿提交态；
  现行代码终态（`CompleteSucceeded`/`CompleteWithUnresolved`、投影逻辑与「[D5 必改（gpt-6-sol 评审第 1 项）]」注解）
  已由批次 18 会诊第 1 轮独立核对一致。

## 5. 边界声明（R3）

- 本锚点不证明：批次 13 原始 TRX 字节级真实性、两轮会诊报告原文未被改动——两者以留档为准，批次 18 不做重放。
- 本锚点不断言「D5 无任何残余」：残余清单见 §24.108 与批次 18 证据文件 §3（全部归 D-E5/C13 等待决或接线面）。
