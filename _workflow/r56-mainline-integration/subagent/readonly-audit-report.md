# 只读独立核查报告（固定开工 ref，只读子 Agent）

- **执行者**：本批派出的 1 个只读子 Agent（`/root/r56_readonly_audit`），固定开工 ref `8a3ee6c4c98e845b2988774fe9c3ab65343ce33e`，`read_only=true`。
- **边界**：未运行 `dotnet build`/`dotnet test`，未做任何写操作、未改分支/HEAD、未 `git add|commit|checkout|stash`，未读真实 `User`/`%APPDATA%`，未改任何配置。
- **用途**：在固定开工字节上核对两份来源交付的主张与主线实现的一致性、A–F 是否已有接线、导入 theory 的断言强度；其结论**不替代**主执行者的回归与反向突变证据，且已由主执行者按最终源码复核关键项。
- **交付原文**：见下（逐字保留；主执行者的处置登记见 `../findings.md` F-4/F-5/F-6 与 R5.3 §24.128.5）。

---

## 结论提要

- **Q2 直接结论**：交付 A「`TryRunProduction` 只有组件定义、没有外部调用方」在**当前主线仍然成立**。全仓 C# 有界搜索仅命中组件自身定义 `MigrationSwitchTransaction.cs:730`（及注释 `:79`）与夹具 `R56MigrationSwitchTransactionTests.cs:68,81,195,358,591,680`。
- **Q3 直接结论**：交付 B 的 A–F 六项**没有一项已被主线接线**，不存在「实际已满足却被标为未接线」的项；最接近边界的是 F/D 所依赖的 `UserConfigRootProvider` 注入与演练隔离校验，但准备包已自行把其用途限定为「演练隔离检查」，定性不变。
- **Q1 结论**：交付 A 覆盖矩阵 7 行主张与当前主线实现**逐行一致**，消费点/符号全部可定位；夹具证据强度有 3 项保留项，集中在 Q4。
- **一处证据锚定要点**：交付 A/B 引用的源码 SHA-256（`87905209E73AE3AE819D4382AA2AF643A942A2C4D2A687270C68D19670DB1ABA`，59,153 字节）是 **CRLF 工作区形态**哈希；主线工作区为 LF（58,135 字节，SHA-256 `C2D4A1224668E39E536A9DD0644AF665F1D805D3860F4A0D3D24926620DC807E`），二者 `git hash-object` 同为 `7beffd6384f5be6bddfb54ee9d8f17b1ec7e863d`，即**内容完全相同**，不构成差异。
- **阻断/未决**：本次为只读核查，未运行构建/测试，未产生新的运行证据；下列 Q5 项保持「无法核实」。

## Q1 逐行核对

版本锚定：主线 `main-OldTeaBag-B168` / `8a3ee6c4c98e845b2988774fe9c3ab65343ce33e`；`MigrationSwitchTransaction.cs` blob `7beffd6384f5be6bddfb54ee9d8f17b1ec7e863d`、`MigrationRehearsal.cs` blob `b84940d23f184f7700819fa8417976baf58a4504`（与交付 A 基线 `8a4b8d98…`、交付 B 固定提交 `5e7e7e22…` 的同名文件 blob **一致**）；`R56MigrationSwitchTransactionTests.cs` blob `cd121220de7aec7f623eac0c3c726636f06e2ce3`（=交付 A 交付版本，已暂存，HEAD 版为 `a7d6991c…`，即 +48 行尚未提交）。

