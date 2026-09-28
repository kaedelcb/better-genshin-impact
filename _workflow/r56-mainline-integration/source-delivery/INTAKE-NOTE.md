# R5.6 主线迁移集成批 · 来源交付材料摄入说明（INTAKE-NOTE）

本目录是**主线摄入副本**，不是新的独立交付，也不构成验收。来源交付仍保留在其自己的 worktree：

| 来源交付 | 来源 worktree | 交付 HEAD | 本目录映射 |
|---|---|---|---|
| r56-migration-audit（核验 R5.6 迁移回滚组件） | `C:/Users/Administrator/.codex/worktrees/r56-migration-audit/better-genshin-impact-LCB` | `c11cb45f6de2436b46c58b91d47f2c9768132ef3` | `r56-migration-audit/_r56_parallel/` |
| r56-activation-prep（完成 R5.6 迁移接点审计） | `C:/Users/Administrator/.codex/worktrees/r56-activation-prep/better-genshin-impact-LCB` | `90588159b4769c41284ade475052d8b56f92e2d6` | `r56-activation-prep/_r56_activation_prep/`、`r56-activation-prep/_workflow/` |

**为什么改路径**：顶层 `_r*` 目录会被 `tools/mistletoe/deliveries.py` 的未登记报告扫描命中（exit 2）。本批是消费/集成，不应表现为新的未登记交付，故按批次证据目录收纳；路径映射与逐文件哈希见
[`../intake-equivalence.json`](../intake-equivalence.json) 与 [`../source-delivery-line-endings.json`](../source-delivery-line-endings.json)。

**逐字节**：25 个材料文件与导入的 1 个测试文件，其**暂存内容**与来源提交 blob 完全相同（26/26）。工作区字节在 25/25 材料文件上等于来源 worktree 现行字节；其中 4 个 JSON 在来源侧为 CRLF 工作区形态而 blob 为 LF，提交由 `core.autocrlf` 归一。

**未收录（刻意）**：来源 worktree 的未跟踪材料（`_r56_parallel/tmp/`、`delivery-discovery-*` 收口扫描原始输出、`_workflow/r56-activation-prep/*-run-1/`），以及来源的逐次 TRX、突变日志/补丁、进程崩溃 harness 与运行数据（本批在主线上重跑回归与反向突变，不搬重复中间材料）。

**边界**：材料并入主线 ≠ 集成完成 ≠ 验收。R5.6 的 A–F 生产前置（真实引用更新/candidate→active 激活、助手元数据恢复、生产检查点消费、真实静止窗口、真实入口回执、目标机路径身份）仍未闭合；生产入口门、真实 User 门、R5.8 签署与生产进程门继续关闭。见
[`../findings.md`](../findings.md) 与 R5.3 §24.128。
