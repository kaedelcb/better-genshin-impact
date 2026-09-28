import json, pathlib
W=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
# 用新的 41 条夹具行号节选替代整份夹具文件（全文 45KB），并压缩旧夹具节选
for p in m["packet"]:
    if p["path"].endswith("R56ReferenceActivationWiringTests.cs"):
        p["start_line"]=1; p["end_line"]=60
        p["coverage_notes"]=("**节选**：新夹具全文 1200+ 行（41 条），本批全部为本批新增；此处给出头部注释（含能力边界），"
                             "其余断言与矩阵行号绑定见 targeted TRX 与 mutations/summary.md。完整文件可用 git diff/工作区读取。")
    if p["path"].endswith("R56MigrationSwitchTransactionTests.cs"):
        p["end_line"]=24
    if p["path"].endswith("ClaimSurfaceManifest.txt"):
        p["end_line"]=4
    if p["path"].endswith("MigrationRehearsal.cs"):
        p["end_line"]=30
(W/"manifest.json").write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("packet trimmed")
