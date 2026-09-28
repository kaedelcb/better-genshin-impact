<!-- 摘录来源：Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md L5190-L5229；全文 SHA-256 B179F0861608CECDC47A089F8690CA4A23F211AB1A3B8618BC4C84E7F7D29D66；本摘录仅含本批新增章节，语义完整，不含例外删减 -->
## §24.128 R5.6 主线迁移集成批（并行成果接收与集成验证，2026-09-28）

### §24.128.1 范围、接收对象与来源绑定

本批是**接收批**：把目标批次为「R5.6 主线迁移集成批」的两项已登记并行成果接入茶包主线，并在主线集成版本上重跑回归与反向突变。**不**施工 R5.6 生产接线（真实引用更新、candidate→active 激活编排、生产检查点消费、真实静止窗口、真实入口回执、目标机路径身份），**不**施工 R6.1 与 R6 diff guard，**不**重开 BO-8/BO-9、BO-6/7-D1、BO-9-D1、BO-13、SB21-3 BO-4、SB21-2 BO-10/12，**不**提前集成其他目标批次的成果。

- 来源交付 **r56-migration-audit**（thread `01a0e060-f824-7bb2-8490-d53b6e6dd88d`）：HEAD `c11cb45f6de2436b46c58b91d47f2c9768132ef3`，提交 `24928d294`／`97d05b931`／`ad248944e`／`c11cb45f6`；报告 `_r56_parallel/report.md` SHA-256 `044A62D0…9D97`、`acceptance.md` SHA-256 `377FC50C…55AE`（接收时逐字节复核一致）。本次重查其来源 thread 最新 turn 为 **completed**，其自身收口亦明确「后续主线集成属独立目标、本批未合入主线」。
- 来源交付 **r56-activation-prep**（thread `01a0e481-0b6d-74d0-a915-ac20cbaac861`）：HEAD `90588159b4769c41284ade475052d8b56f92e2d6`，提交 `0b606fe38`…`90588159b`；报告 SHA-256 `BEE48867…4FED`、`delivery-registration.json` SHA-256 `D8AAE0C5…90BF`（同上复核一致；最新 turn completed）。该包把 §21.10 启用门归并为 **A–F 六项前置**并给出正式消费批最小实施顺序。
- 两项的共同基线均早于本批开工 HEAD `8a3ee6c4c`（`8a4b8d988`／`5e7e7e22f`）：差异只涉及 SB21/Wave3 非迁移文件，迁移组件与 R56/R58 夹具逐 blob 相同（`blob_equal=true`），本批据此重核依赖版本后接收。

### §24.128.2 接收方式（最小且逐字节可核）

- **产品/测试增量**：唯一文件 `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs`，`git diff --numstat 8a3ee6c4c c11cb45f6` = `48 0`；以 `git checkout c11cb45f6 -- <path>` 导入后，主线 blob `cd121220…` 与来源 blob 完全相同，工作区文件 54228 字节、SHA-256 `4C101FF3…`，与来源 worktree 现行字节相同。
- **材料增量**（25 个文件；暂存内容 25/25 等于来源 blob，工作区字节 25/25 等于来源 worktree 现行字节）：`_r56_activation_prep/**`（18）、`_workflow/r56-activation-prep/{context.md,manifest.json}`（2）、`_r56_parallel/{report.md,acceptance.md,pre-edit-inventory.json,reverse-mutation/source-hashes.txt,reverse-mutation/followup/{results.json,Run-AssertionMutations.ps1}}`（6）。
- **收纳位置**：为避免顶层 `_r*` 目录被 `tools/mistletoe/deliveries.py` 的「未登记报告」扫描误判为新交付，来源材料在主线收纳于 `_workflow/r56-mainline-integration/source-delivery/`；路径映射、逐文件哈希与未收录清单见该目录 `INTAKE-NOTE.md`、`intake-equivalence.json`、`source-delivery-line-endings.json`。
- **刻意未收录**：来源 worktree 的未跟踪材料（`_r56_parallel/tmp/`、`delivery-discovery-*` 收口扫描原始输出、`_workflow/r56-activation-prep/*-run-1/`）与来源的逐次运行产物（TRX、突变日志、崩溃 harness 与运行数据）。回归与突变在主线上重跑，不搬重复中间材料。

### §24.128.3 集成版本证据（主线上重跑，不复用来源数字）

- **构建**：助手项目与测试项目 Rebuild 均带 `-p:DeployToBgiTools=false`，exit 0（助手 59 警告/0 错误；测试项目 84 警告/0 错误，导入前后各一次）。部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant` 在全部构建与测试前后清单逐项相同（1158 文件，`deploy-target-comparison.json` 判 `identical=true`）。
- **同条件基线**（导入前 HEAD `8a3ee6c4c`）：定向三类 **78/78**；助手全量 **1567 passed／2 skipped／0 failed／1569**。
- **集成后**：定向三类 **81/81**（三行 theory 的 testId `b1efc0c6-…`／`7481b2d1-…`／`bb416257-…` 全部 Passed）；助手全量 **1570／2／0／1572**；testId 差集 **added=3／removed=0／changed=0／unchanged=78**（定向）与 **added=3／removed=0／changed=0**（全量），见 `testid-comparison.json`。导入前基线与上一子批 `wave3-bo8-bo9` 自己的最终 TRX 逐 testId 相同（0 added/0 removed/0 changed）。
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
