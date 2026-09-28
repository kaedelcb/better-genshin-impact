import pathlib
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
old='CASE_SELECTOR = {"M3-no-outside-write-detection": "(deleteInstead: True)"}'
new='CASE_SELECTOR = {"M3-no-outside-write-detection": "(deleteInstead: False)"}'
assert s.count(old)==1
p.write_text(s.replace(old,new,1),encoding="utf-8"); print("M3 targets the modification case (delete case now caught by the set-equality check / M32)")
