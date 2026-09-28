import json, pathlib, ast, xml.etree.ElementTree as ET, hashlib, subprocess
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
runner=(W/"patch/run-mutations-v2.py").read_text(encoding="utf-8")
entries={}
for node in ast.parse(runner).body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        for e in ast.literal_eval(node.value): entries[e["id"]]=e
recs=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
for r in recs:
    e=entries[r["id"]]; r["old_snippet"]=e["old"]; r["new_snippet"]=e["new"]
    r["mutant_patch"]="替换前：\n%s\n替换后：\n%s" % (e["old"], e["new"])
(W/"mutations/records.json").write_text(json.dumps(recs,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("records:",len(recs))
