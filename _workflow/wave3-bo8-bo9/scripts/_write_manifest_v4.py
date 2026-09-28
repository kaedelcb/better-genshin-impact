import io, json, os, hashlib
root = r"E:\Program Files\better-genshin-impact-LCB"
base = r"_workflow/wave3-bo8-bo9"
m = json.load(io.open(os.path.join(root, base + "/manifest.json"), encoding="utf-8"))
def sha(p): return hashlib.sha256(open(os.path.join(root, p), "rb").read()).hexdigest()
cur = {s: sha(s) for s in m["sources"]}
print("sources", json.dumps(cur, indent=1))
subs = [("fixed-v5/targeted-fixed-v5.trx", "final-v9/targeted-final.trx"),
        ("final-v8/targeted-final.trx", "final-v9/targeted-final.trx"),
        ("final-v8/assistant-full-final.trx", "final-v9/assistant-full-final.trx"),
        ("final-v8/testid-comparison.json", "final-v9/testid-comparison.json"),
        ("final-v8/testproject-build.log", "final-v9/testproject-build.log"),
        ("final-v8/assistant-full-final.log", "final-v9/assistant-full-final.log"),
        ("claims-v3/", "claims-v4/")]
for e in m["evidence"]:
    for a, b in subs:
        e["path"] = e["path"].replace(a, b)
    if e["id"] in ("fixed-v6-targeted",):
        e["path"] = base + "/final-v9/targeted-final.trx"
        e["binding"] = "historical"
    e["source_sha256"] = cur if e.get("binding", "current") == "current" else e["source_sha256"]
m["tests"] = [
    {"id": "final-targeted", "path": base + "/final-v9/targeted-final.trx", "expect_success": True},
    {"id": "final-full", "path": base + "/final-v9/assistant-full-final.trx", "expect_success": True},
    {"id": "claims-regen", "path": base + "/claims-v4/claim-regen.trx", "expect_success": True},
    {"id": "claims-noenv", "path": base + "/claims-v4/claim-noenv.trx", "expect_success": True},
    {"id": "red-final", "path": base + "/red-final/bo8-bo9-red-final.trx", "expect_success": False},
]
m["comparison"] = {"baseline": base + "/baseline/assistant-full-baseline.trx", "final": base + "/final-v9/assistant-full-final.trx"}
m["evidence"].append({
    "id": "consult-outcome-v3", "path": base + "/consultation/review-outcome-v3.md",
    "purpose": "第 3 轮验证结论与逐项处置（IMPORTANT-2 原级闭合；原 IMPORTANT-1 未回退；无新增 MUST/IMPORTANT；建议级 BO-9-D1 登记）",
    "level": "consult", "conditions": "gpt-6-astra / medium，attempts=1，read-only；原文要点逐项抄录", "binding": "current", "source_sha256": cur})
m["evidence"].append({
    "id": "round3-review-packet", "path": base + "/review-v3/packet.md", "purpose": "第 3 轮送审材料（449,331 字节）",
    "level": "consult", "conditions": "workflow.py audit --stage review；快照 review-v3 经 verify 通过", "binding": "historical",
    "source_sha256": {"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "b0b3579bf289551d2f314b1bb9192284f75406d9fb1d8bfd9db2c66bcca6c1ae",
                      "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "7bdf48a66c5245ecff9cdc9aeb0b7b442fcb58845daa6a8d2bfd60e26401f3f2"}})
m["evidence"].append({
    "id": "consult-preflight-v3", "path": base + "/consultation/preflight-v3.json",
    "purpose": "第 3 轮渠道容量预检与材料清单（27 件、502,436 字节）", "level": "document", "conditions": "本地估算",
    "binding": "historical", "source_sha256": {"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs": "b0b3579bf289551d2f314b1bb9192284f75406d9fb1d8bfd9db2c66bcca6c1ae",
                      "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs": "7bdf48a66c5245ecff9cdc9aeb0b7b442fcb58845daa6a8d2bfd60e26401f3f2"}})
m["mutation_scope"] = m["mutation_scope"].replace("14 项独立反向突变全部在本批最终源码字节上执行", "14 项独立反向突变全部在本批最终源码字节（b0b3579b…，与第 3 轮送审字节相同）上执行")
# packet: recompute excerpt ranges
def idn(l):
    p = l.split("\u0001"); return "\u0001".join(p[:3]) if len(p) >= 3 else l
b = {idn(l) for l in io.open(os.path.join(root, base + "/claims-v4/manifest-before.txt"), encoding="utf-8-sig").read().splitlines() if l.strip()}
a = io.open(os.path.join(root, "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt"), encoding="utf-8-sig").read().splitlines()
added = [i for i, l in enumerate(a, 1) if l.strip() and idn(l) not in b]
lines = io.open(os.path.join(root, "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"), encoding="utf-8").read().splitlines()
r53_start = next(i for i, l in enumerate(lines, 1) if l.startswith("## §24.127"))
print("claim added", added, "r53", r53_start, len(lines))
packet = [e for e in m["packet"] if "ClaimSurfaceManifest.txt" not in e["path"] and "onedragon-r5-3" not in e["path"]]
packet.append({"path": "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt", "role": "source",
               "start_line": added[0], "end_line": added[0],
               "coverage_notes": "再生清单中本批最后一版新增的载荷声明（BO-9-D1 登记句）。清单 618 行，本批末次相对 +1／-0（claims-v4/manifest-diff-summary.json）；再生与清除变量复跑由 claims-regen／claims-noenv TRX 与哈希记录证明，全文哈希绑定在所有 current 证据的 source_sha256 中。"})
packet.append({"path": "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md", "role": "source",
               "start_line": r53_start, "end_line": len(lines),
               "coverage_notes": "本批新增的 §24.127 全文（含三轮会诊结论、逐项处置与 BO-9-D1 登记）。这是本批在 R5.3 中的全部改动（另有两句 §24.126.5 日期化更新，原文保留）；文档其余 5000 余行未改动。"})
m["packet"] = packet
m["evidence"] = [e for e in m["evidence"] if e["id"] != "fixed-v5-targeted" or True]
# ensure unique evidence ids
seen = set(); dedup = []
for e in m["evidence"]:
    if e["id"] in seen: continue
    seen.add(e["id"]); dedup.append(e)
m["evidence"] = dedup
io.open(os.path.join(root, base + "/manifest.json"), "w", encoding="utf-8").write(json.dumps(m, ensure_ascii=False, indent=2) + "\n")
print("manifest v4 written; evidence", len(m["evidence"]), "packet", len(m["packet"]))
