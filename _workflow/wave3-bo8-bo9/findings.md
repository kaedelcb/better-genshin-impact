# 发现、实现与证据（findings）

## 本批性质
主线施工批（mode=code）：施工两项已登记 IMPORTANT（BO-8 R29、BO-9 R34 F5）与一项已登记建议级残项（BO-6/7-D1）。
反例先行：先在**开工字节的真实 Runner** 上证明失败/错误成功路径（红），再做最小实现，再重跑定向与全量回归、
反向突变与会诊。第 1 轮会诊给出 1 项 IMPORTANT（BO-9 链尾重入漏掉同轮前插 rescue）与 1 项建议级，均按原级处置：
IMPORTANT 由"重建未履行恢复义务"闭合并新增夹具＋突变钉死，建议级采纳（注释订正）。第 2 轮确认 IMPORTANT-1 按原级闭合，
同时提出 IMPORTANT-2（已清偿停驻标记仍生成前插义务），本批再修复并新增夹具＋突变；第 3 轮为最终验证轮。所有"通过"均为助手侧源码 +
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
该形态在本批"仅重入停驻点"的中间实现上可复现（`mutations-round3/bo9-tail-reentry-parks-only-v5`，命中
`WorkflowRunnerTests.cs:1411`），属本批新引入的收敛缺陷，按原级（IMPORTANT）处置而非降级。

**反例④（第 2 轮会诊 IMPORTANT-2，已清偿停驻标记仍生成前插义务）**：无循环计划 `[A,P,T]`（A@0 完成、P@0 先停驻后完成、旧标记保留）→ 暂停期间改为 `[A,X,P,T]`（X 新插）→ 恢复按完成锚 P 返回 T ⇒ T 完成后链尾重建若**不先剔除已清偿标记**，仍会为该旧标记扫描同轮更早节点而选出 `X` 并额外提交（`T→X`），此时并无任何存活停驻。修复＝在 `TryLocate` 命中后先 `if (HasCompletedOutcome(run, parkOcc)) continue;`（已清偿标记既不作重入点也不产生前插义务）；判别力由
`mutations-round3/bo9-settled-park-probe-v5`（P/F/P，命中 `:1472`）与夹具
`Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode` 证明。

**实现（最小，仍在同一推进层）**：真实链尾处（`occurrence is null`）按计划全序重建并重入**未履行的恢复义务**
（`TryRelocateToOutstandingObligation`），义务集合＝①仍存活停驻；②每个存活停驻**同轮次**、序号更早的**从未执行**出现
（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）；**已清偿（同身份已有完成结果）的停驻标记先被剔除**，既不作重入点也不产生前插义务（第 2 轮会诊 IMPORTANT-2）。守卫：**入口即链尾**（持久 `TailReached`，`enteredAtTail`）不由
本路径重开（保留 BO-6 的 Failed／零提交防御语义，`bo9-persisted-tail-reopen-v5` 钉死）；**终止性**由"每次重入都会
驱动返回的出现一次（完成或被过滤 ⇒ 离开义务集合；再次停驻或 unknown ⇒ 驱动立即返回）⇒ 义务集合严格收缩"给出
（这是收缩论证，不是"重入后必然执行一次"的绝对断言；重入后仍经暂停/取消/修订边界）。

**反例④（第 2 轮会诊 IMPORTANT-2，已清偿停驻标记仍生成前插义务）**：无循环计划 `[A,P,T]`（A@0 完成、P@0 先停驻后完成、旧标记保留）→ 暂停期间改为 `[A,X,P,T]`（X 新插）→ 恢复按完成锚 P 返回 T ⇒ T 完成后链尾重建若**不先剔除已清偿标记**，仍会为该旧标记扫描同轮更早节点而选出 `X` 并额外提交（`T→X`），此时并无任何存活停驻。修复＝在 `TryLocate` 命中后先 `if (HasCompletedOutcome(run, parkOcc)) continue;`（已清偿标记既不作重入点也不产生前插义务）。

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
  `fixed-v5/` 为第 1 轮修复后的中间运行（97/97、1566/2/0/1568）；第 2 轮 IMPORTANT-2 修复后的最终运行见 `final-v8/`
（定向 98/98、全量 1567 passed / 2 skipped / 0 failed / 1569）。