| # | 主张 | 核实到的实际消费点/符号（file:line） | 判定 | 说明 |
|---|---|---|---|---|
| 1 | 全量配置快照：路径集合、字节、SHA-256 清单完整；新增 3 用例核对两文件集合、源/快照字节与 manifest 哈希，注入多余/缺失/篡改后提交被拒、保持 Activated 且无提交标记 | 生产：`MigrationSwitchTransaction.cs:364-405`（TakeSnapshot，含 `:380` 建快照目录、`:381` 枚举、`:384-385` 安全目标、`:386-390` 读字节+算哈希）、`:408-416`（清单哈希）、`:743-774`（VerifySnapshot）、`:581-605`（Commit）；夹具 `R56MigrationSwitchTransactionTests.cs:150-197`（集合 `:166`、逐文件哈希 `:167-168`、清单哈希 `:169`、源/快照字节 `:170-171`、注入 `:173-186`、原因 `:188`、提交闭锁 `:189-193`） | 一致（夹具强度有 3 项保留，见 Q4） | 「3 个用例」＝同一 theory 的 3 行 `InlineData`（`:151-153`），与报告口径一致 |
| 2 | 唯一提交标记、阶段落盘/读回、提交前禁止执行；未发现生产调用方消费 `TryRunProduction` | `:903-913`（WriteManifest：tmp+`File.Move(overwrite)`）、`:599-601`（写 marker→Committed）、`:850-859`/`:843-847`（读回+校验）、`:713-727`（授权：须 Committed+marker 匹配+无 blocked）、`:730-740`（授权与执行同一临界区）；夹具 `:64-83` | 一致 | 与 Q2 结论互补：组件合同成立，真实生产消费链不存在 |
| 3 | 静止窗口绑定实例会话与代次；释放/重取/重启使旧资格失效；委托桩验证；真实 BGI 控制面静止窗口未接线 | `:96`（会话 ID）、`:100`（代次）、`:339-356`（取窗口+持久化会话/时刻/代次）、`:373-374`（采集期须有存续窗口）、`:593-596`（提交须同会话同代次）、`:951-957`（释放并使资格失效）、`:960-975`（重取）；`MigrationRehearsal.cs:114,219`（NoopQuiet）；夹具 `:122-149`、`:477-519` | 一致 | 生产侧唯一非测试注入点就是演练的 NoopQuiet |
| 4 | 快照/引用更新/激活/提交/回滚失败与恢复、半完成保守阻断；引用更新与激活实际消费者不存在；`CrashAtAnyStage…` 是阶段回调抛异常、不冒充进程崩溃 | `:608-644`（Rollback，先落 RollingBack `:635-639`）、`:647-667`（CompleteRollback）、`:670-711`（RecoverOnStart）、`:861-872`（MarkBlocked）、`:495-511`（Mark* 仅阶段推进，无真实服务调用）；夹具 `CrashAtAnyStage_NeverLeavesExecutableState` `:1041-1073`、`Recovery_*` `:713,784,813` | 一致 | 夹具确为 stageHook 抛异常（`:1051-1054`），且用探针区分 `lock_not_held`（`:1067-1070`），与报告表述相符 |
| 5 | 回滚恢复修改/删除文件、只删本事务新增、保留无关文件、幂等续做 | `:915-929`（按清单恢复字节）、`:935-948`（只删归属 Added）、`:616`（已 RolledBack 幂等）、`:617-629`（基线未完成的安全中止）、`:682`/`:700-709`（恢复幂等续做）；夹具 `:278`（`Rollback_DeletesOnlyRecordedAdditions_KeepsOtherWritersFiles`）、`:713`、`:377`、`:398` | 一致 | 无关文件保留由「无记录⇒不删除」+基线集合驱动实现 |
| 6 | UI→Host→`MigrationRehearsal.Run` 委托入口；演练只在新建独立根生成 `a.json`、`sub/b.json` 并模拟阶段与回滚；真实引用更新、candidate/active 与助手侧激活元数据恢复未接线 | `TaskCenterPanelViewModel.cs:74-84`（`:78` 调 Host）、`TaskCenterHost.Admission.cs:789-827`（`:824` 调演练）、`MigrationRehearsal.cs:48-185`（`:62-64` 独占根、`:108-110` 两个代表文件、`:123-124` 登记变更、`:127,130` 阶段推进、`:141-143` 真实变更+回滚）；candidate 侧仅为**只读守卫**：`TaskCenterHost.cs:188-198,248-249,907-909,1068-1070`、`TaskCenterPanelViewModel.cs:175-177,407-414` | 一致 | 「代理/候选」机制在主线是禁写禁启动，不是激活写入；无 candidate→active 写入路径 |
| 7 | 演练根隔离真实 User 根、路径身份与异常拒绝；临时根夹具覆盖绝对本地路径、重叠拒绝、重解析点/设备命名空间与失败闭锁；真实 User、junction/SUBST/8.3、部署机最终路径身份未验证 | `MigrationRehearsal.cs:59-100`（本地盘符绝对路径 `:59`、与 User 根最终身份比较 `:80-89`、重解析点 `:91`、须为新建独占目录 `:93-94`）、`MigrationSwitchTransaction.cs:127-161`（ValidateRoots：扩展/设备前缀 `:133-134`、同卷 `:138-141`、命名空间 `:143-146`、互不包含 `:150-153`、重解析点 `:154-156`、规范名 `:157-160`）；夹具 `R58MigrationRehearsalTests.cs:64-118,208-242`、`R56…:1011-1039`、8.3 不可移植注释 `:949-953,974-975,1026` | 一致 | 「标为未验证」的部分与代码中的注释口径一致，未被夸大 |

