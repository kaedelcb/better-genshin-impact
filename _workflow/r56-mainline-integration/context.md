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
