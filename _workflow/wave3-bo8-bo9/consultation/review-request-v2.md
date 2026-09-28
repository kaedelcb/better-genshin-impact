# 会诊请求 v2（验证轮，本批子批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`。渠道：既有 GPT 会诊工具（read-only，自动附本批工作区差异）。

## 本轮范围（严格限定）
本轮是**验证轮**，只回答两件事：
1. 第 1 轮 **IMPORTANT-1**（`TryRelocateToLivePark` 链尾只重入停驻点、丢掉 `ParkedRescue` 已选出的同轮前插出现，
   最终假成功）是否已按**原级 IMPORTANT 闭合**（逐项给结论与依据）；
2. 是否出现新的 **MUST/IMPORTANT**。
第 1 轮结论与逐项处置见 `consultation/review-outcome-v1.md`；本轮不重复已确认的 BO-8 结论，也不裁决其他批次。

## IMPORTANT-1 的闭合证据（请核对最终字节，不要只信说明文字）
- **实现**：`WorkflowRunner.DriveAsync` 链尾分支改为重建**未履行的恢复义务**并重入（`TryRelocateToOutstandingObligation`）：
  义务集合＝①仍存活（可定位且无完成结果）的停驻出现；②每个存活停驻**同轮次**、序号更早的**从未执行**出现
  （R12 建议-1／R14 F1「不静默跳过未执行节点」口径）。生效层次＝原问题发生的同一层（同一次 `DriveAsync` 的链尾恢复点）。
  两条守卫保持不变：入口即持久 `TailReached` 不由本路径重开（BO-6 防御语义）；终止性由"每次重入都会驱动返回的出现一次
  ⇒ 义务集合严格收缩"给出（已按第 1 轮要求改为收缩论证，不再写"必然执行一次"）。
- **新夹具**：`WorkflowRunnerTests.cs` 的 `Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail`
  ——计划 `[A,P]`（无循环）+ `A@0` 已完成 + `P@1/P@2` 停驻 ⇒ 期望提交 `[(P,0),(A,1),(P,1),(A,2),(P,2)]`（计划全序）、
  `A@0` 不重跑、`Succeeded`、收尾一次；断言 `WorkflowRunnerTests.cs:1395–1420` 附近。
- **新增反向突变**：`mutations-round2/bo9-tail-reentry-parks-only-v5`（把链尾重入退回"只补停驻点"）＝
  baseline Passed／mutant Failed／restored Passed，命中 `WorkflowRunnerTests.cs:1411`。
- **回归**：最终定向三类 97/97（`final-v7/targeted-final.trx`）、助手全量 1566 passed／2 skipped／0 failed／1568
  （`final-v7/assistant-full-final.trx`）；同条件基线 1562/2/0/1564；testId 1564 unchanged／4 added／0 removed／0 changed；
  13 项反向突变全部 P/F/P（`mutation-records-round2.json`）。
- **建议级-1 的处置**：D1 后两处注释已订正（`ParkedRescue` 探测口径、R24 全序说明补"无循环时由链尾重建义务"）；
  另采纳 `HasCompletedOutcome` 口径表述与终止性措辞，并把"夹具均无 scheduled loop"登记为未覆盖边界（R5.3 §24.127.5）。

## 请回答
1. IMPORTANT-1 是否按原级闭合？（若否，写明仍缺什么具体构造或证据）
2. 是否出现新的 MUST/IMPORTANT？
3. 收尾明确一句："本批是否仍有未闭合的 MUST/IMPORTANT：是／否"。

## 边界（不变）
只审 BO-8、BO-9 与 BO-6/7-D1 的本批材料；不审 R5.6／R6.1／R6 diff guard 与未消费并行成果；
未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证，外部执行边界为 fake，生产门关闭。