## Q2

**结论：主张仍成立** —— `TryRunProduction` 在当前主线**没有外部调用方**。

使用命令与范围（工作目录 `E:\Program Files\better-genshin-impact-LCB`，HEAD `8a3ee6c4…`）：

```
rg -n "TryRunProduction" --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' MultiplayerHoeingAssistant BetterGenshinImpact BgiCoordinatorServer Test
rg -n "TryRunProduction" --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' --glob '!_workflow/**' --glob '!_batch21/**' --glob '!_r56*/**' --glob '!.agents/**' --glob '!.kiro/**' .
rg -n "AuthorizeProductionExecution" --glob '*.cs' --glob '!**/bin/**' --glob '!**/obj/**' MultiplayerHoeingAssistant BetterGenshinImpact BgiCoordinatorServer Test
```

命中全集（两类，无第三类）：

- 定义与注释：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs:79`（注释）、`:730`（定义）。
- 夹具：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs:68,81,195,358,591,680`。

含义：`TryRunProduction` 的唯一调用面是测试；即使放宽到全仓（含 `AutoHoeingUpdater`、`BgiCoordinatorServer.Tests` 等其它顶层项目，仅排除 `bin`/`obj` 与 `_workflow`/`_batch21`/`_r56*` 等历史材料目录），也**未出现** Host/VM/Runner/Queue/服务端调用。`AuthorizeProductionExecution` 的命中同样只有定义 `:713`、内部调用 `:735` 与夹具。

范围限制：静态、`*.cs`、所列目录、排除 `bin`/`obj`；不构成对反射、动态调用、脚本注入、运行时生成代码的形式化全称证明（与交付 A/B 的限定一致）。附带说明：`_batch14/disc_hold.cs` 是历史材料目录内的旧副本，该次全仓搜索已包含 `_batch14` 而未命中该符号。

## Q3

逐项判定（依据为 file:line 或命令；命令同 Q2 的 rg 形式）：

