import io, os
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
- **BO-8 与 BO-9 同属 `DriveAsync` 推进层**（BO-8＝推进段按稳定身份跳过已完成出现；BO-9＝真实链尾按计划全序重建并
  重入**未履行的恢复义务**，含仍存活停驻与其同轮前插未执行出现），必须同批收敛，不得拆成互相绕过的子批。
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
反向突变与会诊。第 1 轮会诊给出 1 项 IMPORTANT（BO-9 链尾重入漏掉同轮前插 rescue）与 1 项建议级，均按原级处置：
IMPORTANT 由"重建未履行恢复义务"闭合并新增夹具＋突变钉死，建议级采纳（注释订正）。所有"通过"均为助手侧源码 +
真实 Runner 驱动夹具 + 假边界证据，**不是**实机或生产验收。

## BO-8 R29 IMPORTANT（恢复点之后推进段无完成过滤）
**缺陷（开工字节可复现）**：`RecomputeSuccessor` 只保证**返回点本身**未完成；其后的线性推进段
（`DriveAsync` 的 `plan.Next`/`Relocate` 前进）当时没有任何完成性跳过——只有"恢复记录含 `waitLocally` 历史"才开过滤
（BO-6 的 `filterCompletedDuringParkRecovery` 开关）。因此**纯完成重排**（无停驻）的生产可达路径会把已完成节点二次提交。

**反例①（真实 Runner，`RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted`）**：
旧计划 `[n3,n2,Y]` 已执行 `n3`、`n2`；`n2` 终态观察点外部改流为 `[n2,X,n3,Y]`（`ProcessBoundaryActions` 在驱动中热重载，
生产可达）⇒ 锚＝最后完成 `n2` ⇒ `candidate = Next(n2) = X` ⇒ 返回 X ⇒ X 完成后推进撞**已完成** `n3` ⇒ **n3 被第二次提交**。
开工字节实测提交序列 = `["n3","n2","X","n3","Y"]`（`red-final/bo8-bo9-red-final.trx`，3 failed／3）。

**实现（最小）**：`DriveAsync` 推进段的完成过滤改为**无条件**（去掉 park 历史开关，删 `filterCompletedDuringParkRecovery`），
判据仍是稳定出现身份 `(NodeId, Occurrence, LoopIteration)`（`HasCompletedOutcome`），停驻标记 `waitLocally` 不算完成
⇒ 停驻义务照常重驱（BO-6 语义在该层保持不变）。修复后同一夹具提交序列 = `["n3","n2","X","Y"]`，`n3` 恰一次，
运行按真实链尾收敛 `Succeeded`，收尾恰一次。
**口径说明（第 1 轮会诊要求）**：`HasCompletedOutcome` 是"非 `waitLocally`"谓词，不是完成词白名单；其安全性依赖
既有 Unknown 禁恢复守卫，不得把它描述为脱离调用上下文成立的完成分类器。

## BO-9 R34 F5 IMPORTANT（多有效停驻跨轮次推进）
**缺陷（开工字节可复现）**：多个仍有效（可定位、未完成）的停驻分处不同轮次、且当前修订已**无循环定义**时，
线性推进只能到达全序最早者；`Next` 到链尾即结束，**较晚轮次的停驻义务永不被重驱**（R34 F5 的
`[A] + A@loop1/A@loop2/A@loop3` 形状）。开工字节只驱动 `A@loop1`，运行被 BO-6 聚合保护压成 `Failed`（跳步）。

**反例③（第 1 轮会诊 IMPORTANT-1，多节点无循环 candidate/rescue 跨轮）**：计划 `[A,P]`（无循环定义）+ `A@0` 已完成、
`P@1/P@2` 仍有效停驻 ⇒ `RecomputeSuccessor` 取 `candidate=P@0`、`rescue=A@1`（`P@1` 同轮前插的从未执行出现），
按 R24 全序返回较早的 `candidate`，并依赖线性推进"自然到达"较晚者；无循环时 `Next` 到链尾即 null ⇒
若链尾重入**只**恢复停驻点，`A@1` 与其后 `P@2` 同轮的 `A@2` 永不被驱动，停驻清偿后运行**假成功**并执行收尾。
该形态在本批"仅重入停驻点"的中间实现上可复现（`mutations-round2/bo9-tail-reentry-parks-only-v5`，命中
`WorkflowRunnerTests.cs:1411`），属本批新引入的收敛缺陷，按原级（IMPORTANT）处置而非降级。

**实现（最小，仍在同一推进层）**：真实链尾处（`occurrence is null`）按计划全序重建并重入**未履行的恢复义务**
（`TryRelocateToOutstandingObligation`），义务集合＝①仍存活停驻；②每个存活停驻**同轮次**、序号更早的**从未执行**出现
（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）。守卫：**入口即链尾**（持久 `TailReached`，`enteredAtTail`）不由
本路径重开（保留 BO-6 的 Failed／零提交防御语义，`bo9-persisted-tail-reopen-v5` 钉死）；**终止性**由"每次重入都会
驱动返回的出现一次（完成或被过滤 ⇒ 离开义务集合；再次停驻或 unknown ⇒ 驱动立即返回）⇒ 义务集合严格收缩"给出
（这是收缩论证，不是"重入后必然执行一次"的绝对断言；重入后仍经暂停/取消/修订边界）。

