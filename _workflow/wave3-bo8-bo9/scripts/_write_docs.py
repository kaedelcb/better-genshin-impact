import io, json, os
base = r"_workflow/wave3-bo8-bo9"
def w(name, text):
    io.open(os.path.join(base, name), "w", encoding="utf-8", newline="\r\n").write(text)
    print("wrote", name, len(text))

context = """# wave3-bo8-bo9-2026-09-28 开工范围与目标

## 一句话结果
在茶包主线施工 R5 Wave3 剩余的两项原级未闭合 **IMPORTANT**——**BO-8 R29**（恢复点之后推进段无完成过滤）与
**BO-9 R34 F5**（多有效停驻跨轮次推进），并在同一批（同一份 `WorkflowRunner.cs` 哈希重绑定）内修正已登记的
**BO-6/7-D1** 建议级注释陈旧残项。

## 范围与依赖顺序
- 只施工 BO-8、BO-9 与 BO-6/7-D1。
- 不施工 R5.6、R6.1、R6 diff guard；不提前集成任何其他并行成果。
- 不重开：BO-6 R19／R21 F4、BO-7 R21 F1/F2（已原级闭合）、BO-13、SB21-3 BO-4、SB21-2 BO-10/12。
- **BO-8 与 BO-9 同属 `DriveAsync` 推进层**（BO-8＝推进段按稳定身份跳过已完成出现；BO-9＝真实链尾仍存活的
  停驻义务按计划全序重入驱动），因此必须在同一子批内按依赖顺序收敛，不得拆成互相绕过的子批。
- 生产门：BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程继续关闭；本批不声称实机或生产验收。

## 开工状态（opening.json）
- 分支 `main-OldTeaBag-B168`，开工 HEAD `f47b57b1cba90e78624ae6b6d2236aafd402f0e1`。
- 待审源码（工作区 CRLF 形态）：`WorkflowRunner.cs` = `5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`；
  `WorkflowRunnerTests.cs` = `b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711`。
- 上一批主线接收已完成（`1945913a4`／`8a014cf78`／`f47b57b1c`），BO-6/BO-7 已并入主线并经集成回归与 8 项反向突变重跑；
  其证据绑定当时的 `WorkflowRunner.cs` 字节，**本批改动该文件后旧哈希与旧突变绑定失效**，故全部受影响突变在本批重做。
- 材料外既有未提交文档（本批完整保护、不提交、不修改）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、
  `Docs/design/unified-job-registry-master-plan.md`。
- 部署目标开工读数：`BetterGenshinImpact/bin/x64/.../Tools/MultiplayerHoeingAssistant`（1158 文件，
  目录 LastWriteTimeUtc 2026-09-26T21:41:14.1381449Z）与 `bin/Debug/...` 同名目录（2242 文件，同 21:41:14.1215991Z）。

## 子 Agent 评估（按要求记录判断）
不使用固定版本只读子 Agent。理由：本批改动集中在 `WorkflowRunner.DriveAsync/Relocate/RecomputeSuccessor` 单一共享状态链，
按用户指令不得由多子 Agent 并行改代码；只读子 Agent 能提供的信息与主执行者必须自行完成的代码通读、反例构造、
突变与回归重叠，且项目规则明示 helper／只读报告不能替代真实 Runner 端到端证据（BO-8/BO-9 的闭合判据正是真实驱动）。
故净收益为负，记录为 `not_used`。

## 执行入口
`python -B tools/mistletoe/workflow.py begin|audit|verify`（v2 manifest、开工矩阵、送审材料与工作区差异）
与 `python -B tools/mistletoe/deliveries.py --root .`（并行成果发现，只读）。证据、反向突变与会诊材料均在
`_workflow/wave3-bo8-bo9/`；工具只做机械核验，结论由源码、TRX 与人工复核支持。
"""

