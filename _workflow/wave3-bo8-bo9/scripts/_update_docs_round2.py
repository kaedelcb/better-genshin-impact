import io, os
# 1) fix the evidence-line sentence in R5.3
p = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = io.open(p, encoding="utf-8").read()
old = "被中止的初版运行目录 `mutations/`、中间版 `mutations-final/` 的 12 项记录\n  均不作为最终证据（后者由 `mutations-round3/` 的 13 项取代）。"
new = "被中止的初版运行目录 `mutations/`、中间版 `mutations-final/`（12 项）与 `mutations-round2/`（13 项）\n  均不作为最终证据；最终证据为 `mutations-round3/` 的 14 项。"
assert t.count(old) == 1
io.open(p, "w", encoding="utf-8", newline="\n").write(t.replace(old, new))

# 2) findings.md: add reverse-example 4 (IMPORTANT-2), update numbers/mutation table
f = r"_workflow/wave3-bo8-bo9/findings.md"
t = io.open(f, encoding="utf-8").read()
pairs = [
 ("修复后：反例②提交 `[(A,1),(A,2),(A,3)]` 各恰一次、`A@loop0` 不重跑、`Succeeded`；反例③提交",
  "**反例④（第 2 轮会诊 IMPORTANT-2，已清偿停驻标记仍生成前插义务）**：无循环计划 `[A,P,T]`（A@0 完成、P@0 先停驻后完成、"
  "旧标记保留）→ 暂停期间改为 `[A,X,P,T]`（X 新插）→ 恢复按完成锚 P 返回 T ⇒ T 完成后链尾重建若**不先剔除已清偿标记**，"
  "仍会为该旧标记扫描同轮更早节点而选出 `X` 并额外提交（`T→X`），此时并无任何存活停驻。修复＝在 `TryLocate` 命中后先 "
  "`if (HasCompletedOutcome(run, parkOcc)) continue;`（已清偿标记既不作重入点也不产生前插义务）。\n\n"
  "修复后：反例②提交 `[(A,1),(A,2),(A,3)]` 各恰一次、`A@loop0` 不重跑、`Succeeded`；反例③提交"),
 ("命中 `WorkflowRunnerTests.cs:1411`）与最终夹具共同证明。",
  "命中 `WorkflowRunnerTests.cs:1411`）与最终夹具共同证明；反例④的判别力由 `mutations-round3/bo9-settled-park-probe-v5`"
  "（P/F/P，命中 `:1472`）与夹具 `Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode` 证明。"),
 ("- 第 2 轮（验证轮）：逐项结论见 `consultation/review-outcome-v2.md`，计数以该文件记录为准。",
  "- 第 2 轮（验证轮，计 2/8）：裁定原 IMPORTANT-1 按原级闭合，同时提出**新增 IMPORTANT-2**（已清偿停驻标记仍生成前插义务）；"
  "本批已修复并新增夹具＋突变（原级闭环候选），逐项见 `consultation/review-outcome-v2.md`。\n"
  "- 第 3 轮（验证轮，计 3/8）：逐项结论见 `consultation/review-outcome-v3.md`，计数以该文件记录为准。"),
 ("`fixed-v5/` 为修复后、文档未更新前的中间运行（97/97、1566/2/0/1568）。",
  "`fixed-v5/` 为第 1 轮修复后的中间运行（97/97、1566/2/0/1568）；第 2 轮 IMPORTANT-2 修复后的最终运行见 `final-v8/`\n"
  "（定向 98/98、全量 1567 passed / 2 skipped / 0 failed / 1569）。"),
 ("- 最终：定向 **97/97**；助手全量 **1566 passed / 2 skipped / 0 failed / 1568**（= 基线 1564 与本批 4 项新夹具）。",
  "- 最终：定向 **98/98**；助手全量 **1567 passed / 2 skipped / 0 failed / 1569**（= 基线 1564 与本批 5 项新夹具）。"),
 ("- testId 逐名对照（基线 → 最终）：**1564 unchanged / 4 added / 0 removed / 0 changed**\n  （`final-v7/testid-comparison.json`）。4 added 恰为本批新增夹具，全部 Passed；无 removed、无 changed。",
  "- testId 逐名对照（基线 → 最终）：**1564 unchanged / 5 added / 0 removed / 0 changed**\n  （`final-v8/testid-comparison.json`）。5 added 恰为本批新增夹具，全部 Passed；无 removed、无 changed。"),
 ("## 反向突变（13 项，全部在最终字节上执行，不复用旧哈希）\n`mutations-round2/`（机读记录 `mutation-records-round2.json`）。",
  "## 反向突变（14 项，全部在最终字节上执行，不复用旧哈希）\n`mutations-round3/`（机读记录 `mutation-records-round3.json`）。"),
 ("（`original_sha256 == restored_sha256 == c71db8647501baf98f0de5cb4fab6d33cd63704b7e868ab17f8de7bf4bde0ae1`）。",
  "（`original_sha256 == restored_sha256 == b0b3579bf289551d2f314b1bb9192284f75406d9fb1d8bfd9db2c66bcca6c1ae`）。"),
 ("| bo9-tail-reentry-parks-only-v5 | 本批 BO-9 反例③（第 1 轮会诊 IMPORTANT-1） | 链尾只重入停驻点、丢掉同轮前插未执行出现 | `:1411` |",
  "| bo9-tail-reentry-parks-only-v5 | 本批 BO-9 反例③（第 1 轮会诊 IMPORTANT-1） | 链尾只重入停驻点、丢掉同轮前插未执行出现 | `:1411` |\n"
  "| bo9-settled-park-probe-v5 | 本批 BO-9 反例④（第 2 轮会诊 IMPORTANT-2） | 不剔除已清偿停驻标记，使其再次生成同轮前插义务 | `:1472` |"),
 ("说明：`mutations/`（被中止的初版运行）与 `mutations-final/`（12 项，第 1 轮会诊前的版本）均**不作为最终证据**，\n已由在最终字节上重跑的 `mutations-round2/`（13 项）取代；`-v5` 后缀表示\"同一突变意图在新字节上重新绑定与重跑\"。",
  "说明：`mutations/`（被中止的初版运行）、`mutations-final/`（12 项）与 `mutations-round2/`（13 项）均**不作为最终证据**；\n"
  "最终证据为在最终字节上重跑的 `mutations-round3/`（14 项）。`-v5` 后缀表示\"同一突变意图在新字节上重新绑定与重跑\"。"),
 ("- 第 1 轮（首审，`gpt-6-astra` / `medium`，attempts=1，read-only，自动附本批 diff）：1 项 **IMPORTANT**（BO-9 链尾重入\n  漏掉同轮前插 rescue ⇒ 假成功；即反例③）＋ 1 项**建议级**（D1 残余注释解释不准确）。计数 1/8。",
  "- 第 1 轮（首审，`gpt-6-astra` / `medium`，attempts=1，read-only，自动附本批 diff）：1 项 **IMPORTANT**（BO-9 链尾重入\n  漏掉同轮前插 rescue ⇒ 假成功；即反例③）＋ 1 项**建议级**（D1 残余注释解释不准确）。计数 1/8。"),
 ("IMPORTANT 由\"重建未履行恢复义务\"闭合并新增夹具＋突变钉死，建议级采纳（注释订正）。",
  "IMPORTANT 由\"重建未履行恢复义务\"闭合并新增夹具＋突变钉死，建议级采纳（注释订正）。第 2 轮确认 IMPORTANT-1 按原级闭合，\n"
  "同时提出 IMPORTANT-2（已清偿停驻标记仍生成前插义务），本批再修复并新增夹具＋突变；第 3 轮为最终验证轮。"),
]
for old, new in pairs:
    n = t.count(old)
    if n != 1:
        print("SKIP(%d): %r" % (n, old[:70])); continue
    t = t.replace(old, new)
io.open(f, "w", encoding="utf-8", newline="\r\n").write(t)
print("findings updated")