修复后：反例②提交 `[(A,1),(A,2),(A,3)]` 各恰一次、`A@loop0` 不重跑、`Succeeded`；反例③提交
`[(P,0),(A,1),(P,1),(A,2),(P,2)]`（计划全序，`A@0` 不重跑）、`Succeeded`、收尾一次；持续等待分支
`Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess` 回到 `LocalWaitParking`、零收尾、
`TailReached=false`、义务标记仍可定位且未完成。

## BO-6/7-D1 建议级（注释陈旧）——已在同一哈希重绑定批内修正
按登记口径逐处修正（不改行为、不改断言语义）：
- `:1400–1403`（修正后行号）：下界注释不再写"**最后一条**可定位停驻点"，改为"**计划全序最早**"（R34 F5 已统一口径），
  并说明"推进段穿越已完成出现"的历史理由已由 BO-8/BO-9 的稳定身份过滤消除。
- `:1486`：`ParkedRescue` 文档注释不再写"取最后一条可定位停驻标记为基准"，改为"取**计划全序最早**"，并把探测口径
  改述为"直接构造该停驻所在轮次的链首出现再向前探查"（第 1 轮会诊建议-2）。
- `:1535–1537`：删除"DriveAsync/Relocate 无完成跳过"这一已失效的下界理由，改为"避免越过修订语义下由 candidate 承载的出现"，
  并注明该下界不再承担"防止穿越已完成出现"的职责。
- 另同步 `RecomputeSuccessor` 两条不变量、`ParkedRescue` 返回语义、R24 全序说明（补"无循环时由链尾重建义务"）与
  `DriveAsync` 的作用域注释。
**影响**：`WorkflowRunner.cs` 字节改变 ⇒ 上一批 8 项反向突变绑定失效，本批在**新字节**上逐项重做（见下）。

## 反例先行证据（红 → 绿）
- **红（开工字节 + 本批最终夹具）**：`red-final/bo8-bo9-red-final.trx` = 3 failed / 0 passed / 3 total；
  `red-final/testproject-build.log` exit 0；`red-final/red-final-exits.json` 记录 `prefix_build_exit=0`、`prefix_test_exit=1`。
  红运行使用的**开工字节**由 `red-final/prefix-source.cs` 固定（SHA-256 `5470cfcb…`，等于 opening.json 的
  `source_sha256`），运行后源码已逐字节恢复。实测失败：BO-8 提交 `["n3","n2","X","n3","Y"]`；BO-9 两个夹具均只提交 `[(A,1)]`。
  （该红运行早于第 1 轮会诊新增的反例③夹具；反例③的判别力由 `mutations-round2/bo9-tail-reentry-parks-only-v5` 钉死。）
- **绿（最终字节）**：`final-v3/targeted-final.trx` 96/96 与 `final-v3/assistant-full-final.trx` 1565/2/0/1567 为第 1 轮送审版本；
  第 1 轮 IMPORTANT-1 修复后的最终运行见 `final-v7/`（定向 97/97、全量 1566 passed / 2 skipped / 0 failed / 1568）。
  `fixed-v5/` 为修复后、文档未更新前的中间运行（97/97、1566/2/0/1568）。

## 回归与对照
- 同条件主线基线（开工 HEAD，本批改动前，独立重跑）：助手全量 **1562 passed / 2 skipped / 0 failed / 1564**；
  定向三类（`LocalWaitParkingStateContractTests`＋`LocalWaitIdentityTranslationTests`＋`WorkflowRunnerTests`）93/93。
- 最终：定向 **97/97**；助手全量 **1566 passed / 2 skipped / 0 failed / 1568**（= 基线 1564 与本批 4 项新夹具）。
- testId 逐名对照（基线 → 最终）：**1564 unchanged / 4 added / 0 removed / 0 changed**
  （`final-v7/testid-comparison.json`）。4 added 恰为本批新增夹具，全部 Passed；无 removed、无 changed。
- 构建/测试命令统一带 `-p:DeployToBgiTools=false`；部署目标（x64 1158 文件 21:41:14.1381449Z；bin/Debug 2242 文件
  21:41:14.1215991Z）在开工与全部构建/测试后逐项复核未变（`deploy-target-before.txt`／`deploy-target-final.txt`）。
- 未观察到的部分不写入结论：本批未跑 BGI 主程序、未接真实 User、未做 facade 真实调度与实机；夹具均为无 scheduled loop 的计划。

