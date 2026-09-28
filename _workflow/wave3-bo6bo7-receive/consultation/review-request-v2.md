# 会诊请求 v2（验证轮，接收批 `wave3-bo6bo7-receive`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`。渠道：既有 GPT 会诊工具（本批默认）。

## 本轮范围（严格限定）

本轮是**验证轮**，只回答两件事：
1. 第 1 轮的两项 IMPORTANT 是否已按原级闭合（逐项给结论与依据）；
2. 是否出现新的 MUST/IMPORTANT。

第 1 轮结论与逐项处置见 `consultation/review-outcome-v1.md`。本轮不重复第 1 轮已确认的实现结论，也不裁决其他批次。

## 第 1 轮 IMPORTANT-1 的闭合证据

- `verification/intake-equivalence.json`：逐文件导入保真判定（索引 blob == 交付 blob）与"接收后是否被修改"；导入增量 `git diff --cached -- <8 文件>` 与来源增量 `git diff e2613a851 e009068e2 -- <8 文件>` 的 SHA-256 均为 `0642971729f3eff0…`，`delta_byte_identical=true`。
- `verification/import-delta-8files.diff`：含 4 个状态文档补丁在内的完整导入增量。
- `verification/post-import-delta.diff`：接收后改动（4 个文档的 §24.126/登记追加 + ClaimSurfaceManifest 强制再生），产品源码与两个夹具文件不在其中。
- `findings.md`：已把"逐字节相同"限定为导入时点事实，并逐项列出接收后改动。

## 第 1 轮 IMPORTANT-2 的闭合证据

- `verification/regression-outcomes.json`：每次运行的原始计数、行级结果分布、两条 `NotExecuted` 用例名，并说明 `total = passed + failed + NotExecuted rows`。
- `regression/testid-comparison-summary.json`：已按行级口径更正（skipped=2，附计数器语义说明）。

## 建议项的落实

- D1 范围扩大至约 `:1400–1403`（不改源码，留待下一次绑定 `WorkflowRunner.cs` 哈希的批次）。
- `verification/targeted-93-ids.json`：完整 93 条逐条对照，零 mismatch。
- `verification/mutation-verification.json`：8 项突变结果/目标/断言行/摘要 + 与来源清单 mutant SHA 的逐项相等性。

## 请回答

1. IMPORTANT-1 是否闭合？（若否，写明仍缺什么具体材料）
2. IMPORTANT-2 是否闭合？（若否，写明如何对账）
3. 是否出现新的 MUST/IMPORTANT？
4. 收尾明确一句："本接收批是否仍有未闭合的 MUST/IMPORTANT：是／否"。

## 边界（不变）

只接收 BO-6/BO-7；BO-8/BO-9 未并入、未闭合；未做实机/真实 User/BGI 生产进程/R5.8/E3/E4/E5/热键面验证，外部执行边界为 fake；生产门关闭。`-p:DeployToBgiTools=false`，部署目标未被写入。
