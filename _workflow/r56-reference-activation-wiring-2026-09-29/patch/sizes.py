import json, pathlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
m=json.loads((W/"manifest.json").read_text(encoding="utf-8-sig"))
tot=0
for e in m["packet"]:
    raw=(root/e["path"]).read_bytes()
    lines=raw.decode("utf-8-sig").splitlines(keepends=True)
    s=e.get("start_line",1); en=e.get("end_line",len(lines))
    sz=len("".join(lines[s-1:en]).encode())
    tot+=sz
    print(f"{sz:8d}  {e['role']:10s} L{s}-L{en}  {e['path']}")
print("total", tot)
