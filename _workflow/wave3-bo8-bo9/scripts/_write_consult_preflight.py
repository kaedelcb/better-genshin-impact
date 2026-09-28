import io, json, os
base = r"_workflow/wave3-bo8-bo9"
c = os.path.join(base, "consultation")
# 1) R5.3 section 24.127 excerpt (with the dated notes on the stale bullets)
lines = io.open("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", encoding="utf-8").read().splitlines()
excerpt = "\r\n".join(lines[5040:5207]) + "\r\n"
io.open(os.path.join(c, "r53-24.127-excerpt.md"), "w", encoding="utf-8", newline="").write(
    "# 摘录：R5.3 §24.126.5 更新句 + §24.127 全文（本批在 R5.3 中的全部改动，原文档其余 5000 余行未改动）\r\n\r\n" + excerpt)

# 2) claim manifest added lines
def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
before = [l for l in io.open(os.path.join(base, "claims/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()]
after = io.open("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", encoding="utf-8-sig").read().splitlines()
b = {idn(l) for l in before}
added = [(i, l) for i, l in enumerate(after, 1) if l.strip() and idn(l) not in b]
out = ["# 声明面清单本批新增行（共 %d 行；删除 0 行）" % len(added),
       "# 生成文件全文 609 行 / SHA-256 84e90f99bddd36fe3e0193cf33da758cac33ad103da8d36a1c3bf91dca703434",
       "# 每行格式：行号\\t文档路径\\t指纹\\t出现序号\\t规范化文本", ""]
for i, l in added:
    parts = l.split("\u0001")
    out.append("%d\t%s" % (i, parts[-1] if len(parts) >= 4 else l))
io.open(os.path.join(c, "claim-manifest-added-lines.txt"), "w", encoding="utf-8", newline="\r\n").write("\r\n".join(out) + "\r\n")

# 3) correct the preflight (transport limits + trimmed dispatch set)
dispatch = [
    base + "/consultation/review-request-v1.md",
    base + "/consultation/r53-24.127-excerpt.md",
    base + "/consultation/claim-manifest-added-lines.txt",
    base + "/context.md",
    base + "/findings.md",
    base + "/budget.md",
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs",
    base + "/run-mutants.ps1",
    base + "/mutations-run-final.log",
    base + "/mutation-records-final.json",
    base + "/final-v3/targeted-final.trx",
    base + "/final-v3/assistant-full-final.log",
    base + "/final-v3/testid-comparison.json",
    base + "/baseline/targeted-baseline.log",
    base + "/baseline/assistant-full-baseline.log",
    base + "/red-final/bo8-bo9-red-final.trx",
    base + "/red-final/red-final-exits.json",
    base + "/red-final/testproject-build.log",
    base + "/claims/claim-regen.trx",
    base + "/claims/claim-noenv.trx",
    base + "/claims/manifest-before.sha256",
    base + "/claims/manifest-after-regen.sha256",
    base + "/claims/manifest-after-noenv.sha256",
    base + "/claims/manifest-diff-summary.json",
    base + "/deploy-target-before.txt",
    base + "/deploy-target-final.txt",
]
sizes = {f: os.path.getsize(f) for f in dispatch}
total = sum(sizes.values())
def toks(path):
    data = open(path, "rb").read().decode("utf-8", errors="replace")
    cjk = sum(1 for ch in data if "\u4e00" <= ch <= "\u9fff")
    return cjk / 1.45 + (len(data) - cjk) / 3.6
est = int(sum(toks(f) for f in dispatch))
pre = json.load(io.open(os.path.join(c, "preflight-v1.json"), encoding="utf-8"))
pre["dispatch_material"] = dispatch
pre["file_bytes"] = sizes
pre["total_bytes"] = total
pre["estimated_input_tokens"] = est
pre["estimated_fraction_of_effective_window"] = round(est / 258400, 3)
pre["attachment_transport_status"] = (
    "files={} of 30 allowed; max single file={} B of 524288; total={} B of 2097152 => {}".format(
        len(dispatch), max(sizes.values()), total,
        "WITHIN limits" if len(dispatch) <= 30 and max(sizes.values()) <= 524288 and total <= 2097152 else "OVER limits"))
pre["fallback_assessment"] = ("Estimated input is about {}% of the assumed effective window (>= one third), so if the original "
    "GPT consultation tool returns without a report for this same scope (and the error is not an explicit auth/quota/model "
    "unavailability), the facilities rule pre-justifies switching this single review to the independent local read-only "
    "Codex CLI at the same model/effort. The original tool is used first.").format(round(est / 258400 * 100))
pre["excluded_material"] = [
    {"path": base + "/final-v3/assistant-full-final.trx", "bytes": os.path.getsize(base + "/final-v3/assistant-full-final.trx"),
     "reason": "2.5 MB 超出单文件 512 KiB 附件上限；其可审内容由同名 .log 的最终计数行、testid-comparison.json 与 findings.md 承载；TRX 保留在磁盘。"},
    {"path": base + "/baseline/assistant-full-baseline.trx", "bytes": os.path.getsize(base + "/baseline/assistant-full-baseline.trx"),
     "reason": "同上（基线全量 TRX 2.5 MB）；以 .log 计数行 + testid-comparison.json 承载。"},
    {"path": base + "/red-final/prefix-source.cs",
     "reason": "开工字节（133 KB）可由 `git show HEAD:MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`（LF→CRLF）逐字节重建；哈希 5470cfcb… 已在 findings 与本请求中登记。"},
    {"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt",
     "reason": "生成清单全文（220 KB / 609 行）以本批新增行提取件 + manifest-diff-summary.json + 两份守卫 TRX 承载。"},
    {"path": "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md",
     "reason": "全文 819 KB 超出单文件上限；以 §24.126.5 更新句 + §24.127 全文摘录（本批全部改动）承载。"},
    {"path": base + "/mutations-final/**",
     "reason": "12 个突变目录的 36 份 TRX 与 36 份构建日志由 mutation-records-final.json 承载（三阶段退出码、目标 testId/名称、断言行、源码哈希与路径）；原件保留在磁盘。"},
]
io.open(os.path.join(c, "preflight-v1.json"), "w", encoding="utf-8", newline="\n").write(json.dumps(pre, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({k: pre[k] for k in ("total_bytes","estimated_input_tokens","estimated_fraction_of_effective_window","attachment_transport_status")}, ensure_ascii=False, indent=1))
print("files", len(dispatch))
