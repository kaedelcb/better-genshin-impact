import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations.py")
s = p.read_text(encoding="utf-8")
assert s.count('"-p=DeployToBgiTools=false"') == 2
s = s.replace('"-p=DeployToBgiTools=false"', '"-p:DeployToBgiTools=false"')
old = '''def trx_outcome(path, test_name):
    import xml.etree.ElementTree as ET
    ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    if not path.exists(): return None, ""
    for r in ET.parse(str(path)).getroot().iter(ns + "UnitTestResult"):
        if r.get("testName","").endswith(test_name):
            msg = r.find(".//" + ns + "Message")
            return r.get("outcome"), (msg.text or "") if msg is not None else ""
    return None, ""'''
new = '''def trx_outcomes(path, test_name):
    """Return {testName: outcome} for every result whose name contains the fixture test name (theory cases included)."""
    import xml.etree.ElementTree as ET
    ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    out, msgs = {}, {}
    if not path.exists(): return out, msgs
    for r in ET.parse(str(path)).getroot().iter(ns + "UnitTestResult"):
        name = r.get("testName", "")
        if test_name in name:
            out[name] = r.get("outcome")
            msg = r.find(".//" + ns + "Message")
            msgs[name] = (msg.text or "") if msg is not None else ""
    return out, msgs

def summarize(outcomes):
    if not outcomes: return "NOT_FOUND"
    if all(v == "Passed" for v in outcomes.values()): return "Passed"
    if any(v == "Failed" for v in outcomes.values()): return "Failed"
    return "|".join(sorted(set(outcomes.values())))'''
assert s.count(old) == 1
s = s.replace(old, new, 1)
s = s.replace('''    base_outcome, _ = trx_outcome(trx, test)''', '''    base_outcome, _ = trx_outcomes(trx, test)
    base_outcome = summarize(base_outcome)''')
s = s.replace('''        mutant_outcome, mutant_msg = trx_outcome(md/"mutant.trx", test)''',
              '''        raw, msgs = trx_outcomes(md/"mutant.trx", test)
        mutant_outcome = summarize(raw)
        mutant_msg = " | ".join(msgs.values())''')
s = s.replace('''        restored_outcome = None
    if rc == 0:''', '''        restored_outcome = None
    if rc == 0:''')
s = s.replace('''        restored_outcome, _ = trx_outcome(md/"restored.trx", test)''',
              '''        raw, _ = trx_outcomes(md/"restored.trx", test)
        restored_outcome = summarize(raw)''')
s = s.replace('''    restored_outcome = None
    if rc == 0:''', '''    restored_outcome = "NOT_RUN"
    if rc == 0:''')
p.write_text(s, encoding="utf-8")
print("runner patched")
