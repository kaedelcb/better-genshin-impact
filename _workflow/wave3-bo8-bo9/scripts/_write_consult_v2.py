import io, json, os, hashlib
base = r"_workflow/wave3-bo8-bo9"
c = os.path.join(base, "consultation")
# excerpt files for this round
lines = io.open("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", encoding="utf-8").read().splitlines()
start = next(i for i, l in enumerate(lines, 1) if l.startswith("## §24.127"))
io.open(os.path.join(c, "r53-24.127-excerpt-v2.md"), "w", encoding="utf-8", newline="\r\n").write(
    "# 摘录：R5.3 §24.127 全文（本批在 R5.3 中的全部改动；含 §24.127.4 第 1 轮会诊结论）\r\n\r\n" + "\r\n".join(lines[start-1:]) + "\r\n")
def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
before = [l for l in io.open(os.path.join(base, "claims-v2/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()]
after = io.open("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", encoding="utf-8-sig").read().splitlines()
b = {idn(l) for l in before}
added = [(i, l) for i, l in enumerate(after, 1) if l.strip() and idn(l) not in b]
out = ["# 声明面清单新增行（%d 行；另 1 行 §24.127.5 边界句被改写）" % len(added),
       "# 清单全文 614 行；SHA-256 5ac1737b4405b96fbe3835e5f4b8b9a732ab4889dbe964b40bd2cd29b2f33fa8", ""]
for i, l in added:
    out.append("%d\t%s" % (i, l.split("\u0001")[-1]))
io.open(os.path.join(c, "claim-manifest-added-lines-v2.txt"), "w", encoding="utf-8", newline="\r\n").write("\r\n".join(out) + "\r\n")

req = """# 会诊请求 v2（验证轮，本批子批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

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
"""
io.open(os.path.join(c, "review-request-v2.md"), "w", encoding="utf-8", newline="\r\n").write(req)

dispatch = [
    base + "/consultation/review-request-v2.md",
    base + "/consultation/review-outcome-v1.md",
    base + "/consultation/r53-24.127-excerpt-v2.md",
    base + "/consultation/claim-manifest-added-lines-v2.txt",
    base + "/context.md", base + "/findings.md", base + "/budget.md",
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs",
    base + "/run-mutants.ps1", base + "/mutations-run-round2.log", base + "/mutation-records-round2.json",
    base + "/final-v7/targeted-final.trx", base + "/final-v7/assistant-full-final.log",
    base + "/final-v7/testid-comparison.json",
    base + "/baseline/targeted-baseline.log", base + "/baseline/assistant-full-baseline.log",
    base + "/red-final/bo8-bo9-red-final.trx", base + "/red-final/red-final-exits.json",
    base + "/claims-v2/claim-regen.trx", base + "/claims-v2/claim-noenv.trx",
    base + "/claims-v2/manifest-before.sha256", base + "/claims-v2/manifest-after.sha256",
    base + "/claims-v2/manifest-diff-summary.json",
    base + "/deploy-target-before.txt", base + "/deploy-target-final.txt",
]
sizes = {f: os.path.getsize(f) for f in dispatch}
total = sum(sizes.values())
def toks(path):
    data = open(path, "rb").read().decode("utf-8", errors="replace")
    cjk = sum(1 for ch in data if "\u4e00" <= ch <= "\u9fff")
    return cjk / 1.45 + (len(data) - cjk) / 3.6
est = int(sum(toks(f) for f in dispatch))
pre = {
    "kind": "consultation capacity pre-check (record only; dispatch is the round-2 verification request)",
    "channel": "existing GPT consultation tool (read-only, mode=review; workspace diff auto-attached)",
    "model": "gpt-6-astra", "effort": "medium",
    "fixed_snapshot": {"branch": "main-OldTeaBag-B168", "head": "f47b57b1cba90e78624ae6b6d2236aafd402f0e1",
                       "local_audit_snapshot": "_workflow/wave3-bo8-bo9/review-v2"},
    "dispatch_material": dispatch, "file_bytes": sizes, "total_bytes": total,
    "estimated_input_tokens": est,
    "estimate_method": "tokens ~= cjk_chars/1.45 + non_cjk_chars/3.6 per decoded file",
    "assumed_model_window": 272000, "assumed_effective_window_95pct": 258400,
    "estimated_fraction_of_effective_window": round(est / 258400, 3),
    "attachment_transport_status": "files={} of 30; max single file={} B of 524288; total={} B of 2097152 => {}".format(
        len(dispatch), max(sizes.values()), total,
        "WITHIN limits" if len(dispatch) <= 30 and max(sizes.values()) <= 524288 and total <= 2097152 else "OVER limits"),
    "fallback_assessment": ("Estimated input about {}% of the assumed effective window (>= one third): if this same-scope call "
        "returns without a report and the error is not explicit auth/quota/model unavailability, the facilities rule pre-justifies "
        "switching this verification round to the independent local read-only Codex CLI at the same model/effort. Original tool first."
        ).format(round(est / 258400 * 100)),
    "round1_reference": {"request": "consultation/review-request-v1.md", "outcome": "consultation/review-outcome-v1.md",
                         "preflight": "consultation/preflight-v1.json", "snapshot": "review-v1"},
    "sub_batch_budget": "本子批累计：第 1 轮首审 1 次；本轮为第 2 次（2/8）。BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承。",
}
io.open(os.path.join(c, "preflight-v2.json"), "w", encoding="utf-8", newline="\n").write(json.dumps(pre, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({k: pre[k] for k in ("total_bytes","estimated_input_tokens","estimated_fraction_of_effective_window","attachment_transport_status")}, ensure_ascii=False, indent=1))
print("files", len(dispatch))
