# 批次 21 SB21-1 收尾交接稿（2026-09-27；含原暂停快照）

> 下方“暂停时快照”及其待办记录 2026-09-26 的现场；本文件末尾“续办终态”是当前权威收口状态。

## 暂停时快照（历史）
- 分支 main-OldTeaBag-B168，HEAD＝6217dcc67（批次 20 冻结收口，**本批零提交**）。
- SB21-1（等待判定接线）进行中：BO-1 与 EV1-R1 已实现并转绿；**生产接线未实现**（反射守卫仍红，为预期红）。

## 暂停时已完成的验证（历史）
- 定向夹具 Batch21WiringRedTests：4 条中 **3 绿 1 红**（红＝DefaultConstructedHost_WiresLocalWaitStack，钉生产接线缺失，预期红）。
- 相邻回归（LocalWait|Batch21|OccupantLevel|StartupHandoff|Resume 过滤）：**277 过 / 1 失败（即上述预期红）**，无回归。

## 工作区未提交改动（全部本批范围内；提交一律 git commit --only）
1. **WorkflowRunner.cs**（+64/-7 行，含前两日已落的 BO-1 守卫）：
   - TryRegisterLocalWait 三处 try/catch（reference provider 异常 ~:1473-1485、scope provider 异常 ~:1517-1530、BuildAdmissionIdentity 身份构造越界）⇒ Park=true「登记未完成」零发送停驻；
   - **本会话新增**（~:568-571）：waitLocally 停驻分支把停驻原因追加到 run.Note（AppendNote＋Sanitize），钉死运行级留痕。这是为夹具「登记未完成＋异常」子串断言做的生产侧适配；CommitOutcome 原只把 Reason 写 NodeOutcomes。
2. **TaskCenterHost.Admission.cs**（+11 行，~:904-913）：ResolveOccupantLevels 在 _runs.List() 前加 EV1-R1 守卫——UnknownFiles 非空 ⇒ 级别保持未知＋TryLog 留痕（含「未解析记录」子串），预填级别事实不覆盖。
3. **Batch21WiringRedTests.cs**（新文件，未跟踪）：4 条反例先行夹具。本会话修了两处夹具自身缺陷：runsDirOf 反射字段名补 _runsDir（RunStore 实际字段名）；损坏台账文件名改为 run-corrupt.run.json（RunStore 枚举模式是 *.run.json，原名 run-corrupt.json 根本不进枚举）。

## 夹具合同（生产接线必须满足的反射面）
默认构造（admissionWired:false，生产形状）的 TaskCenterHost 必须有非空成员 **LocalWaitQueue**（LocalWaitQueueStore）与 **WaitDecisionSource**（门面等待结论判定来源；属性或字段、任意可见性均可）。注入点＝CreateRunner（TaskCenterHost.cs:982-997）。

## 暂停时计划的下一步（已由末尾终态更新）
1. **先解设计问题再动手**（见下节勘察结论）；方案定后经正常施工落接线，跑绿反射守卫。
2. 反向突变验证：改坏三处守卫看夹具变红。
3. 助手全量回归（基线 1475/2/0/1477，逐名差集）。
4. 验证会诊（≤8 轮；consult_run 必须 stream=true；首轮并入批次五十一 R16 挂账；台账 C:\Users\Administrator\.tools\zcode-relay\test\ledger-batch21.json；送审材料含 git status --porcelain＋未暂存 diff，区分本批文件与材料外变更）。
5. 声明面若变：CLAIM_SURFACE_REGENERATE=1 再生＋无 env 复跑。
6. git commit --only 本批三文件；落册 R5.3 §24.121＋交接稿批次行＋总计划状态行；刷新 task-relays 两文件。
7. 之后子批：SB21-2（BO-10/BO-12 代际边界）→ SB21-3（BO-4 facade 映射）→ SB21-4（BO-13/BO-11 停驻出口）。

