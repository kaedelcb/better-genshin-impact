import io, json, os
base = r"_workflow/wave3-bo8-bo9"
c = os.path.join(base, "consultation")
lines = io.open("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", encoding="utf-8").read().splitlines()
start = next(i for i, l in enumerate(lines, 1) if l.startswith("## §24.127"))
io.open(os.path.join(c, "r53-24.127-excerpt-v3.md"), "w", encoding="utf-8", newline="\r\n").write(
    "# 摘录：R5.3 §24.127 全文（含 §24.127.4 第 1／2 轮会诊结论）\r\n\r\n" + "\r\n".join(lines[start-1:]) + "\r\n")
def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
b = {idn(l) for l in io.open(os.path.join(base, "claims-v3/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()}
a = io.open("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", encoding="utf-8-sig").read().splitlines()
out = ["# 声明面清单本批新增行（4 行；另 1 行 §24.127.5 边界句被改写）",
       "# 清单全文 617 行；SHA-256 daa2639f36ba35ae9370a759fe84e11afe49cb8d210d3892fa03e246520fb684", ""]
for i, l in enumerate(a, 1):
    if l.strip() and idn(l) not in b:
        out.append("%d\t%s" % (i, l.split("\u0001")[-1]))
io.open(os.path.join(c, "claim-manifest-added-lines-v3.txt"), "w", encoding="utf-8", newline="\r\n").write("\r\n".join(out) + "\r\n")

req = """# 会诊请求 v3（验证轮，本批子批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

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
"""
io.open(os.path.join(c, "review-request-v3.md"), "w", encoding="utf-8", newline="\r\n").write(req)

dispatch = [
    base + "/consultation/review-request-v3.md",
    base + "/consultation/review-outcome-v2.md",
    base + "/consultation/review-outcome-v1.md",
    base + "/consultation/r53-24.127-excerpt-v3.md",
    base + "/consultation/claim-manifest-added-lines-v3.txt",
    base + "/context.md", base + "/findings.md", base + "/budget.md",
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs",
    base + "/run-mutants.ps1", base + "/mutations-run-round3.log", base + "/mutation-records-round3.json",
    base + "/final-v8/targeted-final.trx", base + "/final-v8/assistant-full-final.log", base + "/final-v8/testid-comparison.json",
    base + "/baseline/targeted-baseline.log", base + "/baseline/assistant-full-baseline.log",
    base + "/red-final/bo8-bo9-red-final.trx", base + "/red-final/red-final-exits.json",
    base + "/claims-v3/claim-regen.trx", base + "/claims-v3/claim-noenv.trx",
    base + "/claims-v3/manifest-before.sha256", base + "/claims-v3/manifest-after.sha256",
    base + "/claims-v3/manifest-diff-summary.json", base + "/deploy-target-before.txt", base + "/deploy-target-final.txt",
]
sizes = {f: os.path.getsize(f) for f in dispatch}
total = sum(sizes.values())
def toks(path):
    data = open(path, "rb").read().decode("utf-8", errors="replace")
    cjk = sum(1 for ch in data if "\u4e00" <= ch <= "\u9fff")
    return cjk / 1.45 + (len(data) - cjk) / 3.6
est = int(sum(toks(f) for f in dispatch))
pre = {"kind": "consultation capacity pre-check (record only)", "channel": "existing GPT consultation tool (read-only, mode=review)",
       "model": "gpt-6-astra", "effort": "medium",
       "fixed_snapshot": {"branch": "main-OldTeaBag-B168", "head": "f47b57b1cba90e78624ae6b6d2236aafd402f0e1",
                          "local_audit_snapshot": "_workflow/wave3-bo8-bo9/review-v3"},
       "dispatch_material": dispatch, "file_bytes": sizes, "total_bytes": total, "estimated_input_tokens": est,
       "estimate_method": "tokens ~= cjk_chars/1.45 + non_cjk_chars/3.6 per decoded file",
       "assumed_model_window": 272000, "assumed_effective_window_95pct": 258400,
       "estimated_fraction_of_effective_window": round(est / 258400, 3),
       "attachment_transport_status": "files={} of 30; max single file={} B of 524288; total={} B of 2097152 => {}".format(
           len(dispatch), max(sizes.values()), total,
           "WITHIN limits" if len(dispatch) <= 30 and max(sizes.values()) <= 524288 and total <= 2097152 else "OVER limits"),
       "fallback_assessment": "Estimated input about {}% of the assumed effective window; original GPT tool used first.".format(round(est / 258400 * 100)),
       "sub_batch_budget": "本子批累计：第 1 轮 1 次、第 2 轮 1 次；本轮为第 3 次（3/8）。BO-6/BO-7 子批的 8/8 + 2/2 不重置、不继承。"}
io.open(os.path.join(c, "preflight-v3.json"), "w", encoding="utf-8", newline="\n").write(json.dumps(pre, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({k: pre[k] for k in ("total_bytes","estimated_input_tokens","attachment_transport_status")}, ensure_ascii=False, indent=1))
