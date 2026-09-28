import io, os
base = r"_workflow/wave3-bo8-bo9"
c = os.path.join(base, "consultation")

outcome2 = """# 会诊结果与逐项处置 v2（本批子批，第 2 轮 = 验证轮）

- 渠道：既有 GPT 会诊工具（`gpt_workspace.gpt_review`，mode=review，read-only，`diff_included=true`）
- 模型/强度：`gpt-6-astra` / `medium`；尝试次数 `attempts=1`（一次成功返回，无失败、无超时）
- 计次：**2 / 8**（本批子批累计；BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承）
- 送审快照：`_workflow/wave3-bo8-bo9/review-v2`（audit review + verify 通过，packet 433,123 字节）
- 材料预检：`consultation/preflight-v2.json`（26 件、487,548 字节、最大单件 160,517 字节，均在附件限制内）
- 范围：严格限定为"第 1 轮 IMPORTANT-1 是否按原级闭合 + 是否新增 MUST/IMPORTANT"

## 第 2 轮结论（原文要点）

1. **原 IMPORTANT-1：具体缺陷已修复，原级闭合证据成立**——实现层次成立（在**原链尾位置**重建同轮前插出现，不再依赖已变化的完成锚）；
   原反例 `[A,P] + A@0 完成 + P@1/P@2 停驻` 已被封住；真实 Runner 新夹具成立（提交序列、`A@0` 不重跑、成功、收尾一次与持久结果）；
   反向突变具有判别力（禁用前插探针后提交退化为 `[(P,0),(P,1),(P,2)]`，命中 `:1411`），入口链尾守卫保留；
   并指出"终止性只能在固定计划等前提下作收缩论证，不能扩大为任意修订交错下的保证"。
2. **新增 IMPORTANT-2：已清偿的历史停驻仍会生成前插执行义务，导致额外提交**——`TryRelocateToOutstandingObligation`
   只在 `Consider(parkOcc)` 内排除已完成停驻（`return` 仅退出局部函数），外层仍为该**已失效停驻**扫描同轮更早节点。
   反例（真实 Runner）：无循环计划 `[A,P,T]`（A 完成、P 先停驻后完成、旧标记保留）→ 暂停期间改为 `[A,X,P,T]`（X 新插）
   → 恢复：`RecomputeSuccessor` 按完成锚 P 返回 T（符合既有修订后继语义）⇒ T 完成后新链尾方法仍扫描旧标记 P 之前
   的节点，选出 **X** 并实际提交（`T→X`），随后过滤已完成 P/T 并成功——**此时没有任何存活停驻，却因旧标记额外执行 X**，
   可能产生外部副作用；原 13 项突变与新增夹具均未覆盖。建议在 `TryLocate` 之后、`Consider` 与探针之前加
   `if (HasCompletedOutcome(run, parkOcc)) continue;`，并补该形态的真实 Runner 测试与移除守卫的反向突变。
3. **问题（2）**：有新的 IMPORTANT（即 IMPORTANT-2）；未发现新的 MUST。
4. **收尾原文**："**本批是否仍有未闭合的 MUST/IMPORTANT：是。**"建议原 IMPORTANT-1 单独记为已闭合，
   BO-9 因新增 IMPORTANT-2 保持原级未闭合，修复并补证后再裁决。
5. 审查者声明：结论仅来自所附源码、diff 与证据文本；未调用工具、未修改文件、未执行命令，也未独立计算文件哈希。

## 逐项处置（本批）

| 发现 | 原级 | 处置 | 生效层次与证据 |
|---|---|---|---|
| R1-IMPORTANT-1（链尾丢同轮前插 rescue ⇒ 假成功） | IMPORTANT | **已按原级闭合**（第 2 轮确认） | 原层修复（同一次 `DriveAsync` 的链尾恢复点重建）；夹具 `Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail` ＋ 突变 `bo9-tail-reentry-parks-only-v5`（P/F/P，命中 `:1411`）。 |
| R2-IMPORTANT-2（已清偿停驻标记仍生成前插义务 ⇒ 额外提交） | IMPORTANT | **已修复（原级闭环候选）** | 原层修复：在 `TryLocate` 命中后先 `if (HasCompletedOutcome(run, parkOcc)) continue;`，已清偿标记既不作重入点也不产生前插义务。证据：新夹具 `Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode`（`[A,X,P,T]`、旧标记 P 已清偿 ⇒ 只提交 `["T"]`、X 无 outcome、成功、收尾一次）＋新突变 `bo9-settled-park-probe-v5`（P/F/P，命中 `WorkflowRunnerTests.cs:1472`）。 |
| R1-建议级-1（D1 后两处注释解释不准确） | 建议级 | 采纳并订正 | 见 v1 记录：`ParkedRescue` 探测口径、R24 全序说明；另采纳 `HasCompletedOutcome` 口径表述与终止性收缩论证。 |
| R2 观察（终止性仅能作收缩论证） | 观察项 | 如实登记 | R5.3 §24.127.1 与 findings 已改为收缩论证表述；不扩大为任意修订交错下的保证。 |

## 边界
裁决只覆盖本批送审材料（BO-8、BO-9、BO-6/7-D1）；外部执行边界为 fake；BGI 生产门、真实 User、R5.8、E3/E4/E5、热键面与实机门继续关闭。
"""
io.open(os.path.join(c, "review-outcome-v2.md"), "w", encoding="utf-8", newline="\r\n").write(outcome2)

