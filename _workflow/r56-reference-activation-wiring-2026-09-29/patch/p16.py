import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
old = '''def trx_row(trxpath, name):
    for r in ET.parse(str(trxpath)).getroot().iter(NS+"UnitTestResult"):
        if r.get("testName") == name:
            msg = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"Message", default="")
            st = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"StackTrace", default="")
            return r.get("testId"), r.get("outcome"), msg, st
    return None, None, "", ""'''
new = '''def trx_row(trxpath, name):
    """Match by suffix: the --filter value is a short name, the TRX carries the fully-qualified name."""
    for r in ET.parse(str(trxpath)).getroot().iter(NS+"UnitTestResult"):
        actual = r.get("testName") or ""
        if actual == name or actual.endswith(name):
            msg = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"Message", default="")
            st = r.findtext(NS+"Output/"+NS+"ErrorInfo/"+NS+"StackTrace", default="")
            return r.get("testId"), r.get("outcome"), msg, st, actual
    return None, None, "", "", None'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
s = s.replace('''    tid, base_outcome, _, _ = trx_row(base_trx, target_name)''',
              '''    tid, base_outcome, _, _, full_name = trx_row(base_trx, target_name)''')
s = s.replace('''    _, mutant_outcome, mutant_msg, mutant_stack = trx_row(mutant_trx, target_name)''',
              '''    _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name)''')
s = s.replace('''    _, restored_outcome, _, _ = trx_row(restored_trx, target_name)''',
              '''    _, restored_outcome, _, _, _ = trx_row(restored_trx, target_name)''')
s = s.replace('''    assertion_marker = target_name.split(".")[-1].split("(")[0]''',
              '''    assertion_marker = full_name.split(".")[-1].split("(")[0]''')
s = s.replace('''        "target_test_id": tid, "target_name": target_name,''',
              '''        "target_test_id": tid, "target_name": full_name,''')
p.write_text(s, encoding="utf-8")
print("v2 runner patched")