- **A 真实引用更新与 candidate→active 激活：未接线（与准备包一致）。** `MarkReferenceUpdateCompleted`/`MarkActivated` 仅 `Advance` 推进阶段，无任何真实服务调用（`MigrationSwitchTransaction.cs:495-511`）。全仓搜索 `ActivateCandidate|UpdateReferences|candidate->active|candidate→active` 在生产 C# 中**唯一命中**是演练步骤的展示字符串 `MigrationRehearsal.cs:130`；`ReferenceUpdating` 命中均为枚举、状态机、事务内部与夹具。助手侧 `candidate-ready` 是禁写/禁启动**守卫**（`TaskCenterHost.cs:188-198,248-249,907-909,1068-1070`），不是激活实现。
- **B 助手流程/引用/激活元数据恢复范围：未接线（一致）。** Host 组装 `_workflows`/`_runs`/`LocalWaitQueue`（`TaskCenterHost.cs:117-119`）；启动恢复仅遍历 `_runs.RecoverOnStart()`（`:160-179`，调用点 `:166`）。全仓 `RecoverOnStart` 检索中，`MigrationSwitchTransaction.RecoverOnStart` 仅有定义 `MigrationSwitchTransaction.cs:670` 与夹具调用，**宿主启动路径不调用迁移恢复**。
- **C `TryRunProduction` 生产检查点调用链：未接线（一致）。** 见 Q2。
- **D 静止窗口全写方覆盖：未接线（一致）。** 生产侧唯一注入静止窗口的位置是演练的 `() => new NoopQuiet()`（`MigrationRehearsal.cs:114`，桩定义 `:219`）；`requireQuiescence` 的非测试使用只在组件自身（`:92,103,110,373,593,962`）。存在**独立的** BGI 侧静止机制（`BetterGenshinImpact/Service/Execution/PreemptionGate.cs:23`、`MultiplayerHoeingAssistant/Services/CommandExecutor.cs:1848-1881`）与 `Arbitration/SwitchGuardPolicy.cs:42` 的 `not_quiescent` 判定，但二者都不经由事务的 `Func<IDisposable>` 窗口；`SwitchGuardPolicy.Evaluate` 仅被测试调用。故 D 既非「已接线」，也不是准备包误标。
- **E 真实入口回执/责任：未接线（一致）。** TaskCenter 内 `receipt` 相关实现属 external-start 台账路径（`TaskCenterHost.Admission.cs:305-382` 的 `external_start_receipt_*`/`receipt_key_mismatch` 等），与迁移入口无调用关系；迁移入口产物是 `MigrationRehearsalReport`（`MigrationRehearsal.cs:13-34`），UI 只做摘要展示（`TaskCenterPanelViewModel.cs:79`）。
- **F 目标机路径身份（UNC/映射盘/SUBST/8.3）：未接线/未验证（一致）。** 代码门禁在 `ValidateRoots`（`MigrationSwitchTransaction.cs:127-161`）与 `PathIdentity` 使用点（`MigrationRehearsal.cs:59,80-81`、`MainViewModel.BgiExternal.cs:109,119-120,137`），但夹具明确标注 8.3 别名反例**不可移植**（`R56…:949-953,974-975,1026`），目标机证据本环境无法产生。**边界说明**：F/D 所依赖的 `host.UserConfigRootProvider ??= ResolveBgiUserConfigRoot`（`MainViewModel.BgiExternal.cs:42`）与演练写入前隔离校验（`MigrationRehearsal.cs:74-101`）确实在主线存在，但准备包已在 `contact-inventory.md` 的 F 行把真实路径限定为「只用于 Host 演练隔离检查」，故不构成「已接线却被标未接线」。

## Q4

导入 theory 为 `R56MigrationSwitchTransactionTests.cs:150-197`（3 行 `InlineData`：`extra`/`missing`/`changed`）。关键断言所依赖的生产代码：`VerifySnapshot` `:743-774`（多余文件 `:758-759`、缺失 `:764`、读失败 `:766-770`、哈希不符 `:771`）、`Commit` `:589-601`（阶段/blocked/演练范围/静止窗口/`:597` 快照校验）、`AuthorizeProductionExecution` `:713-727`、`TryRunProduction` `:730-740`、`TakeSnapshot` `:377-402`、`ComputeSnapshotManifestHash` `:408-416`。

**能证明的**：

- 「损坏时提交失败闭锁」：`:189-193` 断言 `Commit` 失败、原因以 `snapshot_invalid:` 开头、且**从磁盘读回**的 manifest 仍为 `Activated` 且无提交标记 —— 是行为+持久态断言，非文本断言。
- 「未授权时零执行」：`:194-196` 断言返回失败且**执行计数为 0** —— 是真实副作用断言（A 的 production-gate 反向突变正是被 `:196` 变红）。
- 「字节对应」：`:170-171` 用测试内独立 `File.ReadAllBytes` 比较活配置根与快照副本（独立 oracle，强度高）。
- 无静默跳过：`default` 分支抛 `ArgumentOutOfRangeException`（`:184-185`）。

**断言弱于主张之处（逐条）**：

