import pathlib, ast, re
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
i=s.index('"id": \'M30-no-addition-ownership-enforcement\'')
j=s.index('"src"', i)
block=s[i:j]
block2=block.replace('R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten',
                     'R56ReferenceActivationWiringTests_Part3.ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks')
assert block2!=block
s=s[:i]+block2+s[j:]
p.write_text(s,encoding="utf-8")
ast.parse(s)
tree=ast.parse(s)
for node in tree.body:
    if isinstance(node,ast.Assign) and getattr(node.targets[0],"id","")=="MUTATIONS":
        for e in ast.literal_eval(node.value):
            if e["id"].startswith("M30"): print("M30 ->", e["test"])
