import pathlib, io
p = pathlib.Path("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md")
s = p.read_text(encoding="utf-8")
assert "§24.129" not in s
sec = """

## §24.129 R5.6 A 项主线施工子批——隔离配置根的真实引用写入与 candidate→active 激活接线（2026-09-29）

### §24.129.1 范围、来源绑定与离线边界

- **本批只施工 R5.6 验收矩阵 A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程只在**隔离配置根**内；**不**执行真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。B–F 与真实入口/实机门**本批未验收**。
- 开工实况：分支 `main-OldTeaBag-B168`、HEAD `22ccd6ee2721abb1642535253997c57dbf9020d6`；开工快照与 23 行风险矩阵见 `_workflow/r56-reference-activation-wiring-2026-09-29/`（`workflow.py begin` 先于任何代码改动）。
- 已 integrated 的 `r56-migration-audit`／`r56-activation-prep` **不重复接收**；`tools/mistletoe/deliveries.py` 只读发现检查在开工与收口各运行一次。
- **只读子 Agent**（固定开工 ref `22ccd6ee2`，只读、零写入）独立复核三问：跨程序集可达性、写方/消费方枚举、写集与回滚归属；报告 `_workflow/r56-reference-activation-wiring-2026-09-29/subagent-readonly-audit.md`。其结论与主执行者审计一致，但**仍由主执行者按最终源码复核**，且不替代回归与突变。
- 材料外变更：工作区其余历史证据、`.bak`／`.stale`、日志、`TestResults`、DLL 与两份既有未提交设计文档（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）**完整保护、未触碰、未提交**。

### §24.129.2 反例先行：阶段标记不等于真实副作用（本批要推翻的旧语义）

- `MarkReferenceUpdateCompleted`／`MarkActivated` 实现为 `Advance(stage)`：**只校验合法转换并落 manifest**，不执行任何引用写入或激活动作。
- 可定位反例（`R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired`）：未注入真实副作用端口时，**零文件写入**即可 `SnapshotReady→ReferenceUpdating→Activated→RehearseRollback→Commit` 全部成功，且配置根字节始终未变（假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
- 另证明 R1 迁移器产出的 `activation.status = "candidate-ready"` 在固定版本中**只有读取判定与拒绝路径**（`TaskCenterHost` 保存/启动/移交启动/移交恢复四处禁写禁启、`WorkflowPlanner` 预检阻断、UI 只读预览），**没有任何生产写入路径**把该状态改写为 `active`。

### §24.129.3 最小实施（真实副作用 + 读回确认先于阶段推进）

- 新增 `IMigrationEffectService` 与真实实现 `WorkflowFileMigrationEffectService`（`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`）：引用重命名（`nodes[].ref.config`）、新增文件、`candidate-ready→active` 激活（读改写）；逐文件**临时文件 + 同目录替换**、**保留原 BOM 形态**、**写前字节复检**（读后被锁外改动即放弃）、解析失败**隔离跳过不覆盖**；结果分**成功／确定拒绝／写后不明／取消**四类并如实回报**已落盘文件数**。
- `MigrationSwitchTransaction` 新增两个真实入口（端口经构造注入，与既有 `quiesce`／`stageHook` 同模式）：
  - `ApplyReferenceUpdate(plan)`：**写集声明校验**（非空、路径安全、无重复、逐项等于已登记变更归属且无漏项/多项；`Deleted` 不支持）→ 真实副作用 → **语义读回 + 字节读回** → 才推进 `ReferenceUpdating` 并持久化 `referenceWriteSet`；另做**写集外零改动**核对（基线文件逐字节不得变化）。
  - `ActivateCandidate(request)`：须已完成已确认的引用更新；目标必须**在已确认写集内**；真实激活 + **状态读回** → 才推进 `Activated` 并持久化 `activationRecord`（同步更新写集哈希，因激活同样改写了该文件）。
- **失败语义**：拒绝／写后不明／副作用后取消 ⇒ `Blocked`（fail-closed、**不盲目重试**、零生产许可）；**副作用前取消**（`CompletedWrites==0`）⇒ 保持当前阶段、可回滚；已到目标阶段时**幂等重读复核**（不二次触发副作用，盘上漂移即拒绝）。
- **提交与回滚**：提交前复核写集与激活记录（`commit_recheck_failed`，锁外漂移一律拒绝）；回滚**先撤销真实激活**（读回当前状态 → 反向副作用 → 再读回确认），再恢复旧字节、按归属删除新增，并要求**旧态字节与引用一致**；未确认即 `Blocked`，**绝不报告完整回滚**。
- **持久化与校验**：manifest 新增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord`，全部纳入 `ManifestIntegrity` 摘要与**结构与状态不变量**（写集路径安全、激活目标须在写集内、真实事务的 `ReferenceUpdating/Activated/Committed` 必须携带对应读回证据）；`realEffectsRequired` 由开事务时是否注入端口决定并受摘要覆盖，**篡改后重算摘要仍判无效**。

### §24.129.4 验证证据（绑定最终代码版本）

- **定向**：`R56MigrationSwitchTransactionTests` + `R58MigrationRehearsalTests` + `R56ReferenceActivationWiringTests` **99/99**；同条件开工基线（前两类）**70/70**。
- **助手全量**：**1599 通过 / 2 跳过 / 0 失败 / 1601**；同条件开工基线 **1570 / 2 / 0 / 1572**；逐 testId 差集 **added=29 / removed=0 / changed=0 / unchanged=1572**。
- **反向突变 17 项**（`_workflow/r56-reference-activation-wiring-2026-09-29/mutations/`，逐项含 build/mutant/restored 独立日志与 TRX）：baseline Passed / mutant Failed（构建 exit 0、测试 exit>0，命中具名断言）/ restored Passed，**源码逐字节恢复**（raw SHA-256 `4122B45C7712…`）。其中 M3、M5 为「同一位点两层独立复核对同一漂移各自判别」的**整块削弱**突变（单独去掉任一层会被另一层拦下，故按整块定义以取得判别力）。
- **矩阵覆盖**：状态/并发/故障 23 行全部 `covered`，每行绑定具名反例与**有效突变**。
- **部署目标**：全部构建与测试带 `-p:DeployToBgiTools=false`；部署目录 1158 个文件、**最新最后写入时间 2026-09-27 09:29:33（早于本批 2026-09-29）**，即本批**未写入部署目标**（该事实只支持「本批未留下可观察变化」，不主张过程中从未写入）。
- **BGI 侧**：本批未修改 `BetterGenshinImpact` 任何文件（`git status`／`git diff` 可核），故未跑 BGI 相关回归。

### §24.129.5 未闭合项、owner 检查点与门禁

- **B–F 六项启用前置继续未闭合**：助手流程/引用/激活元数据恢复范围（B）、生产消费必经检查点（C）、真实静止窗口写方全覆盖（D）、真实入口回执与责任（E）、目标机路径身份（F）本批**未验收**。
- **owner 检查点（1 项，正确性相关，本批不自行裁决）**：BGI 侧 W4 引用服务 `OneDragonConfigReferenceService` 为 `BetterGenshinImpact` 程序集内 `internal static`，而助手工程**无**对 BGI 的程序集/项目引用（`using BetterGenshinImpact` 命中 0、`InternalsVisibleTo` 只授予 BGI 单测）⇒ 助手进程**无法直调** W4。故**生产环境应由哪一端提供引用写方**（BGI 侧直调 W4／下沉共享库／经既有 IPC 委派）**无权威材料裁决**；本批的真实引用写方是助手侧实现（隔离根内），**生产接线保持关闭**，不据本批开启。待 owner 裁决后再定接线归属。
- **门禁**：真实 `User` 目录切换、生产构造开门、E3/E4/E5、热键、R5.8 签署、实机与生产进程门**继续关闭**；本批**不**声称 R5.6 或 R5 完成。
- 既有建议级残项 `R56-D1`（提交后快照损坏 + 直接授权交错）**状态未变**；本批未改其触发条件。

### §24.129.6 设施与声明面

- 设施：`workflow.py begin`（开工快照 + 原始矩阵）／`audit --stage review` + `verify`／`audit --stage closeout` + `verify`；索引、`git status --porcelain`、本批未暂存/已暂存 diff 与材料外变更区分随快照保存。工具只做机械核验，`quality_verdict` 恒为 NOT PROVIDED。
- 本批改动声明面（新增状态/门禁措辞）⇒ 按 §17.4-A 第 1 条 `CLAIM_SURFACE_REGENERATE=1` 再生清单并纳入提交评审，清除变量后复跑守卫。
- 会诊：本子批独立计数（上限 8 次）；逐轮记录见 `_workflow/r56-reference-activation-wiring-2026-09-29/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.129.7。
"""
p.write_text(s.rstrip("\n") + sec, encoding="utf-8")
print("R5.3 §24.129 appended; bytes=", len(p.read_bytes()))