1. **文件集合「完整」的 oracle 是硬编码常量，而非独立枚举。** `:166` 以 `new[]{"a.json","sub/b.json"}` 作期望，`:167-168` 亦按这两个键取值；证明的是「manifest 声明集合＝夹具已知内容」，不是「快照覆盖配置根全部文件」这一普遍命题（两层以上目录、更大文件规模未覆盖）。
2. **快照目录侧「无多余文件」只是间接证据。** theory 未对快照目录做显式集合断言；该性质仅通过 `:188` 命中「早返回原因串」（`VerifySnapshot` 的额外文件扫描在 `:758-759` 先于逐文件检查）间接成立。
3. **清单哈希断言用生产函数自证。** `:169` 以 `MigrationSwitchTransaction.ComputeSnapshotManifestHash`（`:408-416`）重算并与 manifest 字段比较，**无独立 oracle**：若该函数算法本身损坏（例如只摘要键、忽略值），断言仍会通过。
4. **「未授权零执行」只覆盖未提交状态，且未断言门别。** `:195` 在 `Activated` 态命中 `:721` 的 `not_committed` 拒绝，theory 未断言失败原因，因此「未授权」的归因靠阶段推断。更关键：**已提交后快照被篡改、且不调用 `Rollback`/`RecoverOnStart` 时**，授权路径 `:713-727` 并不复查快照（方法体内无 `VerifySnapshot` 调用），该情形在本 theory 中无断言；现有覆盖是另一夹具 `Rollback_BrokenSnapshotAfterCommit_RevokesAuthorization`（`:572-593`），而它只在**调用回滚之后**断言「标记被清、授权被拒、零执行」。该性质按「已提交+快照损坏+直接授权」路径的覆盖为空。
5. **提交失败后未断言活配置根未被写入。** `:192-193` 只断言 manifest 的 `Stage`/`CommitMarker`，未断言 `_configRoot` 字节不变（该方向由 `:1041-1073` 与 `:713` 系列其它夹具承担）。
6. **正向覆盖是传递性的。** 「快照完好时 `VerifySnapshot` 返回空且逐文件哈希匹配」不是本 theory 直接断言，而是经 `:163` `RehearseRollback().Success` 间接证明（生产代码 `:539` 要求 `VerifySnapshot` 为空）。

**判别力**：A 的 `results.json` 显示 `missing` 突变在 `assertionLine: 188` 变红（期望 `snapshot_file_missing:a.json`、实际 `snapshot_read_failed:a.json`）、`changed` 同样落在 `:188`（实际空串），`mutantExitCode: 1`、`restoredExact: true`、恢复后 3/3 —— 与报告一致，说明 `:188` 具备分支判别力；但该判别力本身是**基于消息文本**的，行为判别力由 `:190-196` 承担。

## Q5

本次只读核查中**无法核实或证据不足**的点：

1. 未运行构建/测试，因此**无法核实**当前主线在工作区改动下可编译，也无法复现任何通过数；回归数字只做了 TRX 计数核对（`r56-targeted-final.trx` = 81/81、`assistant-full-final.trx` = 1482 passed / 1484 total、`assistant-full-baseline.trx` = 1479 passed / 1481 total，与报告一致），未核对逐用例身份差集。
2. 反向突变证据只做**部分**核对：`results.json`（missing/changed）与报告一致；`commit-refusal`、`production-gate` 两组仅见于报告与 TRX/日志文件存在，未逐份解析失败行，且未复跑（受只读/禁构建约束）。
3. 真实子进程崩溃演练（父进程 `Kill(entireProcessTree)` 后新实例恢复）属交付报告记载；我未运行 harness，无法独立核实父子进程时序与混合态观察。
4. 目标机路径身份无法产生新证据：UNC/映射盘/SUBST/启用 8.3/junction 的真实行为、以及部署机最终路径身份，本环境不具备（夹具自身也标注 8.3 反例不可移植）。
5. 「无外部调用方」类结论限定于静态、`*.cs`、所列目录、排除 `bin`/`obj` 的搜索；反射、动态调用、脚本与运行时生成代码的形式化排除**未做**。
6. UI 运行期点击行为未核实（`R58MigrationRehearsalHostTests.cs:47-64` 自身标注为文本匹配守卫，不证明运行期行为）；我亦未读 XAML。
7. 交付 A/B 报告中的行号只对**各自版本**负责；主线 `TaskCenterHost.Admission.cs` 行号已漂移（B 的 `contact-inventory` 记 772/807，对应 B 固定提交的同一函数，主线现为 789/824，HEAD blob `8347cfa79b3606aed95fb56874bc1cd2350e77ef` vs B 固定提交 blob `12f089e410fbd16d04932eaa35e00945f886e0b0`）。该文件漂移不影响 Q1–Q4 判定（迁移组件两文件 blob 未变），但我**未**逐个核对其余引用文件（WorkflowStore/RunStore/WorkflowRunner 等）的漂移范围。
8. 交付 B 声称的「源工作区曾短暂误写并已纠正」我只读了其自述与证据文件，**未独立核实**源工作区历史状态。

