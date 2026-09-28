import json, pathlib
W=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
mp=W/"manifest.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
for p in m["packet"]:
    if p["path"].endswith("mutations/summary.md"):
        p["start_line"]=1; p["end_line"]=60
        p["coverage_notes"]=("**节选**：33 项突变台账的头部（说明 + 前若干项，含补丁原文与三段判定）；"
                             "逐项完整记录见同批 `mutations/<id>/record.json` 与各目录日志/TRX。")
    if p["path"].endswith("review-round2-report.md"):
        p["start_line"]=1; p["end_line"]=26
        p["coverage_notes"]="**节选**：第 2 轮报告前 5 项（MUST 1–5）；全文见同目录文件（本批已逐条按原级修复）。"
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("trimmed")
