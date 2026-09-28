# 会诊请求 v1（主线施工批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`。渠道：既有 GPT 会诊工具（read-only，自动附本批工作区差异）。

## 本批目标（请审查）
在茶包主线把 R5 Wave3 剩余两项原级未闭合 **IMPORTANT** 施工到真实 Runner：**BO-8 R29**（恢复点之后推进段无完成过滤）
与 **BO-9 R34 F5**（多有效停驻跨轮次推进）；并在同一份 `WorkflowRunner.cs` 哈希重绑定内按登记口径修正
**BO-6/7-D1**（建议级注释陈旧）。BO-8 与 BO-9 同属 `DriveAsync` 推进层，在同一子批内按依赖顺序收敛。

## 送审材料（本目录同批）
- `context.md`（objective）／`findings.md`（findings）／`budget.md`（budget）；
- 产品与夹具全文：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、
  `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`；
- 生成清单摘录：`ClaimSurfaceManifest.txt` 本批新增行；状态登记摘录：R5.3 §24.127 全文；
- 机读证据：`final-v3/targeted-final.trx`（定向 96/96）、`final-v3/assistant-full-final.trx`（1565/2/0/1567）、
  `baseline/targeted-baseline.trx`（93/93）、`baseline/assistant-full-baseline.trx`（1562/2/0/1564）、
  `final-v3/testid-comparison.json`（1564 unchanged／3 added／0 removed／0 changed）、
  `mutation-records-final.json`（12 项 baseline Passed／mutant Failed／restored Passed）、
  `red-final/bo8-bo9-red-final.trx` 与 `red-final/prefix-source.cs`（反例先行红，开工字节 + 最终夹具）、
  `claims/*`（声明面再生＋清除变量复跑）、`deploy-target-before.txt`／`deploy-target-final.txt`；
- 工作区差异（由会诊工具自动附加）：全量 `git status --porcelain` 与本批 scoped unstaged／staged diff。

## 本轮请回答（逐项给结论与依据）
1. **BO-8 R29 是否可按原级（IMPORTANT）登记为 closed？** 特别请核对：把"推进段完成过滤"改为**无条件生效**是否引入
   "该执行而未执行"的新风险——例如循环回绕（`plan.Next` 到下一轮）、修订语义下应由 candidate 承载的出现、
   前置／闸门（skip/reject）／失败／unknown 路径、以及 `waitLocally` 停驻标记是否会被误当"已完成"而不再重驱；
   判据只用稳定出现身份 `(NodeId, Occurrence, LoopIteration)` 是否充分。
2. **BO-9 R34 F5 是否可按原级（IMPORTANT）登记为 closed？** 特别请核对链尾重入是否可能：
   (a) 不终止（重入循环）；(b) 重开 BO-6 明示的"持久 `TailReached` ＋未清偿停驻 ⇒ Failed 且零提交"防御语义；
   (c) 漏驱动、或经重入跳跃而二次提交已完成出现；(d) 与轮次起点等待 `AwaitLoopRoundStartAsync`／
   `LastScheduledRoundWait` 语义冲突（重入到高轮次出现时）。
3. **BO-6/7-D1 的修正是否只改注释、不改行为**（逐处核对 `:1400–1403`、`:1486`、`:1535–1537` 及同步的文档注释），
   并判断 12 项反向突变（8 项在新字节上 rebased ＋ 4 项新增）是否足以支撑这三项的新旧断言判别力。
4. **是否存在新的 MUST／IMPORTANT？** 若有，请给出可观察后果、反例构造思路与最小修复方向。
5. 收尾请明确一句："**本批是否仍有未闭合的 MUST/IMPORTANT：是／否**"。

## 边界（不变）
只审本批材料：BO-8、BO-9 与 BO-6/7-D1。不审 R5.6／R6.1／R6 diff guard，不审未消费的并行成果；
未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证，外部执行边界为 fake，生产门保持关闭。
本轮结论将原文登记进 `consultation/review-outcome-v1.md`；R5.3 §24.127.4 在该轮返回后按原文补写，
本节以"§24.127.4 引用本轮结论"为登记口径以避免版本漂移。
