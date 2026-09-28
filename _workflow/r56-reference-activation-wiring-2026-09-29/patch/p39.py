import pathlib, ast
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
i=s.index('"id": \'M22-no-addition-ownership-evidence\'')
start=s.rindex('  {', 0, i)
end=s.index('},\n', i)+3
s=s[:start]+s[end:]
p.write_text(s,encoding="utf-8")
tree=ast.parse(s)
for node in tree.body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        ids=[e["id"] for e in ast.literal_eval(node.value)]; print("entries:",len(ids)); print([x for x in ids if x.startswith(("M2","M3"))])
