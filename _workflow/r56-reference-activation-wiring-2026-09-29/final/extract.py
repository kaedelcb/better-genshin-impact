import xml.etree.ElementTree as ET, pathlib, sys
t = ET.parse(r"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final.trx")
ns = {"t":"http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
for r in t.getroot().iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
    if r.get("outcome") != "Passed":
        name = r.get("testName")
        msg = r.find(".//t:Message", ns)
        print("=== ", name, " [", r.get("outcome"), "]")
        if msg is not None and msg.text:
            print("   ", msg.text.strip().replace("\n", "\n    ")[:600])
