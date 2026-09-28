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
