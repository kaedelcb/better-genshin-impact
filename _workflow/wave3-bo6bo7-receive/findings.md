# 发现、处置与残余（findings）

## 本批性质

接收批（mode=code）：把来源独立交付的内容逐字节导入主线并**重跑**验证，不复用来源数字。

**导入保真（含接收后改动，逐项区分）**：导入动作的暂存增量 `git diff --cached -- <8 文件>` 与来源增量 `git diff e2613a851 e009068e2 -- <8 文件>` **逐字节相同**（两侧 SHA-256 均为 `0642971729f3eff0…`）；逐文件核对索引 blob 等于交付 blob，全部为真。因此**导入时**8 个文件与来源已审增量逐字节相同，无改写、无适配。

**但"逐字节相同"只是导入时点的事实，不是最终树的事实**：本批随后按收口要求对 4 个状态文档追加了登记内容（R5.3 §24.126、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划），并按 §17.4-A 第 1 条对 `ClaimSurfaceManifest.txt` 执行了强制再生。故最终工作区相对导入状态有改动；`verification/post-import-delta.diff` 完整列出这些改动，`verification/intake-equivalence.json` 逐文件记录"导入=交付"与"接收后是否修改"两项判定。**产品源码与两个夹具文件在接收后未被修改**（未暂存 diff 为空）。

## 主线工作区行尾与来源记录的对应

主线按 `core.autocrlf=true` 检出，工作区为 CRLF，仓库 blob 为 LF。两者内容逐字节相同（仅行尾差异）：
- `WorkflowRunner.cs`：仓库 blob（LF）`181aa93e000be99d8eed9ce192587a56194d270e47f67bd561297a57476f14c9`（即两名来源审查者核验时引用的"当前源码"），工作区（CRLF）`5470cfcb2a8792ca123d0ac2e46afc2c0046a96b293ca141a1517d1d1f190f96`。
- `ClaimSurfaceManifest.txt`：导入时（LF/blob 形态）`84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`；主线声明面再生后为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
本批 manifest 的所有 `source_sha256` 记录**工作区 CRLF 形态**，内部自洽；来源清单记录的是混合形态（original/restored 为 LF、mutant 为 CRLF），仅作对照。

## 发现 F-1（建议级 · 声明面版本引用陈旧）——已登记，不改写来源工件

**事实**：§24.125.3、`_workflow/wave3-bo6-bo7/owner-checkpoint.md`、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、总计划均把声明面清单 SHA 记为 `9EAA1A7F6EFAECCB763DA8EE10520E01B128F3129E6DBFB831295EA58CD083DC`；随交付提交的清单实际为 `84E1D93DCC4D8B4B13EEB82C8CD9C25163FF9C18E59E3F772184997528B6D10D`。

**已核清原因**：来源目录 `regression/claim-surface-post-consultation/` 记录 `manifest-before=9EAA1A7F…`、再生后 `manifest-after-regen=manifest-after-no-env=84E1D93D…`。即 `9EAA1A7F…` 是"修正 §24.125 状态/预算声明"那一步的中间值；其后新增的 §24.125.5（owner 批准追加会诊的闭环文本）再次改变声明面，来源侧执行了再生却未回改前述散文引用。

**处置**：不改写已与检查点登记哈希 `A4AB7747…` 绑定的来源工件（改写会使登记失效），改在 R5.3 §24.126.4 登记权威口径（最终值以随提交清单为准，`9EAA1A7F…` 只作该步中间值）。**级别判定说明**：这是散文中的证据指针版本问题，不影响源码、夹具、突变、四项义务闭环或清单内容本身；两名来源审查者与来源批均未据此发现运行正确性缺陷。本批不据此升级为重要项，但也不隐藏：它进入本批的受审材料与最终报告。

## 发现 F-2（建议级 · 残留，来源已登记）BO-6/7-D1

