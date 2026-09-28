import json, pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/risk-matrix.json")
d = json.loads(p.read_text(encoding="utf-8-sig"))
assert not any(r["id"] == "REF-F9" for r in d["rows"])
d["rows"].append({
    "id": "REF-F9", "dimension": "fault",
    "scenario": "副作用自报成功但改动了**声明写集之外**的基线文件（含删除）",
    "expected": "unexpected_write_outside_writeset 置 Blocked，阶段不推进",
    "critical": True, "status": "planned"})
p.write_text(json.dumps(d, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("rows:", len(d["rows"]))
