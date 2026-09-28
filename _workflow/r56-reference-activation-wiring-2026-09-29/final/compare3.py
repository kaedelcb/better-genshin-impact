import xml.etree.ElementTree as ET, json, pathlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
NS="{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
def rows(p): return [(r.get("testId"), r.get("testName"), r.get("outcome")) for r in ET.parse(str(p)).getroot().iter(NS+"UnitTestResult")]
base=rows(W/"baseline/assistant-full-baseline.trx"); final=rows(W/"final/assistant-full-final-frozen.trx")
b={t:(n,o) for t,n,o in base}; f={t:(n,o) for t,n,o in final}
res={"baseline_total":len(base),"final_total":len(final),"added":len(set(f)-set(b)),"removed":len(set(b)-set(f)),
     "changed":len([k for k in set(b)&set(f) if b[k]!=f[k]]),"unchanged":len([k for k in set(b)&set(f) if b[k]==f[k]]),
     "added_names":sorted(f[k][0] for k in set(f)-set(b)),"removed_names":sorted(b[k][0] for k in set(b)-set(f)),
     "changed_names":sorted(b[k][0] for k in set(b)&set(f) if b[k]!=f[k])}
(W/"final/testid-comparison.json").write_text(json.dumps(res,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print({k:v for k,v in res.items() if not k.endswith("names")}); print("removed:",res["removed_names"],"changed:",res["changed_names"])
tgt=rows(W/"final/targeted-final-frozen.trx"); print("targeted total:",len(tgt), "failed:", len([1 for _,_,o in tgt if o!="Passed"]))
