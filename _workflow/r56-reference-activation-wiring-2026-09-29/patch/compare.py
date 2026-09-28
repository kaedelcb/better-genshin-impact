import xml.etree.ElementTree as ET, json, pathlib, collections
ns="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
def load(p):
    d={}
    for r in ET.parse(p).getroot().iter(ns+"UnitTestResult"):
        tid=r.get("testId"); d.setdefault(tid, []).append((r.get("testName"), r.get("outcome")))
    return d
base=load(r"_workflow/r56-reference-activation-wiring-2026-09-29/baseline/assistant-full-baseline.trx")
final=load(r"_workflow/r56-reference-activation-wiring-2026-09-29/final/assistant-full-final-frozen.trx")
added=[k for k in final if k not in base]; removed=[k for k in base if k not in final]
changed=[k for k in base if k in final and [o for _,o in base[k]]!=[o for _,o in final[k]]]
res={"baseline_total":sum(len(v) for v in base.values()),"final_total":sum(len(v) for v in final.values()),
     "baseline_unique_testids":len(base),"final_unique_testids":len(final),
     "added":len(added),"removed":len(removed),"changed":len(changed),"unchanged":len(base)-len(removed)-len(changed),
     "added_names":sorted(final[k][0][0] for k in added),
     "removed_names":sorted(base[k][0][0] for k in removed),
     "changed_names":sorted(base[k][0][0] for k in changed)}
pathlib.Path(r"_workflow/r56-reference-activation-wiring-2026-09-29/final/testid-comparison.json").write_text(json.dumps(res,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(json.dumps({k:v for k,v in res.items() if not k.endswith('names')},ensure_ascii=False,indent=1))
print("added:"); [print("  +",n) for n in res["added_names"][:40]]
print("removed:", res["removed_names"][:10], "changed:", res["changed_names"][:10])