findings = """# 发现、实现与证据（findings）

## 本批性质
主线施工批（mode=code）：施工两项已登记 IMPORTANT（BO-8 R29、BO-9 R34 F5）与一项已登记建议级残项（BO-6/7-D1）。
反例先行：先在**开工字节的真实 Runner** 上证明失败/错误成功路径（红），再做最小实现，再重跑定向与全量回归、
反向突变与会诊。所有"通过"均为助手侧源码 + 真实 Runner 驱动夹具 + 假边界证据，**不是**实机或生产验收。

## BO-8 R29 IMPORTANT（恢复点之后推进段无完成过滤）
**缺陷（开工字节可复现）**：`RecomputeSuccessor` 只保证**返回点本身**未完成；其后的线性推进段
（`DriveAsync` 的 `plan.Next`/`Relocate` 前进）当时没有任何完成性跳过——只有"恢复记录含 `waitLocally` 历史"才开过滤
（BO-6 的 `filterCompletedParkRecovery` 开关）。因此**纯完成重排**（无停驻）的生产可达路径会把已完成节点二次提交。

**反例①（真实 Runner，`RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted`）**：
旧计划 `[n3,n2,Y]` 已执行 `n3`、`n2`；`n2` 终态观察点外部改流为 `[n2,X,n3,Y]`（`ProcessBoundaryActions` 在驱动中热重载，
生产可达）⇒ 锚＝最后完成 `n2` ⇒ `candidate = Next(n2) = X` ⇒ 返回 X ⇒ X 完成后推进撞**已完成** `n3` ⇒ **n3 被第二次提交**。
开工字节实测提交序列 = `["n3","n2","X","n3","Y"]`（`_workflow/wave3-bo8-bo9/red-final/bo8-bo9-red-final.trx`，3 失败/3）。

**实现（最小）**：`DriveAsync` 推进段的完成过滤改为**无条件**（去掉 park 历史开关，删 `filterCompletedDuringParkRecovery`），
判据仍是稳定出现身份 `(NodeId, Occurrence, LoopIteration)`（`HasCompletedOutcome`），停驻标记 `waitLocally` 不算完成
⇒ 停驻义务照常重驱（BO-6 语义在该层保持不变）。修复后同一夹具提交序列 = `["n3","n2","X","Y"]`，`n3` 恰一次，
运行按真实链尾收敛 `Succeeded`，收尾恰一次。

## BO-9 R34 F5 IMPORTANT（多有效停驻跨轮次推进）
**缺陷（开工字节可复现）**：多个仍有效（可定位、未完成）的停驻分处不同轮次、且当前修订已**无循环定义**时，
线性推进只能到达全序最早者；`Next` 到链尾即结束，**较晚轮次的停驻义务永不被重驱**（R34 F5 的
`[A] + A@loop1/A@loop2` 形状）。开工字节的后果是"跳步"：义务未被清偿，仅由 BO-6 的聚合保护把运行压成 `Failed`
（不再是假成功，但停驻义务被吞、用户无法在会话内清偿）。

**反例②（真实 Runner，`Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder`）**：
计划 `[A]`（无循环），`NodeOutcomes` = `A@loop0 succeeded` + `A@loop1/A@loop2/A@loop3 waitLocally`。
开工字节实测只驱动 `A@loop1`（`[("A",1)]`），`A@loop2`／`A@loop3` 从未被驱动。

**实现（最小，仍在同一推进层）**：真实链尾处（`occurrence is null`）若仍存在**存活停驻**
（`TryRelocateToLivePark`：可在当前修订定位、且无完成结果），按**计划全序 (LoopIteration, SequenceIndex) 最早**重入驱动，
而不是按链尾放行。守卫两条：
1. **入口即链尾**（持久 `TailReached`，`enteredAtTail`）**不由本路径重开**——保留 BO-6 防御语义：持久不一致仍
   以聚合 `Failed` 收口、零提交、不执行收尾（`bo9-persisted-tail-reopen-v5` 突变证明该守卫有判别力）。
2. **每次重入必然驱动该出现一次**（成功/失败/被过滤 ⇒ 产生完成结果而退出存活集；再次停驻 ⇒ 立即返回
   `LocalWaitParking`），故重入严格消耗义务、可终止；续跑不引入新循环路径。

修复后同一夹具：`[("A",1),("A",2),("A",3)]` 各恰一次、`A@loop0` 不重跑、`Succeeded`、收尾一次；
另有持续等待分支 `Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess`：重入后仍要求本地等待 ⇒
`LocalWaitParking`、零收尾、`TailReached=false`、义务标记仍可定位且未完成。

## BO-6/7-D1 建议级（注释陈旧）——已在同一哈希重绑定批内修正
按登记口径逐处修正（不改行为、不改断言语义）：
- `WorkflowRunner.cs:1400–1403`（修正后行号）：下界注释不再写"**最后一条**可定位停驻点"，改为"**计划全序最早**"
  （R34 F5 已统一的口径），并说明"推进段穿越已完成出现"的历史理由已由 BO-8/BO-9 的稳定身份过滤消除。
- `:1486`（修正后在 `ParkedRescue` 文档注释）：不再写"取最后一条可定位停驻标记为基准"，改为"取**计划全序最早**"。
- `:1535–1537`（修正后）：删除"DriveAsync/Relocate 无完成跳过"这一已失效的下界理由，改为"避免越过修订语义下由
  candidate 承载的出现"，并注明该下界不再承担"防止穿越已完成出现"的职责。
- 另同步 `RecomputeSuccessor` 两条不变量、`ParkedRescue` 返回语义、`DriveAsync` 的 BO-6 作用域注释。
**影响**：`WorkflowRunner.cs` 字节改变 ⇒ 上一批 8 项反向突变绑定失效，本批在**新字节**上逐项重做（见下）。

## 反例先行证据（红 → 绿）
- **红（开工字节 + 本批最终夹具）**：`red-final/bo8-bo9-red-final.trx` = 3 failed / 0 passed / 3 total；
  `red-final/testproject-build.log` exit 0；`red-final/red-final-exits.json` 记录 `prefix_build_exit=0`、`prefix_test_exit=1`。
  红运行使用的**开工字节**由 `red-final/prefix-source.cs` 固定（SHA-256 `5470cfcb…`，等于 opening.json 的
  `source_sha256`），运行后源码已逐字节恢复为本批最终字节 `539dfe39…`。
  实测失败：BO-8 提交 `["n3","n2","X","n3","Y"]`；BO-9 两个夹具均只提交 `[(A,1)]`。
- **绿（最终字节）**：`fixed-v2/targeted-fixed.trx` 96/96；`fixed-v2/assistant-full-fixed.trx`
  1565 passed / 2 skipped / 0 failed / 1567；`fixed-v2/testproject-build.log` exit 0（0 error）。

## 回归与对照
- 同条件主线基线（开工 HEAD，本批改动前，独立重跑）：助手全量 **1562 passed / 2 skipped / 0 failed / 1564**；
  定向三类（`LocalWaitParkingStateContractTests`＋`LocalWaitIdentityTranslationTests`＋`WorkflowRunnerTests`）93/93。
- 最终：定向 **96/96**（93 + 本批 3 项新事实）；助手全量 **1565 passed / 2 skipped / 0 failed / 1567**。
- testId 逐名对照（基线 → 最终）：**1564 unchanged / 3 added / 0 removed / 0 changed**
  （`fixed-v2/testid-comparison.json`）。3 added 恰为本批新增夹具，全部 Passed；无 removed、无 changed。
- 构建/测试命令统一带 `-p:DeployToBgiTools=false`；部署目标（x64 1158 文件 21:41:14.1381449Z；bin/Debug 2242 文件
  21:41:14.1215991Z）在开工与全部构建/测试后逐项复核未变（`deploy-target-before.txt`／`deploy-target-after.txt`）。
- 未观察到的部分不写入结论：本批未跑 BGI 主程序、未接真实 User、未做 facade 真实调度与实机。

## 反向突变（12 项，全部在新字节上重做，不复用旧哈希）
`mutations-final/`（机读记录 `mutation-records-final.json`）。每项均有独立 baseline／build／mutant／restored 日志与 TRX，
`build_exit=baseline_exit=restored_exit=0`、`mutant_exit=1`、命中具名目标断言、源码逐字节恢复
（`original_sha256 == restored_sha256 == 539dfe39e281305b4b8181def55e7d5f1d5ee9f27035137de0f587f3f3a82087`）。

| id | 目标测试 | 突变量 | 命中 |
|---|---|---|---|
| bo6-resume-live-park-v5 | mixed Runner fact | 关闭显式恢复的停驻重算 | `WorkflowRunnerTests.cs:1008` |
| bo6-completed-filter-v5 | mixed Runner fact | 关闭推进段完成过滤（模式按 BO-8 措辞更新） | `:1008` |
| bo6-tail-fail-closed-v5 | tail defensive fact | 关闭未清偿停驻的链尾 fail-closed | `:1071` |
| bo6-tail-persisted-failure-v5 | tail defensive fact | 移除 Failed 分支的持久化写 | `:1080` |
| bo7-candidate-first-v5 | candidate/rescue 跨轮 | 反转 candidate／rescue 全序比较 | `:1131` |
| bo7-rescue-first-v5 | rescue 早于下一轮 candidate | 让 candidate 无条件覆盖更早 rescue | `:1173` |
| bo7-earliest-park-v5 | mixed Runner fact | 反转同轮多停驻的计划全序选择 | `:1008` |
| bo7-stable-identity-v5 | mixed Runner fact | 把可变 SequenceIndex 折进稳定完成身份 | `:1008` |
| bo8-completion-filter-v5 | 本批 BO-8 fact | 关闭推进段完成过滤（与 bo6-completed-filter-v5 共享同一 mutant 源码） | `:1222` |
| bo9-tail-reentry-disabled-v5 | 本批 BO-9 fact | 关闭链尾停驻重入 | `:1283` |
| bo9-reentry-order-last-v5 | 本批 BO-9 fact | 重入改取计划全序最后者 | `:1283` |
| bo9-persisted-tail-reopen-v5 | tail defensive fact | 去掉 `enteredAtTail` 守卫，重开持久链尾 | `:1070`（`Assert.Empty`） |

说明：`_workflow/wave3-bo8-bo9/mutations/` 是一次**被中止的初版运行**（脚本在 11/12 处中断、未写出记录文件），
已由 `mutations-final/` 取代，不作为证据；`mutations-v2` 未使用。`bo7-...-v5`／`bo6-...-v5` 的 `-v5` 后缀表示
"同一突变意图在**新字节**上重新绑定"，不是复用旧哈希。

## 未验证项与边界
- 只施工 BO-8、BO-9 与 BO-6/7-D1；R5.6、R6.1、R6 diff guard 未提前集成；未消费任何其他并行成果。
- 未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证；外部执行边界为 fake；生产门保持关闭。
- 本批不主张"停驻语义已生产可达"：`ShouldRegisterLocalWait` 生产恒 null 的既有结论未被本批改变。
- 不主张跨进程／断电耐久或并发压力结论；本批证据限于助手侧组件与真实 Runner 驱动夹具（单进程、确定性时钟/边界夹具）。

## 范围外变更（材料外）
`git status --porcelain` 与本批 scoped diff 的差异见 `outside_changes` 段落：本批只改
`WorkflowRunner.cs`、`WorkflowRunnerTests.cs`、`ClaimSurfaceManifest.txt`（强制再生）、本批 `_workflow/wave3-bo8-bo9/**`
与状态文档（R5.3、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`、
并行成果索引/台账）。工作区其余未跟踪/已跟踪改动均为**材料外**既有内容（历史批次证据、`.bak`/`.stale`、日志、TestResults、
DLL/工具输出、截图等），本批不改写、不删除、不提交；两份既有未提交设计文档
（`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`）保持原样。
"""

