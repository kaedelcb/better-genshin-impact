import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
old = '''        actual = r.get("testName") or ""
        if (actual == name or actual.endswith(name)) and (not case or actual.endswith(case)):'''
new = '''        actual = r.get("testName") or ""
        base = actual.split("(")[0]          # theory 用例名带参数：先去掉参数再比方法名
        if (base == name or base.endswith(name)) and (not case or actual.endswith(case)):'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
p.write_text(s, encoding="utf-8")
print("theory matching fixed")
