import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations.py")
s = p.read_text(encoding="utf-8")
s = s.replace('''ONLY = {"M1-no-readback-confirmation", "M5-no-commit-recheck"}
for mid, target, desc, old, new, test in MUTATIONS:
    if mid not in ONLY: continue''', '''for mid, target, desc, old, new, test in MUTATIONS:''')
p.write_text(s, encoding="utf-8")
print("ONLY filter removed -> full 8")