`WorkflowRunner.cs` 中注释与实现不一致（`:1486` 仍写"取最后一条可定位停驻标记"，`:1535–1537` 仍以"DriveAsync/Relocate 无完成跳过"为下界理由）。两名来源审查者独立指出、均评为建议级。**本接收批的第 1 轮会诊进一步指出同一族问题还包含约 `:1400–1403` 的"最后一条/重复提交"旧表述；该扩大范围已采纳并并入 D1 的残项描述**，仍不改动已审源码。本批**不为它单独改写已审源码**：改动会作废 manifest 源哈希与 8 项突变绑定、需重跑整套构建/回归并重取同等级复审，而该处不影响行为正确性。按既定处置留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次一并修正并按该批质量门验证。

## 8 项反向突变（在集成后的主线字节上重新执行）

全部为 baseline Passed / mutant Failed / restored Passed，三阶段构建 exit 0、测试 exit 0→1→0，命中具名目标断言，源码逐字节精确恢复。`原始/恢复` SHA-256 = 工作区 `5470cfcb…`；8 个 `mutant_sha256` 与**来源交付记录完全相同**。

| id | target test | target testId | 断言位置 | mutant sha (前12位) |
|---|---|---|---|---|
| bo6-resume-live-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 96212d01e57a |
| bo6-completed-filter-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 3115f2c3a4d8 |
| bo6-tail-fail-closed-v3 | Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1071 | ff0ff5bf3594 |
| bo6-tail-persisted-failure-v4 | 同上 | 113f020c-1c29-e336-4c1a-3a50b69cbcc8 | WorkflowRunnerTests.cs:1080 | a04531dc4871 |
| bo7-candidate-first-v3 | Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner | 1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc | WorkflowRunnerTests.cs:1131 | ef6ed36b1b8b |
| bo7-rescue-first-v3 | Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor | 7a665a8f-f8e9-11b4-598f-a1f8d24b805a | WorkflowRunnerTests.cs:1173 | e5fcb0af5aec |
| bo7-earliest-park-v3 | Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | 8668d00c7f7e |
| bo7-stable-identity-v3 | 同上 | 398aed81-8691-87dd-a9cd-c62780f5f924 | WorkflowRunnerTests.cs:1008 | f04db2ad7111 |

各突变的 baseline/mutant/restored 构建日志与 TRX、`mutation.patch`、`source-original.cs` 位于 `_workflow/wave3-bo6bo7-receive/mutations/<id>/`；机读记录见 `mutation-records.json`。突变执行后 `WorkflowRunner.cs` 与 `WorkflowRunnerTests.cs` 的 `git diff` 为空，即导入字节被逐字节还原。

## 回归与对照

