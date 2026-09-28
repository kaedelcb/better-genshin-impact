import json, pathlib
root = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W = root/"_workflow/r56-reference-activation-wiring-2026-09-29"
def find_lines(rel, start_marker, end_marker=None):
    lines = (root/rel).read_text(encoding="utf-8").splitlines(keepends=True)
    s = next(i+1 for i,l in enumerate(lines) if l.startswith(start_marker))
    if end_marker is None:
        e = len(lines)
    else:
        e = next(i for i,l in enumerate(lines) if i+1 > s and l.startswith(end_marker))
    return s, e, len(lines)

r53 = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
plan = "槲寄生调度器总计划.md"
idx = "Docs/design/mistletoe-parallel-deliveries.md"
a = find_lines(r53, "## §24.129 R5.6 A 项主线施工子批")
b = find_lines(plan, "## 2026-09-29：R5.6 A 项主线施工", "## 2026-09-28：R5.6 主线迁移集成批")
c = find_lines(idx, "### R5.6 A 项进展（主线施工，2026-09-29）", "## 无需 owner 提醒的执行闭环")
mp = W/"manifest.json"; m = json.loads(mp.read_text(encoding="utf-8-sig"))
m["packet"] = [
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/context.md","role":"objective"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/findings.md","role":"findings"},
 {"path":"_workflow/r56-reference-activation-wiring-2026-09-29/budget.md","role":"budget"},
 {"path":r53,"role":"contract","start_line":a[0],"end_line":a[1],
  "coverage_notes":(f"仅含本批新增的 §24.129（L{a[0]}-L{a[1]}）；全文共 {a[2]} 行，其余为历史批次记录，"
                    "未随本批改动（可用 git diff 核对本批只追加本节）。")},
 {"path":plan,"role":"contract","start_line":b[0],"end_line":b[1],
  "coverage_notes":(f"仅含本批新增的 2026-09-29 进度条目（L{b[0]}-L{b[1]}）；全文共 {b[2]} 行，其余未改动。")},
 {"path":idx,"role":"contract","start_line":c[0],"end_line":c[1],
  "coverage_notes":(f"仅含本批新增的「R5.6 A 项进展」段（L{c[0]}-L{c[1]}）；全文共 {c[2]} 行，其余未改动。")},
]
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("packet entries:", len(m["packet"]), "| r53", a, "| plan", b, "| idx", c)