## 回归与对照
- 同条件主线基线（开工 HEAD，本批改动前，独立重跑）：助手全量 **1562 passed / 2 skipped / 0 failed / 1564**；
  定向三类（`LocalWaitParkingStateContractTests`＋`LocalWaitIdentityTranslationTests`＋`WorkflowRunnerTests`）93/93。
- 最终：定向 **98/98**；助手全量 **1567 passed / 2 skipped / 0 failed / 1569**（= 基线 1564 与本批 5 项新夹具）。
- testId 逐名对照（基线 → 最终）：**1564 unchanged / 5 added / 0 removed / 0 changed**
  （`final-v8/testid-comparison.json`）。5 added 恰为本批新增夹具，全部 Passed；无 removed、无 changed。
- 构建/测试命令统一带 `-p:DeployToBgiTools=false`；部署目标（x64 1158 文件 21:41:14.1381449Z；bin/Debug 2242 文件
  21:41:14.1215991Z）在开工与全部构建/测试后逐项复核未变（`deploy-target-before.txt`／`deploy-target-final.txt`）。
- 未观察到的部分不写入结论：本批未跑 BGI 主程序、未接真实 User、未做 facade 真实调度与实机；夹具均为无 scheduled loop 的计划。

## 反向突变（14 项，全部在最终字节上执行，不复用旧哈希）
`mutations-round3/`（机读记录 `mutation-records-round3.json`）。每项均有独立 baseline／build／mutant／restored 日志与 TRX，
`build_exit=baseline_exit=restored_exit=0`、`mutant_exit=1`、命中具名目标断言、源码逐字节恢复
（`original_sha256 == restored_sha256 == b0b3579bf289551d2f314b1bb9192284f75406d9fb1d8bfd9db2c66bcca6c1ae`）。

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
| bo9-settled-park-probe-v5 | 本批 BO-9 反例④（第 2 轮会诊 IMPORTANT-2） | 不剔除已清偿停驻标记，使其再次生成同轮前插义务 | `:1472` |
| bo9-persisted-tail-reopen-v5 | tail defensive fact | 去掉 `enteredAtTail` 守卫，重开持久链尾 | `:1070`（`Assert.Empty`） |

说明：`mutations/`（被中止的初版运行）、`mutations-final/`（12 项）与 `mutations-round2/`（13 项）均**不作为最终证据**；
最终证据为在最终字节上重跑的 `mutations-round3/`（14 项）。`-v5` 后缀表示"同一突变意图在新字节上重新绑定与重跑"。

## 会诊（本子批独立计数）
- 第 1 轮（首审，`gpt-6-astra` / `medium`，attempts=1，read-only，自动附本批 diff）：1 项 **IMPORTANT**（BO-9 链尾重入
  漏掉同轮前插 rescue ⇒ 假成功；即反例③）＋ 1 项**建议级**（D1 残余注释解释不准确）。计数 1/8。
- 处置：IMPORTANT-1 **已修复**（链尾重建未履行义务）＋新增夹具＋新增突变钉死判别力；建议级**采纳**（注释订正）。
- 第 2 轮（验证轮，计 2/8）：裁定原 IMPORTANT-1 按原级闭合，同时提出**新增 IMPORTANT-2**（已清偿停驻标记仍生成前插义务）；本批已修复并新增夹具＋突变（原级闭环候选），逐项见 `consultation/review-outcome-v2.md`。
- 第 3 轮（验证轮，计 3/8）：裁定 IMPORTANT-2 **已按原级闭合**、原 IMPORTANT-1 未回退、**未发现新的 MUST/IMPORTANT**（收尾原文「本批是否仍有未闭合的 MUST/IMPORTANT：否」）。新提出的一条**建议级**观察（3 处旧方法名 `TryRelocateToLivePark` 与 `:1495` 行尾残留措辞）按与 `BO-6/7-D1` 同族口径登记为 **`BO-9-D1`**，本批不改源码（裁决与全部证据绑定当时字节），留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次订正。逐项见 `consultation/review-outcome-v3.md`。
- 边界：会诊结论只覆盖本批材料；不代表实机、生产或真实 User 验收。
- 遗留建议级残项 **`BO-9-D1`**：`WorkflowRunner.cs` 4 处注释陈旧（旧方法名 ×3、`return candidate` 行尾措辞 ×1），第 3 轮明确评为建议级；修正条件为下一次重新绑定该文件哈希的批次（与 `BO-6/7-D1` 同族口径）。

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