## 已读文件清单

主线（工作目录 `E:\Program Files\better-genshin-impact-LCB`，HEAD `8a3ee6c4c98e845b2988774fe9c3ab65343ce33e`）：

- `MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`（全文 1018 行；blob `7beffd6384f5be6bddfb54ee9d8f17b1ec7e863d`）
- `MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs`（全文 220 行；blob `b84940d23f184f7700819fa8417976baf58a4504`）
- `MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs`（`:240`、`:760-840`）
- `MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs`（`:108-200`）
- `MultiplayerHoeingAssistant/ViewModels/MainViewModel.BgiExternal.cs`（`:24-60`、`:99-165`）
- `MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs`（检索命中行）
- `MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/SwitchGuardPolicy.cs`（检索命中行）
- `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`（`:60-230`、`:572-600`、`:1040-1074`）
- `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R58MigrationRehearsalTests.cs`（全文 425 行）
- `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R58MigrationRehearsalHostTests.cs`（`:1-135`）
- `git show 5e7e7e22f11daad0c86795368e9a21bb14d79b19:MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs`

交付 A（worktree `C:\Users\Administrator\.codex\worktrees\r56-migration-audit\better-genshin-impact-LCB`，HEAD `c11cb45f6de2436b46c58b91d47f2c9768132ef3`）：

- `_r56_parallel/report.md`（SHA-256 `044A62D0…9D97`）、`_r56_parallel/acceptance.md`（`377FC50C…55AE`）、`_r56_parallel/reverse-mutation/followup/results.json`、`_r56_parallel/trx/{r56-targeted-final,assistant-full-final,assistant-full-baseline}.trx`（仅 Counters）
- 主线副本 `_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/{report.md,acceptance.md}`（与源逐字节一致）

交付 B（worktree `C:\Users\Administrator\.codex\worktrees\r56-activation-prep\better-genshin-impact-LCB`，HEAD `90588159b4769c41284ade475052d8b56f92e2d6`；固定代码审计提交 `5e7e7e22f11daad0c86795368e9a21bb14d79b19`）：

- `_r56_activation_prep/{acceptance-matrix.md,contact-inventory.md,report.md,source-hashes.json}`
- `_r56_activation_prep/evidence/search-consumer-audit.md`
- 主线副本 `_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/*`（上述 5 份与源逐字节一致）

## 未做的事

- **未运行任何构建或测试**：无 `dotnet build`/`dotnet test`，未运行任何测试、突变脚本、演练 harness 或产品进程；未写入 `TestResults/`。
- **未修改任何文件**：未创建/修改/删除工作区或其它目录文件，未执行 `git add`/`commit`/`checkout`/`stash`，未切换分支或 HEAD，未改动任何配置；所有 Git 操作均为只读命令，未读取真实 `User` 目录或 `%APPDATA%` 配置。

---

## 编辑性注释（主执行者追加，原文逐字保留）

- 本报告 Q5 第 1 条引用的 `r56-targeted-final.trx` = 81/81、`assistant-full-final.trx` = 1482 passed / 1484 total、`assistant-full-baseline.trx` = 1479 passed / 1481 total 均来自**来源交付 A 的隔离 worktree**（`C:/Users/Administrator/.codex/worktrees/r56-migration-audit/...`），**不是本批在主线上的运行数**。本批主线运行数为：同条件基线定向 78/78、助手全量 1567/2/0/1569；集成后定向 81/81、助手全量 1570/2/0/1572（见 `../final/`、`../baseline/`、`../testid-comparison.json`）。两者不可互相代替或交叉认证。
- 本报告 Q4 列举的 6 处断言强度边界与 F-4/F-5，本批已按第 1 轮会诊结论登记于 `../findings.md` 第 8/9 节与 R5.3 §24.128.6。
