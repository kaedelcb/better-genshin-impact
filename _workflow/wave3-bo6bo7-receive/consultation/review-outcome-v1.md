# 会诊结果与逐项处置 v1（本批子批，第 1 轮）

- 渠道：既有 GPT 会诊工具（`gpt_workspace.gpt_review`，mode=review，read-only，`diff_included=true`）
- 模型/强度：`gpt-6-astra` / `medium`（与预算一致，未降配）
- 尝试次数：`attempts=1`（一次成功返回报告；无失败、无超时、无重试）
- 计次：**1 / 8**（本批子批自计；来源 BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承、不被本批消耗）
- 送审快照：`_workflow/wave3-bo6bo7-receive/review-intake-20260928-v2`（audit review + verify 通过，packet 508,322 字节）
- 实际送审文件：24 个（清单与容量估算见 `consultation/preflight-v1.json`，约 117k token ≈ 有效窗口 45%；决策：按默认使用既有工具，未触发分流条件）

## 第 1 轮结论

> "本接收批是否仍有未闭合的 MUST/IMPORTANT：**是** —— 两项涉及接收证据的 IMPORTANT。"（原文同时说明：这不单独重开来源侧四项义务，也不裁决其他批次。）

## 逐项处置（不得降级）

### IMPORTANT-1 —— 送审材料未覆盖八文件导入保真
**审查要点（原文）**：`findings.md` 声明 8 文件与来源增量逐字节相同，但 `scoped-staged.diff` 只含 Runner、两个测试文件与声明面清单；四个文档的补丁缺失；"八文件未变"需明确"初始导入"限定，因为最终清单与导入版本明显不同。
**成立**（本批确认）：这是送审材料完备性缺陷，属本批自称的接收结论缺证。
**修复证据**（已归集于同一修复批）：
- `verification/intake-equivalence.json`：逐文件记录"索引 blob == 交付 blob""来源增量是否触及该文件""接收后是否被修改"，并给出导入增量与来源增量的字节数与 SHA-256 对照（`delta_byte_identical=true`）。
- `verification/import-delta-8files.diff`：完整导入增量（含 4 个状态文档的补丁）。
- `verification/post-import-delta.diff`：接收后的改动（4 个文档的登记追加 + 声明面再生）。
- `findings.md` 上文已改写为"导入时逐字节相同 + 接收后改动逐项清单"，并对最终清单与导入版本的差异给出原因（§24.126 登记 + §17.4-A 第 1 条强制再生）。
**状态：candidate_fixed**（待验证轮会诊确认）。

### IMPORTANT-2 —— 替代摘要的结果口径不一致
**审查要点（原文）**：`testid-comparison-summary.json` 报 baseline 1558/1560 与 final 1562/1564 且 skipped 都为 0，与两次运行各 2 条跳过矛盾；要求对账原始结果类别并指出那两条。
**成立**（本批确认）：摘要把 TRX `Counters@notExecuted`（本 VSTest/xUnit 形态下恒为 0）当作 skipped，而实际有 2 条 `NotExecuted` 结果行。
**修复证据**：
- `verification/regression-outcomes.json`：每次运行的全部原始计数、**行级**结果分布、以及两条 `NotExecuted` 的用例名（`P50_LoadRepro_WholeClass_UnderControlledLoad`、`P50_DiagnosticRepeat_OptIn`），并说明 `total = passed + failed + NotExecuted rows` 在每次运行都精确成立。
- `regression/testid-comparison-summary.json` 已按行级口径更正（baseline/final skipped=2 且注明计数器语义）。
**状态：candidate_fixed**（待验证轮会诊确认）。

### 建议项 —— D1 注释范围更宽；夹具映射不足以独立证明 93 项完全一致
**D1 范围扩大**：采纳。审查者指出约 `:1400–1403` 仍保留"最后一条/重复提交"旧表述；该范围并入 BO-6/7-D1 描述，仍不改已审源码，仍留待下一次重新绑定 `WorkflowRunner.cs` 哈希的批次。
**93 项一致性补证**：采纳。新增 `verification/targeted-93-ids.json`（完整 93 条 testId/名称/结果的逐条对照，零 mismatch）与 `verification/mutation-verification.json`（8 项突变的结果、目标身份、断言行、文件摘要，以及与来源清单 mutant SHA 的逐项相等性）。
**状态：accepted（已落实为证据）**。

## 未降级声明

以上 2 项 IMPORTANT 均按原级保留并已修复，未降级、未书面拒绝、未伪记完成。建议项按"采纳＋落实证据"处置。第 2 轮为**验证轮**，范围限定为"上述 2 项 IMPORTANT 是否闭合、是否新增 MUST/IMPORTANT"。