- 接收前同条件基线（HEAD `6fd6207e5`）：定向三类 89/89；助手全量 1558 passed / 2 skipped / 0 failed / 1560。
- 集成后：定向三类 93/93（93 个 testId/名称/结果与来源交付最终 TRX **完全相同**）；助手全量 1562 passed / 2 skipped / 0 failed / 1564。
- testId 差集（基线→集成）：1556 unchanged / 8 added / 4 removed / 0 changed，与来源交付逐 ID 相同。`0 changed` **不**表示既有测试语义未变：两个保留 testId 的 helper（`ec3b4575…` `RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate`、`14addbd1…` `RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed`）断言由"期望 null（保守链尾）"改为"期望返回停驻标记"。
- 结果口径更正：`Counters@notExecuted` 在这些 VSTest/xUnit 运行中为 0，而实际有 2 条 `NotExecuted` 结果行（两个 opt-in P50 诊断：`P50_LoadRepro_WholeClass_UnderControlledLoad`、`P50_DiagnosticRepeat_OptIn`）。**行级口径为准**：基线 1558 passed + 2 NotExecuted = 1560；集成 1562 passed + 2 NotExecuted = 1564。本批早先的 `testid-comparison-summary.json` 误用计数器属性写成 skipped=0，已按行级口径更正并附 `verification/regression-outcomes.json`。
- 声明面：再生 1/1 通过（清单 599→602 行，**+3 行 / -0 行**，仅新增 §24.126 中三行承载声明关键词的文本；无既有声明行被改或消失），清除变量复跑 1/1 通过，两次 SHA 稳定为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。
- 部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`（及非 x64 同名目录）在全部构建与测试前后 LastWriteTimeUtc 与文件数均未变化（1158 个文件，09/26 21:41:14 UTC）。

## 范围边界与未验证项

- 只接收 BO-6/BO-7；**BO-8 R29 IMPORTANT 与 BO-9 R34 F5 IMPORTANT 仍未并入、仍未闭合**；R5.6、R6.1、R6 diff guard 未提前集成；BO-13、SB21-3 BO-4、SB21-2 BO-10/12 未重开。
- BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭；**未做实机、生产或真实 User 验证**。外部执行边界仍为 fake。
- 本批不声称除上述同步之外的四项义务仍然为 closed；那由来源侧两次独立会诊按原等级裁定，本批只证明其被接收的字节未变且判别力仍在。

## 会诊轮次与逐项处置（本批子批计数）

见 `consultation/review-outcome-v1.md`。第 1 轮（既有 GPT 会诊工具，`gpt-6-astra` / `medium`，attempts=1，read-only，含自动附加 diff）给出 2 项 IMPORTANT 与 1 项建议：
- **IMPORTANT-1（送审材料未覆盖八文件导入保真）**：成立。原材料的 `scoped-*.diff` 只覆盖 manifest 的 `sources`（4 个代码/夹具文件），4 个状态文档的补丁未随材料提供，且"8 文件逐字节相同"缺少"导入时点"限定。**已修复**：新增 `verification/intake-equivalence.json`（逐文件导入=交付判定 + 导入增量与来源增量的 SHA-256 对照 + 接收后改动清单）、`verification/import-delta-8files.diff`（含 4 个文档补丁的完整导入增量）、`verification/post-import-delta.diff`（接收后改动），并修正本文件上文限定语。
- **IMPORTANT-2（替代摘要的结果口径不一致）**：成立。`testid-comparison-summary.json` 用 `Counters@notExecuted`（0）当 skipped，与两次运行各有 2 条 `NotExecuted` 行矛盾。**已修复**：按行级口径重写摘要，并新增 `verification/regression-outcomes.json` 记录每次运行的全部计数与 2 条 `NotExecuted` 的具体用例名。
- **建议（D1 注释范围更宽 + 夹具映射只有 10 条不足以独立证明 93 项完全一致）**：**采纳**。D1 范围扩大至约 `:1400–1403`（见上）；新增 `verification/targeted-93-ids.json`（完整 93 条 testId/名称/结果与来源交付 TRX 的逐条一致性与零 mismatch）与 `verification/mutation-verification.json`（8 项突变的 baseline/mutant/restored 结果、目标身份、断言行、文件摘要，以及与来源清单记录 mutant SHA 的逐项相等性）。

第 1 轮的收尾结论为"本接收批仍有 2 项未闭合 IMPORTANT"；按纪律 R1/R2 不得降级，已按同一修复批归集修复证据并执行验证轮会诊。

**第 2 轮（验证轮，`gpt-6-astra` / `medium`，attempts=1，计次 2/8）结论：两项 IMPORTANT 均按原级闭合，本轮未发现新增 MUST/IMPORTANT；收尾明确"本接收批是否仍有未闭合的 MUST/IMPORTANT：否"。** 逐项原文要点见 `consultation/review-outcome-v2.md`。审查者同时声明该裁决不代表独立重验磁盘原件或完成实机验证。

**会诊计次（本批子批）**：2 / 8（第 1 轮首审 1 次 + 第 2 轮验证 1 次，均一次成功、无失败/超时重试）。