## 反向突变（13 项，全部在最终字节上执行，不复用旧哈希）
`mutations-round2/`（机读记录 `mutation-records-round2.json`）。每项均有独立 baseline／build／mutant／restored 日志与 TRX，
`build_exit=baseline_exit=restored_exit=0`、`mutant_exit=1`、命中具名目标断言、源码逐字节恢复
（`original_sha256 == restored_sha256 == c71db8647501baf98f0de5cb4fab6d33cd63704b7e868ab17f8de7bf4bde0ae1`）。

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
| bo9-tail-reentry-disabled-v5 | 本批 BO-9 反例② | 关闭链尾义务重入 | `:1283` |
| bo9-reentry-order-last-v5 | 本批 BO-9 反例② | 重入改取计划全序最后者 | `:1283` |
| bo9-tail-reentry-parks-only-v5 | 本批 BO-9 反例③（第 1 轮会诊 IMPORTANT-1） | 链尾只重入停驻点、丢掉同轮前插未执行出现 | `:1411` |
| bo9-persisted-tail-reopen-v5 | tail defensive fact | 去掉 `enteredAtTail` 守卫，重开持久链尾 | `:1070`（`Assert.Empty`） |

说明：`mutations/`（被中止的初版运行）与 `mutations-final/`（12 项，第 1 轮会诊前的版本）均**不作为最终证据**，
已由在最终字节上重跑的 `mutations-round2/`（13 项）取代；`-v5` 后缀表示"同一突变意图在新字节上重新绑定与重跑"。

## 会诊（本子批独立计数）
- 第 1 轮（首审，`gpt-6-astra` / `medium`，attempts=1，read-only，自动附本批 diff）：1 项 **IMPORTANT**（BO-9 链尾重入
  漏掉同轮前插 rescue ⇒ 假成功；即反例③）＋ 1 项**建议级**（D1 残余注释解释不准确）。计数 1/8。
- 处置：IMPORTANT-1 **已修复**（链尾重建未履行义务）＋新增夹具＋新增突变钉死判别力；建议级**采纳**（注释订正）。
- 第 2 轮（验证轮）：逐项结论见 `consultation/review-outcome-v2.md`，计数以该文件记录为准。
- 边界：会诊结论只覆盖本批材料；不代表实机、生产或真实 User 验收。

## 未验证项与边界
- 只施工 BO-8、BO-9 与 BO-6/7-D1；R5.6、R6.1、R6 diff guard 未提前集成；未消费任何其他并行成果。
- 未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证；外部执行边界为 fake；生产门保持关闭。
- 本批不主张"停驻语义已生产可达"：`ShouldRegisterLocalWait` 生产恒 null 的既有结论未被本批改变。
- 不主张跨进程／断电耐久或并发压力结论；有循环定义时 `LastScheduledRoundWait` 的全部交错组合未被本批夹具覆盖。
- 部署目标读数一致只支持"这些计数与时间戳未变"，不等于逐文件内容字节级未写入的证明。

## 范围外变更（材料外）
本批只改 `WorkflowRunner.cs`、`WorkflowRunnerTests.cs`、`ClaimSurfaceManifest.txt`（强制再生）、本批
`_workflow/wave3-bo8-bo9/**` 与状态文档（R5.3、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、
`槲寄生调度器总计划.md`、并行成果索引/台账）。工作区其余未跟踪/已跟踪改动均为**材料外**既有内容（历史批次证据、
`.bak`/`.stale`、日志、TestResults、DLL/工具输出、截图等），本批不改写、不删除、不提交；两份既有未提交设计文档
（`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`）保持原样。
"""

budget = """# 会诊预算（本批子批）

- 固定渠道：既有 GPT 会诊工具（`gpt_workspace`，read-only，自动附本批工作区差异）；固定模型/强度：`gpt-6-astra` / `medium`。
- 子批上限：**8 次累计请求**（覆盖所有渠道的首审、复审、验证与收口请求；已发出的失败/超时同样计次；
  本地预检拦截且未发出的不计；代码修复与回归不计次）。
- **本子批计数独立**：BO-6/BO-7 独立子批的 8/8 + owner 批准的 2/2 不重置、不继承、不被本批消耗。
- 计数：第 1 轮首审 1 次（1 IMPORTANT ＋ 1 建议级，一次成功返回）；第 2 轮验证 1 次 —— 累计 2/8。
  逐轮原始结论见 `consultation/review-outcome-v1.md`、`review-outcome-v2.md`。
- 处置纪律：会诊给出的重要/必改不得由施工方降级；有未闭合项必须闭环或形成 owner 检查点；
  全称否定必须附反例尝试记录；"部分采纳"必须写明生效层次；新增关键断言须做反向突变。
- 送审材料：`workflow.py audit --stage review` + `verify` 通过后的本地 packet（含 objective/findings/budget、
  两个源码全文、清单与 R5.3 摘录、突变记录与工作区差异），并做渠道容量预估（`consultation/preflight-*.json`）；
  超限或同范围经允许重试仍无报告且输入达有效窗口三分之一时，按设施规则改用独立本地只读通道并保持模型/强度。
"""
w("context.md", context)
w("findings.md", findings)
w("budget.md", budget)