# ---- R5.3: numbers + IMPORTANT-2 + consultation round-2 record ----
p = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = io.open(p, encoding="utf-8").read()
reps = [
 ("（`TryRelocateToOutstandingObligation`），\n不再按链尾放行。义务集合＝①仍存活（可在当前修订定位且无完成结果）的停驻出现；②每个存活停驻**同轮次**、序号更早的\n**从未执行**出现（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）。",
  "（`TryRelocateToOutstandingObligation`），\n不再按链尾放行。义务集合＝①仍存活（可在当前修订定位且无完成结果）的停驻出现；②每个**存活**停驻**同轮次**、序号更早的\n**从未执行**出现（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）。**已清偿（同身份已有完成结果）的停驻标记先被剔除**，\n既不作重入点也不产生前插义务（第 2 轮会诊 IMPORTANT-2：否则旧标记会额外执行其同轮更早的未执行节点）。"),
 ("- 修复后同一组夹具**：", "- 修复后同一组夹具**："),
 ("- **13 项反向突变全部在最终字节上执行**", "- **14 项反向突变全部在最终字节上执行**"),
 ("5 项为本批新增——`bo8-completion-filter-v5`、\n  `bo9-tail-reentry-disabled-v5`、`bo9-reentry-order-last-v5`、`bo9-tail-reentry-parks-only-v5`（第 1 轮会诊 IMPORTANT-1\n  的判别力钉死）、`bo9-persisted-tail-reopen-v5`。",
  "6 项为本批新增——`bo8-completion-filter-v5`、\n  `bo9-tail-reentry-disabled-v5`、`bo9-reentry-order-last-v5`、`bo9-tail-reentry-parks-only-v5`（第 1 轮会诊 IMPORTANT-1\n  的判别力钉死）、`bo9-settled-park-probe-v5`（第 2 轮会诊 IMPORTANT-2 的判别力钉死）、`bo9-persisted-tail-reopen-v5`。"),
 ("`mutations-round2/`", "`mutations-round3/`"),
 ("`mutation-records-round2.json`", "`mutation-records-round3.json`"),
 ("命中具名目标断言（`WorkflowRunnerTests.cs:1008／1070／1071／1080／1131／1173／1222／1283／1411`），",
  "命中具名目标断言（`WorkflowRunnerTests.cs:1008／1070／1071／1080／1131／1173／1222／1283／1411／1472`），"),
 ("源码逐字节恢复（`original_sha256 == restored_sha256 == c71db8647501baf98f0de5cb4fab6d33cd63704b7e868ab17f8de7bf4bde0ae1`）。",
  "源码逐字节恢复（`original_sha256 == restored_sha256 == b0b3579bf289551d2f314b1bb9192284f75406d9fb1d8bfd9db2c66bcca6c1ae`）。"),
 ("被中止的初版运行目录 `mutations/`、中间版 `mutations-final/` 的 12 项记录\n  均不作为最终证据（后者由 `mutations-round2/` 的 13 项取代）。",
  "被中止的初版运行目录 `mutations/`、中间版 `mutations-final/`（12 项）与 `mutations-round2/`（13 项）\n  均不作为最终证据（最终证据为 `mutations-round3/` 的 14 项）。"),
 ("助手全量 **1566 passed / 2 skipped / 0 failed / 1568**。同条件主线基线（开工 HEAD、本批改动前独立重跑）\n  **1562 passed / 2 skipped / 0 failed / 1564**、定向 93/93；testId 逐名对照 **1564 unchanged／4 added／0 removed／\n  0 changed**（4 added 即本批四项新夹具，全部 Passed）。",
  "助手全量 **1567 passed / 2 skipped / 0 failed / 1569**。同条件主线基线（开工 HEAD、本批改动前独立重跑）\n  **1562 passed / 2 skipped / 0 failed / 1564**、定向 93/93；testId 逐名对照 **1564 unchanged／5 added／0 removed／\n  0 changed**（5 added 即本批五项新夹具，全部 Passed）。"),
 ("**97/97**；\n  助手全量", "**98/98**；\n  助手全量"),
 ("BO-9 反例③（同轮前插 rescue）← `bo9-tail-reentry-parks-only-v5`；",
  "BO-9 反例③（同轮前插 rescue）← `bo9-tail-reentry-parks-only-v5`；反例④（已清偿标记不得产生前插义务）← `bo9-settled-park-probe-v5`；"),
 ("- 计数：**2 / 8**", "- 计数：**3 / 8**"),
 ("- **第 2 轮（验证轮，本子批计 2/8）**：__ROUND2__",
  "- **第 2 轮（验证轮，本子批计 2/8）**：裁定**原 IMPORTANT-1 已按原级闭合**（实现层次、原反例封堵、真实夹具、突变判别力、\n  入口链尾守卫逐项核对通过），同时提出**新增 IMPORTANT-2**（`TryRelocateToOutstandingObligation` 只为已清偿停驻标记也扫描\n  同轮前插出现 ⇒ 凭旧标记额外执行新插节点）；收尾原文为「本批是否仍有未闭合的 MUST/IMPORTANT：是」，\n  BO-9 保持原级未闭合。逐项原文与处置见 `consultation/review-outcome-v2.md`。\n- **第 3 轮（验证轮，本子批计 3/8）**：__ROUND3__"),
]
for old, new in reps:
    n = t.count(old)
    if n != 1:
        print("SKIP(pattern count %d): %r" % (n, old[:70]))
        continue
    t = t.replace(old, new)
io.open(p, "w", encoding="utf-8", newline="\n").write(t)
print("R5.3 updated")
