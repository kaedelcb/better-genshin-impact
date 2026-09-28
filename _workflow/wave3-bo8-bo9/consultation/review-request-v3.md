# 会诊请求 v3（验证轮，本批子批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`。渠道：既有 GPT 会诊工具（read-only，自动附本批工作区差异）。

## 本轮范围（严格限定）
本轮为**验证轮**，只回答两件事：
1. 第 2 轮 **IMPORTANT-2**（`TryRelocateToOutstandingObligation` 为**已清偿**的历史停驻标记也扫描同轮前插出现 ⇒
   凭旧标记额外执行修订新插入的节点）是否已按**原级 IMPORTANT 闭合**；
2. 是否出现新的 **MUST/IMPORTANT**。
第 1／2 轮结论与逐项处置见 `consultation/review-outcome-v1.md` 与 `review-outcome-v2.md`；本轮不重复已确认结论，不裁决其他批次。

## IMPORTANT-2 的闭合证据（请核对最终字节与证据）
- **实现**：`TryRelocateToOutstandingObligation` 在 `plan.TryLocate(...)` 命中后、`Consider` 与探针之前加入
  `if (HasCompletedOutcome(run, parkOcc)) continue;` —— 已清偿标记既不作重入点，也不产生同轮前插义务。
- **新夹具**：`WorkflowRunnerTests.cs` 的 `Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode`
  —— 计划 `[A,X,P,T]`（无循环）+ `A@0` 完成 + `P@0` 先停驻后完成（旧标记保留）+ `T@0` 待执行 ⇒ 期望只提交 `["T"]`、
  `X` 无 outcome、`Succeeded`、收尾一次；断言在 `WorkflowRunnerTests.cs:1447–1475` 附近。
- **新突变**：`mutations-round3/bo9-settled-park-probe-v5`（把该守卫改为 `if (false) continue;`）＝
  baseline Passed／mutant Failed／restored Passed，命中 `WorkflowRunnerTests.cs:1472`。
- **回归**：最终定向 98/98（`final-v8/targeted-final.trx`）、助手全量 1567 passed／2 skipped／0 failed／1569
  （`final-v8/assistant-full-final.trx`）；同条件基线 1562/2/0/1564；testId 1564 unchanged／5 added／0 removed／0 changed；
  14 项反向突变全部 P/F/P（`mutation-records-round3.json`）；部署目标读数未变；声明面再生＋清除变量复跑通过。
- 同时请确认第 2 轮对**原 IMPORTANT-1** 的"已闭合"结论在本最终字节上仍然成立（本批此后仅新增该守卫与夹具，
  未回退前插重建），以及建议级订正没有引入行为改动。

## 请回答
1. IMPORTANT-2 是否按原级闭合？（若否，写明仍缺什么具体构造或证据）
2. 是否出现新的 MUST/IMPORTANT？
3. 收尾明确一句："本批是否仍有未闭合的 MUST/IMPORTANT：是／否"。

## 边界（不变）
只审 BO-8、BO-9 与 BO-6/7-D1 的本批材料；不审 R5.6／R6.1／R6 diff guard 与未消费并行成果；
未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证，外部执行边界为 fake，生产门关闭。
