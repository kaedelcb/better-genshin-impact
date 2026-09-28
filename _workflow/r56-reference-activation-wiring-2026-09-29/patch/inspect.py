import pathlib
s = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py").read_text(encoding="utf-8")
i = s.index("M28-added-target-overwrite-allowed")
j = s.index("CASE_SELECTOR")
seg = s[i:j]
print(seg[-400:])
print("----- length of seg tail -----", len(seg))