## 关键勘察结论（省明日重复推导）
- 接缝是**同步**的：WorkflowRunnerOptions.ShouldRegisterLocalWait 为 Func<WorkflowNodeOccurrence,bool>（:159），在 RecordIntent **之前**调用（:714-719）——异步门面轮次不可能在这里现算。
- 门面侧全仓库**无任何构造** AdmissionResultKind.WaitLocally 的点（ArbitrationAdmissionService.cs:1545 仅比较 winnerResult.Kind）；R5.3 :3102 明载「生产零消费点、未接线」。
- WaitLocally 语义（R5.3 :3081-3096）：只登记等待、不含发送许可；MapAdmissionResultToBoundary 已分流为确定 Rejected 形状（TaskCenterHost.Admission.cs:1670-1673）；批次 14 残项：接线必须让停驻语义在游标/终态层可区分、不被 Rejected 路径吞并；RecomputeSuccessor 等待后重载可能跳过待执行节点（:3131）。
- 同步判定的候选数据源需在明日方案中定夺并过会诊；**不得**在方案未定前堆实现（R5.3 §17.4-A 纪律④）。

## 红线提醒
- 工作区还有大量历史遗留 .bak/.stale 与 2 个有意不提交的文档（mistletoe-session-relay-2026-09-24.md、unified-job-registry-master-plan.md）——**不提交/不删除/不回退**。
- 生产入口门、真实 User 门、R5.8 签署继续关闭；不改 E3/E4/E5/热键等未放行面。
- 交接时戳（UTC）：2026-09-26T16:14:58Z

## 续办终态（2026-09-27）

- **工作区**：分支 `main-OldTeaBag-B168`，基线 HEAD `6217dcc67f52e2d599257f56ca50f5cd22be1eb0`；SB21-1 变更按显式 `git commit --only` 文件清单收口。材料外 `.bak`/`.stale` 与 `mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md` 保留且不提交。
- **生产接线**：`TaskCenterHost` 装配 `LocalWaitQueueStore`＋`WaitDecisionSource`，`CreateRunner` 注入；类型化判定、身份绑定与队列/运行恢复一致性按 owner 有限扩展决定落地。现有生产公开构造的 `_admissionWired=true` 未改，`_successorAdmissionWired` 仍为 false；判定源在后者未开时返回 ContinueAdmission，因此本批未开放后继/等待消费路径。真实 User、R5.8 最终入口、E3/E4/E5 与热键门继续关闭。
- **定向夹具**：`Batch21WiringRedTests` 4/4 通过，TRX `_batch21/sb21-1-r8-review/final-targeted/batch21-wiring-4of4-final.trx`。
- **反向突变**：当前源码 11/11 named mutant 被指定 Fact 检出，11 次精确恢复与恢复态目标通过；首轮 4 个编译失败/无 TRX 的脚本尝试排除，修正后替代突变通过。见 `_batch21/sb21-1-r8-review/current-source-reverse-mutations.md`。
- **助手全量**：1479 通过／2 跳过／0 失败／1481；最终 claim regen 后全量帧：`_batch21/sb21-1-r8-review/final-full-after-claim/sb21-1-assistant-full-final-after-claim.trx`。逐名投影新增四个 Batch21 Facts、移除/改名 0，基线 1475/2/0/1477；未找到旧 HEAD 的 1477-case TRX，故这是源码名投影，不冒称旧 HEAD 重跑。两个 skip 是既有 opt-in P50 诊断。
- **声明面**：`CLAIM_SURFACE_REGENERATE=1` 再生 TRX 与无变量复跑 TRX 均通过；清单 590 行，SHA-256 `1F29901BF9C2C816BA167AE9E36CB9D03F64F33A6791CDB84ADA3DCD9E7DCAD6`，再生前后相同、无清单差异。证据：`_batch21/sb21-1-r8-review/claim-surface-regen/claim-surface-regen.trx`、`_batch21/sb21-1-r8-review/claim-surface-verify/claim-surface-verify-no-env.trx`。
- **会诊预算**：台账 `C:\Users\Administrator\.tools\zcode-relay\test\ledger-batch21.json` R1–R8 共 8 次已发出请求，含失败/超时口径；R8 为 GPT，剩余预算 0。R8 的 1 必改＋4 重要已逐项修复并由定向测试、反向突变或全量回归覆盖；无后续会诊请求。首轮并入的批次五十一 R16 挂账按原证据边界补验。
- **证据目录**：`_batch21/sb21-1-r8-review/` 保存送审快照、台账关联、反向突变日志/TRX、定向/全量 TRX、差集说明及声明面再生/复跑记录。
- **下一项**：只登记/提示 SB21-2（BO-10/BO-12）为后续子批；本交接不启动其实现。BO-6/BO-8 继续留 SB21-4，BO-4 与 BO-11/BO-13 按计划分批；不能把它们记作本批闭合。