budget = """# 会诊预算（本批子批）

- 固定渠道：既有 GPT 会诊工具（`gpt_workspace`，mode=review，read-only）；固定模型/强度：`gpt-6-astra` / `medium`。
- 子批上限：**8 次累计请求**（覆盖所有渠道的首审、复审、验证与收口请求；已发出的失败/超时同样计次；
  本地预检拦截且未发出的不计；代码修复与回归不计次）。
- **本子批计数独立**：BO-6/BO-7 独立子批的 8/8 + owner 批准的 2/2 不重置、不继承、不被本批消耗。
- 当前计数：见 `consultation/review-outcome-*.md` 与 `consultation/dispatch-log.json`。
- 处置纪律：会诊给出的重要/必改不得由施工方降级；有未闭合项必须闭环或形成 owner 检查点；
  全称否定必须附反例尝试记录；"部分采纳"必须写明生效层次；新增关键断言须做反向突变。
- 送审材料：`workflow.py audit --stage review` + `verify` 通过后的本地 packet（含 objective/findings/budget、
  两个源码全文、突变记录与工作区差异），并做渠道容量预估；超限或同范围经允许重试仍无报告且输入达有效窗口
  三分之一时，按设施规则改用独立本地只读通道并保持模型/强度。
"""

w("context.md", context)
w("findings.md", findings)
w("budget.md", budget)
