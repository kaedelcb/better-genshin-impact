import json, pathlib, hashlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
mp=W/"manifest.json"; m=json.loads(mp.read_text(encoding="utf-8-sig"))
m["mutations"]=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("manifest mutations:",len(m["mutations"]))
