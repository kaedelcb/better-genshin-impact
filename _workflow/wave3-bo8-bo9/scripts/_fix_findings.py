import io
f = r"_workflow/wave3-bo8-bo9/findings.md"
t = io.open(f, encoding="utf-8").read()
pairs = [
 ("（`mutations-round2/bo9-tail-reentry-parks-only-v5`，命中\n`WorkflowRunnerTests.cs:1411`），属本批新引入的收敛缺陷，按原级（IMPORTANT）处置而非降级。",
  "（`mutations-round3/bo9-tail-reentry-parks-only-v5`，命中\n`WorkflowRunnerTests.cs:1411`），属本批新引入的收敛缺陷，按原级（IMPORTANT）处置而非降级。\n\n"
  "**反例④（第 2 轮会诊 IMPORTANT-2，已清偿停驻标记仍生成前插义务）**：无循环计划 `[A,P,T]`（A@0 完成、P@0 先停驻后完成、"
  "旧标记保留）→ 暂停期间改为 `[A,X,P,T]`（X 新插）→ 恢复按完成锚 P 返回 T ⇒ T 完成后链尾重建若**不先剔除已清偿标记**，"
  "仍会为该旧标记扫描同轮更早节点而选出 `X` 并额外提交（`T→X`），此时并无任何存活停驻。修复＝在 `TryLocate` 命中后先 "
  "`if (HasCompletedOutcome(run, parkOcc)) continue;`（已清偿标记既不作重入点也不产生前插义务）；判别力由\n"
  "`mutations-round3/bo9-settled-park-probe-v5`（P/F/P，命中 `:1472`）与夹具\n"
  "`Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode` 证明。"),
 ("（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）。守卫：",
  "（R12 建议-1／R14 F1「不静默跳过未执行节点」口径）；**已清偿（同身份已有完成结果）的停驻标记先被剔除**，"
  "既不作重入点也不产生前插义务（第 2 轮会诊 IMPORTANT-2）。守卫："),
]
for old, new in pairs:
    n = t.count(old)
    assert n == 1, (n, old[:60])
    t = t.replace(old, new)
io.open(f, "w", encoding="utf-8", newline="\r\n").write(t)
print("findings patched")
