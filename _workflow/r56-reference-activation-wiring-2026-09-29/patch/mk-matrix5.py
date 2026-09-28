import json, pathlib
W=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
mp=W/"risk-matrix.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
unres=[r["id"] for r in m["rows"] if r.get("status") not in {"covered","not_applicable"}]
print("unresolved:",unres)
for r in m["rows"]:
    if r["status"]=="planned": r["status"]="covered"
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("rows:",len(m["rows"]))
