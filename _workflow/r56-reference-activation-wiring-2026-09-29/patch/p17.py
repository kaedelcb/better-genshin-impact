import pathlib, re
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")
# add explicit filter/case selectors per mutation
s = s.replace('''   "R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks(deleteInstead: True)"),''',
              '''   "R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks"),''')
old = '''(OUT/"records.json")'''
# 1) introduce CASE selector for theory case
s = s.replace('''records = []
for mid, desc, old, new, target_name in MUTATIONS:''',
'''CASE_SELECTOR = {"M3-no-outside-write-detection": "(deleteInstead: True)"}

records = []
for mid, desc, old, new, target_name in MUTATIONS:
    case = CASE_SELECTOR.get(mid, "")''')
# 2) trx matching uses filter name + optional case selector
s = s.replace('''def trx_row(trxpath, name):
    """Match by suffix: the --filter value is a short name, the TRX carries the fully-qualified name."""
    for r in ET.parse(str(trxpath)).getroot().iter(NS+"UnitTestResult"):
        actual = r.get("testName") or ""
        if actual == name or actual.endswith(name):''',
'''def trx_row(trxpath, name, case=""):
    """Match by suffix: the --filter value is a short name, the TRX carries the fully-qualified name."""
    for r in ET.parse(str(trxpath)).getroot().iter(NS+"UnitTestResult"):
        actual = r.get("testName") or ""
        if (actual == name or actual.endswith(name)) and (not case or actual.endswith(case)):''')
s = s.replace('''    tid, base_outcome, _, _, full_name = trx_row(base_trx, target_name)''',
              '''    tid, base_outcome, _, _, full_name = trx_row(base_trx, target_name, case)''')
s = s.replace('''    _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name)''',
              '''    _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name, case)''')
s = s.replace('''    _, restored_outcome, _, _, _ = trx_row(restored_trx, target_name)''',
              '''    _, restored_outcome, _, _, _ = trx_row(restored_trx, target_name, case)''')
p.write_text(s, encoding="utf-8")
print("case selector added")
