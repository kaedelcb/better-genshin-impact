import json, pathlib
W=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
pe=m["packet"]
for p in pe:
    if p["path"].endswith("analysis") or p.get("role")=="contract" and p["path"].endswith(".md") and "onedragon-r5-3" in p["path"]:
        pass
# 更新节选范围与新增 r3 差异、round2 报告
new=[]
for p in pe:
    if p["path"].endswith("review-round1-report.md"):
        new.append({"path":"_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round1-report.md","role":"contract",
          "coverage_notes":"第 1 轮会诊报告原文（10 项发现）。"})
        new.append({"path":"_workflow/r56-reference-activation-wiring-2026-09-29/consultation/review-round2-report.md","role":"contract",
          "coverage_notes":"第 2 轮（验证轮）会诊报告原文：第 1 轮逐项闭合判定 + 新报 MUST 4 / IMPORTANT 4；本批已按原级修复并逐条补夹具与突变。"})
        continue
    if p["path"].endswith("claims-diff-r2.txt"):
        new.append(p)
        new.append({"path":"_workflow/r56-reference-activation-wiring-2026-09-29/claims/claims-diff-r3.txt","role":"contract",
          "coverage_notes":"第 3 轮声明面再生差异（+3/-0）。"})
        continue
    if p["path"].endswith("onedragon-r5-3-external-start-lifecycle-2026-09-21.md"):
        p["start_line"]=5252; p["end_line"]=5342
        p["coverage_notes"]="**节选**：本批新增的 §24.129 全节（含 §24.129.7／§24.129.8 两轮会诊逐项处置）（L5252-L5342）。"
    if p["path"].endswith("槲寄生调度器总计划.md"):
        p["start_line"]=411; p["end_line"]=421
        p["coverage_notes"]="**节选**：本批 2026-09-29 进度条目（L411-L421）。"
    if p["path"].endswith("R56ReferenceActivationWiringTests.cs"):
        p["end_line"]=40
        p["coverage_notes"]="**节选**：新夹具头部（全文 1300+ 行、49 条用例，全部为本批新增）；断言细节见 targeted TRX 与 mutations/summary.md。"
    if p["path"].endswith("mutations/summary.md"):
        p["coverage_notes"]="33 项反向突变的补丁原文与三段判定（含 exit code、命中标记、恢复哈希）。"
    new.append(p)
m["packet"]=new
(W/"manifest.json").write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("packet entries:",len(new))
