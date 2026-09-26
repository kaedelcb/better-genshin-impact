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
- **会诊预算**：台账 `C:\Users\Administrator\.tools\zcode-relay\test\ledger-batch21.json` R1–R8 共 8 次已发出请求，含失败/超时口径；R8 为 GPT，剩余预算 0。R8 的 1 必改＋4 重要已逐项按原等级处置，并有定向测试、反向突变或全量回归证据；无后续会诊请求。批次五十一 R16 挂账在 R1 并入，但 R2 指出原证据缺提交锚点/对象哈希/TRX。封顶后补核：测试源自提交 `029cbff66068bb53418727706356c620bc810bc1`，当前与该提交 blob 相同（`fe254d499b9f43590d1112b657000679a3e4ecdf`），定向复跑 104/104；TRX 与边界见 `sb21-1-r8-review/test-evidence.md`。这是可复核的机械证据，不追称历史 R16 曾取得会诊结论；本子批预算已耗尽，不再请求会诊。
- **证据目录**：`_batch21/sb21-1-r8-review/` 保存送审快照、台账关联、反向突变日志/TRX、定向/全量 TRX、差集说明及声明面再生/复跑记录。
- **下一项**：只登记/提示 SB21-2（BO-10/BO-12）为后续子批；本交接不启动其实现。BO-6/BO-8 继续留 SB21-4，BO-4 与 BO-11/BO-13 按计划分批；不能把它们记作本批闭合。

## Owner 批准的 R9 收口复核补记（2026-09-27）

- 台账 R1–R8 原预算 8 次。Owner 明确批准 1 次 GPT 只读收口复核，限定 R8 五项＋R2 R16 证据项；成功请求为 GPT `gpt-6-astra`／medium、`attempts=1`，累计已发 9 次（8＋1 次有限例外）。allowlist 路径预检失败发生在本地、未发送，不计次。R9 送审快照结果和后续本地补证见 `_batch21/sb21-1-r8-review/post-cap-gpt-review/gpt-r9-closeout-review.md`、`test-evidence.md`。
- R9 保留原等级：R8 反向突变证据必改、R2 R16 证据必改（部分仍开于送审快照）；其余 4 个 R8 重要项获代码/夹具支持。送审后已修正漏写的身份源码 SHA，新增 11 组逐项 mutant/restored TRX 结果与 SHA 对照；补入完整 §24.63 U，并机械核验 R16 锚点 blob、源码 SHA、104/104 TRX。定向夹具复跑 4/4；ClaimSurface regen 与无 env 复跑均 1/1，manifest 仍 590 行、SHA-256 `1F29901BF9C2C816BA167AE9E36CB9D03F64F33A6791CDB84ADA3DCD9E7DCAD6`。
- 上述证据补充由本地独立检查，没有再次 GPT 复核，不声称顾问已认可补充。目标代码路径未再修改；助手全量既有结果仍为 1479/2/0/1481，1475/2/0/1477 仍是差集投影。**生产入口、真实 User、R5.8、E3/E4/E5、热键门继续关闭。**
- 若 owner 认为逐项 TRX 与 §24.63 U 锚点补证仍需外部确认，待 owner 明确批准有限额后，建议只对这两项证据补充做 1 次 GPT 收口复核；不自动发起。

## Owner 收口裁决（2026-09-27）

owner 本轮要求收尾 SB21-1，并按现有本地机械证据关闭本子批。R9 送审快照中的两项必改是证据材料缺口，已分别补齐：11 组有效 mutant/restored TRX 与命名断言、SHA 对照；完整 §24.63 U、R16 锚点/current blob、当前源码 SHA 与 104/104 TRX。独立解析核对 11 组突变均为指定 Fact Failed、恢复态均 Passed；定向 4/4、声明面再生/无变量复跑 1/1 均有留档。原发现等级不变。

R9 没有复核或接受上述后补材料，本裁决不声称顾问接受；不再为 SB21-1 追加会诊。历史 R16 独立会诊结果仍缺失，按 §24.63 U 保留给后续适用批次处理。生产入口、真实 User、R5.8、E3/E4/E5 与热键门继续关闭。
