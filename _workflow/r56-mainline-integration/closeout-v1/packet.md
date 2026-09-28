
## objective: _workflow/r56-mainline-integration/context.md L1-L28 SHA256=f5e45dabc1855026c3d9858383aecb8fc75a8da852bb071a6f2aff0f6f149f34

# 本批目标与范围（objective）

**批次**：`r56-mainline-integration-2026-09-28`（R5.6 主线迁移集成批／并行成果接收与集成验证）。
**开工**：分支 `main-OldTeaBag-B168`，HEAD `8a3ee6c4c98e845b2988774fe9c3ab65343ce33e`。
**性质**：**接收批**。把目标批次为「R5.6 主线迁移集成批」的两项已登记并行成果接入茶包主线，并在主线重跑集成版本的定向/全量回归与反向突变；不施工 R5.6 生产接线。

## 接收对象

| 来源交付 | 来源 worktree / HEAD | 交付物 | 本批处置 |
|---|---|---|---|
| `r56-migration-audit`（核验 R5.6 迁移回滚组件） | `.../worktrees/r56-migration-audit/...` / `c11cb45f6de2436b46c58b91d47f2c9768132ef3` | 迁移快照完整性 theory（`R56MigrationSwitchTransactionTests.cs` +48 行）＋ 组件核验报告/证据 | **接收**：测试增量逐字节导入主线；报告/验收/突变记录作为材料证据摄入 |
| `r56-activation-prep`（完成 R5.6 迁移接点审计） | `.../worktrees/r56-activation-prep/...` / `90588159b4769c41284ade475052d8b56f92e2d6` | A–F 六项启用前置、逐状态验收矩阵、正式集成最小实施顺序、登记侧车 | **接收（材料）**：包内已跟踪文件逐字节并入主线批次证据目录；A–F 仍为 R5.6 生产接线前置 |

来源任务终态：两项的来源 thread（`01a0e060-f824-7bb2-8490-d53b6e6dd88d`、`01a0e481-0b6d-74d0-a915-ac20cbaac861`）在本次重查中最新 turn 均为 completed，且其自身收口说明都声明「主线集成属独立目标，本批未合入」。报告/验收 SHA-256 与登记侧车哈希已逐项复核一致（见 manifest `evidence` 与 `intake-equivalence.json`）。

## 范围与排除

- **做**：接收与逐字节可核的导入；主线集成版本回归（定向＋助手全量，含同条件基线与 testId 差集）；关键断言反向突变重跑；部署目标**未留下可观察变化**的复核；声明面再生/复跑；并行索引与机器台账回填；本批 v2 manifest 与 audit/verify。
- **不做**：R5.6 生产接线（真实引用更新、candidate→active 激活编排、生产检查点消费、真实静止窗口、真实入口回执、目标机路径身份）——A–F 前置与生产门继续关闭；不施工 R6.1、R6 diff guard；不重开 BO-8/BO-9、BO-6/7-D1、BO-13、SB21-3 BO-4、SB21-2 BO-10/12；不提前集成其他目标批次的成果。
- **不改**：工作区两份既有未提交设计文档 `Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md` 一律不触碰、不提交。

## 接收映射与边界

交付材料在主线收纳于 `_workflow/r56-mainline-integration/source-delivery/`（顶层 `_r*` 目录会被 `tools/mistletoe/deliveries.py` 误判为未登记报告）。映射、逐文件哈希与未收录清单见 `source-delivery/INTAKE-NOTE.md`、`intake-equivalence.json`、`source-delivery-line-endings.json`。

## 材料外变更

见 manifest `outside_changes` 与送审快照的 `git status --porcelain`。本批不把其他写者的在途改动当作自己的成果。


## findings: _workflow/r56-mainline-integration/findings.md L1-L69 SHA256=f8beae90f930db1a33be981853d70df053f5f232f91e555b9a81a324e4c5da4b

# 核验发现与证据摘要（findings）

全部结论绑定本批开工 HEAD `8a3ee6c4c` 与集成后的工作区字节；路径相对仓库根。

## 1 接收等价性（逐字节）

- 唯一产品/测试增量：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`，`git diff --numstat 8a3ee6c4c c11cb45f6` = **48 / 0**。
- 导入后主线暂存内容与来源提交 blob **完全相同**（blob `cd121220…`）；工作区文件 54228 字节、SHA-256 `4C101FF3…`，与来源 worktree 现行字节相同。
- 25 个摄入材料文件：暂存内容 25/25 等于来源 blob，工作区字节 25/25 等于来源 worktree 现行字节。证据：`intake-equivalence.json`、`source-delivery-line-endings.json`。

## 2 集成版本回归（主线上重跑，未复用来源数字）

- 构建（两项目 Rebuild 均带 `-p:DeployToBgiTools=false`）：助手项目 exit 0（59 警告/0 错误）；测试项目基线 exit 0（84 警告/0 错误）、导入后 exit 0（84 警告/0 错误）。
- 同条件基线（导入前 HEAD `8a3ee6c4c`）：定向三类 **78/78**；助手全量 **1567 passed / 2 skipped / 0 failed / 1569**。
- 集成后：定向三类 **81/81**；助手全量 **1570 passed / 2 skipped / 0 failed / 1572**；testId 对比见 `final/testid-comparison.json`（added=3、removed=0、changed=0，其余 unchanged）。
- 基线 TRX 与上一子批（`wave3-bo8-bo9-2026-09-28`）自己的最终 TRX 逐 testId 相同（0 added/0 removed/0 changed），说明主线在两次提交之间未再变动测试成员。

## 3 反向突变（集成字节上重跑 5 项）

| 突变 | 目标 theory 行 | 命中行 | mutant 退出码 | restored 退出码 | mutant SHA-256（CRLF 形态）与来源记录 |
|---|---|---|---|---|---|
| extra | `mutation: "extra"` | `:line 188` | 1 | 0 | `96C634C9…`（本批独立等价文本；来源仅留 `source-hashes.txt`：`198C7493…`，未记录精确 needle） |
| missing | `mutation: "missing"` | `:line 188` | 1 | 0 | `56BF4785…`（与来源 `56BF4785…` **相同**） |
| changed | `mutation: "changed"` | `:line 188` | 1 | 0 | `E47AEDE7…`（与来源 **相同**） |
| commit-refusal | `mutation: "missing"` | `:line 190` | 1 | 0 | `09BDC1CC…`（与来源 **相同**） |
| production-gate | `mutation: "changed"` | `:line 196` | 1 | 0 | `2CA29B7A…`（与来源 **相同**） |

- 每次突变三段（baseline/mutant/restored）均为独立 TRX 与独立命令日志；mutant 构建成功、具名 theory 行 Failed 且栈落在指定断言行；restored 逐字节恢复（`C2D4A122…`）后 3/3 通过。
- 4/5 的 mutant 哈希（CRLF 形态）与来源 `reverse-mutation/followup/results.json` 记录**完全相同**，证明是同一内容上的同一突变；`extra` 一项因来源未记录精确 needle，本批为语义等价的独立文本突变，其哈希单独记录、不假称与来源相同。证据：`mutations/`（逐突变目录、`mutation-records.json`、`mutation-hash-crosscheck.json`）。

## 4 部署目标与构建卫生

- 全部构建/测试命令显式带 `-p:DeployToBgiTools=false`。
- 部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant` 在构建与测试前后清单（文件集合＋逐文件 SHA-256）**逐项相同**：`deploy-target-before.json` / `deploy-target-after.json`（`identical=true`）。

## 5 材料发现（建议级，逐项登记）

- **F-1（材料漂移，建议级）**：上一子批 §24.127.3/§24.127.6 与其计划/索引散文记「助手全量 1566/2/0/1568（基线 1562/2/0/1564）、testId 4 added」，而其自己的最终 TRX（`_workflow/wave3-bo8-bo9/final-v9/assistant-full-final.trx`）与基线 TRX 实为 **1567/2/0/1569** 与 **1562/2/0/1564**，差集 **5 added / 0 removed / 0 changed**；多出的一行为第 2 轮会诊新增夹具 `WorkflowRunnerTests.Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode`（见 `_workflow/wave3-bo8-bo9/consultation/review-outcome-v2.md`）。散文数值与 TRX 不一致；**权威以 TRX 为准**。来源未改，本批只登记口径并保留原工件（同 §24.126.4 口径）。
- **F-2（本批预登记的连带影响）**：本批开工前在风险矩阵 INT-S2 的 `expected` 中按上述散文预登记了绝对值（1566/1568 → 1569/1571）。实测为 1567/1569 → 1570/1572（+1）。差值已定位为 F-1 的散文漏记，**不是**本批导入所致：本批导入造成的差集恰为 added=3、removed=0、changed=0。INT-S2 的实体主张（导入只新增 theory、不改变既有成员）由 `final/testid-comparison.json` 直接证明；冻结矩阵行按规则不改写，故以本条登记差额。**第 1 轮会诊口径**：INT-S2 记为「绝对计数偏差已解释，成员差集主张通过」，不得声称原绝对计数预期精确通过。
- **F-3（处置说明，非缺陷）**：来源交付的 `extra` 突变未记录精确 needle，故本批无法证明与来源 mutant 逐字节相同（见第 3 节）。按第 1 轮会诊口径，本批 `extra` 项应表述为**针对同一检查目标的独立突变**，不主张与来源 mutant 语义等价；该缺口不影响其余 4 项（其 mutant 哈希与来源记录完全相同）。

## 6 边界与门禁（不变）

- 本批只并入**组件与准备材料**，不构成 R5.6 生产接线或 R5.6/R5 完成。A–F 六项启用前置（真实引用更新与 candidate→active 激活、助手流程/引用/激活元数据恢复范围、生产检查点实际消费链、静止窗口全写方覆盖、真实入口回执与失败/未知/取消责任、UNC/映射盘/SUBST/8.3 目标机路径身份）**仍未闭合**。
- BGI 产品入口、真实 User、R5.8 签署、E3/E4/E5、热键面与生产进程门**继续关闭**；未做实机或生产验证。
- `ShouldRegisterLocalWait` 生产恒 null 等停驻语义生产不可达结论未因本批改变；BO-8/BO-9、BO-6/7-D1、BO-9-D1 状态未被本批重开或改变。

## 7 子 Agent 评估

开工时拟评估 1 个固定版本只读子 Agent 的净收益（任务：在固定开工字节上核对来源交付报告/验收矩阵与主线实现的一致性、找出材料与代码不符处），结论与处理见送审材料与 §24.128.5。

## 8 只读独立核查（固定开工 ref 子 Agent）带来的补充发现

子 Agent 报告（`subagent/readonly-audit-report.md`，21,674 字节）在固定开工 ref `8a3ee6c4c` 上核对了两份来源交付的主张与主线实现，结论：交付 A 覆盖矩阵 7 行逐行与主线实现**一致**；`TryRunProduction` 在主线**仍无外部调用方**（有界 C# 搜索仅命中定义与夹具）；交付 B 的 A–F 六项**没有一项已被主线接线**（不存在「已满足却被标未接线」的项）。同时提出下列补充点。

- **F-4（覆盖观察，待会诊判定等级）**：`AuthorizeProductionExecution`（`MigrationSwitchTransaction.cs:713-727`）在**已提交之后**不复查快照字节——它只校验持锁、manifest 有效、`Stage==Committed`、`CommitMarker==TransactionId`、无 `BlockedReason`。因此「提交后快照损坏 + 直接授权（未先回滚）」这一交错下，授权仍会通过；快照损坏的封锁与授权撤销发生在 `Rollback()`（`:641` 走 `MarkBlocked("rollback_snapshot_invalid:…")` 并清 marker），已有夹具 `Rollback_BrokenSnapshotAfterCommit_RevokesAuthorization`（测试 `:573-593`）正是覆盖该路径并断言封锁+撤销授权+零执行。
  - **我的反例尝试记录（避免全称否定）**：①试图构造「提交后快照损坏 + 直接授权」的现有夹具——`VerifySnapshot` 的调用点为 `:539`（RehearseRollback 前置）、`:597`（Commit）、`:641`（Rollback）、`:698`（RecoverOnStart，第 1 轮会诊指出本批原文漏记此点，已更正），授权路径内**无调用**（以源码行读取确认，非推测；`:698` 分支在有效已提交态之前返回，故不影响本项判定）；②试图确认该交错是否属设计意图——`:573` 夹具的注释自述为「第 4 轮必改⑥：已提交后快照损坏 ⇒ 回滚**先封锁**再失败 ⇒ 必须撤销生产授权」，即设计把「撤销」绑定在回滚动作上，而非在每次授权时重验；③未尝试构造运行期反例（本批不运行产品）。
  - **第 1 轮会诊判定（gpt-6-astra / medium）**：F-4 属「授权=提交+标记+无阻塞、快照完整性由提交/回滚路径保证」的**设计边界**，非正确性违约，**建议级**；不阻断本批收口，登记为具名建议级残项 **R56-D1**，处置条件＝下一次重绑定 `MigrationSwitchTransaction.cs` 哈希的批次补合同说明与直接交错夹具；若生产合同日后要求「失去回滚能力立即停执行」，须落实检查与文件并发保护（单次授权前重验亦不能消除外部写入竞态）。**未降级任何项**（会诊未给出 MUST/IMPORTANT）。会诊同时指出：本批原文「`VerifySnapshot` 仅三个调用点」有误（漏 `RecoverOnStart` 约 `:698`），已更正。
- **F-5（版本漂移登记，建议级）**：交付 B 的 `contact-inventory.md` 行号绑定其固定基线 `5e7e7e22f`；主线 `TaskCenterHost.Admission.cs` 已漂移（B 记 `:772/:807`，主线为 `:789/:824`；blob `8347cfa7…` vs `12f089e4…`）。迁移组件两文件 blob 未变，Q1–Q4 判定不受影响；**其余被引用文件（WorkflowStore/RunStore/WorkflowRunner 等）的漂移范围未见逐项核对**。后续正式 R5.6 消费批须在自身 HEAD 重新绑定这些行号，不得直接沿用 B 的行号。
- **F-6（断言强度登记，建议级）**：子 Agent 列出导入 theory 的 6 处「断言弱于主张」（文件集合 oracle 为硬编码常量、快照目录无显式集合断言、清单哈希用生产函数自证、未授权零执行只覆盖未提交态且未断言门别、提交失败未断言活配置根字节不变、正向覆盖为传递性）。这些是**该 theory 的覆盖边界说明**，不是缺陷；登记以便后续批次按需补强，本批不因此改写来源已审代码。

上述三项与第 5 节的 F-1/F-2/F-3 一并构成本批的材料/证据发现清单；F-1 与 F-5 属版本绑定问题，F-4 属待判定语义/覆盖问题。

## 9 第 1 轮会诊后的口径收窄（全部采纳，同一修复批）

- 「既有行为未受扰动」→「本次回归未观察到既有实例结果变化」，并列出未覆盖面（跳过用例、未纳入该测试程序集的路径、并行/时序/环境差异、同 testId 下未被断言观察的副作用、生产接线运行行为）。
- 「部署目标未被写入」→「未留下可观察的部署文件变化」；`-p:DeployToBgiTools=false` 是控制手段，不构成「过程中从未写入」的过程全称证明。
- F-4 判为建议级设计边界并登记 **R56-D1**（处置条件见第 8 节）。
- 只读子 Agent 报告 Q5 引用的 `1482/1484`、`1479/1481` 是**来源交付 A 隔离 worktree 的 TRX**，不是本批运行数；已在摄入副本追加标明来源的编辑性注释（原文逐字保留）。


## budget: _workflow/r56-mainline-integration/budget.md L1-L19 SHA256=e708e3a52153d6fa4a622078c84589c72c2dd731dbed9de3f99c52321d59be82

# 会诊预算与处置台账（budget）

- **子批**：`r56-mainline-integration-2026-09-28`（独立计数，不继承、不重置 BO-6/7（8/8＋owner 批准 2/2）与 `wave3-bo8-bo9`（3/8）的计数）。
- **上限**：本子批累计最多 **8 次**会诊请求，覆盖所有模型/渠道的首审、复审、验证与收口；**已发出但失败/超时也计次**；本地预检拦截且未发出的不计。修复与回归本身不计次。
- **固定渠道/模型**：既有 GPT 会诊工具（只读，自动附本批工作区差异）；`gpt-6-astra` / `medium`。若必需材料无法完整送入或 token 预估无法安全容纳，按设施计划的条件式规则切独立本地只读通道，并保持同一模型/强度与同一子批计数。
- **已用**：`1 / 8`（第 1 轮已返回）。逐轮记录在下方追加；本文件同时作为送审材料的 budget 角色。
- **上轮发现**：无（本子批第 1 轮，`review_control.review_round = 1`，`prior_findings = []`）。
- **等级纪律**：重要/必改只能升级不能降级；未闭合的正确性项必须闭环或形成 owner 检查点，不得因预算收口或降级。

## 轮次记录

### 第 1 轮（首审，本子批计 1/8）

- 快照：`_workflow/r56-mainline-integration/review-v2`（`audit --stage review` ok、`verify` ok；packet 365,516 字节）。
- 渠道预检：`consultation/preflight-v1.json`——允许文件 10 个 / 191,784 字节；工具自动附加 status（33,536 B）与 staged（164,596 B）/unstaged（25,340 B）diff；预估合计约 460,051 字节 ≈ 115,012 token，约占 gpt-6-astra 有效窗口（272,000×95% ≈ 258,400）的 40%，**未触发**设施计划的本地只读回退条件。
- 渠道/模型/强度：既有 GPT 会诊工具（只读）、`gpt-6-astra`、`medium`；attempts=1；返回报告（92.9 秒）。
- 原始结论要点：**MUST 0、IMPORTANT 0**；建议级 4 项（F-4 判为设计边界并登记 R56-D1；§24.128.2 材料计数笔误 18→17；`VerifySnapshot` 调用点漏记 `RecoverOnStart:698`；措辞收窄）。收尾原文：「本范围内未发现已确证、仍未闭合的 MUST／IMPORTANT」。
- 逐项处置：全部采纳，同一修复批落地（见 R5.3 §24.128.6 与 `findings.md` 第 8/9 节）；无降级、无未闭合 MUST/IMPORTANT；按纪律 R2 无需追加验证轮。
- 计数：**1 / 8**。


## findings: _workflow/r56-mainline-integration/intake-equivalence.json L1-L276 SHA256=f31d99f31bd3329986ad20a3f7d590bd686e7392775cbada1927719039c60424

{
  "schema_version": 1,
  "batch": "r56-mainline-integration-2026-09-28",
  "rationale": "来源交付材料在主线按 _workflow/r56-mainline-integration/source-delivery/ 收纳：顶层 _r* 目录会被 tools/mistletoe/deliveries.py 的「未登记报告」扫描命中（ok=false），而本批是消费/集成，不是新增未登记交付；映射见本文件与 INTAKE-NOTE.md。",
  "test_file": {
    "path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs",
    "staged_content_equals_source_blob": true,
    "source_commit": "c11cb45f6",
    "pre_import_mainline_commit": "8a3ee6c4c",
    "diff_numstat_vs_pre_import": "48\t0\tTest/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs",
    "worktree_bytes": 54228,
    "worktree_sha256": "4C101FF385B3DD25B96E062D69D985B39721AD07806DDE9CA30E19C1C3E7B5ED",
    "source_worktree_bytes": 54228,
    "source_worktree_sha256": "4C101FF385B3DD25B96E062D69D985B39721AD07806DDE9CA30E19C1C3E7B5ED"
  },
  "material_count": 25,
  "material_files": [
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance-matrix.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/acceptance-matrix.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 4714,
      "worktree_sha256": "0A94A0C4FF4288852A7D8FC763ADCDCCFD6AF1658291F3AA2B5D23BDA7B65103",
      "source_worktree_sha256": "0A94A0C4FF4288852A7D8FC763ADCDCCFD6AF1658291F3AA2B5D23BDA7B65103"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/acceptance.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 8184,
      "worktree_sha256": "12A65A86E1284890B9EE81FF617D8EDDE711FE8DD4C19C01877C0C7C8C887FC3",
      "source_worktree_sha256": "12A65A86E1284890B9EE81FF617D8EDDE711FE8DD4C19C01877C0C7C8C887FC3"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/artifact-inventory.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/artifact-inventory.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 4975,
      "worktree_sha256": "1CD211ED5792814E3825EEC69BE436C048CD4F2BBF5C5F882C49EE371AD2054B",
      "source_worktree_sha256": "1CD211ED5792814E3825EEC69BE436C048CD4F2BBF5C5F882C49EE371AD2054B"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/contact-inventory.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/contact-inventory.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 5370,
      "worktree_sha256": "1B82287E3D0565E43840E1601F96B1C2C749CE34032359909D1D150830C0B349",
      "source_worktree_sha256": "1B82287E3D0565E43840E1601F96B1C2C749CE34032359909D1D150830C0B349"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/delivery-registration.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/delivery-registration.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 14703,
      "worktree_sha256": "D8AAE0C50C0235FC607FABB66FBB32C0000222E4797DE88FC48DF1A772A090BF",
      "source_worktree_sha256": "D8AAE0C50C0235FC607FABB66FBB32C0000222E4797DE88FC48DF1A772A090BF"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/acceptance-history-pre-edit.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/acceptance-history-pre-edit.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1459,
      "worktree_sha256": "80814045639093BBD9E5096FF30F1C83259F0097EA88F14D2FB719CE2717335D",
      "source_worktree_sha256": "80814045639093BBD9E5096FF30F1C83259F0097EA88F14D2FB719CE2717335D"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/boundary-correction.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/boundary-correction.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 2006,
      "worktree_sha256": "27B3B76217B230CB78E5770D98BA49CEF9131A9ADE9DD87E3A914C34E42ABFA9",
      "source_worktree_sha256": "27B3B76217B230CB78E5770D98BA49CEF9131A9ADE9DD87E3A914C34E42ABFA9"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/correction-pre-edit-inventory.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/correction-pre-edit-inventory.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1467,
      "worktree_sha256": "F0C90AFFF3017DCC5A5128C4FAF816A05C7B80B71957B9EEF7032144CE66CB91",
      "source_worktree_sha256": "F0C90AFFF3017DCC5A5128C4FAF816A05C7B80B71957B9EEF7032144CE66CB91"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/correction-source-recheck.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/correction-source-recheck.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 919,
      "worktree_sha256": "08A5DD6640CDBDAE364D5899ADF261433B8D3C96544C836B974A5941849BDBDD",
      "source_worktree_sha256": "08A5DD6640CDBDAE364D5899ADF261433B8D3C96544C836B974A5941849BDBDD"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-boundary.exitcode.txt",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/delivery-discovery-boundary.exitcode.txt",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1,
      "worktree_sha256": "D4735E3A265E16EEE03F59718B9B5D03019C07D8B6C51F90DA3A666EEC13AB35",
      "source_worktree_sha256": "D4735E3A265E16EEE03F59718B9B5D03019C07D8B6C51F90DA3A666EEC13AB35"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-boundary.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/delivery-discovery-boundary.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 6670,
      "worktree_sha256": "128C05E0679BDE8A8E18D63330DFD49693CA5F420D5EE4603471CB515C4A0991",
      "source_worktree_sha256": "128C05E0679BDE8A8E18D63330DFD49693CA5F420D5EE4603471CB515C4A0991"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-open.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/delivery-discovery-open.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 6407,
      "worktree_sha256": "EF43FCEA3558AE3325160D4356F92AC727B94B09213B11B1EE72302992562BFD",
      "source_worktree_sha256": "EF43FCEA3558AE3325160D4356F92AC727B94B09213B11B1EE72302992562BFD"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/discovery-chronology.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/discovery-chronology.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1873,
      "worktree_sha256": "265FCE623D98A8CFA68CFD4C2F2EC07FB038DAF14511EC4F1C1CE1DAFAED4F9B",
      "source_worktree_sha256": "265FCE623D98A8CFA68CFD4C2F2EC07FB038DAF14511EC4F1C1CE1DAFAED4F9B"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/pre-edit-inventory.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/pre-edit-inventory.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 22704,
      "worktree_sha256": "4E19379413E20B6FB73D68608BFFFB5FC866B6682950199D21381F9DA51CF4B9",
      "source_worktree_sha256": "4E19379413E20B6FB73D68608BFFFB5FC866B6682950199D21381F9DA51CF4B9"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/search-consumer-audit.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/evidence/search-consumer-audit.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 5720,
      "worktree_sha256": "8BACCC2E2DEFF1CB783CAA5C7EC75E9954EAD87E64B75F3DFE1BB3646BAA1F53",
      "source_worktree_sha256": "8BACCC2E2DEFF1CB783CAA5C7EC75E9954EAD87E64B75F3DFE1BB3646BAA1F53"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/report.md",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/report.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 9024,
      "worktree_sha256": "BEE488672FA4649817333CDD99EE1E9D99A979BCD48CAAF6D8386442E5FF4FED",
      "source_worktree_sha256": "BEE488672FA4649817333CDD99EE1E9D99A979BCD48CAAF6D8386442E5FF4FED"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/source-hashes.json",
      "source_commit": "90588159b",
      "source_relative_path": "_r56_activation_prep/source-hashes.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 18909,
      "worktree_sha256": "162BC4EC9AF1FE3EBFF609EBF447AA155FDF7F62A3216C8F1A908557F8C13CD0",
      "source_worktree_sha256": "162BC4EC9AF1FE3EBFF609EBF447AA155FDF7F62A3216C8F1A908557F8C13CD0"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/context.md",
      "source_commit": "90588159b",
      "source_relative_path": "_workflow/r56-activation-prep/context.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1471,
      "worktree_sha256": "B9FF057AAF70AAC8BC352F3FACA086E789C81E0E826B40B1139A3B072915F405",
      "source_worktree_sha256": "B9FF057AAF70AAC8BC352F3FACA086E789C81E0E826B40B1139A3B072915F405"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/manifest.json",
      "source_commit": "90588159b",
      "source_relative_path": "_workflow/r56-activation-prep/manifest.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 10561,
      "worktree_sha256": "556EAD0CED17150D951A7CE179D098A9759ADF9FE47FD253D05FCE892B3F3F53",
      "source_worktree_sha256": "556EAD0CED17150D951A7CE179D098A9759ADF9FE47FD253D05FCE892B3F3F53"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/acceptance.md",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/acceptance.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 6975,
      "worktree_sha256": "377FC50C072818764FA178BAFE14CF4F256464368165B41982EE378EB77055AE",
      "source_worktree_sha256": "377FC50C072818764FA178BAFE14CF4F256464368165B41982EE378EB77055AE"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/pre-edit-inventory.json",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/pre-edit-inventory.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 1195,
      "worktree_sha256": "04D37EE6D47F236902E5CD1DE88454CFCB41EA87BFEDAFC599F4B7283054C38C",
      "source_worktree_sha256": "04D37EE6D47F236902E5CD1DE88454CFCB41EA87BFEDAFC599F4B7283054C38C"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/report.md",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/report.md",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 22990,
      "worktree_sha256": "044A62D06DEE49FCDB8E87FAD6ADB30521E03A276217785FACEB2EF817369D97",
      "source_worktree_sha256": "044A62D06DEE49FCDB8E87FAD6ADB30521E03A276217785FACEB2EF817369D97"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/results.json",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/reverse-mutation/followup/results.json",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 6690,
      "worktree_sha256": "E6F2AF18317DEDDF9B5565E2EE67DEED42559312463B4784912B61FD5835020E",
      "source_worktree_sha256": "E6F2AF18317DEDDF9B5565E2EE67DEED42559312463B4784912B61FD5835020E"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/Run-AssertionMutations.ps1",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/reverse-mutation/followup/Run-AssertionMutations.ps1",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 8027,
      "worktree_sha256": "58F30E2265522F8E66C0C6EC2F2534CD18124949DC92649DDA6D90E01D08CF69",
      "source_worktree_sha256": "58F30E2265522F8E66C0C6EC2F2534CD18124949DC92649DDA6D90E01D08CF69"
    },
    {
      "mainline_path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/source-hashes.txt",
      "source_commit": "c11cb45f6",
      "source_relative_path": "_r56_parallel/reverse-mutation/source-hashes.txt",
      "staged_content_equals_source_blob": true,
      "worktree_bytes_equal_source_worktree_file": true,
      "bytes": 257,
      "worktree_sha256": "BC9FC94DA99150496824507EC5D19AC9FF96E8A99D5A48E5E81E44EF3D4847BD",
      "source_worktree_sha256": "BC9FC94DA99150496824507EC5D19AC9FF96E8A99D5A48E5E81E44EF3D4847BD"
    }
  ],
  "all_material_staged_equal_source_blob": true,
  "all_material_worktree_equal_source": true,
  "line_ending_note": "4 个 JSON（_r56_parallel/pre-edit-inventory.json、_r56_parallel/reverse-mutation/followup/results.json、_r56_activation_prep/evidence/{pre-edit-inventory,delivery-discovery-open}.json）在来源侧为 CRLF 工作区形态而提交 blob 为 LF；摄入副本按来源工作区字节复制，提交时由 core.autocrlf 归一为与来源相同的 blob。",
  "not_imported": [
    "来源 worktree 未跟踪材料：_r56_parallel/tmp/（7 个 workload 诊断日志）、_r56_activation_prep/evidence/delivery-discovery-acceptance-final.{json,exitcode.txt,stderr.txt}、delivery-discovery-correction-final.{json,exitcode.txt,stderr.txt}、delivery-discovery-closeout.{json,exitcode.txt}、_workflow/r56-activation-prep/{closeout,evidence,review}-run-1/",
    "来源交付的运行产物：_r56_parallel/trx/*.trx、reverse-mutation/*.log/*.trx 与 attempts/、process-crash-harness/ 与 process-crash-data/（本批在主线上重跑回归与突变，不搬重复中间材料）"
  ]
}


## source: Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md L5190-L5242 SHA256=cd32edf02cf6b95cfbf8dfce7585b40b813e6ad511699a5c63c22ba90189807e
本批在 R5.3 中的全部改动＝新增 §24.128（53 行，含范围/接收方式/集成回归/材料漂移登记/会诊结论与逐项处置）；文档其余 5000 余行未改动。
## §24.128 R5.6 主线迁移集成批（并行成果接收与集成验证，2026-09-28）

### §24.128.1 范围、接收对象与来源绑定

本批是**接收批**：把目标批次为「R5.6 主线迁移集成批」的两项已登记并行成果接入茶包主线，并在主线集成版本上重跑回归与反向突变。**不**施工 R5.6 生产接线（真实引用更新、candidate→active 激活编排、生产检查点消费、真实静止窗口、真实入口回执、目标机路径身份），**不**施工 R6.1 与 R6 diff guard，**不**重开 BO-8/BO-9、BO-6/7-D1、BO-9-D1、BO-13、SB21-3 BO-4、SB21-2 BO-10/12，**不**提前集成其他目标批次的成果。

- 来源交付 **r56-migration-audit**（thread `01a0e060-f824-7bb2-8490-d53b6e6dd88d`）：HEAD `c11cb45f6de2436b46c58b91d47f2c9768132ef3`，提交 `24928d294`／`97d05b931`／`ad248944e`／`c11cb45f6`；报告 `_r56_parallel/report.md` SHA-256 `044A62D0…9D97`、`acceptance.md` SHA-256 `377FC50C…55AE`（接收时逐字节复核一致）。本次重查其来源 thread 最新 turn 为 **completed**，其自身收口亦明确「后续主线集成属独立目标、本批未合入主线」。
- 来源交付 **r56-activation-prep**（thread `01a0e481-0b6d-74d0-a915-ac20cbaac861`）：HEAD `90588159b4769c41284ade475052d8b56f92e2d6`，提交 `0b606fe38`…`90588159b`；报告 SHA-256 `BEE48867…4FED`、`delivery-registration.json` SHA-256 `D8AAE0C5…90BF`（同上复核一致；最新 turn completed）。该包把 §21.10 启用门归并为 **A–F 六项前置**并给出正式消费批最小实施顺序。
- 两项的共同基线均早于本批开工 HEAD `8a3ee6c4c`（`8a4b8d988`／`5e7e7e22f`）：差异只涉及 SB21/Wave3 非迁移文件，迁移组件与 R56/R58 夹具逐 blob 相同（`blob_equal=true`），本批据此重核依赖版本后接收。

### §24.128.2 接收方式（最小且逐字节可核）

- **产品/测试增量**：唯一文件 `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`，`git diff --numstat 8a3ee6c4c c11cb45f6` = `48 0`；以 `git checkout c11cb45f6 -- <path>` 导入后，主线 blob `cd121220…` 与来源 blob 完全相同，工作区文件 54228 字节、SHA-256 `4C101FF3…`，与来源 worktree 现行字节相同。
- **材料增量**（25 个文件；暂存内容 25/25 等于来源 blob，工作区字节 25/25 等于来源 worktree 现行字节）：`_r56_activation_prep/**`（17）、`_workflow/r56-activation-prep/{context.md,manifest.json}`（2）、`_r56_parallel/{report.md,acceptance.md,pre-edit-inventory.json,reverse-mutation/source-hashes.txt,reverse-mutation/followup/{results.json,Run-AssertionMutations.ps1}}`（6）。
- **收纳位置**：为避免顶层 `_r*` 目录被 `tools/mistletoe/deliveries.py` 的「未登记报告」扫描误判为新交付，来源材料在主线收纳于 `_workflow/r56-mainline-integration/source-delivery/`；路径映射、逐文件哈希与未收录清单见该目录 `INTAKE-NOTE.md`、`intake-equivalence.json`、`source-delivery-line-endings.json`。〔2026-09-28 第 1 轮会诊建议级修正：原文误记 `_r56_activation_prep/**` 为 18 个文件（18＋2＋6＝26），实际为 **17＋2＋6＝25**，与 `intake-equivalence.json`/`source-delivery-line-endings.json` 的 25 条材料记录一致；无文件丢失，仅计数笔误。〕
- **刻意未收录**：来源 worktree 的未跟踪材料（`_r56_parallel/tmp/`、`delivery-discovery-*` 收口扫描原始输出、`_workflow/r56-activation-prep/*-run-1/`）与来源的逐次运行产物（TRX、突变日志、崩溃 harness 与运行数据）。回归与突变在主线上重跑，不搬重复中间材料。

### §24.128.3 集成版本证据（主线上重跑，不复用来源数字）

- **构建**：助手项目与测试项目 Rebuild 均带 `-p:DeployToBgiTools=false`，exit 0（助手 59 警告/0 错误；测试项目 84 警告/0 错误，导入前后各一次）。部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant` 在全部构建与测试前后的清单（文件集合＋逐文件 SHA-256）逐项相同（1158 文件，`deploy-target-comparison.json` 判 `identical=true`），即**未留下可观察的部署文件变化**；按第 1 轮会诊口径，不据此主张「过程中从未写入」，且 `-p:DeployToBgiTools=false` 是控制手段而非过程全称证明。
- **同条件基线**（导入前 HEAD `8a3ee6c4c`）：定向三类 **78/78**；助手全量 **1567 passed／2 skipped／0 failed／1569**。
- **集成后**：定向三类 **81/81**（三行 theory 的 testId `b1efc0c6-…`／`7481b2d1-…`／`bb416257-…` 全部 Passed）；助手全量 **1570／2／0／1572**；testId 差集 **added=3／removed=0／changed=0／unchanged=78**（定向）与 **added=3／removed=0／changed=0**（全量），见 `testid-comparison.json`。导入前基线与上一子批 `wave3-bo8-bo9` 自己的最终 TRX 逐 testId 相同（0 added/0 removed/0 changed）。**口径收窄（第 1 轮会诊建议级）**：上述只支持「新增 3 个测试实例、本次回归未观察到既有实例结果变化」，不证明「既有行为完全不变」；两个跳过用例、未纳入该测试程序集的路径、并行/时序/环境差异、同 testId 下未被断言观察的副作用与生产接线运行行为均不在其覆盖内。
- **反向突变**（集成字节上重跑 5 项）：`extra`／`missing`／`changed` 命中 `:line 188`、`commit-refusal` 命中 `:line 190`、`production-gate` 命中 `:line 196`；均 baseline Passed／mutant Failed（构建 exit 0、测试 exit 1）／restored Passed，源码逐字节恢复（`C2D4A122…`）。其中 4/5 的 mutant SHA-256（CRLF 形态）与来源 `results.json` 记录**完全相同**（`56BF4785…`／`E47AEDE7…`／`09BDC1CC…`／`2CA29B7A…`），证明是同一内容上的同一突变；`extra` 一项来源未记录精确 needle，本批为语义等价的独立文本突变（`96C634C9…`），不假称与来源相同。

### §24.128.4 材料漂移登记与权威口径（建议级）

- 上一子批 §24.127.3／§24.127.6、`_batch21/b21_plan.md`、SB21-4 交接稿与总计划的散文记「助手全量 1566/2/0/1568（基线 1562/2/0/1564）、testId 1564 unchanged／4 added」，而该子批自己的最终 TRX（`_workflow/wave3-bo8-bo9/final-v9/assistant-full-final.trx`）与基线 TRX 实为 **1567/2/0/1569** 与 **1562/2/0/1564**，差集 **5 added／0 removed／0 changed**；多出的一行是第 2 轮会诊新增夹具 `WorkflowRunnerTests.Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode`（见该子批 `consultation/review-outcome-v2.md`）。散文数值与该子批 TRX 不一致，**权威以 TRX 为准**；本批不改写已审且与登记哈希绑定的来源工件，只登记口径（同 §24.126.4 口径）。
- 连带影响：本批开工前在风险矩阵 `INT-S2.expected` 中按上述散文预登记绝对值（1566/1568 → 1569/1571），实测为 **1567/1569 → 1570/1572**（+1）。差额已定位为上述散文漏记，**不是**本批导入所致——本批导入造成的差集恰为 added=3／removed=0／changed=0。冻结矩阵行按工具规则不改写，故以本节登记差额。
- 另一条如实登记：来源 `extra` 突变未记录精确 needle，本批无法证明其与来源 mutant 逐字节相同（见 §24.128.3）。

### §24.128.5 会诊、设施核验与边界

- 设施：`workflow.py begin`（开工快照 `opening.json`，7 行风险矩阵）／`audit --stage review`＋`verify`／`closeout`＋`verify`；证据索引、`git status --porcelain`、本批未暂存/已暂存 diff 与材料外变更区分随快照保存。工具只做机械核验，`quality_verdict` 恒为 NOT PROVIDED。
- 会诊：本子批独立计数（上限 8 次，已发失败/超时计次），固定 `gpt-6-astra`／medium 只读渠道；逐轮记录见 `_workflow/r56-mainline-integration/consultation/` 与同目录 `budget.md`，结论与逐项处置见 §24.128.6。
- 边界：本批只并入组件与准备材料，**不**构成 R5.6 生产接线或 R5.6/R5 完成；A–F 六项启用前置仍未闭合；BGI 产品入口、真实 User、R5.8 签署、E3/E4/E5、热键面与生产进程门继续关闭；未做实机或生产验证。停驻语义生产可达性结论、BO-8/BO-9、BO-6/7-D1、BO-9-D1 状态未被本批改变。
- 子 Agent：本批派 1 个固定开工 ref 的只读子 Agent 做独立核查（来源报告/验收矩阵与主线实现的一致性、A–F 是否已有接线、导入 theory 的断言是否弱于主张）；报告按材料索引登记（`_workflow/r56-mainline-integration/subagent/`），其结论仍由主执行者按最终源码复核，不替代回归与突变。

### §24.128.6 会诊结论与逐项处置

（本小节在会诊返回后按同一子批计数追加。）

### §24.128.6 第 1 轮会诊结论与逐项处置（子批计数 1/8）

- **渠道/模型/强度**：既有 GPT 会诊工具（只读，自动附本批 `git status` 与 staged/unstaged diff）；`gpt-6-astra` / `medium`；attempts=1，返回报告。请求与预检见 `consultation/review-request-v1.md`、`consultation/preflight-v1.json`（允许文件 10 个、191,784 字节；含自动附加的 status 与 diff，预估约 46 万字节 ≈ 11.5 万 token，占有效窗口约 4 成，未触发本地回退条件）。
- **结论原文要点**：「所附代码与 diff 未显示本批引入 MUST／IMPORTANT 级缺陷；F-4 判为建议级设计边界，可留待下一次重绑定源码哈希的批次处置……本范围内未发现已确证、仍未闭合的 MUST／IMPORTANT」。**MUST 0 项；IMPORTANT 0 项；建议级 4 项**。
- **逐项处置（全部采纳，同一修复批）**：
  1. **建议级-1（F-4 处置）**：判定为「授权=提交+标记+无阻塞、快照完整性由提交/回滚路径保证」的**设计边界**，非正确性违约；登记为具名建议级残项 **R56-D1**（提交后快照损坏 + 直接授权交错无覆盖），处置条件＝「在下一次重绑定 `MigrationSwitchTransaction.cs` 哈希的批次补合同说明与直接交错夹具；若生产合同日后要求『失去回滚能力立即停执行』，须落实检查与文件并发保护，单次授权前重验亦不能消除外部写入竞态」。**不阻断本批收口**。
  2. **建议级-2（材料计数笔误）**：§24.128.2 原记 `_r56_activation_prep/**` 为 18 个文件（合计 26），实际 17＋2＋6＝25；已就地更正并加注，与 `intake-equivalence.json` / `source-delivery-line-endings.json` 的 25 条记录一致；无文件丢失。
  3. **建议级-3（`VerifySnapshot` 调用点计数）**：本批 `findings.md` F-4 原写「三个调用点」漏记 `RecoverOnStart`（约 `:698`）；实际调用点为 `:539`（RehearseRollback 前置）、`:597`（Commit）、`:641`（Rollback）、`:698`（RecoverOnStart）。已更正；因有效已提交态在 `:698` 之前返回，结论不变。
  4. **建议级-4（措辞收窄）**：(a)「既有行为未受扰动」改为「本次回归未观察到既有实例结果变化」，并列明未覆盖风险面；(b)「部署目标未被写入」改为「未留下可观察的部署文件变化」，不主张过程中从未写入；(c) F-3 的 `extra` 项改称「针对同一检查目标的独立突变」，不主张与来源 mutant 语义等价；(d) `INT-S2` 记为「绝对计数偏差已解释，成员差集主张通过」，不改写冻结矩阵行。
  5. **材料澄清**：只读子 Agent 报告 Q5 引用的 `1482/1484`、`1479/1481` 是**来源交付 A 的隔离 worktree TRX**，不是本批运行数；已在摄入副本追加标明来源的编辑性注释（原文逐字保留）。
- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。无未闭合 MUST/IMPORTANT，故按纪律 R2 无需追加验证轮；新增的 4 项建议级已按上表在同一修复批处置并登记。
- **边界**：会诊只审阅所附材料与 diff，未独立核验原始 TRX、突变脚本与逐项日志；本批据此不主张无条件完整验收。生产门、真实 User 门、R5.8 签署与生产进程门继续关闭。


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt L327-L402 SHA256=543a5687b9b75c263d2dcdab21bfd0fc24ac41c36c0f24499a09614d1c54ee37
本条为最终一次再生新增的声明行区间（清单 621 行，618→621，+3/-0）；清单全文哈希随所有 current 证据的 source_sha256 与 claims-diff.json 绑定。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7E3723C173A7DA0C8FECB72FF73B01A60CA3C122153FB763CC9733960CB8E2061- **结论原文要点**：「所附代码与 diff 未显示本批引入 MUST／IMPORTANT 级缺陷；F-4 判为建议级设计边界，可留待下一次重绑定源码哈希的批次处置……本范围内未发现已确证、仍未闭合的 MUST／IMPORTANT」。**MUST 0 项；IMPORTANT 0 项；建议级 4 项**。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7E9073703DAFE8B505321497CA3A37F51A5018869E65DFB4E6DB926DE7BD1A0C1- 裁定：首轮 4 必改＋3 重要；SOL 第 2 轮判 3 项未闭合，第 3 轮判 `state:null` 必改，第 4 轮判全部闭合且无新增必改、同意限定收口。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7E92B274E07EA8EE4F10F1F63ED2A965727CE5B18E87598D57E148D6025A15EA1> **[更正·2026-09-22 批次五十]** 上句为**当时口径**：P8 的**判据接缝＋夹具**已交付（见 **§24.62**）——
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7F9BAE5F12E995153E0A5C3E7D405BB06F3C31A50B9757457D7033021702D9D11**未验收**，按 §16 收口判据**不得计入「已覆盖」**（§16／§16-A 已同步为 2 项已覆盖／4 项部分）。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md7FF071BED6D659B56272EB814642DDA0847D945CD00489102C6A696FEC0209D01| 11 | **S5 通用 `SendCommandAsync` 直发粗化** | R5.8 安全粗化批（施工方：守卫负责人） | **已交付（组件/宿主层）**（[批次三十四] §24.46 字面量 6 操作类别／9 种拼写→[批次四十三] §24.53 非字面量 10 文件→[批次五十一] §24.63 **间接…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md80DD4EC8C1253AA1549E53BD6D9E4DA8EFA6A553DFB2110D76AF1E19BD3AEFF71| 4 | 裁决 claim 发布失败/证据版本绑定、恢复补终态、台账读写校验未形成全路径 fail-closed 证据 | 未闭合；逐项复核仍需完成 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md80DDF01D0BFC70983CFED44C6BE7AABB647CF583049394FD85354333A54606A11| §24.41-C | 10 | 恢复入口再次取许可、真实磁盘故障分布 | **部分已交付（整行未整体验收）**：重启后阻塞半＋**子项「恢复入口再次取许可」**已交付；**仍欠**＝子进程级重启与真实磁盘故障分布（门禁保留） | 部分证据 | §24.52／§24.60 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md81D7B581B4601E8E67480F720408281D7FBBF84E30DB9F5AB53F67420992203C1"仍存活停驻 + 每个停驻同轮序号更早的从未执行出现"的未履行恢复义务并按计划全序重入
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md825BB94E7C0BAC32A93C2A3E676554F4EE14C61922EE0A139228A6A54F49D3741| 50 | 重要 | 总计划 §3.3 R5 行「**R5.0–R5.7 已完成**」**效力拔高**：未限定证据层级，与 §7「生产接线仍关闭、P50 等门禁未闭、未签署」冲突 | **已修**：该行改为「**R5.0–R5.7 组件/设计层按各自范围收口（≠ 生产启用、≠ 验收签署；R5 整体仍未完成）**」，并…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md85762AB383BD60FC1F9EEE655542A85233D09F596235E6D9236B0F668412CD861收尾原文为「本批是否仍有未闭合的 MUST/IMPORTANT：否」。逐项原文见 `consultation/review-outcome-v3.md`。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md859A37E8D27E1800A45C13862F0E5BF9A602D48CEEEC118913BEFEBC4B73C6DF1| ext 前置／收尾的执行与终态 | 统一结果须区分 `Succeeded/Failed/Cancelled/Unknown` 与物理退出；不可逆动作先落不可重放 intent，实际结果另记 | `RunOpAsync` 失败返回 `false`，协调器将 `false` 记为 `completed`；破坏性收尾动作…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8674C4A4BAD8661F4324DCAD6D7CC9A8DDCA5BA433B0A4BCA5EAA57A3FBA42F31冻结合同未改、真实事件会送达、安全网会被调度、消费者确实完整准入、运行时绝无重复发送——这些均由本节的命令证据与本批的"未接线"立场分别承担，
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md86BD60E3C152B0611611475C231F17D16CC422D534F772EEAF4F7C3A975AF8931第二轮判 ③ 未闭合、④ 部分闭合，并新增 P1（`running` 缺失被当作"没有活动根"＋15s 分支绕过三态）；第三轮判 P1 闭合、新增 P2
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md879643317F36AD41A1B480AA59ACADA168072E40D22F4AEC17F214576D4128E81**B. 状态**：§24.41-C#10 记「**部分已交付·未验收**」——**重启后阻塞半**已交付（本节；证据含所有权代次更替、许可/键/身份不变、零新增预观察）；
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8796CBCB8D66214714FAE723EA68530756EABF294F9FE1F926F55DA5B8DAA7A01| 生产映射唯一事实点 | `RunningOccupantFacts.FromStatus(status, ledgerOccupied, ledgerUnknown, bgiEpochVerified)` | 台账不可读／快照缺失／**纪元未核验**／快照过期 ⇒ Unknown；台账有已受理未终结 ⇒ 占用但**…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md87E6B96129FEA9419AD3C226322C884888F572B83C496E2AB7BB2DB31822D3F31- 会诊：**GPT-6-Astra／medium 三轮各 1 次成功**（首轮 3 必改＋3 重要；验证轮判 ①闭合、③未闭合并新增必改 c；
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md894EAEF276AD4510FA2CAA45B7D2D0A101051DDE448D98A0C051B62079D19BFE1**局部施工与复审**：代码以入口显式 `ProjectRegistryOutcome` 为边界，注册表锁内复制结果；明确拒绝／可证失败才投影 `failed`，已确认的前置成功才投影 `completed`。未知、取消、逃逸异常及仅有破坏性动作意图时保留 `ResultUnknown/result_unknown`，…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8AD6D948ADF86C971E26BA1EF60E7C52C5837FED5BA6B6C4EA38AE5AEB60D3D01- **R3**（收口轮，**无必改、无未闭合重要项**；3 建议全采纳）：页首「仅含 owner 既有未提交文件」与
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8BB3C28725E15D48B2F7205F587007050DE5591AF604693BA44A00E96F15AC771- 四项原级义务在来源侧由 owner 批准的两次追加独立会诊按**原等级**裁定 closed：BO-6 R19 **MUST**、BO-6 R21 F4 **IMPORTANT**、BO-7 R21 F1 **MUST**、BO-7 R21 F2 **MUST**；两份报告的收尾结论均为「本范围仍有未闭合的 MUS…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8CDDAB59B6593D428556E9311CFB817E513D9AE79607642326FCA912BCDBA1891⇒ 本批**不声称会诊已闭环**，按纪律以「owner 一次性批量裁决项」登记。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8E2883FCF8EACA5C9AA4023C7419F6F7F0CFD1123DA9BB30E87B848A0F5D833613. **终态（完成层报 `Succeeded`／`Cancelled`／`ExecutionFailed`）且接管一致性验证通过**：受理→接管台账落盘→**同一次权威发布写 `ExecutionResult` ＋ `PendingTerminal`**→台账转 `Terminal`→关闭 Submission（或按…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8EB3FD01AA65CAB8F38B8D7FA4F33F1A4C17F7E09BCA2F93DB31D7F4E8BF030F1**与旧轮次的接入边界**：运行中低级请求必须先进入等待登记，不能先走 `ProcessRoundAsync` 再把输家回队列；现有整轮会把落选者写为终局 `NotSelected`，占用分支只保留获选者。等待激活后仍由唯一绑定的 Operation 进入原轮，但落选时应回到其等待绑定而非按普通 `NotSelecte…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8ED31BB9E69751B02991D93A458F2BC5BBE87D653EAC676F95AE546EA42FB4181### 24.79 BGI 物理执行入口清点（2026-09-24；源码静态核对，未验收）
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md8F797BEF57642D46B6AA941830D65CED77728B69993E7A6ACB9D5934B51D01E21其余残项不受影响。**不得**据此把「S2/S3 组合根反例」整体计入已完成。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md91FA96965084B1E1D2C65DFD36E3C83BC3A2451B448ACF6880E0C45D56CDD02E1| B 预留持有者到期／取消，或 BGI 收到 F11 | 分三支：未受理且零发送可撤销；**已编号受理但未消费**须原编号取消已耐久、permit 失效且迟到派发不可能，才释放预留；已消费则定向停止并等退出。任一分支的撤销与消费同槽门 CAS 排他 | 未确认撤销时仍挡其他起步；F11 对当前实例和唯一预留分别处置 …
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9246F36F00998AF68ABCA39F61EF63FE4E900BBECE4CC15ADF60FF9ADE33C4DA1**B. 状态**：§24.41-C#4「G4a 启动移交/暂停续行的来源登记」由「已移交·未验收」改为
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md92680AC508D28DEA03B813E56C4E098B68EEE0775BFD1CA10739B8B1C9F507F81> **`指针类型` 语义**：`完成证据`＝本行已交付且给出可核验证据；**`部分证据`＝本行仍有未完成部分**（存在已交付证据，
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9309B3E3A63D7E2840C6457C73BDCA66D532A91F48E82FEA2B6231A918A563581**验证会诊处置**：GPT-6-Astra／medium **1 次成功**，报告 2 阻断（均为代码尚未实现：I8/I10）和 2 重要（上述 `RecordTerminal` 事实纠错、共用 Executor/Skipped/旧消费者映射未闭合）。无会诊工具失败。§24.84 继续标注候选；高优先级设计矛盾尚未清…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9429A16CD043F4D92097CD20844F3419BE035414BEE37916555D57C8B45320461- 本批仍未接线：**生产零消费点、零发送**；生产入口门／真实 User 门／R5.8 签署**继续关闭**。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md968080F86A2A791FE139421CEA561495CBE67516D59A292CE613AAE2144CB06C1> 但**不得**读作已验收）；`残项登记`＝已按 §24.55 格式登记、未验收；`owner 裁决`＝待 owner 书面裁决。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9784CBBC31BD1499FEBCC45B44A678CA21BEFD66115B9A559507969D3417E5451**无必改、无未闭合重要项**，机械闸门绿——**收口未用例外**。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9967FF3F5B22F67B4D3625B60CA80072CE6F437156C3D648F67BE70AAF36407A1### 24.108 落地登记：批次完成类型拆分（D5）（2026-09-24；**纯函数＋夹具，生产零消费点，未接线**）
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9999CA3A949547084EAE09D57FDE5145C57CA3A11E12C4A9BEBD00B7E73E7E6B1| 13 | **§24.33 范围残余**：节点后继/外部启动路径「调用者≠获选者」端到端证据 | R5.8 真实入口层（施工方：入口夹具负责人） | 真实入口层绑定证据 | R5.8 门 | 已移交·**未验收** |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9A23764D02A8E70120CCD40E27412B5861CAEAB51A49610CB30A1EF8D482B7AB1- 边界：本批只并入组件与准备材料，**不**构成 R5.6 生产接线或 R5.6/R5 完成；A–F 六项启用前置仍未闭合；BGI 产品入口、真实 User、R5.8 签署、E3/E4/E5、热键面与生产进程门继续关闭；未做实机或生产验证。停驻语义生产可达性结论、BO-8/BO-9、BO-6/7-D1、BO-9-D1…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9A323A5E52F1015226D4AB938203184847244506B3243D4EA310A6158C822FEE1| D14／B0 物理槽预留顺序 | 对“高级／同级抢占、停止后退出、原生入口撞车”统一为**先绑定被切实例和继任者建立排他预留，再定向停止**。自然完成先于预留时，对空槽 generation CAS 建立预留；入队后预留连续归属该队列项，pump 识别自有预留，起步时转为继任执行实例，不在“已排队、尚未执行”时释放…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9F4C4459B59A09F25C9A32317AC3FE5C3947F4743D85A1F1C8F9B40C1041965B1- **BO-9 反例③（第 1 轮会诊 IMPORTANT-1，多节点无循环 candidate/rescue 跨轮）**：计划 `[A,P]`（无循环）+ `A@0` 已完成、
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md9F73858066624326EE9CAFFDB56F1FA35167FC8B6DE6F46980BA33A27DFA19F31| §24.55 | 1 | 生产归属事实源缺失（自有占用豁免恒不生效） | 已移交·未验收（R5.8／控制面） | 残项登记 | §24.54 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA10898732CE4F674773305E609F63E8222A871E9E0A3A35A602379F29454EA451| `BatchReconcileDecider` 注释与实现一致性 | ⚠ 本批已改准受影响注释（`Attach` 实际按 `IdempotencyKey == RequestKey`，非 `generation+name`／按名）；匹配实现未改，仍属未接线组件 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA16F99860AA9B458584C58655D5D1AB61B6EFB140E6285E76570F1EF908CEB421> **本结论不代表生产开门、不代表 R5.8 签署、不授权真实 User 目录切换**；所有「已移交·未验收」项目继续受原门禁约束。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA1FD2FAAD4A00F602CBBB88620E69042F9ADDDE83E0C3A733FCB94E3D5966D011owner 待决项一次批量交付」，本批**不**自称「会诊已闭环」；是否再开第 4 轮由 owner 一并裁决
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA243DAA5971ADB7BCB64B6A460AD6AC1C2DB40E65BFEADF51D367AE8CF052AA11| 提交点 | 通道／入口 | **测试接线态** | **生产态** | 证据 | 未闭合 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA25EC9B635B455163F5F66A44A68F8F251B6D45F2E200E631A821C2490EE64FF1D2 前置引用＋evaluator／D3 重评触发／D5 完成类型拆分五组件生产**全部未接线**（休眠合同）——
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA34402F4C0BDD06D15CFB85C6CAE8A82C0AD4C04E0325A6575BB479AC447652B1| 14 | **§24.38–§24.39 范围残余**：E3/E4/E5 关闭保护交错；恢复路径令牌透传；`RetryAsync` 令牌合同 | R5.8 取消链收束批（施工方：宿主关闭保护与恢复链负责人）；owner 已裁定本期不加单次点击撤销令牌，软件关闭须取消在途发送并查证 | 各入口关闭/回执/未知结果交错…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA4E3605C3B81B9A3EEE501B62FF40ED10EE3F7BFD92F60C0A4235D2F9C50E7401| 决策器生产消费点 | ❌ **零消费点、未接线**：本批的类型拆分没有接入任何生产决策；`CompleteWithUnresolved` 目前只有夹具读者 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA5076154F4AC810E2565C845C935408DC7CBD5F72181207D1F3210A18998FDA91### 24.109 落地登记：新增 `AdmissionResultKind.WaitLocally`（D1）（2026-09-24；**生产零消费点，未接线**）
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA660524292BB6F14F522BC738E6D2A822912AE2AC7244CAA8BF7361BFBAE542D1| 1 | **生产归属事实源缺失**：控制面快照只有布尔 `TaskRunning`，无法证明占用归属本候选 run ⇒ 生产侧自有占用豁免**恒不生效**（节点子提交在自有驱动在飞时仍被占用阻断） | R5.8 真实入口层／控制面（发送权威侧） | 控制面暴露**带来源**的占用事实（runBinding/jobI…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA6B252D5258B3B46EE3DDD99E8B23801150D185760A6A13272AA8CE086EEE3A51**首节点绑定 0/4 未验收**（M1 未实现）；**提交路由**面板/移交＝测试接线态已验、续行/恢复＝生产已接线（除 **S8b 策略收尾来源未接入**）。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA76A5DFCE5F193B0AAD0F6CB7FC729794104FA949908409E010A2F1E923E7C361未接线时正确为绿），`_batch15` 阶段 TRX＝`batch15_red_core.trx`；首轮实现后 **20/20 全绿**
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA76C2F70EC0814B725BCF147E6CBD02097C8EDB4D0538326AF6855E77F94B2051- **会诊（gpt-6-astra/medium 共 12 轮，auto 预判均自选 astra；台账 `C:\Users\Administrator\.tools\zcode-relay\test\ledger-b17.json`）**：第 1–11 轮逐轮处置（材料完整性／清理路径／判据升级／归因更正／共享运行器…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdA9968F1DAA59EAD5A20779E577F2E01A287BF0FC412CE535C04906349CEDE2811| D2 | 重要 | C 表多项只写「B4／B4 后续」⇒ 循环移交、以移交代替验收；且**遗漏 7 项仍有效残项**（P21 入口表、P49 范围残余、§24.37 未决责任阻挡的门面级证据、§24.33 范围残余、§24.38–39 范围残余、§24.40 范围残余、S5 粗化） | 已修：C 表重写为 **17…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdAC21501FA39FCB736F78C6F73D5D30F22194148A82C05CE959052D67865999DB1- R2（3 建议，**无必改、无未闭合重要项**）：#2① ContainsKey 强化建议采纳后**实测反例成立撤回**
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdAC60AA840D6CD584B6C6212E462D353DA33640746BB217E851105910637BEE7B1沿 `plan.Next` 推进撞**已完成** `n3` ⇒ **n3 被第二次提交**（外部副作用重复发生、不可撤销）。开工字节实测提交序列
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdADA1C05E645AC239EB7BB7672AA5C6505800206DE86C6D7E9AAC593F92CB86D41R5.3 .bak 历史备份残留**不随本批提交**）。R2（复会诊）＝**无必改、无未闭合重要项**，4 建议：C5/C7 粗体配对
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdADF33C7B491E2AAA799144C38F56CB2601E678CF0D4143C3D691828F201BE3891| 79 | 重要 | 交接稿标题仍「截至批次五十」；B′ 待办仍列 #11 | **已修**：标题同步为批次五十一；B′ 从待办移除并标注「已交付（组件/宿主层）」 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB10FC181E740EADDED0DDF05A87F11385F20A08DB7A77CFF674068ADF1EA36051**B. 状态**：§24.41-C#3「首节点绑定（§12.3 M1）」由「已移交·未验收」改为
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB28B419AD23E461BA564962FD08E5433857D21A7FB2EACE80158F9F0DCF4AE8D1本子批在茶包主线只施工两项已登记未闭合 **IMPORTANT**：**BO-8 R29**（恢复点之后的推进段无完成过滤）与
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB29F5DEDA3D3404F8803277406BC0BA47DE2688A3086CB3675699E94479FBA421| §24.41-C | 4 | G4a 启动移交来源登记 | **已交付（组件/宿主层）** | 完成证据 | `TaskCenterSuccessorPathGateTests.NodeSubmit_AfterHandoffStart_InheritsRecordedFixedScope` |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB4B61FD63B591E42EDB526212B1DE590782F86992D53E9BDBC0ECB15C637BFD912. **本轮已落地的安全阻断**：`ContinueUseAsync`／`RetryAsync` 对 `PreemptConfirmPending` 返回 `NeedPreemptConfirm`；`ValidateAndOccupy` 在跨进程原子占位事务内再次拒绝该标记，且本轮新发送不再顺手清标记。`Preemp…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB75E5A2EACE1962029B168FCEDE360E83EB1BA34940BA7F8D7F81A4385C27D611把「逐项校验 `item.State`」改成「只看集合是否非空」之类的突变**尚未执行** ⇒ 该夹具目前**只证明正向行为**，
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB7D65826559758869081F647902117C519A91D2B7F609865046DAAB8F17B96CF1**B. 状态（[首轮会诊重要项处置] 不改写整行效力）**：§24.41-C#10 **整行仍记「部分已交付·未验收（未整体验收）」**——
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB81DEAEB26177852E054D6EADAE8F780CA5F65316EB352C70EFDFFF72FEC58D21**B. 状态**：§24.41-C#6「集合②持续观察重绑」由「已移交·未验收」改为「**已交付（组件/宿主层）**」。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB9500C06CDD95DD80925ED3341B3247EA33E3784AAF302DC4F8379D24C3535091既不作重入点也不产生前插义务（第 2 轮会诊 IMPORTANT-2：否则旧标记会额外执行其同轮更早的未执行节点）。守卫两条："入口即链尾（持久 `TailReached`，
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB9B478706F3335C49EE22FE5A24212966ED10BAF94ECE5CEE9A816EEEC6C47271| 6 | **集合②持续观察重绑** | R5.8 实现闭合序列（施工方：宿主实现＋夹具负责人） | 重启后未终结台账记录**重绑观察责任**并可后续结算 | 外部启用门 | **已交付（组件/宿主层）**（[批次四十六] §24.57：观察义务**持久化重绑**（时点／句柄／单调次数）＋写事务内按完整发送身份复核＋…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdB9F74EA7D49520EE2E846F01934347151268BDC10464CE03D9DA546DA4984B311台账 test/ledger-batch20.json batch_obligations 数组：BO-1 登记异常收敛（接线批）、BO-2 面板 scope 注入源（接线批）、BO-3 跨代际重登记通道（Wave2 期间裁决＝冲突即合同信号，已 R35 落字）、BO-4 facade WaitLocally 映射（W…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBB19B8FB0206D113A5F75AC07FECA4C205C7F47788AD465EEB36500C911B193E1| 3 | `PreemptConfirmPending` 无可信确认入口；普通续用、重试和占位已 fail-closed | 安全阻断已交付，确认后继续及证据绑定尚未交付 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBB2ED328E7AD5B158B323B38958DA55BBE9DE2E6D33E101C11B25A4207ADCA2A1**C. 状态**：§24.41-C#5（P8）由「已移交·未验收」改为「**部分已交付·未验收（组件/宿主层）**」；
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBBC0B5075ED4420D3158259700FDA155BD674470110432A41B8A8D185D51D0621| §23.8 | 6 | 外部入口未纳入宿主生命周期、发送回调恒 `None` | **部分**：owner 已选择软件关闭取消并核查；宿主令牌已接入部分发送链，E3/E4/E5 全路径交错证据仍未闭合 | 部分证据 | §24.68 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBCCF2AB9A16C977CF6947983827AA2F6BB335EFB609C684E4956590097289AE51| #5 多实例／跨进程 | 「书面拒绝 + 登记」（与 #2 同源） | 随 #2 修复而改变；**跨进程单写者**仍属未接线事项，如实登记 | — |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBE80339C4EF6903C4032CA7384547A8FC5E02E1014A018F23430E81871735EDB1| 1 | **重要（必改）** | `_handled` 按稳定身份**永久**去重 ⇒ 占用期间先由 `NewCandidateArrived` 产过一次后，随后的 `OccupancyEnded` 不再产；同一稳定身份代表不同**代际**等待项时，合法的新代际被一并屏蔽 | **部分采纳**：①"同身份重复到达不…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdBFD716415D969151BCD27C12F635C07704E788FA93B9CD59F244FFA181076D891> 时点说明：本清单记录 batch20 冻结归属；BO-6/BO-7 的后续独立子批结果见 §24.125。BO-8/BO-9 仍保持原级未闭合。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC015CC0C5424152E9212942379AE4E53A663605D3CA3EFDF71797C1E5267326D1**B. 状态**：§24.41-C#17 记「**已交付（组件层）**」；其余残项不受影响。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC042A6923E232C1129F9FAA84023AF2D5D09773391B3D9B25100C17F649CC7CC1## 24. B3 外部启动生命周期补全设计（[2026-09-21 **已冻结**：第 23 轮会诊判「无必改项」，§17.4 收口；生产接线仍关闭]）
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC07C2D4C99D576D53D3A623B301CAE05A74AA8621D39D08486EAB20871D5BE951| 不可逆动作抛错且能证明零效果；或已部分执行 | 按各步骤独立证据判断，抛错本身不证明零效果 | 前者可 `Failed`；后者 `Unknown/Committing`，保留已完成步骤 | 部分效果不得整体重放；需动作专用对账 |
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC0E0F82C44A3718BA00F108A87B13A6EDD73CB3CF2670F67BA43E3F57F928E1C1| D1 等待结果 | 冻结 `AdmissionResultKind` 无等待值（`ArbitrationAdmissionService.cs` 第 87–112 行；调用方按值分流见 `TaskCenterHost.Admission.cs` 第 1042–1051、1232–1240、1589–1609、199…
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC1B6886983324DD0BE6CD5DC3C17472966ACC62351ACEA5ED1103C6692FFCB091> **整行仍未验收**（外部启动路径分类端点归 R5.8／外部启用门）。
Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.mdC1D71BAB7F17F15423080E551D7A8B6071C3BF322C5F19FF20FC9A96CD67A3D21- **计数**：本轮后子批累计 **1/8**（无失败/超时请求）。无未闭合 MUST/IMPORTANT，故按纪律 R2 无需追加验证轮；新增的 4 项建议级已按上表在同一修复批处置并登记。


## source: Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs L1-L1074 SHA256=4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed

using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6 事务迁移切换 v3** 夹具（owner 0 点击；**全程只用独立临时配置根**，绝不触碰真实 User 目录）。
/// 覆盖第 2 轮会诊 7 项必改：全入口持锁与「授权+执行」同临界区、RollingBack 幂等恢复、静止窗口全程且绑定会话、
/// 身份/路径/链接边界、未决事务拒绝开新、变更归属基线校验、结构+状态不变量与全字段完整性。
/// **能力边界（如实）**：仍未接线真实引用服务/激活实现与生产消费侧（本类只提供强制检查点 API）；
/// 静止窗口由调用方提供委托，夹具只用桩验证「未取得/非同会话 ⇒ 拒绝提交」。
/// </summary>
public sealed class R56MigrationSwitchTransactionTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R56MigrationSwitchTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private void Seed(string rel, string text)
    {
        var full = Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private MigrationSwitchTransaction NewTx(Func<IDisposable>? quiesce = null, bool requireQuiescence = true,
        Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, quiesce ?? (() => new NoopQuiet()), requireQuiescence, hook);

    private MigrationSwitchTransaction ArrangeActivated(string txId = "t1", IEnumerable<ChangeRecord>? changes = null,
        Action<MigrationStage>? hook = null)
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx(hook: hook);
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        return tx;
    }

    [Fact]
    public void Gate_Uncommitted_ProductionNotInvoked()
    {
        using var tx = ArrangeActivated();
        var runs = 0;
        var r = tx.TryRunProduction(() => runs++);
        Assert.False(r.Success);
        Assert.StartsWith("not_committed", r.Reason, StringComparison.Ordinal);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Gate_Committed_ProductionInvokedOnce()
    {
        using var tx = ArrangeActivated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        var runs = 0;
        Assert.True(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Lock_NotHeld_MutationsRejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.Equal("lock_not_held", tx.TakeSnapshot().Reason);
        Assert.Equal("lock_not_held", tx.RecoverOnStart().Reason);
        Assert.Equal("lock_not_held", tx.AuthorizeProductionExecution().Reason);
        Assert.Equal("lock_not_held", tx.Rollback().Reason);
    }

    [Fact]
    public void ExclusiveLock_SecondInstanceRejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var first = NewTx();
        Assert.True(first.BeginTransaction("t1").Success);
        using var second = NewTx();
        Assert.Equal("transaction_busy", second.TryAcquireExclusive().Reason);
    }

    [Fact]
    public void StateMachine_RejectsSkip_AndAllowsRollbackFromCommitted()
    {
        using var tx = ArrangeActivated();
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Committed));
        Assert.False(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.SnapshotReady, MigrationStage.Activated));
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.Committed, MigrationStage.RollingBack));
        Assert.True(MigrationSwitchTransaction.IsLegalAdvance(MigrationStage.RollingBack, MigrationStage.RolledBack));

        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
        Assert.True(tx.Rollback().Success);                     // 已提交仍可回滚
        Assert.StartsWith("illegal_stage", tx.Commit().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Quiescence_NotAcquired_BlocksCommit()
    {
        // 未提供静止窗口（requireQuiescence=true）⇒ 快照期未取得 ⇒ 禁止提交
        Seed("a.json", "{\"v\":1}");
        using var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, quiesce: null, requireQuiescence: true);
        Assert.True(tx.BeginTransaction("t1").Success);
        Assert.Equal("no_quiescence_window", tx.TakeSnapshot().Reason);   // 采集期即须有存续窗口（不得事后补资格）
    }

    [Fact]
    public void Quiescence_SessionBound_ReopenCannotCommitOnHistoricalTimestamp()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Dispose();                                   // 模拟进程退出（窗口应随之结束）

        using var reopened = NewTx();                   // 新会话：不得凭历史时间戳提交
        Assert.True(reopened.TryAcquireExclusive().Success);
        var commit = reopened.Commit();
        Assert.False(commit.Success);
        Assert.Equal("no_quiescence_window", commit.Reason);
    }
    [Theory]
    [InlineData("extra", "snapshot_untracked_file:rogue.json")]
    [InlineData("missing", "snapshot_file_missing:a.json")]
    [InlineData("changed", "snapshot_hash_mismatch:a.json")]
    public void SnapshotIntegrity_CompleteFileSetAndBytes_AreVerifiedAndCommitFailsClosed(string mutation, string expectedReason)
    {
        Seed("a.json", "{\"v\":1}");
        Seed("sub/b.json", "{\"w\":1}");
        using var tx = NewTx();
        Assert.True(tx.BeginTransaction("snapshot-integrity").Success);
        Assert.True(tx.TakeSnapshot().Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        Assert.True(tx.RehearseRollback().Success);

        var manifest = tx.LoadManifest()!;
        Assert.Equal(new[] { "a.json", "sub/b.json" }, manifest.FileHashes.Keys.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(HashOf(Full("a.json")), manifest.FileHashes["a.json"]);
        Assert.Equal(HashOf(Full("sub/b.json")), manifest.FileHashes["sub/b.json"]);
        Assert.Equal(manifest.SnapshotManifestHash, MigrationSwitchTransaction.ComputeSnapshotManifestHash(manifest.FileHashes));
        Assert.Equal(File.ReadAllBytes(Full("a.json")), File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, "a.json")));
        Assert.Equal(File.ReadAllBytes(Full("sub/b.json")), File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, "sub", "b.json")));

        switch (mutation)
        {
            case "extra":
                File.WriteAllText(Path.Combine(manifest.SnapshotPath, "rogue.json"), "extra", new UTF8Encoding(false));
                break;
            case "missing":
                File.Delete(Path.Combine(manifest.SnapshotPath, "a.json"));
                break;
            case "changed":
                File.WriteAllText(Path.Combine(manifest.SnapshotPath, "a.json"), "tampered", new UTF8Encoding(false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        Assert.Equal(expectedReason, tx.VerifySnapshot());
        var commit = tx.Commit();
        Assert.False(commit.Success);
        Assert.StartsWith("snapshot_invalid:", commit.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
        Assert.Null(tx.LoadManifest()!.CommitMarker);
        var productionRuns = 0;
        Assert.False(tx.TryRunProduction(() => productionRuns++).Success);
        Assert.Equal(0, productionRuns);
    }
}
/// <summary>v3 夹具续（与上同类同文件，此块补齐其余必改项覆盖）。</summary>
public sealed class R56MigrationSwitchTransactionTests_Part2 : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _txRoot;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R56MigrationSwitchTransactionTests_Part2()
    {
        _root = Path.Combine(Path.GetTempPath(), "r56b-" + Guid.NewGuid().ToString("N")[..8]);
        _configRoot = Path.Combine(_root, "cfg");
        _txRoot = Path.Combine(_root, "tx");
        Directory.CreateDirectory(_configRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }

    private void Seed(string rel, string text)
    {
        var full = Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private string Full(string rel) => Path.Combine(_configRoot, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private MigrationSwitchTransaction NewTx(Action<MigrationStage>? hook = null)
        => new(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true, hook);

    private MigrationSwitchTransaction Activated(IEnumerable<ChangeRecord>? changes = null, string txId = "t1")
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        Assert.True(tx.BeginTransaction(txId).Success);
        Assert.True(tx.TakeSnapshot().Success);
        if (changes is not null) Assert.True(tx.RecordChanges(changes).Success);
        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
        Assert.True(tx.MarkActivated().Success);
        return tx;
    }

    [Fact]
    public void RecordChanges_AddedForExistingFile_Rejected()
    {
        using var tx = Activated();
        var bad = tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Added }]);
        Assert.False(bad.Success);
        Assert.StartsWith("change_baseline_mismatch", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordChanges_ModifiedForMissingFile_Rejected()
    {
        using var tx = Activated();
        var bad = tx.RecordChanges([new ChangeRecord { Path = "missing.json", Kind = ChangeKind.Modified }]);
        Assert.False(bad.Success);
        Assert.StartsWith("change_baseline_mismatch", bad.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordChanges_UnsafeOrDuplicate_Rejected()
    {
        using var tx = Activated();
        Assert.StartsWith("unsafe_path", tx.RecordChanges([new ChangeRecord { Path = "../e.json", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
        Assert.StartsWith("duplicate_change_path",
            tx.RecordChanges([
                new ChangeRecord { Path = "b.json", Kind = ChangeKind.Added },
                new ChangeRecord { Path = "B.json", Kind = ChangeKind.Added },
            ]).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_DeletesOnlyRecordedAdditions_KeepsOtherWritersFiles()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("gone.json", "{\"gone\":1}");                 // 快照期存在的文件（事务期将被删除）
        var original = HashOf(Full("a.json"));
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "added/x.json", Kind = ChangeKind.Added },
            new ChangeRecord { Path = "gone.json", Kind = ChangeKind.Deleted },
        ]).Success, "变更归属应按基线通过");
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Assert.True(tx.RehearseRollback().Success, "演练应通过");
        Assert.True(tx.Commit().Success);

        Seed("a.json", "{\"v\":2}");
        Seed("added/x.json", "{\"tx\":true}");
        Seed("other/y.json", "{\"other\":true}");        // 他方新增（未记录）
        File.Delete(Full("gone.json"));                   // 事务期删除

        Assert.True(tx.Rollback().Success, "回滚应成功");

        Assert.Equal(original, HashOf(Full("a.json")));
        Assert.False(File.Exists(Full("added/x.json")));
        Assert.True(File.Exists(Full("other/y.json")));   // 未记录 ⇒ 保留
    }

    [Fact]
    public void Rehearsal_ScopeInvalidatedByChangeSet()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "b.json", Kind = ChangeKind.Added }]).Success);
        Assert.Equal("rollback_not_rehearsed_for_current_scope", tx.Commit().Reason);
        Assert.True(tx.RehearseRollback().Success);
        Assert.True(tx.Commit().Success);
    }

    [Fact]
    public void PendingTransaction_BlocksNewBegin()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        var again = tx.BeginTransaction("t2");
        Assert.False(again.Success);
        Assert.StartsWith("pending_transaction_exists", again.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionId_Unsafe_Rejected()
    {
        Seed("a.json", "{}");
        using var tx = NewTx();
        Assert.Equal("invalid_transaction_id", tx.BeginTransaction("x/../../outside").Reason);
        Assert.Equal("invalid_transaction_id", tx.BeginTransaction("../evil").Reason);
    }

    [Fact]
    public void Roots_Overlapping_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new MigrationSwitchTransaction(_configRoot, Path.Combine(_configRoot, "tx")));
        Assert.Throws<InvalidOperationException>(() => new MigrationSwitchTransaction(Path.Combine(_txRoot, "cfg"), _txRoot));
    }

    [Fact]
    public void ManifestIntegrity_StageTamper_RejectedAndGateRefuses()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        var m = tx.LoadManifest()!;
        m.Stage = MigrationStage.Activated;                 // 单改阶段、不重算摘要
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void ManifestIntegrity_NullChangedFiles_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();

        var json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(tx.ManifestPath), "\"changedFiles\":\\s*\\[[\\s\\S]*?\\]", "\"changedFiles\": null");
        File.WriteAllText(tx.ManifestPath, json);
        Assert.Null(tx.LoadValidated());                    // null 字段 ⇒ 结构化拒绝，不抛异常
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);
    }

    [Fact]
    public void RecoverOnStart_RollingBack_IsIdempotent()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Seed("a.json", "{\"v\":2}");
        tx.Dispose();

        using var reopened = NewTx();
        Assert.True(reopened.TryAcquireExclusive().Success);
        Assert.True(reopened.RecoverOnStart().Success);      // 收敛为旧态
        Assert.Equal(original, HashOf(Full("a.json")));
        Assert.True(reopened.RecoverOnStart().Success);      // **重复启动幂等**
        Assert.Equal(MigrationStage.RolledBack, reopened.RecoverOnStart().Stage);
    }

    [Fact]
    public void RecoverOnStart_Committed_KeepsNewState()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        var rec = tx.RecoverOnStart();
        Assert.True(rec.Success);
        Assert.Equal(MigrationStage.Committed, rec.Stage);
        Assert.True(tx.AuthorizeProductionExecution().Success);
    }

    [Fact]
    public void CrashAfterStageWrite_NeverExecutable()
    {
        Seed("a.json", "{\"v\":1}");
        var seen = new Dictionary<MigrationStage, int>();
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage =>
            {
                seen[stage] = seen.TryGetValue(stage, out var n) ? n + 1 : 1;
                if (stage == MigrationStage.Activated && seen[stage] == 2)   // **写后**（第二次回调）崩溃
                    throw new InvalidOperationException("模拟写入完成后崩溃");
            });
        Assert.Throws<InvalidOperationException>(() =>
        {
            tx.BeginTransaction("t1");
            tx.TakeSnapshot();
            tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
            tx.MarkReferenceUpdateCompleted();
            tx.MarkActivated();
        });
        tx.Dispose();

        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.False(probe.AuthorizeProductionExecution().Success);
        probe.Dispose();
    }

    [Fact]
    public void RecoverFromPersistedRollingBack_ConvergesToOldState()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var phase = 0;
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stage =>
            {
                if (stage == MigrationStage.RollingBack && ++phase == 2)    // RollingBack **已落盘**后崩溃（恢复尚未开始）
                    throw new InvalidOperationException("回滚中断");
            });
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        Assert.Throws<InvalidOperationException>(() => tx.Rollback());
        tx.Dispose();

        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.Equal(MigrationStage.RollingBack, probe.LoadManifest()!.Stage);   // 持久化在回滚中
        Assert.True(probe.RecoverOnStart().Success);
        Assert.Equal(MigrationStage.RolledBack, probe.LoadManifest()!.Stage);
        Assert.Equal(original, HashOf(Full("a.json")));
        probe.Dispose();
    }

    [Fact]
    public void Baseline_CaseInsensitiveAlias_Rejected()
    {
        using var tx = Activated();
        Assert.StartsWith("change_baseline_mismatch",
            tx.RecordChanges([new ChangeRecord { Path = "A.json", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
        Assert.StartsWith("unsafe_path",
            tx.RecordChanges([new ChangeRecord { Path = "a.json.", Kind = ChangeKind.Added }]).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SameInstanceReLock_CannotReuseHistoricalQuiescence()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        tx.Dispose();                                     // 释放锁与窗口（代次前进）

        tx.TryAcquireExclusive();
        Assert.Equal("no_quiescence_window", tx.Commit().Reason);   // 同实例重取锁也不得复用历史资格
        tx.Dispose();
    }

    [Fact]
    public void Rollback_AfterCommit_AcquiresFreshQuiescence()
    {
        var quietCount = 0;
        Seed("a.json", "{\"v\":1}");
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () =>
        {
            quietCount++;
            return new NoopQuiet();
        }, true);
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        Assert.Equal(1, quietCount);                                 // 提交后窗口已释放

        Seed("a.json", "{\"v\":2}");
        Assert.True(tx.Rollback().Success, "提交后回滚应重新取得窗口并成功");
        Assert.Equal(2, quietCount);
        tx.Dispose();
    }

    [Fact]
    public void PendingCorruptManifest_BlocksNewBegin()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.True(tx.TryAcquireExclusive().Success);
        Directory.CreateDirectory(_txRoot);
        File.WriteAllText(tx.ManifestPath, "{ this is not json");
        Assert.Equal("pending_manifest_corrupt", tx.BeginTransaction("t2").Reason);
    }

    [Fact]
    public void TransactionId_HistoryReuse_Rejected()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        var again = tx.BeginTransaction("t1");               // 历史占用：事务号不得复用
        Assert.False(again.Success);
        Assert.Equal("transaction_id_in_use", again.Reason);
    }

    [Fact]
    public void ManifestIntegrity_NullSnapshotPath_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(tx.ManifestPath), "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": null");
        File.WriteAllText(tx.ManifestPath, json);
        Assert.Null(tx.LoadValidated());                     // 不得抛异常
    }

    [Fact]
    public void StateCombination_CommittedWithBlocked_Rejected()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var m = tx.LoadManifest()!;
        m.BlockedReason = "tampered";
        m.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(m);
        File.WriteAllText(tx.ManifestPath, System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.Null(tx.LoadValidated());                     // Committed + blocked 组合非法
    }

    /// <summary>**第 4 轮必改⑥**：已提交后快照损坏 ⇒ 回滚**先封锁**再失败 ⇒ 必须撤销生产授权（不得继续可执行）。</summary>
    [Fact]
    public void Rollback_BrokenSnapshotAfterCommit_RevokesAuthorization()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        Assert.True(tx.AuthorizeProductionExecution().Success);

        var m = tx.LoadManifest()!;
        File.Delete(Path.Combine(m.SnapshotPath, "a.json"));       // 提交后快照损坏

        var rb = tx.Rollback();
        Assert.False(rb.Success);
        Assert.StartsWith("rollback_snapshot_invalid", rb.Reason, StringComparison.Ordinal);
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);   // 已封锁
        Assert.Null(tx.LoadManifest()!.CommitMarker);                     // 授权已撤销
        Assert.False(tx.AuthorizeProductionExecution().Success);
        var runs = 0;
        Assert.False(tx.TryRunProduction(() => runs++).Success);
        Assert.Equal(0, runs);
    }

    /// <summary>**第 4 轮必改①**：清理未完成（新增文件删除失败）⇒ **保持阻断**，不得落 `RolledBack` 假报成功。</summary>
    [Fact]
    public void Rollback_CleanupIncomplete_StaysBlocked()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "added/x.json", Kind = ChangeKind.Added },
        ]).Success);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        Seed("added/x.json", "{\"tx\":true}");
        var added = Full("added/x.json");
        File.SetAttributes(added, FileAttributes.ReadOnly);        // 使删除失败（清理未完成）
        try
        {
            var rb = tx.Rollback();
            Assert.False(rb.Success);
            Assert.Equal("rollback_cleanup_incomplete", rb.Reason);
            Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);   // 不得报告完整回滚
            Assert.False(tx.AuthorizeProductionExecution().Success);
        }
        finally
        {
            if (File.Exists(added)) File.SetAttributes(added, FileAttributes.Normal);
        }
    }

    /// <summary>**第 4 轮必改③**：快照归属**精确绑定**本事务——他事务（同前缀）的快照路径必须被拒。</summary>
    [Fact]
    public void SnapshotIdentity_OtherTransactionPrefix_Rejected()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = File.ReadAllText(tx.ManifestPath);
        var tampered = System.Text.RegularExpressions.Regex.Replace(json,
            "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": \"" + Path.Combine(_txRoot, "snapshot-t1-b-other").Replace("\\", "\\\\") + "\"");
        File.WriteAllText(tx.ManifestPath, tampered);
        Assert.Null(tx.LoadValidated());
    }

    /// <summary>**第 4 轮必改④**：占号历史**先于 manifest 发布** ⇒ 占号后崩溃（有历史、无 manifest）也不得复用事务号。</summary>
    [Fact]
    public void TransactionId_HistoryOccupiedBeforeManifest_BlocksReuse()
    {
        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.True(tx.TryAcquireExclusive().Success);
        Directory.CreateDirectory(_txRoot);
        File.AppendAllText(tx.HistoryPath, "t9" + Environment.NewLine);   // 模拟「占号已落盘、manifest 未发布」的崩溃残件
        Assert.Equal("transaction_id_in_use", tx.BeginTransaction("t9").Reason);
    }

    /// <summary>**第 4 轮必改⑤**：含 NUL 的非法 snapshotPath ⇒ **结构化拒绝**（不得抛异常）。</summary>
    [Fact]
    public void ManifestIntegrity_NulInSnapshotPath_StructuredReject()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        tx.Commit();
        var json = File.ReadAllText(tx.ManifestPath);
        var tampered = System.Text.RegularExpressions.Regex.Replace(json,
            "\"snapshotPath\":\\s*\"[^\"]*\"", "\"snapshotPath\": \"bad\\u0000path\"");
        File.WriteAllText(tx.ManifestPath, tampered);
        Assert.Null(tx.LoadValidated());     // 不抛异常
    }

    /// <summary>**第 4 轮必改⑥**：授权/执行与回滚**互斥**（同一临界区）——执行期间回滚不得并行介入。</summary>
    [Fact]
    public async Task Concurrency_AuthorizationAndRollback_AreSerialized()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var runs = 0;
        var exec = Task.Run(() => tx.TryRunProduction(() =>
        {
            runs++;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "生产执行应已进入临界区");

        var rollback = Task.Run(() => tx.Rollback());
        Assert.False(rollback.Wait(TimeSpan.FromMilliseconds(200)), "执行期间回滚不得并行完成（同临界区串行）");

        release.Set();
        Assert.True((await exec).Success);
        Assert.True((await rollback).Success);
        Assert.Equal(1, runs);
    }

    /// <summary>**第 4 轮必改⑥**：`.tmp` 半写残件**不污染**权威 manifest（读取只看正式文件）。</summary>
    [Fact]
    public void PartialTmpWrite_DoesNotCorruptManifest()
    {
        using var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        File.WriteAllText(tx.ManifestPath + ".tmp", "{ half-written");      // 模拟半写残件
        Assert.NotNull(tx.LoadValidated());
        Assert.True(tx.AuthorizeProductionExecution().Success);
    }
    /// <summary>
    /// **第 5 轮必改⑥之一（逐文件恢复中断）**：回滚在**恢复第二个文件后**中断 ⇒ 配置根处于**混合态**且事务 `Blocked`；
    /// 重开实例 `RecoverOnStart` 必须收敛为**完整旧态**（两文件均恢复原字节）。
    /// </summary>
    [Fact]
    public void Recovery_AfterPartialRestoreInterruption_ConvergesToCompleteOldState()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("b.json", "{\"w\":1}");
        var originalA = HashOf(Full("a.json"));
        var originalB = HashOf(Full("b.json"));
        var restored = 0;
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stageHook: null, fileRestoredHook: _ =>
            {
                if (++restored == 1) throw new InvalidOperationException("模拟**第一个**文件恢复后中断");   // 制造混合态
            });
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([
            new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified },
            new ChangeRecord { Path = "b.json", Kind = ChangeKind.Modified },
        ]).Success);
        tx.MarkReferenceUpdateCompleted();
        tx.MarkActivated();
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);

        Seed("a.json", "{\"v\":2}");     // 事务期改动（真实新内容）
        Seed("b.json", "{\"w\":2}");
        var rb = tx.Rollback();
        Assert.False(rb.Success);                                        // 中断 ⇒ 失败
        Assert.Equal(MigrationStage.Blocked, tx.LoadManifest()!.Stage);
        // 重开前必须**确实处于混合态**（一个文件已恢复为旧字节、另一个仍是新字节）
        var hA = HashOf(Full("a.json"));
        var hB = HashOf(Full("b.json"));
        Assert.True((hA == originalA && hB != originalB) || (hA != originalA && hB == originalB),
            "夹具须制造混合态（至少一个旧文件 + 至少一个新文件）");
        tx.Dispose();

        var probe = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.True(probe.RecoverOnStart().Success);
        Assert.Equal(MigrationStage.RolledBack, probe.LoadManifest()!.Stage);
        Assert.Equal(originalA, HashOf(Full("a.json")));                 // 完整旧态（含中断前已恢复者）
        Assert.Equal(originalB, HashOf(Full("b.json")));
        probe.Dispose();
    }

    /// <summary>**第 5 轮必改⑥之二（真实新态 + 重开实例）**：已提交并产生真实新内容后重开 ⇒ 保持完整新态且可执行。</summary>
    [Fact]
    public void Recovery_CommittedWithRealNewContent_ReopenKeepsNewState()
    {
        Seed("a.json", "{\"v\":1}");
        var tx = Activated(changes: [new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
        tx.RehearseRollback();
        Assert.True(tx.Commit().Success);
        Seed("a.json", "{\"v\":2}");                                     // 真实新内容
        var newHash = HashOf(Full("a.json"));
        tx.Dispose();

        var probe = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(probe.TryAcquireExclusive().Success);
        var rec = probe.RecoverOnStart();
        Assert.True(rec.Success);
        Assert.Equal(MigrationStage.Committed, rec.Stage);
        Assert.Equal(newHash, HashOf(Full("a.json")));                   // 不得回滚新态
        Assert.True(probe.AuthorizeProductionExecution().Success);
        probe.Dispose();
    }
    /// <summary>
    /// **第 6 轮必改①（基线未完成的中止出口）**：`BeginTransaction` 发布 `Snapshotting` 后（或复制中途）退出 ⇒
    /// 重开 `RecoverOnStart` **不得**用部分快照恢复，而应安全中止（清未完成快照、置 `RolledBack`），
    /// 且**旧配置保持完整、可开启下一事务**。
    /// </summary>
    [Fact]
    public void Recovery_SnapshottingAborted_OldConfigIntactAndNewTransactionPossible()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet(), true,
            stageHook: null, fileRestoredHook: null);
        Assert.True(tx.BeginTransaction("t1").Success);      // 仅发布 Snapshotting（基线未完成）
        tx.Dispose();                                        // 模拟复制前/中途退出

        var probe = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(probe.TryAcquireExclusive().Success);
        var rec = probe.RecoverOnStart();
        Assert.True(rec.Success, rec.Reason);
        Assert.Equal(MigrationStage.RolledBack, rec.Stage);
        Assert.Equal(original, HashOf(Full("a.json")));       // 旧配置完整
        Assert.False(probe.AuthorizeProductionExecution().Success);
        probe.Dispose();

        var next = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(next.TryAcquireExclusive().Success);
        Assert.True(next.BeginTransaction("t2").Success, "中止后应可开启下一事务");   // 未决事务不再阻塞
        next.Dispose();
    }
    /// <summary>
    /// **第 7 轮必改（基线未完成的 `Blocked` 亦须有中止出口）**：快照复制中途失败（组件自身转为 `Blocked`，
    /// 基线未完成）⇒ 重开 `RecoverOnStart` 必须**安全中止**（清未完成快照、置 `RolledBack`），
    /// 旧配置保持完整且**可开启下一事务**；不得被 `pending_transaction_exists` 永久阻挡。
    /// </summary>
    [Fact]
    public void Recovery_SnapshotCopyWithFailureAbortsBaseline_AndAllowsNextTransaction()
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(tx.BeginTransaction("t1").Success);
        // 受控故障：**开事务后**在快照目录内预置同名目录，使 File.WriteAllBytes 复制该文件时失败（无需链接权限）
        var snap = tx.SnapshotPathOf("t1", tx.SessionId);
        Directory.CreateDirectory(Path.Combine(snap, "a.json"));
        var snapResult = tx.TakeSnapshot();
        Assert.False(snapResult.Success);                                  // 复制失败
        Assert.StartsWith("snapshot_io_failed", snapResult.Reason, StringComparison.Ordinal);
        var m = tx.LoadManifest()!;
        Assert.Equal(MigrationStage.Blocked, m.Stage);
        Assert.False(m.BaselineCompleted);                                 // **基线未完成**
        tx.Dispose();

        var probe = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.True(probe.RecoverOnStart().Success);
        Assert.Equal(MigrationStage.RolledBack, probe.LoadManifest()!.Stage);
        Assert.Equal(original, HashOf(Full("a.json")));                    // 旧配置完整
        probe.Dispose();

        var next = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(next.TryAcquireExclusive().Success);
        Assert.True(next.BeginTransaction("t2").Success, "基线中止后应可开启下一事务");
        next.Dispose();
    }
    /// <summary>
    /// **第 8 轮必改（空配置根）**：空基线也必须**可验证**（建立快照目录），否则基线完成后 `VerifySnapshot` 报
    /// `snapshot_missing`、事务永久卡在 `Blocked`。断言：空根 ⇒ 快照成功 + 验证通过 + 重开可收敛（置 `RolledBack`）+
    /// 可开启下一事务。
    /// </summary>
    [Fact]
    public void EmptyConfigRoot_SnapshotVerifiable_RecoverableAndNextTransactionPossible()
    {
        // 空配置根（不 Seed 任何文件）
        var tx = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(tx.BeginTransaction("t1").Success);
        var snap = tx.TakeSnapshot();
        Assert.True(snap.Success, snap.Reason);
        Assert.Equal("", tx.VerifySnapshot());                        // 空基线可验证（快照目录已建立）
        Assert.True(tx.LoadManifest()!.BaselineCompleted);
        tx.Dispose();

        var probe = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(probe.TryAcquireExclusive().Success);
        Assert.True(probe.RecoverOnStart().Success);                   // 未提交 ⇒ 收敛
        Assert.Equal(MigrationStage.RolledBack, probe.LoadManifest()!.Stage);
        probe.Dispose();

        var next = new MigrationSwitchTransaction(_configRoot, _txRoot, () => Now, () => new NoopQuiet());
        Assert.True(next.TryAcquireExclusive().Success);
        Assert.True(next.BeginTransaction("t2").Success);
        next.Dispose();
    }
    /// <summary>
    /// **第 9 轮必改（文件/目录拓扑互换）**：`Added("sub")` 与 `Deleted("sub/a.json")` 这类**拓扑互换**必须
    /// 在**登记期**被结构化拒绝（否则提交前崩溃后回滚「先恢复子路径、后删父路径」无法收敛）。
    /// </summary>
    [Fact]
    public void RecordChanges_FileDirectoryTopologySwap_Rejected()
    {
        Seed("sub/a.json", "{\"a\":1}");                    // 基线下 sub 是目录
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();

        var bad = tx.RecordChanges([
            new ChangeRecord { Path = "sub", Kind = ChangeKind.Added },          // 想变成文件
            new ChangeRecord { Path = "sub/a.json", Kind = ChangeKind.Deleted },
        ]);
        Assert.False(bad.Success);
        Assert.StartsWith("unsupported_topology_change", bad.Reason, StringComparison.Ordinal);
        Assert.Empty(tx.LoadManifest()!.ChangedFiles);      // 拒绝后不得留下部分变更归属
    }
    /// <summary>
    /// **第 10 轮必改（Added↔Added 祖先）**：同批 `Added("sub")` + `Added("sub/a.json")` 与**跨次登记**同类组合
    /// 都必须在**登记期**拒绝，且**既有变更记录保持不变**（校验先于任何写入）。
    /// </summary>
    [Fact]
    public void RecordChanges_AddedAncestorPair_Rejected_ExistingRecordsIntact()
    {
        // 空基线：Added 均合法，问题只来自拓扑
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "ok.json", Kind = ChangeKind.Added }]).Success);

        // ① 同批：Added↔Added 互为祖先前缀 ⇒ 拒绝
        var batch = tx.RecordChanges([
            new ChangeRecord { Path = "sub", Kind = ChangeKind.Added },
            new ChangeRecord { Path = "sub/a.json", Kind = ChangeKind.Added },
        ]);
        Assert.False(batch.Success);
        Assert.StartsWith("unsupported_topology_change", batch.Reason, StringComparison.Ordinal);
        Assert.Single(tx.LoadManifest()!.ChangedFiles);                        // 既有记录不变（未写入部分归属）
        Assert.Equal("ok.json", tx.LoadManifest()!.ChangedFiles[0].Path);

        // ② 跨次：先登记 Added("sub") 成功，再登记 Added("sub/a.json") ⇒ 拒绝且前次记录保留
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "sub", Kind = ChangeKind.Added }]).Success);
        var cross = tx.RecordChanges([new ChangeRecord { Path = "sub/a.json", Kind = ChangeKind.Added }]);
        Assert.False(cross.Success);
        Assert.StartsWith("unsupported_topology_change", cross.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(tx.LoadManifest()!.ChangedFiles, c => c.Path == "sub/a.json");
    }
    /// <summary>
    /// **第 11 轮必改（Added 与基线文件的拓扑冲突）**：仅登记 `Added`（未同时登记对应 Deleted）也必须被拒——
    /// ①新增 `sub`（基线有 `sub/a.json`）②新增 `f.txt/x.json`（基线有文件 `f.txt`）；拒绝后既有登记不变。
    /// </summary>
    [Fact]
    public void RecordChanges_AddedConflictsWithBaselineTopology_Rejected()
    {
        Seed("sub/a.json", "{\"a\":1}");     // 基线下 sub 是目录
        Seed("f.txt", "{\"f\":1}");          // 基线下 f.txt 是文件
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "ok.json", Kind = ChangeKind.Added }]).Success);

        // ① Added("sub") 是基线文件 sub/a.json 的祖先 ⇒ 拒绝
        var a = tx.RecordChanges([new ChangeRecord { Path = "sub", Kind = ChangeKind.Added }]);
        Assert.False(a.Success);
        Assert.StartsWith("unsupported_topology_change", a.Reason, StringComparison.Ordinal);

        // ② Added("f.txt/x.json") 以基线文件 f.txt 为祖先 ⇒ 拒绝
        var b = tx.RecordChanges([new ChangeRecord { Path = "f.txt/x.json", Kind = ChangeKind.Added }]);
        Assert.False(b.Success);
        Assert.StartsWith("unsupported_topology_change", b.Reason, StringComparison.Ordinal);

        var changed = tx.LoadManifest()!.ChangedFiles;
        Assert.Single(changed);                                  // 既有登记保持不变（无部分写入）
        Assert.Equal("ok.json", changed[0].Path);
    }
    /// <summary>
    /// **第 12 轮必改（Windows 短名/别名防护）**：已存在段的名称必须与其父目录**枚举名**匹配；
    /// 位处「存在但非枚举名」（如 NTFS 8.3 短名）⇒ 拒绝。此处覆盖**可构造分支**：
    /// ①规范存在路径（`a.json`）被接受；②新增不存在路径（`new/x.json`）被接受；
    /// ③已存在目录下的新文件（`sub/b.json`，`sub` 为规范名）被接受。
    /// **8.3 短名本身的反例构造不可移植**（需启用 8.3 的目标机）——按纪律**不设计需 owner 手工构造的场景**，
    /// 该分支以「存在但非枚举名 ⇒ 拒绝」的代码路径 + §21.10 残余登记承接。
    /// </summary>
    [Fact]
    public void RecordChanges_CanonicalNameRules_AcceptCanonicalAndNewPaths()
    {
        Seed("a.json", "{\"v\":1}");
        Seed("sub/b.json", "{\"w\":1}");
        using var tx = NewTx();
        tx.BeginTransaction("t1");
        tx.TakeSnapshot();

        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "new/x.json", Kind = ChangeKind.Added }]).Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "sub/b.json", Kind = ChangeKind.Modified }]).Success);
        Assert.True(tx.RecordChanges([new ChangeRecord { Path = "A.JSON", Kind = ChangeKind.Modified }]).Success); // 大小写变体＝同一身份
        var keys = tx.LoadManifest()!.ChangedFiles.Select(c => c.Path.ToLowerInvariant()).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(3, keys.Count);                                   // 大小写变体**按身份去重**（不新增记录）
        Assert.Equal(new[] { "a.json", "new/x.json", "sub/b.json" }, keys);
    }
    /// <summary>
    /// **第 13 轮必改（根路径别名）**：根路径的**已有祖先段**必须为规范枚举名（短名别名会绕过根隔离）。
    /// 可构造分支：规范根（临时目录）通过校验且可正常开事务；**8.3 别名反例不可移植**（需启用短名的目标机），
    /// 按纪律登记为残余并由「非枚举名 ⇒ 拒绝」代码路径承接。
    /// </summary>
    [Fact]
    public void Roots_CanonicalAbsolutePath_Accepted()
    {
        Assert.True(MigrationSwitchTransaction.IsCanonicalAbsolutePath(_configRoot, out var bad1), bad1);
        Assert.True(MigrationSwitchTransaction.IsCanonicalAbsolutePath(_txRoot, out var bad2), bad2);

        Seed("a.json", "{\"v\":1}");
        using var tx = NewTx();
        Assert.True(tx.BeginTransaction("t1").Success);
    }
    /// <summary>
    /// **第 14 轮必改（盘符根分隔符丢失）**：`EnsureTrailingSeparator` 必须保留根分隔符，
    /// 使「根 + 首段」始终是**完全限定**路径（不得退化为 `C:Users` 这种随当前目录漂移的盘符相对路径）。
    /// </summary>
    [Fact]
    public void EnsureTrailingSeparator_DriveRoot_KeepsAbsoluteSemantics()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(_configRoot))!;      // 例如 "C:\"
        var withSep = MigrationSwitchTransaction.EnsureTrailingSeparator(driveRoot);
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), withSep, StringComparison.Ordinal);
        var combined = Path.Combine(withSep, "probe-segment");
        Assert.True(Path.IsPathFullyQualified(combined), "根 + 首段必须仍为完全限定路径");
        Assert.NotEqual("C:probe-segment", combined);

        // 对真实配置根的绝对路径做规范名校验应通过（且不会因首段位置错误而“提前通过”）
        Assert.True(MigrationSwitchTransaction.IsCanonicalAbsolutePath(Path.GetFullPath(_configRoot), out var bad), bad);
    }
    /// <summary>
    /// **第 15 轮必改（路径命名空间别名）**：`\\?\C:\data` 与 `C:\data` 指向同一目录但字符串前缀不匹配，
    /// 可绕过根互不包含检查。处置＝**明确拒绝尚不支持的扩展/设备前缀**并要求两根同命名空间；
    /// 断言在**创建任何事务资料之前**拒绝（构造期抛）。
    /// </summary>
    [Fact]
    public void Roots_ExtendedOrMixedNamespacePrefixes_RejectedBeforeAnyWrite()
    {
        // ① 扩展路径前缀（任一根）⇒ 拒绝
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(_configRoot, @"\\?\C:\data\tx"));
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(@"\\?\C:\data", _txRoot));
        // ② 设备前缀 ⇒ 拒绝
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(_configRoot, @"\\.\C:\data\tx"));
        // ③ 普通盘符根 与 UNC 根 混用 ⇒ 拒绝（不同命名空间，无法比较目录身份）
        Assert.Throws<InvalidOperationException>(() =>
            new MigrationSwitchTransaction(_configRoot, @"\\server\share\tx"));
    }
    /// <summary>
    /// **第 16 轮必改（映射盘/SUBST 别名）**：不同盘符的两个根可能由 SUBST/映射盘指向同一目录 ⇒ 目录身份不可比较，
    /// 故要求**同卷**；不同卷在**创建任何事务资料之前**拒绝。若本机只有一个就绪卷，本条不可构造 ⇒ 依据事实跳过
    /// （不得伪报通过；该环境残余由 §21.10 登记承接）。
    /// </summary>
    [Fact]
    public void Roots_DifferentVolumes_RejectedBeforeAnyWrite()
    {
        // **确定性构造**：同卷约束按设计在访问磁盘之前执行 ⇒ 使用「另一个盘符」即可验证，无需该卷真实存在
        // （因此单卷环境同样确定性通过，不存在「提前 return 计为通过」的空过分支）。
        var cfgVolume = (Path.GetPathRoot(Path.GetFullPath(_configRoot)) ?? "C:").TrimEnd(Path.DirectorySeparatorChar);
        var alt = string.Equals(cfgVolume, "Z:", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
        var altRoot = alt + Path.DirectorySeparatorChar + "r56-tx";

        Assert.Throws<InvalidOperationException>(() => new MigrationSwitchTransaction(_configRoot, altRoot));
    }
    [Theory]
    [InlineData(MigrationStage.Snapshotting)]
    [InlineData(MigrationStage.SnapshotReady)]
    [InlineData(MigrationStage.ReferenceUpdating)]
    [InlineData(MigrationStage.Activated)]
    [InlineData(MigrationStage.Committed)]
    public void CrashAtAnyStage_NeverLeavesExecutableState(MigrationStage crashAt)
    {
        Seed("a.json", "{\"v\":1}");
        var original = HashOf(Full("a.json"));
        var tx = NewTx(hook: stage =>
        {
            if (stage == crashAt) throw new InvalidOperationException("模拟阶段崩溃：" + stage);
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            tx.BeginTransaction("t1");
            tx.TakeSnapshot();
            tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]);
            tx.MarkReferenceUpdateCompleted();
            tx.MarkActivated();
            tx.RehearseRollback();
            tx.Commit();
        });

        tx.Dispose();                                     // 先释放锁（否则探针的失败只由 lock_not_held 解释）
        var probe = NewTx();
        Assert.True(probe.TryAcquireExclusive().Success, "探针须真正取到锁，否则授权失败可能只由 lock_not_held 解释");
        Assert.False(probe.AuthorizeProductionExecution().Success);
        Assert.Equal(original, HashOf(Full("a.json")));
        probe.Dispose();
    }
}

## source: MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs L1-L1018 SHA256=c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>事务阶段（**持久化**；除 Committed 外一律不可生产执行）。</summary>
public enum MigrationStage
{
    None = 0,
    Snapshotting = 1,
    SnapshotReady = 2,
    ReferenceUpdating = 3,
    Activated = 4,
    Committed = 5,
    RollingBack = 6,
    RolledBack = 7,
    Blocked = 8,
}

/// <summary>变更归属（回滚据此判定「本事务新增」；**不再删除未登记文件**）。</summary>
public enum ChangeKind { Added = 0, Modified = 1, Deleted = 2 }

/// <summary>一条变更记录（相对路径 + 归属）。</summary>
public sealed class ChangeRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("kind")] public ChangeKind Kind { get; set; }
}

/// <summary>迁移 manifest（**全字段完整性 + 结构与状态不变量**）。</summary>
public sealed class MigrationManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("transactionId")] public string TransactionId { get; set; } = "";
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset CreatedAtUtc { get; set; }
    [JsonPropertyName("configRoot")] public string ConfigRoot { get; set; } = "";
    [JsonPropertyName("snapshotPath")] public string SnapshotPath { get; set; } = "";
    /// <summary>不可变快照身份（＝开事务时的会话标识）；快照路径**精确**绑定本事务，不用前缀判定。</summary>
    [JsonPropertyName("snapshotId")] public string SnapshotId { get; set; } = "";
    [JsonPropertyName("rollbackEntry")] public string RollbackEntry { get; set; } = "";
    [JsonPropertyName("fileHashes")] public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.Ordinal);
    /// <summary>本事务变更归属（回滚据此判定新增；无记录 ⇒ 不删除任何文件）。</summary>
    [JsonPropertyName("changedFiles")] public List<ChangeRecord> ChangedFiles { get; set; } = [];
    [JsonPropertyName("snapshotManifestHash")] public string SnapshotManifestHash { get; set; } = "";
    /// <summary>**基线是否完成**（快照清单已发布）。未完成 ⇒ 允许安全中止（**不得**用部分快照恢复）。</summary>
    [JsonPropertyName("baselineCompleted")] public bool BaselineCompleted { get; set; }
    [JsonPropertyName("stage")] public MigrationStage Stage { get; set; }
    /// <summary>唯一提交标记；提交前为空、非提交态必为空。</summary>
    [JsonPropertyName("commitMarker")] public string? CommitMarker { get; set; }
    [JsonPropertyName("rollbackRehearsed")] public bool RollbackRehearsed { get; set; }
    /// <summary>演练范围（绑定快照清单 + 变更归属；变更集改变即失效）。</summary>
    [JsonPropertyName("rehearsalScope")] public string? RehearsalScope { get; set; }
    /// <summary>结构化 blocked 原因（非空 ⇒ 禁止提交与生产执行）。</summary>
    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }
    /// <summary>静止窗口取得时刻（须与 QuiesceSessionId 同会话才算有效）。</summary>
    [JsonPropertyName("quiescedAtUtc")] public DateTimeOffset? QuiescedAtUtc { get; set; }
    /// <summary>取得静止窗口的会话标识（重启后不得凭历史记录提交）。</summary>
    [JsonPropertyName("quiesceSessionId")] public string? QuiesceSessionId { get; set; }
    /// <summary>静止窗口**代次**（释放即失效；同实例重新取锁不得复用历史资格）。</summary>
    [JsonPropertyName("quiesceGeneration")] public int QuiesceGeneration { get; set; }
    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
}

/// <summary>事务操作结果。</summary>
public sealed record MigrationResult(bool Success, string Reason, MigrationStage Stage)
{
    public static MigrationResult Ok(MigrationStage s) => new(true, "", s);
    public static MigrationResult Fail(string reason, MigrationStage s) => new(false, reason, s);
}
/// <summary>
/// **R5.6 事务迁移切换（v3：按第 2 轮会诊 7 项必改重构）**。
/// 不变量：①串行边界覆盖全部入口（恢复/变更/授权/执行/回滚均须本实例持锁，且实例内串行；
/// TryRunProduction 的「授权+执行」在同一临界区）；②回滚可恢复（先落 RollingBack 再做 IO，
/// 恢复路径幂等续做）；③静止窗口覆盖全程且绑定会话（重启后不得凭历史时间戳提交）；
/// ④身份/路径前置校验（事务号安全、快照路径属本事务、根绑定、链接拒绝）；
/// ⑤未决事务存在时拒绝开新事务，事务号/快照目录不复用；⑥变更归属按基线校验；
/// ⑦结构与状态不变量 + 全字段完整性双校验，null 字段结构化拒绝。
/// 开发验证只用独立配置根；真实 User 目录切换由 owner 另行下令。
/// </summary>
public sealed class MigrationSwitchTransaction : IDisposable
{
    private readonly string _configRoot;
    private readonly string _transactionRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<IDisposable>? _quiesce;
    private readonly bool _requireQuiescence;
    private readonly Action<MigrationStage>? _stageHook;
    private readonly Action<string>? _fileRestoredHook;
    private readonly object _sync = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private FileStream? _lock;
    private IDisposable? _quiet;
    private bool _quietValid;
    private int _quietGeneration;

    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null)
    {
        _configRoot = Path.GetFullPath(configRoot ?? throw new ArgumentNullException(nameof(configRoot)));
        _transactionRoot = Path.GetFullPath(transactionRoot ?? throw new ArgumentNullException(nameof(transactionRoot)));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _quiesce = quiesce;
        _requireQuiescence = requireQuiescence;
        _stageHook = stageHook;
        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
        ValidateRoots();
    }

    public string ManifestPath => Path.Combine(_transactionRoot, "migration-manifest.json");

    /// <summary>事务号**占用历史**（追加式；重复事务号一律拒绝——不依赖当前 manifest 是否仍在）。</summary>
    public string HistoryPath => Path.Combine(_transactionRoot, "migration-history.txt");
    internal bool HoldsExclusiveLock => _lock is not null;
    internal string SessionId => _sessionId;

    /// <summary>快照路径＝事务根下按「事务号 + 会话」确定性推导（不复用既有目录）。</summary>
    internal string SnapshotPathOf(string transactionId, string sessionId)
        => Path.Combine(_transactionRoot, "snapshot-" + transactionId + "-" + sessionId);

    private void ValidateRoots()
    {
        // **路径命名空间门禁（第 15 轮会诊）**：`\\?\C:\data` 与 `C:\data` 指向同一目录却字符串前缀不匹配，
        // 可绕过「根互不包含」。此处**明确拒绝尚不支持的扩展/设备前缀**，并要求两根同属一种命名空间。
        foreach (var chain in new[] { _configRoot, _transactionRoot })
        {
            if (chain.StartsWith(@"\\?\", StringComparison.Ordinal) || chain.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new InvalidOperationException("不支持扩展/设备路径前缀（\\\\?\\、\\\\.\\）：无法保证与普通路径的目录身份一致，拒绝迁移事务：" + chain);
        }
        // **同卷约束（第 16 轮会诊）**：`SUBST X: C:\data` / 映射盘会让两个不同盘符指向同一目录，字符串互不包含且
        // 逐段名称检查也通过 ⇒ 无法证明隔离。处置＝要求两根**同卷**（不同卷直接拒绝；同卷别名由 reparse point 检查拦截）。
        var cfgVolume = (Path.GetPathRoot(_configRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        var txVolume = (Path.GetPathRoot(_transactionRoot) ?? "").TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(cfgVolume, txVolume, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根与事务根必须位于**同一卷**（不同卷可能由 SUBST/映射盘指向同一目录，目录身份不可比较），拒绝迁移事务。");

        var cfgIsUnc = _configRoot.StartsWith(@"\\", StringComparison.Ordinal);
        var txIsUnc = _transactionRoot.StartsWith(@"\\", StringComparison.Ordinal);
        if (cfgIsUnc != txIsUnc)
            throw new InvalidOperationException("配置根与事务根必须同属一种路径命名空间（均为盘符路径或均为 UNC），拒绝迁移事务。");

        var cfg = _configRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tx = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (tx.StartsWith(cfg, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务根不得位于配置根内（自包含快照会污染备份）。");
        if (cfg.StartsWith(tx, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置根不得位于事务根内。");
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (HasReparsePoint(chain))
                throw new InvalidOperationException("根路径链上存在重解析点（junction/符号链接），拒绝迁移事务：" + chain);
        foreach (var chain in new[] { _configRoot, _transactionRoot })
            if (!IsCanonicalAbsolutePath(chain, out var badSegment))
                throw new InvalidOperationException("根路径存在非规范名（如 NTFS 8.3 短名别名），拒绝迁移事务：" + badSegment
                    + "（根：" + chain + "）");
    }

    /// <summary>链接逃逸防护：路径链上任一层为 reparse point 即拒绝。</summary>
    internal static bool HasReparsePoint(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            dir = dir.Parent;
        }
        return false;
    }

    /// <summary>
    /// 相对路径安全校验（**先拒绝不支持的原始路径**，不做静默裁剪）：绝对路径/盘符/`..`/`.`/空段、
    /// 控制字符、以及段内**前导/尾随空格或尾点**（Windows 会裁剪 ⇒ 别名冲突）一律拒绝。
    /// </summary>
    internal static bool IsSafeRelativePath(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return false;
        if (rel.Any(char.IsControl)) return false;                 // NUL 等控制字符拒绝（避免路径解析异常/绕过）
        var norm = rel.Replace('\\', '/');
        if (norm.StartsWith('/') || norm.Contains(':')) return false;
        foreach (var seg in norm.Split('/'))
        {
            if (seg is ".." or "." || seg.Length == 0) return false;
            if (seg.EndsWith('.') || seg.StartsWith(' ') || seg.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>路径身份规范化（**只统一分隔符，不裁剪**——裁剪会把合法文件名映射到另一个文件）。</summary>
    internal static string NormalizePath(string? rel) => (rel ?? "").Replace('\\', '/');

    /// <summary>路径**身份键**（Windows 语义：大小写不敏感）——基线、变更记录与实际文件操作三处统一使用。</summary>
    internal static string PathKey(string? rel) => NormalizePath(rel).ToLowerInvariant();

    /// <summary>按身份键查快照哈希（大小写不敏感；避免 `A.json` 与 `a.json` 在 Windows 上互相删改）。</summary>
    internal static bool TryGetHashCaseInsensitive(IReadOnlyDictionary<string, string> hashes, string? path, out string? hash)
    {
        var key = PathKey(path);
        foreach (var p in hashes)
        {
            if (PathKey(p.Key) == key)
            {
                hash = p.Value;
                return true;
            }
        }
        hash = null;
        return false;
    }

    /// <summary>
    /// **规范化名称校验**（Windows 短名/别名防护）：对已存在的每一段，要求该段名称与其父目录**枚举名**大小写不敏感匹配；
    /// 若路径在磁盘上存在、却**未被父目录枚举名匹配**（例：NTFS 8.3 短名别名）⇒ 视为**非规范名**拒绝。
    /// 尚未存在的新路径（新增文件/新目录）不受此限（新名不可能是既有别名）。
    /// </summary>
    internal static bool IsCanonicalExistingPath(string root, string? rel, out string notCanonicalAt)
    {
        notCanonicalAt = "";
        var segments = NormalizePath(rel).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = EnsureTrailingSeparator(root);   // 保留分隔符：`C:\` + seg 必须仍是**完全限定**路径
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(current, segments[i]);
            var exists = File.Exists(candidate) || Directory.Exists(candidate);
            if (!exists) return true;                        // 从这里起都是新路径 ⇒ 无别名风险
            var parentEntries = Directory.EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName).Where(n => n is not null).ToList();
            if (!parentEntries.Any(n => string.Equals(n, segments[i], StringComparison.OrdinalIgnoreCase)))
            {
                notCanonicalAt = string.Join('/', segments.Take(i + 1));   // 存在但非枚举名 ⇒ 别名（如 8.3 短名）
                return false;
            }
            current = candidate;
        }
        return true;
    }

    /// <summary>规范化并**保留尾分隔符**（避免 `C:\` 被裁剪成 `C:`；盘符相对路径会随当前目录漂移）。</summary>
    internal static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
            ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>绝对路径版规范名校验（从盘符/UNC 根逐段枚举比对；用于根路径别名防护）。</summary>
    internal static bool IsCanonicalAbsolutePath(string fullPath, out string notCanonicalAt)
    {
        var full = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(full) ?? "";
        var rel = full.Length > root.Length ? full[root.Length..] : "";
        if (rel.Length == 0) { notCanonicalAt = ""; return true; }
        return IsCanonicalExistingPath(root, rel, out notCanonicalAt);
    }

    /// <summary>路径前缀判定（a 为 b 的祖先目录）：用于**文件/目录拓扑互换**的登记拒绝。</summary>
    internal static bool IsAncestorPath(string? a, string? b)
    {
        var ka = PathKey(a);
        var kb = PathKey(b);
        if (ka.Length == 0 || kb.Length == 0 || ka == kb) return false;
        return kb.StartsWith(ka + "/", StringComparison.Ordinal);
    }

    /// <summary>目标路径安全性：规范化后必须仍在 root 内，且**父目录链上无 reparse point**（逐段链接拒绝）。</summary>
    internal static bool IsSafeTarget(string root, string rel)
    {
        if (!IsSafeRelativePath(rel)) return false;
        var rootFull = EnsureTrailingSeparator(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            return false;                                                                         // **目标文件本身**是链接 ⇒ 拒绝
        var dir = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (dir is not null)
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;   // 父链任一段链接 ⇒ 拒绝
            if (string.Equals(EnsureTrailingSeparator(dir.FullName), rootFull, StringComparison.OrdinalIgnoreCase)) break;
            dir = dir.Parent;
        }
        return true;
    }

    /// <summary>事务号安全校验（单段安全路径）。</summary>
    internal static bool IsSafeTransactionId(string? txId)
        => IsSafeRelativePath(txId) && !(txId ?? "").Contains('/') && !(txId ?? "").Contains('\\');

    /// <summary>取得事务独占锁（不写 manifest；恢复路径用）。</summary>
    public MigrationResult TryAcquireExclusive()
    {
        lock (_sync)
        {
            if (HoldsExclusiveLock) return MigrationResult.Ok(MigrationStage.None);
            Directory.CreateDirectory(_transactionRoot);
            try
            {
                _lock = new FileStream(Path.Combine(_transactionRoot, "migration.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return MigrationResult.Fail("transaction_busy", MigrationStage.None);
            }
            return MigrationResult.Ok(MigrationStage.None);
        }
    }

    /// <summary>开启新事务：须持锁；有未决事务或事务号占用 ⇒ 拒绝；先持久化 Snapshotting 并取得全程静止窗口。</summary>
    public MigrationResult BeginTransaction(string transactionId)
    {
        lock (_sync)
        {
            if (!IsSafeTransactionId(transactionId)) return MigrationResult.Fail("invalid_transaction_id", MigrationStage.None);
            if (!HoldsExclusiveLock)
            {
                var acq = TryAcquireExclusive();
                if (!acq.Success) return acq;
            }

            if (File.Exists(ManifestPath))
            {
                var validated = LoadValidated();
                if (validated is null) return MigrationResult.Fail("pending_manifest_corrupt", MigrationStage.None);  // 存在但不可验证 ⇒ 保守阻断
                if (validated.Stage is not (MigrationStage.Committed or MigrationStage.RolledBack))
                    return MigrationResult.Fail("pending_transaction_exists:" + validated.TransactionId, validated.Stage);
                if (string.Equals(validated.TransactionId, transactionId, StringComparison.Ordinal))
                    return MigrationResult.Fail("transaction_id_in_use", validated.Stage);
            }
            if (File.Exists(HistoryPath) && File.ReadAllLines(HistoryPath).Any(l => string.Equals(l.Trim(), transactionId, StringComparison.Ordinal)))
                return MigrationResult.Fail("transaction_id_in_use", MigrationStage.None);        // 历史占用：事务号不复用

            var snapshotPath = SnapshotPathOf(transactionId, _sessionId);
            if (Directory.Exists(snapshotPath)) return MigrationResult.Fail("snapshot_path_in_use", MigrationStage.None);

            _quiet = _quiesce?.Invoke();                       // 静止窗口：覆盖全程，提交/回滚/释放时结束
            _quietValid = _quiet is not null;
            _quietGeneration++;
            var manifest = new MigrationManifest
            {
                TransactionId = transactionId,
                CreatedAtUtc = _utcNow(),
                ConfigRoot = _configRoot,
                SnapshotPath = snapshotPath,
                SnapshotId = _sessionId,
                RollbackEntry = "rollback:MigrationSwitchTransaction.Rollback(transactionId=" + transactionId + ")",
                Stage = MigrationStage.Snapshotting,
                CommitMarker = null,
                RollbackRehearsed = false,
                QuiescedAtUtc = _quiet is null ? null : _utcNow(),
                QuiesceSessionId = _quiet is null ? null : _sessionId,
                QuiesceGeneration = _quiet is null ? 0 : _quietGeneration,
            };
            // **先持久化占号、再发布 manifest**（占号失败/崩溃也保守占号 ⇒ 事务号不复用；不依赖窗口是否存在）。
            File.AppendAllText(HistoryPath, transactionId + Environment.NewLine);
            WriteManifest(manifest);
            return MigrationResult.Ok(MigrationStage.Snapshotting);
        }
    }
    /// <summary>步骤①②：全量快照（字节+SHA256）→ 清单哈希与快照路径落盘 → SnapshotReady。</summary>
    public MigrationResult TakeSnapshot()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Snapshotting) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 采集期须有**存续**窗口（重开实例不得续用旧资格）
            if (!Directory.Exists(_configRoot)) return MigrationResult.Fail("config_root_missing", m.Stage);

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Directory.CreateDirectory(m.SnapshotPath);   // **空配置根也须建立可验证快照目录**（否则基线完成后 VerifySnapshot 报 snapshot_missing）
                foreach (var file in EnumerateFiles(_configRoot))
                {
                    var rel = Rel(file, _configRoot);
                    if (!IsSafeTarget(_configRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                        return MarkBlocked("unsafe_path:" + rel);      // 读端与写端都须安全（含目标文件本身与父链链接）
                    var bytes = File.ReadAllBytes(file);
                    var target = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    hashes[rel] = Sha256Hex(bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("snapshot_io_failed:" + ex.GetType().Name);
            }

            m.FileHashes = hashes;
            m.SnapshotManifestHash = ComputeSnapshotManifestHash(hashes);
            m.BaselineCompleted = true;                       // 基线（完整清单）已发布
            m.Stage = MigrationStage.SnapshotReady;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>快照清单哈希（排序后的 相对路径:内容哈希 取 SHA256）。</summary>
    public static string ComputeSnapshotManifestHash(IReadOnlyDictionary<string, string> fileHashes)
    {
        var ordered = (fileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .OrderBy(p => p.Key, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var p in ordered) sb.Append(p.Key).Append(':').Append(p.Value ?? "").Append('\n');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>记录变更归属（基线校验：Added 不得命中快照；Modified/Deleted 必须在快照中）。</summary>
    public MigrationResult RecordChanges(IEnumerable<ChangeRecord> changes)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage is not (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated))
                return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);

            var batch = new List<ChangeRecord>();
            foreach (var c in changes ?? [])
            {
                if (c is null) return MigrationResult.Fail("null_change_record", m.Stage);
                var raw = c.Path ?? "";
                if (!IsSafeRelativePath(raw)) return MigrationResult.Fail("unsafe_path:" + raw, m.Stage);   // **先验原始输入**
                var path = NormalizePath(raw);                                                             // 再统一解析（**不裁剪**）
                if (batch.Any(x => PathKey(x.Path) == PathKey(path)))
                    return MigrationResult.Fail("duplicate_change_path:" + path, m.Stage);
                var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, path, out _);
                var ok = c.Kind switch
                {
                    ChangeKind.Added => !inSnapshot,
                    ChangeKind.Modified or ChangeKind.Deleted => inSnapshot,
                    _ => false,
                };
                if (!ok) return MigrationResult.Fail("change_baseline_mismatch:" + path, m.Stage);
                batch.Add(new ChangeRecord { Path = path, Kind = c.Kind });
            }

            // **拓扑约束**：`Added` 路径不得与 `Modified/Deleted` 路径互为祖先——文件↔目录互换会让「先恢复子路径、
            // 后删除父路径」无法收敛（父被新文件阻挡）。登记期结构化拒绝，避免提交前崩溃后进入不可恢复回滚。
            foreach (var c in batch)
            {
                if (!IsCanonicalExistingPath(_configRoot, c.Path, out var badSegment))
                    return MigrationResult.Fail("non_canonical_path:" + badSegment, m.Stage);   // 短名/别名等新引用不一致 ⇒ 拒绝
            }

            var merged = m.ChangedFiles.Concat(batch).ToList();
            for (var i = 0; i < merged.Count; i++)
            {
                for (var j = 0; j < merged.Count; j++)
                {
                    if (i == j) continue;
                    var a = merged[i];
                    var b = merged[j];
                    if (a.Kind != ChangeKind.Added && b.Kind != ChangeKind.Added) continue;   // Modified/Deleted 必对应基线文件，不互为祖先
                    if (IsAncestorPath(a.Path, b.Path))                                        // 含 **Added↔Added**（同批与跨次）
                        return MigrationResult.Fail("unsupported_topology_change:" + a.Path + "<->" + b.Path, m.Stage);
                }
            }

            // **拓扑（基线侧）**：每条 Added 亦须与**完整基线文件集合**双向祖先检查——只登记 Added（未同时登记
            // 对应 Deleted）同样可能造成「先恢复基线子路径、后删除新增父路径」的不可收敛回滚，不能依赖调用方补齐。
            foreach (var added in merged.Where(c => c.Kind == ChangeKind.Added))
            {
                foreach (var basePath in m.FileHashes.Keys)
                {
                    if (IsAncestorPath(added.Path, basePath) || IsAncestorPath(basePath, added.Path))
                        return MigrationResult.Fail("unsupported_topology_change:" + added.Path + "<->baseline:" + basePath, m.Stage);
                }
            }

            foreach (var c in batch)
            {
                m.ChangedFiles.RemoveAll(x => PathKey(x.Path) == PathKey(c.Path));
                m.ChangedFiles.Add(c);
            }
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    public MigrationResult MarkReferenceUpdateCompleted() => Advance(MigrationStage.ReferenceUpdating);
    public MigrationResult MarkActivated() => Advance(MigrationStage.Activated);

    private MigrationResult Advance(MigrationStage to)
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (!IsLegalAdvance(m.Stage, to)) return MigrationResult.Fail("illegal_advance:" + m.Stage + "->" + to, m.Stage);
            m.Stage = to;
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>合法阶段转换（跳步提交被拒；回滚可自任意中间态/已提交/阻塞态发起）。</summary>
    public static bool IsLegalAdvance(MigrationStage from, MigrationStage to) => (from, to) switch
    {
        (MigrationStage.Snapshotting, MigrationStage.SnapshotReady) => true,
        (MigrationStage.SnapshotReady, MigrationStage.ReferenceUpdating) => true,
        (MigrationStage.ReferenceUpdating, MigrationStage.Activated) => true,
        (MigrationStage.Activated, MigrationStage.Committed) => true,
        (_, MigrationStage.Blocked) => from is not (MigrationStage.Committed or MigrationStage.RolledBack),
        (MigrationStage.SnapshotReady or MigrationStage.ReferenceUpdating or MigrationStage.Activated
            or MigrationStage.Committed or MigrationStage.Blocked or MigrationStage.RollingBack,
            MigrationStage.RollingBack) => true,
        (MigrationStage.Snapshotting, MigrationStage.RolledBack) => true,   // 基线未完成的中止出口（不迁移任何变更）
        (MigrationStage.RollingBack, MigrationStage.RolledBack) => true,
        (MigrationStage.RolledBack, MigrationStage.RolledBack) => true,
        _ => false,
    };
    /// <summary>步骤④：回滚演练——副本＝快照 → 施加代表变更 → 复用实际回滚核心 → 逐字节校验。</summary>
    public MigrationResult RehearseRollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            var rehearsalRoot = Path.Combine(_transactionRoot, "rehearsal-" + _sessionId);
            try
            {
                if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true);
                Directory.CreateDirectory(rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);
                ApplyRepresentativeChanges(m, rehearsalRoot);
                RestoreFromSnapshot(m, rehearsalRoot);      // 复用实际回滚核心
                if (DeleteRecordedAdditions(m, rehearsalRoot) > 0)
                    return MigrationResult.Fail("rehearsal_cleanup_incomplete", m.Stage);
                foreach (var p in m.FileHashes)
                {
                    var target = Path.Combine(rehearsalRoot, p.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target)) return MigrationResult.Fail("rehearsal_missing:" + p.Key, m.Stage);
                    if (!string.Equals(Sha256Hex(File.ReadAllBytes(target)), p.Value, StringComparison.Ordinal))
                        return MigrationResult.Fail("rehearsal_hash_mismatch:" + p.Key, m.Stage);
                }
                foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                {
                    var target = Path.Combine(rehearsalRoot, added.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(target)) return MigrationResult.Fail("rehearsal_addition_not_cleaned:" + added.Path, m.Stage);
                }
            }
            catch (IOException ex)
            {
                return MarkBlocked("rehearsal_io_failed:" + ex.GetType().Name);
            }
            finally
            {
                try { if (Directory.Exists(rehearsalRoot)) Directory.Delete(rehearsalRoot, true); } catch { }
            }

            m.RollbackRehearsed = true;
            m.RehearsalScope = RehearsalScopeOf(m);
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>提交：阶段 Activated、演练范围一致、无 blocked、静止窗口为当前会话且快照有效 ⇒ 写唯一提交标记。</summary>
    public MigrationResult Commit()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Activated) return MigrationResult.Fail("illegal_stage:" + m.Stage, m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            if (!m.RollbackRehearsed || !string.Equals(m.RehearsalScope, RehearsalScopeOf(m), StringComparison.Ordinal))
                return MigrationResult.Fail("rollback_not_rehearsed_for_current_scope", m.Stage);
            if (_requireQuiescence && (!_quietValid || _quiet is null || m.QuiescedAtUtc is null
                || !string.Equals(m.QuiesceSessionId, _sessionId, StringComparison.Ordinal)
                || m.QuiesceGeneration != _quietGeneration))
                return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 须**实际存续**且同代次的窗口
            if (VerifySnapshot() is { Length: > 0 } bad) return MigrationResult.Fail("snapshot_invalid:" + bad, m.Stage);

            m.CommitMarker = m.TransactionId;
            m.Stage = MigrationStage.Committed;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>回滚：先落 RollingBack（清提交标记与演练资格）再做 IO，最后落 RolledBack；只删归属为 Added 的文件。</summary>
    public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (!m.BaselineCompleted)
            {
                // 基线未完成（快照发布前失败/中止）⇒ **安全中止**：清理未完成快照、置 RolledBack，使新事务可开启。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (!IsLegalAdvance(m.Stage, MigrationStage.RollingBack))
                return MigrationResult.Fail("illegal_advance:" + m.Stage + "->RollingBack", m.Stage);

            // **先持久化封锁**（落 RollingBack 并清标记/演练资格）——此后即不可生产执行；
            // 再做窗口/快照校验与恢复，失败一律 Blocked（失败处理产出仍可加载）。
            m.Stage = MigrationStage.RollingBack;
            m.RollbackRehearsed = false;
            m.RehearsalScope = null;
            m.CommitMarker = null;
            WriteManifest(m);
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("rollback_snapshot_invalid:" + bad);
            return CompleteRollback(m);
        }
    }

    /// <summary>回滚主体（恢复旧字节 + 按归属删除新增 + 撤销激活 + 落 RolledBack）；可被恢复路径幂等重入。</summary>
    private MigrationResult CompleteRollback(MigrationManifest m)
    {
        lock (_sync)
        {
            try
            {
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot) > 0)
                    return MarkBlocked("rollback_cleanup_incomplete");      // 新增未清理 ⇒ 保持阻断
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return MarkBlocked("rollback_io_failed:" + ex.GetType().Name);
            }
            m.Stage = MigrationStage.RolledBack;
            m.CommitMarker = null;
            WriteManifest(m);
            ReleaseQuiescence();
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>重启恢复（须先持锁）：已提交+标记+无 blocked ⇒ 保持新态；RollingBack ⇒ 幂等续做；RolledBack ⇒ 幂等；其余 ⇒ 回滚旧态。</summary>
    public MigrationResult RecoverOnStart()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage == MigrationStage.Committed
                && string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(m.BlockedReason))
                return MigrationResult.Ok(MigrationStage.Committed);
            if (m.Stage == MigrationStage.RolledBack) return MigrationResult.Ok(MigrationStage.RolledBack);
            if (m.Stage == MigrationStage.None) return MigrationResult.Fail("illegal_stage:None", m.Stage);
            if (m.Stage == MigrationStage.Snapshotting || (m.Stage == MigrationStage.Blocked && !m.BaselineCompleted))
            {
                // **基线尚未完成**（且尚未发生任何迁移变更）⇒ 安全中止：清理未完成快照、置 RolledBack，使新事务可开启。
                // **绝不**把部分快照用于恢复（不调用 VerifySnapshot/CompleteRollback）。
                try { if (Directory.Exists(m.SnapshotPath)) Directory.Delete(m.SnapshotPath, recursive: true); } catch { }
                m.Stage = MigrationStage.RolledBack;
                m.CommitMarker = null;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.BlockedReason = null;
                WriteManifest(m);
                ReleaseQuiescence();
                return MigrationResult.Ok(MigrationStage.RolledBack);
            }
            if (VerifySnapshot() is { Length: > 0 } bad) return MarkBlocked("recover_snapshot_invalid:" + bad);

            if (m.Stage != MigrationStage.RollingBack)
            {
                m.Stage = MigrationStage.RollingBack;
                m.RollbackRehearsed = false;
                m.RehearsalScope = null;
                m.CommitMarker = null;
                WriteManifest(m);
            }
            if (!EnsureQuiescence(m)) return MigrationResult.Fail("no_quiescence_window", m.Stage);   // 恢复亦须有效窗口
            return CompleteRollback(m);
        }
    }
    /// <summary>授权（须持锁）：完整性校验 → 已提交 → 标记匹配 → 无 blocked。</summary>
    public MigrationResult AuthorizeProductionExecution()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", m.Stage);
            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal))
                return MigrationResult.Fail("commit_marker_mismatch", m.Stage);
            if (!string.IsNullOrEmpty(m.BlockedReason)) return MigrationResult.Fail("blocked:" + m.BlockedReason, m.Stage);
            return MigrationResult.Ok(m.Stage);
        }
    }

    /// <summary>唯一生产执行检查点：授权与执行在同一临界区；未获授权 ⇒ 不执行任何动作。</summary>
    public MigrationResult TryRunProduction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            var auth = AuthorizeProductionExecution();
            if (!auth.Success) return auth;
            action();
            return MigrationResult.Ok(MigrationStage.Committed);
        }
    }

    /// <summary>快照完整性：manifest 结构与不变量 → 快照文件齐全/哈希一致 → 无未登记多余文件。</summary>
    public string VerifySnapshot()
    {
        var m = LoadManifest();
        if (m is null) return "manifest_missing_or_corrupt";
        if (!IsManifestIntegrityValid(m)) return "manifest_integrity_mismatch";
        if (!string.Equals(m.SnapshotManifestHash, ComputeSnapshotManifestHash(m.FileHashes), StringComparison.Ordinal))
            return "snapshot_manifest_hash_mismatch";
        if (!Directory.Exists(m.SnapshotPath)) return "snapshot_missing";

        List<string> onDisk;
        try { onDisk = EnumerateFilesSafe(m.SnapshotPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "snapshot_enumeration_failed:" + ex.GetType().Name;      // 读端链接/IO 失败 ⇒ 验证失败（不继续读）
        }
        foreach (var extra in onDisk.Where(f => !m.FileHashes.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal))
            return "snapshot_untracked_file:" + extra;
        foreach (var p in m.FileHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!IsSafeTarget(m.SnapshotPath, p.Key)) return "snapshot_unsafe_target:" + p.Key;   // **读端**同样拒绝链接逃逸
            var target = Path.Combine(m.SnapshotPath, NormalizePath(p.Key).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) return "snapshot_file_missing:" + p.Key;
            string hash;
            try { hash = Sha256Hex(File.ReadAllBytes(target)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return "snapshot_read_failed:" + p.Key;
            }
            if (!string.Equals(hash, p.Value, StringComparison.Ordinal)) return "snapshot_hash_mismatch:" + p.Key;
        }
        return "";
    }

    /// <summary>全字段完整性摘要（仅完整性，不提供来源认证）。</summary>
    public static string ComputeManifestIntegrity(MigrationManifest m)
    {
        var sb = new StringBuilder();
        sb.Append(m.SchemaVersion).Append('|').Append(m.TransactionId).Append('|').Append(m.CreatedAtUtc.ToString("O")).Append('|');
        sb.Append(m.ConfigRoot).Append('|').Append(m.SnapshotPath).Append('|').Append(m.SnapshotId).Append('|').Append(m.RollbackEntry).Append('|');
        sb.Append(m.SnapshotManifestHash).Append('|').Append(m.BaselineCompleted ? '1' : '0').Append('|').Append((int)m.Stage).Append('|').Append(m.CommitMarker ?? "<null>").Append('|');
        sb.Append(m.RollbackRehearsed ? '1' : '0').Append('|').Append(m.RehearsalScope ?? "<null>").Append('|');
        sb.Append(m.BlockedReason ?? "<null>").Append('|').Append(m.QuiescedAtUtc?.ToString("O") ?? "<null>").Append('|');
        sb.Append(m.QuiesceSessionId ?? "<null>").Append('|').Append(m.QuiesceGeneration).Append('|');
        foreach (var c in (m.ChangedFiles ?? []).OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        sb.Append('|').Append(ComputeSnapshotManifestHash(m.FileHashes ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>结构与状态不变量校验（先于摘要校验；null/非法枚举/非法组合一律拒绝）。</summary>
    public bool IsManifestIntegrityValid(MigrationManifest m)
    {
        try { return IsManifestIntegrityValidCore(m); }
        catch (Exception) { return false; }        // 路径解析等异常 ⇒ **稳定转为验证失败**（不向上抛）
    }

    private bool IsManifestIntegrityValidCore(MigrationManifest m)
    {
        if (m is null) return false;
        if (m.SchemaVersion != 1) return false;
        if (!IsSafeTransactionId(m.TransactionId)) return false;
        if (!string.Equals(m.ConfigRoot, _configRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(m.SnapshotPath) || string.IsNullOrWhiteSpace(m.RollbackEntry)) return false;  // 先验空值，避免 GetFullPath 抛异常
        if (m.SnapshotId is not { Length: 32 }) return false;                                                       // 快照身份必填且格式固定
        foreach (var ch in m.SnapshotId) if (!Uri.IsHexDigit(ch)) return false;                                     // 仅 32 位十六进制（无分隔符/..）
        var expectedSnapshot = SnapshotPathOf(m.TransactionId, m.SnapshotId);
        if (!string.Equals(Path.GetFullPath(m.SnapshotPath ?? ""), Path.GetFullPath(expectedSnapshot), StringComparison.OrdinalIgnoreCase))
            return false;                                                                                            // **精确**绑定本事务快照（非前缀判定）
        if (!IsWithin(m.SnapshotPath, _transactionRoot)) return false;
        if (!Enum.IsDefined(m.Stage)) return false;
        if (m.FileHashes is null || m.ChangedFiles is null) return false;
        foreach (var k in m.FileHashes.Keys) if (!IsSafeRelativePath(k)) return false;
        foreach (var c in m.ChangedFiles)
        {
            if (c is null || !IsSafeRelativePath(c.Path) || !Enum.IsDefined(c.Kind)) return false;
            var inSnapshot = TryGetHashCaseInsensitive(m.FileHashes, c.Path, out _);
            if (c.Kind == ChangeKind.Added && inSnapshot) return false;
            if (c.Kind is ChangeKind.Modified or ChangeKind.Deleted && !inSnapshot) return false;
        }
        if (m.Stage == MigrationStage.Committed)
        {
            if (string.IsNullOrEmpty(m.CommitMarker) || !string.Equals(m.CommitMarker, m.TransactionId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(m.BlockedReason)) return false;          // 已提交不得带 blocked
            if (!m.BaselineCompleted) return false;                            // 已提交 ⇒ 基线必已建立
        }
        else if (!string.IsNullOrEmpty(m.CommitMarker)) return false;          // 非提交态不得带标记（含 Blocked）
        if (m.Stage == MigrationStage.RolledBack && (m.RollbackRehearsed || m.RehearsalScope is not null)) return false;
        if (m.RollbackRehearsed && string.IsNullOrEmpty(m.RehearsalScope)) return false;
        if (m.BlockedReason is { Length: 0 }) return false;
        return string.Equals(m.ManifestIntegrity, ComputeManifestIntegrity(m), StringComparison.Ordinal);
    }

    private static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取并按结构+不变量+完整性校验的 manifest（不合格＝null）。</summary>
    public MigrationManifest? LoadValidated()
    {
        var m = LoadManifest();
        return m is not null && IsManifestIntegrityValid(m) ? m : null;
    }

    /// <summary>读取 manifest（原样，不校验；业务判定用 LoadValidated）。</summary>
    public MigrationManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
        }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private MigrationResult MarkBlocked(string reason)
    {
        var m = LoadManifest();
        if (m is null) return MigrationResult.Fail(reason, MigrationStage.None);
        m.BlockedReason = reason;
        m.Stage = MigrationStage.Blocked;
        m.RollbackRehearsed = false;
        m.RehearsalScope = null;
        m.CommitMarker = null;                 // 与校验规则一致：Blocked 态不得带提交标记（失败处理产出仍可加载）
        WriteManifest(m);
        return MigrationResult.Fail(reason, MigrationStage.Blocked);
    }

    private static string RehearsalScopeOf(MigrationManifest m)
    {
        var sb = new StringBuilder(m.SnapshotManifestHash).Append('|');
        foreach (var c in m.ChangedFiles.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void ApplyRepresentativeChanges(MigrationManifest m, string root)
    {
        foreach (var c in m.ChangedFiles)
        {
            var target = Path.Combine(root, c.Path.Replace('/', Path.DirectorySeparatorChar));
            switch (c.Kind)
            {
                case ChangeKind.Added:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "rehearsal-added", new UTF8Encoding(false));
                    break;
                case ChangeKind.Modified:
                    if (File.Exists(target)) File.WriteAllText(target, "rehearsal-modified", new UTF8Encoding(false));
                    break;
                case ChangeKind.Deleted:
                    if (File.Exists(target)) File.Delete(target);
                    break;
            }
        }
    }

    private void WriteManifest(MigrationManifest manifest)
    {
        Directory.CreateDirectory(_transactionRoot);
        _stageHook?.Invoke(manifest.Stage);
        manifest.ManifestIntegrity = ComputeManifestIntegrity(manifest);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        var tmp = ManifestPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ManifestPath, overwrite: true);
        _stageHook?.Invoke(manifest.Stage);
    }

    private void RestoreFromSnapshot(MigrationManifest m, string targetRoot)
    {
        foreach (var rel in m.FileHashes.Keys)
        {
            if (!IsSafeTarget(targetRoot, rel) || !IsSafeTarget(m.SnapshotPath, rel))
                throw new InvalidOperationException("unsafe_target:" + rel);   // 恢复目标与快照源都须安全
            var source = Path.Combine(m.SnapshotPath, rel.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(targetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, File.ReadAllBytes(source));
            if (string.Equals(Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                    _configRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                _fileRestoredHook?.Invoke(rel);   // 恢复中断注入点（**仅真实配置根**；演练副本不触发）
        }
    }

    /// <summary>
    /// 只删除变更归属为「本事务新增」的文件（无记录 ⇒ 不删任何文件）。返回**失败条数**——
    /// 不安全目标或删除失败**不得静默跳过**：调用方据此保持阻断（不得报告完整回滚）。
    /// </summary>
    private static int DeleteRecordedAdditions(MigrationManifest m, string targetRoot)
    {
        var failed = 0;
        foreach (var c in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
        {
            if (!IsSafeTarget(targetRoot, c.Path)) { failed++; continue; }
            var target = Path.Combine(targetRoot, NormalizePath(c.Path).Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(target)) { failed++; continue; }     // 期望文件却出现目录/目录链接 ⇒ 不得递归删除，计为未完成
            if (!File.Exists(target)) continue;                       // 确认不存在 ⇒ 无需删除
            try { File.Delete(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        return failed;
    }

    /// <summary>释放实际窗口并**使资格失效**（代次前进 ⇒ 历史时间戳/会话不可复用）。</summary>
    private void ReleaseQuiescence()
    {
        try { _quiet?.Dispose(); } catch { }
        _quiet = null;
        _quietValid = false;
        _quietGeneration++;
    }

    /// <summary>确保存在**有效（存续）**静止窗口：已有效则通过；否则重新取得并持久化会话/时刻/代次。</summary>
    private bool EnsureQuiescence(MigrationManifest m)
    {
        if (!_requireQuiescence) return true;
        if (!_quietValid || _quiet is null)
        {
            _quiet = _quiesce?.Invoke();
            if (_quiet is null) return false;
            _quietValid = true;
            _quietGeneration++;
        }
        m.QuiescedAtUtc = _utcNow();
        m.QuiesceSessionId = _sessionId;
        m.QuiesceGeneration = _quietGeneration;
        WriteManifest(m);
        return true;
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

    /// <summary>安全枚举（**先拒绝重解析点再进入子目录**）；用于快照验证读端。</summary>
    private static List<string> EnumerateFilesSafe(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("snapshot_dir_reparse_point:" + dir);
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_dir_reparse_point:" + sub);
                pending.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("snapshot_file_reparse_point:" + f);
                result.Add(Rel(f, root));
            }
        }
        return result;
    }
    private static string Rel(string file, string root) => Path.GetRelativePath(root, file).Replace('\\', '/');
    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        lock (_sync)
        {
            ReleaseQuiescence();
            _lock?.Dispose();
            _lock = null;
        }
    }
}


## source: MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs L1-L220 SHA256=6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>演练步骤结果（报告行）。</summary>
public sealed record MigrationRehearsalStep(string Name, bool Success, string Detail);

/// <summary>演练报告（结构化；供 UI 展示与验收单取证）。</summary>
public sealed class MigrationRehearsalReport
{
    public bool Success { get; init; }
    public string RehearsalRoot { get; init; } = "";
    public string ConfigRoot { get; init; } = "";
    public string TransactionRoot { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    /// <summary>快照清单哈希（事务产物）。</summary>
    public string SnapshotManifestHash { get; init; } = "";
    /// <summary>事务标识（取证：与 manifest/台账对照）。</summary>
    public string TransactionId { get; init; } = "";
    /// <summary>最终事务阶段（取证：回滚后应为 `RolledBack`）。</summary>
    public string FinalStage { get; init; } = "";
    /// <summary>参与比对的文件集合（相对路径，排序）。</summary>
    public IReadOnlyList<string> ComparedFiles { get; init; } = [];
    /// <summary>证据路径（manifest／快照目录／演练根）。</summary>
    public IReadOnlyList<string> EvidencePaths { get; init; } = [];
    /// <summary>逐步骤结果（顺序即执行顺序）。</summary>
    public IReadOnlyList<MigrationRehearsalStep> Steps { get; init; } = [];
    public string Summary => (Success ? "演练通过：" : "演练未通过：")
        + string.Join(" → ", Steps.Select(s => s.Name + (s.Success ? "✓" : "✗")));
}

/// <summary>
/// **R5.6/R5.8「迁移演练」入口（§21.4）**：在**独立配置根**上跑完整切换事务（不接触真实 User 目录）并产出报告。
/// **语义**：①**只接受独立根**——若配置根等于真实 User 目录（调用方传入的 `userConfigRoot`）⇒ 直接拒绝；
/// ②演练＝快照→（无迁移变更的）引用更新→激活→**回滚演练**→提交→**真实回滚**→逐字节比对；
/// ③**不授权生产开门**：本入口不执行真实 User 目录切换（须 owner 另行下令）。
/// </summary>
public static class MigrationRehearsal
{
    /// <summary>
    /// 在 `rehearsalRoot` 下创建独立配置根与事务根，跑完整演练并返回报告。
    /// `userConfigRoot` 用于**拒绝**：配置根不得等于真实 User 目录（防御性校验，调用方仍应传独立根）。
    /// </summary>
    public static MigrationRehearsalReport Run(string rehearsalRoot, string? userConfigRoot = null,
        Func<DateTimeOffset>? utcNow = null, Action<MigrationStage>? stageHook = null)
    {
        if (string.IsNullOrWhiteSpace(rehearsalRoot))
            return Fail("", "", "", new MigrationRehearsalStep("参数校验", false, "rehearsalRoot 为空"));
        string baseRoot;
        string root;
        string configRoot;
        string transactionRoot;
        try
        {
            if (!PathIdentity.TryNormalizeLocalDriveAbsolute(rehearsalRoot, out baseRoot))
                return Fail("", "", "", new MigrationRehearsalStep("独立根校验", false,
                    "演练根须为本地盘符绝对路径（拒绝相对/UNC/设备命名空间，未写入任何内容）"));
            root = Path.Combine(baseRoot, "rehearsal-" + Guid.NewGuid().ToString("N")[..8]);   // **每次新建、独占**
            configRoot = Path.Combine(root, "independent-config-root");
            transactionRoot = Path.Combine(root, "transaction");
        }
        catch (Exception ex)
        {
            return Fail("", "", "", new MigrationRehearsalStep("独立根校验", false,
                "演练根路径非法/无法解析（拒绝，未写入任何内容）：" + ex.GetType().Name));
        }
        var steps = new List<MigrationRehearsalStep>();

        // ① **写入前**独立根校验：不得与真实 User 目录重叠（相等/包含/被包含），不得含重解析点，目标须尚不存在
        try
        {
            if (!string.IsNullOrWhiteSpace(userConfigRoot))
            {
                // 先解析 Windows 最终路径身份，SUBST/映射盘/符号链接不会以字符串前缀漏判；
                // 任一路径无法解析时拒绝，绝不退回纯字符串猜测。
                if (!PathIdentity.TryCanonicalizeForComparison(userConfigRoot, out var user)
                    || !PathIdentity.TryCanonicalizeForComparison(configRoot, out var cfg))
                    return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false,
                        "无法证明真实 User 根与演练配置根的实际路径身份（拒绝，未写入任何内容）"));
                user = user.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                cfg = cfg.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(user, cfg, StringComparison.OrdinalIgnoreCase)
                    || cfg.StartsWith(user + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || user.StartsWith(cfg + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练根与真实 User 目录重叠（拒绝，未写入任何内容）"));
            }
            if (MigrationSwitchTransaction.HasReparsePoint(baseRoot))
                return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练根链上存在重解析点（拒绝，未写入任何内容）"));
            if (Directory.Exists(root) || File.Exists(root))
                return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false, "演练目录已存在（须使用新建独占目录）"));
        }
        catch (Exception ex)
        {
            return Fail(root, configRoot, transactionRoot, new MigrationRehearsalStep("独立根校验", false,
                "用户配置根或演练根路径非法/无法解析（拒绝，未写入任何内容）：" + ex.GetType().Name));
        }
        steps.Add(new MigrationRehearsalStep("独立根校验", true, "写入前校验通过：演练使用新建独占独立配置根"));

        var txId = "rehearsal-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(configRoot);
            // 预置两个文件作为基线（空根亦已由夹具单独覆盖）
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":1}", new UTF8Encoding(false));
            Directory.CreateDirectory(Path.Combine(configRoot, "sub"));
            File.WriteAllText(Path.Combine(configRoot, "sub", "b.json"), "{\"w\":1}", new UTF8Encoding(false));
            var baselineA = File.ReadAllBytes(Path.Combine(configRoot, "a.json"));
            var baselineB = File.ReadAllBytes(Path.Combine(configRoot, "sub", "b.json"));

            using var tx = new MigrationSwitchTransaction(configRoot, transactionRoot, utcNow, () => new NoopQuiet(),
                stageHook: stageHook);
            // **失败即停**：任一步失败 ⇒ 直接返回（不再制造变更、不做未授权回滚）
            var begin = Step("开启事务", tx.BeginTransaction(txId));
            steps.Add(begin);
            if (!begin.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var snap = Step("全量快照（字节+SHA256+清单哈希）", tx.TakeSnapshot());
            steps.Add(snap);
            if (!snap.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var changes = Step("登记变更归属（演练：修改 a.json）",
                tx.RecordChanges([new ChangeRecord { Path = "a.json", Kind = ChangeKind.Modified }]));
            steps.Add(changes);
            if (!changes.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var refUpd = Step("引用更新完成", tx.MarkReferenceUpdateCompleted());
            steps.Add(refUpd);
            if (!refUpd.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var activated = Step("激活（candidate→active）", tx.MarkActivated());
            steps.Add(activated);
            if (!activated.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var rehearsal = Step("回滚演练（复用真实回滚核心）", tx.RehearseRollback());
            steps.Add(rehearsal);
            if (!rehearsal.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            var commit = Step("提交（写唯一提交标记）", tx.Commit());
            steps.Add(commit);
            if (!commit.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);

            // 提交后制造**真实变更**，再执行真实回滚并**全文件集字节比对**
            File.WriteAllText(Path.Combine(configRoot, "a.json"), "{\"v\":2}", new UTF8Encoding(false));
            var rollback = tx.Rollback();
            steps.Add(Step("真实回滚（恢复旧字节+按归属清理+撤销激活）", rollback));
            if (!rollback.Success) return Abort(tx, steps, root, configRoot, transactionRoot, txId);
            // 全文件集比对：**完整文件集合 + 逐字节**（含被修改与未被修改者；新增文件须不存在）
            var after = Directory.EnumerateFiles(configRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(configRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var identical = after.Count == 2 && after[0] == "a.json" && after[1] == "sub/b.json"
                            && BytesEqual(Path.Combine(configRoot, "a.json"), baselineA)
                            && BytesEqual(Path.Combine(configRoot, "sub", "b.json"), baselineB);
            steps.Add(new MigrationRehearsalStep("全文件集逐字节比对", identical,
                identical ? "回滚后完整文件集合与内容均与基线逐字节一致" : "回滚后文件集合或内容与基线不一致"));

            var manifest = tx.LoadManifest();
            steps.Add(new MigrationRehearsalStep("回滚终态校验",
                manifest is not null && manifest.Stage == MigrationStage.RolledBack && manifest.CommitMarker is null,
                manifest is null ? "manifest 不可读" : "stage=" + manifest.Stage + " marker=" + (manifest.CommitMarker ?? "<null>")));
            return new MigrationRehearsalReport
            {
                Success = steps.All(s => s.Success),
                RehearsalRoot = root,
                ConfigRoot = configRoot,
                TransactionRoot = transactionRoot,
                ManifestPath = tx.ManifestPath,
                SnapshotManifestHash = manifest?.SnapshotManifestHash ?? "",
                TransactionId = txId,
                FinalStage = manifest?.Stage.ToString() ?? "",
                ComparedFiles = after,
                EvidencePaths = [tx.ManifestPath, manifest?.SnapshotPath ?? "", root],
                Steps = steps,
            };
        }
        catch (Exception ex)
        {
            steps.Add(new MigrationRehearsalStep("异常", false, ex.GetType().Name + ": " + ex.Message));
            return new MigrationRehearsalReport
            {
                Success = false, RehearsalRoot = root, ConfigRoot = configRoot,
                TransactionRoot = transactionRoot, TransactionId = txId,
                ManifestPath = File.Exists(Path.Combine(transactionRoot, "migration-manifest.json")) ? Path.Combine(transactionRoot, "migration-manifest.json") : "",
                Steps = steps,
            };
        }
    }

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    /// <summary>失败即停：不再制造变更、不做未授权回滚；报告保留**已形成的** manifest/事务标识以便取证。</summary>
    private static MigrationRehearsalReport Abort(MigrationSwitchTransaction tx, List<MigrationRehearsalStep> steps,
        string root, string configRoot, string transactionRoot, string txId)
    {
        var m = tx.LoadManifest();
        return new MigrationRehearsalReport
        {
            Success = false,
            RehearsalRoot = root,
            ConfigRoot = configRoot,
            TransactionRoot = transactionRoot,
            ManifestPath = File.Exists(tx.ManifestPath) ? tx.ManifestPath : "",
            TransactionId = txId,
            FinalStage = m?.Stage.ToString() ?? "",
            SnapshotManifestHash = m?.SnapshotManifestHash ?? "",
            EvidencePaths = File.Exists(tx.ManifestPath) ? [tx.ManifestPath, root] : [root],
            Steps = steps,
        };
    }

    private static MigrationRehearsalStep Step(string name, MigrationResult r)
        => new(name, r.Success, r.Success ? r.Stage.ToString() : r.Reason);

    private static MigrationRehearsalReport Fail(string root, string cfg, string tx, MigrationRehearsalStep step)
        => new() { Success = false, RehearsalRoot = root, ConfigRoot = cfg, TransactionRoot = tx, Steps = [step] };

    private static string Sha256(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class NoopQuiet : IDisposable { public void Dispose() { } }
}


## findings: _workflow/r56-mainline-integration/subagent/readonly-audit-report.md L1-L136 SHA256=9f600a90d2e309fdd5d9847aa159d3e74b9cec819684406b63575bf847d53e0f
独立核查结论与补充发现；其中 F-4 为待会诊判定的语义/覆盖问题，是本次送审的显式问项之一。
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


## Review readiness (mechanical claims, not quality verdict)
{
  "opening": {
    "opening_snapshot": "_workflow/r56-mainline-integration/opening.json",
    "opening_head": "8a3ee6c4c98e845b2988774fe9c3ab65343ce33e",
    "risk_matrix": "_workflow/r56-mainline-integration/risk-matrix.json",
    "risk_rows": 7,
    "subagents": "used",
    "existing_results": "none",
    "review_round": 1,
    "prior_findings": 0,
    "quality_verdict": "NOT PROVIDED"
  },
  "risk_matrix": {
    "schema_version": 1,
    "batch": "r56-mainline-integration-2026-09-28",
    "rows": [
      {
        "id": "INT-S1",
        "dimension": "state",
        "scenario": "来源交付 r56-migration-audit 的测试增量导入主线后，导入结果与来源 blob 逐字节一致，且新增 theory 在主线上覆盖 extra/missing/changed 三情形并按预期原因拒绝提交。",
        "expected": "git rev-parse HEAD:<test path> 等于来源提交 c11cb45f6 的同路径 blob；主线导入后定向集合 81/81 通过，三行 theory 全部 Passed 且 VerifySnapshot 返回 snapshot_untracked_file/snapshot_file_missing/snapshot_hash_mismatch。",
        "critical": true,
        "status": "covered",
        "note": "导入等价性由 intake-equivalence.json 证明（blob 相同、工作区 4C101FF3…）；三行 theory 的 testId 与来源 results.json 记录一致。",
        "counterexample_ids": [
          "targeted-final-v2"
        ],
        "test_ids": [
          "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
          "bb416257-70f3-d03a-4df9-51f86d37131c"
        ],
        "mutation_ids": [
          "extra",
          "missing",
          "changed"
        ]
      },
      {
        "id": "INT-S2",
        "dimension": "state",
        "scenario": "导入不改变既有 R56/R58 迁移与演练测试的身份与结果：导入前基线集合与导入后集合的既有成员逐名一致，全量 testId 差集只体现本批新增 theory。",
        "expected": "导入前同条件基线定向 78/78、助手全量 1566/2/0/1568；导入后定向 81/81、助手全量 1569/2/0/1571；testId 差集仅 added=3、removed=0、changed=0、其余 unchanged。",
        "critical": false,
        "status": "covered",
        "note": "本行 expected 的绝对值按上一子批散文预登记（1566/1568→1569/1571），实测为 1567/1569→1570/1572（+1）。差额已定位为上一子批散文漏记第 2 轮会诊新增夹具（见 findings.md F-1/F-2 与 R5.3 §24.128.4），非本批导入所致；本行的实体主张（导入只新增 theory、既有成员不变）由 testid-comparison.json 证明：added=3/removed=0/changed=0。冻结行不改写。",
        "counterexample_ids": [
          "full-final-v2"
        ],
        "test_ids": [
          "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
          "bb416257-70f3-d03a-4df9-51f86d37131c"
        ]
      },
      {
        "id": "INT-C1",
        "dimension": "concurrency",
        "scenario": "来源交付的关键断言在主线集成字节上重新做反向突变：修改生产实现后具名断言必须失败，源码逐字节恢复后同条件转绿。",
        "expected": "5 项突变（extra/missing/changed/commit-refusal/production-gate）均为 baseline Passed、mutant Failed（命中具名 testId 与指行断言）、restored Passed，且恢复后源码 SHA-256 与原始相同。",
        "critical": true,
        "status": "covered",
        "note": "5 项突变在集成字节上重跑；4/5 的 mutant SHA-256（CRLF 形态）与来源记录完全相同，extra 为语义等价独立文本突变。",
        "counterexample_ids": [
          "restored-missing",
          "restored-changed",
          "restored-extra"
        ],
        "test_ids": [
          "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
          "bb416257-70f3-d03a-4df9-51f86d37131c"
        ],
        "mutation_ids": [
          "extra",
          "missing",
          "changed",
          "commit-refusal",
          "production-gate"
        ]
      },
      {
        "id": "INT-C2",
        "dimension": "concurrency",
        "scenario": "新增 theory 使用进程内临时根与重复运行，不与其他迁移夹具共享可变状态；重复执行同一集合结果稳定，无跨测试污染。",
        "expected": "同一源码版本上重复执行的定向集合与首次执行通过数一致（81/81），助手全量在导入后仍为 0 失败。",
        "critical": false,
        "status": "covered",
        "note": "同一源码版本上重复执行定向集合：baseline 78/78 与 final 81/81 均为 0 失败；导入后全量 1570/2/0/1572。",
        "counterexample_ids": [
          "targeted-final-v2"
        ],
        "test_ids": [
          "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
          "bb416257-70f3-d03a-4df9-51f86d37131c"
        ]
      },
      {
        "id": "INT-F1",
        "dimension": "fault",
        "scenario": "快照故障路径在主线集成字节上仍失败闭锁：多余/缺失/被篡改的快照文件必须被 VerifySnapshot 指名拒绝，Commit 拒绝且阶段保持 Activated、无 CommitMarker。",
        "expected": "三情形 theory 断言 expectedReason、Commit.Success=false、Reason 以 snapshot_invalid: 开头、Stage=Activated、CommitMarker=null；对应突变证明判别力。",
        "critical": true,
        "status": "covered",
        "note": "三情形断言 expectedReason + Commit 拒绝 + Stage=Activated + CommitMarker=null；commit-refusal 突变专门放宽 Commit 门，命中 :line 190。",
        "counterexample_ids": [
          "targeted-final-v2"
        ],
        "test_ids": [
          "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
          "bb416257-70f3-d03a-4df9-51f86d37131c"
        ],
        "mutation_ids": [
          "extra",
          "missing",
          "changed",
          "commit-refusal"
        ]
      },
      {
        "id": "INT-F2",
        "dimension": "fault",
        "scenario": "未通过生产授权时不得执行动作：TryRunProduction 在授权失败时必须返回失败且动作零次执行。",
        "expected": "theory 断言 TryRunProduction(...).Success=false 与执行计数等于 0；production-gate 突变放宽授权后该断言变红。",
        "critical": true,
        "status": "covered",
        "note": "production-gate 突变在授权失败时仍执行动作，命中 :line 196 的执行计数断言。",
        "counterexample_ids": [
          "targeted-final-v2"
        ],
        "test_ids": [
          "7481b2d1-35ca-c66b-9954-00d698d9ff2d"
        ],
        "mutation_ids": [
          "production-gate"
        ]
      },
      {
        "id": "INT-F3",
        "dimension": "fault",
        "scenario": "构建/测试不得写入部署目标目录（BGI 产品运行目录）。",
        "expected": "全部构建与测试命令带 -p:DeployToBgiTools=false，部署目标路径在前后清单中不存在或未变化。",
        "critical": false,
        "status": "not_applicable",
        "reason": "本批无产品行为或构建配置改动，\"部署目标被写入\"不是本批引入的行为分支；该风险以构建参数 -p:DeployToBgiTools=false 与部署目标前后清单（deploy-target-before/after/comparison.json，level=document）机械登记，不适用 test/runtime 反例。前后清单摘要相同（1158 文件）。"
      }
    ]
  },
  "review_control": {
    "existing_results": {
      "decision": "none",
      "reason": "逐项核对现有交付：来源组件交付 c11cb45f6 的 81/81 与助手 1482/2/0/1484 绑定隔离 worktree 的开工字节（测试源为交付版、产品源为旧主线字节），不是主线集成 HEAD 的结果；§24.126/§24.127 的主线回归与突变绑定 WorkflowRunner 相关字节，与 R5.6 迁移组件无关；来源的 extra 突变未记录精确 needle，其 mutant 无法复用。因此无可复用的同版本集成回归或突变结果，本批在主线上重新执行。"
    },
    "review_round": 1,
    "prior_findings": [],
    "criticality_reason": "INT-S1/INT-C1/INT-F1/INT-F2 设 critical：它们决定「导入是否等价」「关键断言是否仍有判别力」「损坏快照能否被提交」与「未授权是否仍零执行」，其中后者涉及不可撤销副作用（提交/执行）与失败闭锁，故要求具名突变绑定。",
    "subagents": {
      "decision": "used",
      "reason": "固定开工 ref 上的只读核查有净收益：它独立核对了来源交付的 7 行覆盖矩阵与主线实现的逐行对应、A–F 接线现状（含全仓有界搜索）与导入 theory 的断言强度，并给出主执行者未预先假设的两项新信息（授权路径不复查快照的覆盖边界 F-4、TaskCenterHost.Admission.cs 行号漂移 F-5）；该核查不写文件、不运行测试，因而不与主执行者的回归/突变证据重叠。",
      "tasks": [
        {
          "question": "在固定开工 ref 上核对：交付 A 覆盖矩阵 7 行与主线实现是否逐行一致；TryRunProduction 是否仍无外部调用方；交付 B 的 A–F 是否有项已被主线接线；导入 theory 的断言是否弱于主张。",
          "read_only": true,
          "fixed_ref": "8a3ee6c4c98e845b2988774fe9c3ab65343ce33e",
          "opening_source_hashes": {
            "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
            "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
            "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
          },
          "report_evidence_id": "subagent-readonly-audit"
        }
      ]
    }
  }
}

## Generated evidence overview (mechanical only)
{
  "tests": {
    "_workflow/r56-mainline-integration/final/targeted-final-v2.trx": {
      "total": 81,
      "passed": 81,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-mainline-integration/final/assistant-full-final-v2.trx": {
      "total": 1572,
      "passed": 1570,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/r56-mainline-integration/claims/claim-postconsult-noenv.trx": {
      "total": 1,
      "passed": 1,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\extra\\restored.trx": {
      "total": 3,
      "passed": 3,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\missing\\restored.trx": {
      "total": 3,
      "passed": 3,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\changed\\restored.trx": {
      "total": 3,
      "passed": 3,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\commit-refusal\\restored.trx": {
      "total": 3,
      "passed": 3,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\production-gate\\restored.trx": {
      "total": 3,
      "passed": 3,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\extra\\mutant.trx": {
      "total": 3,
      "passed": 2,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\missing\\mutant.trx": {
      "total": 3,
      "passed": 2,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\changed\\mutant.trx": {
      "total": 3,
      "passed": 2,
      "failed": 1,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\commit-refusal\\mutant.trx": {
      "total": 3,
      "passed": 0,
      "failed": 3,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow\\r56-mainline-integration\\mutations\\production-gate\\mutant.trx": {
      "total": 3,
      "passed": 0,
      "failed": 3,
      "skipped": 0,
      "duplicate_names": {}
    },
    "_workflow/r56-mainline-integration/baseline/assistant-full-baseline.trx": {
      "total": 1569,
      "passed": 1567,
      "failed": 0,
      "skipped": 2,
      "duplicate_names": {
        "MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.SubmissionPointInventoryTests.IndirectSendFormPatterns_HaveExpectedSamples(id: \"reflection-name-filter\", sample: \"var m = typeof(IpcClient).GetMethods().First(x => \"···, expected: True)": 3
      }
    },
    "_workflow/r56-mainline-integration/final/targeted-final.trx": {
      "total": 81,
      "passed": 81,
      "failed": 0,
      "skipped": 0,
      "duplicate_names": {}
    }
  },
  "comparison": {
    "identity_basis": "testId; provider changes require manual reconciliation",
    "added_ids": [
      "7481b2d1-35ca-c66b-9954-00d698d9ff2d",
      "b1efc0c6-a858-0d3a-f1ea-1cf252267ba9",
      "bb416257-70f3-d03a-4df9-51f86d37131c"
    ],
    "removed_ids": [],
    "changed_ids": [],
    "unchanged_ids": [
      "00142b57-1df8-928f-2651-3cbbb3206447",
      "0029f7b4-4cbb-e63f-7b5a-c30fa71e3281",
      "003a600a-a77b-ea57-dac9-336c7597f3a1",
      "003d5177-4eb2-cf90-dfc0-0922617f3967",
      "004be13c-df7b-6dce-dc23-8f25831c275a",
      "00561123-4e38-0327-8a59-60c754b57e14",
      "009197d2-4962-c363-1781-ec974087b085",
      "00bae332-096a-6738-aa59-fa898eb9d249",
      "00f53e62-c820-0cfc-f1b8-92d3e07e3280",
      "00fd1f0c-d2db-d03a-ec3d-4c1fa2db9c79",
      "01079a79-95f7-8058-b3d3-c07611ef62d6",
      "0147a014-80d6-daff-6fa2-066622dd1389",
      "0189c645-a3c5-e893-d377-56937c56d6ba",
      "019a9877-8f34-5643-0454-4b04bbee58a2",
      "01bd8e51-65e1-bf78-ec97-394754066ce7",
      "02121445-9cfa-e6ae-5a08-f0096bf979e5",
      "021b7f9f-0aee-4bb3-82c2-cd0a485f48e5",
      "0223a883-d0f3-d8be-d213-a74a83e4f9f3",
      "02305864-f10b-4e97-86d7-e604303554de",
      "023359c9-267e-2daf-a7cb-411d05b8545e",
      "024e5643-ef97-1f6e-5b7f-8200ca550fe7",
      "025141ac-b6a6-7938-e786-37c41145f43a",
      "02d9d50c-8920-a196-ab6a-c853befc2e4b",
      "0315f37c-45f6-8125-cd57-8e9658b09a66",
      "03221eac-6101-edd7-8229-82e79bab7090",
      "034f5f27-dd97-f4ca-61d5-0a63fc519270",
      "0366307a-5837-3de2-867a-0aede6702d98",
      "03ee9dd3-83e0-5921-6230-80e96ef329a5",
      "04442ed7-ad82-465e-5627-36fe971841df",
      "045de764-8532-677d-4ced-5a18a58883a5",
      "046edc8b-21d9-ee32-c595-04fbca9472b5",
      "0495c5c2-f2bf-78cd-28e4-54742d868691",
      "04af6a23-eaa4-291f-b8d4-35f990558b0d",
      "0510bf67-65ea-147d-7277-c229d2ec2b59",
      "052784c0-6d50-5e34-ab72-0ed54588bfb6",
      "054386e2-c6fd-49a3-d5c2-de430858192c",
      "054abd8e-5417-4420-236d-48c480a779be",
      "054b652d-c40f-684d-f64e-0a22289fe986",
      "0571ad29-6729-373b-9dfc-e398f772fd17",
      "057ecaf9-acdd-0fb3-b353-5fea565edbdc",
      "05c5442b-1658-6289-be67-54e1b3d7fec8",
      "05fb4393-9d22-27a2-41d0-ee4f8727f8d0",
      "05fface1-85ac-b4ab-1c49-1097352578a6",
      "06492130-398c-399f-6e68-527b7b5bc499",
      "067149f9-0370-4184-d971-d15f695706a2",
      "06a8c9f2-fa4d-c63c-2244-7747daa4bc2f",
      "06cfa5f6-a7a8-1274-202b-4ea3d454f210",
      "06d16786-48d0-02c8-6d54-afd6eb30370d",
      "06dba09a-e49b-c2fe-9402-b1e827f6da92",
      "06f9f636-aaad-06a7-4e32-a78785d62290",
      "078165f1-6c31-a760-fed6-de6958300469",
      "0791e4d4-2d59-bd27-5e2d-0abfc4c478d8",
      "07a9ab30-beff-e25a-f695-ea667ef86527",
      "07fc34dd-5e13-1688-f973-796a9039381a",
      "08014a98-94c7-5c1b-85a5-711be1f776c4",
      "0804af87-f343-333f-61d8-ecc9d77ea5f3",
      "0808d2a7-164c-c1ad-9ddd-d2a377128055",
      "0871427e-dcbe-6848-48d8-aa138c852ce1",
      "0878fd71-2dda-a1af-e169-e42d2a96a5df",
      "08871413-002b-e4e4-bc40-b7a852c49f37",
      "08a0ae26-bbb0-4758-a916-604b6207168f",
      "08f1c7c1-f15d-3e4c-7238-12c527ae5213",
      "08f4e732-b1d6-2bb4-bd69-9137ccd7e904",
      "0927c39f-e0cf-acb6-30ee-ed6f7d2b048c",
      "095a3ac4-3dac-a8b7-0057-a40a703e7058",
      "095abd7d-263f-ade9-f942-1bc1430e7470",
      "099829df-2e76-bdfd-f274-26b2c9a6a5d3",
      "09a05ca4-4ace-0785-981a-184674460496",
      "09a61b22-8045-cbcb-8acf-b25c18cb2848",
      "09fe8795-85ae-161f-13de-b462eae553e2",
      "0a05ef84-0523-4a1f-e4e7-2188426ffb7b",
      "0a33feed-7e0a-c4fe-19b5-fb778bc0a5ec",
      "0a3d044c-ed18-dd73-584b-75754bf5815e",
      "0a43a784-9763-43ca-71d5-c7fa3ef8e364",
      "0a5455aa-4a2d-3d48-3283-452a5dba23b8",
      "0a889cb7-3bce-7150-66a8-55fc4a4ddf5f",
      "0aaeca06-c904-2318-3c17-4850fac4875c",
      "0ad18e39-1bac-3b01-70da-1861a75fdd8d",
      "0ad96afc-4b16-be75-51fc-47c360770156",
      "0af21ded-4f28-aac4-d2e5-fe525dfa2a11",
      "0b970562-a75b-ca3f-5720-58fa0cdb7a06",
      "0b9712bb-ebf9-4b53-7dc1-dc96a0be7129",
      "0bc18e5b-5778-0c88-a844-a1e45ca5923e",
      "0bf48d71-bf36-69d4-3ce9-b95b0a390e8e",
      "0bf9c977-664e-5ffe-c39c-c2dced02d6b3",
      "0bfa7728-821b-ed8c-3cf9-4a6cac8c6553",
      "0c1d538d-bf33-7474-c7b7-e4443ff168f7",
      "0c4adbe0-49c7-c8cf-a943-6701e7b4ac4c",
      "0ca9b55e-bbbd-4256-2736-b43bc4c9e04c",
      "0ce085f0-0e7e-2726-1397-71c4505cae11",
      "0cec4223-d71b-e7dc-d677-c175650edc03",
      "0d052212-97ab-692f-e1a7-11c43c1abfb7",
      "0d3d066f-c4a5-b9b7-efff-855452aa5b5c",
      "0d5cc7f5-04b8-111a-8a9b-8727025fdb5f",
      "0d5d4664-d63a-36dc-c4b7-e80a1ea41c29",
      "0d5d5b4b-c48d-6c1c-26e2-85f4147fe8f8",
      "0e1187d5-6686-1d05-0e83-e82e837b7271",
      "0e4105d6-0c09-6fbc-3cb2-dc172274d528",
      "0e419e65-ea97-9104-dc61-306ad26adf4b",
      "0e45c80c-838c-ea53-95b3-8566619958bd",
      "0e6f7184-5df0-48a4-50fd-f4114cc0615d",
      "0e893e81-3b49-a44c-7d41-c56d0fd6eff3",
      "0e8c34cf-fcb8-7d9a-5214-ada45efcfb98",
      "0e9cfdd0-9994-a02f-ab43-d87f585627b7",
      "0e9d82b0-bf7f-3cad-4ad7-b619035603d3",
      "0f1b4b43-2174-bb8e-2959-449657fcc9d8",
      "0f28ef63-1962-b2c7-ac46-e277ebe8f527",
      "0f2cb31f-05b0-568e-9d26-4a686b620ddf",
      "0fe6e276-2320-ac40-58b3-3a84023b9b58",
      "0fed73b3-5124-3481-43ee-ef3bddd56b86",
      "0ff4fb0e-be67-5083-a7fe-24a472139116",
      "0ffcb24a-3e07-098e-ebf0-bfa83871e165",
      "101929e7-d0dc-7453-c004-ed9714a6599e",
      "1024a001-8280-110a-3f94-41e4229b1de3",
      "102caee8-97ad-b1e9-2cc2-2de07015ea43",
      "1085391a-daa8-0a43-1e8e-19fe0dade5f7",
      "10b11358-0d38-ac41-3599-6e9c6e13826f",
      "10c6a8cc-f33f-af9c-7181-18960c333def",
      "10c706cd-f9e7-e2b8-df67-bca171864646",
      "10dfd457-b43a-77c1-5c41-6bea79edd369",
      "10e0235f-7546-84da-a25e-bb8a80fe025c",
      "10fc23c2-fa03-9d03-a34c-502d5972cb65",
      "113f020c-1c29-e336-4c1a-3a50b69cbcc8",
      "11a8b597-c214-e249-f365-608e6fa633cd",
      "11c28d5e-d922-1dfd-ea48-4a52ddf39535",
      "11ce9403-f420-8054-c42e-70055a934c9f",
      "11d6262f-352d-e185-ac7e-ce17d38c0354",
      "11f522a8-241f-d91c-9304-1c32e6cd57c6",
      "121142e7-9bdb-3594-c6a0-9add18855844",
      "129fd047-3028-277b-c599-fbab9cafcc5b",
      "12a70f37-cf12-7de6-de6a-fd841495405a",
      "12d0e8ad-db9c-edf9-ee94-b64afd4db362",
      "131d17e6-b057-99df-39e8-01db618f7737",
      "13530bff-f176-5745-f265-8aaa2adce3e3",
      "1357f438-f8fd-ed43-413c-0a939516552b",
      "13a5bbe8-89f2-3d01-1a68-1c54ced7eccd",
      "14164b6a-cc69-3859-eee8-0630c138ce28",
      "141c3871-f4d2-0409-8fd8-74790a37f76a",
      "142ef5f1-b79f-c83a-cdce-c15299459e48",
      "1440192f-f2a7-024e-6ff0-c85606d3f668",
      "144e77c8-dde4-b90e-6ef4-67c275627d02",
      "148d427e-7143-588b-09e9-0074651b3590",
      "14addbd1-4d7d-814d-ec3d-c070098441ad",
      "14b12c92-31c4-d84d-5129-de01fac6b561",
      "14c7717c-4fab-0f2f-9e2b-5efcba4b124a",
      "14dd3e0f-2c52-548c-96f8-a12c8084c3c2",
      "151ac447-4101-a692-a5c2-d6b1f50b513b",
      "159174ae-6a51-5cfc-c09f-6f3bc06eaeb2",
      "15a13e67-cbe8-66f2-3b44-3878d536d5df",
      "15c2ace4-f76d-791f-78a2-7cec261109bd",
      "15dbe3b0-f449-f21b-aeb9-3d77ff171fee",
      "15f6117e-b614-0b91-832e-114d26b1209e",
      "1619b2b0-813d-3b20-086b-eb87ee62d0ff",
      "164246a2-ce6a-6430-5a57-7d6f1add27da",
      "16672732-edc7-4bb8-ca13-466826858396",
      "16860feb-8f9b-5ac1-d91a-8eb67eb8059f",
      "16a1b797-7b4b-37cd-339b-efbee393adc2",
      "16c35e31-f480-a72b-fc90-21246e4e2c42",
      "1716ee02-bfe0-2c6c-9e01-e6224bb95048",
      "1795f64c-da77-e8d7-2553-f809a15ead01",
      "17b632b8-4769-8870-9bcd-c783e73d90cc",
      "17fe3ac0-768a-8f20-c800-1122ffca4db4",
      "1845a269-edde-acc6-2863-aa8fda4ab402",
      "18b4a3db-dc7a-ea36-0f33-864f0940aea3",
      "194b3142-c253-a959-de7a-8a1b489c8e47",
      "194fcedb-5e86-679a-1cd6-c4485e36ae7c",
      "195e2102-a019-b234-3594-76fd527dafba",
      "197592fe-b175-34ac-9021-b4baa8d3575c",
      "1998488c-f983-de71-f1a9-a210758ec5f0",
      "19cf57a8-3e5d-df79-63d9-0f0c3007126c",
      "19e5baf9-b263-6b1f-5c8f-6ef294a14306",
      "19ff07a3-d7ef-2f7a-8f37-f827991c5aa3",
      "1a124453-a7b4-e27f-f8ff-45b6b1679d91",
      "1a26c543-b582-710f-dade-a54ce67ce9da",
      "1a270617-9920-6d68-3bae-9e32762b17c7",
      "1a3ca4a7-d969-e1aa-1ff0-0da847e2786f",
      "1a6fd421-c147-72bd-bf97-4f0f29722972",
      "1a7da4d6-d4b8-d91b-ed46-f34c5420aea7",
      "1a90fbc7-b7c3-9717-624c-6c4e4a6c3945",
      "1a981ac3-e494-c3e3-a8c9-1fa101f6398a",
      "1a9d4afa-5d09-4ef7-d1f7-d1751ee818e5",
      "1aceeccd-0427-4d41-c9de-cd3e54e63c02",
      "1ae03c6c-32f4-4acd-4022-f1433b83c5ff",
      "1b6b91d3-3dbe-ce42-ee59-4e73492fd276",
      "1b770f31-fabf-108e-9cff-f320a659957a",
      "1b7da3d7-39c4-3576-1f76-c13bedc18a2b",
      "1b966097-289d-5c9a-054c-6248d4c5624d",
      "1c3341c6-410d-0a23-173f-e21152311368",
      "1c3ec39b-92c2-7700-ac3e-7dc1bae2c896",
      "1c491d2b-a18c-d800-908f-6f2b75dc0445",
      "1c703abe-5121-67a6-efca-61d127f0f93f",
      "1c9118b6-37e5-b14b-e5e3-402aa46d43fc",
      "1ca019c8-8c12-999c-aaf8-fb8e279ca622",
      "1ca3c6bf-e3ef-4045-269c-159fe8ae537e",
      "1ce1a004-8458-54cc-3ebd-b38929f9326d",
      "1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc",
      "1d24eedb-5451-1e47-f74c-681c3a2d4d35",
      "1d68c536-f3ce-838c-2a56-f780b96d365d",
      "1d780516-9761-fa27-2f42-4d437a0b878c",
      "1dc7fe41-0d08-df13-8a38-e8102ce1fda7",
      "1dd4356a-07e8-ca20-9443-79d10b4919b6",
      "1de70f01-c8c7-4564-091c-8a3e9a15b0ca",
      "1e300de3-48b6-274a-aec5-41b657a7cd8e",
      "1e60fe17-d75e-c8f0-b16d-2dfd60edaaa6",
      "1e70d6a8-25e8-7050-1a1c-ecd6821ad4d8",
      "1f39b3bd-736e-11d1-8d3e-b2a7b4e64494",
      "1f648110-8e74-3dd2-a83a-4a5c96391189",
      "1f7911fd-2c6a-cb0d-71c3-fc9dc0693a3d",
      "1f939dd6-118c-65bb-43a4-7b9fbba84d36",
      "1fb4f53a-d7fd-a5fa-a807-ae35455e815e",
      "20011b8a-b299-4d6c-8208-e0d42beeb3d5",
      "200b98fa-0343-fcc4-5eba-8c81d2e7cb1a",
      "2010f702-2803-6ddb-49b4-2c517b3ab370",
      "2025f31f-39d0-43b6-a7b3-152d2285164f",
      "203c29c3-8043-1331-ce78-9891a7030967",
      "205da83c-d919-af08-cfec-07769e61f8a5",
      "20655ff5-3ae8-a011-2e28-f9405d8a1220",
      "20be6204-98ae-8571-9fe0-935a37ac0d9c",
      "20e9bf2f-8fce-e2c8-b678-3bf0893d9003",
      "2117176e-8aad-a2bb-a3c7-2459ccd33c86",
      "21ad2012-15d1-21f5-b558-c3807229324c",
      "21d5dab5-9c3b-8d2a-84ad-256d9b0a03a3",
      "21e83bdb-3a7d-3ac8-9e6a-1931e708118c",
      "21ec47d1-b180-e0f3-673b-647d6807a24b",
      "2211ab88-f62f-e169-f302-bea2c6355975",
      "2231c264-20e5-0421-62a8-f8e9f62ea308",
      "2245f9a6-c1e7-632a-e805-842f1d043f2b",
      "2267dc2e-4528-6c84-e77f-b1fdde4dad29",
      "2277a9d8-fbb9-8230-5298-2d3ec8960c86",
      "22ac99f3-9601-5fb7-d4a6-f2521be8c3a8",
      "2318056c-6e28-881e-7126-51cb9c6d2495",
      "233e2c80-440d-8d50-75a9-49fa2da015f2",
      "234f5a00-94a4-8ccc-7814-b9f85865a769",
      "23b1d4d8-798b-aced-dfee-f034086a46c6",
      "23e435a7-e1ad-669d-f741-b5faf0e3c47a",
      "2400f663-306d-9c34-3603-082688a612dd",
      "24028c13-098e-9d7d-2897-41888cdf195d",
      "24051436-cd3d-6e8b-f80a-5cc6eb6bc99d",
      "2406d19e-fdeb-2531-480b-04db277cdee4",
      "2408b12a-3be2-ed41-141e-f00bf650f0c8",
      "24420979-ecf1-5ecc-c550-e3430be1bdb7",
      "24596fd0-cb59-f8fb-764c-7a86429fbefa",
      "2486ea99-6168-bf04-8bbc-afeb6e5e1171",
      "248d5545-dff4-4a61-4840-f14b3c748475",
      "24a61d65-808c-9cc5-0347-dd0f2c6c6cd7",
      "24babe44-0d56-2504-dcc6-4d65f67e0cc1",
      "24c4b9f5-7a3b-5d1b-78c7-642291dda286",
      "24c8b883-ce39-5d1a-aeaa-95a50e4cc42f",
      "24d56785-d648-36a9-f60d-0ded110425be",
      "24ee8844-8c3c-5434-74d7-b57d3aeadc67",
      "24fddba1-235d-f407-f8fe-396dbd825383",
      "25421dda-36a6-5dc7-c426-bf907d060cec",
      "254a3475-b34c-5a30-4d58-68cc51efad60",
      "256e8bf4-1a23-fe77-66de-4309fa416c59",
      "259feb58-4a10-1216-3c17-14e62e308993",
      "25b25ff2-2389-8c5b-5aab-5b575640bf32",
      "25cfed8c-ec8d-6473-5b81-df7b709e89da",
      "260d9a6e-4e9e-c464-e723-c4ae8e20eaf4",
      "2616dd8c-c42c-6a14-5c29-ba1f9b6ba217",
      "2643e525-faf9-a7b2-961e-d9ae2cf8c670",
      "26ce627b-9b8d-0e56-5659-be317ae7c38a",
      "26d23683-7ab4-1fde-cd65-aedbd21fd783",
      "26df61dd-f5e1-2968-df0e-d1c3964c42e8",
      "26ea9147-8a3e-37b1-7ebf-7a7807578ca0",
      "26eaac62-563c-8447-1dde-e446dae7d965",
      "270ef12e-0f7f-57dc-e19d-fbcb33334708",
      "278249ab-bdf4-4a47-a0fa-c9e5435babea",
      "279a3bb5-25b8-3648-6c4a-1f0b5f0e6d1a",
      "27b7d19d-3d2c-5b8a-1093-79de2489f8ad",
      "27d09088-d434-9ba0-dfe6-99a16f57fa42",
      "27d2ae6c-bcfb-d9ef-698c-e4fd188b776d",
      "27d87b49-979e-9dc4-59bb-e846ab279bc4",
      "27e518f0-41f3-4a16-4164-e9bf5a28c36e",
      "280ee53c-d14f-2e67-2b26-734411ced7b8",
      "287c9ab8-7d2b-6385-1f1a-accbccedb6fa",
      "2889e327-2d87-fa7b-6beb-395a398342b3",
      "28add68c-ab0a-13e4-6b2e-6148412cb5c3",
      "28bfc9f6-f898-5914-8fd6-7458eec24ae2",
      "28e43da3-f3b7-324d-bff5-37796cfff13d",
      "291f7b72-705b-75d2-0a0a-8b4180529f40",
      "293a4953-c134-3300-f941-c26d943864f1",
      "2962cd4d-18b4-7352-f31f-57a3e538da55",
      "296c7867-baa5-9d54-ce57-7c4b0c97416b",
      "29a0500a-c5f6-5c72-434f-b76556fb7a1a",
      "29a8d075-62aa-15c7-957d-d3587a81a54c",
      "2a110364-66d8-3e33-fbe4-06d907c54f09",
      "2aaa57d9-3907-c14e-e49d-438fdfb977cc",
      "2abb8b27-1b51-4540-7944-3805c44969fa",
      "2adea4d4-1a8a-361b-f277-1b8270bcd680",
      "2af0c04a-24c4-5566-bce4-1ef851ad7c02",
      "2b0e2ad8-e5dc-0c76-f30c-91868822f5c7",
      "2b164511-52d5-1204-f0d3-00e398253f9e",
      "2b19c8ed-7268-d67f-bbc5-fc083217fdd4",
      "2b1e40c8-5ca9-9ad6-e47b-4a6b36a3e4cd",
      "2b53b7f9-96c6-e80b-7d81-0483decae077",
      "2b744d25-40e4-3ba5-e0b6-33b95bfdbb19",
      "2b9c2322-12ce-2ce7-5734-a10454f56a01",
      "2b9d16b0-f572-2c80-79e3-509fdf06ce21",
      "2badf7c5-debb-5c22-6f95-bf27e86cb28e",
      "2bd81863-2fcb-9085-046e-50132d053a40",
      "2bebf90b-1388-32fb-19a1-38ac2719bd94",
      "2c1cb34e-2d56-ae1f-905b-543dd5081224",
      "2c93fe06-84fb-f4a9-0d29-79d6ba10263e",
      "2c9676cb-7d12-527f-d69f-cdba9d909a7f",
      "2cda2753-aa21-2c97-c01e-392c9ba4597d",
      "2cfc22cb-30e2-c1e9-5c90-d4c16945eccf",
      "2d64e9fa-4870-1c0a-bf4c-8eff6585e981",
      "2d878291-278e-a262-ee5d-47deb57c0f48",
      "2d8c7290-12dd-431a-a803-50a28e216488",
      "2dd3c698-6b2b-14da-6221-2485b879d3d0",
      "2e0b725d-ea9f-4661-73d6-7cb40701321c",
      "2e64ef86-996c-8445-9928-110c7d31b7c9",
      "2ed0462b-8991-ea6b-da1f-5abf28a1f75b",
      "2eee92f8-1202-30b3-cd99-dcc01dff5d09",
      "2f8b74b2-0086-22c9-70dd-fb2e197b0eb7",
      "2faddd19-e14c-c0c9-fb1c-5b72ad3929ad",
      "2feaadcd-f066-244e-68a1-5189a7be58e9",
      "3014d945-4f35-ea3b-d307-a518df8bfb0a",
      "3052d686-b4dc-3483-eb86-ecaf168cbc7f",
      "307dc5d4-c66a-7067-7ef9-ec0f64cd8a73",
      "30a7044d-add6-ae8a-8532-5e952849f25e",
      "30df229e-c089-b540-d305-d8518b6836e3",
      "313690bb-8394-8f97-cf0f-05dd52a06cf8",
      "31461362-00a7-6c61-e7a5-bed9d1e52c79",
      "31533669-b089-5e91-5472-6f0fb4eb5164",
      "315d0ab0-2f3a-04cb-00e3-cb54f8f3ac36",
      "31b70e08-6e37-abdb-0062-340e6eb798e7",
      "31dc9127-cc23-e16c-b6c6-6c5570ecac7e",
      "3218ca8f-29ff-db16-8baf-c16b0a22ae5f",
      "3229fbc2-2071-bb37-f691-f819dca802af",
      "32318ef8-80e5-4e44-0de9-3cb145e4f9d5",
      "326f3e48-3afd-34ab-ba9b-67c13f6e4304",
      "32850e94-f98e-3a2f-a77d-21e4fca81ce7",
      "3326a896-144b-e150-0eaf-d7b4649c890d",
      "3333b721-a748-723d-5c57-db9a7ea9d91d",
      "3373fa8c-cd25-2766-0d88-ad72137275e6",
      "33bac64d-ddfa-e0bb-decf-bf592a67ed16",
      "33dad0c9-1307-413c-cb94-179dc4670246",
      "34079930-8fcc-359e-24f2-3a026229e749",
      "340f04df-8c38-5c09-b4c3-d9f7c6092404",
      "343f529c-dfbe-dbd4-b1b8-95f1d9a86eb7",
      "344b0c65-8da5-6c06-0284-ac938f7df7cd",
      "34fa88a2-f9e6-960e-1658-9eadbb57d168",
      "35081fa5-66ac-ea0e-24cf-cc6c6a788764",
      "352ed97a-288c-a49d-6fa3-d35cdd1e56ef",
      "353bb81d-87b3-af43-d1cc-8f3663401a5a",
      "35514ca7-980a-1683-6fdb-27d001c0c2d2",
      "3574b1b7-cab8-7636-88c6-18665782f837",
      "359cd6e3-210c-2a9e-e85f-f9c10991b076",
      "35e6a5a0-8c0f-900f-2a21-7e5d75294924",
      "35e758db-191b-115e-92ef-1b006f623f9a",
      "363c2e2f-5db1-212d-1d7d-5a687760f538",
      "364a69a4-a63c-2d18-fc7c-e468bc467ff5",
      "366dac3f-517e-9e95-f338-7aa239a4b828",
      "36c56f22-4298-490a-612e-6fd0ce5d6d36",
      "36d529c3-caba-2e02-719f-341d2f82c13b",
      "36f4e74f-a707-999c-b28a-89e4a567b848",
      "372631da-ed59-9b2b-0feb-20da3b93575e",
      "37e5053b-3332-ac3a-8261-b58ed834e88a",
      "37e6e5f7-6015-c1bf-1f1b-1e8440fab063",
      "37fc37ad-ffe7-6898-7d6a-051fc0ac0fc4",
      "382c07c9-8f34-f6de-8d07-ed7156479eb9",
      "382c98f0-bca0-c42d-8aee-dd4f2e47e361",
      "38be1be3-a879-fd2e-16de-405fc8ff2688",
      "3902c818-f2c4-51b7-0c11-253e93444f0a",
      "39061be3-21b3-5cda-e592-a82a073ad740",
      "390bd7e4-14d2-e6a9-7d72-e2b1405242ec",
      "3914599a-8bcb-17d5-0b53-3717e2394a25",
      "39215c65-fa73-b560-b0e3-ccc7a040f2ef",
      "39871320-b4f2-4cef-1997-f820d63a87c1",
      "398aed81-8691-87dd-a9cd-c62780f5f924",
      "39cd6343-7e04-0ac4-f9c7-f6b9807a4d83",
      "39d00e9e-8619-aa98-d691-2323ac234ec7",
      "39fc4642-47cf-6af3-5f76-90eb7f5a8996",
      "3a113757-0d17-a9c0-f008-0bf5577bc058",
      "3a2bd604-11bc-e782-d5b6-428776dd6532",
      "3a2f0b4b-e12b-8516-059b-207b8665adad",
      "3a35269e-a775-65c5-9e06-2f565f927afd",
      "3a51e788-5101-87a9-e879-d7d6edc76e18",
      "3a9bce68-3cf6-2529-c81b-75583ba0778d",
      "3b0e2682-0e17-0367-d2c8-ad471964d61b",
      "3b4e76c5-9c47-1435-e492-742fd7eca5f0",
      "3b61fbb9-0cee-8231-31d8-2f23403be93c",
      "3b6fede2-96fb-b17e-ac27-3f7b3bb91bfa",
      "3bbf9c77-542f-9a9f-771e-af2642175cda",
      "3bc58d94-5b43-8041-4ca2-04d3f796be33",
      "3bee2f37-a2d4-eb06-fb55-c6d458325ebd",
      "3bfba981-8fee-cc77-35da-e21159d7d982",
      "3c379d6c-ed89-0186-ac0e-40e6b7c86979",
      "3c38a48c-65ba-79ff-ab05-a4844fe70076",
      "3c547640-8ec9-4a8b-6233-3755c96b8adb",
      "3cba2927-7bd6-23d9-328e-d3a4c87ade1e",
      "3ccd6cee-4476-b4c8-e28a-13707ff33aeb",
      "3d3ec3e4-9ce2-739d-c9d4-a391984e7d45",
      "3d41e3e0-e7ab-e449-2fa4-9a5c52144ec2",
      "3dc6219b-1b8e-d643-1187-322310dd09f3",
      "3e152489-d2f5-5894-5907-7dd4aa2f7030",
      "3f0862e5-31d8-8bd9-ea3f-2436d733ceb9",
      "3f12d21d-5ff4-4fa1-4395-675022cdddc0",
      "3f66e0ac-bb20-f6f4-36d2-a485bd16d781",
      "3f9723da-820a-6354-4e81-5d930f0e979e",
      "3fa75949-b6c2-f686-0562-da2fd4e42cff",
      "3fc7ba0b-37ed-d9f3-fe23-8ae9c37acaf8",
      "3ff1ddaa-8ae5-4200-5007-7b861c907faf",
      "3ff20d13-2e10-89d6-da68-b64da8ea0d17",
      "400a4789-a5d8-0d12-5e25-2a00a43f256e",
      "402d0e6c-6af6-fd4c-a468-bae2b5cc0038",
      "4036baec-f77c-fffc-4fa9-161f095f51ba",
      "40407c66-3649-a016-1509-4a9cf349d04d",
      "404d1fd3-16c6-f091-96c2-73c8305ae7c5",
      "4070599e-5019-262f-dd8f-8f9c067801de",
      "407bcc93-4676-2806-19d4-7dd6df63df49",
      "4096cc77-05fa-5651-2dc5-f48a97090607",
      "40c0293b-9937-da09-9fa2-671eef57e267",
      "40f863c4-f1f2-e309-40b1-94d8f48d9a75",
      "41190329-7282-9735-ec28-c2bef9d9281f",
      "4149e980-b7b5-9c8b-d4f6-9fe030941f12",
      "4173a1ec-8816-9943-95a3-500f0697a380",
      "417d4984-7726-ea33-9c86-90c3605e3418",
      "4180eb13-1f46-c5ed-be10-6532e8ef0ad3",
      "41c4114f-bbe7-3923-e2da-bead1305bb56",
      "41ddbfc0-ad04-56fd-18dd-0e017c60cc79",
      "421725d6-46d4-3766-8459-3ab8a2bc17a6",
      "427ef644-6de3-d3bf-27cd-c2e17b237ee4",
      "42c55c44-0a5c-43a9-9cc3-12f57caa0be2",
      "430dcdfc-b551-71c9-3476-0d64bdb2d14e",
      "43136988-f212-dbe5-482c-5e06f65fe276",
      "4340bcaf-bb5d-c5fc-62d9-e7cd5565069d",
      "4373a707-431e-4ce4-5cd7-465c9a2f6055",
      "43ccc934-12e7-147f-dba8-33b34d5da1e8",
      "43defdd5-e426-359d-7a6d-5cb31d593b2a",
      "442143a3-9622-f76d-3190-78d5699f03ae",
      "444e6611-0ec8-b868-8daf-8d825ade1f00",
      "4459903b-e5cc-f2fc-2dfe-e17daafd8ec5",
      "44630786-69ca-5b33-b21e-18cab81152aa",
      "449ced58-937d-a8cc-afd5-5b8804d016af",
      "44b0d1e4-9421-9bcd-d759-8e4443e50602",
      "44ce09b4-1218-9db5-fabf-5bac9ddd29e7",
      "44e8eda7-63b5-e964-2143-36145d77d170",
      "44f41471-8e16-e628-1ede-7fd6532e4f00",
      "454b1b43-257d-4db1-6324-724985ff361b",
      "456d701d-ca5c-3f7c-98f7-663e19da003e",
      "45823701-b743-092e-85cd-c4bdbf601721",
      "45a33f9c-2031-0389-53e7-359e6fab768e",
      "45b6cbda-c088-e41d-bff2-2bf19f9a5275",
      "45c44c91-6e42-12d1-9c69-3375953176f3",
      "45d18079-26e3-4c48-d292-4075a8feedb5",
      "45fc69b6-e676-e952-293c-b603bacc3797",
      "46357ab0-c4f5-c189-b193-06a068bc4a97",
      "463fcd56-1cdf-e349-5466-7fda8a3c7875",
      "46466978-6b64-2411-fb5f-79ee88a8efd3",
      "467b04de-c12f-8910-5d78-840c704f2f52",
      "469abd48-059d-1a19-e028-a2c2ada93248",
      "46e9a027-0485-fb66-95b3-b8d26beb016d",
      "4713550b-1811-0188-a37a-b2c75c3e0f6e",
      "4781215b-17d3-9b1f-53e3-aa921efd8b12",
      "47ef8c07-9c6e-db44-c799-dcda2ae91c2e",
      "488a1de0-ad46-da22-8bdc-5db998a92549",
      "488a4974-ed1f-a01c-8c2d-1e3edb4efbdf",
      "489cdcb9-efb5-f3ca-0b2d-ee503e8b8c3b",
      "48c80e2d-f80f-7938-0b67-bb1f367c0333",
      "48d1bfe7-e7f2-4b14-7de2-36df4111c534",
      "48fbc11b-84ea-de07-741a-538865c92f7e",
      "496bfcaa-95da-268b-f489-66a3d9d73f5f",
      "498a7976-527c-fae1-ab3e-f136522b4597",
      "49ed957c-1109-9f53-6f87-ae6a491c78c8",
      "4a1851d8-fc7b-ece2-367f-b0cc60157f47",
      "4a21ef3d-bbc8-6a62-e77a-247f785c1fd1",
      "4a5512d4-628e-d16d-d31e-53d2f7cf3bfc",
      "4a75e252-2d65-8d7a-1b94-1d141cbc5087",
      "4a788db1-907e-8f77-836a-6d86b1f105f8",
      "4a857373-4a6c-848d-b1d0-85c11bd99db4",
      "4aed04fe-e158-2565-22ce-fef239336cbc",
      "4af6f741-9a64-6a93-e8ef-f17d7a969254",
      "4b326417-083c-118b-a51e-51537c8ecefa",
      "4b4a1177-756a-dadf-210f-48e695d0e526",
      "4b911f5b-ae11-c8af-88da-75d4559547f3",
      "4bb621f1-ca1e-513f-48b8-df1b331346c1",
      "4c2e5228-1cf8-5ddb-f9bf-eda6840ecfd7",
      "4c525d70-abc9-0fac-9ad9-2ad2be8f99f3",
      "4cb47db4-4f90-ed10-ec7c-8945d3b855de",
      "4cb96399-456d-c567-088d-863b7508577c",
      "4cea0377-5b28-781e-07c2-bff167001c27",
      "4cf69cf2-790f-1aa1-ca76-c9b53c7c78dc",
      "4d00bd52-058d-068e-2207-9f4b81916eed",
      "4d0eb756-7630-b7aa-98ea-d638c2c702c7",
      "4d197cd2-fed4-bd4f-bdbb-dfec108d5a10",
      "4de35c7e-1035-ecba-7a29-08c3a1d11bf5",
      "4e406f2d-d06c-27d6-a5a7-da653232fd32",
      "4e4aa0fc-b976-01bb-8b0d-ad14bf579e1f",
      "4e61a2fb-96f6-8455-5f6e-588c070e327a",
      "4eaab00a-7ebf-b8b7-3cc6-0802f98d2865",
      "4ee31cea-540d-a10f-621c-810afe820415",
      "4ef0b0ac-6e3d-cb46-5224-3bf06ca10292",
      "4f0a923a-448f-c1e9-d91f-53356f403644",
      "4f5f359e-b4c1-049c-8fda-21ed47742e94",
      "4f624e94-f115-1867-4afb-20396b6350fe",
      "4f63504a-bc7d-6b85-8dbb-3f11f1612f69",
      "4f68dc61-d0cb-9b7d-bdec-7724045ae65e",
      "4f81a805-398d-cb0d-2b4a-e5da4a2eca12",
      "4f9310c1-6997-f66e-681f-dbcd97180e91",
      "4fc58f5d-fcf1-55b8-d061-bfc4d9bb0d8a",
      "50164772-7759-8a68-7282-ddd43ccd05d0",
      "50a452fc-ba6f-51b8-1b9f-e567a2155908",
      "50a5aa43-7b95-8ccb-cdb6-f5b429b91b39",
      "50aaae5e-ef43-1f6a-bbf9-df3072f645b7",
      "50d40c8d-6da5-6f2b-f730-7d51a073cc34",
      "50eab2c0-a859-abfd-2dcb-cb8939a4a3c2",
      "50fab0f2-e62b-c198-667f-4fdf7c5d13a7",
      "51043591-a24f-67d3-5ecd-1decbd19bae2",
      "51063747-e39f-65f8-5810-9eb32848ffdd",
      "511ae5b9-1ed9-609c-034c-570bdfeb732b",
      "5125182b-5295-9070-aded-a378a1aae87b",
      "5136a3ed-0614-0e20-471a-92a892be626a",
      "515b5663-2e0d-ca58-ac21-7f09e168fe24",
      "51789f87-b7ae-7bc1-0b99-13a015114f0b",
      "517f96ad-7e82-6368-ec96-b9b30cbcaec7",
      "51948c78-38e9-4cd3-2236-ec0c2d24311f",
      "519ec9c0-d9c2-d39f-c9f9-c09f513954b5",
      "51b16b2f-884a-6404-4fbb-1bff6c445643",
      "51bb0e7b-5e4e-1de7-aaf0-5f81e3c8ee16",
      "51c61bab-dc31-060e-6844-488bc1e730dd",
      "51eaf727-a6c5-2b1c-f022-8f8ca2ddb128",
      "5216fa2b-a4c1-5c55-9861-d8fb71648a60",
      "52eaa852-1955-65a8-b69a-57f8cb149725",
      "53022e31-a0dd-646c-41c9-44cffa59a92b",
      "5391b1cd-62f0-ceeb-e3af-34b32c17a58f",
      "539369d4-a8c2-deac-54c3-5ec588b6f2ec",
      "53ab48b1-5f5c-a1a9-8155-3c4e89f10323",
      "53dcdb72-1ad9-dc62-ed9d-721f301f1935",
      "53eb4eb0-db1d-9422-6736-c60c089711a9",
      "5433dce8-8750-c648-87ab-80e77a39b186",
      "54e0919e-6773-27dd-3ec6-541ca9ce8cef",
      "54fde810-a27c-3593-8e75-b6c55569d11d",
      "552717dc-2b65-4078-0ff8-c94ded2266d9",
      "557d6155-af65-c4f8-6819-1257fb082f60",
      "55ce4aeb-02f6-5979-b443-9fdf6573f062",
      "55cff473-0845-32fd-88d3-76fe547cf2e3",
      "55edc41a-88cf-7ae9-cbc5-70c83f3d87fc",
      "561f1dfd-7160-9890-7eb7-75af8119de65",
      "5659aaca-894f-1601-c0ee-1f7d5b7790b0",
      "568894e9-bbf1-098b-77cf-969edcfdc624",
      "569bd082-3bd2-91f2-f681-9a644b77bc02",
      "56ae76bd-5a2f-8bb8-8e5e-036e8ac15054",
      "56e30932-a728-01ce-166d-4d55b22c287d",
      "5718a51e-5354-d285-3ec2-cc7ef5ce1e9c",
      "5718efab-8d36-f0d5-fd1e-b9eafe7f9eb9",
      "572ee832-b3b1-844f-322d-c228652edd8d",
      "574ed46b-d41f-8681-a47b-cb7a3bdccb29",
      "575a4bae-9f61-b2f9-141b-4f5e65385506",
      "5798f83f-c30d-a2f4-9f46-2e651a88da2b",
      "57d1d377-d041-c378-76ae-e1e418c64445",
      "57fe38c2-9660-2d37-f43b-e965519a82d4",
      "5807596e-f8c7-ff6f-3a21-72c0cc71a834",
      "583a7801-e84d-a372-3613-6e3932d531e3",
      "5841b333-cbcb-2031-376d-0a4743e5d56f",
      "58874a3b-da75-4e78-5ad2-c7187f42b431",
      "5897ac5e-9d2b-528e-9467-6a1dd1f50af4",
      "58b435b8-edd9-9b85-5131-0db8d5d9f362",
      "58bd3a17-00fb-c0f6-9bcb-04799083d997",
      "58efa830-c3a1-a543-206f-850608936b1d",
      "5908a2d4-569b-afba-7a65-1ad71d5aedb8",
      "5915e9e2-2d56-f99c-7b15-70d030523b07",
      "59262fee-b5d9-698c-0dc4-3cf0ea030c25",
      "597bdfe1-11e3-bf38-36b0-b3db5a805f8f",
      "5a249635-bb80-cfdf-c966-c14eb72273ff",
      "5a6d051d-e95f-0f1d-26c8-6010680d60e3",
      "5ad7d878-c6c5-abc5-264d-b1906b53837b",
      "5af18b30-2e08-5dd9-a0b0-9ff6645386ef",
      "5afc1445-eaac-d698-8d67-f569acf455ab",
      "5afee4a0-06a5-69cd-3e29-743e5c88ff35",
      "5b3bed37-b3b7-4093-5345-26cdd287fc58",
      "5b6c7709-2f58-0f39-4c81-baa9e2f9b58e",
      "5b86cb75-7ee6-52b6-f3c5-977e549b35e6",
      "5b8fca40-657e-0cf6-eca7-24018ae36df5",
      "5b97dad8-34e3-b58a-7da1-cd5d748bcf2b",
      "5bb00c9b-4ff0-433c-a4a8-41a36e0dfd25",
      "5be5c01c-9731-04f1-8bcf-46268ec86057",
      "5c33fd5a-a3e4-f3fb-f35c-97d9d1e5b600",
      "5c404c4f-d9d7-d6cc-ef13-f0618cc4d9c2",
      "5c829a04-e5ac-38c9-c8ba-4c8bb35f4eeb",
      "5c9341a4-949f-1933-495a-c43d4ba5c9a2",
      "5d538487-dd34-5015-e083-9e3feec90b60",
      "5d827860-4fac-6a1e-9c73-70bcc9b2d5d8",
      "5dad80f3-9410-56f9-9eb5-360400aa1a81",
      "5e371183-386e-ff1f-44b2-e14856c0e963",
      "5e3df100-9d59-1da7-0b6d-856f8edc0c30",
      "5e92274f-5ac9-4d59-85b7-faf5fed4d9d2",
      "5eaf26c7-ba7a-54c6-67af-de45face77ef",
      "5eb7f46e-c70a-d5e8-2673-ed417f569d74",
      "5ec818a4-3ae3-8feb-f680-332f53d2a5b1",
      "5f483b3e-7899-b5ff-c225-45498611e751",
      "5f5ea1f0-048a-1358-fcd4-06a1a256e9d0",
      "5f8cfd1f-43c4-354a-b893-2e6846db70bf",
      "5faefae5-5aae-ae0e-7e23-85282a01a1f0",
      "5fb9f07c-d5b2-0bfc-1027-3bbf1a51681b",
      "6057ce2e-a9d7-a5e1-2413-c4388cf15893",
      "6081f44e-def9-f660-5645-459740ac0501",
      "60da87c2-b4a4-9879-3896-79540701c4c6",
      "60dcb186-2a55-593c-ebf6-89f1fba738b6",
      "613e6e7f-598a-4a26-0e47-87939c008d18",
      "61a08ae5-a2fa-8fef-dc10-66d6f0ac262c",
      "61cc391c-137f-19cb-9c17-6e26d82313ce",
      "6239c6ce-32c9-613a-bac4-201d7bee9145",
      "6247a77c-edaf-9c51-7e5c-3bb4be6ac727",
      "624e258b-d3d4-50b6-a36b-ceaf404a56af",
      "62ad6694-f7e5-3bd3-0bf0-666cbaf0fe8c",
      "62d390bb-e91f-98d9-6e78-38ce05d3ea47",
      "62f12bcf-6aa1-1e2f-6e17-5d38431d20e1",
      "6349a170-bcd1-58c5-5b1c-dbc913d73c72",
      "6369e269-4e3b-f6b7-11b3-1f5e4617d98a",
      "63812b75-43f9-e6c5-f7dd-3a43f34c2173",
      "638f70a0-6b8b-e358-07c2-10661c8cebe1",
      "63d91430-9d6b-a92d-d6f3-86c97f9ef9a5",
      "63d96ba2-036e-8678-a572-0e466e6d1f6c",
      "63ddc4f9-9817-6440-2b93-da90cc2bd933",
      "63e4cd04-7e76-1ed7-c89b-3df73a6d6131",
      "63f367d0-1178-dd85-c327-26e5e7e0df0f",
      "640197df-6b9e-a54e-eb7c-0f05e64fd8b1",
      "645d1d12-869b-c7a7-b1c1-3402dbf194dd",
      "646546e2-16f7-ffea-297a-6bdec747adfb",
      "649615b1-05cd-37fd-dd38-a13785289b5f",
      "64aa666f-f101-947f-d529-4c3bfbf3cef7",
      "64c7557a-3207-724d-7865-2fa538f17648",
      "64c94c7e-fc5b-5d0a-8e03-9e04ea9fab37",
      "64df35de-ddf1-f851-01e3-488858776d3f",
      "64df4f0b-c7d8-5c8f-be27-d8984b569408",
      "654e4e1b-b033-ecf7-f97c-5e6c15ae9467",
      "65771a60-3bda-359a-9841-4771faf3a9af",
      "65ef8146-3f59-c61e-42e1-b08ebffc2a3d",
      "65f24447-f478-0cbd-31ab-1c8aae7d7d14",
      "66351d6f-e743-cab8-b6d8-65b400be1e7f",
      "66537de6-cb5b-50f8-b0b1-2efc47af0a32",
      "6658fc83-8cc1-cdcd-066e-b2cc6070608b",
      "666215f0-90f5-fdeb-9a14-fc8af73c53dc",
      "66666b6f-2a25-0878-fcef-ce9ff52ec9de",
      "666a3ad0-e1a5-1112-6c64-d861ec9a34c2",
      "6677c6d8-6c74-dcfa-9d88-2ec013642a1f",
      "66a4053d-8825-bf1d-4185-fd6d2de11b2a",
      "66b8e67f-d7f4-6798-ec24-925da1181981",
      "66d7155f-8f12-bd80-8c98-34dec2d5cc87",
      "66ee9c3d-7c32-96ea-680a-f1968c100541",
      "67377150-1c02-e229-74c1-dbc5a248a58d",
      "67e8a37f-9f0c-56ca-1c0c-689816fb5224",
      "67eddf97-714a-1341-b3de-3d85eb4166c3",
      "67ee60dc-3513-d62a-2fda-386eb835aad9",
      "67fd4085-dd87-668b-c3dc-fbde5b1b1204",
      "68369c1c-79b3-b49c-d144-905c68498cad",
      "6866a48c-b63b-b2ff-d8f5-3d038210898e",
      "68890aa5-589f-15a0-f0d6-ac8f02ea65c3",
      "68a57e5e-e7de-adb5-ef6c-33811eb16aec",
      "68de0cbb-5e09-32fd-6759-6a83cd78fb8a",
      "69215b2b-abd1-ce7a-4e07-c8c33d9fd608",
      "69255b5c-bf55-530b-fd12-141cc4a3103d",
      "692b2392-b635-8e21-db8c-4feb737e19d7",
      "692eee5c-7b6f-a5a5-2557-9f7ca4a08b06",
      "698440bd-c610-636d-33c7-7270f3adbf32",
      "6990b298-d26e-6e8e-802f-957f49ac37d9",
      "699a13c2-4844-6592-e1cd-1855889eb366",
      "699b31f1-4377-7420-1a47-8d9a16a72c5f",
      "69cfcd49-147c-5d6e-4c91-25f49e85a0ad",
      "69d05a3c-330b-371c-382f-c216c8d6bd6c",
      "6a3802fd-313a-1b78-9d0f-0144774be870",
      "6a56f166-6666-1a67-bd2e-b18509dd84b9",
      "6a5f540e-f310-14a4-0c4f-cbaf3b90cdda",
      "6a760905-0829-f683-b8e5-501e7034e0e3",
      "6a8b4411-c778-0621-be9c-42466eca6c8d",
      "6a9ea839-e7ab-6cd2-bd13-65a240f4159b",
      "6acbe7a2-e361-fffb-0cbf-b06cce2b1cfa",
      "6b4cc60d-6802-722f-a450-e6bd868ead29",
      "6b69710a-6a3d-d19b-f3e0-1076ab15c90d",
      "6bc4fcbe-72cc-3e2f-bb49-34cd51d4b073",
      "6be68028-1814-0386-6a21-15d4f1c7ae18",
      "6c143bd6-ea4d-de96-87bd-177b1ed3c357",
      "6c182843-0302-6578-141e-dde2a6fa7467",
      "6c52b4f3-58e4-f7b1-8342-1cf528cca56a",
      "6c73296d-dfb8-2705-f313-d2115e4518c4",
      "6ca463c6-ccc5-283e-efd4-c7cd4b979420",
      "6ce5dbd7-60cc-888f-7a5d-5586e3b70a58",
      "6d2c9f6d-d7e7-1ccd-3b94-54070d0ac83d",
      "6d837540-b421-3d0c-8b7c-b94be0cc3c16",
      "6de3cf50-62a2-a172-1f98-c51d0cdbe7a2",
      "6deb0ad3-6707-4b9b-043d-d899fa1d45e4",
      "6e2c4b0e-e2db-2cc9-403a-80d2ee4d650f",
      "6e45c2d4-1f9b-fad4-4a3e-d824ea323ab8",
      "6e6d908c-db04-d7b4-1d2d-6cef8dc02359",
      "6e8857fb-de2e-1cb8-784d-66d7ce501366",
      "6e914f28-6cfe-7662-8160-f5dbeda9cabd",
      "6e98a8c7-6961-e83e-faf8-e84975e7e7f5",
      "6ead67bb-60b4-4a5c-7065-40726383d558",
      "6ec042e3-e072-c721-6f7b-69846478d652",
      "6ec0761b-3c2d-3c0d-1bce-b00bd091b22a",
      "6ef17117-adc3-fb21-b6e8-ef8c2b1ba398",
      "6efe9d9a-6c24-955b-b312-23f624e79df1",
      "6f04d11b-8025-a915-acae-cbce8e10e031",
      "6f1129b3-8d03-92a8-3d0c-39d4cc57738c",
      "6f423c16-ea04-52a8-ea4d-024f1cef2f8e",
      "6f424851-5bfc-1b81-7ec1-147ab92f3371",
      "6f7ba4b6-08d4-67f4-337c-19c9e35b5d18",
      "6fae42b5-fea0-b290-12cc-c605bcb8aa6a",
      "7030ed9e-0765-b95b-8d6f-e1f121c160b0",
      "705dd29d-b9da-5d3e-3ef6-2e59d9b9174d",
      "70723dcb-8e2e-eb59-0b19-39fdca74bb55",
      "7073e39e-2832-8576-93fd-5ac5e161be3b",
      "70b0cb81-64ef-3e9d-91dd-c2f22298ef33",
      "70fcaa92-c531-fd76-183e-e6df81d1386a",
      "7102b748-1939-d3f9-80d2-cc1d3aeef256",
      "711d8e9a-8b6e-6451-d709-56882ca5dfb4",
      "713fc6f9-1608-10d6-0c66-bf4b438ae960",
      "714d181f-e303-c2df-dccb-a7e838953b44",
      "71a85982-f327-3997-3fc9-57654755269a",
      "720cb80f-e6b2-bde8-6d89-8ef98827bc15",
      "723e4738-0714-c395-1d61-496431f1e65c",
      "725fb04f-d29d-3ce0-0fbb-0b42f262afd3",
      "72857e13-f22b-d0d1-9663-0e23f5f58437",
      "7288d0b9-6ea7-52de-61fc-60aa382ac6c9",
      "72a16648-0dad-3de8-fd60-4e8f2f31c86a",
      "7350283a-ea3c-f4b8-ea7b-5bb866b39944",
      "736d5a30-f8f7-cd9f-1c0c-0e01f2bd2196",
      "736f5c91-76a6-2490-16b8-5866a1df83c2",
      "738076de-7b68-5ca8-976a-acfe5e607507",
      "7395ccf0-334a-b58c-49e3-cce9c8e13508",
      "73bd6ad3-0df1-0105-9e64-3c2aa017243d",
      "73f41565-cadf-2571-c690-71e8beed9b95",
      "74189cc5-c658-c1ae-7306-0b304704e987",
      "7487b1f9-a2ad-0cd1-c098-3f74b9ff3e11",
      "74fb28a2-6200-c1c9-5385-2f4e789e3ceb",
      "75426fb6-32b8-59e0-a821-309579d73e81",
      "75476ece-1e42-d1c7-3236-25de43f450e0",
      "7583e68e-16ea-7676-0ab6-1ae528cd4b8e",
      "759a2c11-2a07-f072-3563-d950d7aef548",
      "75a93fcd-3b0e-33fc-7051-e22eb3720a71",
      "75c664f1-9a86-c806-2881-5bf995f3e445",
      "75cd4cc9-3819-bd90-386f-e81b8d34616f",
      "75ec973b-ff93-59e1-97ed-137e607a4f47",
      "76085c53-eb05-f09e-da9c-4278b9bb8bce",
      "760b86fe-f352-d140-84bf-c9fb878efcc4",
      "760cac2c-1d29-5307-9186-9dd306ce80b6",
      "7656b2cf-2ec7-b9a7-1c97-e28bb20d19b6",
      "767fde72-a8ad-12d5-ad7f-a4e382fca72c",
      "76837a78-5830-d89e-daab-11ea2c45307b",
      "76c221e8-ec81-4306-2a22-c557668a3eb0",
      "76c711f2-cad2-d1ec-3746-76374839c56a",
      "76e2d0d2-bc5a-bf7b-e293-9a9365241e4b",
      "76ead242-85b8-7dc7-3c8a-579e6740c3c7",
      "772d1bf2-8ac4-fa44-d75e-55d5d331f96b",
      "773721cb-ee7e-262c-6eb9-412675530aa8",
      "77c92358-db0c-9605-214c-f942d3d30cf5",
      "77d90737-dc5d-ae7e-abc8-f07932de328b",
      "77dbeb77-0faa-f6d3-840c-e728427ee0ab",
      "780a7b57-8dfa-1099-391c-e17b5a9b30fa",
      "78301e8c-11e0-6e72-8f58-3fdd73e18355",
      "7873794a-d403-1587-82b4-d8f671f79783",
      "78839c44-c92b-218d-dc21-f2aafa65f2df",
      "7893375f-b92b-a885-a8cb-2ed9d1b16009",
      "78954246-0049-98de-b58b-f39d7bc0ff66",
      "78c184fd-890f-24e0-5a27-44cbff4bdcf0",
      "7928e0a7-9722-9bbd-23f7-8dcb765b1c48",
      "7971bef3-c983-43d7-0b0e-c6c01d60fa85",
      "7986e2e2-09bc-905f-c643-b6187ca48872",
      "79ca3488-a80f-3105-c9fb-e2258def4f29",
      "7a243f52-7691-9fd2-5a5b-186094a915e8",
      "7a332db4-22ad-e359-d97a-66f1e32203c8",
      "7a665a8f-f8e9-11b4-598f-a1f8d24b805a",
      "7a6b2314-06a9-9229-378a-79df1d4ab886",
      "7a6fd1a8-c66d-a521-9082-550684005894",
      "7a825413-7687-f042-b2a6-167b9bac9f18",
      "7a8b9b97-d99e-c295-a7e0-139e3dabfcd9",
      "7a8e575f-8139-625b-d636-21ead5587def",
      "7acf19cd-2077-c1b6-21cc-e0e1da16bebb",
      "7aef64f4-5285-3013-5618-1fecbded6387",
      "7af5a285-e7f5-e904-67d3-ca5d546268c9",
      "7afa314a-ea98-97c6-dea8-fe882c0d10a1",
      "7b47f1e4-e29c-d328-df89-e28678371ba1",
      "7b68635c-8992-7217-ea5f-e92e24ef88f9",
      "7b719226-57a7-c3e6-1ecf-57ad304a8800",
      "7bd6e569-ef42-a7b1-1a8b-cc90de9fa56c",
      "7be74e22-37a2-2820-d7bb-29869cdd7cbd",
      "7c85c38f-9810-bb07-78fb-37c0f99955d5",
      "7cabd69f-aec3-4d53-74fc-3aca03a1d77f",
      "7cb9bc91-258b-787a-33dc-7666c76b4124",
      "7cbe185f-7900-350b-9a6c-9d85b0549d48",
      "7cc84f18-9830-07da-10e8-a65fd5e476f3",
      "7d2f4811-0f08-9b2f-f6d7-7ebc206fc705",
      "7d3eac56-cfe0-5d42-d7e4-5c2bcc1eea69",
      "7d5313ad-761e-8261-ea7c-4a223afb5be0",
      "7d8003b1-1329-5202-1f0c-ebe3803c605b",
      "7d8ee91a-7ade-ce53-f3d7-cf37a2109765",
      "7e223adb-59f3-58c6-fd03-4a9072c4b9ad",
      "7e248a7b-ab29-e9c0-625d-1e86ffb85fdf",
      "7e43e54b-0dc9-42b4-a5f7-9ce8041db7b2",
      "7ec4cc6c-9c78-2926-a9c6-2efa9e96804d",
      "7eda29bc-6ee4-9095-71cb-6b97cc21fa15",
      "7ef3f121-6ea8-d573-0a2d-0e4c1564cecb",
      "7f1d3825-cf4e-b371-4c02-c5d681052311",
      "7f3b575b-4a69-9f43-f7dd-691e78885e74",
      "7f955968-d40e-6c52-9e2c-925c8855dcee",
      "7fa3eb3d-9d2b-aec0-baea-f8e5675a6fd9",
      "7fd0ee46-9e8d-2a38-fb6b-7e57f0b56c3d",
      "7fe25de9-adb3-e402-bd29-d37f53de154e",
      "7ff2dc0d-a3c3-d14e-a619-fee985a3deca",
      "8014a689-9081-69ff-c2d5-a3eaa1cb8057",
      "8015ed2e-681b-55d0-63b1-bbc61ca3d73f",
      "80307ca6-ca76-08ac-e691-babe0083900b",
      "80896733-2a73-e8b2-5c17-0c1c7f40a673",
      "80b10c44-eb2b-c962-4b3e-29dd5778e7ea",
      "80da5e5c-1b62-042c-a1d3-df291db27123",
      "80dd7244-dcbc-97bd-6b3a-95376c0502f1",
      "80ef8514-9285-12c3-a284-0dfad2b4202d",
      "80efe9ac-2ebd-b621-de8e-3e2375379b5f",
      "80f886bc-cd92-c478-21b0-ec4a1ecff277",
      "814e3fbf-5748-2cfd-b123-8a44b33a7289",
      "81b3c148-baba-3b14-172a-1e089dafe787",
      "81e688b1-aeb5-5470-a9cd-ad764744c1cb",
      "82064300-6b56-8fe9-2cb5-1e249c63dcf2",
      "82772c31-8d3f-81be-a3af-af35aa9d0348",
      "82a4c9c0-c24d-a33f-0e0e-f6584dbd4234",
      "82fbdb40-2ad8-7b1e-4831-8a5d4d4b7dab",
      "82fd7130-8575-e0c2-381a-1bb3b78606fc",
      "8361a653-d600-4a4c-25e3-dd2dffe5129c",
      "837082bf-e7b4-a92c-f37b-8b46a4417beb",
      "8372e4b8-3e9e-335d-75c5-e23d64d5b46b",
      "83793e69-7c84-41d2-e9bc-91c3837101b7",
      "8393d549-abb1-af1f-e6f6-03a52fb289d8",
      "83c959b3-9330-ed7d-5464-ee731a7fc186",
      "842ab264-4f2f-9fc5-aaa9-61ce02135dba",
      "84648bb2-2c07-2853-f713-11f8b7feab5b",
      "847ee407-cb12-2885-3f18-0eda55880466",
      "84871a19-371f-90f6-4d32-669eb5005c62",
      "84963219-4d88-6ff5-2ec3-2dc2e94f8300",
      "84ae4b1e-5819-8bae-939c-870047780592",
      "84afa6d3-653d-92d0-6276-f75ae2bd0d4d",
      "8510a02a-4be4-bc6b-d445-23721e6e2ae6",
      "851f5003-ddf2-fadd-4aa5-b6f23b062990",
      "8586bbf2-64d6-917a-9b81-a9c1f1fd9249",
      "85bd80e3-8390-b6c8-2ac7-85d6ff5a0427",
      "861b8ba9-d558-bb68-fc91-cd909cdda25a",
      "865fd498-62dd-7d93-b891-b397251d7eed",
      "869b768e-98ee-d585-7ae5-4cf04b77fb6e",
      "86ed2b0f-442b-4243-87f8-267c90e95c72",
      "86f8e3f0-039d-5f3a-0829-6469b982ce78",
      "87068ade-91a2-f4e3-bd8d-2613bdd50497",
      "8735a2f8-1122-48e0-bf53-5621226b80c9",
      "874497c3-8157-1225-d0c7-f2a64157d19c",
      "877db3cc-cba6-21cf-8c17-1c7db15d5076",
      "87ca02bd-cff5-6a0c-ab14-604fd0bedeb0",
      "87d025c9-aeb8-1e1a-ebf8-529d13319f4d",
      "88092a45-8ec5-c1eb-0122-a5f3f0e4d55e",
      "880a5ff7-5027-a0d4-a574-1eea0f2ad653",
      "88204333-f80b-d8a7-6e0f-429c38d1dbea",
      "88248b1d-6959-4f15-4337-df52fd7e8e3d",
      "88c2b012-c964-7673-98fc-90d11b2b00ca",
      "89034b87-8aa1-82b7-a630-3cc9e73fb524",
      "893bf6ce-a24a-b471-ae1c-bc2306c7a4f9",
      "894d5ca3-83ba-1940-218c-64336c055690",
      "895b4929-9a8e-afc8-fceb-b7fa9a14a966",
      "8983db62-6f34-8d98-20b8-01879036554a",
      "899248e7-9818-da95-6fa3-228fc29fb751",
      "89c67c42-78c0-de01-0b34-87270e2fe6be",
      "89e435f6-f5b5-6806-f2d4-f72878f59ba5",
      "89e894ed-a5da-b5fc-6d81-e4bc88c6a1e7",
      "89eaea41-9afd-ee71-fd57-83a8c6621029",
      "8a05c8b7-fbb9-b2da-3c84-13d86d9b1b93",
      "8a09e069-d2b2-2d5d-a800-1ab772a77244",
      "8a3e7534-d3fb-7b7f-09ee-e458c8b1b080",
      "8a457035-20ae-9cf3-d500-dd73ba2e4b11",
      "8a694ddb-f36a-22be-df6d-496e39f5c470",
      "8a6b1406-8a13-0232-3e8d-fb41db791c1d",
      "8a8e9755-54f1-6f92-3533-12b8a4f04f0c",
      "8abf6d28-3ac9-13db-eb92-a16a0e10788a",
      "8b1d7451-4ab4-d98e-2062-c261c37b3fe0",
      "8b3d2128-05df-a441-3071-87b6c38d1fd2",
      "8b7fe6e8-c6ad-d9c6-d3d3-a4816acf43fc",
      "8bd70622-52b2-e8c6-f62d-85eeda216bdf",
      "8be2facc-5e7f-285d-d027-26c34d8ae6ee",
      "8bed1e40-df5e-24b3-8bad-e2d6045bbeb7",
      "8befcd88-b898-97e6-123c-7ea9299eebe7",
      "8c208ad4-8b20-b9c2-bbea-acbcb28fd355",
      "8c5b06d8-fcbe-6df6-c777-5be3b3d50de4",
      "8c755af4-dd29-b6f2-05be-a97dc99faf6f",
      "8c8c302a-91dd-95c8-94c6-0590627cc210",
      "8c94cc54-6b7c-98fe-c6c1-d81766e43f39",
      "8cc49789-0421-ce7c-4823-1fa9b60bff9c",
      "8cd0b119-129c-b7d3-03f3-dd49123fb28c",
      "8cf866f2-8783-3704-e441-778ee590c009",
      "8d231493-e185-9bfc-a046-d57190da855b",
      "8d3bfdae-8840-147d-4125-daaf43f6d300",
      "8d7517d4-2c0a-a24b-da79-bb6fcd9fb20d",
      "8e21a9fc-2e94-c62b-a32f-e20449ed2b77",
      "8e45193f-4f42-f5ef-45ff-2011135eb106",
      "8ea0495d-c3c2-f6f9-2cab-ad5f4d6194c4",
      "8eabe7f4-9b9b-8593-c90f-e8bbf87a4bbe",
      "8eb055eb-1c6d-c932-2ffc-9ca3c8663273",
      "8eb66fb4-76e0-e878-de56-36217daf5348",
      "8ee8db36-db39-c067-f2eb-6844abc9ac33",
      "8f04d1e1-943d-5461-6694-814b2764acfe",
      "8f42df5e-22d5-3215-5af8-dde79b9d50ef",
      "8f6b7019-09d4-812a-ac52-53f2a155a0e1",
      "8f847d58-5a37-6fb2-5ca8-1dedbd81e720",
      "8f8e25aa-d737-db32-4999-a26b749149d5",
      "8fcfdb85-9109-4d02-190c-c72e325cfec1",
      "8ff9468c-cf91-4fe7-e72b-3282f5a7a983",
      "90256b14-6b7c-15e9-a504-4c5525201b67",
      "90289ea3-1298-e377-5adf-bed75acd6fed",
      "9065e0f9-51c9-740c-d304-a3bf84144c5f",
      "90e063c0-731e-ea4e-d806-070f1b50335e",
      "90f09267-3d3d-9bec-59dc-c31b84103ee5",
      "911e9b1b-e4b2-1408-f2f5-d9ea5048db2b",
      "912ba26e-1916-23d6-7a66-d96a40e3cf3b",
      "918f4556-79fa-40d2-c09a-1422572084a6",
      "91b00edf-e1b8-ad0d-32b7-a6c7f9fc52d9",
      "91e58181-c31b-6251-1ed7-627827071837",
      "92104663-e2dc-5d4e-e456-72d1bc9edde0",
      "923cfc33-7d54-bbb5-e106-048fd6b1b50e",
      "9271d5e0-f606-211c-a421-cada3354bb35",
      "92977682-ee74-fac9-02ff-cee360704c48",
      "92b23966-7faa-d517-5edd-4b82945a7131",
      "933f78e3-56d6-6519-3ce3-39aeda492676",
      "936366aa-9fd8-9d17-1db4-c50aa35574e1",
      "93849a9b-0f6c-9c37-f740-6806df995078",
      "93cb944d-0591-077a-eab6-beb41d545cca",
      "9405d208-f6a2-688e-96f9-e97b6056611f",
      "9439c691-e98a-52ae-4258-802c88fa2847",
      "94549705-4351-9051-87ca-29388f0104d4",
      "9489320d-cf9d-486f-e2f6-009a6608ee6d",
      "94ae9d4b-383d-9a6b-7344-88957b28f185",
      "94f36e5b-1ad4-cdb8-f518-fdf98f83c857",
      "951684ab-1e6d-8cc7-9b4a-340d1da473cd",
      "963d9811-68d5-33db-6bde-f81642da06be",
      "9646c079-6241-0af7-d227-0375300bfb92",
      "968007db-4db7-5367-596e-d131f53ef64e",
      "96c6466c-69a4-54ce-7bed-1c3ac3bc6cf4",
      "96d8e824-044d-39a1-9191-a6ed05b7341c",
      "96f9bcba-8e0e-e62c-430d-9fad33fac89e",
      "971ec3c6-6384-c5f6-11b2-3ed1c25084e8",
      "9737a467-886e-6dda-a7b2-86503b091f06",
      "97435ee7-dcbd-dba9-7f85-44c119a338e5",
      "97dd0ed7-8d33-0187-90ea-b0a9946cb52b",
      "97fe93ad-0865-00b2-89e9-bf1deb3f3921",
      "98a0c7cc-0bcd-75a0-f847-0f97b023e8bb",
      "98b9677b-593b-6949-8fce-15c4ba374e45",
      "98db95f1-beb7-5558-89d1-db18b5f9c013",
      "9915c01a-7d94-b680-cac8-be917e4a29cb",
      "996446b5-1ae4-6327-3218-52c29e49fca4",
      "9972e0cf-787e-e172-0fb2-8503b0b608bf",
      "99811af7-f815-3da1-553c-1d87404b9f56",
      "99b8bc1d-8b88-fee5-1bec-076dad675378",
      "99cad053-97a8-b695-5cf6-fa716809b1cc",
      "99e7eae6-7f97-6eeb-fab0-95e2f3323d88",
      "9a25482d-c348-8529-7962-0126bb8304ed",
      "9a2d7aac-e198-4366-dfec-e637eb96f85d",
      "9a56d593-ab1b-a4c3-3d01-42d18b223bbd",
      "9a66d29b-2eff-620d-28e7-54b74e87be41",
      "9a6f8718-b80d-489e-1c0b-c4a278fc15b1",
      "9a7fb2a1-0a7c-e0ae-b203-5d14343203da",
      "9a81a1d8-32c1-65a8-5bdd-ae6216a1df89",
      "9a98ab4d-cf58-f768-9678-256995e82bf3",
      "9ac254e1-bd83-b33a-33a0-94329ef1dfaf",
      "9af2a623-08d2-d735-58a9-e94765329424",
      "9af9b078-b523-24bc-a21d-487f85b8814e",
      "9b17441d-20fb-77a6-db45-f11ff47d08e7",
      "9b66cc4f-2a21-4172-efbe-18114302536d",
      "9b73800a-866a-708c-a522-97b9c3ebb178",
      "9b946079-d04c-3b9a-bab3-fc1456b3f644",
      "9b998d3a-bda0-29c6-f24d-c71217a46cdf",
      "9b9f4d81-38ae-1c17-8060-84ba6d225f62",
      "9bc9e4a9-2c47-a14b-02e8-79fa5e429598",
      "9c027c3e-6748-8055-fb9b-b013ce516431",
      "9c1b5316-7611-a44e-4044-776359ac3820",
      "9c2e5f01-38d8-fa07-565b-0ea3b2d2b87b",
      "9c7cb5b6-bb66-bb39-f8ac-efaeb0fb33fb",
      "9c9169bc-7ed6-ac60-c5ef-b6996f3f114c",
      "9cb5dc6d-2af6-1fc2-ec11-bb617e6ecc56",
      "9ced6a91-f120-759b-ab26-8b4229013c40",
      "9d0c2649-c9c1-2e65-9f7e-340c51556929",
      "9df539b5-f73a-016c-8f36-8e7f06364231",
      "9e1fd64f-901b-b542-c8a9-1d82950e8ffe",
      "9e3de44b-c69c-72ac-1da2-d7d4490c84eb",
      "9e4c2d1d-7b8a-556d-4e2b-5fa8d5d0b938",
      "9e5eee28-827d-6c1d-ea7e-baeff8118ea4",
      "9e61878b-dc75-146a-cf09-ef195041bf59",
      "9e6ec7cb-45da-b4f2-5dae-edb3f0785320",
      "9e84d879-bf54-eab8-2006-64abd280339a",
      "9ef83e00-aa14-6090-371d-cb3bb9aa548f",
      "9f4bb619-08e7-3bef-ab8e-76b87ffba09f",
      "9f657598-63c4-1500-45fe-4bb368fb136c",
      "9f8686f4-cd75-07ef-0025-85d41d5e409b",
      "9fd90cfe-7ec5-4125-6827-0fbbb4aac316",
      "9fdddd40-bedf-80e5-985c-00ed094e0406",
      "a02885b6-4038-8229-b4cd-d25ee1158162",
      "a02eec5b-6d27-b43e-1e16-8b12252cc834",
      "a033485c-5419-aaea-f2bf-1290dfe5d08b",
      "a046eb36-4494-d9aa-4ba4-5c1e3b75a1a4",
      "a04c8b33-7293-6bc0-3315-876d7a9f9e0c",
      "a055dc0d-9d80-59f5-94ed-154f301eb7ea",
      "a080e2dd-ec7e-5300-3d23-7d2c28c8d59a",
      "a0a6b886-5f09-8505-40ea-6c077da5ba41",
      "a0d1b2fa-c2df-669a-fbd8-d2c452733fdf",
      "a0e75229-c2a1-7652-895b-41dffc7ea9f3",
      "a12816e2-cbf4-9179-a38e-e0b42b266b7f",
      "a12c96ac-faba-0015-a0bc-56261fda44fc",
      "a142ef19-9b26-d819-3b22-c3f2cabc9243",
      "a1ae7be7-c439-f0fe-a51a-b7caec3cf1a7",
      "a1fdd2bb-77b5-780a-d89a-1feb17d9021b",
      "a202cfbe-6ef3-5274-f3fa-cb64be0c0781",
      "a208630c-8da8-6c72-0121-12e64786b8c7",
      "a20d4812-eed6-be0b-4c6f-8861175716de",
      "a21e4779-835c-bde8-dee2-f9c7e93a6cfb",
      "a25ca7c2-bc47-55af-668d-f337aba4a60f",
      "a26037ba-7ae5-81e8-787d-d7d599ab9a96",
      "a276dd1a-01b7-0a12-6531-801606eeec0a",
      "a281fc5a-d569-cb87-4288-a34f393c7302",
      "a311047d-ae2a-ebad-7b13-5608c0e8e4d7",
      "a37b2143-1fbc-5237-4038-5159afb5e5f2",
      "a3e11870-70cb-c58b-de69-e2035fbcf95a",
      "a3e51f8c-a874-0a0e-1781-3a6968f78236",
      "a40be035-9544-87a3-b992-eb67bab64b9c",
      "a44393f2-976b-19c5-22ed-0d3551c03ea9",
      "a44e4465-6f31-fdb9-f527-f182fad3e0dd",
      "a458bd48-920f-67aa-c71a-b67ae76f90c3",
      "a47d39f8-a01e-a21e-ed81-d39501a20e1c",
      "a49f2c8d-93c5-f2ae-1b73-4080260aa1eb",
      "a4c3164b-5606-e270-67fb-7d2be71aec5a",
      "a4ec2af6-aea4-8b4e-adc4-bb2c1e1f750a",
      "a511a70d-c5f1-4d5c-42f5-3a16b1073142",
      "a538b491-e632-d770-7f1a-eae4ecab9073",
      "a5396007-6b3e-7f56-909e-cef76c9e4251",
      "a55af77b-bc6d-cf8e-6670-2eff261d8210",
      "a56e4704-c39a-0775-23df-3e54de4598c0",
      "a58392fb-c186-e75e-d939-982918b23b57",
      "a5a74505-7628-820a-3315-112d2e594df5",
      "a5bdfa23-e791-78b2-8680-5de0db1bfed2",
      "a617330c-7d66-7909-c706-b45dca4399dd",
      "a623e94f-fd46-75e0-bd25-8cadbcda6d18",
      "a6452412-ff11-98f8-4067-63f6d4c20b20",
      "a700d3c0-45a1-a5ad-0717-bae9902dfc33",
      "a788771a-88cf-f2d7-2728-d314b0670dd5",
      "a7aefc60-dae1-3dbe-f6f5-704f4e7920f1",
      "a81579c6-9db0-aed8-7de8-3932a6b6b39f",
      "a81c1321-fa9c-f219-f4c5-5ae9c1f7b92a",
      "a85e1ebe-f6d9-1aaa-226f-2478694efed5",
      "a870bb61-226c-9cbf-1666-28f844d75f68",
      "a898c3b6-8258-f587-b697-964e858dd638",
      "a8b46732-0d2f-b368-939c-e0bb48284507",
      "a8ddda99-020e-2e1c-fb8d-6ffdf24e8029",
      "a9561eba-0296-a30e-4f77-994c87e95b0d",
      "a9a0c2b3-b5b4-d003-e4e1-74a7d7800934",
      "a9aa052c-2b0d-063c-2f39-5fdb9103ffd2",
      "a9d1abb8-f7b8-9ddc-4a34-75be568d9ff8",
      "a9eaf6ab-4e64-a03f-eb49-6b2a3e911063",
      "aa5e754a-463f-2d39-98d4-f498bf6f680c",
      "aa82eff6-65e2-a3d7-b4b0-9a65321c1e41",
      "aab86597-1a7b-abe9-0ad7-92fee8b56ed2",
      "aac86acf-44f1-277b-29ff-7747e89eaa47",
      "ab088440-aca8-7f8e-0abc-c6177d62ea91",
      "ab2b85c0-acfb-f209-2d93-96028ad8374d",
      "ab4aab07-f762-1503-3d04-4380ad5ac08f",
      "ab7bbfbe-6e89-ff16-4731-a4f8c32f0229",
      "ab7f5b36-df74-e590-3b05-f3c59fa4b2a0",
      "ab9b59f8-7288-19b5-8e01-e09e44579e13",
      "abc66a89-927d-2d67-5ee9-72b7a78a24bd",
      "abce83b0-690a-c2b0-2e8f-202cbf3fc8c9",
      "abe21c56-4aac-b6dc-e5fc-19f6e075e451",
      "abe780c8-c2eb-ee37-2e5a-d082a0725591",
      "ac0109f5-198e-eac1-9efe-10dc1f1c69e5",
      "ac21525f-6fee-9f89-ef87-fc44b017eac8",
      "aca9c611-534a-8139-633e-075a04831a1f",
      "acb27c9f-3056-4397-8ce3-dd940ed7702a",
      "accc02b1-7769-f87b-8c7b-fef81d993d6a",
      "acf00fea-f072-621b-92d3-5f2a6de14c26",
      "acff16b7-9ded-2798-a32a-cb8d66de13c0",
      "ad1a97df-4b52-00ab-6825-991405d63541",
      "ad3a2071-4862-006a-d9ff-5d513c3f6fc5",
      "ad4fd168-a878-663d-f282-024a21b47ce5",
      "ad82e640-718a-2be1-26a8-1aa26f70d20d",
      "adee83f1-f18e-dd69-c4fb-dc874f42afca",
      "ae14766a-0229-fe8b-dd1b-645fb6c55a8a",
      "ae359a66-856e-6c93-1fc4-8bd50341fefd",
      "ae44b708-e4b0-00a8-f7a4-91d07258b5cf",
      "ae4b1a05-4654-7ad0-d1c2-7c2670a0a887",
      "ae6ce38f-85c3-c668-8d30-8b43e2a88325",
      "ae8ffde4-cb14-0f92-1afa-bdc4ba52289d",
      "aeb3f42b-9f39-9e7c-e078-fa2eb9288a05",
      "aeb96a8c-6f0e-3924-7f7a-0ecb784c8bb7",
      "aed05ebe-4b3e-8fe9-21c1-ed57706cf161",
      "aee5ce05-9b3f-9571-4685-f1937c9ef6cf",
      "aeee2fde-9061-8179-335d-bcc49f3dbc33",
      "af14d3fd-e462-493c-c64a-e8f78600485f",
      "af33bf0f-decd-c1bc-8a0d-8de588fe380b",
      "af42380c-305c-a308-7083-ff9ab377d273",
      "afd02175-836a-8b89-d63b-af57c1a95dce",
      "afe5b63d-7f37-1b47-9697-88aa86c63c00",
      "afe94cab-75ee-f1b8-c3ab-d83b6200998a",
      "b025c52d-76fd-9407-e2fd-76b141bc65d4",
      "b02becde-ba64-3f5a-2629-793df746a331",
      "b0426639-2d51-1406-e6c7-7e7bcda05c59",
      "b099ba85-da4b-88fa-aa8b-2242e273217a",
      "b0cdd7a9-8863-56ce-3dc3-11a57ca2ae2d",
      "b1058be0-9250-db9e-32d1-ff6f51d45f90",
      "b117040f-9755-a6ad-c142-e4533d284dad",
      "b1846c87-aae6-3933-3ce5-e929f767adc0",
      "b18d69ff-1c54-5de1-7c7b-3151ed727efa",
      "b1ad2fb8-105d-0cdb-a762-d436fd80acf7",
      "b1ed7faa-88fd-2955-7609-7c0257d61bd0",
      "b2069634-4630-9e92-35ed-91606c36ce03",
      "b2662a8a-5c35-9d45-7dbb-5cbe87a7d9ce",
      "b2752d82-e5e3-610d-1b1e-587f1de14f90",
      "b2969b94-ae96-3cad-3dd8-778c75dd320c",
      "b2984909-581f-aba7-c49a-efe6be7274aa",
      "b2da051f-5df4-f52c-533e-ad86cc4e4cf5",
      "b2e95089-e2fe-c5c1-419a-618fc554622d",
      "b33bf27b-d201-a264-a8b3-4e2c606cd566",
      "b35660df-8c01-1f50-e3c9-d4c91b875282",
      "b39720c5-5394-12cc-b913-a602e52c7a0a",
      "b3a6b900-bbee-a20a-c8d5-72c5c3bb235d",
      "b476a91a-c3db-8a87-b210-88dc1751064f",
      "b4889ba5-4619-38c3-fd08-2378776b593a",
      "b4e46616-068c-bf5c-8169-c99dce4a5a09",
      "b4f217ea-8acf-3abc-6052-6aee105297f8",
      "b5379f70-83ad-ed38-cb4d-d3256db30b6b",
      "b58c6876-72eb-bd76-d75a-6a5325aed482",
      "b59258e2-c419-b7b0-8793-864203703dff",
      "b5ca91cd-918c-5f8e-c84c-13285ac990ae",
      "b5f640b7-27ee-0862-a4d0-1f5805adeabf",
      "b608f2e8-f152-f742-a3c4-dbf2c1778281",
      "b60a0169-20b9-9db5-1884-51be54d4ded0",
      "b6396849-c1e2-6232-61c7-fb12ff5f1b0b",
      "b64017f1-4528-ce02-89cc-3193ae008620",
      "b651b121-b34c-f077-bdf3-ff2c28170d99",
      "b69d58d4-b46f-fcf4-91d8-bf2e89e2e910",
      "b6a07e11-f65f-b9ae-37df-b72060eb9407",
      "b72e7b30-e0c5-8c4d-1b1b-7f267a91f3d2",
      "b748cde9-c496-1ebb-3c33-edc8f41057de",
      "b78a12f3-397a-f9fc-b119-41d3e33e09dc",
      "b7a75d35-a2c7-6c50-77e5-34a139cc0a7c",
      "b7b01544-e902-cecd-cb0a-bf8664f4ddb8",
      "b7f26c10-c43c-4288-c46d-b1c13688f643",
      "b8307bf2-0e31-dc11-2b09-40e882aa73c9",
      "b839296a-2279-7b22-fa88-8f53d3d21624",
      "b840330c-1a05-d734-7a1d-2d4adc2464c1",
      "b8981943-1c52-1ca9-d2f2-c64cd4d1a14d",
      "b8d1b571-663e-5e95-0a9c-a7ff1a1d5e31",
      "b91c0ae3-80b1-afbe-1e30-7702661e47c2",
      "b9486373-2a29-240e-8610-5f6572fb320b",
      "b9719ed6-5ac7-fa44-f083-de96b9531db0",
      "b98b3b36-2c6e-6d9c-8eeb-a567e56f25ed",
      "b9a2eb4d-cebb-f7c3-7e19-ae0963de42d5",
      "ba147715-6745-f852-3605-6bbde2164cb4",
      "ba26ee93-4329-808a-a998-4cc78122a16c",
      "ba5ca97b-7ef6-8323-2427-7b80713d2c32",
      "ba940fb9-1e4f-4efb-0b9a-d5d378524b13",
      "ba9fb665-f192-5e3c-2654-d91472d538ee",
      "bad4e68b-9422-901d-fa68-24049aa9f961",
      "bad52820-ac89-e884-1362-d29c700a27cd",
      "baeaaf58-d448-7acf-7fc0-f7132fbcfa77",
      "bb0fbfa9-5820-1fae-a9df-61341b7e6e19",
      "bb27989e-3df7-2494-1fff-2d2c31222c19",
      "bb6733c8-a825-beb9-0964-7d080b1e700c",
      "bbdbb4de-2f86-82e7-d6da-97d02d23a4e4",
      "bc205159-8e3e-c60c-93d1-79c042702ef1",
      "bc2e211b-1553-54d2-cb10-889907353619",
      "bc4ab773-29ac-dd52-579c-16ffd15db72b",
      "bc86898c-227b-5e35-d4a9-a688b7a392f3",
      "bcaf90a1-48be-bbc7-4a41-38e5e4efdc69",
      "bcc2bf58-98b5-d955-e087-19cf55c3f4df",
      "bce68864-aa24-a6bc-1be9-b7c2b11fe24e",
      "bd1cfe8d-482f-b1cf-8323-30750d88691d",
      "bd238e74-eb5f-8311-1e3b-10aca683f093",
      "bd427e5e-3945-8701-e5eb-e1d789f05e93",
      "bd46be7b-4899-daf1-758b-84c67648a913",
      "bd4c854d-a728-5ac2-9670-2c2fac74dd45",
      "bd82e0ef-7d9a-476f-b7ce-6c7fb2c6c0e5",
      "bd86a304-6c0c-1538-8fa8-12081d33eb83",
      "bdd6ebd2-0c1e-924e-2764-2f68c5213ea2",
      "be053156-a112-af95-44f1-9e61cfc1827e",
      "beca1b27-fa5c-8cea-8319-f80d1f49b9c1",
      "bedbe20e-7aea-bb3a-5f3b-5a6a6f22a562",
      "beeb158b-20d1-ae97-8282-9f545bc3c169",
      "bf5b87f3-2f15-b511-454b-33645d3dd95c",
      "bf847586-1809-9f99-2e7c-dde3a35fb94c",
      "bf8a4668-7c0d-853e-4061-bc94b2a7d2fa",
      "bfc00787-64db-e692-f439-1d1578022362",
      "bfca1a6a-8189-f0ae-a4eb-0609fdfcd596",
      "bfe70ebc-a6bb-5a60-31b9-1fa2a3fb8760",
      "bfeb8ca7-7fa2-45b9-c5f0-b343c9f4a962",
      "bff90f30-0903-a85a-fa0b-cafb80b80566",
      "c00ac986-f1bb-20d1-f535-6f0df9e586de",
      "c0388e6b-a779-390d-4eca-cfe95b03e6c5",
      "c0ca8036-9243-87c1-6153-47852f96d910",
      "c1061266-abd4-1ecf-0012-4a4f0d6fce42",
      "c1134bc2-edec-27af-b6e8-e987ec3c3804",
      "c1268eb3-fbd0-b57c-5fb9-857a044c564a",
      "c12d1c6d-d670-0b00-d04a-80ea08b7850f",
      "c136b7ef-7428-ff9a-71d3-c3c84b15754d",
      "c13c5a30-f4a4-5958-65f5-f7e0f05f1de5",
      "c1410478-9c9d-7b0c-9149-da879cf8c7b3",
      "c1417c88-f282-b3f9-1102-d85485db4ba2",
      "c15f67e1-1752-0479-b725-996c976e1aee",
      "c1b1040c-beb8-84b2-eb77-9ddfe727f772",
      "c1c3dbb1-c244-61d3-0de7-74d731377f06",
      "c1e56288-5dfe-1233-8b7b-6b54f0128a3f",
      "c1e8fe69-38df-2900-499c-e7db12866b10",
      "c2205b34-3cd9-7c38-b2df-3c0e88feefd0",
      "c23d457b-80f4-ddbc-da0e-2baf0fe48c7b",
      "c24594e0-cfcb-9f36-1dfa-036be7bfa2d2",
      "c2546203-7f19-41c1-5142-784bf3b0b003",
      "c28ae0d1-0869-c051-102e-81b6f59bc853",
      "c28c94b3-b36c-ba25-88f1-79e1dc5c7107",
      "c29cbc77-a3d4-87d1-9480-3349dc035bf0",
      "c2a068ba-9744-7b47-ab9c-69f33df229d9",
      "c2b672d8-0460-028e-3a0b-aff093ca57b7",
      "c2e26faf-be0a-7209-39fe-53f40394cdb6",
      "c30b49f6-ae8c-9fff-184c-c3f70e5cf99f",
      "c32336a5-f3c3-442e-6baa-eb1b7dd6bce1",
      "c35068d3-257e-584b-f218-84cd364ab584",
      "c3d82dd4-bd83-85b5-485b-3a9b45989bfa",
      "c4785784-fb5b-8de4-3a9e-e53345b26d4e",
      "c4a1ea89-bfb1-1347-7808-5cdfc8b5b3c5",
      "c4f4e397-5cf5-b163-ca36-5dddcd9b4049",
      "c53c8d9b-6cf1-843e-0fa5-f82276c015bc",
      "c5402bcc-6cd0-4d51-121e-43538c795310",
      "c56e6f92-a76d-a206-0550-3e746d573bf7",
      "c578c4d1-8c94-6d45-4c82-97dde6ed6292",
      "c5a6d31c-7f24-efef-9d9f-3a9b18fe42ef",
      "c5ad594b-8a5b-6d83-5d63-6c79ee11ac11",
      "c5dcb5fe-69a8-bb82-ddd1-d4c0a7336ae6",
      "c5e50355-2692-d36a-3f2e-c6ee4fbb77b1",
      "c5ebd4f9-162f-d619-f486-2ecbaede4b90",
      "c60c6a03-d1e1-1641-c24c-f156eac5249c",
      "c62d1954-a59a-c2a0-8e51-94bede232e14",
      "c64fa76b-d17f-34c8-587e-66ad0ff69b6a",
      "c66f754f-853b-1642-197b-7188dad6a98d",
      "c683f015-2de3-f0ba-2388-3aa9ce813dea",
      "c698df09-f0d2-cd18-a81c-32e3175df2ca",
      "c6a0ac05-cc40-2911-399e-464300553e2e",
      "c6bfe26a-d9dc-545e-e8b2-f26eb0797bae",
      "c6e7f77c-01b1-aa60-3ae0-54fee81ca16c",
      "c703f675-50dc-6a8d-b774-e4b778d39cec",
      "c7316f15-6ccc-8b79-d74d-12c2dfb0b9f4",
      "c7a73b9d-3a8e-6ac4-d397-b8c9d9c160cb",
      "c7aa41c2-e133-45df-d4e8-c7d2b4b1cc72",
      "c7bd9941-0447-8865-9314-43affd9c739f",
      "c7fe583b-9b28-df59-fd36-0dff1f3ea80d",
      "c80bb198-745c-dc68-69a6-c6daf5de15c4",
      "c8628d3f-06f5-d7ec-e8fc-c30f90a22105",
      "c86d684e-cd09-41f8-0d65-ff19b0af8833",
      "c8a93f9b-d0a9-7eae-272f-f3a0a962832a",
      "c8af7f83-9dee-eac6-a5c3-ce5b46334f3c",
      "c8f5cee5-7002-62ca-e0e4-cf3c9b97f595",
      "c8fec91f-2228-757a-765a-d7836170e59e",
      "c963ad1e-7e19-ee71-014c-e924fedcdc0f",
      "c9816994-8ee7-8137-33a7-1a189c1cf855",
      "c991722b-498b-d997-2e9e-42131edeb9c5",
      "c9ae45e0-35af-0de5-e444-5442860f7752",
      "c9b0e7ac-71f0-c5d4-4f67-7d07b8e4d548",
      "c9b4acef-4348-1bd4-9f96-d3adfec68fca",
      "c9e3ca06-e337-098f-0a82-ce98e4645566",
      "c9e8ba67-b836-e455-ee0e-73f2fdf1612e",
      "ca130fc0-427d-56db-262a-42752d6e24d8",
      "ca1c8505-6954-15fe-a352-9fcd9c8d016a",
      "ca666596-b5dd-b365-1378-269010325c4b",
      "ca6ebebd-8d0b-be86-1cec-082f2ba6fccc",
      "ca824df4-888f-fc27-bee1-02574015e285",
      "caa7c472-a8e6-1e33-2e78-3ff3728347f1",
      "caeaccee-53dd-ae82-3148-ba0d4bb6ca16",
      "caeeff58-b580-0b28-20bb-6e1ff2b85fdc",
      "cb0a2701-1c97-253f-c246-93ccc53b3da6",
      "cb67b78b-3210-dd63-8f94-883c95957d0a",
      "cb8d8648-aa1f-9206-a66f-73f83be03612",
      "cba2b69f-7088-3c06-f799-a71ac4c76acd",
      "cbbf3575-a94b-6374-eaa8-8728fd983f2e",
      "cbebdf54-d98f-1634-f259-2cf16ab1f79b",
      "cbee2d96-8126-e745-27cf-bbb2528132c4",
      "cc29b660-0a24-79a5-53e6-7ce49d55af0e",
      "cc2c12b6-ee50-e9f8-665c-25862a617b71",
      "cc314f58-8fd3-2a19-a631-b26ad95ce873",
      "cc76eb84-7fe5-ec22-c0b7-5014805d96dd",
      "cc9619ce-46cb-e6c1-1545-35c32d437d3d",
      "ccf88082-4067-348c-0a4a-c01cf6745d9a",
      "cde88516-3ae4-65e0-006c-5b525a397641",
      "ce2be85d-6036-ee64-fca7-46ee4c910ec6",
      "ce60ed81-80c0-c3e1-c655-b65c7b77fc58",
      "ce6cb83f-190b-4474-48e5-d29037003498",
      "ce995f4c-484d-a224-3003-9641fa1549d2",
      "cea59186-244a-adde-4a92-797fd46e8cea",
      "cf17108e-f657-1dcf-4459-357a89b39b47",
      "d00e56b0-3f75-651e-513d-089d4f3a38c6",
      "d0100932-678c-be95-8325-918a3c3e3bf5",
      "d04f3510-324e-8256-fff1-60400f21bf61",
      "d08c0e65-3527-64ac-68cc-dbe6fc00a486",
      "d09a3aed-b967-5d1b-293d-099ecf774005",
      "d0a5e2ac-cf5a-2cfe-50bd-7eda4a99531e",
      "d0aead39-6e7b-5789-3ca3-6f75ee198940",
      "d0ee6a57-cf7c-19cb-ed95-5d95451081a7",
      "d14037a9-ca34-15ea-624f-929dfb876160",
      "d159f1a4-8121-957b-c6e2-16b0ca107434",
      "d163fd10-5447-9738-3f4b-904cc48c0d6a",
      "d1662770-0ecf-a126-5f04-6dbacff93514",
      "d17d5f3a-46c8-2c79-6d9b-9d547705c15a",
      "d19d1fcf-a289-a87d-30b7-7152b34af4da",
      "d1b0cfa2-c180-94b2-7eea-5a7679a01227",
      "d1b2a0b5-ba0f-6af9-6831-b3e5c4b2bfc4",
      "d1baa080-bc7f-f0f4-8b16-96d1f9e021ab",
      "d1c3f543-c51e-0a70-bd82-1faae5e24295",
      "d2570c26-41b8-dc60-a14d-6941b37e3b7e",
      "d28c252d-f771-0056-9779-443a9a3a9c90",
      "d2b042d4-c14f-c8f5-133a-5be9cbd39ab8",
      "d2c74a77-0997-c5f6-22c3-2f19818493ea",
      "d2cade33-70ea-f88a-d2e3-5971d3b7019b",
      "d2d06ee1-0ec2-0c3d-f737-a137a3126091",
      "d30d5903-0ff5-96e0-46b9-fccb93b9d3e9",
      "d30f1d16-2efc-78cb-c858-14bb121548d4",
      "d35a0c27-1454-5df6-9769-bf9c85c00488",
      "d3beb7d3-29fe-1689-ae12-6caf16c8c949",
      "d3db43c6-950c-a6d5-8771-bf447ddabd7b",
      "d3ef1b78-a11b-b822-9221-4836c358ac2e",
      "d46b4eab-5a5e-c3b8-4795-aa0828c3dce9",
      "d4817dcb-2cc9-a186-3c51-a45da00c5980",
      "d4989791-e3d8-33f3-03f5-01bdc2fb73a7",
      "d4c65ac7-c265-5bab-e3c7-f603c48f92a0",
      "d500c339-4dd8-6e90-24ee-1e3ee401d2fb",
      "d5346fb8-6aa5-493a-ed88-1415e0c2005b",
      "d53c3129-3271-dc9f-0987-9d069b42304f",
      "d546f04d-2345-b0b0-5c1e-e2bf5bcf270d",
      "d549dcee-15b7-7ec4-5358-e81e914ad2d8",
      "d5953d95-d9e2-a837-ed07-60643992cb16",
      "d5970f32-ce1b-50be-c762-a77134c5456e",
      "d5a75c20-8265-9dc8-9213-10cdc5af8688",
      "d5b73704-705a-d089-b50c-b716eba59c0c",
      "d5bf0427-c3ea-f9c8-933d-deb15ff23d3d",
      "d5debe7b-da69-a4a0-c60a-d7f9fcf71b06",
      "d5fab709-9ddf-7581-227a-d50b9587a6a5",
      "d6082ab9-80dd-1157-6553-2d03aff7fbe9",
      "d6219e1d-87a5-5a7c-e114-9321849add8c",
      "d66d5d60-9f1b-7425-03c8-bc1b1a9a0910",
      "d672aef8-6565-e8f3-6df3-7cb4ecb019d2",
      "d67fa128-22c1-5b86-56b8-046a47578664",
      "d6afddb4-c774-fd8c-cee9-8edd6e831c53",
      "d6c524f3-c801-302d-be1e-cc16fda99d0b",
      "d6f88c23-2f87-964f-b33b-f31b8e0aceb2",
      "d6f8df19-66d2-4b57-2d5f-b6726de7b667",
      "d6fa1359-5b6a-b3eb-a67b-b1cf714291d5",
      "d702fa9d-b05e-f70c-b26c-1eae48912fd5",
      "d71bd310-4404-257d-f344-e161f5d80db6",
      "d7427258-34d9-44e9-73c5-630308a8b138",
      "d79c6998-3271-e88a-0451-56601bb12bff",
      "d7b3cf40-4f50-fd7a-17c5-df5eae6a61b5",
      "d7bc8d89-361c-d61d-29f9-19c3d9414155",
      "d7e65a54-5dec-994e-09b5-602004f3347d",
      "d7f08099-cd22-9ed0-a95a-6b469abcc680",
      "d82c26a1-f316-f72f-d1d7-441caf19728a",
      "d83d5ac0-1923-c45b-563c-dd961754ad5e",
      "d86f8c85-1f42-3bd7-9e85-78036e4b7177",
      "d8716125-dfe6-69c4-eb64-c8a8da2c2128",
      "d884e1c7-cfde-e2da-b504-c88cf3621cec",
      "d8e3917b-930e-f719-e1c6-450ea3cb6e80",
      "d9071f6d-87a8-c2ff-2a8c-609fbe331cb8",
      "d90da23d-8cd7-ae08-1d22-269cae0da83f",
      "d98fcbec-80bd-cc85-4784-5326aaf21ffa",
      "d9defa5b-19db-bd1d-5700-3d70a731c4d7",
      "d9e07297-dabb-1965-130a-5950b96d6d0a",
      "da1cb414-98b2-5b08-8475-30b5db256ae1",
      "da4ddb5a-1190-b86e-b81a-5f91399dee9b",
      "da710d26-932b-e95f-627a-19565d2d531e",
      "dab01bcb-763f-c6be-3c34-a6bf7396d663",
      "dacd6390-2842-651f-ee61-d11512f91e86",
      "db0aaf8f-4bd9-cca7-40bc-c07708ac2fc7",
      "db269c06-33f7-a19a-f25d-2664f1276d2e",
      "db816d77-a2c5-6f2d-799b-593a81f6cc92",
      "dc0fc0d9-634a-ccfd-ad46-7d3c83d2108a",
      "dc3c9962-c515-2a7d-281b-052a8952da9b",
      "dc60c383-14a4-5a2e-32fb-cddb52453bb0",
      "dc789115-cf84-fe7e-73a3-2e16153cc4e2",
      "dca10ce6-e8cb-4a06-7d49-d197529de614",
      "dcaab5e5-ebb8-fb51-6398-d3734aa9d300",
      "dd42ef66-6029-2d67-dd1f-c28bd4ad4c6d",
      "dd97e287-1c3c-1171-803b-5498eb00b620",
      "dd9c8652-ec81-3c52-d7db-2788a2935b2d",
      "ddb5f52b-5610-a708-5271-23915cdab993",
      "ddd138af-472f-296b-b747-74eebe9a2859",
      "dde61f40-31cf-8fe9-7585-9bf05a408fd6",
      "de8a4ac3-c8d9-5b58-f98f-ad8d12406a6f",
      "de8c793b-100e-3eed-e813-a1cbdccd67ee",
      "dee93a05-3313-61aa-8165-dd596c9cc623",
      "df16766f-51ae-c4ce-0bd6-7a45b36b4529",
      "df4c7c95-80e4-6726-2249-c42e36d98d79",
      "dfc05e75-bedd-cabe-2e13-60e0279cc92e",
      "e036c956-8869-3c98-bbab-86c5caf8ed12",
      "e080bf87-a185-708c-b158-83fac382b89c",
      "e097eb80-4e39-3ef8-0a82-1b12fbb54115",
      "e0b6391f-1bef-9887-a303-a50236c327df",
      "e0dac2f7-3d8a-c806-336f-0651010826c2",
      "e0ea3f2e-6d32-7593-6b2b-794998ca63e1",
      "e1014146-479a-7fc5-0a59-fa8b417a8b91",
      "e10ad457-d9a0-15db-921a-2e8eaa99cdbe",
      "e14eb3a1-9ff2-2320-9065-b14e2f7adfc3",
      "e17d1b88-833b-4b22-3124-1ef48d03a184",
      "e17eaf15-ebd7-8ba3-8ff9-b23ea5afc8c4",
      "e1b4ec19-ae93-cf18-9c95-eb309759d0bb",
      "e21e83a1-ffe7-4b09-6c74-4ca9a298543f",
      "e2276d16-c1c3-6f2f-1a88-c092e12b672e",
      "e239661c-1d58-afc3-271b-bf149b275724",
      "e2c86e00-bf80-5a0d-005e-6154b26f59d0",
      "e2d79df7-7b4c-88ab-daeb-402665514a88",
      "e3789a69-8c3a-32d1-e515-1e1bbb601de1",
      "e380f952-4c83-3e73-0dea-4539f0bdf019",
      "e3c72cf3-6a64-a2ed-8c8f-69dfa089e589",
      "e3d1bfb7-4b26-251e-66e8-677a4cb0362d",
      "e4472897-ca7d-9e21-0d82-2b8e83a4064f",
      "e46b3bfb-2f7c-6f76-d174-eda79ded9686",
      "e4fa22f0-a063-eee0-d7db-c64412dcd3f5",
      "e54cb2bd-0991-a935-1c64-8733e139d96e",
      "e5913a3b-0ddf-3006-1b42-bb1b2640a748",
      "e5d0627b-a7a5-c151-ae1e-4dcd48bb9983",
      "e5eebb47-7818-27d1-a83a-dd54f1d1033b",
      "e60ed4db-d840-e44b-416e-9c9f27b2932f",
      "e63c0dfb-bff7-a1a1-8f3a-84dc48cab3b2",
      "e65ddf51-7b60-3578-57a0-36997a5bb425",
      "e6998624-8225-68af-17c0-1b44b7df3d1c",
      "e6a7fe1b-eeae-5e58-9a67-b255931653c7",
      "e6f9a325-02a2-fb2a-85fd-28ca16afbccf",
      "e7081c24-ddad-8ee4-6eac-4d386b0dc36b",
      "e711bd89-7df2-904b-2472-2972022f4839",
      "e714e94c-b208-3b4b-4318-8b2dbb7c0200",
      "e739383f-adf0-8b84-1326-fdde32a9e8cd",
      "e761c576-16a1-c058-4aa6-58df7f447436",
      "e7a5fae4-049e-f75c-ca82-97838d9c37a3",
      "e7b09558-b4e4-d87a-766f-91a2f8518cbb",
      "e7c4ca7e-d0cc-1ae3-fb36-dbc9d73ffbc9",
      "e7cac818-d71b-6823-b66d-49a123afda02",
      "e7ce18a8-61f6-b562-c338-7709830030fa",
      "e7e7294b-f915-4846-1e8c-eda0f7022430",
      "e860e4e4-fce3-08c1-c97e-97e9ce859435",
      "e89e85bb-40aa-03c1-3197-1db87aa61207",
      "e92178ee-c616-1629-b20d-f12bec381b04",
      "e9335fec-4d38-38cb-fbad-25dff403d5b7",
      "e93c7055-0c67-e6b1-2dd3-6069d53c1c2e",
      "e99473e9-999c-66da-7997-c22f9c007a08",
      "e9dae09a-8ff7-9f1f-2104-68da27c88f21",
      "e9ef12de-d81c-f872-2f2c-b861751ad8d2",
      "e9fa2305-e7fe-a04d-41fd-9c9382bba390",
      "e9fb8b6b-5809-d292-c348-2b0962e8eca6",
      "ea0be47e-99ed-ca63-8386-c5d2c5caaef6",
      "ea2159f2-e93b-4a94-f8bd-3fd37b9298c5",
      "ea258620-a86d-62f7-7caf-fc2e4cca8d30",
      "ea61ca6f-f2dc-6ce2-c488-53992bb56fec",
      "eac3f5de-47ee-8ebc-2c06-9aa619e42e6f",
      "eaca5708-e469-40f3-2c12-8da66b2dfa4c",
      "eaf3e1be-ec85-a0d1-3dea-c11e6e4aa63a",
      "eb0d969b-65f2-1ab7-8471-d514c443f569",
      "eb107684-f4ed-a90f-5ebe-580369c9b08a",
      "eb300dab-65c4-49f0-5a82-4cbb1e08d2d1",
      "eb57bb9e-e574-a7a9-b285-48c6f9f3786a",
      "eb6f6d9d-0718-0bf0-cd06-80f8e5930e22",
      "eb7dc658-a74c-2170-54f1-577b5379970d",
      "eb8abca0-a5c8-5b39-9ea3-f044196efde4",
      "eb93065d-b188-1aad-7f9a-7c330be3dc25",
      "ebdad1d2-5eae-e9ce-e99d-ff3d5a84a82a",
      "ebe838e1-6ce2-680f-0b1e-8c4a533e5464",
      "ec3b4575-60e7-a499-c37f-c9ae12b11e0e",
      "ec59f453-1855-f508-32d2-f63f10699526",
      "ecb5781d-7c00-ac84-5831-c2d971c76b9b",
      "ecb9fcda-9a8e-86e9-b548-12327b05fe26",
      "eccc9735-913f-1ea9-2d80-146da0716d28",
      "ecdb4257-4905-f6a4-e28f-656ea33ab87c",
      "ed31d169-323a-f162-6020-3882412d04d8",
      "ed70c501-bf76-ffed-7f7e-59421d4f35f3",
      "eddbefb5-44ff-415b-f9c7-8f0b2c63fe5e",
      "ee010e8e-ab94-0cd2-d826-4a0e144c4ffa",
      "ee4a5125-3af0-858b-7745-16e46080c33f",
      "eea2ec2e-9e02-f19c-81ab-238412df461f",
      "eea6f80c-b871-fad7-f989-7021f8db58e0",
      "ef0af52f-cdb2-f336-d575-22848a385532",
      "ef2335ef-865d-4921-c2e5-977ec4dc9ac2",
      "ef3d8eef-ad86-0910-7c0b-550eeb8f5451",
      "ef51d721-4265-e390-f4c0-2dcef95c03d9",
      "ef881666-f6d3-c565-a332-8b1c32d6767b",
      "efcb95a0-6f5b-fba1-f700-465ba398fcef",
      "efd15ca8-c9fd-e5b5-fd7c-153c5417b7ec",
      "efeb6a1e-c75b-3d03-0db8-ebf4d67528bd",
      "f054397a-1259-52a6-55bf-0d870a5e19a5",
      "f07ca678-966f-7c86-2ff7-1e2e5c789f00",
      "f0816440-682a-f732-697d-16863f42e962",
      "f0821ab7-a335-b778-a899-2e4096e77ab8",
      "f086dd03-5f1d-d609-0481-6437df537f6f",
      "f098ca40-9b7b-ac95-36e5-cdecc772bc8e",
      "f0ea43e7-de92-1bd8-83b8-07525c223864",
      "f0efb4e5-2f72-55a7-cee4-10d0a5f0efe1",
      "f1151544-8f4b-8366-9a6f-34d169b63e93",
      "f13d3eb6-1328-46b1-e6d8-bcbcf32f9f88",
      "f18c11cc-0c7e-f94d-9d96-d2b970763b61",
      "f197ff05-afd2-ce9b-577c-de4381d9d0dc",
      "f1d89459-9368-d428-634d-64dbf50df233",
      "f228c3c1-e50a-135f-86fc-f39f6be3e994",
      "f248d8c3-9cde-2774-fa1b-268e5d9915be",
      "f255031c-4d59-a77e-c3c4-07360beb6a70",
      "f2a42dc6-736e-c19b-3734-eb3e01e6e936",
      "f2bcbb9d-d6bd-1323-ab8c-58ef05672709",
      "f2e0653b-18c3-91e4-84a4-0f361625b012",
      "f34ffdd6-6f1e-d798-d8e8-3a9667727a50",
      "f35c1682-0ad8-c906-053c-8ec3c11842af",
      "f3fc55cb-a112-5768-f291-7aa7e6e6910d",
      "f40fa041-b021-e7be-30cd-72a3878e1a5c",
      "f41fd1aa-0799-bc5f-a72d-f53c68cccea5",
      "f4301ac0-7b76-7251-fa56-7151d12627c0",
      "f46afc5b-48d2-01e2-26df-96de77496f2e",
      "f485f448-6463-bf0c-5c73-5612226e638f",
      "f49674cd-6d23-cee5-afc3-2ab0c44378c2",
      "f4b3d190-9819-f908-08d3-428aaaad3863",
      "f4c12d61-e836-d654-58a5-fe93bf176a87",
      "f4e29300-d124-0127-52fb-14d5be0f1ef6",
      "f5232418-3705-8661-dda0-651e98d991b6",
      "f57c5e07-d978-28da-be57-ba8699acc3f8",
      "f5940a1e-17a9-6ad1-da0e-ef7bf8e5d738",
      "f5a5c886-4f07-a6a9-729d-44ccd2ca053a",
      "f5b1db8f-9d56-d29e-2ed3-67d0159e2807",
      "f5d0ddfb-dc85-27c0-9491-279a16aa736b",
      "f5e25625-e4c0-0e8e-4c5a-7aa0601bc418",
      "f5e64514-8fc9-7102-5d02-cc8964685457",
      "f62833bc-4e33-5c5a-16a7-30f44f0dfb16",
      "f6357b69-8777-4f27-73a1-6639e4421881",
      "f69b4ac2-c7c0-9aef-a771-2e558daa4ce1",
      "f6aeab43-7d7b-4476-fe6d-bbe69de759fe",
      "f75a4003-4438-e59e-7d69-4d70f99a81a1",
      "f7d03d9a-23f1-83f9-afb7-770f58aec66c",
      "f7f0235f-f1f3-15d2-ccf5-b870a48b2963",
      "f8ad1f8b-aa8f-75cf-c387-ee2fb475e9b7",
      "f8e9fc76-8f79-3ba2-865c-86297dc7736b",
      "f904c9f7-f66c-3fb3-0783-0460f363145c",
      "f94a18d9-2212-e986-d735-e8d6517f51c7",
      "f999102b-70e4-326e-b4b7-212381fa92b6",
      "f9d83b86-6120-7b43-ec87-953ea6d355eb",
      "f9f3ee49-dd77-1283-b2ef-03abe5e9b4ee",
      "f9f607e8-cae7-06d8-4518-e4bfb811cf48",
      "fa9831c6-6775-a870-e951-3dc897a8c398",
      "fab66767-82dc-d7ff-0214-48dc81671e1d",
      "faee7d4e-e7c7-6b61-6162-8bfbc9873b44",
      "fb199ce7-c83e-19ee-ddd3-cb678550198e",
      "fb903198-40d0-d653-e337-c5a78e92602a",
      "fc0129e3-c37e-b179-e94b-e65159182b50",
      "fc0c17bf-cf1e-736f-2ade-d0520a71e6eb",
      "fc2a9a46-ecb8-7b64-a029-35a65d96b24b",
      "fc2c10cf-d31e-3c6a-5748-8e8e1db47718",
      "fc46a616-3618-60bc-a085-aa97017af805",
      "fcb28975-0bc5-e238-cdec-aceb492b7ddb",
      "fd0b20bc-4cce-db68-f3d1-5689f740af1b",
      "fd60def2-8c4e-a7b0-97b3-7b4a14996ff1",
      "fd6c8b1b-fb78-ab68-ae62-7c279a5f23f4",
      "fdc03c36-ee06-7477-74f4-673a4497fcdc",
      "fe1be7b9-f02e-daf5-2bb4-2fdcdfefe12b",
      "fe3f1ace-2794-6bdf-1e0d-7340c089a2f1",
      "fe3ff1fd-a9c4-18b8-585a-de1977f72e68",
      "fe4a601e-5ea1-ebe2-237f-57e1797814d8",
      "fe679bba-77d8-3999-4971-fadc7385be80",
      "fe9e3b28-ae3f-606d-39b5-eba2e817c2cd",
      "feac5974-899d-282b-f5cf-c9fabb9d9958",
      "feaffd78-30aa-6c1c-d1df-d14f31e98ded",
      "fec44bff-0abd-e91a-a277-550beba03fbf",
      "fec7937b-b7fb-3cb6-3b80-30cbcbefc07c",
      "fef43715-094d-af8e-8056-a4352e57eb4e",
      "fefdf415-49e2-0b92-2a44-68af13b388d8",
      "ff176f6d-d64b-12fa-9d50-98a7f02bcee0",
      "ff22b4ef-785e-c033-fd9d-4429492b3ac6",
      "ff270387-accb-0a70-bf15-3a7ce1232ca2",
      "ff4d67c7-ee7e-3876-1e73-afa2b7e500fa",
      "ff811e0e-4e3f-f433-c5e8-481e818ec140",
      "ffb8fdc4-b3e9-a755-ddb2-ee062c30e2da",
      "ffca3d0e-6852-2c7f-a626-ce5d701ae2bb",
      "ffcd2c36-e746-3f85-e2d7-e73f4f0a9e28"
    ]
  },
  "mutations": [
    {
      "id": "extra",
      "mechanical_status": "ok",
      "source_sha256": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "missing",
      "mechanical_status": "ok",
      "source_sha256": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "changed",
      "mechanical_status": "ok",
      "source_sha256": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "commit-refusal",
      "mechanical_status": "ok",
      "source_sha256": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    },
    {
      "id": "production-gate",
      "mechanical_status": "ok",
      "source_sha256": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
      "scope": "recorded target assertion only; supplied exit codes/build provenance require review"
    }
  ],
  "evidence": [
    {
      "id": "opening-snapshot",
      "path": "_workflow/r56-mainline-integration/opening.json",
      "purpose": "本批开工快照（分支 main-OldTeaBag-B168、HEAD 8a3ee6c4c、工作区状态、三个源的开工哈希、7 行原始风险矩阵）",
      "level": "document",
      "conditions": "python -B tools/mistletoe/workflow.py begin --manifest …；无产品副作用；此快照早于本批导入",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "c083c11ee6003b079b9772779ba040b7258883db6e13fa5412ef25e5fa1e705a"
    },
    {
      "id": "intake-equivalence",
      "path": "_workflow/r56-mainline-integration/intake-equivalence.json",
      "purpose": "接收等价性：唯一测试增量 48/0、blob 相同、工作区 4C101FF3…；25 个材料文件的暂存内容与来源 blob 逐项相等",
      "level": "document",
      "conditions": "git checkout <来源提交> -- <路径> 导入后逐文件哈希比对",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "f31d99f31bd3329986ad20a3f7d590bd686e7392775cbada1927719039c60424"
    },
    {
      "id": "intake-line-endings",
      "path": "_workflow/r56-mainline-integration/source-delivery-line-endings.json",
      "purpose": "摄入材料的行尾形态与来源 worktree 字节对照（25/25 等于来源现行字节；4 个 JSON 在来源侧为 CRLF 工作区形态）",
      "level": "document",
      "conditions": "逐文件 SHA-256 与 git blob 比对",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "c4f555487d3bc15ffbef633bca0b074faaa7c9911f905f47137e54ab5126a244"
    },
    {
      "id": "intake-note",
      "path": "_workflow/r56-mainline-integration/source-delivery/INTAKE-NOTE.md",
      "purpose": "摄入说明：来源 worktree/HEAD、路径映射（为何收纳到 _workflow 下）、逐字节结论、刻意未收录清单、边界",
      "level": "document",
      "conditions": "人工编写；映射与哈希由 intake-equivalence.json 支持",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "2ac97afb83684a6bdcfe042c42d9745b15da24a01552ab643853b13f2ab3a8ba"
    },
    {
      "id": "src-report-audit",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/report.md",
      "purpose": "来源交付 A 报告（含 7 行覆盖矩阵、回归/突变证据、提交清单、主线集成要求）；来源 SHA-256 044A62D0…9D97",
      "level": "document",
      "conditions": "逐字节摄入来源提交 c11cb45f6 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "044a62d06dee49fcdb8e87fad6adb30521e03a276217785faceb2ef817369d97",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "044a62d06dee49fcdb8e87fad6adb30521e03a276217785faceb2ef817369d97"
    },
    {
      "id": "src-acceptance-audit",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/acceptance.md",
      "purpose": "来源交付 A 验收单（范围、允许改动、覆盖矩阵要求、回归/突变要求、关闭门禁）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 c11cb45f6 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "377fc50c072818764fa178bafe14cf4f256464368165b41982ee378eb77055ae"
    },
    {
      "id": "src-mutation-results",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/results.json",
      "purpose": "来源 4 项断言突变的记录（原始/突变/恢复哈希、命中行、退出码），本批据此做 mutant 哈希交叉核对",
      "level": "document",
      "conditions": "逐字节摄入来源提交 c11cb45f6 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "e6f2af18317deddf9b5565e2ee67deed42559312463b4784912b61fd5835020e"
    },
    {
      "id": "src-mutation-source-hashes",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/source-hashes.txt",
      "purpose": "来源 extra 突变的 before/mutant/restored 哈希（未记录精确 needle）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 c11cb45f6 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "bc9fc94da99150496824507ec5d19ac9ff96e8a99d5a48e5e81e44ef3d4847bd"
    },
    {
      "id": "src-prep-report",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/report.md",
      "purpose": "来源交付 B 报告：固定基线审计结论、去重核销、正式消费批最小实施顺序、未决事项；来源 SHA-256 BEE48867…4FED",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "bee488672fa4649817333cdd99ee1e9d99a979bcd48caaf6d8386442e5ff4fed"
    },
    {
      "id": "src-prep-acceptance-matrix",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance-matrix.md",
      "purpose": "来源交付 B 逐状态验收矩阵（A–F 前置 × 状态/证据/余证/门禁）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "0a94a0c4ff4288852a7d8fc763adcdccfd6af1658291f3aa2b5d23bda7b65103"
    },
    {
      "id": "src-prep-acceptance",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance.md",
      "purpose": "来源交付 B 验收单（准备包与未来正式集成的分层判据）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "12a65a86e1284890b9ee81ff617d8edde711fe8dd4c19c01877c0c7c8c887fc3"
    },
    {
      "id": "src-prep-contact-inventory",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/contact-inventory.md",
      "purpose": "来源交付 B 六项接点清单（条款、定义与调用者、状态、证据能与不能证明的范围）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "1b82287e3d0565e43840e1601f96b1c2c749ce34032359909d1d150830c0b349"
    },
    {
      "id": "src-prep-search-audit",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/search-consumer-audit.md",
      "purpose": "来源交付 B 有界搜索记录（生产消费者命中/未命中范围与命令）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "8baccc2e2deff1cb783caa5c7ec75e9954ead87e64b75f3dfe1bb3646baa1f53"
    },
    {
      "id": "src-prep-registration",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/delivery-registration.json",
      "purpose": "来源交付 B 登记侧车（唯一交付 ID、目标批次、A–F 门禁、主线接管说明）；来源 SHA-256 D8AAE0C5…90BF",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "d8aae0c50c0235fc607fabb66fbb32c0000222e4797de88fc48df1a772a090bf"
    },
    {
      "id": "src-prep-manifest",
      "path": "_workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/manifest.json",
      "purpose": "来源交付 B 自己的 v2 manifest（范围、来源、证据、修正历史）",
      "level": "document",
      "conditions": "逐字节摄入来源提交 90588159b 的该文件",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "556ead0ced17150d951a7ce179d098a9759adf9fe47fd253d05fce892b3f3f53"
    },
    {
      "id": "baseline-build-assistant",
      "path": "_workflow/r56-mainline-integration/baseline/assistant-build.log",
      "purpose": "导入前助手项目 Rebuild（-p:DeployToBgiTools=false，exit 0，59 警告/0 错误）",
      "level": "build",
      "conditions": "dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "2e97f49de2cf518858dc9b055cad19705c898ad6a6fe809b25fff6308bf9a62c"
    },
    {
      "id": "baseline-build-testproject",
      "path": "_workflow/r56-mainline-integration/baseline/testproject-build.log",
      "purpose": "导入前测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）",
      "level": "build",
      "conditions": "dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "422a5a928cbc440f71843a4021eabc2059c90216fd07048306b572d492b8aaf8"
    },
    {
      "id": "baseline-targeted",
      "path": "_workflow/r56-mainline-integration/baseline/targeted-baseline.trx",
      "purpose": "导入前同条件基线：R56/R58 迁移定向三类 78/78（未导入 theory）",
      "level": "test",
      "conditions": "dotnet test … --no-build --filter R56MigrationSwitchTransactionTests|R58MigrationRehearsalTests|R58MigrationRehearsalHostTests",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "70112377e235f27312e7b0f0d1b210d956b323f89c2b5c6dfec103ec293c6368"
    },
    {
      "id": "baseline-full",
      "path": "_workflow/r56-mainline-integration/baseline/assistant-full-baseline.trx",
      "purpose": "导入前同条件基线：助手全量 1567 passed/2 skipped/0 failed/1569",
      "level": "test",
      "conditions": "dotnet test … --no-build（全部用例）",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "f99d1b76f3488f3c0bd1471f9ccf098de0f7d10203a57293c8196f7c9d7acb0a"
    },
    {
      "id": "final-build-testproject",
      "path": "_workflow/r56-mainline-integration/final/testproject-build.log",
      "purpose": "导入后测试项目 Rebuild（-p:DeployToBgiTools=false，exit 0，84 警告/0 错误）",
      "level": "build",
      "conditions": "dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "9996a02ecae226d7a01f4342df43076596dcd34c427fc112edf4e34914aa0733"
    },
    {
      "id": "targeted-final",
      "path": "_workflow/r56-mainline-integration/final/targeted-final.trx",
      "purpose": "集成后定向三类 81/81：三行新增 theory（extra/missing/changed）全部 Passed；风险矩阵 S1/C2/F1/F2 的反例证据",
      "level": "test",
      "conditions": "dotnet test … --no-build --filter 三类迁移夹具；exit 0；81 passed",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "573275689055e2de45836bb297d651f3a5a83bad6cfb427f7eeeb49c6b613039"
    },
    {
      "id": "full-final",
      "path": "_workflow/r56-mainline-integration/final/assistant-full-final.trx",
      "purpose": "集成后助手全量 1570 passed/2 skipped/0 failed/1572（含声明面守卫与全部既有夹具）",
      "level": "test",
      "conditions": "dotnet test … --no-build；exit 0",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "d41e8a5caa4152ac96065c8fd5d44e5cf0bf5aa7e566fc1b16c5298edf684d27"
    },
    {
      "id": "testid-comparison",
      "path": "_workflow/r56-mainline-integration/testid-comparison.json",
      "purpose": "testId 差集：定向 78→81（added=3/removed=0/changed=0/unchanged=78）；全量 1569→1572（added=3/removed=0/changed=0/unchanged=1569）",
      "level": "document",
      "conditions": "workflow.compare_trx 逐 testId 对照（同名不折叠）",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "7bc644e035b8ace5c179db075e519d9d8266ef2e46ca907a7ddd7191c187d0c8"
    },
    {
      "id": "deploy-target-before",
      "path": "_workflow/r56-mainline-integration/deploy-target-before.json",
      "purpose": "构建/测试前部署目标目录清单（1158 个文件 + 逐文件 SHA-256 + 清单摘要）",
      "level": "document",
      "conditions": "本批全部构建/测试开始前采集",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "fa2fdda663db7a8719bae60ec50d897451350fd1dac23ebf38e1a0f9b5b9e974"
    },
    {
      "id": "deploy-target-after",
      "path": "_workflow/r56-mainline-integration/deploy-target-after.json",
      "purpose": "构建/测试后部署目标目录清单（与前后对比使用）",
      "level": "document",
      "conditions": "本批全部构建/测试之后采集",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "6ef3e2f8230e06acbd54222bd3cc68464059665f927af4eb98cba5dc693c7a3b"
    },
    {
      "id": "deploy-target-comparison",
      "path": "_workflow/r56-mainline-integration/deploy-target-comparison.json",
      "purpose": "部署目标前后清单逐项相同（identical=true，1158→1158）",
      "level": "document",
      "conditions": "前后清单摘要对比",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "797c454e0f6f146961697d03f428f87c0dd388fc3614524933f3c12dc094435e"
    },
    {
      "id": "claim-manifest-before",
      "path": "_workflow/r56-mainline-integration/claims/manifest-before.txt",
      "purpose": "声明面再生前清单（618 行），用于评审清单差异",
      "level": "document",
      "conditions": "R5.3 §24.128 追加后、再生前",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "1f2b674365c9f150a0c4e898a54e586177283e07fa5bfb83bd2f4cbb786a5ccc"
    },
    {
      "id": "claim-preguard",
      "path": "_workflow/r56-mainline-integration/claims/claim-preguard.trx",
      "purpose": "声明面守卫在追加 §24.128 后按预期失败（新增/变更 1 行、移除 0 行）——证明本批触及声明面、不得援引措辞类豁免",
      "level": "test",
      "conditions": "dotnet test … --filter FullyQualifiedName~ClaimSurfaceGuardTests（不带 CLAIM_SURFACE_REGENERATE）；exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "3ed9bd4ab09856f67fd4af4a18176f1d9af2c549906afb5999e0fca54643be38"
    },
    {
      "id": "claim-regen",
      "path": "_workflow/r56-mainline-integration/claims/claim-regen.trx",
      "purpose": "带 CLAIM_SURFACE_REGENERATE=1 再生清单并守卫通过（618→619 行，+1/-0）",
      "level": "test",
      "conditions": "dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "390ad177db2fca30ea9b20c8288598e015af5f873e927706862e3c37a549670d",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "229274fa674a819cc7be6ffdecab344e06d078396ab345783bb6577aa379f2c4"
    },
    {
      "id": "claim-noenv",
      "path": "_workflow/r56-mainline-integration/claims/claim-noenv.trx",
      "purpose": "清除 CLAIM_SURFACE_REGENERATE 后复跑守卫通过（清单 SHA 28567E75…FF52，619 行）",
      "level": "test",
      "conditions": "dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0；绑定 §24.128.1–.5 的文档状态（§24.128.6 追加后将再次再生复跑）",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "17daa0d26217b1ac8ce7fb988ea97cad72cd1e2559ca300c4508a850dd618336"
    },
    {
      "id": "claims-diff",
      "path": "_workflow/r56-mainline-integration/claims/claims-diff.json",
      "purpose": "声明面清单差异摘要（新增/移除行数与清单 SHA 前后值）",
      "level": "document",
      "conditions": "man anifest-before/after 逐行对照",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "a560c6b23d16531e59e482580e2b407a7d0917d117222b973a5d32ce56bdcd5f"
    },
    {
      "id": "mutation-records",
      "path": "_workflow/r56-mainline-integration/mutations/mutation-records.json",
      "purpose": "本批 5 项反向突变的机读记录（原/突变/恢复哈希、三段 TRX、具名 testId、命中断言行、退出码）",
      "level": "document",
      "conditions": "由 mutations/run-mutations.py 依来源突变定义重跑并落盘",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "ad6417197e7c7896d1135b3a98828a03336475efbc0e39c83ee75c6dc1c6de7c"
    },
    {
      "id": "mutation-crosscheck",
      "path": "_workflow/r56-mainline-integration/mutations/mutation-hash-crosscheck.json",
      "purpose": "与来源 records.json/source-hashes.txt 的 mutant 哈希交叉核对（4/5 CRLF 形态完全相同；extra 为等价独立突变）",
      "level": "document",
      "conditions": "逐突变 CRLF/LF 形态哈希比对",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "ad2fdde105f107a13d6a2328df55b81557878bfed0112800bbb030de038913af"
    },
    {
      "id": "mutation-runner",
      "path": "_workflow/r56-mainline-integration/mutations/run-mutations.py",
      "purpose": "本批突变执行脚本（needle/anchor/断言行/恢复校验），供复查复现",
      "level": "document",
      "conditions": "Python；不改产品源码之外的任何内容，逐次 finally 恢复",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "596da1317409956a16a3c4dc35fc5fb82867e24fe59da195d8aa9e3781bd630d"
    },
    {
      "id": "mut-extra",
      "path": "_workflow\\r56-mainline-integration\\mutations\\extra\\mutant.trx",
      "purpose": "反向突变 extra 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）",
      "level": "test",
      "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "59eb823584a3d52b7231e0b319b5f5ed99db702f4e29972211d93d6712fc062a",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "d09afb4ed83de425fba4018eb31f5403789da697d4542ab151b02b0511d285a7"
    },
    {
      "id": "restored-extra",
      "path": "_workflow\\r56-mainline-integration\\mutations\\extra\\restored.trx",
      "purpose": "反向突变 extra 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
      "level": "test",
      "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "c418c574f682d201758ceef7c6cdab57adafd0d3129729ce67f464ad54ba260e"
    },
    {
      "id": "mut-extra-log",
      "path": "_workflow\\r56-mainline-integration\\mutations\\extra\\mutant.log",
      "purpose": "反向突变 extra 的执行日志（构建与测试输出、退出码）",
      "level": "document",
      "conditions": "dotnet test 全量输出重定向",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "59eb823584a3d52b7231e0b319b5f5ed99db702f4e29972211d93d6712fc062a",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "7f340dd8d976a78e3a5a928d8ecdc5006723c383872f9d6e838e3aee4701334d"
    },
    {
      "id": "mut-missing",
      "path": "_workflow\\r56-mainline-integration\\mutations\\missing\\mutant.trx",
      "purpose": "反向突变 missing 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）",
      "level": "test",
      "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "71520d42192a9788d715f693a47da67d5e3d4999a9913ad5e900f1150fbd2bb9",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "df99b2e324c35898e22a36b9dd1fa58240bd66f8dbd3c832d024a1bc61dbbfcb"
    },
    {
      "id": "restored-missing",
      "path": "_workflow\\r56-mainline-integration\\mutations\\missing\\restored.trx",
      "purpose": "反向突变 missing 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
      "level": "test",
      "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "8da6955f0042b4dbf4a01f6ff3133d56acfba8ae3d4f5b45cfe03e03c5f6fca5"
    },
    {
      "id": "mut-missing-log",
      "path": "_workflow\\r56-mainline-integration\\mutations\\missing\\mutant.log",
      "purpose": "反向突变 missing 的执行日志（构建与测试输出、退出码）",
      "level": "document",
      "conditions": "dotnet test 全量输出重定向",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "71520d42192a9788d715f693a47da67d5e3d4999a9913ad5e900f1150fbd2bb9",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "980376147e322446ef955a2beef13b92ff0f3eabf766d8b9271ada438f93d483"
    },
    {
      "id": "mut-changed",
      "path": "_workflow\\r56-mainline-integration\\mutations\\changed\\mutant.trx",
      "purpose": "反向突变 changed 的 mutant TRX：目标 theory 行 Failed（命中 :line 188）",
      "level": "test",
      "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c4af90bb0a6da79dd529e7bbb8d1717800e23fb78658612952b7288525a77ab2",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "b88e5b70e27ed1f193cebc9545b3f81fc6d998fd1961dd829066ed3d6b68c1fa"
    },
    {
      "id": "restored-changed",
      "path": "_workflow\\r56-mainline-integration\\mutations\\changed\\restored.trx",
      "purpose": "反向突变 changed 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
      "level": "test",
      "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "505f451598bc1033fab42b5e57500467b081c73a7ada3ec82d5e5de0874ff2e4"
    },
    {
      "id": "mut-changed-log",
      "path": "_workflow\\r56-mainline-integration\\mutations\\changed\\mutant.log",
      "purpose": "反向突变 changed 的执行日志（构建与测试输出、退出码）",
      "level": "document",
      "conditions": "dotnet test 全量输出重定向",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c4af90bb0a6da79dd529e7bbb8d1717800e23fb78658612952b7288525a77ab2",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "3e44e497de2b97984239a7698b566f0a3d7f219828499ec4c0ea0425e2c31b70"
    },
    {
      "id": "mut-commit-refusal",
      "path": "_workflow\\r56-mainline-integration\\mutations\\commit-refusal\\mutant.trx",
      "purpose": "反向突变 commit-refusal 的 mutant TRX：目标 theory 行 Failed（命中 :line 190）",
      "level": "test",
      "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "3c7ae396e7af96071fbce3a542deb67e5ede7b3eceec67f87ca9cb9c9103468e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "b48d090c49ce6ba5da9d6bd3783761fb699cc87033622f97bf527be56fc583bc"
    },
    {
      "id": "restored-commit-refusal",
      "path": "_workflow\\r56-mainline-integration\\mutations\\commit-refusal\\restored.trx",
      "purpose": "反向突变 commit-refusal 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
      "level": "test",
      "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "b9b1963a26a6d9232d09a8a789ecdaea868e48bde2dfc4f4cdf59c22fe9f12e0"
    },
    {
      "id": "mut-commit-refusal-log",
      "path": "_workflow\\r56-mainline-integration\\mutations\\commit-refusal\\mutant.log",
      "purpose": "反向突变 commit-refusal 的执行日志（构建与测试输出、退出码）",
      "level": "document",
      "conditions": "dotnet test 全量输出重定向",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "3c7ae396e7af96071fbce3a542deb67e5ede7b3eceec67f87ca9cb9c9103468e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "790f6ca5f454650613fbcc29996e8d7aab53c978a00988b17098a1ceb1c759c9"
    },
    {
      "id": "mut-production-gate",
      "path": "_workflow\\r56-mainline-integration\\mutations\\production-gate\\mutant.trx",
      "purpose": "反向突变 production-gate 的 mutant TRX：目标 theory 行 Failed（命中 :line 196）",
      "level": "test",
      "conditions": "dotnet test … --filter SnapshotIntegrity_…（未 --no-build，含重建）；mutant 构建 exit 0、测试 exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "6e14a630fac7183f9738aac218aacf3794a6e0c872dd0e8a758aa3af41e0194e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "2fbf51a4d512fb1810f1ad3c0423198f273413077440a795df99c5ee789dbeec"
    },
    {
      "id": "restored-production-gate",
      "path": "_workflow\\r56-mainline-integration\\mutations\\production-gate\\restored.trx",
      "purpose": "反向突变 production-gate 的 restored TRX：源码逐字节恢复后目标 theory 3/3 Passed",
      "level": "test",
      "conditions": "同命令；恢复后 exit 0；恢复哈希 = 原始哈希 C2D4A122…",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "4c9989b71a3dc807bf5d8978201eadd78bc477274a2211b04885742d77f35f10"
    },
    {
      "id": "mut-production-gate-log",
      "path": "_workflow\\r56-mainline-integration\\mutations\\production-gate\\mutant.log",
      "purpose": "反向突变 production-gate 的执行日志（构建与测试输出、退出码）",
      "level": "document",
      "conditions": "dotnet test 全量输出重定向",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "6e14a630fac7183f9738aac218aacf3794a6e0c872dd0e8a758aa3af41e0194e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "5bdfaa415d81b4d139e8097eff1b04722f2d8a97add37534e8044a032ba325f0"
    },
    {
      "id": "subagent-readonly-audit",
      "path": "_workflow/r56-mainline-integration/subagent/readonly-audit-report.md",
      "purpose": "固定开工 ref 的只读子 Agent 报告：核对交付 A 覆盖矩阵 7 行与主线实现一致、TryRunProduction 无外部调用方、A–F 无一项已接线，并列出导入 theory 的 6 处断言强度边界与 1 处待判定授权/快照交错（F-4）、1 处行号漂移（F-5）",
      "level": "document",
      "conditions": "子 Agent 只读执行（未构建/未测试/未写文件），固定 ref = 开工 HEAD 8a3ee6c4c，与 opening.json 源哈希一致；主执行者已按最终源码复核 F-4/F-5 关键行",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "9f600a90d2e309fdd5d9847aa159d3e74b9cec819684406b63575bf847d53e0f"
    },
    {
      "id": "targeted-final-v2",
      "path": "_workflow/r56-mainline-integration/final/targeted-final-v2.trx",
      "purpose": "会诊修复后的集成定向三类 81/81（最终文档版本；风险矩阵 S1/C2/F1/F2 的反例证据）",
      "level": "test",
      "conditions": "dotnet test … --no-build --filter 三类迁移夹具；exit 0；81 passed",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "cbe4a4a4eedc64f120025244f4034e517c9eb47642357cd18347d6cbde7e9538"
    },
    {
      "id": "full-final-v2",
      "path": "_workflow/r56-mainline-integration/final/assistant-full-final-v2.trx",
      "purpose": "会诊修复后的助手全量 1570 passed/2 skipped/0 failed/1572（含声明面守卫与全部既有夹具）",
      "level": "test",
      "conditions": "dotnet test … --no-build；exit 0",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "02f60577caae3ac007ea7fc3b1eb2662ca57ad48e17ee1a4690505e77d83c1f4"
    },
    {
      "id": "claim-postconsult-preguard",
      "path": "_workflow/r56-mainline-integration/claims/claim-postconsult-preguard.trx",
      "purpose": "追加 §24.128.6 与其计数更正后，声明面守卫按预期失败（新增/变更 2 行、移除 0 行）——本批再次触及声明面",
      "level": "test",
      "conditions": "dotnet test … --filter ClaimSurfaceGuardTests（不带再生变量）；exit 1",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "0808b1d2808b381c8f735050e6a575bc1d3f3f9a063e371619d1eb7f20d9a431"
    },
    {
      "id": "claim-postconsult-regen",
      "path": "_workflow/r56-mainline-integration/claims/claim-postconsult-regen.trx",
      "purpose": "带 CLAIM_SURFACE_REGENERATE=1 再生（619→621 行，+2/-0）",
      "level": "test",
      "conditions": "dotnet test … --filter ClaimSurfaceGuardTests（带再生变量）；exit 0",
      "binding": "historical",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "ba71a6323ef64a92f8385256102a2bc957c4a7e82d15521d2423d943ee0ab07d"
    },
    {
      "id": "claim-postconsult-noenv",
      "path": "_workflow/r56-mainline-integration/claims/claim-postconsult-noenv.trx",
      "purpose": "清除再生变量后复跑守卫通过（清单 621 行、SHA-256 543A5687…EE37）——最终权威清单",
      "level": "test",
      "conditions": "dotnet test … --filter ClaimSurfaceGuardTests（已清除变量）；exit 0",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "2c7203ab7ba7dd770bf5fd16d7c0c77db71a95327e0ff1a6a6323eac8d9dc2ae"
    },
    {
      "id": "claim-manifest-final",
      "path": "_workflow/r56-mainline-integration/claims/manifest-final.txt",
      "purpose": "本批最终声明面清单（621 行）",
      "level": "document",
      "conditions": "再生并复跑通过后的清单副本",
      "binding": "current",
      "source_sha256": {
        "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs": "4c101ff385b3dd25b96e062d69d985b39721ad07806dde9ca30e19c1c3e7b5ed",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs": "c2d4a1224668e39e536a9dd0644af665f1d805d3860f4a0d3d24926620dc807e",
        "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs": "6a72e076a0e38a96326f1fc44bd4bf8b1663f7deaa763819956ba486973181e9"
      },
      "file_sha256": "543a5687b9b75c263d2dcdab21bfd0fc24ac41c36c0f24499a09614d1c54ee37"
    }
  ],
  "quality_verdict": "NOT PROVIDED"
}

## git status --porcelain (all changes; ownership requires manual classification)
 M Docs/design/mistletoe-session-relay-2026-09-24.md
 M Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md
 M Docs/design/unified-job-registry-master-plan.md
 M Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt
M  Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs
 M _batch21/b21_plan.md
 M _batch21/sb21-4-handoff-2026-09-28.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance-matrix.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/acceptance.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/artifact-inventory.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/contact-inventory.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/delivery-registration.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/acceptance-history-pre-edit.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/boundary-correction.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/correction-pre-edit-inventory.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/correction-source-recheck.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-boundary.exitcode.txt
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-boundary.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/delivery-discovery-open.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/discovery-chronology.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/pre-edit-inventory.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/evidence/search-consumer-audit.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/report.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_r56_activation_prep/source-hashes.json
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/context.md
A  _workflow/r56-mainline-integration/source-delivery/r56-activation-prep/_workflow/manifest.json
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/acceptance.md
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/pre-edit-inventory.json
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/report.md
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/Run-AssertionMutations.ps1
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/followup/results.json
A  _workflow/r56-mainline-integration/source-delivery/r56-migration-audit/_r56_parallel/reverse-mutation/source-hashes.txt
 M 槲寄生调度器总计划.md
?? .zcodeignore
?? AGENTS.md.bak-20260926-142346
?? AGENTS.md.bak-20260926-1438
?? AGENTS.md.bak-20260927-brake
?? AGENTS.md.bak-auto-handoff-20260927
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoAutoEqDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/ArlecchinoBurstGateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/AutoFightTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/CombatHealthDetector.cs.bak
?? BetterGenshinImpact/GameTask/AutoFight/Model/Avatar.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialAutoFightRouter.cs.bak
?? BetterGenshinImpact/GameTask/AutoFightOfficial/OfficialParamAdapter.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateDecisions.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/CameraRotateTask.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/MiniMapPositionDiagnostics.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/PathExecutor.cs.bak
?? BetterGenshinImpact/GameTask/AutoPathing/ZeroCoordGuard.cs.bak
?? BetterGenshinImpact/GameTask/Common/CaptureRetryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/FocusRecoveryDecisions.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak
?? BetterGenshinImpact/GameTask/Common/TaskControl.cs.bak2
?? Docs/design/mistletoe-parallel-recovery-2026-09-27.md
?? Docs/design/mistletoe-r62-registration-2026-09-27.md
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r4_doc
?? Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md.bak_b16r5_1
?? MultiplayerHoeingAssistant.dll
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitModels.cs.bak_b16r4_2
?? MultiplayerHoeingAssistant/Models/TaskCenter/LocalWaitPrerequisiteModels.cs.bak_b16r4_1
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartBugConditionTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/GameTaskTests/AutoPathingTests/DeathRespawnRestartPreservationPbtTest.cs.stale
?? Test/BetterGenshinImpact.UnitTest/TestResults/
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_10
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_11
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_12
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_13
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_3
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_4
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_5
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_6
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_7
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_8
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitPrerequisiteContractTests.cs.bak_b16r4_9
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationModels.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTrigger.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.r13snap
?? Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitReevaluationTriggerTests.cs.wip-batch16
?? Test/MultiplayerHoeingAssistant.UnitTest/TestResults/
?? TestResults/
?? _aout.txt
?? _assist_xamlcheck.log
?? _audit1.txt
?? _audit2.txt
?? _audit3.txt
?? _audit4.txt
?? _audit5.txt
?? _audit6.txt
?? _backup_assistant-config.json
?? _backup_dodoco_settings.json
?? _batch13/
?? _batch14/
?? _batch14_admissionkind_hits.txt
?? _batch15/
?? _batch16/
?? _batch17/
?? _batch19/b19_commit_verify.txt
?? _batch20/
?? _batch21/b21_red.trx
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/evidence.json
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/mutant.log
?? _batch21/sb21-1-r8-review/accepted-no-job-inflight-mutant2/restored.log
?? _batch21/sb21-1-r8-review/accepted-no-job-mutant/
?? _batch21/sb21-1-r8-review/accepted-no-job/
?? _batch21/sb21-1-r8-review/assistant-full-post-r8-2.log
?? _batch21/sb21-1-r8-review/assistant-full-post-r8.log
?? _batch21/sb21-1-r8-review/claim-surface-final-docs/
?? _batch21/sb21-1-r8-review/claim-surface-final-docs2/
?? _batch21/sb21-1-r8-review/claim-surface-final-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-manifest-before.txt
?? _batch21/sb21-1-r8-review/claim-surface-post-r16-no-env/
?? _batch21/sb21-1-r8-review/claim-surface-regen/
?? _batch21/sb21-1-r8-review/claim-surface-verify/
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-identity-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-reference-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-bo1-scope-catch-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-boundary-hold-precedence-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-integrity-guard-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-ev1-two-scans-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-resume-control-after-write-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-successor-ranking-mapping-restored.trx
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-unresolved-send-attempted-restored.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-mutant.log
?? _batch21/sb21-1-r8-review/current-mutant-results/r8-wait-reason-sanitization-restored.log
?? _batch21/sb21-1-r8-review/doc-relay-before-inventory.txt
?? _batch21/sb21-1-r8-review/final-full/
?? _batch21/sb21-1-r8-review/git-diff.txt
?? _batch21/sb21-1-r8-review/git-status.txt
?? _batch21/sb21-1-r8-review/ledger-batch21-before-sb21-1-closeout-refresh.json
?? _batch21/sb21-1-r8-review/ledger-batch21-pre-disposition.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/batch51-r16-design-excerpt.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/committed-sb21-1.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-scoped-working-tree.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-staged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-status-porcelain.txt
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/current-unstaged.diff
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/ledger-batch21-before-r9-update.json
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/r8-findings-and-dispositions.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/review-context.md
?? _batch21/sb21-1-r8-review/post-cap-gpt-review/sb21-1-design-closeout-excerpt.txt
?? _batch21/sb21-1-r8-review/pre-accepted-fact-guard-inventory.txt
?? _batch21/sb21-1-r8-review/pre-repair-source-inventory.txt
?? _batch21/sb21-1-r8-review/previous-reverse-mutants.md
?? _batch21/sb21-1-r8-review/relay-before-final-refresh/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/catch-summary.json
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-identity-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-balanced/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-bo1-scope-catch-valid/
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-ev1-two-scans-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/mutant.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/r8-successor-ranking-mapping-valid/restored.log
?? _batch21/sb21-1-r8-review/repaired-mutant-results/summary.json
?? _batch21/sb21-1-r8-review/reverse-mutation-evidence.md
?? _batch21/sb21-1-r8-review/review-context.md
?? _batch21/sb21-1-r8-review/send-attempted-current-mutant/
?? _batch21/sb21-1-r8-review/targeted-after-r8.log
?? _batch21/sb21-1-reverse-mutants/
?? _batch21/sb21-2-review/assistant-full-baseline/
?? _batch21/sb21-2-review/assistant-full-final/
?? _batch21/sb21-2-review/assistant-full-r5-counter-final/
?? _batch21/sb21-2-review/assistant-full-r5-final/
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes.log
?? _batch21/sb21-2-review/assistant-full-r5-note-fixes/
?? _batch21/sb21-2-review/assistant-full-r5-original-parser-final/
?? _batch21/sb21-2-review/assistant-full-red-baseline/
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.json
?? _batch21/sb21-2-review/assistant-full-test-diff-r4.md
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.json
?? _batch21/sb21-2-review/assistant-full-test-diff-strict-parser-r5.md
?? _batch21/sb21-2-review/assistant-full-test-diff.json
?? _batch21/sb21-2-review/assistant-full-test-diff.md
?? _batch21/sb21-2-review/claim-final-noenv/
?? _batch21/sb21-2-review/claim-final-regen/
?? _batch21/sb21-2-review/claim-manifest-before-r5-closeout.txt
?? _batch21/sb21-2-review/claim-manifest-before-r5-finalize.txt
?? _batch21/sb21-2-review/claim-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-closeout-final-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-noenv/
?? _batch21/sb21-2-review/claim-r5-closeout-regen/
?? _batch21/sb21-2-review/claim-r5-counters-final-noenv/
?? _batch21/sb21-2-review/claim-r5-counters-final-regen/
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv.log
?? _batch21/sb21-2-review/claim-r5-current-docs-noenv/
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry.log
?? _batch21/sb21-2-review/claim-r5-exact-final-noenv-retry/
?? _batch21/sb21-2-review/claim-r5-exact-final-regen.log
?? _batch21/sb21-2-review/claim-r5-exact-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-noenv/
?? _batch21/sb21-2-review/claim-r5-final-regen/
?? _batch21/sb21-2-review/claim-r5-final-state-noenv.log
?? _batch21/sb21-2-review/claim-r5-final-state-noenv/
?? _batch21/sb21-2-review/claim-r5-noenv.log
?? _batch21/sb21-2-review/claim-r5-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-noenv/
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen.log
?? _batch21/sb21-2-review/claim-r5-r5disposition-final-regen/
?? _batch21/sb21-2-review/claim-r5-regen.log
?? _batch21/sb21-2-review/claim-r5-regen/
?? _batch21/sb21-2-review/claim-regen/
?? _batch21/sb21-2-review/claim-review-noenv/
?? _batch21/sb21-2-review/claim-review-regen/
?? _batch21/sb21-2-review/consultation-ledger-before-r4-record.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-dispatch.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/consultation-ledger-pre-r5-update.json
?? _batch21/sb21-2-review/implementation-pre-edit-inventory.json
?? _batch21/sb21-2-review/item-generation-overflow-diagnostic/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v2/
?? _batch21/sb21-2-review/item-generation-overflow-fixed-v3/
?? _batch21/sb21-2-review/item-generation-overflow-fixed/
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/LocalWaitQueueStore.strict-current.cs
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-nonincr-build.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/original-source-overflow-test.log
?? _batch21/sb21-2-review/item-generation-overflow-original-source-probe/test-results/
?? _batch21/sb21-2-review/ledger-pre-r5-finalization.json
?? _batch21/sb21-2-review/ledger-pre-r5-update.json
?? _batch21/sb21-2-review/legacy-gap-v2/
?? _batch21/sb21-2-review/localwait-r5-counter/
?? _batch21/sb21-2-review/localwait-r5-final/
?? _batch21/sb21-2-review/localwait-r5-note-fixes.log
?? _batch21/sb21-2-review/localwait-r5-note-fixes/
?? _batch21/sb21-2-review/localwait-r5-original-parser-final/
?? _batch21/sb21-2-review/localwait-suite-final-pre-docs/
?? _batch21/sb21-2-review/localwait-suite-final/
?? _batch21/sb21-2-review/localwait-suite-v1/
?? _batch21/sb21-2-review/mutations-r3/legacy-reservation/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v2/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow-v3/
?? _batch21/sb21-2-review/mutations-r4/item-generation-overflow/
?? _batch21/sb21-2-review/mutations-r5/c5-consume-generation/
?? _batch21/sb21-2-review/original-parser-confirmation-build.log
?? _batch21/sb21-2-review/original-parser-confirmation/
?? _batch21/sb21-2-review/pre-edit-inventory.json
?? _batch21/sb21-2-review/pre-implementation-inventory.json
?? _batch21/sb21-2-review/pre-r5-closeout/b21_plan-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/handoff-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/r5-3-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/red-results-summary-before-r5.md
?? _batch21/sb21-2-review/pre-r5-closeout/reverse-mutations-before-r5.md
?? _batch21/sb21-2-review/r4-item-overflow-fix-pre-edit.json
?? _batch21/sb21-2-review/r5_3_anchor_excerpt.md
?? _batch21/sb21-2-review/r5_3_sb21_2_current_excerpt.md
?? _batch21/sb21-2-review/red-final-before-implementation-v2/
?? _batch21/sb21-2-review/red-final-before-implementation/
?? _batch21/sb21-2-review/red-r1/
?? _batch21/sb21-2-review/red-r2/
?? _batch21/sb21-2-review/red/
?? _batch21/sb21-2-review/review-r5-material-out-staged.diff
?? _batch21/sb21-2-review/targeted-final/
?? _batch21/sb21-2-review/targeted-r5-counter/
?? _batch21/sb21-2-review/targeted-r5-generation-final/
?? _batch21/sb21-2-review/targeted-r5-generation/
?? _batch21/sb21-2-review/targeted-r5-note-fixes.log
?? _batch21/sb21-2-review/targeted-r5-note-fixes/
?? _batch21/sb21-2-review/targeted-r5-original-parser-final/
?? _batch21/sb21-2-review/targeted-v1/
?? _batch21/sb21-2-review/targeted-v2/
?? _batch21/sb21-2-review/targeted-v3/
?? _batch21/sb21-2-review/test-project-build-r5-counter-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-note-fixes-nonincr.log
?? _batch21/sb21-2-review/test-project-build-r5-original-parser.log
?? _batch21/sb21-3-review/
?? _c16out.txt
?? _dpiprobe/
?? _extprobe/
?? _fixhash.py
?? _incidentprobe/
?? _log.py
?? _mergebuild.log
?? _mergebuild2.log
?? _mergebuild3.log
?? _mergebuild4.log
?? _mergebuild5.log
?? _mergebuild6.log
?? _mergebuild7.log
?? _mergebuild8.log
?? _mergebuild9.log
?? _probe/
?? _probe_taskline.png
?? _probe_taskline2.png
?? _r17.txt
?? _r5_batch9_assistant_full.log
?? _r5_test_temp/
?? _statusprobe/
?? _styleprobe/
?? _tools/
?? _uidmask_preview.png
?? _wf.py
?? _workflow/import-integration/
?? _workflow/import-pilot-r56/
?? _workflow/parallel-recovery/
?? _workflow/parallel-registry-integration/
?? _workflow/r56-mainline-integration/baseline/
?? _workflow/r56-mainline-integration/budget.md
?? _workflow/r56-mainline-integration/build_manifest.py
?? _workflow/r56-mainline-integration/claims/
?? _workflow/r56-mainline-integration/consultation/
?? _workflow/r56-mainline-integration/context.md
?? _workflow/r56-mainline-integration/deploy-target-after.json
?? _workflow/r56-mainline-integration/deploy-target-before.json
?? _workflow/r56-mainline-integration/deploy-target-comparison.json
?? _workflow/r56-mainline-integration/final/
?? _workflow/r56-mainline-integration/findings.md
?? _workflow/r56-mainline-integration/intake-equivalence.json
?? _workflow/r56-mainline-integration/manifest.json
?? _workflow/r56-mainline-integration/mutations/
?? _workflow/r56-mainline-integration/opening.json
?? _workflow/r56-mainline-integration/review-v1/
?? _workflow/r56-mainline-integration/review-v2/
?? _workflow/r56-mainline-integration/risk-matrix.json
?? _workflow/r56-mainline-integration/source-delivery-line-endings.json
?? _workflow/r56-mainline-integration/source-delivery/INTAKE-NOTE.md
?? _workflow/r56-mainline-integration/subagent/
?? _workflow/r56-mainline-integration/testid-comparison.json
?? _workflow/r62-registration/
?? _workflow/sb21-3-bo4/
?? _workflow/sb21-4/baseline/
?? _workflow/sb21-4/claims/
?? _workflow/sb21-4/closeout-final-20260928-v2/
?? _workflow/sb21-4/closeout-final-20260928-v3/
?? _workflow/sb21-4/closeout-final-20260928-v4/scoped-staged.diff
?? _workflow/sb21-4/closeout/
?? _workflow/sb21-4/consultation-budget.md
?? _workflow/sb21-4/deliveries/capture-parallel-index-diff-r7.py
?? _workflow/sb21-4/deliveries/closeout-discovery-r3.json
?? _workflow/sb21-4/deliveries/final-closeout-r1.exit-code
?? _workflow/sb21-4/deliveries/final-closeout-r1.json
?? _workflow/sb21-4/deliveries/final-closeout-r2.json
?? _workflow/sb21-4/deliveries/final-closeout-r3.json
?? _workflow/sb21-4/deliveries/final-r7-discovery.json
?? _workflow/sb21-4/deliveries/natural-boundary-final-r2.json
?? _workflow/sb21-4/deliveries/natural-boundary-v2.json
?? _workflow/sb21-4/deliveries/natural-r7-continuation.json
?? _workflow/sb21-4/deliveries/natural-r7.json
?? _workflow/sb21-4/deliveries/parallel-index-json-malformed-pre-fix.bin
?? _workflow/sb21-4/deliveries/parallel-index-r7-diff.patch
?? _workflow/sb21-4/deliveries/parallel-index-r7-hashes.json
?? _workflow/sb21-4/deliveries/post-completion-registered-r1.json
?? _workflow/sb21-4/deliveries/post-registration-r2.json
?? _workflow/sb21-4/deliveries/post-registry-update-r1.json
?? _workflow/sb21-4/deliveries/pre-r7-registration/
?? _workflow/sb21-4/deliveries/pre-review-r2.json
?? _workflow/sb21-4/deliveries/r8-review-discovery.json
?? _workflow/sb21-4/deliveries/register-continuation-candidates.py
?? _workflow/sb21-4/deliveries/repair-parallel-index-r7.py
?? _workflow/sb21-4/evidence-closeout-r4/
?? _workflow/sb21-4/evidence-final-20260928-v1/
?? _workflow/sb21-4/evidence-final-20260928-v2/
?? _workflow/sb21-4/evidence-final-20260928-v3/
?? _workflow/sb21-4/evidence-final-20260928-v4/
?? _workflow/sb21-4/evidence-final-r2-retry1/
?? _workflow/sb21-4/evidence-final-r2-retry2/
?? _workflow/sb21-4/evidence-final-r2-retry3/
?? _workflow/sb21-4/evidence-final-r2-retry4/
?? _workflow/sb21-4/evidence-final-r2-retry5/
?? _workflow/sb21-4/evidence-postcommit-r1/
?? _workflow/sb21-4/evidence-postcommit-r2/
?? _workflow/sb21-4/evidence-pre-review-v2/
?? _workflow/sb21-4/evidence-pre-review-v3/
?? _workflow/sb21-4/evidence-pre-review/
?? _workflow/sb21-4/evidence-r5-05b/
?? _workflow/sb21-4/evidence-r5-05c/
?? _workflow/sb21-4/evidence-r5-05d/
?? _workflow/sb21-4/evidence-r8-20260928-v1/
?? _workflow/sb21-4/evidence-r8-budget-fit-20260928-v1/
?? _workflow/sb21-4/evidence-r8-corrected-20260928-v1/
?? _workflow/sb21-4/final/
?? _workflow/sb21-4/mutations-review-r1-retry1/
?? _workflow/sb21-4/mutations-review-r1-retry2/
?? _workflow/sb21-4/mutations-review-r1/
?? _workflow/sb21-4/mutations/
?? _workflow/sb21-4/pre-closeout-refresh-20260928-0307/
?? _workflow/sb21-4/red/
?? _workflow/sb21-4/refresh_closeout_docs.py
?? _workflow/sb21-4/review-packet-r8-final-20260928-v1/
?? _workflow/sb21-4/review-prep-final/
?? _workflow/sb21-4/review-prep-r2/
?? _workflow/sb21-4/review-r5-05d/
?? _workflow/sb21-4/review-snapshot-r2-retry3/
?? _workflow/sb21-4/review-snapshot-r2-retry4/
?? _workflow/sb21-4/review/committed-diff-afe84.patch
?? _workflow/sb21-4/review/gpt-r1-request.md
?? _workflow/sb21-4/review/gpt-r1-review.md
?? _workflow/sb21-4/review/gpt-r1-snapshot-v2/
?? _workflow/sb21-4/review/gpt-r1-snapshot-v3/
?? _workflow/sb21-4/review/gpt-r2-attempt1-failure.md
?? _workflow/sb21-4/review/gpt-r2-request.md
?? _workflow/sb21-4/review/gpt-r3-budget.md
?? _workflow/sb21-4/review/gpt-r3-objective.md
?? _workflow/sb21-4/review/gpt-r3-request.md
?? _workflow/sb21-4/review/gpt-r3-review.md
?? _workflow/sb21-4/review/gpt-r4-review.md
?? _workflow/sb21-4/review/gpt-r5-review.md
?? _workflow/sb21-4/review/gpt-r6-review.md
?? _workflow/sb21-4/review/gpt-r7-review.md
?? _workflow/sb21-4/review/ledger-closeout-before-owner-checkpoint.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-final.json
?? _workflow/sb21-4/review/ledger-owner-checkpoint-snapshot.json
?? _workflow/sb21-4/review/ledger-pre-r3-update.json
?? _workflow/sb21-4/review/ledger-r2-snapshot.json
?? _workflow/sb21-4/review/manifest-r3.json
?? _workflow/sb21-4/review/manifest-r3b.json
?? _workflow/sb21-4/review/manifest-r3c.json
?? _workflow/sb21-4/review/material-out-tracked.diff
?? _workflow/sb21-4/review/parallel-recovery-registration-2026-09-28.json
?? _workflow/sb21-4/review/parallel-recovery-registration-latest.json
?? _workflow/sb21-4/review/parallel-recovery-report-2026-09-28.md
?? _workflow/sb21-4/review/parallel-recovery-report-latest.md
?? _workflow/sb21-4/review/pre-edit-r3.json
?? _workflow/sb21-4/review/r3-baseline/
?? _workflow/sb21-4/review/r3-repair/
?? _workflow/sb21-4/review/r4-prep/
?? _workflow/sb21-4/review/r5-3-contract-extract.md
?? _workflow/sb21-4/review/r5-3-sb21-4-current.md
?? _workflow/sb21-4/review/r5-repair/
?? _workflow/sb21-4/review/r6-audit-evidence-v6/
?? _workflow/sb21-4/review/r6-repair/
?? _workflow/sb21-4/review/r6-review-20260928-v1/
?? _workflow/sb21-4/review/r6-review-snapshot-final/
?? _workflow/sb21-4/review/r6-review-snapshot-v2/
?? _workflow/sb21-4/review/r6-review-validation-final/
?? _workflow/sb21-4/review/r6-review-validation/
?? _workflow/sb21-4/review/r7-repair/
?? _workflow/sb21-4/review/r7-review-20260928-v1/
?? _workflow/sb21-4/review/r8-review-20260928-v1/.keep
?? _workflow/sb21-4/review/r8-review-20260928-v1/budget-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/findings-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-before-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-before-r8-sync.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/ledger-r7-sync-evidence.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m1-final-reread-runid-guard/
?? _workflow/sb21-4/review/r8-review-20260928-v1/mutations/r8-m2-tombstone-retry-idempotence-run2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/objective-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/parallel-deliveries-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/active-ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/assistant-full-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/bo13-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/builds-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-noenv/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-regen/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/claim-surface-final-closeout.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-after-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/edit-inventory-pre-final-docs.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v2/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v3/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source-v4/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/final-current-source/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/ledger-before-final-disposition.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-exact-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final-handoff/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-final/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-sb21-exact240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/localwait-scoped-240/
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/next-batch-prompt-final.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/test-project-build.log
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/testid-comparisons-postreview.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-closeout/validation-summary.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/postreview-regression/
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-comparisons-r8.json
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-opening-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/testid-diff-r6-to-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/validation-summary-r8.md
?? _workflow/sb21-4/review/r8-review-20260928-v1/workflow-preflight-correction.md
?? _workflow/sb21-4/review/redundant-mutant-exclusion.md
?? _workflow/sb21-4/review/snapshot-r3/
?? _workflow/sb21-4/review/snapshot-r3c/
?? _workflow/sb21-4/review/snapshot-r4f/
?? _workflow/sb21-4/review/snapshot-r4g/
?? _workflow/sb21-4/review/staged-supplement.patch
?? _workflow/sb21-4/review/status-at-r2-prep.txt
?? _workflow/sb21-4/review/supplemental-tracked-diffs.patch
?? _workflow/sb21-4/review/unstaged-supplement.patch
?? _workflow/sb21-4/settled-final-r2/
?? _workflow/sb21-4/settled-final-r3/
?? _workflow/sb21-4/settled-final-r4/
?? _workflow/sb21-4/write_manifest.py
?? _workflow/wave3-bo6bo7-receive/closeout-receive-20260928-v7/
?? _workflow/wave3-bo6bo7-receive/scripts/_fin.py
?? _workflow/wave3-bo8-bo9/claims-v2/
?? _workflow/wave3-bo8-bo9/claims-v3/
?? _workflow/wave3-bo8-bo9/closeout-20260928-v1/
?? _workflow/wave3-bo8-bo9/closeout-20260928-v2/
?? _workflow/wave3-bo8-bo9/deploy-target-after.txt
?? _workflow/wave3-bo8-bo9/final-v3/
?? _workflow/wave3-bo8-bo9/final-v7/
?? _workflow/wave3-bo8-bo9/final-v8/
?? _workflow/wave3-bo8-bo9/fixed-v2/
?? _workflow/wave3-bo8-bo9/fixed/
?? _workflow/wave3-bo8-bo9/mutation-records-final.json
?? _workflow/wave3-bo8-bo9/mutation-records-round2.json
?? _workflow/wave3-bo8-bo9/mutations-final/
?? _workflow/wave3-bo8-bo9/mutations-round2/
?? _workflow/wave3-bo8-bo9/mutations-run-final.log
?? _workflow/wave3-bo8-bo9/mutations-run-round2.log
?? _workflow/wave3-bo8-bo9/mutations-run.log
?? _workflow/wave3-bo8-bo9/mutations/
?? _workflow/wave3-bo8-bo9/review-v1/
?? _workflow/wave3-bo8-bo9/review-v2/
?? _workflow/wave3-bo8-bo9/scripts/_write_commit_record.py
?? build_final.log
?? build_head_output.txt
?? test_preservation.txt
?? testrun_preservation.log

## scoped unstaged diff

## scoped staged diff
diff --git a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs
index a7d6991cf..cd121220d 100644
--- a/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs
+++ b/Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs
@@ -147,6 +147,54 @@ public sealed class R56MigrationSwitchTransactionTests : IDisposable
         Assert.False(commit.Success);
         Assert.Equal("no_quiescence_window", commit.Reason);
     }
+    [Theory]
+    [InlineData("extra", "snapshot_untracked_file:rogue.json")]
+    [InlineData("missing", "snapshot_file_missing:a.json")]
+    [InlineData("changed", "snapshot_hash_mismatch:a.json")]
+    public void SnapshotIntegrity_CompleteFileSetAndBytes_AreVerifiedAndCommitFailsClosed(string mutation, string expectedReason)
+    {
+        Seed("a.json", "{\"v\":1}");
+        Seed("sub/b.json", "{\"w\":1}");
+        using var tx = NewTx();
+        Assert.True(tx.BeginTransaction("snapshot-integrity").Success);
+        Assert.True(tx.TakeSnapshot().Success);
+        Assert.True(tx.MarkReferenceUpdateCompleted().Success);
+        Assert.True(tx.MarkActivated().Success);
+        Assert.True(tx.RehearseRollback().Success);
+
+        var manifest = tx.LoadManifest()!;
+        Assert.Equal(new[] { "a.json", "sub/b.json" }, manifest.FileHashes.Keys.OrderBy(p => p, StringComparer.Ordinal));
+        Assert.Equal(HashOf(Full("a.json")), manifest.FileHashes["a.json"]);
+        Assert.Equal(HashOf(Full("sub/b.json")), manifest.FileHashes["sub/b.json"]);
+        Assert.Equal(manifest.SnapshotManifestHash, MigrationSwitchTransaction.ComputeSnapshotManifestHash(manifest.FileHashes));
+        Assert.Equal(File.ReadAllBytes(Full("a.json")), File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, "a.json")));
+        Assert.Equal(File.ReadAllBytes(Full("sub/b.json")), File.ReadAllBytes(Path.Combine(manifest.SnapshotPath, "sub", "b.json")));
+
+        switch (mutation)
+        {
+            case "extra":
+                File.WriteAllText(Path.Combine(manifest.SnapshotPath, "rogue.json"), "extra", new UTF8Encoding(false));
+                break;
+            case "missing":
+                File.Delete(Path.Combine(manifest.SnapshotPath, "a.json"));
+                break;
+            case "changed":
+                File.WriteAllText(Path.Combine(manifest.SnapshotPath, "a.json"), "tampered", new UTF8Encoding(false));
+                break;
+            default:
+                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
+        }
+
+        Assert.Equal(expectedReason, tx.VerifySnapshot());
+        var commit = tx.Commit();
+        Assert.False(commit.Success);
+        Assert.StartsWith("snapshot_invalid:", commit.Reason, StringComparison.Ordinal);
+        Assert.Equal(MigrationStage.Activated, tx.LoadManifest()!.Stage);
+        Assert.Null(tx.LoadManifest()!.CommitMarker);
+        var productionRuns = 0;
+        Assert.False(tx.TryRunProduction(() => productionRuns++).Success);
+        Assert.Equal(0, productionRuns);
+    }
 }
 /// <summary>v3 夹具续（与上同类同文件，此块补齐其余必改项覆盖）。</summary>
 public sealed class R56MigrationSwitchTransactionTests_Part2 : IDisposable

## materials outside this batch
本批只改：`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`（+48 行，逐字节导入）、`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt`（按 §17.4-A 强制再生）、本批证据目录 `_workflow/r56-mainline-integration/**`（含 `source-delivery/**` 摄入副本）、状态文档 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`、`槲寄生调度器总计划.md`、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`Docs/design/mistletoe-parallel-deliveries.md` 与同名 `.json`。材料外变更（非本批写入者，本批不触碰、不提交）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md`（两份既有未提交设计文档），以及工作区其余历史批次证据、`.bak`／`.stale`、日志、TestResults、DLL／工具输出与截图等未跟踪内容。
